using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

public class SettingsStore
{
    private readonly string _connString;
    // Same ":memory:" shared-cache keep-alive pattern as StackStore — see there for why.
    private readonly SqliteConnection? _keepAlive;

    public SettingsStore(string dbPath = "aspireui.db")
    {
        // Unlike StackStore (keyed by stack id, so a shared fixed name is harmless across
        // instances), settings has fixed global keys - a shared name would leak state between
        // unrelated SettingsStore(":memory:") instances in different tests. Unique name per
        // instance keeps the same keep-alive pattern but isolates each instance's data.
        _connString = dbPath == ":memory:"
            ? $"Data Source=SettingsStore-{Guid.NewGuid():n};Mode=Memory;Cache=Shared"
            : $"Data Source={dbPath}";
        if (dbPath == ":memory:")
        {
            _keepAlive = new SqliteConnection(_connString);
            _keepAlive.Open();
        }
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT)";
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

    public AppSettings Get()
    {
        var values = new Dictionary<string, string?>();
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM settings";
            using var r = cmd.ExecuteReader();
            while (r.Read()) values[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        });
        return new AppSettings(
            values.GetValueOrDefault("AiBaseUrl"),
            values.GetValueOrDefault("AiApiKey"),
            values.GetValueOrDefault("AiModel"),
            values.GetValueOrDefault("AiProviderLabel"),
            values.GetValueOrDefault("AiKind"),
            values.GetValueOrDefault("AiCliTool"));
    }

    // Generic key/value access for settings outside the fixed AppSettings shape (e.g. the app-store
    // exclusion list), so saving AI settings never clobbers them.
    public string? GetValue(string key)
    {
        string? val = null;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            var r = cmd.ExecuteScalar();
            val = r is DBNull or null ? null : (string)r;
        });
        return val;
    }

    public void SetValue(string key, string? value) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key,value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    });

    public void Save(AppSettings s) => UsingConnection(conn =>
    {
        void Upsert(string key, string? value)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO settings (key,value) VALUES ($k,$v) " +
                              "ON CONFLICT(key) DO UPDATE SET value=$v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        Upsert("AiBaseUrl", s.AiBaseUrl);
        Upsert("AiApiKey", s.AiApiKey);
        Upsert("AiModel", s.AiModel);
        Upsert("AiProviderLabel", s.AiProviderLabel);
        Upsert("AiKind", s.AiKind);
        Upsert("AiCliTool", s.AiCliTool);
    });
}
