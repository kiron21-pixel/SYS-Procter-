using System.Text.Json;

namespace Guardian.Platform.Windows;

public sealed record DefenderStatus(bool? ServiceEnabled, bool? AntivirusEnabled, bool? RealTimeEnabled, DateTimeOffset? SignaturesUpdated, DateTimeOffset? LastQuickScan);

public sealed record AvProduct(string Name, bool Enabled, bool UpToDate);

/// <summary>Pure parsers for Windows tool output, testable on any OS.</summary>
public static class WindowsParsers
{
    /// <summary>Parses Get-MpComputerStatus | ConvertTo-Json output.</summary>
    public static DefenderStatus? Defender(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        bool? B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        return new DefenderStatus(B("AMServiceEnabled"), B("AntivirusEnabled"), B("RealTimeProtectionEnabled"),
            Date(r, "AntivirusSignatureLastUpdated"), Date(r, "QuickScanEndTime"));
    }

    /// <summary>
    /// Parses SecurityCenter2 AntiVirusProduct JSON (object or array). productState is undocumented by Microsoft;
    /// the widely used decoding (byte 2: 0x10 = on; byte 1: 0x00 = signatures up to date) is treated as best-effort.
    /// </summary>
    public static IReadOnlyList<AvProduct> AntivirusProducts(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        return items.Select(e =>
        {
            var name = e.TryGetProperty("displayName", out var n) ? n.GetString() ?? "?" : "?";
            var state = e.TryGetProperty("productState", out var s) && s.TryGetInt32(out var i) ? i : 0;
            var on = ((state >> 8) & 0xF0) == 0x10;
            var current = (state & 0xF0) == 0x00;
            return new AvProduct(name, on, current);
        }).ToList();
    }

    /// <summary>Parses Get-NetFirewallProfile | Select Name,Enabled | ConvertTo-Json.</summary>
    public static IReadOnlyList<(string Profile, bool Enabled)> FirewallProfiles(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        return items.Select(e =>
        {
            var name = e.GetProperty("Name").GetString() ?? "?";
            var en = e.GetProperty("Enabled");
            // Enabled is a GpoBoolean: serialised as 1/0 or true/false depending on PowerShell version.
            var enabled = en.ValueKind switch { JsonValueKind.True => true, JsonValueKind.Number => en.GetInt32() == 1, _ => false };
            return (name, enabled);
        }).ToList();
    }

    /// <summary>DISM /CheckHealth result.</summary>
    public static string? DismHealth(string output)
    {
        if (output.Contains("No component store corruption detected", StringComparison.OrdinalIgnoreCase)) return "healthy";
        if (output.Contains("component store is repairable", StringComparison.OrdinalIgnoreCase)) return "repairable";
        if (output.Contains("not repairable", StringComparison.OrdinalIgnoreCase)) return "not-repairable";
        return null;
    }

    /// <summary>sfc /verifyonly writes UTF-16; strip NULs before matching.</summary>
    public static string? SfcVerify(string output)
    {
        var text = output.Replace("\0", "");
        if (text.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase)) return "clean";
        if (text.Contains("found integrity violations", StringComparison.OrdinalIgnoreCase)) return "violations";
        if (text.Contains("must be an administrator", StringComparison.OrdinalIgnoreCase)) return "needs-admin";
        return null;
    }

    private static DateTimeOffset? Date(JsonElement r, string key)
    {
        if (!r.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()!;
            // Windows PowerShell 5.1 serialises dates as "/Date(1700000000000)/".
            var m = System.Text.RegularExpressions.Regex.Match(s, @"/Date\((-?\d+)\)/");
            if (m.Success) return DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(m.Groups[1].Value));
            if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) return d;
        }
        return null;
    }
}
