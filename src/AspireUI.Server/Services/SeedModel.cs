using System.Text.Json;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>
/// What an install can be handed on first start: accounts, deploy targets, api tokens, store sources,
/// settings and stacks. The same shape comes from a JSON file (ASPIREUI_SEED_FILE) and from the short
/// env-var forms, so there is one applier per section and two ways to fill it in.
/// </summary>
public record SeedDoc(
    List<SeedUser>? Users = null,
    List<SeedTarget>? Targets = null,
    Dictionary<string, string>? Settings = null,
    List<SeedStack>? Stacks = null,
    List<SeedApp>? Apps = null,
    List<SeedAppSource>? AppSources = null,
    List<SeedToken>? Tokens = null,
    bool Deploy = false)
{
    /// <summary>Concatenates two documents; later entries win only where a name collides at apply time.</summary>
    public static SeedDoc Merge(SeedDoc a, SeedDoc b)
    {
        Dictionary<string, string>? settings = null;
        if (a.Settings is not null || b.Settings is not null)
        {
            settings = new Dictionary<string, string>(a.Settings ?? new());
            foreach (var (k, v) in b.Settings ?? new()) settings[k] = v;
        }
        return new SeedDoc(Concat(a.Users, b.Users), Concat(a.Targets, b.Targets), settings,
            Concat(a.Stacks, b.Stacks), Concat(a.Apps, b.Apps), Concat(a.AppSources, b.AppSources),
            Concat(a.Tokens, b.Tokens), a.Deploy || b.Deploy);
    }

    private static List<T>? Concat<T>(List<T>? a, List<T>? b) =>
        a is null ? b : b is null ? a : [.. a, .. b];
}

/// <summary>An account. No permissions given means <see cref="Perm.Default"/>; Admin outranks the list.</summary>
public record SeedUser(string Username, string? Password = null, bool Admin = false,
    List<string>? Permissions = null, List<string>? ViewModes = null, bool MustChangePassword = false);

/// <summary>
/// A deploy target. Key material (Key, Ca, Cert, TlsKey, Kubeconfig) is either the PEM/YAML itself or
/// the path to a file holding it — a mounted secret is the usual way in.
/// </summary>
public record SeedTarget(string Name, string Kind = TargetKind.Ssh, string? Host = null, int? Port = null,
    string? User = null, string? Key = null, string? Passphrase = null, string? HostKey = null,
    string? DockerHost = null, string? Ca = null, string? Cert = null, string? TlsKey = null,
    string? Kubeconfig = null, string? Context = null, string? Namespace = null,
    string? Expose = null, string? IngressHost = null, string? StorageClass = null,
    string? PublicHost = null, bool Default = false, string? Notes = null);

/// <summary>A bearer token for automation. The value is given, not generated: it has to be known to be used.</summary>
public record SeedToken(string Name, string Username, string Token);

public record SeedAppSource(string Name, string Url);

/// <summary>An app from the store, by catalog id. Name overrides the app's own label.</summary>
public record SeedApp(string Id, string? Name = null, bool? Deploy = null);

/// <summary>
/// A stack. Exactly one source: Projects (AddProject nodes), Compose (yaml text or a file path),
/// Path (a directory to import) or Git (a repository to clone).
/// </summary>
public record SeedStack(string? Name = null, List<string>? Projects = null, string? Compose = null,
    string? Path = null, string? Git = null, string? Branch = null, string? Subdir = null,
    string? Mode = null, bool? Deploy = null);

