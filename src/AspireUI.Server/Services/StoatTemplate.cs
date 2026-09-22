using System.Security.Cryptography;
using System.Text;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>
/// Stoat (formerly Revolt) as a stack: the services from the project's own compose file, and the
/// three values it must not share between installations generated for each stack.
/// </summary>
public static class StoatTemplate
{
    public const string Id = "stoat";

    // Every absolute URL the server hands out has to be the one a browser can reach — the client asks
    // the edge where the API is and the file server signs attachment links with it — and the port the
    // edge ends up published under is the deployment's to choose, so the compose carries the
    // placeholders HostingService fills once it has picked one.
    private const string Host = "__ASPIREUI_HOST_8080__";
    private const string Url = "__ASPIREUI_URL_8080__";

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "catalog", "templates", Id);

    private static Dictionary<string, string> Secrets()
    {
        var (vapidPublic, vapidPrivate) = Vapid();
        return new Dictionary<string, string>
        {
            ["STOAT_FILES_ENCRYPTION_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["STOAT_VAPID_PUBLIC_KEY"] = vapidPublic,
            ["STOAT_VAPID_PRIVATE_KEY"] = vapidPrivate,
        };
    }

    /// <summary>
    /// Web push identifies a server by a P-256 key pair. Stoat reads the private half as base64 of the
    /// SEC1 PEM `openssl ecparam -genkey` writes, and the public half as the base64url 65-byte
    /// uncompressed point — the shapes its own generate_config.sh produces.
    /// </summary>
    private static (string Public, string Private) Vapid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(includePrivateParameters: false);
        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1 + (32 - p.Q.X!.Length));
        p.Q.Y!.CopyTo(point, 33 + (32 - p.Q.Y!.Length));
        var pem = key.ExportECPrivateKeyPem() + "\n";
        return (Base64Url(point), Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)).TrimEnd('='));
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
            ["STOAT_HOST"] = Host,
            ["STOAT_PUBLIC_URL"] = Url,
            ["STOAT_WS_URL"] = $"ws://{Host}",
        };

        var (stack, _) = new ComposeImporter().Import(stackId, "Stoat",
            ComposeImporter.ResolveEnv(File.ReadAllText(compose), values));
        if (stack is null) return null;

        var (nodes, parameters) = ComposeImporter.AsParameters(stack.Nodes, secrets);
        var files = new List<ExtraFile>();
        var caddyfile = Path.Combine(Dir, "Caddyfile");
        if (File.Exists(caddyfile)) files.Add(new ExtraFile("Caddyfile", File.ReadAllText(caddyfile)));

        return stack with { Nodes = [.. nodes, .. parameters], ExtraFiles = files, HasSource = true };
    }
}
