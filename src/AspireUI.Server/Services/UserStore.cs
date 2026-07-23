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
            // Migrations: add columns for accounts added after v1 (ignore "duplicate column" on re-run).
            foreach (var col in new[] { "disabled INTEGER DEFAULT 0", "must_change_password INTEGER DEFAULT 0", "view_modes TEXT" })
                try { using var alter = conn.CreateCommand(); alter.CommandText = $"ALTER TABLE users ADD COLUMN {col}"; alter.ExecuteNonQuery(); }
                catch { /* column already exists */ }
        });
    }

    private void UsingConnection(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        action(conn);
    }

    private const string Cols = "id, username, password_hash, is_admin, created_at, disabled, must_change_password, view_modes";
    private static User Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetString(4),
        !r.IsDBNull(5) && r.GetInt64(5) != 0, !r.IsDBNull(6) && r.GetInt64(6) != 0,
        r.IsDBNull(7) || string.IsNullOrWhiteSpace(r.GetString(7))
            ? null
            : r.GetString(7).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());

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
            cmd.CommandText = $"SELECT {Cols} FROM users WHERE username=$u";
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
            cmd.CommandText = $"SELECT {Cols} FROM users WHERE id=$i";
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
            cmd.CommandText = $"SELECT {Cols} FROM users ORDER BY username";
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

    public void SetPassword(string id, string passwordHash, bool mustChange) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET password_hash=$h, must_change_password=$m WHERE id=$i";
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$m", mustChange ? 1 : 0);
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    });

    public void SetViewModes(string id, List<string> modes) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET view_modes=$v WHERE id=$i";
        cmd.Parameters.AddWithValue("$v", string.Join(",", modes));
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    });

    public void SetAdmin(string id, bool isAdmin) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET is_admin=$a WHERE id=$i";
        cmd.Parameters.AddWithValue("$a", isAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    });

    public void SetDisabled(string id, bool disabled) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET disabled=$d WHERE id=$i";
        cmd.Parameters.AddWithValue("$d", disabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    });

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
