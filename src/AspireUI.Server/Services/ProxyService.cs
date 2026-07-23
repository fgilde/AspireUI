using System.Text;
using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

// Managed reverse proxy (Caddy) for hosting: gives each deployed app a friendly URL
// (<slug>.<baseDomain>) and, for a real domain, automatic HTTPS via Caddy's ACME. Runs Caddy as its
// own long-lived compose project on the host network so it can reach each app's published host port.
// Local default baseDomain "localhost" → plain http on *.localhost (resolves to 127.0.0.1 in browsers).
public class ProxyService(DeployService deploy, string proxyRoot, string baseDomain)
{
    public const string Project = "aspireui-proxy";
    public string BaseDomain => baseDomain;
    private bool LocalDomain => baseDomain is "localhost" || baseDomain.EndsWith(".localhost") || baseDomain == "127.0.0.1";

    // "Demo Shop" -> "demo-shop"; safe DNS label.
    public static string Slug(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "app" : s;
    }

    public string UrlFor(string name) => $"{(LocalDomain ? "http" : "https")}://{Slug(name)}.{baseDomain}";

    // routes: (slug, hostPort). One site block per app; ACME on for a real domain.
    public static string BuildCaddyfile(IEnumerable<(string Slug, int Port)> routes, string baseDomain)
    {
        var sb = new StringBuilder();
        foreach (var (slug, port) in routes)
        {
            sb.AppendLine($"{slug}.{baseDomain} {{");
            sb.AppendLine($"    reverse_proxy localhost:{port}");
            sb.AppendLine("}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ComposeYaml() => """
        services:
          caddy:
            image: caddy:2
            network_mode: host
            restart: unless-stopped
            volumes:
              - ./Caddyfile:/etc/caddy/Caddyfile
              - caddy_data:/data
              - caddy_config:/config
        volumes:
          caddy_data:
          caddy_config:
        """;

    // Write the Caddyfile for the given routes, ensure Caddy is running, and reload it. Best-effort:
    // docker failures are swallowed (the app's direct host:port URL still works without the proxy).
    public void Reload(IEnumerable<(string Slug, int Port)> routes)
    {
        try
        {
            Directory.CreateDirectory(proxyRoot);
            File.WriteAllText(Path.Combine(proxyRoot, "Caddyfile"), BuildCaddyfile(routes, baseDomain));
            File.WriteAllText(Path.Combine(proxyRoot, "docker-compose.yaml"), ComposeYaml());
            deploy.UpProject(proxyRoot, Project);
            // Apply the new Caddyfile without downtime.
            deploy.Docker(proxyRoot, $"exec {Project}-caddy-1 caddy reload --config /etc/caddy/Caddyfile");
        }
        catch { /* proxy is best-effort; host:port URLs remain valid */ }
    }
}
