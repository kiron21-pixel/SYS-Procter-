using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Guardian.Core.Cleanup;
using Guardian.Core.Inventory;
using Guardian.Core.Performance;
using Guardian.Core.Platform;
using Guardian.Core.Repair;
using Guardian.Core.Reporting;
using Guardian.Core.Scheduling;
using Microsoft.Win32;

namespace Guardian.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPerformanceProbe : PerformanceProbeBase
{
    protected override Task<(MemoryUsage?, CheckReport)> ReadMemoryAsync(CancellationToken ct)
    {
        const string id = "perf.memory", title = "Memory";
        var s = new WindowsNative.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowsNative.MemoryStatusEx>() };
        if (!WindowsNative.GlobalMemoryStatusEx(ref s))
            return Task.FromResult<(MemoryUsage?, CheckReport)>((null, CheckReport.Failed(id, title, "GlobalMemoryStatusEx failed.")));
        var mem = new MemoryUsage((long)s.TotalPhys, (long)s.AvailPhys);
        return Task.FromResult<(MemoryUsage?, CheckReport)>((mem, CheckReport.Completed(id, title, s.MemoryLoad > 90 ? Finding.Attention : Finding.NoIssueFound,
            $"{Bytes.Format(mem.AvailableBytes)} available of {Bytes.Format(mem.TotalBytes)} ({s.MemoryLoad}% in use).",
            [new("GlobalMemoryStatusEx", $"load {s.MemoryLoad}%")],
            s.MemoryLoad > 90 ? "Close programs you are not using. Guardian never stops other programs." : null)));
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupInventory : IStartupInventory
{
    public Task<InventoryResult<StartupItem>> ListAsync(CancellationToken ct = default)
    {
        var items = new List<StartupItem>();
        void ReadRun(RegistryKey hive, string path, ItemScope scope)
        {
            using var k = hive.OpenSubKey(path);
            if (k is null) return;
            foreach (var name in k.GetValueNames())
                items.Add(new StartupItem(name, k.GetValue(name)?.ToString() ?? "", $@"{hive.Name}\{path}", scope, "Registry Run key"));
        }
        ReadRun(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", ItemScope.CurrentUser);
        ReadRun(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", ItemScope.AllUsers);
        ReadRun(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", ItemScope.AllUsers);
        foreach (var (folder, scope) in new[] { (Environment.SpecialFolder.Startup, ItemScope.CurrentUser), (Environment.SpecialFolder.CommonStartup, ItemScope.AllUsers) })
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) continue;
            items.AddRange(Directory.EnumerateFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
                .Select(f => new StartupItem(Path.GetFileNameWithoutExtension(f), f, dir, scope, "Startup folder")));
        }
        var reports = new List<CheckReport>
        {
            CheckReport.Completed("startup.windows", "Startup programs", Finding.NoIssueFound,
                $"{items.Count} startup item(s) listed. Nothing was disabled.", [new("Sources", "HKCU/HKLM Run keys, Startup folders")]),
            CheckReport.NotImplementedYet("startup.tasks", "Scheduled tasks and services at logon", "Not yet listed. Use Task Manager > Startup apps for the full view."),
        };
        return Task.FromResult(new InventoryResult<StartupItem>(items, reports));
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationInventory : IApplicationInventory
{
    public Task<InventoryResult<InstalledApplication>> ListAsync(CancellationToken ct = default)
    {
        var apps = new List<InstalledApplication>();
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || k.GetValue("SystemComponent") is 1) continue;
                apps.Add(new InstalledApplication(name, k.GetValue("DisplayVersion") as string, k.GetValue("Publisher") as string, "Windows (registry)"));
            }
        }
        var distinct = apps.DistinctBy(a => (a.Name, a.Version)).OrderBy(a => a.Name).ToList();
        return Task.FromResult(new InventoryResult<InstalledApplication>(distinct,
            [CheckReport.Completed("apps.windows", "Installed applications", Finding.NoIssueFound, $"{distinct.Count} applications listed. Nothing was changed.",
                [new("Uninstall registry keys", "HKLM (64- and 32-bit) and HKCU")], "Microsoft Store apps are not listed yet.")]));
    }
}

/// <summary>Only %TEMP% for the current user. Ownership via the file's security descriptor; in-use via an exclusive open attempt.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCleanupPlatform : ICleanupPlatform
{
    private readonly SecurityIdentifier? _me = WindowsIdentity.GetCurrent().User;

    public bool PathsAreCaseInsensitive => true;

    public IReadOnlyList<CleanupRoot> GetRoots() => [new CleanupRoot(Path.GetTempPath(), "Your temporary folder (%TEMP%)")];

    public bool? IsOwnedByCurrentUser(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            return _me is null ? null : owner == _me;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException or IdentityNotMappedException) { return null; }
    }

