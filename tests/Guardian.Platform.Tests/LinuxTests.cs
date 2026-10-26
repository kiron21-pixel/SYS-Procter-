using Guardian.Core.Inventory;
using Guardian.Core.Platform;
using Guardian.Core.Reporting;
using Guardian.Core.Scheduling;
using Guardian.Platform.Linux;

namespace Guardian.Platform.Tests;

public class LinuxTests
{
    [Fact]
    public void OsRelease_parses_quotes_and_picks_package_manager()
    {
        var ubuntu = OsRelease.Parse("PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nID=ubuntu\nID_LIKE=debian\nVERSION_ID=\"24.04\"\n");
        Assert.Equal("Ubuntu 24.04.1 LTS", ubuntu["PRETTY_NAME"]);
        Assert.Equal("apt", OsRelease.PackageManagerFor(ubuntu));
        Assert.Equal("dnf", OsRelease.PackageManagerFor(OsRelease.Parse("ID=fedora\nVERSION_ID=42")));
        Assert.Equal("apt", OsRelease.PackageManagerFor(OsRelease.Parse("ID=linuxmint\nID_LIKE=\"ubuntu debian\"")));
        Assert.Null(OsRelease.PackageManagerFor(OsRelease.Parse("ID=arch")));
        Assert.Null(OsRelease.PackageManagerFor(OsRelease.Parse("ID=opensuse-tumbleweed\nID_LIKE=\"opensuse suse\"")));
    }

    [Fact]
    public void Parsers_handle_real_output()
    {
        Assert.Equal((16L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024),
            LinuxParsers.MemInfo("MemTotal:       16777216 kB\nMemFree:         1000 kB\nMemAvailable:    8388608 kB\n"));
        Assert.Equal((2, 1), LinuxParsers.AptSimulatedUpgrades(
            "NOTE: This is only a simulation!\nInst libssl3 [3.0.13-0ubuntu3.4] (3.0.13-0ubuntu3.5 Ubuntu:24.04/noble-security [amd64])\n" +
            "Inst vim [2:9.1.0016-1ubuntu7.2] (2:9.1.0016-1ubuntu7.3 Ubuntu:24.04/noble-updates [amd64])\nConf libssl3 (3.0.13)\n"));
        Assert.Equal(2, LinuxParsers.DnfCheckUpdateCount("\nkernel.x86_64    6.11.4-301.fc42    updates\nfirefox.x86_64   131.0.3-1.fc42   updates\n"));
        var clam = LinuxParsers.ClamVersion("ClamAV 1.0.7/27440/Sat Oct 19 08:35:01 2024\n");
        Assert.Equal("1.0.7", clam!.Value.Engine);
        Assert.Equal(new DateTimeOffset(2024, 10, 19, 8, 35, 1, TimeSpan.Zero), clam.Value.SignaturesDate);
    }

    [Fact]
    public void Lsblk_detects_root_on_luks()
    {
        const string enc = """{"blockdevices":[{"name":"nvme0n1","type":"disk","mountpoint":null,"children":[{"name":"nvme0n1p1","type":"part","mountpoint":"/boot/efi"},{"name":"nvme0n1p3","type":"part","mountpoint":null,"children":[{"name":"luks-1","type":"crypt","mountpoint":null,"children":[{"name":"vg-root","type":"lvm","mountpoint":"/"}]}]}]}]}""";
        const string plain = """{"blockdevices":[{"name":"sda","type":"disk","mountpoint":null,"children":[{"name":"sda1","type":"part","mountpoint":"/"}]}]}""";
        Assert.Equal((true, true, true), LinuxParsers.LsblkEncryption(enc));
        Assert.Equal((false, false, true), LinuxParsers.LsblkEncryption(plain));
    }

