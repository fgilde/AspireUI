using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

// Tracks hosting deployments (one per stack) in the shared SQLite DB. Mirrors SnippetStore.
public class DeploymentStore
{
    private readonly string _connString;
    private readonly SqliteConnection? _keepAlive;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DeploymentStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:" ? "Data Source=DeploymentStore;Mode=Memory;Cache=Shared" : $"Data Source={dbPath}";
        if (dbPath == ":memory:") { _keepAlive = new SqliteConnection(_connString); _keepAlive.Open(); }
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS deployments (id TEXT PRIMARY KEY, stack_id TEXT UNIQUE, " +
                              "name TEXT, compose_dir TEXT, project TEXT, state TEXT, urls TEXT, " +
                              "created_at TEXT, updated_at TEXT, last_error TEXT)";
            cmd.ExecuteNonQuery();
        });
    }

    private void UsingConnection(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString); conn.Open(); action(conn);
    }

    private static Deployment Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        JsonSerializer.Deserialize<List<string>>(r.IsDBNull(6) ? "[]" : r.GetString(6), Json) ?? new(),
        r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));

    private const string Cols = "id, stack_id, name, compose_dir, project, state, urls, created_at, updated_at, last_error";

    public void Upsert(Deployment d) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO deployments (" + Cols + ") VALUES " +
                          "($i,$s,$n,$c,$p,$st,$u,$ca,$ua,$e)";
        cmd.Parameters.AddWithValue("$i", d.Id);
        cmd.Parameters.AddWithValue("$s", d.StackId);
        cmd.Parameters.AddWithValue("$n", d.Name);
        cmd.Parameters.AddWithValue("$c", d.ComposeDir);
        cmd.Parameters.AddWithValue("$p", d.Project);
        cmd.Parameters.AddWithValue("$st", d.State);
        cmd.Parameters.AddWithValue("$u", JsonSerializer.Serialize(d.Urls, Json));
        cmd.Parameters.AddWithValue("$ca", d.CreatedAt);
        cmd.Parameters.AddWithValue("$ua", d.UpdatedAt);
        cmd.Parameters.AddWithValue("$e", (object?)d.LastError ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    });

    public Deployment? Get(string id) => QueryOne("WHERE id=$k", id);
    public Deployment? GetByStack(string stackId) => QueryOne("WHERE stack_id=$k", stackId);

    private Deployment? QueryOne(string where, string key)
    {
        Deployment? result = null;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM deployments {where}";
            cmd.Parameters.AddWithValue("$k", key);
            using var r = cmd.ExecuteReader();
            if (r.Read()) result = Read(r);
        });
        return result;
    }

    public IReadOnlyList<Deployment> List()
    {
        var result = new List<Deployment>();
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM deployments ORDER BY created_at DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(Read(r));
        });
        return result;
    }

    public void SetState(string id, string state, string? error = null) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE deployments SET state=$st, last_error=$e, updated_at=$ua WHERE id=$i";
        cmd.Parameters.AddWithValue("$st", state);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ua", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    });

    public bool Delete(string id)
    {
        var n = 0;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM deployments WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            n = cmd.ExecuteNonQuery();
        });
        return n > 0;
    }
}
