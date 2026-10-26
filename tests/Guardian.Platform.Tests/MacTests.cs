using Guardian.Core.Scheduling;
using Guardian.Platform.MacOS;

namespace Guardian.Platform.Tests;

/// <summary>Parser tests use output captured from Apple's documentation and typical systems; the adapter itself has not run on a Mac yet.</summary>
public class MacTests
{
    [Fact]
    public void Security_parsers()
    {
        Assert.True(MacParsers.GatekeeperEnabled("assessments enabled\n"));
        Assert.False(MacParsers.GatekeeperEnabled("assessments disabled\n"));
        Assert.True(MacParsers.SipEnabled("System Integrity Protection status: enabled.\n"));
        Assert.False(MacParsers.SipEnabled("System Integrity Protection status: disabled.\n"));
        Assert.True(MacParsers.FirewallEnabled("Firewall is enabled. (State = 1)\n"));
        Assert.False(MacParsers.FirewallEnabled("Firewall is disabled. (State = 0)\n"));
        Assert.True(MacParsers.FileVaultOn("FileVault is On.\n"));
        Assert.False(MacParsers.FileVaultOn("FileVault is Off.\n"));
        Assert.Null(MacParsers.FileVaultOn("something unexpected"));
    }

    [Fact]
    public void VmStat_and_softwareupdate()
    {
        const string vm = "Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages free:                               10000.\nPages active:                            200000.\nPages inactive:                           50000.\nPages speculative:                         5000.\n";
        Assert.Equal((10000L + 50000 + 5000) * 16384, MacParsers.VmStatAvailable(vm));
        var (count, restart, none) = MacParsers.SoftwareUpdates("Software Update found the following new or updated software:\n* Label: macOS Sequoia 15.1-24B83\n\tTitle: macOS Sequoia 15.1, Version: 15.1, Size: 1G, Recommended: YES, Action: restart,\n");
        Assert.Equal((1, true, false), (count, restart, none));
        Assert.True(MacParsers.SoftwareUpdates("No new software available.").NoneAvailable);
    }

    [Fact]
    public void Applications_json()
    {
        var apps = MacParsers.Applications("""{"SPApplicationsDataType":[{"_name":"Safari","version":"18.1","obtained_from":"apple","signed_by":["Software Signing"]},{"_name":"Slack","version":"4.41"}]}""");
        Assert.Equal(2, apps.Count);
        Assert.Equal("18.1", apps[0].Version);
    }

    [Fact]
    public void Launchd_plist_renders()
    {
        var d = Assert.Single(new LaunchdScheduler().Render(new ScheduledJob("Daily Report", "/Applications/AI Laptop Guardian.app/Contents/MacOS/ai-laptop-guardian", ["--report"], new TimeOnly(9, 30), DayOfWeek.Monday)));
        Assert.Contains("<key>Hour</key><integer>9</integer>", d.Content);
        Assert.Contains("<key>Weekday</key><integer>1</integer>", d.Content);
        Assert.Contains("<string>--report</string>", d.Content);
    }
}
