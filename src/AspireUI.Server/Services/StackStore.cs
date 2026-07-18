using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

public class StackStore
{
    private readonly string _connString;
    // Only needed to keep the ":memory:" shared-cache DB alive between per-operation
    // connections (SQLite drops an in-memory DB once its last connection closes).
    // A real file DB needs no keep-alive, so this stays null for that case and each
    // operation below opens its own connection instead of sharing one across
    // concurrent HTTP requests (SqliteConnection isn't safe for concurrent commands).
    private readonly SqliteConnection? _keepAlive;

    public StackStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:"
            ? "Data Source=StackStore;Mode=Memory;Cache=Shared"
            : $"Data Source={dbPath}";
        if (dbPath == ":memory:")
        {
            _keepAlive = new SqliteConnection(_connString);
            _keepAlive.Open();
        }
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS stacks (id TEXT PRIMARY KEY, name TEXT, json TEXT)";
            cmd.ExecuteNonQuery();
        });
    }

    private void UsingConnection(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        action(conn);
    }

    public void Save(StackModel s) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO stacks (id,name,json) VALUES ($i,$n,$j) " +
                          "ON CONFLICT(id) DO UPDATE SET name=$n, json=$j";
        cmd.Parameters.AddWithValue("$i", s.Id);
        cmd.Parameters.AddWithValue("$n", s.Name);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(s));
        cmd.ExecuteNonQuery();
    });

    public StackModel? Get(string id)
    {
        StackModel? result = null;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM stacks WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            var json = cmd.ExecuteScalar() as string;
            result = json is null ? null : JsonSerializer.Deserialize<StackModel>(json);
        });
        return result;
    }

    public IReadOnlyList<StackModel> List()
    {
        var result = new List<StackModel>();
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM stacks ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(JsonSerializer.Deserialize<StackModel>(r.GetString(0))!);
        });
        return result;
    }

    public void Delete(string id) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM stacks WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    });
}
