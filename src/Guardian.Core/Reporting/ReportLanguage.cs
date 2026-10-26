namespace Guardian.Core.Reporting;

/// <summary>
/// Guards the wording Guardian shows. Guardian must never claim a computer is virus-free,
/// unhackable, fully optimized or guaranteed safe. UI text built from check results goes through here.
/// </summary>
public static class ReportLanguage
{
    private static readonly string[] ForbiddenClaims =
    [
        "virus-free", "virus free", "malware-free", "malware free", "unhackable", "immune to hack",
        "fully optimized", "fully optimised", "guaranteed safe", "100% safe", "100% secure",
        "completely secure", "completely safe", "zero risk",
    ];

    /// <summary>Returns the forbidden claim found in <paramref name="text"/>, or null.</summary>
    public static string? FindOverclaim(string text) =>
        ForbiddenClaims.FirstOrDefault(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));

    public static string Describe(SupportStatus status) => status switch
    {
        SupportStatus.Supported => "Supported",
        SupportStatus.Unsupported => "Unsupported",
        SupportStatus.NotAvailable => "Not available",
        SupportStatus.PermissionRequired => "Permission required",
        SupportStatus.NotImplemented => "Not implemented",
        _ => "Unknown",
    };

    public static string Describe(Finding? finding) => finding switch
    {
        null => "Did not run",
        Finding.NoIssueFound => "No issue found by this check",
        Finding.Attention => "Needs attention",
        Finding.Problem => "Problem found",
        Finding.Inconclusive => "Inconclusive",
        _ => "Unknown",
    };

    /// <summary>
    /// A summary line for a set of reports that never overclaims. Even when every check passes,
    /// it says only that the checks that ran found no issue.
    /// </summary>
    public static string Summarize(IReadOnlyCollection<CheckReport> reports)
    {
        var ran = reports.Count(r => r.Ran);
        var problems = reports.Count(r => r.Finding == Finding.Problem);
        var attention = reports.Count(r => r.Finding == Finding.Attention);
        var skipped = reports.Count - ran;
        var parts = new List<string> { $"{ran} of {reports.Count} checks ran" };
        if (problems > 0) parts.Add($"{problems} found a problem");
        if (attention > 0) parts.Add($"{attention} need attention");
        if (problems == 0 && attention == 0 && ran > 0) parts.Add("the checks that ran found no issue");
        if (skipped > 0) parts.Add($"{skipped} could not run (see each check for why)");
        return string.Join("; ", parts) + ". This is not a guarantee that the computer is safe.";
    }
}
