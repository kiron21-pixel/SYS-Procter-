using Guardian.Core.Cleanup;
using Guardian.Core.Inventory;
using Guardian.Core.Performance;
using Guardian.Core.Platform;
using Guardian.Core.Reporting;
using Guardian.Core.Scheduling;

namespace Guardian.Platform.MacOS;

/// <summary>
/// macOS adapter. Uses only Apple's built-in command-line tools in read-only form. Guardian never tries to change
/// Gatekeeper, SIP, XProtect, the firewall or FileVault, and does not attempt anything that would need SIP to be off.
/// NOT YET RUN ON A MAC: see docs/PLATFORM_STATUS.md.
/// </summary>
public sealed class MacAdapter : IPlatformAdapter
{
    private const string XProtectPlist = "/Library/Apple/System/Library/CoreServices/XProtect.bundle/Contents/Info.plist";
    private readonly ReadOnlyCommandRunner _run;
    private readonly ReadOnlyCommand? _spctl, _csrutil, _firewall, _fdesetup, _xprotect, _softwareupdate, _swVersName, _swVers;

    public MacAdapter(ICommandRunner? runner = null)
    {
        var allow = new List<ReadOnlyCommand>();
        var tools = new List<string>();
        ReadOnlyCommand? Add(string name, string? path, params string[] args)
        {
            if (path is null || !File.Exists(path)) return null;
            tools.Add(name);
            var c = new ReadOnlyCommand(path, args);
            allow.Add(c);
            return c;
        }
        _spctl = Add("spctl", "/usr/sbin/spctl", "--status");
        _csrutil = Add("csrutil", "/usr/bin/csrutil", "status");
        _firewall = Add("socketfilterfw", "/usr/libexec/ApplicationFirewall/socketfilterfw", "--getglobalstate");
        _fdesetup = Add("fdesetup", "/usr/bin/fdesetup", "status");
        _xprotect = Add("plutil", "/usr/bin/plutil", "-extract", "CFBundleShortVersionString", "raw", "-o", "-", XProtectPlist);
        _softwareupdate = Add("softwareupdate", "/usr/sbin/softwareupdate", "--list");
        _swVers = Add("sw_vers", "/usr/bin/sw_vers", "-productVersion");
        _swVersName = Add("sw_vers", "/usr/bin/sw_vers", "-productName");
        var vmStat = Add("vm_stat", "/usr/bin/vm_stat");
        var memsize = Add("sysctl", "/usr/sbin/sysctl", "-n", "hw.memsize");
        var profiler = Add("system_profiler", "/usr/sbin/system_profiler", "SPApplicationsDataType", "-json");
        var lsof = Add("lsof", "/usr/sbin/lsof", "-t");
        _run = new ReadOnlyCommandRunner(runner ?? new ProcessCommandRunner(), allow);

        var version = Environment.OSVersion.Version;
        var (targeted, note) = SupportMatrix.MacOS(version);
        Info = new PlatformInfo
        {
            Family = OsFamily.MacOS,
            OsName = $"macOS {version.Major}.{version.Minor}",
            OsVersion = version.ToString(),
            Architecture = PlatformInfo.DetectArchitecture(),
            IsElevated = MacNative.EffectiveUserId() == 0,
            IsSupportedVersion = targeted,
            SupportNote = note + " " + SupportMatrix.ReleaseStatus,
            DetectedTools = tools.Distinct().Order().ToList(),
        };

        SecurityChecks =
        [
            new DelegateCheck("mac.gatekeeper", "Gatekeeper", GatekeeperCheck),
            new DelegateCheck("mac.sip", "System Integrity Protection", SipCheck),
            new DelegateCheck("mac.xprotect", "XProtect", XProtectCheck),
            new DelegateCheck("mac.firewall", "Built-in firewall", FirewallCheck),
            new DelegateCheck("mac.filevault", "FileVault disk encryption", FileVaultCheck),
            new DelegateCheck("mac.updates", "Software updates (contacts Apple)", UpdatesCheck, onDemandOnly: true),
        ];
        Performance = new MacPerformanceProbe(_run, vmStat, memsize);
        Startup = new MacStartupInventory();
        Applications = new MacApplicationInventory(_run, profiler);
        Cleanup = new MacCleanupPlatform(_run, lsof);
        Scheduler = new LaunchdScheduler();
    }

    public PlatformInfo Info { get; }
    public IReadOnlyList<ISecurityCheck> SecurityChecks { get; }
    public IPerformanceProbe Performance { get; }
    public IStartupInventory Startup { get; }
    public IApplicationInventory Applications { get; }
    public ICleanupPlatform Cleanup { get; }
    public INativeScheduler Scheduler { get; }

