namespace Guardian.Core.Platform;

/// <summary>
/// Which OS versions Guardian targets. "Targeted" is not "supported": no platform is declared supported until its
/// installer, integrations, permissions, cleanup safeguards and uninstall have been verified on that platform
/// (see docs/PLATFORM_STATUS.md). Re-check vendor lifecycles before each release.
/// </summary>
public static class SupportMatrix
{
    public const string ReleaseStatus = "Preview: not yet release-verified on this platform.";

    /// <summary>Windows 10 22H2 (build 19045) and Windows 11 (build 22000+).</summary>
    public static (bool Targeted, string Note) Windows(Version v)
    {
        if (v.Major != 10) return (false, "Only Windows 10 and Windows 11 are targeted.");
        if (v.Build >= 22000) return (true, "Windows 11.");
        if (v.Build == 19045) return (true, "Windows 10 22H2. Microsoft ended mainstream support in October 2025; security updates require Extended Security Updates (ESU).");
        return (false, $"Windows 10 build {v.Build} is no longer serviced by Microsoft. Guardian runs in Observe Only with limited checks.");
    }

    /// <summary>macOS 15 and later (Apple security-updates roughly the three most recent major versions).</summary>
    public static (bool Targeted, string Note) MacOS(Version v) =>
        v.Major >= 15 ? (true, $"macOS {v.Major}.") : (false, $"macOS {v.Major} is older than Guardian targets.");

    /// <summary>Ubuntu LTS, Debian stable/oldstable and current Fedora releases.</summary>
    public static (bool Targeted, string Note) Linux(string? id, string? versionId)
    {
        _ = double.TryParse(versionId, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ver);
        return id switch
        {
            "ubuntu" when versionId is "22.04" or "24.04" or "26.04" => (true, $"Ubuntu {versionId} LTS."),
            "ubuntu" => (false, $"Ubuntu {versionId} is not an LTS release Guardian targets."),
            "debian" when ver >= 12 => (true, $"Debian {versionId}."),
            "debian" => (false, $"Debian {versionId} is older than Guardian targets."),
            "fedora" when ver >= 42 => (true, $"Fedora {versionId}."),
            "fedora" => (false, $"Fedora {versionId} is older than Guardian targets."),
            _ => (false, $"The distribution '{id ?? "unknown"}' is not one Guardian targets. Checks that depend on a package manager are disabled."),
        };
    }
}
