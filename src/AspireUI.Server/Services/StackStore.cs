using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

public class StackStore
{
    private readonly string _connString;

    public StackStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:"
            ? "Data Source=StackStore;Mode=Memory;Cache=Shared"
            : $"Data Source={dbPath}";
        // Keep one open connection for :memory: shared cache to persist across calls.
        _keepAlive = new SqliteConnection(_connString);
        _keepAlive.Open();
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS stacks (id TEXT PRIMARY KEY, name TEXT, json TEXT)";
        cmd.ExecuteNonQuery();
    }

    private readonly SqliteConnection _keepAlive;

    public void Save(StackModel s)
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "INSERT INTO stacks (id,name,json) VALUES ($i,$n,$j) " +
                          "ON CONFLICT(id) DO UPDATE SET name=$n, json=$j";
        cmd.Parameters.AddWithValue("$i", s.Id);
        cmd.Parameters.AddWithValue("$n", s.Name);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(s));
        cmd.ExecuteNonQuery();
    }

    public StackModel? Get(string id)
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "SELECT json FROM stacks WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<StackModel>(json);
    }

    public IReadOnlyList<StackModel> List()
    {
        var result = new List<StackModel>();
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "SELECT json FROM stacks ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(JsonSerializer.Deserialize<StackModel>(r.GetString(0))!);
        return result;
    }

    public void Delete(string id)
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "DELETE FROM stacks WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }
}
