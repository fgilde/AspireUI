using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

/// <summary>One thing somebody did: when, who, what, to which app, and whether it worked.</summary>
public record AuditEntry(long Id, string At, string? UserId, string User, string Method, string Route,
    string Action, string? TargetId, string? Target, int Status, int Ms);

/// <summary>
/// The activity log. Rows are written by <see cref="AuditMiddleware"/> — every request that changes
/// something — and read back newest first. Old rows are dropped after <c>AuditRetainDays</c> so the
/// log cannot grow without end.
/// </summary>
public class AuditStore
{
    private readonly string _connString;
    private readonly SqliteConnection? _keepAlive;
    private int _sinceLastPrune;

    public AuditStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:"
            ? $"Data Source=AuditStore-{Guid.NewGuid():n};Mode=Memory;Cache=Shared"
            : $"Data Source={dbPath}";
        if (dbPath == ":memory:") { _keepAlive = new SqliteConnection(_connString); _keepAlive.Open(); }
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS audit (" +
                              "id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT, user_id TEXT, user TEXT, " +
                              "method TEXT, route TEXT, action TEXT, target_id TEXT, target TEXT, " +
                              "status INTEGER, ms INTEGER)";
            cmd.ExecuteNonQuery();
            using var idx = conn.CreateCommand();
            idx.CommandText = "CREATE INDEX IF NOT EXISTS audit_at ON audit (at DESC)";
            idx.ExecuteNonQuery();
        });
    }

    private void Using(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString); conn.Open(); action(conn);
    }

    public void Add(string? userId, string user, string method, string route, string action,
        string? targetId, string? target, int status, int ms)
    {
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO audit (at,user_id,user,method,route,action,target_id,target,status,ms) " +
                              "VALUES ($at,$ui,$u,$m,$r,$a,$ti,$t,$s,$ms)";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$ui", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", user);
            cmd.Parameters.AddWithValue("$m", method);
            cmd.Parameters.AddWithValue("$r", route);
            cmd.Parameters.AddWithValue("$a", action);
            cmd.Parameters.AddWithValue("$ti", (object?)targetId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$ms", ms);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Newest first. <paramref name="q"/> matches the user, the action or the app name.</summary>
    public IReadOnlyList<AuditEntry> List(int limit = 200, int offset = 0, string? q = null, string? userId = null,
        string? targetId = null)
    {
        var result = new List<AuditEntry>();
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT id,at,user_id,user,method,route,action,target_id,target,status,ms FROM audit WHERE 1=1" +
                (string.IsNullOrWhiteSpace(q) ? "" : " AND (user LIKE $q OR action LIKE $q OR target LIKE $q)") +
                (string.IsNullOrWhiteSpace(userId) ? "" : " AND user_id = $ui") +
                (string.IsNullOrWhiteSpace(targetId) ? "" : " AND target_id = $ti") +
                " ORDER BY id DESC LIMIT $l OFFSET $o";
            if (!string.IsNullOrWhiteSpace(q)) cmd.Parameters.AddWithValue("$q", "%" + q.Trim() + "%");
            if (!string.IsNullOrWhiteSpace(userId)) cmd.Parameters.AddWithValue("$ui", userId);
            if (!string.IsNullOrWhiteSpace(targetId)) cmd.Parameters.AddWithValue("$ti", targetId);
            cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 1000));
            cmd.Parameters.AddWithValue("$o", Math.Max(0, offset));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new AuditEntry(r.GetInt64(0), r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                    r.GetInt32(9), r.GetInt32(10)));
        });
        return result;
    }

    public int Count()
    {
        var n = 0;
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit";
            n = Convert.ToInt32(cmd.ExecuteScalar());
        });
        return n;
    }

    /// <summary>Drops everything older than <paramref name="days"/>. Zero or less keeps everything.</summary>
    public int Prune(int days)
    {
        if (days <= 0) return 0;
        var removed = 0;
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM audit WHERE at < $cut";
            cmd.Parameters.AddWithValue("$cut", DateTime.UtcNow.AddDays(-days).ToString("O"));
            removed = cmd.ExecuteNonQuery();
        });
        return removed;
    }

    /// <summary>Prunes now and then rather than on every write — the log is not worth a query per request.</summary>
    internal void PruneOccasionally(int days)
    {
        if (Interlocked.Increment(ref _sinceLastPrune) % 200 != 1) return;
        try { Prune(days); } catch { }
    }
}
