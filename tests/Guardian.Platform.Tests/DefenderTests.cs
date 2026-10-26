using Guardian.Core.Antivirus;
using Guardian.Core.Reporting;
using Guardian.Platform.Windows;

namespace Guardian.Platform.Tests;

public class DefenderTests
{
    [Fact]
    public void Protection_report_flags_tamper_protection_off_as_a_problem()
    {
        var ok = DefenderProtectionReport.Build(new DefenderProtection(true, true, true, true, 0, "Normal", 2, 1));
        Assert.Equal(Finding.NoIssueFound, ok.Finding);

        var tamperOff = DefenderProtectionReport.Build(new DefenderProtection(true, false, true, true, 0, "Normal", 2, 1));
        Assert.Equal(Finding.Problem, tamperOff.Finding);
        Assert.Contains("tamper protection", tamperOff.Summary);

        var stale = DefenderProtectionReport.Build(new DefenderProtection(true, true, true, true, 9, "Normal", 2, 0));
        Assert.Equal(Finding.Attention, stale.Finding);

        var passive = DefenderProtectionReport.Build(new DefenderProtection(false, false, null, null, 0, "Passive Mode", 0, 0));
        Assert.Equal(Finding.Attention, passive.Finding);
        Assert.Contains("another antivirus", passive.Summary);
    }

    [Fact]
    public void Protection_json()
    {
        var p = DefenderParsers.Protection("""{"RealTime":true,"Tamper":true,"Behavior":true,"Ioav":true,"SignatureAgeDays":1,"Mode":"Normal","Maps":2,"Pua":1}""")!;
        Assert.Equal(1, p.SignatureAgeDays);
        Assert.Equal(2, p.Maps);
    }

    [Fact]
    public void Threat_history_separates_resolved_from_unresolved()
    {
        var t = DefenderParsers.Threats("""[{"Name":"Trojan:Win32/Wacatac.B!ml","Severity":5,"Detected":"2026-10-20T07:31:00+05:30","StatusId":3,"Resources":["file:_C:\\Users\\shan\\Downloads\\setup.exe"]},{"Name":"PUA:Win32/Presenoker","Severity":2,"Detected":"2026-10-21T07:31:00+05:30","StatusId":103,"Resources":[]}]""");
        Assert.Equal(2, t.Count);
        Assert.Equal(("Quarantined", false), (t[0].Detection.Status, t[0].Unresolved));
        Assert.Equal("Severe", t[0].Detection.Severity);
        Assert.Equal(("Removal failed", true), (t[1].Detection.Status, t[1].Unresolved));
        Assert.Empty(DefenderParsers.Threats("[]"));
    }

    [Fact]
    public void Schedule_state()
    {
        Assert.False(DefenderParsers.Schedule("{}", "x").Installed);
        var s = DefenderParsers.Schedule("""{"Start":"2026-01-01T07:30:00","Args":"-Scan -ScanType 1","LastRun":"2026-10-26T07:30:05+05:30","LastResult":0}""", "x");
        Assert.True(s.Installed);
        Assert.Equal(new TimeOnly(7, 30), s.Time);
        Assert.Equal(ScanKind.Quick, s.Kind);
        Assert.Equal("Completed, no threats found", s.LastResult);
        var never = DefenderParsers.Schedule("""{"Start":"2026-01-01T07:30:00","Args":"-Scan -ScanType 2","LastRun":"1999-11-30T00:00:00","LastResult":267011}""", "x");
        Assert.Equal(ScanKind.Full, never.Kind);
        Assert.Equal("Not run yet", never.LastResult);
    }

    [Fact]
    public void Task_xml_runs_defender_daily_as_the_user_and_catches_up()
    {
        var xml = DefenderTaskXml.Render(@"C:\Program Files\Windows Defender\MpCmdRun.exe", new TimeOnly(7, 30), ScanKind.Quick);
        Assert.Contains("<StartBoundary>2026-01-01T07:30:00</StartBoundary>", xml);
        Assert.Contains("<DaysInterval>1</DaysInterval>", xml);
        Assert.Contains("<Arguments>-Scan -ScanType 1</Arguments>", xml);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml);
        Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml);
        Assert.DoesNotContain("SYSTEM", xml);
        Assert.DoesNotContain("HighestAvailable", xml);
    }

    [Fact]
    public void Pending_updates_json()
    {
        var u = DefenderParsers.PendingUpdates("""[{"Title":"2026-10 Cumulative Update for Windows 11","MsrcSeverity":"Critical"},{"Title":"Driver","MsrcSeverity":null}]""");
        Assert.Equal(2, u.Count);
        Assert.Equal("Critical", u[0].Severity);
    }
}