/// <summary>
/// The short forms. Every one of these is also expressible in the seed file; these exist because a
/// docker-compose file or an Aspire AppHost hands over env vars, not documents.
/// </summary>
public static class SeedParse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string[] Entries(string? raw) =>
        (raw ?? "").Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool LooksLikeJson(string? raw, char open) =>
        raw is not null && raw.TrimStart().StartsWith(open);

    public static SeedDoc? Doc(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SeedDoc>(json, Json);

    /// <summary>
    /// <c>name:password[:permissions]</c> per entry, where permissions is <c>admin</c>, a preset name
    /// (all, operator, app-user, viewer, none) or a comma-separated list of permission ids. A JSON
    /// array is accepted too — the way out when a password contains a colon.
    /// </summary>
    public static List<SeedUser> Users(string? raw)
    {
        if (LooksLikeJson(raw, '['))
            return JsonSerializer.Deserialize<List<SeedUser>>(raw!, Json) ?? [];
        var list = new List<SeedUser>();
        foreach (var entry in Entries(raw))
        {
            var parts = entry.Split(':', 3);
            if (parts.Length < 2 || parts[0].Length == 0) continue;
            var spec = parts.Length > 2 ? parts[2].Trim() : "";
            var admin = spec.Equals("admin", StringComparison.OrdinalIgnoreCase);
            list.Add(new SeedUser(parts[0].Trim(), parts[1], admin,
                admin || spec.Length == 0 ? null : Perms(spec)));
        }
        return list;
    }

    /// <summary>A preset name, or a comma-separated list of permission ids. Unknown ids are dropped.</summary>
    public static List<string> Perms(string? spec)
    {
        var s = (spec ?? "").Trim();
        var preset = s.ToLowerInvariant() switch
        {
            "all" or "everything" => Perm.All,
            "operator" => Perm.Operator,
            "app-user" or "appuser" or "app" => [Perm.Deploy, Perm.Configure, Perm.Files],
            "viewer" or "readonly" or "read-only" => Perm.Viewer,
            "none" or "nothing" => [],
            "default" => Perm.Default,
            _ => null,
        };
        if (preset is not null) return preset.ToList();
        return s.Split([',', '+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Perm.All.Contains).Distinct().ToList();
    }

    /// <summary>
    /// <c>name=uri</c> per entry: <c>ssh://user@host:22?key=/run/secrets/id_ed25519</c>,
    /// <c>tcp://host:2376?ca=…&amp;cert=…&amp;key=…</c>, <c>k8s://context?namespace=apps&amp;kubeconfig=…</c>.
    /// Query keys: key, passphrase, hostKey, ca, cert, namespace, kubeconfig, expose, ingressHost,
    /// storageClass, publicHost, default, notes.
    /// </summary>
    public static List<SeedTarget> Targets(string? raw)
    {
        if (LooksLikeJson(raw, '['))
            return JsonSerializer.Deserialize<List<SeedTarget>>(raw!, Json) ?? [];
        var list = new List<SeedTarget>();
        foreach (var entry in Entries(raw))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var name = entry[..eq].Trim();
            if (!Uri.TryCreate(entry[(eq + 1)..].Trim(), UriKind.Absolute, out var uri)) continue;
            var q = Query(uri.Query);
            var kind = uri.Scheme.ToLowerInvariant() switch
            {
                "ssh" => TargetKind.Ssh,
                "tcp" or "tcps" or "dockertcp" => TargetKind.DockerTcp,
                "k8s" or "kubernetes" => TargetKind.K8s,
                _ => "",
            };
            if (kind.Length == 0) continue;
            var user = uri.UserInfo.Length > 0 ? uri.UserInfo.Split(':')[0] : q.GetValueOrDefault("user");
            list.Add(new SeedTarget(name, kind,
                Host: kind == TargetKind.K8s ? null : uri.Host,
                Port: uri.IsDefaultPort ? null : uri.Port,
                User: user,
                Key: q.GetValueOrDefault("key"),
                Passphrase: q.GetValueOrDefault("passphrase"),
                HostKey: q.GetValueOrDefault("hostkey"),
                DockerHost: q.GetValueOrDefault("dockerhost"),
                Ca: q.GetValueOrDefault("ca"),
                Cert: q.GetValueOrDefault("cert"),
                TlsKey: kind == TargetKind.DockerTcp ? q.GetValueOrDefault("key") : null,
                Kubeconfig: q.GetValueOrDefault("kubeconfig"),
                // k8s://my-context puts the context in the host part, where a hostname would be.
                Context: kind == TargetKind.K8s ? (uri.Host.Length > 0 ? uri.Host : q.GetValueOrDefault("context")) : null,
                Namespace: q.GetValueOrDefault("namespace"),
                Expose: q.GetValueOrDefault("expose"),
                IngressHost: q.GetValueOrDefault("ingresshost"),
                StorageClass: q.GetValueOrDefault("storageclass"),
                PublicHost: q.GetValueOrDefault("publichost"),
                Default: Truthy(q.GetValueOrDefault("default")),
                Notes: q.GetValueOrDefault("notes")));
        }
        return list;
    }

    /// <summary><c>name:username:token</c> per entry.</summary>
    public static List<SeedToken> Tokens(string? raw)
    {
        if (LooksLikeJson(raw, '['))
            return JsonSerializer.Deserialize<List<SeedToken>>(raw!, Json) ?? [];
        return Entries(raw).Select(e => e.Split(':', 3))
            .Where(p => p.Length == 3 && p.All(x => x.Trim().Length > 0))
            .Select(p => new SeedToken(p[0].Trim(), p[1].Trim(), p[2].Trim())).ToList();
    }

    /// <summary><c>name=url</c> per entry.</summary>
    public static List<SeedAppSource> AppSources(string? raw)
    {
        if (LooksLikeJson(raw, '['))
            return JsonSerializer.Deserialize<List<SeedAppSource>>(raw!, Json) ?? [];
        var list = new List<SeedAppSource>();
        foreach (var entry in Entries(raw))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var url = entry[(eq + 1)..].Trim();
            if (url.Length > 0) list.Add(new SeedAppSource(entry[..eq].Trim(), url));
        }
        return list;
    }

    /// <summary><c>catalog-id[=name]</c> per entry, comma or semicolon separated.</summary>
    public static List<SeedApp> Apps(string? raw)
    {
        if (LooksLikeJson(raw, '['))
            return JsonSerializer.Deserialize<List<SeedApp>>(raw!, Json) ?? [];
        var list = new List<SeedApp>();
        foreach (var entry in (raw ?? "").Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            list.Add(eq > 0
                ? new SeedApp(entry[..eq].Trim(), entry[(eq + 1)..].Trim())
                : new SeedApp(entry.Trim()));
        }
        return list;
    }

    /// <summary><c>url[#branch][|subdir]</c> per entry — <c>|</c> because it appears in neither.</summary>
    public static List<SeedStack> GitStacks(string? raw)
    {
        var list = new List<SeedStack>();
        foreach (var entry in Entries(raw))
        {
            var rest = entry;
            string? subdir = null, branch = null;
            var bar = rest.IndexOf('|');
            if (bar >= 0) { subdir = rest[(bar + 1)..].Trim(); rest = rest[..bar]; }
            var hash = rest.IndexOf('#');
            if (hash >= 0) { branch = rest[(hash + 1)..].Trim(); rest = rest[..hash]; }
            if (rest.Trim().Length == 0) continue;
            list.Add(new SeedStack(Git: rest.Trim(), Branch: branch, Subdir: subdir));
        }
        return list;
    }

    /// <summary>Paths to directories or compose files, one stack each.</summary>
    public static List<SeedStack> PathStacks(string? raw) =>
        Entries(raw).Select(p => new SeedStack(Path: p)).ToList();

    public static bool Truthy(string? v) =>
        v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1"
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Length == 0);

    private static Dictionary<string, string> Query(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            d[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return d;
    }
}
