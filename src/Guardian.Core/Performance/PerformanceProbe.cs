using System.Diagnostics;
using Guardian.Core.Reporting;

namespace Guardian.Core.Performance;

public sealed record DiskUsage(string Name, string Format, long TotalBytes, long FreeBytes)
{
    public double FreePercent => TotalBytes == 0 ? 0 : 100.0 * FreeBytes / TotalBytes;
}

public sealed record ProcessUsage(int Pid, string Name, long WorkingSetBytes);

public sealed record MemoryUsage(long TotalBytes, long AvailableBytes);

public sealed record PerformanceSnapshot(
    DateTimeOffset At,
    MemoryUsage? Memory,
    IReadOnlyList<DiskUsage> Disks,
    TimeSpan Uptime,
    IReadOnlyList<ProcessUsage> TopProcessesByMemory,
    IReadOnlyList<CheckReport> Reports);

public interface IPerformanceProbe
{
    Task<PerformanceSnapshot> CaptureAsync(CancellationToken ct = default);
}

/// <summary>
/// Shared measurements that work the same way on every OS. Memory totals differ per OS and come
/// from the adapter. High-usage processes are reported, never stopped.
/// </summary>
public abstract class PerformanceProbeBase : IPerformanceProbe
{
    protected abstract Task<(MemoryUsage? Memory, CheckReport Report)> ReadMemoryAsync(CancellationToken ct);

    /// <summary>File systems that are virtual or not user storage and should not be reported.</summary>
    protected virtual bool IncludeDrive(DriveInfo d) => d.IsReady && d.TotalSize > 0 &&
        d.DriveType is DriveType.Fixed or DriveType.Removable;

    public async Task<PerformanceSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        var reports = new List<CheckReport>();
        var (memory, memReport) = await ReadMemoryAsync(ct);
        reports.Add(memReport);

        var disks = new List<DiskUsage>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (IncludeDrive(d)) disks.Add(new DiskUsage(d.Name, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        reports.Add(DiskReport(disks));

        var processes = new List<ProcessUsage>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { processes.Add(new ProcessUsage(p.Id, p.ProcessName, p.WorkingSet64)); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        var top = processes.OrderByDescending(p => p.WorkingSetBytes).Take(10).ToList();

        return new PerformanceSnapshot(DateTimeOffset.UtcNow, memory, disks,
            TimeSpan.FromMilliseconds(Environment.TickCount64), top, reports);
    }

    public static CheckReport DiskReport(IReadOnlyList<DiskUsage> disks)
    {
        const string id = "perf.disk", title = "Disk free space";
        if (disks.Count == 0) return CheckReport.NotAvailable(id, title, "No local drives could be read.");
        var low = disks.Where(d => d.FreePercent < 10).ToList();
        var evidence = disks.Select(d => new Evidence($"DriveInfo {d.Name}",
            $"{Bytes.Format(d.FreeBytes)} free of {Bytes.Format(d.TotalBytes)} ({d.FreePercent:0.#}%)")).ToList();
        return low.Count == 0
            ? CheckReport.Completed(id, title, Finding.NoIssueFound, "All local drives have at least 10% free space.", evidence)
            : CheckReport.Completed(id, title, Finding.Attention,
                $"Low free space on {string.Join(", ", low.Select(d => d.Name))}.", evidence,
                "Review large files yourself. Guardian does not delete files to free space.");
    }
}

public static class Bytes
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
