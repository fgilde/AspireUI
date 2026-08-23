using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// Domains are per target: each machine has its own reverse proxy in front of it, and a managed platform
// binds hostnames through its own API. The kinds:
//
//   npm     — a Nginx Proxy Manager instance (what "this machine" has always used). Full support:
//             list, create, Let's Encrypt certificate, enable/disable, delete.
//   azure   — a custom domain on the target's Container Apps environment, bound with the az CLI.
//   manual  — no API: we show which host and port to point DNS at, nothing is configured for you.
//   none    — domains are off for this target.
public class DomainService(TargetStore targets, SecretStore secrets, SettingsStore settings)
{
    public const string KindNone = "none";
    public const string KindNpm = "npm";
    public const string KindAzure = "azure";
    public const string KindManual = "manual";

    // The old global NPM settings become the local target's domain configuration, once.
    public void MigrateGlobalNpm()
    {
        if (targets.Get(DeployTarget.LocalId) is not { } local) return;
        if (local.Domains is not null) return;
        var baseUrl = settings.GetValue("NpmBaseUrl") ?? "";
        var enabled = (settings.GetValue("NpmEnabled") ?? "false") == "true";
        if (!enabled || string.IsNullOrWhiteSpace(baseUrl))
        {
            targets.Upsert(local with { Domains = new TargetDomains(KindNone) });
            return;
        }
        var pwRef = secrets.Put(settings.GetValue("NpmPassword"), "npm password (this machine)");
        targets.Upsert(local with
        {
            Domains = new TargetDomains(KindNpm, new TargetNpm(baseUrl,
                settings.GetValue("NpmEmail") ?? "", pwRef, settings.GetValue("NpmForwardHost") ?? "")),
        });
    }

    public string KindOf(DeployTarget t) => t.Domains?.Kind ?? KindNone;

    public NpmConfig? Npm(DeployTarget t)
    {
        if (KindOf(t) != KindNpm || t.Domains?.Npm is not { } n || string.IsNullOrWhiteSpace(n.BaseUrl)) return null;
        return new NpmConfig(true, n.BaseUrl, n.Email, secrets.Resolve(n.PasswordRef) ?? "", n.ForwardHost);
    }

    public bool Configured(DeployTarget t) => KindOf(t) switch
    {
        KindNpm => Npm(t) is not null,
        KindAzure => t.Kind == TargetKind.Aca && !string.IsNullOrWhiteSpace(t.Cloud?.ResourceGroup),
        KindManual => true,
        _ => false,
    };

    // Where the proxy must forward to. For a remote target that is the target's own address; for this
    // machine it must never be loopback, because the proxy usually runs on a different box.
    public string ForwardHost(DeployTarget t, string requestHostFallback)
    {
        if (t.Domains?.Npm?.ForwardHost is { Length: > 0 } fixedHost) return fixedHost;
        if (!t.IsLocal) return t.HostForUrls();
        var h = string.IsNullOrWhiteSpace(t.PublicHost) ? requestHostFallback : t.PublicHost!;
        return string.IsNullOrEmpty(h) || h == "localhost" || h.StartsWith("127.") || h == "::1"
            ? HostUrls.CandidateIPs().FirstOrDefault() ?? h
            : h;
    }

    public async Task<List<NpmProxyHost>> ListAsync(DeployTarget t, bool cached = true)
    {
        if (Npm(t) is { } c)
            try { return cached ? await NpmService.ListCachedAsync(c) : await NpmService.ListAsync(c); } catch { return new(); }
        if (KindOf(t) == KindAzure) return await AzureListAsync(t);
        return new();
    }

    public async Task<(bool ok, string? error)> TestAsync(DeployTarget t)
    {
        if (Npm(t) is { } c) return await NpmService.TestAsync(c);
        if (KindOf(t) == KindAzure)
        {
            var r = Cli.Run("az", ["containerapp", "env", "show", "-g", t.Cloud?.ResourceGroup ?? "",
                "-n", t.Cloud?.Environment ?? "aspireui", "-o", "none"], CloudCli.EnvFor(t, secrets));
            return (r.Ok, r.Ok ? null : r.Log);
        }
        if (KindOf(t) == KindManual) return (true, null);
        return (false, "domains are not configured for this target");
    }

