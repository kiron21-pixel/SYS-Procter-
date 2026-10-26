namespace Guardian.Platform.Linux;

/// <summary>Parser for /etc/os-release (freedesktop.org format).</summary>
public static class OsRelease
{
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            d[line[..eq]] = value;
        }
        return d;
    }

    /// <summary>
    /// Chooses the package manager from the distribution identity. Only apt (Debian family) and dnf (Fedora)
    /// are supported; anything else returns null so that no package-manager command is ever run on the wrong distro.
    /// </summary>
    public static string? PackageManagerFor(IReadOnlyDictionary<string, string> osRelease)
    {
        var id = osRelease.GetValueOrDefault("ID");
        var like = osRelease.GetValueOrDefault("ID_LIKE")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (id is "debian" or "ubuntu" || like.Contains("debian") || like.Contains("ubuntu")) return "apt";
        if (id == "fedora" || like.Contains("fedora")) return "dnf";
        return null;
    }
}
