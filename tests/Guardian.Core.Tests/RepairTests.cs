using Guardian.Core.Modes;
using Guardian.Core.Platform;
using Guardian.Core.Recovery;
using Guardian.Core.Repair;
using Guardian.Core.Reporting;

namespace Guardian.Core.Tests;

public class RepairTests
{
    private sealed class Preflight(params string[] blocking) : IRepairPreflight
    {
        public Task<IReadOnlyList<string>> BlockingConditionsAsync(RepairPlan plan, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(blocking);
    }

    /// <summary>Mocked executor: destructive operations are never really run in tests.</summary>
    private sealed class MockExecutor(bool runOk = true, bool verifyOk = true) : IRepairExecutor
    {
        public int Runs;
        public Task<CommandResult> RunApprovedAsync(RepairPlan plan, CancellationToken ct) { Runs++; return Task.FromResult(new CommandResult(true, runOk ? 0 : 1, "", "", false)); }
        public Task<CommandResult> VerifyAsync(RepairPlan plan, CancellationToken ct) => Task.FromResult(new CommandResult(true, verifyOk ? 0 : 1, "", "", false));
    }

    private static readonly PlatformInfo Win = new()
    {
        Family = OsFamily.Windows, OsName = "Windows 11", OsVersion = "10.0.26100", Architecture = "x64", IsElevated = true, IsSupportedVersion = true,
    };

    private static RepairPlan Plan() => new()
    {
        Id = "test.repair", TargetOs = OsFamily.Windows, Issue = "Something is broken",
        Evidence = [new Evidence("check", "broken")], AffectedComponents = ["C:\\thing"], ComponentOwner = "Operating system",
        Impact = "Replaces the thing", RestartRequired = false, SupportedOnThisOs = true, Backup = BackupKind.ManualUserBackup,
        BackupDescription = "Back up first", DependentsChecked = true, KnownDependents = [], Command = "C:\\fix.exe", Arguments = ["/fix"],
        NeedsElevation = true, VerifyCommand = "C:\\fix.exe", VerifyArguments = ["/verify"], RollbackAvailable = false,
        RollbackProcedure = "Cannot be undone", RequiredFreeBytes = 0,
    };

    private static (RepairCoordinator, MockExecutor, MemoryOperationLog) Rig(OperatingMode mode, MockExecutor? exec = null, PlatformInfo? platform = null, params string[] blocking)
    {
        var modes = new ModeManager(new MemoryModeStore { Mode = mode });
        var log = new MemoryOperationLog();
        exec ??= new MockExecutor();
        return (new RepairCoordinator(new OperationGate(modes), modes, platform ?? Win, new Preflight(blocking), exec, log), exec, log);
    }

    private static RepairApproval Approve(RepairPlan p) => new(p.Id, p.Fingerprint(), "shan", DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(OperatingMode.ObserveOnly)]
    [InlineData(OperatingMode.SafeMaintenance)]
    public async Task Repairs_are_refused_outside_controlled_repair(OperatingMode mode)
    {
        var (c, exec, log) = Rig(mode);
        var plan = Plan();
        var outcome = await c.ExecuteAsync(plan, Approve(plan));
        Assert.False(outcome.Executed);
        Assert.Equal(0, exec.Runs);
        Assert.Equal(OperationStatus.Refused, Assert.Single(log.Records).Status);
    }

    [Fact]
    public async Task Without_approval_nothing_runs()
    {
        var (c, exec, _) = Rig(OperatingMode.ControlledRepair);
        Assert.False((await c.ExecuteAsync(Plan(), null)).Executed);
        Assert.Equal(0, exec.Runs);
    }

    [Fact]
    public async Task Approval_for_a_different_plan_does_not_carry_over()
    {
        var (c, exec, _) = Rig(OperatingMode.ControlledRepair);
        var approved = Plan();
        var changed = approved with { Arguments = ["/fix", "/all"] };
        var outcome = await c.ExecuteAsync(changed, Approve(approved));
        Assert.False(outcome.Executed);
        Assert.Contains("does not match", outcome.Message);
        Assert.Equal(0, exec.Runs);
    }

    [Fact]
    public async Task Incomplete_plan_is_refused()
    {
        var (c, exec, _) = Rig(OperatingMode.ControlledRepair);
        var plan = Plan() with { DependentsChecked = null, Evidence = [] };
        var outcome = await c.ExecuteAsync(plan, Approve(plan));
        Assert.False(outcome.Executed);
        Assert.Contains("Dependency check", outcome.Message);
        Assert.Equal(0, exec.Runs);
    }

    [Fact]
    public async Task Wrong_os_or_missing_elevation_is_refused()
    {
        var plan = Plan();
        var (c1, e1, _) = Rig(OperatingMode.ControlledRepair, platform: Win with { Family = OsFamily.Linux });
        Assert.False((await c1.ExecuteAsync(plan, Approve(plan))).Executed);
        var (c2, e2, _) = Rig(OperatingMode.ControlledRepair, platform: Win with { IsElevated = false });
        Assert.False((await c2.ExecuteAsync(plan, Approve(plan))).Executed);
        Assert.Equal(0, e1.Runs + e2.Runs);
    }

    [Fact]
    public async Task Pending_restart_or_conflicting_update_blocks_the_repair()
    {
        var (c, exec, _) = Rig(OperatingMode.ControlledRepair, blocking: "A restart is pending.");
        var plan = Plan();
        var outcome = await c.ExecuteAsync(plan, Approve(plan));
        Assert.False(outcome.Executed);
        Assert.Contains("restart is pending", outcome.Message);
        Assert.Equal(0, exec.Runs);
    }

    [Fact]
    public async Task Approved_plan_runs_once_and_is_verified()
    {
        var (c, exec, log) = Rig(OperatingMode.ControlledRepair);
        var plan = Plan();
        var outcome = await c.ExecuteAsync(plan, Approve(plan));
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Verified);
        Assert.Equal(1, exec.Runs);
        var rec = Assert.Single(log.Records);
        Assert.Equal(OperationStatus.Succeeded, rec.Status);
        Assert.Equal(Reversibility.Irreversible, rec.Reversibility); // never claims rollback that does not exist
    }

    [Fact]
    public async Task Failed_verification_is_reported_as_partial_failure()
    {
        var (c, _, log) = Rig(OperatingMode.ControlledRepair, new MockExecutor(verifyOk: false));
        var plan = Plan();
        var outcome = await c.ExecuteAsync(plan, Approve(plan));
        Assert.False(outcome.Verified);
        Assert.Equal(OperationStatus.PartiallyFailed, log.Records[0].Status);
    }

    [Fact]
    public async Task Failing_repair_is_not_retried_endlessly()
    {
        var exec = new MockExecutor(runOk: false);
        var (c, _, _) = Rig(OperatingMode.ControlledRepair, exec);
        var plan = Plan();
        for (var i = 0; i < 5; i++) await c.ExecuteAsync(plan, Approve(plan));
        Assert.Equal(RetryGuard.MaxFailuresPerDay, exec.Runs);
    }
}
