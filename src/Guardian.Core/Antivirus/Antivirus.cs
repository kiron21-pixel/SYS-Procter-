using Guardian.Core.Platform;
using Guardian.Core.Reporting;

namespace Guardian.Core.Antivirus;

public enum ScanKind { Quick, Full }

/// <summary>The scheduled scan Guardian registered with the OS scheduler, as read back from the scheduler itself.</summary>
public sealed record ScanScheduleState(bool Installed, TimeOnly? Time, ScanKind? Kind, DateTimeOffset? LastRun, string? LastResult, string Evidence);

/// <summary>One detection reported by the antivirus engine itself (never by Guardian's own guess).</summary>
public sealed record ThreatDetection(string Name, string Severity, DateTimeOffset? DetectedAt, string Status, IReadOnlyList<string> Resources);

/// <summary>
/// The OS's own antivirus engine (Microsoft Defender, ClamAV, ...). Guardian never scans or removes files itself:
/// it asks the engine to scan, and the engine applies its own remediation (for Defender: quarantine, restorable from
/// Windows Security > Protection history). Implementations run only fixed, documented commands.
/// </summary>
public interface IAntivirusEngine
{
    string EngineName { get; }
    /// <summary>The exact command lines the scheduled task would run, shown to the user before consent.</summary>
    string DescribeScanCommand(ScanKind kind);
    Task<ScanScheduleState> GetScheduleAsync(CancellationToken ct = default);
    Task<CommandResult> InstallScheduleAsync(TimeOnly time, ScanKind kind, CancellationToken ct = default);
    Task<CommandResult> RemoveScheduleAsync(CancellationToken ct = default);
    Task<CommandResult> RunScanAsync(ScanKind kind, CancellationToken ct = default);
    Task<(IReadOnlyList<ThreatDetection> Threats, CheckReport Report)> RecentThreatsAsync(TimeSpan window, CancellationToken ct = default);
}

public sealed record AntivirusActionResult(bool Done, string Message, Guid? OperationId);
