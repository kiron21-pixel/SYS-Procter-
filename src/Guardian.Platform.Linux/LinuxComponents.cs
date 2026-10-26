using Guardian.Core.Cleanup;
using Guardian.Core.Inventory;
using Guardian.Core.Performance;
using Guardian.Core.Platform;
using Guardian.Core.Reporting;
using Guardian.Core.Scheduling;

namespace Guardian.Platform.Linux;

public sealed class LinuxPerformanceProbe : PerformanceProbeBase
{
    private static readonly string[] VirtualFormats = ["tmpfs", "devtmpfs", "overlay", "squashfs", "proc", "sysfs", "cgroup2", "devpts", "fuse.snapfuse"];

    protected override bool IncludeDrive(DriveInfo d) => base.IncludeDrive(d) && !VirtualFormats.Contains(d.DriveFormat) && !d.Name.StartsWith("/snap/", StringComparison.Ordinal);

    protected override Task<(MemoryUsage?, CheckReport)> ReadMemoryAsync(CancellationToken ct)
    {
        const string id = "perf.memory", title = "Memory";
        const string src = "/proc/meminfo";
        if (!File.Exists(src)) return Task.FromResult<(MemoryUsage?, CheckReport)>((null, CheckReport.NotAvailable(id, title, "/proc/meminfo is not readable.")));
        var m = LinuxParsers.MemInfo(File.ReadAllText(src));
        if (m is null) return Task.FromResult<(MemoryUsage?, CheckReport)>((null, CheckReport.Failed(id, title, "Could not parse /proc/meminfo.")));
        var mem = new MemoryUsage(m.Value.Total, m.Value.Available);
        var pct = 100.0 * mem.AvailableBytes / mem.TotalBytes;
        var report = CheckReport.Completed(id, title, pct < 10 ? Finding.Attention : Finding.NoIssueFound,
            $"{Bytes.Format(mem.AvailableBytes)} available of {Bytes.Format(mem.TotalBytes)}.",
            [new(src, $"MemTotal/MemAvailable ({pct:0}% available)")],
            pct < 10 ? "Close programs you are not using. Guardian never stops other programs." : null);
        return Task.FromResult<(MemoryUsage?, CheckReport)>((mem, report));
    }
}

