using System.Text.Json;
using Guardian.Core.Inventory;

namespace Guardian.Platform.Linux;

/// <summary>Pure parsers for Linux tool output, kept separate so they can be tested on any OS.</summary>
public static class LinuxParsers
{
    /// <summary>Parses /proc/meminfo. Returns (MemTotal, MemAvailable) in bytes.</summary>
    public static (long Total, long Available)? MemInfo(string content)
    {
        long? total = null, avail = null;
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var kb)) continue;
            if (parts[0] == "MemTotal:") total = kb * 1024;
            else if (parts[0] == "MemAvailable:") avail = kb * 1024;
        }
        return total is { } t && avail is { } a ? (t, a) : null;
    }

    /// <summary>Counts upgradable packages from "apt-get -s upgrade" (simulation; changes nothing).</summary>
    public static (int Total, int Security) AptSimulatedUpgrades(string output)
    {
        int total = 0, security = 0;
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith("Inst ", StringComparison.Ordinal)) continue;
            total++;
            if (line.Contains("-security", StringComparison.Ordinal)) security++;
        }
        return (total, security);
    }

    /// <summary>Counts package lines in "dnf check-update" output (exit code 100 means updates exist).</summary>
    public static int DnfCheckUpdateCount(string output)
    {
        var count = 0;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0].Contains('.') && !line.StartsWith("Last metadata", StringComparison.Ordinal)
                && !line.StartsWith("Obsoleting", StringComparison.Ordinal)) count++;
            if (line.StartsWith("Obsoleting", StringComparison.Ordinal)) break;
        }
        return count;
    }

    /// <summary>Parses "clamscan --version", e.g. "ClamAV 1.0.7/27440/Sat Oct 19 08:35:01 2024".</summary>
    public static (string Engine, DateTimeOffset? SignaturesDate)? ClamVersion(string output)
    {
        var line = output.Trim();
        if (!line.StartsWith("ClamAV ", StringComparison.Ordinal)) return null;
        var parts = line[7..].Split('/');
        DateTimeOffset? date = null;
        if (parts.Length >= 3 && DateTime.TryParseExact(
                System.Text.RegularExpressions.Regex.Replace(parts[2].Trim(), @"\s+", " "),
                "ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            date = dt;
        return (parts[0], date);
    }

    /// <summary>
    /// From "lsblk -J -o NAME,TYPE,MOUNTPOINT", returns whether "/" sits on (or below) a dm-crypt device,
    /// and whether any crypt device exists.
    /// </summary>
    public static (bool RootEncrypted, bool AnyCrypt, bool RootFound) LsblkEncryption(string json)
    {
        using var doc = JsonDocument.Parse(json);
        bool rootEnc = false, any = false, rootFound = false;
        void Walk(JsonElement dev, bool underCrypt)
        {
            var type = dev.TryGetProperty("type", out var t) ? t.GetString() : null;
            var crypt = underCrypt || type == "crypt";
            if (type == "crypt") any = true;
            if (dev.TryGetProperty("mountpoint", out var mp) && mp.ValueKind == JsonValueKind.String && mp.GetString() == "/")
            { rootFound = true; rootEnc |= crypt; }
            if (dev.TryGetProperty("mountpoints", out var mps) && mps.ValueKind == JsonValueKind.Array &&
                mps.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "/"))
            { rootFound = true; rootEnc |= crypt; }
            if (dev.TryGetProperty("children", out var ch))
                foreach (var c in ch.EnumerateArray()) Walk(c, crypt);
        }
        if (doc.RootElement.TryGetProperty("blockdevices", out var devs))
            foreach (var d in devs.EnumerateArray()) Walk(d, false);
        return (rootEnc, any, rootFound);
    }

    /// <summary>Parses an XDG autostart .desktop file. Returns null if it is hidden or disabled.</summary>
    public static StartupItem? DesktopAutostart(string content, string path, ItemScope scope)
    {
        string? name = null, exec = null;
        var hidden = false;
        var inEntry = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inEntry = line == "[Desktop Entry]"; continue; }
            if (!inEntry) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var (k, v) = (line[..eq].Trim(), line[(eq + 1)..].Trim());
            switch (k)
            {
                case "Name": name ??= v; break;
                case "Exec": exec = v; break;
                case "Hidden" when v.Equals("true", StringComparison.OrdinalIgnoreCase): hidden = true; break;
                case "X-GNOME-Autostart-enabled" when v.Equals("false", StringComparison.OrdinalIgnoreCase): hidden = true; break;
            }
        }
        if (hidden) return null;
        return new StartupItem(name ?? Path.GetFileNameWithoutExtension(path), exec ?? "", path, scope, "XDG autostart");
    }

    /// <summary>Parses "systemctl --user list-unit-files --state=enabled --no-legend --plain".</summary>
    public static IReadOnlyList<string> EnabledUnits(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2 && p[1] == "enabled")
            .Select(p => p[0]).ToList();

    /// <summary>Parses tab-separated "name\tversion\tpublisher" lines from dpkg-query/rpm.</summary>
    public static IReadOnlyList<InstalledApplication> TabSeparatedPackages(string output, string source) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 1 && p[0].Length > 0)
            .Select(p => new InstalledApplication(p[0], p.ElementAtOrDefault(1), NullIfEmpty(p.ElementAtOrDefault(2)), source))
            .ToList();

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) || s == "(none)" ? null : s;
}
