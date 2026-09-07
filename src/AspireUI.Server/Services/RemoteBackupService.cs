using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace AspireUI.Server.Services;

/// <summary>
/// Where a backup goes when it must not sit on the same disk as the thing it is a backup of: an
/// S3-compatible bucket, a WebDAV share, or another machine over ssh. One interface, three ways.
/// </summary>
public record RemoteBackupConfig(
    string Kind = "",
    // S3 and anything that speaks it (MinIO, Backblaze, Wasabi, Hetzner, …)
    string? Endpoint = null, string? Region = null, string? Bucket = null,
    string? AccessKey = null, string? SecretKey = null, bool PathStyle = true,
    // WebDAV (Nextcloud, ownCloud, a plain apache)
    string? BaseUrl = null, string? User = null, string? Password = null,
    // ssh: scp to a directory on another machine
    string? Host = null, int Port = 22, string? Path = null, string? KeyFile = null)
{
    public const string None = "";
    public const string S3 = "s3";
    public const string WebDav = "webdav";
    public const string Sftp = "sftp";

    public static readonly string[] Kinds = [None, S3, WebDav, Sftp];

    public bool Enabled => Kind is S3 or WebDav or Sftp;
}

public class RemoteBackupService(SettingsStore settings, SecretStore secrets)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    // Settings hold everything but the two values worth encrypting; those hold a secret-store
    // reference and are resolved on the way out.
    public RemoteBackupConfig Config() => new(
        Kind: settings.GetValue("BackupRemoteKind") ?? RemoteBackupConfig.None,
        Endpoint: settings.GetValue("BackupS3Endpoint"),
        Region: settings.GetValue("BackupS3Region"),
        Bucket: settings.GetValue("BackupS3Bucket"),
        AccessKey: settings.GetValue("BackupS3AccessKey"),
        SecretKey: secrets.Resolve(settings.GetValue("BackupS3SecretKey")),
        PathStyle: (settings.GetValue("BackupS3PathStyle") ?? "true") == "true",
        BaseUrl: settings.GetValue("BackupWebDavUrl"),
        User: settings.GetValue("BackupWebDavUser"),
        Password: secrets.Resolve(settings.GetValue("BackupWebDavPassword")),
        Host: settings.GetValue("BackupSftpHost"),
        Port: int.TryParse(settings.GetValue("BackupSftpPort"), out var p) && p > 0 ? p : 22,
        Path: settings.GetValue("BackupSftpPath"),
        KeyFile: settings.GetValue("BackupSftpKeyFile"));

    public void Save(RemoteBackupConfig c)
    {
        settings.SetValue("BackupRemoteKind", RemoteBackupConfig.Kinds.Contains(c.Kind) ? c.Kind : RemoteBackupConfig.None);
        settings.SetValue("BackupS3Endpoint", c.Endpoint?.Trim());
        settings.SetValue("BackupS3Region", c.Region?.Trim());
        settings.SetValue("BackupS3Bucket", c.Bucket?.Trim());
        settings.SetValue("BackupS3AccessKey", c.AccessKey?.Trim());
        settings.SetValue("BackupS3PathStyle", c.PathStyle ? "true" : "false");
        settings.SetValue("BackupWebDavUrl", c.BaseUrl?.Trim());
        settings.SetValue("BackupWebDavUser", c.User?.Trim());
        settings.SetValue("BackupSftpHost", c.Host?.Trim());
        settings.SetValue("BackupSftpPort", c.Port.ToString());
        settings.SetValue("BackupSftpPath", c.Path?.Trim());
        settings.SetValue("BackupSftpKeyFile", c.KeyFile?.Trim());
        // An empty password means "leave the one that is there": a form that shows *** must not be
        // able to erase a secret by being saved.
        if (!string.IsNullOrWhiteSpace(c.SecretKey))
            settings.SetValue("BackupS3SecretKey", secrets.Replace(settings.GetValue("BackupS3SecretKey"), c.SecretKey, "s3 secret key"));
        if (!string.IsNullOrWhiteSpace(c.Password))
            settings.SetValue("BackupWebDavPassword", secrets.Replace(settings.GetValue("BackupWebDavPassword"), c.Password, "webdav password"));
    }

    /// <summary>Writes a small file and reads it back — the only honest way to say "it works".</summary>
    public async Task<(bool ok, string? error)> TestAsync()
    {
        var c = Config();
        if (!c.Enabled) return (false, "no off-site target configured");
        var name = $"_aspireui-test/{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".txt");
        await File.WriteAllTextAsync(tmp, "AspireUI reachability check");
        try
        {
            var (ok, error) = await UploadAsync(tmp, name);
            if (!ok) return (false, error);
            var (deleted, delError) = await DeleteAsync(name);
            return deleted ? (true, null) : (true, $"upload works, deleting does not: {delError}");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public async Task<(bool ok, string? error)> UploadAsync(string localPath, string key)
    {
        var c = Config();
        if (!c.Enabled) return (false, "no off-site target configured");
        if (!File.Exists(localPath)) return (false, $"{localPath} is not there");
        try
        {
            return c.Kind switch
            {
                RemoteBackupConfig.S3 => await S3Async(c, HttpMethod.Put, key, localPath),
                RemoteBackupConfig.WebDav => await WebDavPutAsync(c, key, localPath),
                RemoteBackupConfig.Sftp => await Task.Run(() => ScpUp(c, localPath, key)),
                _ => (false, "unknown kind"),
            };
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool ok, string? error)> DownloadAsync(string key, string localPath)
    {
        var c = Config();
        if (!c.Enabled) return (false, "no off-site target configured");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        try
        {
            return c.Kind switch
            {
                RemoteBackupConfig.S3 => await S3Async(c, HttpMethod.Get, key, null, localPath),
                RemoteBackupConfig.WebDav => await WebDavGetAsync(c, key, localPath),
                RemoteBackupConfig.Sftp => await Task.Run(() => ScpDown(c, key, localPath)),
                _ => (false, "unknown kind"),
            };
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool ok, string? error)> DeleteAsync(string key)
    {
        var c = Config();
        if (!c.Enabled) return (false, "no off-site target configured");
        try
        {
            return c.Kind switch
            {
                RemoteBackupConfig.S3 => await S3Async(c, HttpMethod.Delete, key, null),
                RemoteBackupConfig.WebDav => await WebDavDeleteAsync(c, key),
                RemoteBackupConfig.Sftp => await Task.Run(() => Rm(c, key)),
                _ => (false, "unknown kind"),
            };
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Keys under a prefix, newest last. Used to offer an off-site restore.</summary>
    public async Task<(List<string> keys, string? error)> ListAsync(string prefix = "")
    {
        var c = Config();
        if (!c.Enabled) return ([], "no off-site target configured");
        try
        {
            return c.Kind switch
            {
                RemoteBackupConfig.S3 => await S3ListAsync(c, prefix),
                RemoteBackupConfig.WebDav => await WebDavListAsync(c, prefix),
                RemoteBackupConfig.Sftp => await Task.Run(() => SftpList(c, prefix)),
                _ => ([], "unknown kind"),
            };
        }
        catch (Exception ex) { return ([], ex.Message); }
    }

    // --- S3 ---------------------------------------------------------------------------------------

    private static Uri S3Uri(RemoteBackupConfig c, string key, string? query = null)
    {
        var endpoint = (c.Endpoint ?? "").TrimEnd('/');
        if (endpoint.Length == 0) endpoint = $"https://s3.{c.Region ?? "us-east-1"}.amazonaws.com";
        var path = c.PathStyle ? $"/{c.Bucket}/{key}" : $"/{key}";
        var host = c.PathStyle ? endpoint : endpoint.Replace("://", $"://{c.Bucket}.");
        return new Uri(host + Uri.EscapeUriString(path) + (query is null ? "" : "?" + query));
    }

    private async Task<(bool ok, string? error)> S3Async(RemoteBackupConfig c, HttpMethod method, string key,
        string? uploadFrom, string? downloadTo = null)
    {
        var uri = S3Uri(c, key);
        using var req = new HttpRequestMessage(method, uri);
        byte[] body = [];
        if (uploadFrom is not null)
        {
            body = await File.ReadAllBytesAsync(uploadFrom);
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        Sign(req, c, body);

        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
            return (false, $"{(int)res.StatusCode} {res.ReasonPhrase}: {Short(await res.Content.ReadAsStringAsync())}");
        if (downloadTo is not null)
        {
            await using var file = File.Create(downloadTo);
            await res.Content.CopyToAsync(file);
        }
        return (true, null);
    }

    private async Task<(List<string> keys, string? error)> S3ListAsync(RemoteBackupConfig c, string prefix)
    {
        var uri = S3Uri(c, "", $"list-type=2&prefix={Uri.EscapeDataString(prefix)}&max-keys=1000");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        Sign(req, c, []);
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return ([], $"{(int)res.StatusCode}: {Short(text)}");

        var doc = XDocument.Parse(text);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        return (doc.Descendants(ns + "Contents")
            .Select(e => e.Element(ns + "Key")?.Value ?? "")
            .Where(k => k.Length > 0).OrderBy(k => k, StringComparer.Ordinal).ToList(), null);
    }

    /// <summary>
    /// AWS Signature Version 4, by hand. One PUT, one GET, one DELETE and a listing do not justify a
    /// cloud SDK with a hundred transitive packages, and the signing is a documented recipe.
    /// </summary>
    public static void Sign(HttpRequestMessage req, RemoteBackupConfig c, byte[] body)
    {
        var now = DateTime.UtcNow;
        var stamp = now.ToString("yyyyMMddTHHmmssZ");
        var date = now.ToString("yyyyMMdd");
        var region = string.IsNullOrWhiteSpace(c.Region) ? "us-east-1" : c.Region!;
        var payloadHash = Hex(SHA256.HashData(body));

        req.Headers.TryAddWithoutValidation("x-amz-date", stamp);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var uri = req.RequestUri!;
        var canonicalHeaders = $"host:{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}\n" +
                               $"x-amz-content-sha256:{payloadHash}\nx-amz-date:{stamp}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalQuery = string.Join("&", (uri.Query.TrimStart('?')).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(x => x, StringComparer.Ordinal));
        var canonicalRequest = $"{req.Method.Method}\n{uri.AbsolutePath}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var scope = $"{date}/{region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{stamp}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        var signature = Hex(Hmac(SigningKey(c.SecretKey ?? "", date, region, "s3"), toSign));

        req.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={c.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    public static byte[] SigningKey(string secret, string date, string region, string service)
    {
        var k = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date);
        k = Hmac(k, region);
        k = Hmac(k, service);
        return Hmac(k, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // --- WebDAV -----------------------------------------------------------------------------------

    private static Uri DavUri(RemoteBackupConfig c, string key) =>
        new((c.BaseUrl ?? "").TrimEnd('/') + "/" + key.TrimStart('/'));

    private static void DavAuth(HttpRequestMessage req, RemoteBackupConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.User)) return;
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.User}:{c.Password}")));
    }

    // A share has no mkdir -p, so every folder on the way has to be made, and "it exists" is a
    // success as far as we are concerned.
    private async Task EnsureDavFoldersAsync(RemoteBackupConfig c, string key)
    {
        var parts = key.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), DavUri(c, string.Join('/', parts[..i]) + "/"));
            DavAuth(req, c);
            try { using var _ = await Http.SendAsync(req); } catch { }
        }
    }

    private async Task<(bool ok, string? error)> WebDavPutAsync(RemoteBackupConfig c, string key, string localPath)
    {
        await EnsureDavFoldersAsync(c, key);
        using var req = new HttpRequestMessage(HttpMethod.Put, DavUri(c, key));
        DavAuth(req, c);
        await using var stream = File.OpenRead(localPath);
        req.Content = new StreamContent(stream);
        using var res = await Http.SendAsync(req);
        return res.IsSuccessStatusCode ? (true, null)
            : (false, $"{(int)res.StatusCode} {res.ReasonPhrase}");
    }

    private async Task<(bool ok, string? error)> WebDavGetAsync(RemoteBackupConfig c, string key, string localPath)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, DavUri(c, key));
        DavAuth(req, c);
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return (false, $"{(int)res.StatusCode} {res.ReasonPhrase}");
        await using var file = File.Create(localPath);
        await res.Content.CopyToAsync(file);
        return (true, null);
    }

    private async Task<(bool ok, string? error)> WebDavDeleteAsync(RemoteBackupConfig c, string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, DavUri(c, key));
        DavAuth(req, c);
        using var res = await Http.SendAsync(req);
        return res.IsSuccessStatusCode || res.StatusCode == System.Net.HttpStatusCode.NotFound
            ? (true, null) : (false, $"{(int)res.StatusCode} {res.ReasonPhrase}");
    }

    private async Task<(List<string> keys, string? error)> WebDavListAsync(RemoteBackupConfig c, string prefix)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), DavUri(c, prefix));
        DavAuth(req, c);
        req.Headers.TryAddWithoutValidation("Depth", "infinity");
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return ([], $"{(int)res.StatusCode} {res.ReasonPhrase}");

        var basePath = new Uri((c.BaseUrl ?? "").TrimEnd('/') + "/").AbsolutePath;
        var doc = XDocument.Parse(text);
        return (doc.Descendants().Where(e => e.Name.LocalName == "href")
            .Select(e => Uri.UnescapeDataString(e.Value))
            .Select(href => href.StartsWith(basePath, StringComparison.Ordinal) ? href[basePath.Length..] : href)
            .Where(k => k.Length > 0 && !k.EndsWith('/'))
            .OrderBy(k => k, StringComparer.Ordinal).ToList(), null);
    }

    // --- ssh --------------------------------------------------------------------------------------

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
    private static string RemotePath(RemoteBackupConfig c, string key) => ((c.Path ?? ".").TrimEnd('/')) + "/" + key;

    private static DeployResult Ssh(RemoteBackupConfig c, string command)
    {
        var args = new List<string> { "-p", c.Port.ToString(), "-o", "StrictHostKeyChecking=accept-new", "-o", "BatchMode=yes" };
        if (!string.IsNullOrWhiteSpace(c.KeyFile)) { args.Add("-i"); args.Add(c.KeyFile!); }
        args.Add($"{c.User}@{c.Host}");
        args.Add(command);
        return Cli.Run("ssh", [.. args], timeoutMs: 600_000);
    }

    private static (bool ok, string? error) ScpUp(RemoteBackupConfig c, string localPath, string key)
    {
        var remote = RemotePath(c, key);
        var dir = remote[..remote.LastIndexOf('/')];
        var made = Ssh(c, $"mkdir -p -- {Quote(dir)}");
        if (!made.Ok) return (false, made.Log);
        return Scp(c, localPath, $"{c.User}@{c.Host}:{remote}");
    }

    private static (bool ok, string? error) ScpDown(RemoteBackupConfig c, string key, string localPath) =>
        Scp(c, $"{c.User}@{c.Host}:{RemotePath(c, key)}", localPath);

    private static (bool ok, string? error) Scp(RemoteBackupConfig c, string from, string to)
    {
        var args = new List<string> { "-P", c.Port.ToString(), "-o", "StrictHostKeyChecking=accept-new", "-o", "BatchMode=yes" };
        if (!string.IsNullOrWhiteSpace(c.KeyFile)) { args.Add("-i"); args.Add(c.KeyFile!); }
        args.Add(from);
        args.Add(to);
        var r = Cli.Run("scp", [.. args], timeoutMs: 1_800_000);
        return (r.Ok, r.Ok ? null : r.Log);
    }

    private static (List<string> keys, string? error) SftpList(RemoteBackupConfig c, string prefix)
    {
        var root = (c.Path ?? ".").TrimEnd('/');
        // Paths relative to the configured root, so a key looks the same here as it does on S3.
        var r = Ssh(c, $"cd {Quote(root)} && find {Quote("./" + prefix)} -type f 2>/dev/null || true");
        if (!r.Ok) return ([], r.Log);
        return (r.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.StartsWith("./", StringComparison.Ordinal) ? k[2..] : k)
            .Where(k => k.Length > 0)
            .OrderBy(k => k, StringComparer.Ordinal).ToList(), null);
    }

    private static (bool ok, string? error) Rm(RemoteBackupConfig c, string key)
    {
        var r = Ssh(c, $"rm -f -- {Quote(RemotePath(c, key))}");
        return (r.Ok, r.Ok ? null : r.Log);
    }

    private static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
