using Guardian.Core.Antivirus;
using Guardian.Core.Modes;
using Guardian.Core.Platform;
using Guardian.Core.Recovery;
using Guardian.Core.Reporting;

namespace Guardian.Core.Tests;

public class AntivirusTests
{
    /// <summary>Mocked engine: no real scans or scheduled tasks in tests.</summary>
    private sealed class FakeEngine(int exitCode = 0) : IAntivirusEngine
    {
        public List<string> Calls = [];
        public string EngineName => "Fake AV";
        public string DescribeScanCommand(ScanKind kind) => "fake-scan " + kind;
        public Task<ScanScheduleState> GetScheduleAsync(CancellationToken ct = default) => Task.FromResult(new ScanScheduleState(false, null, null, null, null, "fake"));
        public Task<CommandResult> InstallScheduleAsync(TimeOnly time, ScanKind kind, CancellationToken ct = default) { Calls.Add($"install {time:HH\\:mm} {kind}"); return Result(); }
        public Task<CommandResult> RemoveScheduleAsync(CancellationToken ct = default) { Calls.Add("remove"); return Result(); }
        public Task<CommandResult> RunScanAsync(ScanKind kind, CancellationToken ct = default) { Calls.Add("scan " + kind); return Result(); }
        public Task<(IReadOnlyList<ThreatDetection> Threats, CheckReport Report)> RecentThreatsAsync(TimeSpan window, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<ThreatDetection>, CheckReport)>(([], CheckReport.NotAvailable("x", "x", "x")));
        private Task<CommandResult> Result() => Task.FromResult(new CommandResult(true, exitCode, "", exitCode == 0 ? "" : "access denied", false));
    }

    private static (AntivirusCoordinator, FakeEngine, MemoryOperationLog) Rig(OperatingMode mode, int exitCode = 0)
    {
        var modes = new ModeManager(new MemoryModeStore { Mode = mode });
        var log = new MemoryOperationLog();
        var engine = new FakeEngine(exitCode);
        return (new AntivirusCoordinator(engine, new OperationGate(modes), modes, log), engine, log);
    }

    [Fact]
    public async Task Observe_only_cannot_schedule_or_start_scans()
    {
        var (c, engine, log) = Rig(OperatingMode.ObserveOnly);
        Assert.False((await c.EnableDailyScanAsync(new TimeOnly(7, 30), ScanKind.Quick)).Done);
        Assert.False((await c.ScanNowAsync(ScanKind.Quick)).Done);
        Assert.Empty(engine.Calls);
        Assert.Empty(log.Records);
    }

    [Fact]
    public async Task Safe_maintenance_schedules_at_the_requested_time_and_logs_how_to_undo()
    {
        var (c, engine, log) = Rig(OperatingMode.SafeMaintenance);
        var r = await c.EnableDailyScanAsync(new TimeOnly(7, 30), ScanKind.Quick);
        Assert.True(r.Done);
        Assert.Equal(["install 07:30 Quick"], engine.Calls);
        var rec = Assert.Single(log.Records);
        Assert.Equal(OperationStatus.Succeeded, rec.Status);
        Assert.Equal(Reversibility.UndoByGuardian, rec.Reversibility);
        Assert.Contains("07:30", rec.Target);
    }

    [Fact]
    public async Task Removing_guardians_own_schedule_is_allowed_in_observe_only()
    {
        var (c, engine, _) = Rig(OperatingMode.ObserveOnly);
        Assert.True((await c.DisableDailyScanAsync()).Done);
        Assert.Equal(["remove"], engine.Calls);
    }

    [Fact]
    public async Task Failures_are_logged_and_not_retried_endlessly()
    {
        var (c, engine, log) = Rig(OperatingMode.SafeMaintenance, exitCode: 5);
        for (var i = 0; i < 4; i++) await c.ScanNowAsync(ScanKind.Quick);
        Assert.Equal(RetryGuard.MaxFailuresPerDay, engine.Calls.Count);
        Assert.All(log.Records, r => Assert.Equal(OperationStatus.Failed, r.Status));
        Assert.Contains("access denied", log.Records[0].Error);
    }
}
