using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

// Deploy targets in the shared SQLite database. "local" is seeded on first use and cannot be removed,
// so there is always somewhere to deploy to and old deployments keep a home.
public class TargetStore
{
    private readonly string _connString;
    private readonly SqliteConnection? _keepAlive;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TargetStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:"
            ? $"Data Source=TargetStore-{Guid.NewGuid():n};Mode=Memory;Cache=Shared"
            : $"Data Source={dbPath}";
        if (dbPath == ":memory:") { _keepAlive = new SqliteConnection(_connString); _keepAlive.Open(); }
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS targets (id TEXT PRIMARY KEY, name TEXT, kind TEXT, " +
                              "is_default INTEGER, json TEXT, created_at TEXT, updated_at TEXT)";
            cmd.ExecuteNonQuery();
        });
        EnsureLocal();
    }

    private void UsingConnection(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString); conn.Open(); action(conn);
    }

    private void EnsureLocal()
    {
        if (Get(DeployTarget.LocalId) is not null) return;
        var now = DateTime.UtcNow.ToString("O");
        var anyDefault = List().Any(t => t.Default);
        Upsert(new DeployTarget(DeployTarget.LocalId, "This machine", TargetKind.Local,
            Default: !anyDefault, CreatedAt: now, UpdatedAt: now));
    }

    public DeployTarget? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        DeployTarget? result = null;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM targets WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            var r = cmd.ExecuteScalar();
            if (r is string s) result = JsonSerializer.Deserialize<DeployTarget>(s, Json);
        });
        return result;
    }

    // Never null for a deployment: an unknown or missing id falls back to local, which always exists.
    public DeployTarget Resolve(string? id) => Get(id) ?? Get(DeployTarget.LocalId)!;

    public IReadOnlyList<DeployTarget> List()
    {
        var result = new List<DeployTarget>();
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM targets ORDER BY (id='local') DESC, name COLLATE NOCASE";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (JsonSerializer.Deserialize<DeployTarget>(r.GetString(0), Json) is { } t) result.Add(t);
        });
        return result;
    }

    public DeployTarget Upsert(DeployTarget t)
    {
        var now = DateTime.UtcNow.ToString("O");
        var full = t with { CreatedAt = t.CreatedAt ?? now, UpdatedAt = now };
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO targets (id,name,kind,is_default,json,created_at,updated_at) " +
                              "VALUES ($i,$n,$k,$d,$j,$c,$u)";
            cmd.Parameters.AddWithValue("$i", full.Id);
            cmd.Parameters.AddWithValue("$n", full.Name);
            cmd.Parameters.AddWithValue("$k", full.Kind);
            cmd.Parameters.AddWithValue("$d", full.Default ? 1 : 0);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(full, Json));
            cmd.Parameters.AddWithValue("$c", full.CreatedAt!);
            cmd.Parameters.AddWithValue("$u", full.UpdatedAt!);
            cmd.ExecuteNonQuery();
        });
        if (full.Default) SetDefault(full.Id);
        return Get(full.Id)!;
    }

    public void SetDefault(string id)
    {
        if (Get(id) is null) return;
        foreach (var t in List())
        {
            var want = t.Id == id;
            if (t.Default == want) continue;
            var updated = t with { Default = want, UpdatedAt = DateTime.UtcNow.ToString("O") };
            UsingConnection(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE targets SET is_default=$d, json=$j, updated_at=$u WHERE id=$i";
                cmd.Parameters.AddWithValue("$d", want ? 1 : 0);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(updated, Json));
                cmd.Parameters.AddWithValue("$u", updated.UpdatedAt!);
                cmd.Parameters.AddWithValue("$i", t.Id);
                cmd.ExecuteNonQuery();
            });
        }
    }

    public DeployTarget DefaultTarget() => List().FirstOrDefault(t => t.Default) ?? Get(DeployTarget.LocalId)!;

    // local is the fallback for everything, so it stays.
    public bool Delete(string id)
    {
        if (id == DeployTarget.LocalId) return false;
        var wasDefault = Get(id)?.Default == true;
        var n = 0;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM targets WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            n = cmd.ExecuteNonQuery();
        });
        if (wasDefault) SetDefault(DeployTarget.LocalId);
        return n > 0;
    }

    // A stable, readable id from the name, made unique against what is already stored.
    public string UniqueId(string name)
    {
        var baseId = Slug(name);
        var id = baseId;
        for (var i = 2; Get(id) is not null; i++) id = $"{baseId}-{i}";
        return id;
    }

    public static string Slug(string name)
    {
        var slug = new string((name ?? "target").ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length == 0) slug = "target";
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        return slug == DeployTarget.LocalId ? slug + "-1" : slug;
    }
}
