using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

// Credentials for deploy targets (ssh keys, cloud tokens, registry passwords, NPM passwords).
//
// Two ways to keep one, both usable side by side:
//   "sec:<id>"    the value itself, AES-GCM encrypted in the database. The key comes from
//                 ASPIREUI_SECRET_KEY (base64, 32 bytes) when set — that is what a container deploy
//                 should use, because then the database alone is worthless — otherwise from a key file
//                 next to the workspace, created on first use with owner-only permissions.
//   "env:NAME"    / "file:PATH": nothing secret is stored at all, we read it where it already lives.
//
// A ref is safe to hand to the UI; the value never leaves the server except to the process we start.
public class SecretStore
{
    private readonly string _connString;
    private readonly SqliteConnection? _keepAlive;
    private readonly string _keyPath;
    private byte[]? _key;

    public SecretStore(string dbPath = "aspireui.db", string? keyDir = null)
    {
        _connString = dbPath == ":memory:"
            ? $"Data Source=SecretStore-{Guid.NewGuid():n};Mode=Memory;Cache=Shared"
            : $"Data Source={dbPath}";
        if (dbPath == ":memory:") { _keepAlive = new SqliteConnection(_connString); _keepAlive.Open(); }
        _keyPath = Path.Combine(keyDir ?? Path.GetDirectoryName(Path.GetFullPath(dbPath == ":memory:" ? "." : dbPath))!,
            "_keys", "secrets.key");
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS secrets (id TEXT PRIMARY KEY, label TEXT, " +
                              "payload TEXT NOT NULL, created_at TEXT)";
            cmd.ExecuteNonQuery();
        });
    }

    private void UsingConnection(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString); conn.Open(); action(conn);
    }

    public static bool IsRef(string? value) =>
        value is not null && (value.StartsWith("sec:", StringComparison.Ordinal)
            || value.StartsWith("env:", StringComparison.Ordinal)
            || value.StartsWith("file:", StringComparison.Ordinal));

    private byte[] Key()
    {
        if (_key is not null) return _key;
        var fromEnv = Environment.GetEnvironmentVariable("ASPIREUI_SECRET_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            try
            {
                var raw = Convert.FromBase64String(fromEnv.Trim());
                if (raw.Length == 32) return _key = raw;
            }
            catch { }
            // A passphrase instead of a base64 key: derive one, so a human-typed value still works.
            return _key = SHA256.HashData(Encoding.UTF8.GetBytes(fromEnv.Trim()));
        }
        if (File.Exists(_keyPath))
        {
            try
            {
                var raw = Convert.FromBase64String(File.ReadAllText(_keyPath).Trim());
                if (raw.Length == 32) return _key = raw;
            }
            catch { }
        }
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        File.WriteAllText(_keyPath, Convert.ToBase64String(key));
        FileGuard.OwnerOnly(_keyPath);
        return _key = key;
    }

    // Stores a value and returns its ref. An empty value stores nothing and returns null.
    public string? Put(string? plaintext, string? label = null)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        if (IsRef(plaintext)) return plaintext;              // already a ref: keep the indirection
        var id = Guid.NewGuid().ToString("n");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(Key(), 16)) gcm.Encrypt(nonce, pt, ct, tag);
        var payload = $"v1.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(ct)}";
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO secrets (id,label,payload,created_at) VALUES ($i,$l,$p,$c)";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$l", (object?)label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });
        return "sec:" + id;
    }

    // Replaces the value behind a ref, keeping the ref stable. Null/empty leaves it alone.
    public string? Replace(string? existingRef, string? plaintext, string? label = null)
    {
        if (string.IsNullOrEmpty(plaintext)) return existingRef;
        if (existingRef is not null && existingRef.StartsWith("sec:", StringComparison.Ordinal)) Delete(existingRef);
        return Put(plaintext, label);
    }

    public string? Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("env:", StringComparison.Ordinal))
            return Environment.GetEnvironmentVariable(value[4..]);
        if (value.StartsWith("file:", StringComparison.Ordinal))
        {
            try { return File.ReadAllText(value[5..]); } catch { return null; }
        }
        if (!value.StartsWith("sec:", StringComparison.Ordinal)) return value;   // a literal, e.g. from an import
        string? payload = null;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM secrets WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", value[4..]);
            var r = cmd.ExecuteScalar();
            payload = r is DBNull or null ? null : (string)r;
        });
        if (payload is null) return null;
        var parts = payload.Split('.');
        if (parts.Length != 4 || parts[0] != "v1") return null;
        try
        {
            var nonce = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            var ct = Convert.FromBase64String(parts[3]);
            var pt = new byte[ct.Length];
            using var gcm = new AesGcm(Key(), 16);
            gcm.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return null; }
    }

    public bool Has(string? value) => !string.IsNullOrEmpty(value);

    public void Delete(string? value)
    {
        if (value is null || !value.StartsWith("sec:", StringComparison.Ordinal)) return;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM secrets WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", value[4..]);
            cmd.ExecuteNonQuery();
        });
    }
}

// ssh refuses to use a private key that others can read, and on Windows that means the ACL, not a mode.
public static class FileGuard
{
    public static void OwnerOnly(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var user = Environment.UserName;
                Run("icacls", [path, "/inheritance:r", "/grant:r", $"{user}:F"]);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { }
    }

    private static void Run(string exe, string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(10_000);
    }
}
