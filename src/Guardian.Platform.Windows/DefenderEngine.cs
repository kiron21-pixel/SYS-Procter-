using System.Runtime.Versioning;
using System.Text;
using Guardian.Core.Antivirus;
using Guardian.Core.Platform;
using Guardian.Core.Reporting;

namespace Guardian.Platform.Windows;

/// <summary>
/// Drives Microsoft Defender through its documented tools. Scans use MpCmdRun.exe; Defender applies its own
/// remediation (quarantine by default, restorable in Windows Security > Protection history). The daily scan is a
/// Task Scheduler task under "\AI Laptop Guardian\" that runs as the signed-in user, never as SYSTEM.
/// Guardian never changes Defender's own settings, exclusions or scan schedule.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DefenderEngine(ICommandRunner runner, ReadOnlyCommandRunner readOnly, string dataDirectory) : IAntivirusEngine
{
    public const string TaskFolder = @"AI Laptop Guardian";
    public const string TaskName = @"Daily Defender Scan";
    private static string FullTaskName => TaskFolder + "\\" + TaskName;

    public static readonly string MpCmdRun = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    internal static readonly ReadOnlyCommand QuerySchedule = PowerShell(
        "$t = Get-ScheduledTask -TaskPath '\\AI Laptop Guardian\\' -TaskName 'Daily Defender Scan' -ErrorAction SilentlyContinue; " +
        "if (-not $t) { '{}' } else { $i = $t | Get-ScheduledTaskInfo; " +
        "[pscustomobject]@{ Start = $t.Triggers[0].StartBoundary; Args = $t.Actions[0].Arguments; LastRun = $i.LastRunTime.ToString('o'); LastResult = $i.LastTaskResult; NextRun = $i.NextRunTime.ToString('o'); Missed = $i.NumberOfMissedRuns; State = [string]$t.State } | ConvertTo-Json -Compress }");

    internal static readonly ReadOnlyCommand QueryThreats = PowerShell(
        "$since = (Get-Date).AddDays(-30); $names = @{}; " +
        "Get-MpThreat -ErrorAction SilentlyContinue | ForEach-Object { $names[[string]$_.ThreatID] = @($_.ThreatName, [int]$_.SeverityID) }; " +
        "$d = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -gt $since } | ForEach-Object { " +
        "$n = $names[[string]$_.ThreatID]; [pscustomobject]@{ Name = $(if ($n) { $n[0] } else { 'Unknown' }); Severity = $(if ($n) { $n[1] } else { 0 }); " +
        "Detected = $_.InitialDetectionTime.ToString('o'); StatusId = [int]$_.ThreatStatusID; Resources = @($_.Resources) } }); " +
        "ConvertTo-Json -InputObject $d -Compress -Depth 3");

    internal static IEnumerable<ReadOnlyCommand> ReadOnlyCommands => [QuerySchedule, QueryThreats];

    public string EngineName => "Microsoft Defender";

    public string DescribeScanCommand(ScanKind kind) => $"\"{MpCmdRun}\" {string.Join(' ', ScanArgs(kind))}";

    private static string[] ScanArgs(ScanKind kind) => DefenderTaskXml.ScanArgs(kind);

    public async Task<ScanScheduleState> GetScheduleAsync(CancellationToken ct = default)
    {
        var r = await readOnly.RunAsync(QuerySchedule, ct: ct);
        if (!r.Succeeded) return new(false, null, null, null, null, "Could not read Task Scheduler: " + r.StdErr.Trim());
        return DefenderParsers.Schedule(r.StdOut.Trim(), $"Task Scheduler: \\{FullTaskName}");
    }

    public async Task<CommandResult> InstallScheduleAsync(TimeOnly time, ScanKind kind, CancellationToken ct = default)
    {
        if (!File.Exists(MpCmdRun)) return CommandResult.NotStarted($"Microsoft Defender's command-line tool was not found at {MpCmdRun}.");
        var xmlPath = Path.Combine(dataDirectory, "defender-scan-task.xml");
        Directory.CreateDirectory(dataDirectory);
        // schtasks /XML requires UTF-16.
        await File.WriteAllTextAsync(xmlPath, DefenderTaskXml.Render(MpCmdRun, time, kind), Encoding.Unicode, ct);
        try
        {
            return await runner.RunAsync(Schtasks, ["/Create", "/TN", FullTaskName, "/XML", xmlPath, "/F"], TimeSpan.FromSeconds(30), ct);
        }
        finally { try { File.Delete(xmlPath); } catch (IOException) { } }
    }

    public Task<CommandResult> RemoveScheduleAsync(CancellationToken ct = default) =>
        runner.RunAsync(Schtasks, ["/Delete", "/TN", FullTaskName, "/F"], TimeSpan.FromSeconds(30), ct);

    public async Task<CommandResult> RunScanAsync(ScanKind kind, CancellationToken ct = default)
    {
        if (!File.Exists(MpCmdRun)) return CommandResult.NotStarted($"Microsoft Defender's command-line tool was not found at {MpCmdRun}.");
        var r = await runner.RunAsync(MpCmdRun, ScanArgs(kind), kind == ScanKind.Full ? TimeSpan.FromHours(6) : TimeSpan.FromHours(1), ct);
        // MpCmdRun exits 2 when it found (and acted on) threats: the scan itself completed.
        return r.Started && !r.TimedOut && r.ExitCode == 2
            ? r with { ExitCode = 0, StdOut = "Threats were found; Defender acted on them. " + r.StdOut }
            : r;
    }

    public async Task<(IReadOnlyList<ThreatDetection> Threats, CheckReport Report)> RecentThreatsAsync(TimeSpan window, CancellationToken ct = default)
    {
        const string id = "win.defender-threats", title = "Threats Defender found (last 30 days)";
        var r = await readOnly.RunAsync(QueryThreats, ct: ct);
        if (!r.Succeeded) return ([], CheckReport.NotAvailable(id, title, "Defender's threat history could not be read (Defender may not be the active antivirus).", new Evidence("Get-MpThreatDetection", r.StdErr.Trim())));
        var parsed = DefenderParsers.Threats(r.StdOut.Trim());
        var threats = parsed.Select(p => p.Detection).ToList();
        var unresolved = parsed.Where(p => p.Unresolved).Select(p => p.Detection).ToList();
        var ev = threats.Select(t => new Evidence($"{t.DetectedAt?.ToLocalTime():yyyy-MM-dd HH:mm} · {t.Severity}", $"{t.Name}: {t.Status} {string.Join(", ", t.Resources.Take(3))}")).ToList();
        var report = threats.Count == 0
            ? CheckReport.Completed(id, title, Finding.NoIssueFound, "Defender recorded no detections in the last 30 days.", [new("Get-MpThreatDetection", "0 detections")])
            : unresolved.Count == 0
                ? CheckReport.Completed(id, title, Finding.Attention, $"Defender found {threats.Count} threat(s) and dealt with all of them.", ev,
                    "Review them in Windows Security > Virus & threat protection > Protection history.")
                : CheckReport.Completed(id, title, Finding.Problem, $"{unresolved.Count} threat(s) may still be active (Defender could not finish removing them).", ev,
                    "Open Windows Security > Protection history and follow Defender's recommended action, then run a full scan.");
        return (threats, report);
    }

    private static ReadOnlyCommand PowerShell(string script) =>
        new(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command", script]);
}

/// <summary>Pure Task Scheduler XML for the daily Defender scan (kept OS-neutral so it can be tested anywhere).</summary>
public static class DefenderTaskXml
{
    public static string[] ScanArgs(ScanKind kind) => ["-Scan", "-ScanType", kind == ScanKind.Full ? "2" : "1"];

    /// <summary>Task Scheduler definition: daily at the given time, runs as the signed-in user, catches up if the laptop was off.</summary>
    public static string Render(string mpCmdRun, TimeOnly time, ScanKind kind)
    {
        string X(string s) => System.Security.SecurityElement.Escape(s)!;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>AI Laptop Guardian</Author>
                <Description>Asks Microsoft Defender to run a daily {kind.ToString().ToLowerInvariant()} scan. Defender handles anything it finds. Created with your consent; remove it from Guardian's Protection tab.</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>2026-01-01T{time:HH\:mm}:00</StartBoundary>
                  <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <ExecutionTimeLimit>PT6H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{X(mpCmdRun)}</Command><Arguments>{X(string.Join(' ', ScanArgs(kind)))}</Arguments></Exec>
              </Actions>
            </Task>
            """;
    }
}
