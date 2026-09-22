using System.Security.Cryptography;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>
/// Fluxer as a stack: the twenty-five services from the project's own compose file, and the sixteen
/// values it refuses to start without as Aspire parameters — generated for each stack, because a
/// secret baked into a template is the same secret in every installation of it.
/// </summary>
public static class FluxerTemplate
{
    public const string Id = "fluxer";

    // Where the stack answers before anything is put in front of it. The vendored compose carries the
    // project's proxy overlay, so the edge serves plain http here rather than fetching a certificate.
    private const string Host = "localhost";
    private const string Port = "8080";

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "catalog", "templates", Id);

    /// <summary>The values that are secrets: a fresh one each time, and a real key pair where one is wanted.</summary>
    private static Dictionary<string, string> Secrets()
    {
        string Hex(int bytes = 24) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
        string B64(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

        var (vapidPublic, vapidPrivate) = Vapid();
        return new Dictionary<string, string>
        {
            ["POSTGRES_PASSWORD"] = Hex(16),
            ["MEILI_MASTER_KEY"] = Hex(),
            ["LIVEKIT_API_KEY"] = Hex(8),
            ["LIVEKIT_API_SECRET"] = Hex(),
            ["FLUXER_ERLANG_COOKIE"] = Hex(16),
            ["FLUXER_SUDO_MODE_SECRET"] = Hex(),
            ["FLUXER_CONNECTION_INITIATION_SECRET"] = Hex(),
            ["FLUXER_GATEWAY_RPC_AUTH_TOKEN"] = Hex(),
            ["FLUXER_MEDIA_PROXY_SECRET_KEY"] = Hex(),
            ["FLUXER_MEDIA_PROXY_UPLOAD_RELAY_SECRET_BASE64"] = B64(32),
            ["FLUXER_ADMIN_SECRET_KEY_BASE"] = B64(48),
            ["FLUXER_ADMIN_OAUTH_CLIENT_SECRET"] = Hex(),
            ["FLUXER_S3_ACCESS_KEY"] = Hex(16),
            ["FLUXER_S3_SECRET_KEY"] = Hex(),
            ["FLUXER_VAPID_PUBLIC_KEY"] = vapidPublic,
            ["FLUXER_VAPID_PRIVATE_KEY"] = vapidPrivate,
        };
    }

    /// <summary>
    /// Web push identifies a server by a P-256 key pair, and Fluxer checks the shape of it before it
    /// starts: two random strings there and the api and the worker crash-loop on the first boot.
    /// </summary>
    private static (string Public, string Private) Vapid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(includePrivateParameters: true);
        var point = new byte[65];
        point[0] = 0x04;                                  // uncompressed
        p.Q.X!.CopyTo(point, 1 + (32 - p.Q.X!.Length));
        p.Q.Y!.CopyTo(point, 33 + (32 - p.Q.Y!.Length));
        var scalar = new byte[32];
        p.D!.CopyTo(scalar, 32 - p.D!.Length);
        return (Base64Url(point), Base64Url(scalar));
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static StackModel? Create(string stackId)
    {
        var compose = Path.Combine(Dir, "docker-compose.yml");
        if (!File.Exists(compose)) return null;

        var secrets = Secrets();
        var values = new Dictionary<string, string>(secrets)
        {
            ["FLUXER_DOMAIN"] = Host,
            ["FLUXER_PUBLIC_SCHEME"] = "http",
            ["FLUXER_PUBLIC_PORT"] = Port,
            ["FLUXER_EDGE_BIND"] = Port,
        };

        var (stack, _) = new ComposeImporter().Import(stackId, "Fluxer",
            ComposeImporter.ResolveEnv(File.ReadAllText(compose), values));
        if (stack is null) return null;

        var (nodes, parameters) = ComposeImporter.AsParameters(stack.Nodes, secrets);
        var files = new List<ExtraFile>();
        var caddyfile = Path.Combine(Dir, "Caddyfile");
        if (File.Exists(caddyfile)) files.Add(new ExtraFile("Caddyfile", File.ReadAllText(caddyfile)));

        return stack with { Nodes = [.. nodes, .. parameters], ExtraFiles = files, HasSource = true };
    }
}
