using System.Text.Json;
using Guardian.Core.Antivirus;

namespace Guardian.Platform.Windows;

public sealed record DefenderProtection(bool? RealTime, bool? Tamper, bool? Behavior, bool? Ioav, int? SignatureAgeDays, string? Mode, int? Maps, int? Pua);

public sealed record PendingUpdate(string Title, string? Severity);

/// <summary>Parsers for Defender, Task Scheduler and Windows Update JSON produced by the scripts in <see cref="DefenderEngine"/> and WindowsAdapter.</summary>
public static class DefenderParsers
{
    public static DefenderProtection? Protection(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new DefenderProtection(Bool(r, "RealTime"), Bool(r, "Tamper"), Bool(r, "Behavior"), Bool(r, "Ioav"),
            Int(r, "SignatureAgeDays"), Str(r, "Mode"), Int(r, "Maps"), Int(r, "Pua"));
    }

    /// <summary>Defender ThreatStatusID values (documented for MSFT_MpThreatDetection).</summary>
    public static string ThreatStatus(int id) => id switch
    {
        1 => "Detected", 2 => "Cleaned", 3 => "Quarantined", 4 => "Removed", 5 => "Allowed by user", 6 => "Blocked",
        102 => "Quarantine failed", 103 => "Removal failed", 104 => "Allow failed", 105 => "Abandoned", 107 => "Block failed",
        _ => "Unknown",
    };

    /// <summary>Statuses that mean the threat may still be active.</summary>
    public static bool IsUnresolved(int id) => id is 0 or 1 or 102 or 103 or 105 or 107;

    public static string Severity(int id) => id switch { 1 => "Low", 2 => "Moderate", 4 => "High", 5 => "Severe", _ => "Unknown" };

    public static IReadOnlyList<(ThreatDetection Detection, bool Unresolved)> Threats(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        return items.Where(e => e.ValueKind == JsonValueKind.Object).Select(e =>
        {
            var status = Int(e, "StatusId") ?? 0;
            var resources = e.TryGetProperty("Resources", out var res) && res.ValueKind == JsonValueKind.Array
                ? res.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
            DateTimeOffset? at = DateTimeOffset.TryParse(Str(e, "Detected"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var d) ? d : null;
            return (new ThreatDetection(Str(e, "Name") ?? "Unknown threat", Severity(Int(e, "Severity") ?? 0), at, ThreatStatus(status), resources), IsUnresolved(status));
        }).ToList();
    }

    /// <summary>Parses the scheduled-task query. "{}" means the task does not exist.</summary>
    public static ScanScheduleState Schedule(string json, string evidence)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(false, null, null, null, null, evidence);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.TryGetProperty("Start", out _)) return new(false, null, null, null, null, evidence);
        TimeOnly? time = DateTime.TryParse(Str(r, "Start"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var s) ? TimeOnly.FromDateTime(s) : null;
        var args = Str(r, "Args") ?? "";
        ScanKind? kind = args.Contains("-ScanType 2") ? ScanKind.Full : args.Contains("-ScanType 1") ? ScanKind.Quick : null;
        DateTimeOffset? last = DateTimeOffset.TryParse(Str(r, "LastRun"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var l) && l.Year > 2000 ? l : null;
        var code = Int(r, "LastResult");
        var result = last is null ? "Not run yet" : code switch
        {
            0 => "Completed, no threats found",
            2 => "Completed, threats found (Defender handled them; see recent threats)",
            267009 => "Running now",
            null => "Unknown",
            _ => $"Did not complete (code 0x{code:X})",
        };
        DateTimeOffset? next = DateTimeOffset.TryParse(Str(r, "NextRun"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var n) && n.Year > 2000 ? n : null;
        return new(true, time, kind, last, result, evidence) { NextRun = next, MissedRuns = Int(r, "Missed"), State = Str(r, "State") };
    }

    public static IReadOnlyList<PendingUpdate> PendingUpdates(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        return items.Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => new PendingUpdate(Str(e, "Title") ?? "?", Str(e, "MsrcSeverity"))).ToList();
    }

    private static bool? Bool(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static int? Int(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static string? Str(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
