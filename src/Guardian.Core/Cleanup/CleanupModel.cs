namespace Guardian.Core.Cleanup;

/// <summary>A directory Guardian may look in for disposable files. Only per-user temp locations are used.</summary>
public sealed record CleanupRoot(string Path, string Description);

/// <summary>OS-specific facts the cleanup safeguards depend on.</summary>
public interface ICleanupPlatform
{
    /// <summary>Per-user temporary directories only. Never system directories, caches of other apps, or downloads.</summary>
    IReadOnlyList<CleanupRoot> GetRoots();
    /// <summary>true/false when known; null when ownership cannot be determined (treated as "not eligible").</summary>
    bool? IsOwnedByCurrentUser(string path);
    /// <summary>true/false when known; null when it cannot be determined (treated as "not eligible").</summary>
    bool? IsInUse(string path);
    /// <summary>Paths compare case-insensitively on Windows and (by default) macOS.</summary>
    bool PathsAreCaseInsensitive { get; }
}

public sealed record CleanupPolicy
{
    /// <summary>A file must not have been written for at least this long.</summary>
    public TimeSpan MinimumAge { get; init; } = TimeSpan.FromDays(7);
    public int MaxDepth { get; init; } = 6;
    public int MaxFilesExamined { get; init; } = 20_000;
    /// <summary>
    /// File-name patterns that are never candidates. Lock files, sockets, pid files and anything that looks
    /// like a database or a document are excluded even inside temp folders, because "temporary" does not mean "unneeded".
    /// </summary>
    public IReadOnlyList<string> ExcludedNamePatterns { get; init; } =
    [
        "*.lock", "*.lck", "*.pid", "*.sock", "*.socket", ".X*-lock",
        "*.db", "*.db-*", "*.sqlite", "*.sqlite3", "*.sqlite-*", "*.mdb", "*.ldb",
        "*.doc", "*.docx", "*.xls", "*.xlsx", "*.ppt", "*.pptx", "*.pdf", "*.odt", "*.ods", "*.txt", "*.md",
        "*.jpg", "*.jpeg", "*.png", "*.heic", "*.mov", "*.mp4", "*.zip", "*.7z",
        "*.msi", "*.msp", "*.exe", "*.dll", "*.pkg", "*.dmg", "*.deb", "*.rpm", "*.sys",
        "*.kdbx", "*.key", "*.pem", "*.p12", "*.pfx", "*.ovpn",
    ];
    /// <summary>Directory names that are never descended into (installers in progress, IDE/runtime state, Guardian itself).</summary>
    public IReadOnlyList<string> ExcludedDirectoryNames { get; init; } =
    [
        "AI Laptop Guardian", "ai-laptop-guardian", "msdtcadm", "WinSAT", "Low",
        "systemd-private-*", "snap-private-tmp", ".X11-unix", ".ICE-unix", ".font-unix", "tmux-*", "ssh-*",
        "com.apple.*", "TemporaryItems", "vscode-*", "JetBrains*", "dotnet-*", "Nuget*", "npm-*", "pip-*",
    ];
    /// <summary>User-supplied extra exclusions (full paths or name patterns).</summary>
    public IReadOnlyList<string> UserExclusions { get; init; } = [];
}

public enum Eligibility
{
    Eligible,
    TooRecent,
    Excluded,
    NotOwnedByUser,
    OwnershipUnknown,
    InUse,
    InUseUnknown,
    OutsideRoot,
    LinkOrReparsePoint,
    Changed,
    Missing,
}

public sealed record CleanupCandidate(
    string Path,
    string RootPath,
    long SizeBytes,
    DateTime LastWriteUtc,
    Eligibility Eligibility,
    string Explanation)
{
    public bool IsEligible => Eligibility == Eligibility.Eligible;
}

public sealed record CleanupScanResult(
    IReadOnlyList<CleanupCandidate> Eligible,
    IReadOnlyDictionary<Eligibility, int> SkippedByReason,
    int FilesExamined,
    bool LimitReached,
    IReadOnlyList<Reporting.CheckReport> Reports)
{
    public long EligibleBytes => Eligible.Sum(c => c.SizeBytes);
}