    public bool? IsInUse(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public sealed class TaskSchedulerXml : INativeScheduler
{
    public string Mechanism => "Windows Task Scheduler";

    public IReadOnlyList<ScheduleDefinition> Render(ScheduledJob job)
    {
        var name = @"AI Laptop Guardian\" + job.Name;
        string X(string s) => System.Security.SecurityElement.Escape(s)!;
        var start = $"2026-01-01T{job.TimeOfDay:HH\\:mm}:00";
        var trigger = job.Weekly is { } d
            ? $"<ScheduleByWeek><DaysOfWeek><{d} /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>"
            : "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>AI Laptop Guardian: {X(job.Name)}</Description></RegistrationInfo>
              <Triggers><CalendarTrigger><StartBoundary>{start}</StartBoundary>{trigger}</CalendarTrigger></Triggers>
              <Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd></IdleSettings>
                <Priority>7</Priority>
                <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author"><Exec><Command>{X(job.ExecutablePath)}</Command><Arguments>{X(string.Join(' ', job.Arguments))}</Arguments></Exec></Actions>
            </Task>
            """;
        return [new(Mechanism, job.Name.Replace(' ', '-') + ".xml", xml,
            $"schtasks /Create /TN \"{name}\" /XML <file>", $"schtasks /Delete /TN \"{name}\" /F")];
    }
}

/// <summary>Blocks repairs while Windows is mid-update, a restart is pending, or disk space is short.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRepairPreflight(ReadOnlyCommandRunner run, ReadOnlyCommand? trustedInstallerQuery) : IRepairPreflight
{
    public async Task<IReadOnlyList<string>> BlockingConditionsAsync(RepairPlan plan, CancellationToken ct)
    {
        var problems = new List<string>();
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            if (k is not null) problems.Add("Windows has a restart pending from servicing; restart first.");
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            if (k is not null) problems.Add("Windows Update needs a restart; restart first.");
        if (trustedInstallerQuery is null) problems.Add("Could not check whether Windows Modules Installer is busy.");
        else
        {
            var r = await run.RunAsync(trustedInstallerQuery, ct: ct);
            if (!r.Succeeded) problems.Add("Could not check whether Windows Modules Installer is busy.");
            else if (r.StdOut.Contains("RUNNING", StringComparison.Ordinal)) problems.Add("Windows Modules Installer is running (updates may be installing). Try again later.");
        }
        var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        if (sys.AvailableFreeSpace < plan.RequiredFreeBytes)
            problems.Add($"Not enough free space on {sys.Name} ({Bytes.Format(sys.AvailableFreeSpace)} free, {Bytes.Format(plan.RequiredFreeBytes)} needed).");
        return problems;
    }
}

/// <summary>Builders for the two Windows repairs Guardian can propose. They are only offered after a read-only check found evidence.</summary>
public static class WindowsRepairPlans
{
    public static RepairPlan DismRestoreHealth(CheckReport dismEvidence, bool elevated) => new()
    {
        Id = "win.dism.restorehealth",
        TargetOs = OsFamily.Windows,
        Issue = "The Windows component store reports repairable corruption.",
        Evidence = dismEvidence.Evidence,
        AffectedComponents = [@"Windows component store (C:\Windows\WinSxS)"],
        ComponentOwner = "Operating system (Microsoft Windows)",
        Impact = "DISM downloads replacement components from Windows Update and replaces damaged ones. It can take 10 to 30 minutes and uses the network. Other applications are not modified.",
        RestartRequired = false,
        SupportedOnThisOs = OperatingSystem.IsWindows(),
        Backup = BackupKind.ManualUserBackup,
        BackupDescription = "DISM keeps no undo copy. Back up your files first. A System Restore point may help but does not cover the component store.",
        DependentsChecked = true,
        KnownDependents = ["Windows Update and SFC rely on the component store; repairing it is what Microsoft recommends before SFC."],
        Command = @"C:\Windows\System32\dism.exe",
        Arguments = ["/Online", "/Cleanup-Image", "/RestoreHealth"],
        NeedsElevation = true,
        VerifyCommand = @"C:\Windows\System32\dism.exe",
        VerifyArguments = ["/Online", "/Cleanup-Image", "/CheckHealth"],
        RollbackAvailable = false,
        RollbackProcedure = "This cannot be undone by Guardian. If Windows misbehaves afterwards, use Settings > System > Recovery (\"Fix problems using Windows Update\" or a restore point).",
        RequiredFreeBytes = 10L * 1024 * 1024 * 1024,
    };

    public static RepairPlan SfcScanNow(CheckReport sfcEvidence) => new()
    {
        Id = "win.sfc.scannow",
        TargetOs = OsFamily.Windows,
        Issue = "System File Checker found integrity violations in protected Windows files.",
        Evidence = sfcEvidence.Evidence,
        AffectedComponents = [@"Protected Windows system files (C:\Windows\System32 and related)"],
        ComponentOwner = "Operating system (Microsoft Windows)",
        Impact = "SFC replaces damaged protected Windows files with copies from the component store. Third-party files are not touched. A restart may be needed for files in use.",
        RestartRequired = true,
        SupportedOnThisOs = OperatingSystem.IsWindows(),
        Backup = BackupKind.ManualUserBackup,
        BackupDescription = @"SFC keeps a log at C:\Windows\Logs\CBS\CBS.log but no undo copy. Back up your files first.",
        DependentsChecked = true,
        KnownDependents = ["Run the DISM repair first if DISM reported corruption, as SFC uses the component store as its source."],
        Command = @"C:\Windows\System32\sfc.exe",
        Arguments = ["/scannow"],
        NeedsElevation = true,
        VerifyCommand = @"C:\Windows\System32\sfc.exe",
        VerifyArguments = ["/verifyonly"],
        RollbackAvailable = false,
        RollbackProcedure = "This cannot be undone by Guardian. Replaced files are Microsoft's originals. If problems appear, use Settings > System > Recovery.",
        RequiredFreeBytes = 2L * 1024 * 1024 * 1024,
    };
}

/// <summary>Runs exactly the approved command line from a plan, and nothing else.</summary>
public sealed class PlanExecutor(ICommandRunner runner) : IRepairExecutor
{
    public Task<Guardian.Core.Platform.CommandResult> RunApprovedAsync(RepairPlan plan, CancellationToken ct) =>
        runner.RunAsync(plan.Command, plan.Arguments, TimeSpan.FromHours(1), ct);

    public Task<Guardian.Core.Platform.CommandResult> VerifyAsync(RepairPlan plan, CancellationToken ct) =>
        runner.RunAsync(plan.VerifyCommand, plan.VerifyArguments, TimeSpan.FromHours(1), ct);
}