public sealed class LinuxStartupInventory(ReadOnlyCommandRunner run, ReadOnlyCommand? userUnits) : IStartupInventory
{
    public async Task<InventoryResult<StartupItem>> ListAsync(CancellationToken ct = default)
    {
        var items = new List<StartupItem>();
        var reports = new List<CheckReport>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        foreach (var (dir, scope) in new[] { (Path.Combine(xdgConfig, "autostart"), ItemScope.CurrentUser), ("/etc/xdg/autostart", ItemScope.AllUsers) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.desktop"))
            {
                try { if (LinuxParsers.DesktopAutostart(File.ReadAllText(f), f, scope) is { } item) items.Add(item); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        reports.Add(CheckReport.Completed("startup.xdg", "Desktop autostart entries", Finding.NoIssueFound,
            $"{items.Count} enabled autostart entr{(items.Count == 1 ? "y" : "ies")} found. Listed only; nothing was disabled.",
            [new("Folders", $"{Path.Combine(xdgConfig, "autostart")}, /etc/xdg/autostart")]));

        if (userUnits is null)
            reports.Add(CheckReport.NotAvailable("startup.systemd-user", "systemd user services", "systemctl was not found."));
        else
        {
            var r = await run.RunAsync(userUnits, ct: ct);
            if (r.Succeeded)
            {
                var units = LinuxParsers.EnabledUnits(r.StdOut);
                items.AddRange(units.Select(u => new StartupItem(u, "", "systemd --user", ItemScope.CurrentUser, "systemd user unit")));
                reports.Add(CheckReport.Completed("startup.systemd-user", "systemd user services", Finding.NoIssueFound, $"{units.Count} enabled user unit(s).", [new(userUnits.Display, $"{units.Count} enabled")]));
            }
            else reports.Add(CheckReport.Failed("startup.systemd-user", "systemd user services", "No user systemd session was reachable.", new Evidence(userUnits.Display, r.StdErr.Trim())));
        }
        return new(items, reports);
    }
}

public sealed class LinuxApplicationInventory(ReadOnlyCommandRunner run, string? packageManager,
    ReadOnlyCommand? dpkg, ReadOnlyCommand? rpm, ReadOnlyCommand? flatpak, ReadOnlyCommand? snap) : IApplicationInventory
{
    public async Task<InventoryResult<InstalledApplication>> ListAsync(CancellationToken ct = default)
    {
        var items = new List<InstalledApplication>();
        var reports = new List<CheckReport>();
        var native = packageManager switch { "apt" => dpkg, "dnf" => rpm, _ => null };
        if (native is null)
            reports.Add(CheckReport.NotSupported("apps.native", "System packages", "No supported package manager for this distribution."));
        else
        {
            var r = await run.RunAsync(native, TimeSpan.FromSeconds(60), ct);
            var pkgs = r.Succeeded ? LinuxParsers.TabSeparatedPackages(r.StdOut, packageManager!) : [];
            items.AddRange(pkgs);
            reports.Add(r.Succeeded
                ? CheckReport.Completed("apps.native", "System packages", Finding.NoIssueFound, $"{pkgs.Count} packages listed. Nothing was changed.", [new(native.Display, $"{pkgs.Count} lines")])
                : CheckReport.Failed("apps.native", "System packages", "Package listing failed.", new Evidence(native.Display, r.StdErr.Trim())));
        }
        if (flatpak is not null)
        {
            var r = await run.RunAsync(flatpak, ct: ct);
            if (r.Succeeded) items.AddRange(LinuxParsers.TabSeparatedPackages(r.StdOut, "flatpak"));
        }
        if (snap is not null)
        {
            var r = await run.RunAsync(snap, ct: ct);
            if (r.Succeeded)
                items.AddRange(r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                    .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length >= 2).Select(p => new InstalledApplication(p[0], p[1], p.ElementAtOrDefault(4), "snap")));
        }
        return new(items, reports);
    }
}

/// <summary>
/// Cleanup facts on Linux. Only $TMPDIR or /tmp is scanned (never /var/tmp, which is meant to persist).
/// Ownership is read with statx. The in-use check reads /proc/[pid]/fd for processes this user can see; processes of
/// other users are not visible without root, which is a documented limitation.
/// </summary>
public sealed class LinuxCleanupPlatform : ICleanupPlatform
{
    private HashSet<string>? _openFiles;
    private DateTime _openFilesAt;

    public bool PathsAreCaseInsensitive => false;

    public IReadOnlyList<CleanupRoot> GetRoots()
    {
        var tmp = Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : "/tmp";
        return [new CleanupRoot(tmp, "Temporary folder ($TMPDIR or /tmp); only files owned by you")];
    }

    public bool? IsOwnedByCurrentUser(string path) =>
        LinuxNative.OwnerUid(path) is { } uid ? uid == LinuxNative.EffectiveUserId() : null;

    public bool? IsInUse(string path)
    {
        if (_openFiles is null || DateTime.UtcNow - _openFilesAt > TimeSpan.FromSeconds(5))
        {
            _openFiles = ReadOpenFiles();
            _openFilesAt = DateTime.UtcNow;
        }
        return _openFiles?.Contains(Path.GetFullPath(path));
    }

    private static HashSet<string>? ReadOpenFiles()
    {
        if (!Directory.Exists("/proc")) return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pidDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(pidDir), out _)) continue;
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(pidDir, "fd")))
                {
                    try { if (new FileInfo(fd).LinkTarget is { } target) set.Add(target); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                // Memory-mapped files (shared libraries, mmapped data) count as in use too.
                var maps = Path.Combine(pidDir, "map_files");
                if (Directory.Exists(maps))
                    foreach (var m in Directory.EnumerateFileSystemEntries(maps))
                    {
                        try { if (new FileInfo(m).LinkTarget is { } target) set.Add(target); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return set;
    }
}

public sealed class SystemdUserTimerScheduler : INativeScheduler
{
    public string Mechanism => "systemd user timer";

    public IReadOnlyList<ScheduleDefinition> Render(ScheduledJob job)
    {
        var unit = "ai-laptop-guardian-" + job.Name.ToLowerInvariant().Replace(' ', '-');
        var exec = string.Join(' ', new[] { job.ExecutablePath }.Concat(job.Arguments).Select(Quote));
        var when = job.Weekly is { } d ? $"{d.ToString()[..3]} *-*-* {job.TimeOfDay:HH\\:mm}:00" : $"*-*-* {job.TimeOfDay:HH\\:mm}:00";
        var service = $"""
            [Unit]
            Description=AI Laptop Guardian: {job.Name}

            [Service]
            Type=oneshot
            ExecStart={exec}
            Nice=10
            IOSchedulingClass=idle
            """;
        var timer = $"""
            [Unit]
            Description=AI Laptop Guardian schedule: {job.Name}

            [Timer]
            OnCalendar={when}
            Persistent=true
            RandomizedDelaySec=10min

            [Install]
            WantedBy=timers.target
            """;
        const string dir = "~/.config/systemd/user";
        return
        [
            new(Mechanism, unit + ".service", service, $"Copy to {dir}/", $"rm {dir}/{unit}.service"),
            new(Mechanism, unit + ".timer", timer,
                $"Copy to {dir}/ then run: systemctl --user daemon-reload && systemctl --user enable --now {unit}.timer",
                $"systemctl --user disable --now {unit}.timer && rm {dir}/{unit}.timer && systemctl --user daemon-reload"),
        ];
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
}
