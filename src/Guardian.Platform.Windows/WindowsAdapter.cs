using System.Runtime.Versioning;
using System.Security.Principal;
using Guardian.Core.Cleanup;
using Guardian.Core.Inventory;
using Guardian.Core.Performance;
using Guardian.Core.Platform;
using Guardian.Core.Reporting;
using Guardian.Core.Scheduling;
using Microsoft.Win32;

namespace Guardian.Platform.Windows;

/// <summary>
/// Windows adapter. Reads Microsoft Defender, Windows Security Center, Windows Firewall, update and restart state
/// through documented read-only interfaces. Guardian never disables Defender, changes firewall rules, or edits the registry.
/// NOT YET RUN ON WINDOWS: see docs/PLATFORM_STATUS.md.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAdapter : IPlatformAdapter
{
    private readonly ReadOnlyCommandRunner _run;
    private readonly ReadOnlyCommand? _defender, _avProducts, _firewall, _hotfix, _bitlocker, _dismCheck, _sfcVerify, _trustedInstaller;

    public WindowsAdapter(ICommandRunner? runner = null)
    {
        var allow = new List<ReadOnlyCommand>();
        var tools = new List<string>();
        var ps = ToolLocator.FindWindowsSystem(@"WindowsPowerShell\v1.0\powershell.exe");
        ReadOnlyCommand? Ps(string script)
        {
            if (ps is null) return null;
            var c = new ReadOnlyCommand(ps, ["-NoProfile", "-NonInteractive", "-Command", script]);
            allow.Add(c);
            return c;
        }
        ReadOnlyCommand? Exe(string relative, params string[] args)
        {
            var p = ToolLocator.FindWindowsSystem(relative);
            if (p is null) return null;
            tools.Add(Path.GetFileName(p));
            var c = new ReadOnlyCommand(p, args);
            allow.Add(c);
            return c;
        }
        if (ps is not null) tools.Add("powershell.exe");
        _defender = Ps("Get-MpComputerStatus | Select-Object AMServiceEnabled,AntivirusEnabled,RealTimeProtectionEnabled,AntivirusSignatureLastUpdated,QuickScanEndTime | ConvertTo-Json -Compress");
        _avProducts = Ps("Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | Select-Object displayName,productState | ConvertTo-Json -Compress");
        _firewall = Ps("Get-NetFirewallProfile | Select-Object Name,@{n='Enabled';e={[bool]$_.Enabled}} | ConvertTo-Json -Compress");
        _hotfix = Ps("Get-CimInstance -ClassName Win32_QuickFixEngineering | Where-Object InstalledOn | Sort-Object InstalledOn -Descending | Select-Object -First 1 -ExpandProperty InstalledOn | ForEach-Object { $_.ToString('o') }");
        _bitlocker = Ps("Get-BitLockerVolume -MountPoint $env:SystemDrive | Select-Object -ExpandProperty ProtectionStatus");
        _dismCheck = Exe("dism.exe", "/Online", "/Cleanup-Image", "/CheckHealth");
        _sfcVerify = Exe("sfc.exe", "/verifyonly");
        _trustedInstaller = Exe("sc.exe", "query", "TrustedInstaller");
        _run = new ReadOnlyCommandRunner(runner ?? new ProcessCommandRunner(), allow);

        var v = Environment.OSVersion.Version;
        var (targeted, note) = SupportMatrix.Windows(v);
        var edition = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", null) as string;
        var display = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
        Info = new PlatformInfo
        {
            Family = OsFamily.Windows,
            // ProductName in the registry still says "Windows 10" on Windows 11, so the build number decides.
            OsName = $"{(v.Build >= 22000 ? "Windows 11" : "Windows 10")} {edition} {display}".Trim(),
            OsVersion = v.ToString(),
            Architecture = PlatformInfo.DetectArchitecture(),
            IsElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            IsSupportedVersion = targeted,
            SupportNote = note + " " + SupportMatrix.ReleaseStatus,
            DetectedTools = tools.Order().ToList(),
        };

        SecurityChecks =
        [
            new DelegateCheck("win.defender", "Microsoft Defender Antivirus", DefenderCheck),
            new DelegateCheck("win.av-products", "Antivirus registered with Windows Security", AvProductsCheck),
            new DelegateCheck("win.firewall", "Windows Firewall", FirewallCheck),
            new DelegateCheck("win.updates", "Recent Windows updates", UpdatesCheck),
            new DelegateCheck("win.reboot", "Restart pending", RebootCheck),
            new DelegateCheck("win.uac", "User Account Control", UacCheck),
            new DelegateCheck("win.bitlocker", "BitLocker on the system drive", BitLockerCheck),
            new DelegateCheck("win.dism", "Component store health (DISM /CheckHealth)", DismCheck, onDemandOnly: true),
            new DelegateCheck("win.sfc", "System file integrity (SFC /verifyonly)", SfcCheck, onDemandOnly: true),
        ];
        Performance = new WindowsPerformanceProbe();
        Startup = new WindowsStartupInventory();
        Applications = new WindowsApplicationInventory();
        Cleanup = new WindowsCleanupPlatform();
        Scheduler = new TaskSchedulerXml();
        Preflight = new WindowsRepairPreflight(_run, _trustedInstaller);
    }

    public PlatformInfo Info { get; }
    public IReadOnlyList<ISecurityCheck> SecurityChecks { get; }
    public IPerformanceProbe Performance { get; }
    public IStartupInventory Startup { get; }
    public IApplicationInventory Applications { get; }
    public ICleanupPlatform Cleanup { get; }
    public INativeScheduler Scheduler { get; }
    public WindowsRepairPreflight Preflight { get; }

    private async Task<CheckReport> DefenderCheck(CancellationToken ct)
    {
        const string id = "win.defender", title = "Microsoft Defender Antivirus";
        if (_defender is null) return CheckReport.NotAvailable(id, title, "Windows PowerShell was not found.");
        var r = await _run.RunAsync(_defender, ct: ct);
        if (!r.Succeeded || string.IsNullOrWhiteSpace(r.StdOut))
            return CheckReport.NotAvailable(id, title, "Defender status is not available. This is normal when another antivirus product is the active one; see the next check.",
                new Evidence(_defender.Display, Trim(r.StdErr)));
        var s = WindowsParsers.Defender(r.StdOut);
        if (s is null) return CheckReport.Failed(id, title, "Could not read Defender status.");
        var ev = new List<Evidence>
        {
            new("Get-MpComputerStatus", $"service {s.ServiceEnabled}; antivirus {s.AntivirusEnabled}; real-time {s.RealTimeEnabled}; signatures {s.SignaturesUpdated:u}; last quick scan {s.LastQuickScan:u}"),
        };
        if (s.AntivirusEnabled == false || s.RealTimeEnabled == false)
            return CheckReport.Completed(id, title, Finding.Attention, "Defender real-time protection is off. If another antivirus is active this is expected.", ev,
                "Check Windows Security > Virus & threat protection. Guardian never turns protection on or off.");
        var age = s.SignaturesUpdated is { } d ? DateTimeOffset.UtcNow - d : (TimeSpan?)null;
        if (age is { TotalDays: > 7 })
            return CheckReport.Completed(id, title, Finding.Attention, $"Defender signatures are {age.Value.TotalDays:0} days old.", ev, "Open Windows Security and check for protection updates.");
        return CheckReport.Completed(id, title, Finding.NoIssueFound, "Defender real-time protection is on and signatures are recent.", ev);
    }

    private async Task<CheckReport> AvProductsCheck(CancellationToken ct)
    {
        const string id = "win.av-products", title = "Antivirus registered with Windows Security";
        if (_avProducts is null) return CheckReport.NotAvailable(id, title, "Windows PowerShell was not found.");
        var r = await _run.RunAsync(_avProducts, ct: ct);
        if (!r.Succeeded) return CheckReport.NotAvailable(id, title, "Windows Security Center is not available (it does not exist on Windows Server).", new Evidence(_avProducts.Display, Trim(r.StdErr)));
        var products = WindowsParsers.AntivirusProducts(r.StdOut);
        var ev = products.Select(p => new Evidence("SecurityCenter2", $"{p.Name}: {(p.Enabled ? "on" : "off")}, {(p.UpToDate ? "up to date" : "out of date")} (decoded best-effort)")).ToList();
        if (products.Count == 0) return CheckReport.Completed(id, title, Finding.Problem, "No antivirus product is registered with Windows Security.", ev, "Open Windows Security to review protection.");
        return products.Any(p => p.Enabled)
            ? CheckReport.Completed(id, title, Finding.NoIssueFound, $"Active: {string.Join(", ", products.Where(p => p.Enabled).Select(p => p.Name))}.", ev)
            : CheckReport.Completed(id, title, Finding.Problem, "No registered antivirus product reports itself as on.", ev, "Open Windows Security to review protection.");
    }

    private async Task<CheckReport> FirewallCheck(CancellationToken ct)
    {
        const string id = "win.firewall", title = "Windows Firewall";
        if (_firewall is null) return CheckReport.NotAvailable(id, title, "Windows PowerShell was not found.");
        var r = await _run.RunAsync(_firewall, ct: ct);
        if (!r.Succeeded) return CheckReport.Failed(id, title, "Could not read firewall profiles.", new Evidence(_firewall.Display, Trim(r.StdErr)));
        var profiles = WindowsParsers.FirewallProfiles(r.StdOut);
        var ev = profiles.Select(p => new Evidence("Get-NetFirewallProfile", $"{p.Profile}: {(p.Enabled ? "on" : "off")}")).ToList();
        var off = profiles.Where(p => !p.Enabled).Select(p => p.Profile).ToList();
        return off.Count == 0
            ? CheckReport.Completed(id, title, Finding.NoIssueFound, "Windows Firewall is on for all profiles.", ev)
            : CheckReport.Completed(id, title, Finding.Attention, $"Windows Firewall is off for: {string.Join(", ", off)}. A third-party firewall may be handling this.", ev,
                "Review in Windows Security > Firewall & network protection. Guardian does not change firewall settings.");
    }

    private async Task<CheckReport> UpdatesCheck(CancellationToken ct)
    {
        const string id = "win.updates", title = "Recent Windows updates";
        if (_hotfix is null) return CheckReport.NotAvailable(id, title, "Windows PowerShell was not found.");
        var r = await _run.RunAsync(_hotfix, ct: ct);
        if (!r.Succeeded || !DateTimeOffset.TryParse(r.StdOut.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var last))
            return CheckReport.Failed(id, title, "Could not read the update history.", new Evidence(_hotfix.Display, Trim(r.StdErr)));
        var days = (DateTimeOffset.Now - last).TotalDays;
        var ev = new List<Evidence> { new("Win32_QuickFixEngineering", $"most recent update installed {last:yyyy-MM-dd}") };
        return days > 45
            ? CheckReport.Completed(id, title, Finding.Attention, $"The most recent update was installed {days:0} days ago.", ev, "Open Settings > Windows Update. Guardian does not install Windows updates.")
            : CheckReport.Completed(id, title, Finding.NoIssueFound, $"An update was installed {days:0} days ago.", ev, "This shows install history only, not whether new updates are waiting.");
    }

    private Task<CheckReport> RebootCheck(CancellationToken ct)
    {
        const string id = "win.reboot", title = "Restart pending";
        var ev = new List<Evidence>();
        bool KeyExists(string path)
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            ev.Add(new($@"HKLM\{path}", k is null ? "absent" : "present"));
            return k is not null;
        }
        var cbs = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        var wu = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        var renames = sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        ev.Add(new(@"HKLM\...\Session Manager\PendingFileRenameOperations", renames ? "present" : "absent"));
        var pending = cbs || wu || renames;
        return Task.FromResult(CheckReport.Completed(id, title, pending ? Finding.Attention : Finding.NoIssueFound,
            pending ? "Windows has a restart pending to finish updates or installs." : "No restart is pending.", ev,
            pending ? "Restart when convenient. Guardian never restarts the computer, and repairs are blocked until you do." : null));
    }

    private Task<CheckReport> UacCheck(CancellationToken ct)
    {
        const string id = "win.uac", title = "User Account Control";
        var v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", null);
        var ev = new List<Evidence> { new(@"HKLM\...\Policies\System\EnableLUA", v?.ToString() ?? "absent") };
        return Task.FromResult(v is int i && i == 0
            ? CheckReport.Completed(id, title, Finding.Problem, "User Account Control is turned off.", ev, "Turn it back on in Control Panel > User Accounts. Guardian does not change it.")
            : CheckReport.Completed(id, title, Finding.NoIssueFound, "User Account Control is on.", ev));
    }

    private async Task<CheckReport> BitLockerCheck(CancellationToken ct)
    {
        const string id = "win.bitlocker", title = "BitLocker on the system drive";
        if (!Info.IsElevated) return CheckReport.NeedsPermission(id, title, "Reading BitLocker status needs administrator rights.");
        if (_bitlocker is null) return CheckReport.NotAvailable(id, title, "Windows PowerShell was not found.");
        var r = await _run.RunAsync(_bitlocker, ct: ct);
        var s = r.StdOut.Trim();
        if (!r.Succeeded) return CheckReport.NotAvailable(id, title, "BitLocker is not available on this edition of Windows (Home uses Device Encryption instead).", new Evidence(_bitlocker.Display, Trim(r.StdErr)));
        return s is "On" or "1"
            ? CheckReport.Completed(id, title, Finding.NoIssueFound, "BitLocker protection is on for the system drive.", [new(_bitlocker.Display, s)], permissionGranted: true)
            : CheckReport.Completed(id, title, Finding.Attention, "BitLocker protection is off for the system drive.", [new(_bitlocker.Display, s)], "Guardian does not change disk encryption.", true);
    }

    private async Task<CheckReport> DismCheck(CancellationToken ct)
    {
        const string id = "win.dism", title = "Component store health (DISM /CheckHealth)";
        if (!Info.IsElevated) return CheckReport.NeedsPermission(id, title, "DISM needs administrator rights. Start Guardian as administrator to run this check.");
        if (_dismCheck is null) return CheckReport.NotAvailable(id, title, "dism.exe was not found.");
        var r = await _run.RunAsync(_dismCheck, TimeSpan.FromMinutes(5), ct);
        var health = WindowsParsers.DismHealth(r.StdOut);
        var ev = new List<Evidence> { new(_dismCheck.Display, health ?? Trim(r.StdOut)) };
        return health switch
        {
            "healthy" => CheckReport.Completed(id, title, Finding.NoIssueFound, "No component store corruption was detected.", ev, permissionGranted: true),
            "repairable" => CheckReport.Completed(id, title, Finding.Problem, "The component store reports repairable corruption.", ev,
                "A DISM /RestoreHealth repair can be proposed in Controlled Repair mode. It needs your explicit approval.", true),
            "not-repairable" => CheckReport.Completed(id, title, Finding.Problem, "DISM reports corruption it cannot repair.", ev, "Back up your files. Microsoft's guidance is an in-place repair install.", true),
            _ => CheckReport.Failed(id, title, "DISM output was not recognised.", ev.ToArray()),
        };
    }

    private async Task<CheckReport> SfcCheck(CancellationToken ct)
    {
        const string id = "win.sfc", title = "System file integrity (SFC /verifyonly)";
        if (!Info.IsElevated) return CheckReport.NeedsPermission(id, title, "SFC needs administrator rights. Start Guardian as administrator to run this check.");
        if (_sfcVerify is null) return CheckReport.NotAvailable(id, title, "sfc.exe was not found.");
        var r = await _run.RunAsync(_sfcVerify, TimeSpan.FromMinutes(30), ct);
        var result = WindowsParsers.SfcVerify(r.StdOut);
        var ev = new List<Evidence> { new(_sfcVerify.Display, result ?? "unrecognised output") };
        return result switch
        {
            "clean" => CheckReport.Completed(id, title, Finding.NoIssueFound, "SFC did not find integrity violations.", ev, permissionGranted: true),
            "violations" => CheckReport.Completed(id, title, Finding.Problem, "SFC found integrity violations (verify only; nothing was repaired).", ev,
                "An SFC /scannow repair can be proposed in Controlled Repair mode. It needs your explicit approval.", true),
            "needs-admin" => CheckReport.NeedsPermission(id, title, "SFC needs administrator rights.", ev.ToArray()),
            _ => CheckReport.Failed(id, title, "SFC output was not recognised.", ev.ToArray()),
        };
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s.Trim();
}
