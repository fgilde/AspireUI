using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

/// <summary>A conversation. Belongs to one account: a chat that can act on the system is not shared.</summary>
public record ChatSession(string Id, string UserId, string Title, string CreatedAt, string UpdatedAt, int Messages);

/// <summary>
/// One turn. <paramref name="Role"/> is <c>user</c>, <c>assistant</c> or <c>tool</c>;
/// <paramref name="Tools"/> holds what the assistant called on the way to its answer, as json, so the
/// person can see what was done in their name.
/// </summary>
public record ChatMessage(long Id, string SessionId, string Role, string Content, string At, string? Tools);

public class ChatStore
{
    private readonly string _connString;
    private readonly SqliteConnection? _keepAlive;

    public ChatStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:"
            ? $"Data Source=ChatStore-{Guid.NewGuid():n};Mode=Memory;Cache=Shared"
            : $"Data Source={dbPath}";
        if (dbPath == ":memory:") { _keepAlive = new SqliteConnection(_connString); _keepAlive.Open(); }
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS chat_sessions (id TEXT PRIMARY KEY, user_id TEXT, title TEXT, " +
                "created_at TEXT, updated_at TEXT);" +
                "CREATE TABLE IF NOT EXISTS chat_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "session_id TEXT, role TEXT, content TEXT, at TEXT, tools TEXT);" +
                "CREATE INDEX IF NOT EXISTS chat_messages_session ON chat_messages (session_id, id);";
            cmd.ExecuteNonQuery();
        });
    }

    private void Using(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString); conn.Open(); action(conn);
    }

    public ChatSession Create(string userId, string? title = null)
    {
        var now = DateTime.UtcNow.ToString("O");
        var session = new ChatSession("chat" + Guid.NewGuid().ToString("n")[..10], userId,
            string.IsNullOrWhiteSpace(title) ? "New chat" : title!.Trim(), now, now, 0);
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO chat_sessions (id,user_id,title,created_at,updated_at) VALUES ($i,$u,$t,$c,$c)";
            cmd.Parameters.AddWithValue("$i", session.Id);
            cmd.Parameters.AddWithValue("$u", userId);
            cmd.Parameters.AddWithValue("$t", session.Title);
            cmd.Parameters.AddWithValue("$c", now);
            cmd.ExecuteNonQuery();
        });
        return session;
    }

    /// <summary>Newest first, with how many turns each holds.</summary>
    public List<ChatSession> List(string userId, int limit = 50)
    {
        var result = new List<ChatSession>();
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT s.id, s.user_id, s.title, s.created_at, s.updated_at, " +
                "(SELECT COUNT(*) FROM chat_messages m WHERE m.session_id = s.id) " +
                "FROM chat_sessions s WHERE s.user_id = $u ORDER BY s.updated_at DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$u", userId);
            cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 200));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new ChatSession(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetInt32(5)));
        });
        return result;
    }

    /// <summary>The session, but only if it belongs to this user — an id is not an authorisation.</summary>
    public ChatSession? Get(string id, string userId)
    {
        ChatSession? found = null;
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            // The outer table is aliased because chat_messages has an `id` of its own: unqualified,
            // the subquery would compare a session id to a message id and always find nothing.
            cmd.CommandText = "SELECT s.id, s.user_id, s.title, s.created_at, s.updated_at, " +
                              "(SELECT COUNT(*) FROM chat_messages m WHERE m.session_id = s.id) " +
                              "FROM chat_sessions s WHERE s.id=$i AND s.user_id=$u";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$u", userId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                found = new ChatSession(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetInt32(5));
        });
        return found;
    }

    public List<ChatMessage> Messages(string sessionId, int limit = 200)
    {
        var result = new List<ChatMessage>();
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,session_id,role,content,at,tools FROM chat_messages " +
                              "WHERE session_id=$s ORDER BY id LIMIT $l";
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 1000));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new ChatMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        });
        return result;
    }

    public ChatMessage Add(string sessionId, string role, string content, IEnumerable<object>? tools = null)
    {
        var now = DateTime.UtcNow.ToString("O");
        var toolJson = tools is null ? null : JsonSerializer.Serialize(tools, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var id = 0L;
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO chat_messages (session_id,role,content,at,tools) VALUES ($s,$r,$c,$a,$t); " +
                              "SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$c", content);
            cmd.Parameters.AddWithValue("$a", now);
            cmd.Parameters.AddWithValue("$t", (object?)toolJson ?? DBNull.Value);
            id = Convert.ToInt64(cmd.ExecuteScalar());

            using var touch = conn.CreateCommand();
            touch.CommandText = "UPDATE chat_sessions SET updated_at=$a WHERE id=$s";
            touch.Parameters.AddWithValue("$a", now);
            touch.Parameters.AddWithValue("$s", sessionId);
            touch.ExecuteNonQuery();
        });
        return new ChatMessage(id, sessionId, role, content, now, toolJson);
    }

    /// <summary>Names an untitled session after its first question — nobody wants a list of "New chat".</summary>
    public void Rename(string id, string userId, string title) => Using(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_sessions SET title=$t WHERE id=$i AND user_id=$u";
        cmd.Parameters.AddWithValue("$t", Trim(title));
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.ExecuteNonQuery();
    });

    public bool Delete(string id, string userId)
    {
        var gone = false;
        Using(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM chat_messages WHERE session_id IN " +
                              "(SELECT id FROM chat_sessions WHERE id=$i AND user_id=$u); " +
                              "DELETE FROM chat_sessions WHERE id=$i AND user_id=$u;";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$u", userId);
            gone = cmd.ExecuteNonQuery() > 0;
        });
        return gone;
    }

    private static string Trim(string title)
    {
        var one = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= 60 ? one : one[..60].TrimEnd() + "…";
    }
}
