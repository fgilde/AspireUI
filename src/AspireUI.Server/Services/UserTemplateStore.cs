using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

// User-saved templates: a stack snapshot the user chose to reuse as a starting point. Stored in the
// same SQLite DB as stacks/users (its own table), so it survives restarts. Distinct from the built-in
// demo templates baked into TemplateService.
public class UserTemplateStore
{
    private readonly string _connString;
    private readonly SqliteConnection? _keepAlive;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public UserTemplateStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:" ? "Data Source=UserTemplateStore;Mode=Memory;Cache=Shared" : $"Data Source={dbPath}";
        if (dbPath == ":memory:") { _keepAlive = new SqliteConnection(_connString); _keepAlive.Open(); }
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS user_templates (id TEXT PRIMARY KEY, name TEXT, description TEXT, json TEXT)";
            cmd.ExecuteNonQuery();
        });
    }

    private void UsingConnection(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString); conn.Open(); action(conn);
    }

    public record Entry(string Id, string Name, string Description, StackModel Stack);

    public void Save(string id, string name, string description, StackModel stack) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO user_templates (id,name,description,json) VALUES ($i,$n,$d,$j)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$d", description);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(stack, Json));
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<Entry> List()
    {
        var result = new List<Entry>();
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,name,description,json FROM user_templates ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new Entry(r.GetString(0), r.GetString(1), r.GetString(2),
                    JsonSerializer.Deserialize<StackModel>(r.GetString(3), Json)!));
        });
        return result;
    }

    public StackModel? Get(string id) => List().FirstOrDefault(e => e.Id == id)?.Stack;

    public bool Delete(string id)
    {
        var n = 0;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM user_templates WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            n = cmd.ExecuteNonQuery();
        });
        return n > 0;
    }
}
