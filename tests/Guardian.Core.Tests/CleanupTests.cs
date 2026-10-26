using Guardian.Core.Cleanup;
using Guardian.Core.Modes;
using Guardian.Core.Recovery;

namespace Guardian.Core.Tests;

public class CleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Old = Now.UtcDateTime.AddDays(-30);
    private static readonly DateTime Recent = Now.UtcDateTime.AddDays(-1);

    private static (CleanupScanner, FakeCleanupPlatform) Scanner(TempTree t, ManualClock clock)
    {
        var p = new FakeCleanupPlatform(t.Temp);
        return (new CleanupScanner(p, new CleanupPolicy(), clock), p);
    }

    [Fact]
    public void Old_unused_owned_file_is_the_only_kind_that_is_eligible()
    {
        using var t = new TempTree();
        var clock = new ManualClock(Now);
        var old = t.File("a/old.tmp", Old);
        t.File("recent.tmp", Recent);
        t.File("data.sqlite", Old);
        t.File("report.docx", Old);
        t.File("app.lock", Old);
        var (scanner, _) = Scanner(t, clock);

        var result = scanner.Scan();

        Assert.Equal([old], result.Eligible.Select(c => c.Path));
        Assert.Equal(1, result.SkippedByReason[Eligibility.TooRecent]);
        Assert.Equal(3, result.SkippedByReason[Eligibility.Excluded]);
    }

    [Fact]
    public void Unknown_ownership_or_usage_makes_a_file_ineligible()
    {
        using var t = new TempTree();
        var a = t.File("a.tmp", Old);
        var b = t.File("b.tmp", Old);
        var c = t.File("c.tmp", Old);
        var (scanner, p) = Scanner(t, new ManualClock(Now));
        p.Owned = path => path == a ? null : path != b;
        p.InUse = path => path == c ? null : false;

        Assert.Equal(Eligibility.OwnershipUnknown, scanner.Evaluate(t.Temp, a).Eligibility);
        Assert.Equal(Eligibility.NotOwnedByUser, scanner.Evaluate(t.Temp, b).Eligibility);
        Assert.Equal(Eligibility.InUseUnknown, scanner.Evaluate(t.Temp, c).Eligibility);
        Assert.Empty(scanner.Scan().Eligible);
    }

    [Fact]
    public void In_use_file_is_not_eligible()
    {
        using var t = new TempTree();
        var a = t.File("a.tmp", Old);
        var (scanner, p) = Scanner(t, new ManualClock(Now));
        p.InUse = _ => true;
        Assert.Equal(Eligibility.InUse, scanner.Evaluate(t.Temp, a).Eligibility);
    }

    [Fact]
    public void Links_are_never_followed_or_offered()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation needs privileges on Windows
        using var t = new TempTree();
        var outside = Path.Combine(t.Root, "precious");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "thesis.bin");
        File.WriteAllText(precious, "do not touch");
        File.SetLastWriteTimeUtc(precious, Old);
        File.SetCreationTimeUtc(precious, Old);
        Directory.CreateSymbolicLink(Path.Combine(t.Temp, "linked-dir"), outside);
        var fileLink = Path.Combine(t.Temp, "linked-file.tmp");
        File.CreateSymbolicLink(fileLink, precious);

        var (scanner, _) = Scanner(t, new ManualClock(Now));
        var result = scanner.Scan();

        Assert.Empty(result.Eligible);
        Assert.Equal(Eligibility.LinkOrReparsePoint, scanner.Evaluate(t.Temp, Path.Combine(t.Temp, "linked-dir", "thesis.bin")).Eligibility);
    }

    [Fact]
    public void Files_outside_the_root_are_rejected()
    {
        using var t = new TempTree();
        var outside = Path.Combine(t.Root, "elsewhere.tmp");
        File.WriteAllText(outside, "x");
        var (scanner, _) = Scanner(t, new ManualClock(Now));
        Assert.Equal(Eligibility.OutsideRoot, scanner.Evaluate(t.Temp, outside).Eligibility);
        Assert.Equal(Eligibility.OutsideRoot, scanner.Evaluate(t.Temp, Path.Combine(t.Temp, "..", "elsewhere.tmp")).Eligibility);
    }

    [Fact]
    public void User_exclusions_are_respected()
    {
        using var t = new TempTree();
        var keep = t.File("keep/me.tmp", Old);
        var clock = new ManualClock(Now);
        var scanner = new CleanupScanner(new FakeCleanupPlatform(t.Temp), new CleanupPolicy { UserExclusions = [Path.Combine(t.Temp, "keep")] }, clock);
        Assert.Equal(Eligibility.Excluded, scanner.Evaluate(t.Temp, keep).Eligibility);
    }

    private sealed record Rig(TempTree Tree, ManualClock Clock, MemoryModeStore Store, MemoryOperationLog Log, CleanupScanner Scanner, HoldingAreaCleaner Cleaner, FakeCleanupPlatform Platform);

    private static Rig MakeRig(TempTree t, OperatingMode mode)
    {
        var clock = new ManualClock(Now);
        var store = new MemoryModeStore { Mode = mode };
        var modes = new ModeManager(store, clock);
        var log = new MemoryOperationLog();
        var platform = new FakeCleanupPlatform(t.Temp);
        var scanner = new CleanupScanner(platform, new CleanupPolicy(), clock);
        return new(t, clock, store, log, scanner, new HoldingAreaCleaner(t.Holding, scanner, new OperationGate(modes), modes, log, clock), platform);
    }

    [Fact]
    public void Observe_only_refuses_to_move_anything()
    {
        using var t = new TempTree();
        var f = t.File("old.tmp", Old);
        var rig = MakeRig(t, OperatingMode.ObserveOnly);
        var candidates = rig.Scanner.Scan().Eligible;

        var results = rig.Cleaner.MoveToHolding(candidates);

        Assert.All(results, r => Assert.False(r.Moved));
        Assert.True(File.Exists(f));
        Assert.Empty(rig.Log.Records);
    }

    [Fact]
    public void Safe_maintenance_moves_to_holding_logs_and_can_restore()
    {
        using var t = new TempTree();
        var f = t.File("sub/old.tmp", Old, "content");
        var rig = MakeRig(t, OperatingMode.SafeMaintenance);

        var result = Assert.Single(rig.Cleaner.MoveToHolding(rig.Scanner.Scan().Eligible));

        Assert.True(result.Moved);
        Assert.False(File.Exists(f));
        var rec = Assert.Single(rig.Log.Records);
        Assert.Equal(OperationStatus.Succeeded, rec.Status);
        Assert.Equal(Reversibility.UndoByGuardian, rec.Reversibility);
        Assert.Equal(f, rec.Target);

        var restore = rig.Cleaner.Restore(rec.Id);
        Assert.True(restore.Moved);
        Assert.Equal("content", File.ReadAllText(f));
        Assert.Equal(OperationStatus.RolledBack, rig.Log.Records[0].Status);
        Assert.False(rig.Cleaner.Restore(rec.Id).Moved); // cannot restore twice
    }

    [Fact]
    public void Restore_never_overwrites_a_new_file_at_the_original_path()
    {
        using var t = new TempTree();
        var f = t.File("old.tmp", Old, "original");
        var rig = MakeRig(t, OperatingMode.SafeMaintenance);
        var id = rig.Cleaner.MoveToHolding(rig.Scanner.Scan().Eligible)[0].OperationId!.Value;
        File.WriteAllText(f, "newer");

        Assert.False(rig.Cleaner.Restore(id).Moved);
        Assert.Equal("newer", File.ReadAllText(f));
    }

    [Fact]
    public void A_file_that_changed_since_the_scan_is_not_moved()
    {
        using var t = new TempTree();
        var f = t.File("old.tmp", Old, "a");
        var rig = MakeRig(t, OperatingMode.SafeMaintenance);
        var candidates = rig.Scanner.Scan().Eligible;
        File.WriteAllText(f, "much longer content");
        File.SetLastWriteTimeUtc(f, Old);

        Assert.False(rig.Cleaner.MoveToHolding(candidates)[0].Moved);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void A_file_that_became_in_use_after_the_scan_is_not_moved()
    {
        using var t = new TempTree();
        var f = t.File("old.tmp", Old);
        var rig = MakeRig(t, OperatingMode.SafeMaintenance);
        var candidates = rig.Scanner.Scan().Eligible;
        rig.Platform.InUse = _ => true;

        Assert.False(rig.Cleaner.MoveToHolding(candidates)[0].Moved);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Purge_needs_safe_maintenance_and_marks_the_operation_irreversible()
    {
        using var t = new TempTree();
        t.File("old.tmp", Old);
        var rig = MakeRig(t, OperatingMode.SafeMaintenance);
        rig.Cleaner.MoveToHolding(rig.Scanner.Scan().Eligible);
        rig.Clock.Now = Now.AddDays(8);

        rig.Store.Mode = OperatingMode.ObserveOnly;
        Assert.Equal(0, rig.Cleaner.PurgeExpired());

        rig.Store.Mode = OperatingMode.SafeMaintenance;
        Assert.Equal(1, rig.Cleaner.PurgeExpired());
        Assert.Equal(Reversibility.Irreversible, rig.Log.Records[0].Reversibility);
        Assert.Empty(Directory.EnumerateFiles(t.Holding, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Repeated_failures_stop_retries()
    {
        using var t = new TempTree();
        var f = t.File("old.tmp", Old);
        var rig = MakeRig(t, OperatingMode.SafeMaintenance);
        var candidates = rig.Scanner.Scan().Eligible;
        for (var i = 0; i < 2; i++)
            rig.Log.Append(new OperationRecord
            {
                Id = Guid.NewGuid(), StartedAt = Now, Kind = HoldingAreaCleaner.OperationKindName, ModeAtTime = OperatingMode.SafeMaintenance,
                Target = f, Description = "x", Status = OperationStatus.Failed, Reversibility = Reversibility.NothingChanged,
            });

        var r = rig.Cleaner.MoveToHolding(candidates)[0];
        Assert.False(r.Moved);
        Assert.Contains("will not keep retrying", r.Message);
    }

    [Theory]
    [InlineData("/tmp", "/tmp/a", true)]
    [InlineData("/tmp", "/tmp", false)]
    [InlineData("/tmp", "/tmpfoo/a", false)]
    [InlineData("/tmp", "/tmp/../etc/passwd", false)]
    public void IsStrictlyWithin(string root, string path, bool expected)
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, PathSafety.IsStrictlyWithin(root, path, false));
    }
}