    private async Task<CheckReport> Simple(string id, string title, ReadOnlyCommand? cmd, Func<string, bool?> parse, string onText, string offText, string offFollowUp)
    {
        if (cmd is null) return CheckReport.NotAvailable(id, title, "The macOS tool for this check was not found.");
        var r = await _run.RunAsync(cmd);
        var output = (r.StdOut + r.StdErr).Trim();
        var ev = new List<Evidence> { new(cmd.Display, output) };
        return parse(output) switch
        {
            true => CheckReport.Completed(id, title, Finding.NoIssueFound, onText, ev),
            false => CheckReport.Completed(id, title, Finding.Problem, offText, ev, offFollowUp),
            null when output.Contains("root", StringComparison.OrdinalIgnoreCase) || output.Contains("permission", StringComparison.OrdinalIgnoreCase)
                => CheckReport.NeedsPermission(id, title, "macOS requires administrator rights for this check.", ev.ToArray()),
            null => CheckReport.Completed(id, title, Finding.Inconclusive, "The output was not in a recognised format.", ev),
        };
    }

    private Task<CheckReport> GatekeeperCheck(CancellationToken ct) => Simple("mac.gatekeeper", "Gatekeeper", _spctl, MacParsers.GatekeeperEnabled,
        "Gatekeeper is enabled.", "Gatekeeper is disabled.", "Re-enable it in System Settings > Privacy & Security. Guardian does not change it.");

    private Task<CheckReport> SipCheck(CancellationToken ct) => Simple("mac.sip", "System Integrity Protection", _csrutil, MacParsers.SipEnabled,
        "System Integrity Protection is enabled.", "System Integrity Protection is disabled.", "SIP can only be re-enabled from macOS Recovery (Apple support article HT204899). Guardian never changes it.");

    private Task<CheckReport> FirewallCheck(CancellationToken ct) => Simple("mac.firewall", "Built-in firewall", _firewall, MacParsers.FirewallEnabled,
        "The macOS application firewall is on.", "The macOS application firewall is off.", "Turn it on in System Settings > Network > Firewall if you want it. Guardian does not change it.");

    private Task<CheckReport> FileVaultCheck(CancellationToken ct) => Simple("mac.filevault", "FileVault disk encryption", _fdesetup, MacParsers.FileVaultOn,
        "FileVault is on.", "FileVault is off.", "You can turn on FileVault in System Settings > Privacy & Security. Guardian does not change it.");

    private async Task<CheckReport> XProtectCheck(CancellationToken ct)
    {
        const string id = "mac.xprotect", title = "XProtect";
        if (_xprotect is null || !File.Exists(XProtectPlist))
            return CheckReport.NotAvailable(id, title, "XProtect's version file was not found where Apple documents it.");
        var r = await _run.RunAsync(_xprotect, ct: ct);
        var info = new FileInfo(XProtectPlist);
        var ev = new List<Evidence> { new(_xprotect.Display, r.StdOut.Trim()), new(XProtectPlist, $"last modified {info.LastWriteTimeUtc:u}") };
        var age = DateTime.UtcNow - info.LastWriteTimeUtc;
        return CheckReport.Completed(id, title, age.TotalDays > 60 ? Finding.Attention : Finding.NoIssueFound,
            $"XProtect version {r.StdOut.Trim()}, updated {age.TotalDays:0} days ago. XProtect is managed by macOS; Guardian only reads its version.",
            ev, age.TotalDays > 60 ? "Check that automatic security responses are on in System Settings > General > Software Update." : null);
    }

    private async Task<CheckReport> UpdatesCheck(CancellationToken ct)
    {
        const string id = "mac.updates", title = "Software updates (contacts Apple)";
        if (_softwareupdate is null) return CheckReport.NotAvailable(id, title, "softwareupdate was not found.");
        var r = await _run.RunAsync(_softwareupdate, TimeSpan.FromMinutes(3), ct);
        var text = r.StdOut + r.StdErr;
        var (count, restart, none) = MacParsers.SoftwareUpdates(text);
        var ev = new List<Evidence> { new(_softwareupdate.Display, $"{count} update(s); restart required: {restart}") };
        if (none) return CheckReport.Completed(id, title, Finding.NoIssueFound, "No new software available from Apple.", ev);
        if (count > 0) return CheckReport.Completed(id, title, Finding.Attention, $"{count} update(s) available{(restart ? ", at least one needs a restart" : "")}.", ev,
            "Install them from System Settings > General > Software Update. Guardian does not install macOS updates.");
        return CheckReport.Failed(id, title, "softwareupdate did not return a recognised result.", ev.ToArray());
    }
}
