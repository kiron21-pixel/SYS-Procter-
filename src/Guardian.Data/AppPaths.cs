namespace Guardian.Data;

/// <summary>Where Guardian keeps its own files. Guardian writes nowhere else.</summary>
public static class AppPaths
{
    public const string FolderName = "AI Laptop Guardian";

    /// <summary>
    /// Windows: %LOCALAPPDATA%\AI Laptop Guardian. macOS: ~/Library/Application Support/AI Laptop Guardian.
    /// Linux: $XDG_DATA_HOME/ai-laptop-guardian (default ~/.local/share/ai-laptop-guardian).
    /// </summary>
    public static string DataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", FolderName);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "ai-laptop-guardian");
    }

    public static string DatabasePath(string dataDir) => Path.Combine(dataDir, "guardian.db");
    public static string HoldingArea(string dataDir) => Path.Combine(dataDir, "holding");
}
