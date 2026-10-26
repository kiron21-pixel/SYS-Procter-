namespace Guardian.Core.Reporting;

/// <summary>Whether a capability can be used on this computer, right now.</summary>
public enum SupportStatus
{
    /// <summary>The check is supported here and Guardian was able to try it.</summary>
    Supported,
    /// <summary>The operating system has no equivalent of this capability.</summary>
    Unsupported,
    /// <summary>The capability exists on this OS but the tool or component is not installed.</summary>
    NotAvailable,
    /// <summary>The check needs a permission (for example administrator rights) that Guardian does not have.</summary>
    PermissionRequired,
    /// <summary>Guardian does not implement this check yet on this OS.</summary>
    NotImplemented,
}

/// <summary>What a check concluded. Only meaningful when the check actually ran.</summary>
public enum Finding
{
    /// <summary>The check ran and found nothing that needs attention.</summary>
    NoIssueFound,
    /// <summary>Something the user should look at, but not urgent.</summary>
    Attention,
    /// <summary>A security or stability problem was found.</summary>
    Problem,
    /// <summary>The check ran but its output could not be interpreted with confidence.</summary>
    Inconclusive,
}

/// <summary>A single piece of evidence: where it came from and what it said.</summary>
public sealed record Evidence(string Source, string Detail);

/// <summary>
/// The honest-reporting record required for every feature: implemented, supported, permitted,
/// actually ran, evidence, changes made, success, and follow-up. Guardian never shows a result
/// that is not backed by one of these.
/// </summary>
public sealed record CheckReport
{
    public required string CheckId { get; init; }
    public required string Title { get; init; }
    public required bool Implemented { get; init; }
    public required SupportStatus Support { get; init; }
    /// <summary>null when the check needs no special permission.</summary>
    public bool? PermissionGranted { get; init; }
    public required bool Ran { get; init; }
    /// <summary>null when the check did not run.</summary>
    public Finding? Finding { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    /// <summary>Observe-only checks never change anything, so this is empty for them.</summary>
    public IReadOnlyList<string> ChangesMade { get; init; } = [];
    /// <summary>Whether the check itself completed (not whether the computer is "safe").</summary>
    public bool? Succeeded { get; init; }
    public string? FollowUp { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;

    public static CheckReport NotSupported(string id, string title, string reason) => new()
    {
        CheckId = id, Title = title, Implemented = true, Support = SupportStatus.Unsupported,
        Ran = false, Summary = reason,
    };

    public static CheckReport NotAvailable(string id, string title, string reason, params Evidence[] evidence) => new()
    {
        CheckId = id, Title = title, Implemented = true, Support = SupportStatus.NotAvailable,
        Ran = false, Summary = reason, Evidence = evidence,
    };

    public static CheckReport NeedsPermission(string id, string title, string reason, params Evidence[] evidence) => new()
    {
        CheckId = id, Title = title, Implemented = true, Support = SupportStatus.PermissionRequired,
        PermissionGranted = false, Ran = false, Summary = reason, Evidence = evidence,
    };

    public static CheckReport NotImplementedYet(string id, string title, string reason) => new()
    {
        CheckId = id, Title = title, Implemented = false, Support = SupportStatus.NotImplemented,
        Ran = false, Summary = reason,
    };

    public static CheckReport Completed(string id, string title, Finding finding, string summary,
        IReadOnlyList<Evidence> evidence, string? followUp = null, bool? permissionGranted = null) => new()
    {
        CheckId = id, Title = title, Implemented = true, Support = SupportStatus.Supported,
        PermissionGranted = permissionGranted, Ran = true, Finding = finding, Summary = summary,
        Evidence = evidence, Succeeded = true, FollowUp = followUp,
    };

    public static CheckReport Failed(string id, string title, string summary, params Evidence[] evidence) => new()
    {
        CheckId = id, Title = title, Implemented = true, Support = SupportStatus.Supported,
        Ran = true, Finding = Reporting.Finding.Inconclusive, Summary = summary, Evidence = evidence,
        Succeeded = false, FollowUp = "The check could not complete. No conclusion was drawn.",
    };
}
