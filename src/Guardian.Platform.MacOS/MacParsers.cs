using System.Text.Json;
using Guardian.Core.Inventory;

namespace Guardian.Platform.MacOS;

/// <summary>Pure parsers for macOS tool output, testable on any OS.</summary>
public static class MacParsers
{
    /// <summary>"spctl --status" prints "assessments enabled" or "assessments disabled".</summary>
    public static bool? GatekeeperEnabled(string output) =>
        output.Contains("assessments enabled", StringComparison.OrdinalIgnoreCase) ? true :
        output.Contains("assessments disabled", StringComparison.OrdinalIgnoreCase) ? false : null;

    /// <summary>"csrutil status": "System Integrity Protection status: enabled." (may list custom configuration).</summary>
    public static bool? SipEnabled(string output)
    {
        var first = output.Split('\n').FirstOrDefault() ?? "";
        if (first.Contains("status: enabled", StringComparison.OrdinalIgnoreCase)) return true;
        if (first.Contains("status: disabled", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>"socketfilterfw --getglobalstate": "Firewall is enabled. (State = 1)" / "Firewall is disabled. (State = 0)".</summary>
    public static bool? FirewallEnabled(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"State\s*=\s*(\d)");
        if (m.Success) return m.Groups[1].Value != "0";
        if (output.Contains("is enabled", StringComparison.OrdinalIgnoreCase)) return true;
        if (output.Contains("is disabled", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>"fdesetup status": "FileVault is On." / "FileVault is Off."</summary>
    public static bool? FileVaultOn(string output) =>
        output.Contains("FileVault is On", StringComparison.Ordinal) ? true :
        output.Contains("FileVault is Off", StringComparison.Ordinal) ? false : null;

    /// <summary>Parses vm_stat; returns approximate available bytes (free + inactive + speculative pages).</summary>
    public static long? VmStatAvailable(string output)
    {
        var pageSize = 4096L;
        var m = System.Text.RegularExpressions.Regex.Match(output, @"page size of (\d+) bytes");
        if (m.Success) pageSize = long.Parse(m.Groups[1].Value);
        long Pages(string key)
        {
            var mm = System.Text.RegularExpressions.Regex.Match(output, @"^" + System.Text.RegularExpressions.Regex.Escape(key) + @":\s+(\d+)\.", System.Text.RegularExpressions.RegexOptions.Multiline);
            return mm.Success ? long.Parse(mm.Groups[1].Value) : -1;
        }
        var free = Pages("Pages free");
        var inactive = Pages("Pages inactive");
        var spec = Pages("Pages speculative");
        if (free < 0 || inactive < 0) return null;
        return (free + inactive + Math.Max(spec, 0)) * pageSize;
    }

    /// <summary>"softwareupdate --list": counts lines starting with "* Label:" and whether any needs a restart.</summary>
    public static (int Count, bool RestartRequired, bool NoneAvailable) SoftwareUpdates(string output)
    {
        var count = output.Split('\n').Count(l => l.TrimStart().StartsWith("* Label:", StringComparison.Ordinal));
        return (count, output.Contains("Action: restart", StringComparison.OrdinalIgnoreCase),
            output.Contains("No new software available", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses "system_profiler SPApplicationsDataType -json".</summary>
    public static IReadOnlyList<InstalledApplication> Applications(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("SPApplicationsDataType", out var arr)) return [];
        return arr.EnumerateArray().Select(a => new InstalledApplication(
            a.TryGetProperty("_name", out var n) ? n.GetString() ?? "?" : "?",
            a.TryGetProperty("version", out var v) ? v.GetString() : null,
            a.TryGetProperty("signed_by", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0 ? s[0].GetString() : null,
            a.TryGetProperty("obtained_from", out var o) ? "macOS (" + o.GetString() + ")" : "macOS")).ToList();
    }
}
