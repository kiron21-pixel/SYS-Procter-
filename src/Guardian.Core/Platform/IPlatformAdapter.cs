using Guardian.Core.Antivirus;
using Guardian.Core.Cleanup;
using Guardian.Core.Inventory;
using Guardian.Core.Performance;
using Guardian.Core.Reporting;
using Guardian.Core.Scheduling;

namespace Guardian.Core.Platform;

/// <summary>A read-only check. Implementations must not change the system.</summary>
public interface ISecurityCheck
{
    string Id { get; }
    string Title { get; }
    /// <summary>On-demand checks are slow or need elevation; they are not part of the default scan.</summary>
    bool OnDemandOnly => false;
    Task<CheckReport> RunAsync(CancellationToken ct = default);
}

/// <summary>
/// Everything that differs between operating systems lives behind this interface.
/// A missing capability returns an honest report instead of throwing, so one OS gap never breaks the app.
/// </summary>
public interface IPlatformAdapter
{
    PlatformInfo Info { get; }
    IReadOnlyList<ISecurityCheck> SecurityChecks { get; }
    IPerformanceProbe Performance { get; }
    IStartupInventory Startup { get; }
    IApplicationInventory Applications { get; }
    ICleanupPlatform Cleanup { get; }
    INativeScheduler Scheduler { get; }
    /// <summary>The OS antivirus engine Guardian can ask to scan, or null when none is supported on this OS.</summary>
    IAntivirusEngine? Antivirus => null;
}
