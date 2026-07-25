using System.Text;
using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

public class ProxyService(DeployService deploy, string proxyRoot, string baseDomain)
{
    public const string Project = "aspireui-proxy";
    public string BaseDomain => baseDomain;
    private bool LocalDomain => baseDomain is "localhost" || baseDomain.EndsWith(".localhost") || baseDomain == "127.0.0.1";
    public bool Enabled => !LocalDomain;

    public static string Slug(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "app" : s;
    }

    public string UrlFor(string name) => $"{(LocalDomain ? "http" : "https")}://{Slug(name)}.{baseDomain}";

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

    public void Reload(IEnumerable<(string Slug, int Port)> routes)
    {
        try
        {
            var list = routes.ToList();
            Directory.CreateDirectory(proxyRoot);
            File.WriteAllText(Path.Combine(proxyRoot, "docker-compose.yaml"), ComposeYaml());
            if (list.Count == 0)
            {
                // No apps → an empty Caddyfile makes Caddy exit 1 and crash-loop. Take the proxy down.
                deploy.DownProject(proxyRoot, Project);
                return;
            }
            File.WriteAllText(Path.Combine(proxyRoot, "Caddyfile"), BuildCaddyfile(list, baseDomain));
            deploy.UpProject(proxyRoot, Project);
            deploy.Docker(proxyRoot, $"exec {Project}-caddy-1 caddy reload --config /etc/caddy/Caddyfile");
        }
        catch { }
    }
}
