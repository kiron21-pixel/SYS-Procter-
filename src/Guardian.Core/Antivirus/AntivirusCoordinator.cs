using Guardian.Core.Modes;
using Guardian.Core.Platform;
using Guardian.Core.Recovery;

namespace Guardian.Core.Antivirus;

/// <summary>
/// Applies the safety rules to antivirus actions. Scheduling and running scans are Safe Maintenance operations
/// ("running approved security scans"), so they are refused in Observe Only, and every one is logged with how to undo it.
/// </summary>
public sealed class AntivirusCoordinator(IAntivirusEngine engine, OperationGate gate, ModeManager modes, IOperationLog log, TimeProvider? clock = null)
{
    public const string ScheduleKind = "antivirus.schedule";
    public const string ScanKindName = "antivirus.scan";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly RetryGuard _retry = new(log, clock);

    public IAntivirusEngine Engine => engine;

    public Task<AntivirusActionResult> EnableDailyScanAsync(TimeOnly time, ScanKind kind, CancellationToken ct = default) =>
        RunLogged(ScheduleKind, $"{engine.EngineName}: daily {kind.ToString().ToLowerInvariant()} scan at {time:HH\\:mm}",
            $"Schedule a daily {kind.ToString().ToLowerInvariant()} {engine.EngineName} scan at {time:HH\\:mm}. {engine.EngineName} removes or quarantines what it finds using its own settings.",
            Reversibility.UndoByGuardian, "Turn off the daily scan in Guardian's Protection tab (removes the scheduled task).",
            () => engine.InstallScheduleAsync(time, kind, ct));

    public Task<AntivirusActionResult> DisableDailyScanAsync(CancellationToken ct = default) =>
        RunLogged(ScheduleKind, $"{engine.EngineName}: remove daily scan", "Remove Guardian's scheduled antivirus scan.",
            Reversibility.UndoByGuardian, "Turn the daily scan back on in Guardian's Protection tab.",
            () => engine.RemoveScheduleAsync(ct), allowWhenObserveOnly: true);

    public Task<AntivirusActionResult> ScanNowAsync(ScanKind kind, CancellationToken ct = default) =>
        RunLogged(ScanKindName, $"{engine.EngineName}: {kind.ToString().ToLowerInvariant()} scan now",
            $"Run a {kind.ToString().ToLowerInvariant()} {engine.EngineName} scan now. {engine.EngineName} handles anything it finds.",
            Reversibility.ManualProcedure, $"Items {engine.EngineName} quarantined can be reviewed and restored in its own protection history.",
            () => engine.RunScanAsync(kind, ct));

    private async Task<AntivirusActionResult> RunLogged(string kind, string target, string description, Reversibility reversibility,
        string rollback, Func<Task<CommandResult>> action, bool allowWhenObserveOnly = false)
    {
        // Removing Guardian's own scheduled task only lowers what Guardian does, so it is allowed in every mode.
        if (!allowWhenObserveOnly)
        {
            var decision = gate.Check(OperationKind.SafeMaintenance);
            if (!decision.Allowed) return new(false, decision.Reason, null);
        }
        if (_retry.ShouldStop(kind, target))
            return new(false, "This failed twice in the last day. Guardian will not keep retrying; see the Activity tab for the error.", null);

        var record = new OperationRecord
        {
            Id = Guid.NewGuid(), StartedAt = _clock.GetUtcNow(), Kind = kind, ModeAtTime = modes.Current, Target = target,
            Description = description, Status = OperationStatus.Started, Reversibility = reversibility, RollbackProcedure = rollback,
        };
        log.Append(record);
        var r = await action();
        if (r.Succeeded)
        {
            log.Update(record with { Status = OperationStatus.Succeeded, FinishedAt = _clock.GetUtcNow() });
            return new(true, "Done.", record.Id);
        }
        var error = !r.Started ? "Could not start: " + r.StdErr : r.TimedOut ? "Timed out." : $"Exit code {r.ExitCode}. {Trim(r.StdErr + " " + r.StdOut)}";
        log.Update(record with { Status = OperationStatus.Failed, FinishedAt = _clock.GetUtcNow(), Error = error, Reversibility = Reversibility.NothingChanged });
        return new(false, error, record.Id);
    }

    private static string Trim(string s) => s.Trim().Length > 300 ? s.Trim()[..300] + "…" : s.Trim();
}