    [Fact]
    public void Autostart_respects_hidden_and_disabled()
    {
        Assert.Equal("Dropbox", LinuxParsers.DesktopAutostart("[Desktop Entry]\nName=Dropbox\nExec=dropbox start -i\n", "/x/dropbox.desktop", ItemScope.CurrentUser)!.Name);
        Assert.Null(LinuxParsers.DesktopAutostart("[Desktop Entry]\nName=X\nHidden=true\n", "/x/x.desktop", ItemScope.CurrentUser));
        Assert.Null(LinuxParsers.DesktopAutostart("[Desktop Entry]\nName=X\nX-GNOME-Autostart-enabled=false\n", "/x/x.desktop", ItemScope.CurrentUser));
    }

    private static string WriteOsRelease(string content)
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, content);
        return f;
    }

    /// <summary>Words that would indicate a state-changing command. "upgrade" only appears in apt-get's -s simulation.</summary>
    private static readonly string[] MutatingWords = ["install", "remove", "purge", "autoremove", "erase", "enable", "disable", "stop", "start", "restart", "kill", "rm ", "delete", "reset", "--set", "--add", "--remove"];

    private static void AssertReadOnly(IEnumerable<string> calls)
    {
        foreach (var c in calls)
        {
            Assert.DoesNotContain(MutatingWords, w => c.Contains(" " + w.Trim(), StringComparison.OrdinalIgnoreCase) && !c.Contains("--state=enabled") && !c.Contains("is-enabled"));
            if (c.EndsWith(" upgrade", StringComparison.Ordinal)) Assert.Contains("apt-get -s ", c);
        }
    }

    [Fact]
    public async Task Fedora_never_runs_apt_and_ubuntu_never_runs_dnf()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var (osr, forbidden) in new[] { ("ID=fedora\nVERSION_ID=42\n", "apt"), ("ID=ubuntu\nVERSION_ID=\"24.04\"\n", "dnf"), ("ID=arch\n", "apt") })
        {
            var runner = new Guardian.Core.Tests.RecordingRunner();
            var adapter = new LinuxAdapter(runner, WriteOsRelease(osr));
            foreach (var c in adapter.SecurityChecks) await c.RunAsync();
            await adapter.Startup.ListAsync();
            await adapter.Applications.ListAsync();
            Assert.DoesNotContain(runner.Calls, c => c.Contains("/" + forbidden));
            if (osr.StartsWith("ID=arch")) Assert.DoesNotContain(runner.Calls, c => c.Contains("/dnf") || c.Contains("/rpm") || c.Contains("dpkg"));
            AssertReadOnly(runner.Calls);
        }
    }

    [Fact]
    public async Task Unknown_distribution_reports_unsupported_instead_of_guessing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var adapter = new LinuxAdapter(new Guardian.Core.Tests.RecordingRunner(), WriteOsRelease("ID=arch\nPRETTY_NAME=\"Arch Linux\"\n"));
        Assert.False(adapter.Info.IsSupportedVersion);
        var updates = await adapter.SecurityChecks.Single(c => c.Id == "linux.updates").RunAsync();
        Assert.Equal(SupportStatus.Unsupported, updates.Support);
        Assert.False(updates.Ran);
    }

    [Fact]
    public void Cleanup_platform_reads_real_ownership_and_open_files()
    {
        if (!OperatingSystem.IsLinux()) return;
        var p = new LinuxCleanupPlatform();
        var f = Path.GetTempFileName();
        try
        {
            Assert.True(p.IsOwnedByCurrentUser(f));
            Assert.False(p.IsInUse(f));
            using var open = File.OpenRead(f);
            Assert.True(new LinuxCleanupPlatform().IsInUse(f));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Systemd_timer_renders()
    {
        var defs = new SystemdUserTimerScheduler().Render(new ScheduledJob("Daily Report", "/opt/ai-laptop-guardian/ai-laptop-guardian", ["--report"], new TimeOnly(9, 30), null));
        Assert.Equal(2, defs.Count);
        Assert.Contains("ExecStart=/opt/ai-laptop-guardian/ai-laptop-guardian --report", defs[0].Content);
        Assert.Contains("OnCalendar=*-*-* 09:30:00", defs[1].Content);
    }
}
