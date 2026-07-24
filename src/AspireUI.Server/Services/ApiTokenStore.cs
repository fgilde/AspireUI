using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

// A personal access token: shown in the UI without the secret (only the prefix, for identification).
public record ApiToken(string Id, string UserId, string Name, string Prefix, string CreatedAt, string? LastUsed);

// Personal access tokens for programmatic / agent (MCP) access. Only the SHA-256 hash is stored — the
// plaintext token is shown once at creation. A token authenticates as its owning user.
public class ApiTokenStore
{
    private readonly string _connString;
    private readonly SqliteConnection? _keepAlive;

    public ApiTokenStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:" ? "Data Source=ApiTokenStore;Mode=Memory;Cache=Shared" : $"Data Source={dbPath}";
        if (dbPath == ":memory:") { _keepAlive = new SqliteConnection(_connString); _keepAlive.Open(); }
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS api_tokens (id TEXT PRIMARY KEY, user_id TEXT, name TEXT, " +
                              "prefix TEXT, hash TEXT UNIQUE, created_at TEXT, last_used TEXT)";
            cmd.ExecuteNonQuery();
        });
    }

    private void Using(Action<SqliteConnection> a)
    {
        if (_keepAlive is { } s) { a(s); return; }
        using var c = new SqliteConnection(_connString); c.Open(); a(c);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    // Create a token for a user; returns (plaintext token shown once, stored record).
    public (string Token, ApiToken Record) Create(string userId, string name)
    {
        var secret = "aspireui_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace("+", "").Replace("/", "").Replace("=", "");
        var id = "tok" + Guid.NewGuid().ToString("n")[..10];
        var prefix = secret[..17];   // "aspireui_" + 8 chars, enough to recognise, not to use
        var now = DateTime.UtcNow.ToString("O");
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO api_tokens (id,user_id,name,prefix,hash,created_at,last_used) VALUES ($i,$u,$n,$p,$h,$c,NULL)";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$u", userId);
            cmd.Parameters.AddWithValue("$n", string.IsNullOrWhiteSpace(name) ? "token" : name);
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$h", Hash(secret));
            cmd.Parameters.AddWithValue("$c", now);
            cmd.ExecuteNonQuery();
        });
        return (secret, new ApiToken(id, userId, name, prefix, now, null));
    }

    // Resolve a presented token to its owning user id (and touch last_used). Null if unknown.
    public string? ResolveUserId(string token)
    {
        string? userId = null, tokenId = null;
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, user_id FROM api_tokens WHERE hash=$h";
            cmd.Parameters.AddWithValue("$h", Hash(token));
            using var r = cmd.ExecuteReader();
            if (r.Read()) { tokenId = r.GetString(0); userId = r.GetString(1); }
        });
        if (tokenId is not null) Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE api_tokens SET last_used=$t WHERE id=$i";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$i", tokenId);
            cmd.ExecuteNonQuery();
        });
        return userId;
    }

    public IReadOnlyList<ApiToken> List(string userId)
    {
        var list = new List<ApiToken>();
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,user_id,name,prefix,created_at,last_used FROM api_tokens WHERE user_id=$u ORDER BY created_at DESC";
            cmd.Parameters.AddWithValue("$u", userId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ApiToken(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        });
        return list;
    }

    public bool Delete(string id, string userId)
    {
        var n = 0;
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM api_tokens WHERE id=$i AND user_id=$u";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$u", userId);
            n = cmd.ExecuteNonQuery();
        });
        return n > 0;
    }
}
