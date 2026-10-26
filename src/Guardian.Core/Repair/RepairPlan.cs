using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Guardian.Core.Platform;
using Guardian.Core.Reporting;

namespace Guardian.Core.Repair;

public enum BackupKind { None, ConfigurationSnapshot, SystemRestorePoint, TimeMachineSnapshot, FilesystemSnapshot, ManualUserBackup }

/// <summary>
/// A Controlled Repair proposal. Every field maps to one of the ten required steps; a plan missing any of
/// them is rejected by <see cref="RepairPlanValidator"/> before the user is even asked.
/// </summary>
public sealed record RepairPlan
{
    public required string Id { get; init; }
    public required OsFamily TargetOs { get; init; }
    /// <summary>1. The detected issue.</summary>
    public required string Issue { get; init; }
    /// <summary>1. The evidence for it.</summary>
    public required IReadOnlyList<Evidence> Evidence { get; init; }
    /// <summary>2. Exact files, settings, services, packages or applications affected.</summary>
    public required IReadOnlyList<string> AffectedComponents { get; init; }
    /// <summary>2. Who owns them: "operating system", "third-party: name", or "Guardian".</summary>
    public required string ComponentOwner { get; init; }
    /// <summary>3. Potential impact.</summary>
    public required string Impact { get; init; }
    /// <summary>3. Whether a restart is needed.</summary>
    public required bool RestartRequired { get; init; }
    /// <summary>4. Verified supported on this OS (filled in by the adapter, not assumed).</summary>
    public required bool SupportedOnThisOs { get; init; }
    /// <summary>5. Backup or recovery safeguard taken before running.</summary>
    public required BackupKind Backup { get; init; }
    public required string BackupDescription { get; init; }
    /// <summary>6. Result of the dependency check: null = not checked (blocks execution).</summary>
    public required bool? DependentsChecked { get; init; }
    public required IReadOnlyList<string> KnownDependents { get; init; }
    /// <summary>8. The exact command that will run. Nothing else will be run.</summary>
    public required string Command { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required bool NeedsElevation { get; init; }
    /// <summary>9. The read-only command that verifies the result afterwards.</summary>
    public required string VerifyCommand { get; init; }
    public required IReadOnlyList<string> VerifyArguments { get; init; }
    /// <summary>10. Recovery or rollback procedure, and whether one actually exists.</summary>
    public required bool RollbackAvailable { get; init; }
    public required string RollbackProcedure { get; init; }
    public required long RequiredFreeBytes { get; init; }

    /// <summary>A hash of the exact content. An approval is only valid for the plan it was given for.</summary>
    public string Fingerprint()
    {
        var json = JsonSerializer.Serialize(this);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

/// <summary>7. The user's approval of one exact plan.</summary>
public sealed record RepairApproval(string PlanId, string PlanFingerprint, string ApprovedBy, DateTimeOffset ApprovedAt);

public static class RepairPlanValidator
{
    public static IReadOnlyList<string> Problems(RepairPlan p)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Issue)) problems.Add("No issue described.");
        if (p.Evidence.Count == 0) problems.Add("No evidence collected.");
        if (p.AffectedComponents.Count == 0) problems.Add("Affected components not identified.");
        if (string.IsNullOrWhiteSpace(p.ComponentOwner)) problems.Add("Component ownership unknown.");
        if (string.IsNullOrWhiteSpace(p.Impact)) problems.Add("Impact not explained.");
        if (!p.SupportedOnThisOs) problems.Add("Operation is not verified as supported on this operating system.");
        if (string.IsNullOrWhiteSpace(p.BackupDescription)) problems.Add("Backup or recovery safeguard not described.");
        if (p.DependentsChecked != true) problems.Add("Dependency check has not been completed.");
        if (string.IsNullOrWhiteSpace(p.Command)) problems.Add("No exact command.");
        if (string.IsNullOrWhiteSpace(p.VerifyCommand)) problems.Add("No verification step.");
        if (string.IsNullOrWhiteSpace(p.RollbackProcedure)) problems.Add("Rollback/recovery procedure not explained (say plainly if none exists).");
        return problems;
    }
}
