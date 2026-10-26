namespace Guardian.Core.Platform;

/// <summary>
/// Finds system tools by absolute path. Commands are always run by absolute path so a writable
/// directory early in PATH cannot substitute a different program.
/// </summary>
public static class ToolLocator
{
    private static readonly string[] UnixSystemDirs = ["/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/libexec", "/usr/local/bin", "/usr/local/sbin"];

    /// <summary>Unix: searches fixed system directories only (not the user's PATH).</summary>
    public static string? FindUnix(string name, params string[] extraDirs)
    {
        foreach (var dir in extraDirs.Concat(UnixSystemDirs))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Windows: searches the System32 directory only.</summary>
    public static string? FindWindowsSystem(string relative)
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), relative);
        return File.Exists(p) ? p : null;
    }
}
