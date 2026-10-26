using Guardian.Core.Modes;
using Guardian.Core.Platform;
using Guardian.Core.Recovery;

namespace Guardian.Core.Repair;

public sealed record RepairOutcome(bool Executed, bool Succeeded, bool Verified, string Message, Guid? OperationId);

/// <summary>Runs the exact approved command and its verification. Separate so tests can mock it.</summary>
public interface IRepairExecutor
{
    Task<CommandResult> RunApprovedAsync(RepairPlan plan, CancellationToken ct);
    Task<CommandResult> VerifyAsync(RepairPlan plan, CancellationToken ct);
}

/// <summary>Pre-flight state the adapter detects before any repair: pending restarts, updates in progress, disk space.</summary>
public interface IRepairPreflight
{
    Task<IReadOnlyList<string>> BlockingConditionsAsync(RepairPlan plan, CancellationToken ct);
}

/// <summary>
/// Executes a repair only when: Controlled Repair is on, the plan is complete, the approval matches the
/// plan's exact fingerprint, the target OS matches, no blocking condition exists, and the operation has not
/// already failed repeatedly. Anything else stops before a single change is made.
/// </summary>
public sealed class RepairCoordinator(
    OperationGate gate,
    ModeManager modes,
    PlatformInfo platform,
    IRepairPreflight preflight,
    IRepairExecutor executor,
    IOperationLog log,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly RetryGuard _retry = new(log, clock);

    public async Task<RepairOutcome> ExecuteAsync(RepairPlan plan, RepairApproval? approval, CancellationToken ct = default)
    {
        var refusal = await RefusalReasonAsync(plan, approval, ct);
        if (refusal is not null)
        {
            log.Append(new OperationRecord
            {
                Id = Guid.NewGuid(), StartedAt = _clock.GetUtcNow(), FinishedAt = _clock.GetUtcNow(),
                Kind = "repair." + plan.Id, ModeAtTime = modes.Current, Target = CommandLine(plan),
                Description = plan.Issue, Status = OperationStatus.Refused, Reversibility = Reversibility.NothingChanged,
                Error = refusal,
            });
            return new(false, false, false, refusal, null);
        }

        var record = new OperationRecord
        {
            Id = Guid.NewGuid(), StartedAt = _clock.GetUtcNow(), Kind = "repair." + plan.Id, ModeAtTime = modes.Current,
            Target = CommandLine(plan), Description = plan.Issue, Status = OperationStatus.Started,
            Reversibility = plan.RollbackAvailable ? Reversibility.ManualProcedure : Reversibility.Irreversible,
            RollbackProcedure = plan.RollbackProcedure,
        };
        log.Append(record);

        var run = await executor.RunApprovedAsync(plan, ct);
        if (!run.Succeeded)
        {
            log.Update(record with { Status = OperationStatus.Failed, FinishedAt = _clock.GetUtcNow(), Error = Describe(run) });
            return new(true, false, false, $"The repair command did not succeed: {Describe(run)}. It will not be retried automatically.", record.Id);
        }

        var verify = await executor.VerifyAsync(plan, ct);
        var status = verify.Succeeded ? OperationStatus.Succeeded : OperationStatus.PartiallyFailed;
        log.Update(record with { Status = status, FinishedAt = _clock.GetUtcNow(), Error = verify.Succeeded ? null : "Verification failed: " + Describe(verify) });
        return verify.Succeeded
            ? new(true, true, true, "The approved repair ran and verification passed.", record.Id)
            : new(true, true, false, "The repair ran but verification did not pass. Review the recovery procedure.", record.Id);
    }

    private async Task<string?> RefusalReasonAsync(RepairPlan plan, RepairApproval? approval, CancellationToken ct)
    {
        var decision = gate.Check(OperationKind.Repair);
        if (!decision.Allowed) return decision.Reason;
        var problems = RepairPlanValidator.Problems(plan);
        if (problems.Count > 0) return "The repair plan is incomplete: " + string.Join(" ", problems);
        if (plan.TargetOs != platform.Family) return "This repair is for a different operating system.";
        if (approval is null) return "The user has not approved this operation.";
        if (approval.PlanId != plan.Id || approval.PlanFingerprint != plan.Fingerprint())
            return "The approval does not match this exact operation. Ask the user to review and approve it again.";
        if (plan.NeedsElevation && !platform.IsElevated) return "This repair needs administrator rights, which Guardian does not have.";
        if (_retry.ShouldStop("repair." + plan.Id, CommandLine(plan))) return "This repair already failed repeatedly today. Guardian will not retry it.";
        var blocking = await preflight.BlockingConditionsAsync(plan, ct);
        if (blocking.Count > 0) return "Stopped before making changes: " + string.Join(" ", blocking);
        return null;
    }

    private static string CommandLine(RepairPlan p) => p.Arguments.Count == 0 ? p.Command : $"{p.Command} {string.Join(' ', p.Arguments)}";
    private static string Describe(CommandResult r) => !r.Started ? "could not start (" + r.StdErr + ")" : r.TimedOut ? "timed out" : $"exit code {r.ExitCode}";
}
