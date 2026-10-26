using Guardian.Core.Platform;

namespace Guardian.App;

public sealed record FeatureRow(string Feature, string Implemented, string OnThisOs, string Notes);

/// <summary>The honest feature list shown in the app. Keep in sync with docs/PLATFORM_STATUS.md.</summary>
public static class FeatureStatus
{
    public static IReadOnlyList<FeatureRow> For(PlatformInfo p)
    {
        var os = p.Family;
        string Only(params OsFamily[] fams) => fams.Contains(os) ? "Yes" : "Unsupported";
        return
        [
            new("Security status checks", "Yes", "Yes", "See the Security tab for each check's status, permission and evidence."),
            new("Performance readings", "Yes", "Yes", "Memory, disks, top processes by memory. Processes are never stopped."),
            new("Startup inventory", "Yes", "Yes", os == OsFamily.MacOS ? "Login Items not read (needs admin)." : "Read-only list."),
            new("Application inventory", "Yes", "Yes", "Read-only list. Guardian never uninstalls or modifies apps."),
            new("Cleanup candidates (scan)", "Yes", "Yes", "Per-user temp folder only. Nothing is deleted by scanning."),
            new("Move temp files to holding area", "Yes", "Yes", "Safe Maintenance only. Restorable for 7 days."),
            new("DISM / SFC verification", "Yes", Only(OsFamily.Windows), "Read-only, needs administrator; run on demand."),
            new("DISM / SFC repair proposals", "Partly", Only(OsFamily.Windows), "Plans and approval flow exist; not yet exposed in the UI."),
            new("macOS software update check", "Yes", Only(OsFamily.MacOS), "On demand; contacts Apple."),
            new("ClamAV status", "Yes", Only(OsFamily.Linux), "Optional; reported as Not available when not installed."),
            new("Run antivirus scans", "No", "Not implemented", "Planned for Safe Maintenance using the system's own scanner."),
            new("Scheduled scans", "Partly", "Not installed", $"{(p.Family switch { OsFamily.Windows => "Task Scheduler", OsFamily.MacOS => "launchd", _ => "systemd user timer" })} definitions are generated; installing them is manual."),
            new("Self-update", "No", "Not implemented", "Updates come from your installer/package manager."),
            new("AI analysis", "No", "Not implemented", "No AI features yet. AI suspicion alone will never trigger quarantine or deletion."),
        ];
    }
}