    public async Task<NpmProxyHost> UpsertAsync(DeployTarget t, int? id, List<string> domains, string scheme,
        string forwardHost, int port, bool websockets, bool ssl, int certificateId)
    {
        if (Npm(t) is { } c)
        {
            var certId = certificateId;
            if (ssl && certId <= 0)
            {
                if (string.IsNullOrWhiteSpace(c.Email))
                    throw new InvalidOperationException("Set the NPM account email for this target — Let's Encrypt needs it.");
                certId = await NpmService.RequestCertAsync(c, domains, c.Email);
            }
            return await NpmService.UpsertAsync(c, id, domains, scheme, forwardHost, port, websockets, certId, ssl && certId > 0);
        }
        if (KindOf(t) == KindAzure) return await AzureBindAsync(t, domains);
        throw new InvalidOperationException($"'{t.Name}' cannot bind domains ({KindOf(t)})");
    }

    public async Task DeleteAsync(DeployTarget t, int id, string? hostname = null)
    {
        if (Npm(t) is { } c) { await NpmService.DeleteAsync(c, id); return; }
        if (KindOf(t) == KindAzure && hostname is { Length: > 0 })
        {
            Cli.Run("az", ["containerapp", "hostname", "delete", "-g", t.Cloud?.ResourceGroup ?? "",
                "-n", AzureApp(t), "--hostname", hostname, "--yes", "-o", "none"], CloudCli.EnvFor(t, secrets));
            return;
        }
        throw new InvalidOperationException("this target has no domain API");
    }

    public async Task SetEnabledAsync(DeployTarget t, int id, bool enabled)
    {
        if (Npm(t) is { } c) { await NpmService.SetEnabledAsync(c, id, enabled); return; }
        throw new InvalidOperationException("this target cannot enable or disable a domain");
    }

    // ---------- Azure Container Apps custom domains ----------

    private static string AzureApp(DeployTarget t) => t.Cloud?.Account ?? "";

    private Task<List<NpmProxyHost>> AzureListAsync(DeployTarget t)
    {
        var list = new List<NpmProxyHost>();
        var env = CloudCli.EnvFor(t, secrets);
        var apps = Cli.Run("az", ["containerapp", "list", "-g", t.Cloud?.ResourceGroup ?? "",
            "--query", "[].{name:name,hostnames:properties.configuration.ingress.customDomains[].name}", "-o", "json"], env);
        if (!apps.Ok) return Task.FromResult(list);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(apps.Log);
            var i = 1;
            foreach (var app in doc.RootElement.EnumerateArray())
            {
                if (!app.TryGetProperty("hostnames", out var hs) || hs.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                var names = hs.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
                if (names.Count == 0) continue;
                list.Add(new NpmProxyHost(i++, names, "https", app.GetProperty("name").GetString() ?? "", 443, false, true, 1, true));
            }
        }
        catch { }
        return Task.FromResult(list);
    }

    private Task<NpmProxyHost> AzureBindAsync(DeployTarget t, List<string> domains)
    {
        var env = CloudCli.EnvFor(t, secrets);
        var app = AzureApp(t);
        if (string.IsNullOrWhiteSpace(app))
            throw new InvalidOperationException("no container app to bind to — deploy the app first");
        foreach (var d in domains)
        {
            var add = Cli.Run("az", ["containerapp", "hostname", "add", "-g", t.Cloud?.ResourceGroup ?? "",
                "-n", app, "--hostname", d, "-o", "none"], env, timeoutMs: 300_000);
            if (!add.Ok) throw new InvalidOperationException(add.Log);
            var bind = Cli.Run("az", ["containerapp", "hostname", "bind", "-g", t.Cloud?.ResourceGroup ?? "",
                "-n", app, "--hostname", d, "--environment", t.Cloud?.Environment ?? "aspireui",
                "--validation-method", "CNAME", "-o", "none"], env, timeoutMs: 900_000);
            if (!bind.Ok) throw new InvalidOperationException(bind.Log);
        }
        return Task.FromResult(new NpmProxyHost(0, domains, "https", app, 443, false, true, 1, true));
    }
}
