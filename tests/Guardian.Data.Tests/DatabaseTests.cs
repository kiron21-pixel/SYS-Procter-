using Guardian.Core.Modes;
using Guardian.Core.Recovery;
using Guardian.Core.Reporting;
using Guardian.Data;

namespace Guardian.Data.Tests;

public class DatabaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("guardian-db-").FullName;
    private string DbPath => Path.Combine(_dir, "guardian.db");

    [Fact]
    public void Fresh_database_starts_in_observe_only()
    {
        using var db = new GuardianDatabase(DbPath);
        Assert.Null(db.LoadMode());
        Assert.Equal(OperatingMode.ObserveOnly, new ModeManager(db).Current);
    }

    [Fact]
    public void Mode_and_consent_survive_restart()
    {
        using (var db = new GuardianDatabase(DbPath))
            new ModeManager(db).RaiseTo(OperatingMode.SafeMaintenance, "shan", "ENABLE SAFE MAINTENANCE");
        using (var db = new GuardianDatabase(DbPath))
        {
            Assert.Equal(OperatingMode.SafeMaintenance, db.LoadMode());
            var consent = Assert.Single(db.LoadConsentHistory());
            Assert.Equal("shan", consent.UserName);
            Assert.Equal("ENABLE SAFE MAINTENANCE", consent.ConfirmationTyped);
            new ModeManager(db).LowerTo(OperatingMode.ObserveOnly);
            Assert.Equal(OperatingMode.ObserveOnly, db.LoadMode());
            Assert.Single(db.LoadConsentHistory()); // lowering adds no consent record
        }
    }

    [Fact]
    public void Operations_round_trip_and_failures_are_counted()
    {
        using var db = new GuardianDatabase(DbPath);
        var now = DateTimeOffset.UtcNow;
        var rec = new OperationRecord
        {
            Id = Guid.NewGuid(), StartedAt = now, Kind = "k", ModeAtTime = OperatingMode.SafeMaintenance, Target = "/tmp/x",
            Description = "d", Status = OperationStatus.Started, Reversibility = Reversibility.UndoByGuardian, RollbackData = "{}",
        };
        db.Append(rec);
        db.Update(rec with { Status = OperationStatus.Failed, FinishedAt = now, Error = "boom" });

        var loaded = db.Get(rec.Id)!;
        Assert.Equal(OperationStatus.Failed, loaded.Status);
        Assert.Equal("boom", loaded.Error);
        Assert.Equal(1, db.CountFailures("k", "/tmp/x", now.AddMinutes(-1)));
        Assert.Equal(0, db.CountFailures("k", "/tmp/other", now.AddMinutes(-1)));
        Assert.Single(db.Recent());
    }

    [Fact]
    public void Check_history_is_stored()
    {
        using var db = new GuardianDatabase(DbPath);
        db.SaveReports([CheckReport.Completed("x", "X", Finding.Attention, "s", [new Evidence("src", "detail")])]);
        var h = Assert.Single(db.ReportHistory("x"));
        Assert.Equal(Finding.Attention, h.Finding);
        Assert.Equal("detail", h.Evidence[0].Detail);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
