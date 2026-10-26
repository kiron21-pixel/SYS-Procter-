using System.Runtime.InteropServices;

namespace Guardian.Core.Platform;

public enum OsFamily { Windows, MacOS, Linux, Unknown }

/// <summary>What Guardian detected about the computer at runtime. Nothing here is assumed.</summary>
public sealed record PlatformInfo
{
    public required OsFamily Family { get; init; }
    /// <summary>Human-readable OS name, e.g. "Windows 11 Pro", "macOS 15.1", "Ubuntu 24.04.1 LTS".</summary>
    public required string OsName { get; init; }
    public required string OsVersion { get; init; }
    public required string Architecture { get; init; }
    /// <summary>true when running as Administrator (Windows) or root (macOS/Linux).</summary>
    public required bool IsElevated { get; init; }
    /// <summary>Linux distribution id from /etc/os-release (e.g. "ubuntu"), otherwise null.</summary>
    public string? DistributionId { get; init; }
    /// <summary>Linux: "apt", "dnf", or null when not detected or not supported.</summary>
    public string? PackageManager { get; init; }
    /// <summary>Whether Guardian has been tested on, and claims support for, this OS version.</summary>
    public required bool IsSupportedVersion { get; init; }
    public string? SupportNote { get; init; }
    /// <summary>Tools found on this machine that adapters may use (read-only), e.g. "powershell", "spctl", "ufw".</summary>
    public IReadOnlyList<string> DetectedTools { get; init; } = [];

    public static OsFamily DetectFamily()
    {
        if (OperatingSystem.IsWindows()) return OsFamily.Windows;
        if (OperatingSystem.IsMacOS()) return OsFamily.MacOS;
        if (OperatingSystem.IsLinux()) return OsFamily.Linux;
        return OsFamily.Unknown;
    }

    public static string DetectArchitecture() => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
}
