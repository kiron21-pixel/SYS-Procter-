using Guardian.Core.Reporting;

namespace Guardian.Core.Cleanup;

/// <summary>
/// Finds cleanup candidates. This class only reads; it has no code path that deletes or moves files.
/// A file is eligible only when every safeguard gives a definite "safe" answer. Any "unknown" makes it ineligible.
/// </summary>
public sealed class CleanupScanner(ICleanupPlatform platform, CleanupPolicy policy, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public CleanupScanResult Scan(CancellationToken ct = default)
    {
        var eligible = new List<CleanupCandidate>();
        var skipped = new Dictionary<Eligibility, int>();
        var reports = new List<CheckReport>();
        var examined = 0;
        var limitReached = false;

        foreach (var root in platform.GetRoots())
        {
            if (!Directory.Exists(root.Path))
            {
                reports.Add(CheckReport.NotAvailable($"cleanup.root", $"Temporary files in {root.Path}", "This folder does not exist."));
                continue;
            }
            var rootInfo = new DirectoryInfo(root.Path);
            if (rootInfo.LinkTarget is not null)
            {
                reports.Add(CheckReport.Completed("cleanup.root", $"Temporary files in {root.Path}", Finding.Inconclusive,
                    "The temp folder is itself a link, so Guardian will not scan it.", [new("Path check", root.Path)]));
                continue;
            }

            var countBefore = eligible.Count;
            foreach (var file in Enumerate(root.Path))
            {
                ct.ThrowIfCancellationRequested();
                if (++examined > policy.MaxFilesExamined) { limitReached = true; break; }
                var c = Evaluate(root.Path, file);
                if (c.IsEligible) eligible.Add(c);
                else skipped[c.Eligibility] = skipped.GetValueOrDefault(c.Eligibility) + 1;
            }
            var found = eligible.Skip(countBefore).ToList();
            reports.Add(CheckReport.Completed("cleanup.root", $"Temporary files in {root.Path}",
                found.Count == 0 ? Finding.NoIssueFound : Finding.Attention,
                $"{found.Count} file(s) ({Performance.Bytes.Format(found.Sum(f => f.SizeBytes))}) passed every safeguard. Nothing was deleted.",
                [new("Scan", $"{root.Description}; minimum age {policy.MinimumAge.TotalDays:0} days; links not followed")]));
            if (limitReached) break;
        }

        return new CleanupScanResult(eligible, skipped, Math.Min(examined, policy.MaxFilesExamined), limitReached, reports);
    }

    private IEnumerable<string> Enumerate(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Device,
            ReturnSpecialDirectories = false,
        };
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", options).ToList();
                dirs = depth < policy.MaxDepth ? Directory.EnumerateDirectories(dir, "*", options).ToList() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (policy.ExcludedDirectoryNames.Any(p => PathSafety.NameMatches(name, p))) continue;
                stack.Push((d, depth + 1));
            }
        }
    }

    /// <summary>Applies every safeguard to one file. Also used again just before any action, to catch changes.</summary>
    public CleanupCandidate Evaluate(string root, string path)
    {
        var ci = platform.PathsAreCaseInsensitive;
        var info = new FileInfo(path);
        if (!info.Exists) return new(path, root, 0, default, Eligibility.Missing, "The file no longer exists.");
        CleanupCandidate Make(Eligibility e, string why) => new(path, root, info.Length, info.LastWriteTimeUtc, e, why);

        if (!PathSafety.IsStrictlyWithin(root, path, ci))
            return Make(Eligibility.OutsideRoot, "The file is not inside an approved temporary folder.");
        if (PathSafety.HasLinkInChain(root, path, ci))
            return Make(Eligibility.LinkOrReparsePoint, "The file or a folder above it is a link; links are never followed.");

        var name = info.Name;
        if (policy.ExcludedNamePatterns.Any(p => PathSafety.NameMatches(name, p)))
            return Make(Eligibility.Excluded, "Excluded by the built-in exclusion list.");
        foreach (var ex in policy.UserExclusions)
        {
            if (PathSafety.NameMatches(name, ex) ||
                string.Equals(PathSafety.Normalize(ex), PathSafety.Normalize(path), ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                (Directory.Exists(ex) && PathSafety.IsStrictlyWithin(ex, path, ci)))
                return Make(Eligibility.Excluded, "Excluded by your exclusion list.");
        }

        var newest = info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
        if (_clock.GetUtcNow().UtcDateTime - newest < policy.MinimumAge)
            return Make(Eligibility.TooRecent, $"Changed within the last {policy.MinimumAge.TotalDays:0} days.");

        switch (platform.IsOwnedByCurrentUser(path))
        {
            case null: return Make(Eligibility.OwnershipUnknown, "Ownership could not be confirmed.");
            case false: return Make(Eligibility.NotOwnedByUser, "Owned by another user or the system.");
        }
        switch (platform.IsInUse(path))
        {
            case null: return Make(Eligibility.InUseUnknown, "Could not confirm the file is unused.");
            case true: return Make(Eligibility.InUse, "The file is open in a running program.");
        }
        return Make(Eligibility.Eligible, "Passed: inside your temp folder, not a link, not excluded, old, owned by you, not in use.");
    }
}
