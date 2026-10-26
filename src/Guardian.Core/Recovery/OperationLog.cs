using Guardian.Core.Modes;

namespace Guardian.Core.Recovery;

public enum OperationStatus { Started, Succeeded, Failed, PartiallyFailed, RolledBack, Refused }

/// <summary>Whether, and how, an operation can be undone. Never claims more than is true.</summary>
public enum Reversibility
{
    /// <summary>Guardian can undo this itself (for example, restoring a file from its holding area).</summary>
    UndoByGuardian,
    /// <summary>Guardian cannot undo it but documented manual steps exist.</summary>
    ManualProcedure,
    /// <summary>This cannot be reversed. The user was told before approving.</summary>
    Irreversible,
    /// <summary>Nothing was changed, so there is nothing to undo.</summary>
    NothingChanged,
}

/// <summary>A timestamped record of exactly what Guardian did.</summary>
public sealed record OperationRecord
{
    public required Guid Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public required string Kind { get; init; }
    public required OperatingMode ModeAtTime { get; init; }
    /// <summary>The exact target, e.g. a full file path or the exact command line.</summary>
    public required string Target { get; init; }
    public required string Description { get; init; }
    public required OperationStatus Status { get; init; }
    public required Reversibility Reversibility { get; init; }
    /// <summary>Data needed to undo, e.g. the holding-area path. Null when not reversible by Guardian.</summary>
    public string? RollbackData { get; init; }
    public string? RollbackProcedure { get; init; }
    public string? Error { get; init; }
}

public interface IOperationLog
{
    void Append(OperationRecord record);
    void Update(OperationRecord record);
    OperationRecord? Get(Guid id);
    IReadOnlyList<OperationRecord> Recent(int limit = 200);
    /// <summary>Failures for this exact target since <paramref name="since"/>; used to stop repeating failed operations.</summary>
    int CountFailures(string kind, string target, DateTimeOffset since);
}

/// <summary>Stops Guardian from repeating a failing operation over and over.</summary>
public sealed class RetryGuard(IOperationLog log, TimeProvider? clock = null)
{
    public const int MaxFailuresPerDay = 2;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public bool ShouldStop(string kind, string target) =>
        log.CountFailures(kind, target, _clock.GetUtcNow().AddDays(-1)) >= MaxFailuresPerDay;
}
