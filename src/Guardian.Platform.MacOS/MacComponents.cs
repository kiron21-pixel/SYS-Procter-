using Guardian.Core.Cleanup;
using Guardian.Core.Inventory;
using Guardian.Core.Performance;
using Guardian.Core.Platform;
using Guardian.Core.Reporting;
using Guardian.Core.Scheduling;

namespace Guardian.Platform.MacOS;

public sealed class MacPerformanceProbe(ReadOnlyCommandRunner run, ReadOnlyCommand? vmStat, ReadOnlyCommand? memsize) : PerformanceProbeBase
{
    protected override bool IncludeDrive(DriveInfo d) => base.IncludeDrive(d) &&
        (d.Name == "/" || d.Name.StartsWith("/Volumes/", StringComparison.Ordinal));

    protected override async Task<(MemoryUsage?, CheckReport)> ReadMemoryAsync(CancellationToken ct)
    {
        const string id = "perf.memory", title = "Memory";
        if (vmStat is null || memsize is null) return (null, CheckReport.NotAvailable(id, title, "vm_stat or sysctl was not found."));
        var total = await run.RunAsync(memsize, ct: ct);
        var vm = await run.RunAsync(vmStat, ct: ct);
        if (!long.TryParse(total.StdOut.Trim(), out var t) || MacParsers.VmStatAvailable(vm.StdOut) is not { } avail)
            return (null, CheckReport.Failed(id, title, "Could not read memory statistics."));
        var mem = new MemoryUsage(t, avail);
        return (mem, CheckReport.Completed(id, title, Finding.NoIssueFound,
            $"About {Bytes.Format(avail)} available of {Bytes.Format(t)} (approximate: macOS uses spare memory for caches and compression).",
            [new(memsize.Display, total.StdOut.Trim()), new(vmStat.Display, "free + inactive + speculative pages")]));
    }
}

public sealed class MacStartupInventory : IStartupInventory
{
    public Task<InventoryResult<StartupItem>> ListAsync(CancellationToken ct = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var items = new List<StartupItem>();
        foreach (var (dir, scope, mech) in new[]
                 {
                     (Path.Combine(home, "Library/LaunchAgents"), ItemScope.CurrentUser, "LaunchAgent"),
                     ("/Library/LaunchAgents", ItemScope.AllUsers, "LaunchAgent"),
                     ("/Library/LaunchDaemons", ItemScope.System, "LaunchDaemon"),
                 })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                items.AddRange(Directory.EnumerateFiles(dir, "*.plist")
                    .Select(f => new StartupItem(Path.GetFileNameWithoutExtension(f), "", f, scope, mech)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var reports = new List<CheckReport>
        {
            CheckReport.Completed("startup.launchd", "Launch agents and daemons", Finding.NoIssueFound,
                $"{items.Count} launchd item(s) listed. Nothing was disabled.", [new("Folders", "~/Library/LaunchAgents, /Library/LaunchAgents, /Library/LaunchDaemons")]),
            CheckReport.NotImplementedYet("startup.login-items", "Login items (System Settings)",
                "Reading Login Items needs administrator rights (sfltool dumpbtm). Open System Settings > General > Login Items to review them."),
        };
        return Task.FromResult(new InventoryResult<StartupItem>(items, reports));
    }
}

public sealed class MacApplicationInventory(ReadOnlyCommandRunner run, ReadOnlyCommand? profiler) : IApplicationInventory
{
    public async Task<InventoryResult<InstalledApplication>> ListAsync(CancellationToken ct = default)
    {
        const string id = "apps.mac", title = "Applications";
        if (profiler is null) return new([], [CheckReport.NotAvailable(id, title, "system_profiler was not found.")]);
        var r = await run.RunAsync(profiler, TimeSpan.FromMinutes(2), ct);
        if (!r.Succeeded) return new([], [CheckReport.Failed(id, title, "system_profiler did not complete.")]);
        var apps = MacParsers.Applications(r.StdOut);
        return new(apps, [CheckReport.Completed(id, title, Finding.NoIssueFound, $"{apps.Count} applications listed. Nothing was changed.", [new(profiler.Display, $"{apps.Count} entries")])]);
    }
}

/// <summary>
/// Only the per-user temporary folder ($TMPDIR, under /var/folders) is scanned. Ownership via lstat; in-use via "lsof -t".
/// </summary>
public sealed class MacCleanupPlatform(ReadOnlyCommandRunner run, ReadOnlyCommand? lsof) : ICleanupPlatform
{
    public bool PathsAreCaseInsensitive => true;

    public IReadOnlyList<CleanupRoot> GetRoots() => [new CleanupRoot(Path.GetTempPath(), "Your per-user temporary folder ($TMPDIR)")];

    public bool? IsOwnedByCurrentUser(string path) => MacNative.OwnerUid(path) is { } uid ? uid == MacNative.EffectiveUserId() : null;

    public bool? IsInUse(string path)
    {
        if (lsof is null) return null;
        var r = run.RunWithPathAsync(lsof, path, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (!r.Started || r.TimedOut) return null;
        // lsof exits 0 with PIDs when open, 1 with no output when not open.
        return r.ExitCode switch { 0 => r.StdOut.Trim().Length > 0, 1 when r.StdOut.Trim().Length == 0 => false, _ => null };
    }
}

public sealed class LaunchdScheduler : INativeScheduler
{
    public string Mechanism => "launchd (LaunchAgent)";

    public IReadOnlyList<ScheduleDefinition> Render(ScheduledJob job)
    {
        var label = "app.ailaptopguardian." + job.Name.ToLowerInvariant().Replace(' ', '-');
        var args = string.Join("\n", new[] { job.ExecutablePath }.Concat(job.Arguments).Select(a => $"        <string>{System.Security.SecurityElement.Escape(a)}</string>"));
        var weekday = job.Weekly is { } d ? $"\n        <key>Weekday</key><integer>{(int)d}</integer>" : "";
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>{label}</string>
                <key>ProgramArguments</key>
                <array>
            {args}
                </array>
                <key>StartCalendarInterval</key>
                <dict>
                    <key>Hour</key><integer>{job.TimeOfDay.Hour}</integer>
                    <key>Minute</key><integer>{job.TimeOfDay.Minute}</integer>{weekday}
                </dict>
                <key>ProcessType</key><string>Background</string>
                <key>LowPriorityIO</key><true/>
            </dict>
            </plist>
            """;
        return [new(Mechanism, label + ".plist", plist,
            $"Copy to ~/Library/LaunchAgents/ then run: launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/{label}.plist",
            $"launchctl bootout gui/$(id -u)/{label} && rm ~/Library/LaunchAgents/{label}.plist")];
    }
}
