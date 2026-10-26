namespace Guardian.Core.Cleanup;

/// <summary>Path validation used before every file operation.</summary>
public static class PathSafety
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/> (never the root itself).</summary>
    public static bool IsStrictlyWithin(string root, string path, bool caseInsensitive)
    {
        var r = Normalize(root);
        var p = Normalize(path);
        var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(r, p, cmp)) return false;
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, cmp);
    }

    /// <summary>
    /// True when the file itself or any directory between it and the root is a symbolic link, junction or
    /// other reparse point. Following links is how a temp-file cleaner ends up deleting the wrong thing.
    /// </summary>
    public static bool HasLinkInChain(string root, string path, bool caseInsensitive)
    {
        var r = Normalize(root);
        var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = Normalize(path);
        while (!string.Equals(current, r, cmp))
        {
            FileSystemInfo info = File.Exists(current) ? new FileInfo(current) : new DirectoryInfo(current);
            if (!info.Exists) return true; // vanished or unreadable: treat as unsafe
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Length >= current.Length) return true;
            current = parent;
        }
        return false;
    }

    /// <summary>Simple glob for file names: '*' matches any run of characters, '?' one character.</summary>
    public static bool NameMatches(string name, string pattern, bool caseInsensitive = true) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: caseInsensitive);
}
