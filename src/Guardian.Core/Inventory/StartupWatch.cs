using Guardian.Core.Reporting;

namespace Guardian.Core.Inventory;

/// <summary>Remembers which startup items the user has already seen. Implemented by Guardian.Data.</summary>
public interface IStartupSeenStore
{
    bool HasBaseline();
    IReadOnlySet<string> KnownKeys();
    void Add(IEnumerable<(string Key, string Display)> items, bool reviewed, DateTimeOffset at);
    IReadOnlyList<(string Key, string Display, DateTimeOffset FirstSeen)> Unreviewed();
    void MarkAllReviewed();
}

/// <summary>
/// Flags programs that added themselves to startup since the last time the user reviewed the list. Adding a startup
/// entry is a common way for malware to persist, but most new entries are legitimate, so this only reports; it never
/// disables anything.
/// </summary>
public sealed class StartupWatch(IStartupSeenStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string Key(StartupItem i) => $"{i.Mechanism}|{i.Location}|{i.Name}|{i.Command}";

    public CheckReport Update(IReadOnlyList<StartupItem> current)
    {
        const string id = "startup.new-items", title = "New startup items";
        var first = !store.HasBaseline();
        var known = store.KnownKeys();
        var fresh = current.Where(i => !known.Contains(Key(i))).Select(i => (Key(i), $"{i.Name} ({i.Mechanism}: {i.Location})")).ToList();
        store.Add(fresh, reviewed: first, _clock.GetUtcNow());
        if (first)
            return CheckReport.Completed(id, title, Finding.NoIssueFound,
                $"Recorded {current.Count} existing startup item(s) as the starting point. Items added after today will be flagged.", [new("Baseline", $"{current.Count} items")]);
        var unreviewed = store.Unreviewed();
        return unreviewed.Count == 0
            ? CheckReport.Completed(id, title, Finding.NoIssueFound, "No new startup items since you last reviewed the list.", [new("Startup items", $"{current.Count} total")])
            : CheckReport.Completed(id, title, Finding.Attention, $"{unreviewed.Count} program(s) added themselves to startup since your last review.",
                unreviewed.Select(u => new Evidence($"First seen {u.FirstSeen.ToLocalTime():yyyy-MM-dd HH:mm}", u.Display)).ToList(),
                "If you don't recognise one, look it up or ask someone you trust before removing it. Guardian does not disable startup items.");
    }
}
