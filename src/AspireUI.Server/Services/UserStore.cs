using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

public class UserStore
{
    private readonly string _connString;
    // Same ":memory:" shared-cache keep-alive pattern as StackStore — keyed by id/username,
    // so a shared fixed name is harmless across instances.
    private readonly SqliteConnection? _keepAlive;

    public UserStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:"
            ? "Data Source=UserStore;Mode=Memory;Cache=Shared"
            : $"Data Source={dbPath}";
        if (dbPath == ":memory:")
        {
            _keepAlive = new SqliteConnection(_connString);
            _keepAlive.Open();
        }
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS users (" +
                               "id TEXT PRIMARY KEY, username TEXT UNIQUE, password_hash TEXT, " +
                               "is_admin INTEGER, created_at TEXT)";
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

    private static User Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetString(4));

    public int Count()
    {
        var count = 0;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM users";
            count = Convert.ToInt32(cmd.ExecuteScalar());
        });
        return count;
    }

    public int AdminCount()
    {
        var count = 0;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM users WHERE is_admin=1";
            count = Convert.ToInt32(cmd.ExecuteScalar());
        });
        return count;
    }

    public User? FindByUsername(string username)
    {
        User? result = null;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, username, password_hash, is_admin, created_at FROM users WHERE username=$u";
            cmd.Parameters.AddWithValue("$u", username);
            using var r = cmd.ExecuteReader();
            if (r.Read()) result = Read(r);
        });
        return result;
    }

    public User? Get(string id)
    {
        User? result = null;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, username, password_hash, is_admin, created_at FROM users WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            using var r = cmd.ExecuteReader();
            if (r.Read()) result = Read(r);
        });
        return result;
    }

    public IReadOnlyList<User> List()
    {
        var result = new List<User>();
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, username, password_hash, is_admin, created_at FROM users ORDER BY username";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(Read(r));
        });
        return result;
    }

    public User Create(string username, string passwordHash, bool isAdmin)
    {
        var user = new User(Guid.NewGuid().ToString("n"), username, passwordHash, isAdmin,
            DateTime.UtcNow.ToString("O"));
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO users (id,username,password_hash,is_admin,created_at) " +
                              "VALUES ($i,$u,$h,$a,$c)";
            cmd.Parameters.AddWithValue("$i", user.Id);
            cmd.Parameters.AddWithValue("$u", user.Username);
            cmd.Parameters.AddWithValue("$h", user.PasswordHash);
            cmd.Parameters.AddWithValue("$a", user.IsAdmin ? 1 : 0);
            cmd.Parameters.AddWithValue("$c", user.CreatedAt);
            cmd.ExecuteNonQuery();
        });
        return user;
    }

    public bool Delete(string id)
    {
        var affected = 0;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM users WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            affected = cmd.ExecuteNonQuery();
        });
        return affected > 0;
    }
}
