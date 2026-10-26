using Guardian.Core.Reporting;
using Guardian.Core.Repair;
using Guardian.Core.Scheduling;
using Guardian.Platform.Windows;

namespace Guardian.Platform.Tests;

/// <summary>Parser tests with representative PowerShell/DISM/SFC output; the adapter itself has not run on Windows yet.</summary>
public class WindowsTests
{
    [Fact]
    public void Defender_json_from_windows_powershell()
    {
        var s = WindowsParsers.Defender("""{"AMServiceEnabled":true,"AntivirusEnabled":true,"RealTimeProtectionEnabled":false,"AntivirusSignatureLastUpdated":"\/Date(1729900000000)\/","QuickScanEndTime":null}""")!;
        Assert.True(s.AntivirusEnabled);
        Assert.False(s.RealTimeEnabled);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1729900000000), s.SignaturesUpdated);
        Assert.Null(s.LastQuickScan);
    }

    [Fact]
    public void Security_center_product_state()
    {
        // 397568 = 0x061100 (on, up to date); 393472 = 0x060100 (off, up to date); 397584 = 0x061110 (on, out of date)
        var p = WindowsParsers.AntivirusProducts("""[{"displayName":"Windows Defender","productState":397568},{"displayName":"Other AV","productState":393472},{"displayName":"Stale AV","productState":397584}]""");
        Assert.Equal([true, false, true], p.Select(x => x.Enabled));
        Assert.Equal([true, true, false], p.Select(x => x.UpToDate));
        Assert.Single(WindowsParsers.AntivirusProducts("""{"displayName":"Only","productState":397568}"""));
    }

    [Fact]
    public void Firewall_profiles_accept_bool_or_number()
    {
        var p = WindowsParsers.FirewallProfiles("""[{"Name":"Domain","Enabled":true},{"Name":"Private","Enabled":1},{"Name":"Public","Enabled":0}]""");
        Assert.Equal([true, true, false], p.Select(x => x.Enabled));
    }

    [Fact]
    public void Dism_and_sfc()
    {
        Assert.Equal("healthy", WindowsParsers.DismHealth("No component store corruption detected.\nThe operation completed successfully."));
        Assert.Equal("repairable", WindowsParsers.DismHealth("The component store is repairable."));
        Assert.Equal("clean", WindowsParsers.SfcVerify("W\0i\0n\0d\0o\0w\0s\0 \0R\0e\0s\0o\0u\0r\0c\0e\0 \0P\0r\0o\0t\0e\0c\0t\0i\0o\0n\0 \0d\0i\0d\0 \0n\0o\0t\0 \0f\0i\0n\0d\0 \0a\0n\0y\0 \0i\0n\0t\0e\0g\0r\0i\0t\0y\0 \0v\0i\0o\0l\0a\0t\0i\0o\0n\0s\0.\0"));
        Assert.Equal("violations", WindowsParsers.SfcVerify("Windows Resource Protection found integrity violations."));
    }

    [Fact]
    public void Repair_plans_are_complete_and_never_claim_rollback()
    {
        var ev = CheckReport.Completed("win.dism", "d", Finding.Problem, "x", [new Evidence("DISM", "repairable")]);
        foreach (var plan in new[] { WindowsRepairPlans.DismRestoreHealth(ev, true), WindowsRepairPlans.SfcScanNow(ev) })
        {
            var problems = RepairPlanValidator.Problems(plan with { SupportedOnThisOs = true });
            Assert.Empty(problems);
            Assert.False(plan.RollbackAvailable);
            Assert.Contains("cannot be undone", plan.RollbackProcedure, StringComparison.OrdinalIgnoreCase);
            Assert.True(plan.NeedsElevation);
        }
    }

    [Fact]
    public void Task_scheduler_xml_runs_with_least_privilege()
    {
        var d = Assert.Single(new TaskSchedulerXml().Render(new ScheduledJob("Daily Report", @"C:\Program Files\AI Laptop Guardian\ai-laptop-guardian.exe", ["--report"], new TimeOnly(9, 30), null)));
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", d.Content);
        Assert.Contains("T09:30:00", d.Content);
        Assert.Contains("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", d.Content);
    }
}
