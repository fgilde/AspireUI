using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

// A reusable palette snippet: a saved sub-graph (one or more configured nodes + the edges among them
// + any files) the user captured from a stack and wants to drop again. Per-instance, stored in the
// shared SQLite DB (its own table), so all users of this AspireUI instance see it — like user templates.
public record SnippetModel(string Id, string Name, string? Group, string? Icon,
    List<NodeModel> Nodes, List<EdgeModel> Edges, List<ExtraFile> Files);

public class SnippetStore
{
    private readonly string _connString;
    private readonly SqliteConnection? _keepAlive;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SnippetStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:" ? "Data Source=SnippetStore;Mode=Memory;Cache=Shared" : $"Data Source={dbPath}";
        if (dbPath == ":memory:") { _keepAlive = new SqliteConnection(_connString); _keepAlive.Open(); }
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS snippets (id TEXT PRIMARY KEY, name TEXT, grp TEXT, icon TEXT, json TEXT)";
            cmd.ExecuteNonQuery();
        });
    }

    private void UsingConnection(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString); conn.Open(); action(conn);
    }

    public void Save(SnippetModel s) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO snippets (id,name,grp,icon,json) VALUES ($i,$n,$g,$c,$j)";
        cmd.Parameters.AddWithValue("$i", s.Id);
        cmd.Parameters.AddWithValue("$n", s.Name);
        cmd.Parameters.AddWithValue("$g", (object?)s.Group ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", (object?)s.Icon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(s, Json));
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<SnippetModel> List()
    {
        var result = new List<SnippetModel>();
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM snippets ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(JsonSerializer.Deserialize<SnippetModel>(r.GetString(0), Json)!);
        });
        return result;
    }

    public bool Delete(string id)
    {
        var n = 0;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM snippets WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            n = cmd.ExecuteNonQuery();
        });
        return n > 0;
    }
}
