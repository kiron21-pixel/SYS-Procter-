using System.Text.Json;
using Guardian.Core.Inventory;
using Guardian.Core.Modes;
using Guardian.Core.Recovery;
using Guardian.Core.Reporting;
using Microsoft.Data.Sqlite;

namespace Guardian.Data;

/// <summary>
/// Guardian's local SQLite store: mode and consent history, the operation log, and check history.
/// It only ever lives in Guardian's own data folder and is never sent anywhere.
/// </summary>
public sealed class GuardianDatabase : IModeStore, IOperationLog, IStartupSeenStore, IDisposable
{
    public const int SchemaVersion = 1;
    private readonly SqliteConnection _conn;
    private readonly Lock _lock = new();

    public GuardianDatabase(string databasePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (dir is not null) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _conn.Open();
        Migrate();
    }

    /// <summary>An in-memory database for tests.</summary>
    public static GuardianDatabase InMemory() => new(":memory:");

    private void Migrate()
    {
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS mode_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                mode INTEGER NOT NULL,
                changed_at TEXT NOT NULL,
                consent_user TEXT, consent_statement TEXT, consent_typed TEXT);
            CREATE TABLE IF NOT EXISTS operations (
                id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL, finished_at TEXT,
                kind TEXT NOT NULL, mode INTEGER NOT NULL, target TEXT NOT NULL, description TEXT NOT NULL,
                status INTEGER NOT NULL, reversibility INTEGER NOT NULL,
                rollback_data TEXT, rollback_procedure TEXT, error TEXT);
            CREATE INDEX IF NOT EXISTS ix_operations_target ON operations(kind, target, status);
            CREATE TABLE IF NOT EXISTS check_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                check_id TEXT NOT NULL, completed_at TEXT NOT NULL, report_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS startup_seen (
                key TEXT PRIMARY KEY, display TEXT NOT NULL, first_seen TEXT NOT NULL, reviewed INTEGER NOT NULL);
            """);
        Exec("INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', $v);", ("$v", SchemaVersion.ToString()));
    }

    // ---- IModeStore -------------------------------------------------------

    public OperatingMode? LoadMode()
    {
        using var cmd = Command("SELECT mode FROM mode_history ORDER BY id DESC LIMIT 1;");
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (OperatingMode)Convert.ToInt32(v);
    }

    public void SaveMode(OperatingMode mode, ConsentRecord? consent) =>
        Exec("""
            INSERT INTO mode_history(mode, changed_at, consent_user, consent_statement, consent_typed)
            VALUES ($m, $t, $u, $s, $c);
            """,
            ("$m", (int)mode), ("$t", (consent?.GrantedAt ?? DateTimeOffset.UtcNow).ToString("O")),
            ("$u", consent?.UserName), ("$s", consent?.StatementShown), ("$c", consent?.ConfirmationTyped));

    public IReadOnlyList<ConsentRecord> LoadConsentHistory()
    {
        using var cmd = Command("SELECT mode, changed_at, consent_user, consent_statement, consent_typed FROM mode_history WHERE consent_user IS NOT NULL ORDER BY id;");
        using var r = cmd.ExecuteReader();
        var list = new List<ConsentRecord>();
        while (r.Read())
            list.Add(new((OperatingMode)r.GetInt32(0), r.GetString(2), DateTimeOffset.Parse(r.GetString(1)), r.GetString(3), r.GetString(4)));
        return list;
    }

    // ---- IOperationLog ----------------------------------------------------

    public void Append(OperationRecord rec) => Exec("""
        INSERT INTO operations(id, started_at, finished_at, kind, mode, target, description, status, reversibility, rollback_data, rollback_procedure, error)
        VALUES ($id, $s, $f, $k, $m, $t, $d, $st, $rv, $rd, $rp, $e);
        """, Params(rec));

    public void Update(OperationRecord rec) => Exec("""
        UPDATE operations SET finished_at=$f, status=$st, reversibility=$rv, rollback_data=$rd, rollback_procedure=$rp, error=$e
        WHERE id=$id;
        """, Params(rec));

    public OperationRecord? Get(Guid id)
    {
        using var cmd = Command("SELECT * FROM operations WHERE id=$id;", ("$id", id.ToString()));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadOp(r) : null;
    }

    public IReadOnlyList<OperationRecord> Recent(int limit = 200)
    {
        using var cmd = Command("SELECT * FROM operations ORDER BY started_at DESC LIMIT $n;", ("$n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<OperationRecord>();
        while (r.Read()) list.Add(ReadOp(r));
        return list;
    }

    public int CountFailures(string kind, string target, DateTimeOffset since)
    {
        using var cmd = Command("SELECT COUNT(*) FROM operations WHERE kind=$k AND target=$t AND status IN ($f1, $f2) AND started_at >= $s;",
            ("$k", kind), ("$t", target), ("$f1", (int)OperationStatus.Failed), ("$f2", (int)OperationStatus.PartiallyFailed), ("$s", since.ToString("O")));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---- Check history ----------------------------------------------------

    public void SaveReports(IEnumerable<CheckReport> reports)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var rep in reports)
            {
                using var cmd = Command("INSERT INTO check_history(check_id, completed_at, report_json) VALUES ($c, $t, $j);",
                    ("$c", rep.CheckId), ("$t", rep.CompletedAt.ToString("O")), ("$j", JsonSerializer.Serialize(rep)));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<CheckReport> ReportHistory(string checkId, int limit = 50)
    {
        using var cmd = Command("SELECT report_json FROM check_history WHERE check_id=$c ORDER BY id DESC LIMIT $n;", ("$c", checkId), ("$n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<CheckReport>();
        while (r.Read()) list.Add(JsonSerializer.Deserialize<CheckReport>(r.GetString(0))!);
        return list;
    }

    // ---- IStartupSeenStore -------------------------------------------------

    public bool HasBaseline()
    {
        using var cmd = Command("SELECT COUNT(*) FROM meta WHERE key='startup_baseline';");
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public IReadOnlySet<string> KnownKeys()
    {
        using var cmd = Command("SELECT key FROM startup_seen;");
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public void Add(IEnumerable<(string Key, string Display)> items, bool reviewed, DateTimeOffset at)
    {
        foreach (var (key, display) in items)
            Exec("INSERT OR IGNORE INTO startup_seen(key, display, first_seen, reviewed) VALUES ($k, $d, $t, $r);",
                ("$k", key), ("$d", display), ("$t", at.ToString("O")), ("$r", reviewed ? 1 : 0));
        Exec("INSERT OR IGNORE INTO meta(key, value) VALUES ('startup_baseline', $t);", ("$t", at.ToString("O")));
    }

    public IReadOnlyList<(string Key, string Display, DateTimeOffset FirstSeen)> Unreviewed()
    {
        using var cmd = Command("SELECT key, display, first_seen FROM startup_seen WHERE reviewed=0 ORDER BY first_seen;");
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, DateTimeOffset)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2))));
        return list;
    }

    public void MarkAllReviewed() => Exec("UPDATE startup_seen SET reviewed=1 WHERE reviewed=0;");

    // ---- helpers ----------------------------------------------------------

    private static (string, object?)[] Params(OperationRecord rec) =>
    [
        ("$id", rec.Id.ToString()), ("$s", rec.StartedAt.ToString("O")), ("$f", rec.FinishedAt?.ToString("O")),
        ("$k", rec.Kind), ("$m", (int)rec.ModeAtTime), ("$t", rec.Target), ("$d", rec.Description),
        ("$st", (int)rec.Status), ("$rv", (int)rec.Reversibility), ("$rd", rec.RollbackData),
        ("$rp", rec.RollbackProcedure), ("$e", rec.Error),
    ];

    private static OperationRecord ReadOp(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        StartedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("started_at"))),
        FinishedAt = r.IsDBNull(r.GetOrdinal("finished_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("finished_at"))),
        Kind = r.GetString(r.GetOrdinal("kind")),
        ModeAtTime = (OperatingMode)r.GetInt32(r.GetOrdinal("mode")),
        Target = r.GetString(r.GetOrdinal("target")),
        Description = r.GetString(r.GetOrdinal("description")),
        Status = (OperationStatus)r.GetInt32(r.GetOrdinal("status")),
        Reversibility = (Reversibility)r.GetInt32(r.GetOrdinal("reversibility")),
        RollbackData = Nullable(r, "rollback_data"),
        RollbackProcedure = Nullable(r, "rollback_procedure"),
        Error = Nullable(r, "error"),
    };

    private static string? Nullable(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, params (string Name, object? Value)[] ps)
    {
        lock (_lock)
        {
            using var cmd = Command(sql, ps);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();
}
