using System.Text.Json;
using Guardian.Core.Modes;
using Guardian.Core.Reporting;

namespace Guardian.App;

/// <summary>
/// "ai-laptop-guardian --report [--json]": runs the default read-only checks and a cleanup scan without a window.
/// Used by scheduled jobs and for testing on machines without a display. Never changes the system.
/// </summary>
public static class CliReport
{
    public static async Task<int> RunAsync(bool json, string? dataDir)
    {
        using var host = new GuardianHost(dataDir);
        var info = host.Adapter.Info;
        var reports = (await host.RunDefaultChecksAsync()).ToList();
        var perf = await host.Adapter.Performance.CaptureAsync();
        reports.AddRange(perf.Reports);
        var scan = host.Scanner.Scan();
        reports.AddRange(scan.Reports);
        var startup = await host.Adapter.Startup.ListAsync();
        reports.AddRange(startup.Reports);
        reports.Add(host.StartupWatch.Update(startup.Items));

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { platform = info, mode = host.Modes.Current.ToString(), reports },
                new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            return 0;
        }
        Console.WriteLine($"AI Laptop Guardian {typeof(CliReport).Assembly.GetName().Version?.ToString(3)}");
        Console.WriteLine($"{info.OsName} | {info.Architecture} | {(info.IsElevated ? "elevated" : "standard user")} | mode: {ModeDescriptions.Title(host.Modes.Current)}");
        Console.WriteLine(info.SupportNote);
        Console.WriteLine();
        foreach (var r in reports)
        {
            Console.WriteLine($"[{ReportLanguage.Describe(r.Support),-19}] {r.Title}");
            Console.WriteLine($"    ran: {(r.Ran ? "yes" : "no")}  result: {ReportLanguage.Describe(r.Finding)}  permission: {(r.PermissionGranted switch { null => "n/a", true => "granted", false => "not granted" })}  changes made: {(r.ChangesMade.Count == 0 ? "none" : string.Join(", ", r.ChangesMade))}");
            Console.WriteLine($"    {r.Summary}");
            foreach (var e in r.Evidence) Console.WriteLine($"      evidence: {e.Source}: {e.Detail}");
            if (r.FollowUp is not null) Console.WriteLine($"    follow-up: {r.FollowUp}");
        }
        Console.WriteLine();
        Console.WriteLine(ReportLanguage.Summarize(reports));
        return 0;
    }
}
