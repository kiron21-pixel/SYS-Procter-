using Guardian.Core.Inventory;
using Guardian.Core.Reporting;

namespace Guardian.Data.Tests;

public class StartupWatchTests
{
    private static StartupItem Item(string name) => new(name, $"C:\\{name}.exe", "HKCU\\...\\Run", ItemScope.CurrentUser, "Registry Run key");

    [Fact]
    public void First_run_records_a_baseline_then_new_items_are_flagged_until_reviewed()
    {
        using var db = GuardianDatabase.InMemory();
        var watch = new StartupWatch(db);

        var first = watch.Update([Item("OneDrive"), Item("Teams")]);
        Assert.Equal(Finding.NoIssueFound, first.Finding);
        Assert.Contains("starting point", first.Summary);

        Assert.Equal(Finding.NoIssueFound, watch.Update([Item("OneDrive"), Item("Teams")]).Finding);

        var second = watch.Update([Item("OneDrive"), Item("Teams"), Item("updater-x")]);
        Assert.Equal(Finding.Attention, second.Finding);
        Assert.Contains(second.Evidence, e => e.Detail.Contains("updater-x"));

        // Still flagged on the next check until the user reviews it.
        Assert.Equal(Finding.Attention, watch.Update([Item("OneDrive"), Item("Teams"), Item("updater-x")]).Finding);

        db.MarkAllReviewed();
        Assert.Equal(Finding.NoIssueFound, watch.Update([Item("OneDrive"), Item("Teams"), Item("updater-x")]).Finding);
    }
}
