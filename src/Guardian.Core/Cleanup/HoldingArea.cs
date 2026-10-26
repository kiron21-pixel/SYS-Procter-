using System.Text.Json;
using Guardian.Core.Modes;
using Guardian.Core.Recovery;

namespace Guardian.Core.Cleanup;

public sealed record HeldFile(string OriginalPath, string HeldPath, long SizeBytes, DateTime LastWriteUtc);

public sealed record CleanupActionResult(string Path, bool Moved, string Message, Guid? OperationId);

/// <summary>
/// Safe Maintenance cleanup. Selected files are moved, not deleted, into Guardian's own holding area and can
/// be restored until the retention period ends. Every file is re-checked against every safeguard immediately
/// before it is moved, and the whole call is refused unless Safe Maintenance is enabled.
/// </summary>
public sealed class HoldingAreaCleaner(
    string holdingRoot,
    CleanupScanner scanner,
    OperationGate gate,
    ModeManager modes,
    IOperationLog log,
    TimeProvider? clock = null)
{
    public const string OperationKindName = "cleanup.move-to-holding";
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly RetryGuard _retry = new(log, clock);

    public string HoldingRoot => holdingRoot;

    public IReadOnlyList<CleanupActionResult> MoveToHolding(IReadOnlyList<CleanupCandidate> selected)
    {
        var decision = gate.Check(OperationKind.SafeMaintenance);
        if (!decision.Allowed)
            return selected.Select(c => new CleanupActionResult(c.Path, false, decision.Reason, null)).ToList();

        var results = new List<CleanupActionResult>();
        foreach (var c in selected)
            results.Add(MoveOne(c));
        return results;
    }

    private CleanupActionResult MoveOne(CleanupCandidate selected)
    {
        if (_retry.ShouldStop(OperationKindName, selected.Path))
            return new(selected.Path, false, "Skipped: this file failed to move twice in the last day. Guardian will not keep retrying.", null);

        // Re-validate right now; the scan may be minutes old.
        var now = scanner.Evaluate(selected.RootPath, selected.Path);
        if (!now.IsEligible)
            return new(selected.Path, false, $"Not moved: {now.Explanation}", null);
        if (now.SizeBytes != selected.SizeBytes || now.LastWriteUtc != selected.LastWriteUtc)
            return new(selected.Path, false, "Not moved: the file changed since the scan.", null);

        var id = Guid.NewGuid();
        var destDir = Path.Combine(holdingRoot, id.ToString("N"));
        var dest = Path.Combine(destDir, Path.GetFileName(selected.Path));
        var held = new HeldFile(selected.Path, dest, now.SizeBytes, now.LastWriteUtc);
        var record = new OperationRecord
        {
            Id = id,
            StartedAt = _clock.GetUtcNow(),
            Kind = OperationKindName,
            ModeAtTime = modes.Current,
            Target = selected.Path,
            Description = $"Move temporary file to Guardian holding area ({Performance.Bytes.Format(now.SizeBytes)})",
            Status = OperationStatus.Started,
            Reversibility = Reversibility.UndoByGuardian,
            RollbackData = JsonSerializer.Serialize(held),
            RollbackProcedure = $"Restore from the Activity screen, or move '{dest}' back to '{selected.Path}'.",
        };
        log.Append(record);

        try
        {
            if (!HasSpaceFor(destDir, now.SizeBytes))
                throw new IOException("Not enough free space in the holding area's drive.");
            Directory.CreateDirectory(destDir);
            File.Move(selected.Path, dest, overwrite: false);
            log.Update(record with { Status = OperationStatus.Succeeded, FinishedAt = _clock.GetUtcNow() });
            return new(selected.Path, true, "Moved to holding area. You can restore it for 7 days.", id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Update(record with
            {
                Status = OperationStatus.Failed, FinishedAt = _clock.GetUtcNow(), Error = ex.Message,
                Reversibility = File.Exists(selected.Path) ? Reversibility.NothingChanged : Reversibility.ManualProcedure,
            });
            TryRemoveEmptyDir(destDir);
            return new(selected.Path, false, $"Not moved: {ex.Message}", id);
        }
    }

    /// <summary>Moves a held file back to where it came from. Never overwrites a file that now exists there.</summary>
    public CleanupActionResult Restore(Guid operationId)
    {
        var record = log.Get(operationId);
        if (record is null || record.Kind != OperationKindName || record.RollbackData is null)
            return new("", false, "No restorable operation with that id.", operationId);
        if (record.Status != OperationStatus.Succeeded || record.Reversibility != Reversibility.UndoByGuardian)
            return new(record.Target, false, "This operation cannot be restored by Guardian (it failed, was already restored, or was purged).", operationId);

        var held = JsonSerializer.Deserialize<HeldFile>(record.RollbackData)!;
        if (!File.Exists(held.HeldPath))
            return new(held.OriginalPath, false, "The held file is missing from the holding area.", operationId);
        if (File.Exists(held.OriginalPath))
            return new(held.OriginalPath, false, "A file already exists at the original location. Guardian will not overwrite it.", operationId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(held.OriginalPath)!);
            File.Move(held.HeldPath, held.OriginalPath, overwrite: false);
            TryRemoveEmptyDir(Path.GetDirectoryName(held.HeldPath)!);
            log.Update(record with { Status = OperationStatus.RolledBack, Reversibility = Reversibility.NothingChanged });
            return new(held.OriginalPath, true, "Restored to its original location.", operationId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(held.OriginalPath, false, $"Restore failed: {ex.Message}", operationId);
        }
    }

    /// <summary>
    /// Permanently removes held files older than the retention period. Requires Safe Maintenance.
    /// After this, the log is updated to say the operation can no longer be undone.
    /// </summary>
    public int PurgeExpired()
    {
        if (!gate.Check(OperationKind.SafeMaintenance).Allowed) return 0;
        var cutoff = _clock.GetUtcNow() - Retention;
        var purged = 0;
        foreach (var r in log.Recent(10_000).Where(r => r.Kind == OperationKindName && r.Status == OperationStatus.Succeeded
                     && r.Reversibility == Reversibility.UndoByGuardian && r.FinishedAt < cutoff && r.RollbackData is not null))
        {
            var held = JsonSerializer.Deserialize<HeldFile>(r.RollbackData!)!;
            // Only ever delete inside our own holding root.
            if (!PathSafety.IsStrictlyWithin(holdingRoot, held.HeldPath, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())) continue;
            try
            {
                if (File.Exists(held.HeldPath)) File.Delete(held.HeldPath);
                TryRemoveEmptyDir(Path.GetDirectoryName(held.HeldPath)!);
                log.Update(r with
                {
                    Reversibility = Reversibility.Irreversible,
                    RollbackProcedure = $"Purged from the holding area on {_clock.GetUtcNow():u} after {Retention.TotalDays:0} days; it can no longer be restored.",
                });
                purged++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return purged;
    }

    private static bool HasSpaceFor(string dir, long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (root is null) return false;
            return new DriveInfo(root).AvailableFreeSpace > bytes + 100L * 1024 * 1024;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return false; }
    }

    private static void TryRemoveEmptyDir(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
