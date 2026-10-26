using Guardian.Core.Antivirus;
using Guardian.Core.Cleanup;
using Guardian.Core.Inventory;
using Guardian.Core.Modes;
using Guardian.Core.Platform;
using Guardian.Core.Reporting;
using Guardian.Data;
using Guardian.Platform.Linux;
using Guardian.Platform.MacOS;
using Guardian.Platform.Windows;

namespace Guardian.App;

/// <summary>Wires the platform adapter, the database and the safety components together.</summary>
public sealed class GuardianHost : IDisposable
{
    public GuardianHost(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? AppPaths.DataDirectory();
        Directory.CreateDirectory(DataDirectory);
        Database = new GuardianDatabase(AppPaths.DatabasePath(DataDirectory));
        Adapter = CreateAdapter(DataDirectory);
        Modes = new ModeManager(Database);
        Gate = new OperationGate(Modes);
        CleanupPolicy = new CleanupPolicy();
        Scanner = new CleanupScanner(Adapter.Cleanup, CleanupPolicy);
        Cleaner = new HoldingAreaCleaner(AppPaths.HoldingArea(DataDirectory), Scanner, Gate, Modes, Database);
        Antivirus = Adapter.Antivirus is { } engine ? new AntivirusCoordinator(engine, Gate, Modes, Database) : null;
        StartupWatch = new StartupWatch(Database);
    }

    public string DataDirectory { get; }
    public GuardianDatabase Database { get; }
    public IPlatformAdapter Adapter { get; }
    public ModeManager Modes { get; }
    public OperationGate Gate { get; }
    public CleanupPolicy CleanupPolicy { get; }
    public CleanupScanner Scanner { get; }
    public HoldingAreaCleaner Cleaner { get; }
    /// <summary>Null when Guardian cannot drive an antivirus engine on this OS.</summary>
    public AntivirusCoordinator? Antivirus { get; }
    public StartupWatch StartupWatch { get; }

    public static IPlatformAdapter CreateAdapter(string dataDirectory)
    {
        if (OperatingSystem.IsWindows()) return new WindowsAdapter(dataDirectory: dataDirectory);
        if (OperatingSystem.IsMacOS()) return new MacAdapter();
        if (OperatingSystem.IsLinux()) return new LinuxAdapter();
        throw new PlatformNotSupportedException("AI Laptop Guardian runs on Windows, macOS and Linux only.");
    }

    /// <summary>Runs every default (not on-demand) check. Read-only in every mode.</summary>
    public async Task<IReadOnlyList<CheckReport>> RunDefaultChecksAsync(CancellationToken ct = default)
    {
        var reports = new List<CheckReport>();
        foreach (var c in Adapter.SecurityChecks.Where(c => !c.OnDemandOnly))
            reports.Add(await c.RunAsync(ct));
        Database.SaveReports(reports);
        return reports;
    }

    public void Dispose() => Database.Dispose();
}
