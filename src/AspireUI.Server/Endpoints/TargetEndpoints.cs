using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Endpoints;

// Deploy targets: the places a stack or store app can be deployed to. Reading is open to any signed-in
// user (the deploy pickers need the list); creating, editing and deleting is admin-only, because a
// target carries credentials and decides where other people's apps end up.
public static class TargetEndpoints
{
    public static void MapTargetEndpoints(this RouteGroupBuilder api, TargetStore store, TargetService svc,
        SecretStore secrets, DeploymentStore deployments, ProvisionService provision)
    {
        var grp = api.MapGroup("/targets");
        var admin = api.MapGroup("/targets").RequireAuthorization(p => p.RequireRole("Admin"));

        // What a target looks like to the client: no secret values, only whether one is set.
        object Dto(DeployTarget t) => new
        {
            t.Id, t.Name, t.Kind, t.Default, t.PublicHost, t.PortFrom, t.PortTo, t.Notes,
            t.CreatedAt, t.UpdatedAt, t.Probe,
            isLocal = t.IsLocal,
            compose = TargetKind.IsCompose(t.Kind),
            host = t.HostForUrls(),
            deployments = deployments.List().Count(d => d.Target == t.Id),
            ssh = t.Ssh is null ? null : new
            {
                t.Ssh.Host, t.Ssh.Port, t.Ssh.User,
                hasKey = !string.IsNullOrEmpty(t.Ssh.KeyRef),
                hasHostKey = !string.IsNullOrEmpty(t.Ssh.HostKey),
            },
            dockerHost = t.DockerHost,
            tls = t.Tls is null ? null : new { hasCa = t.Tls.CaRef is not null, hasCert = t.Tls.CertRef is not null, hasKey = t.Tls.KeyRef is not null },
            kube = t.Kube is null ? null : new { t.Kube.Context, t.Kube.Namespace, hasKubeconfig = t.Kube.KubeconfigRef is not null,
                t.Kube.IngressClass, t.Kube.StorageClass, expose = t.Kube.Expose ?? "clusterip", t.Kube.IngressHostPattern, t.Kube.StorageSize },
            cloud = t.Cloud is null ? null : new { t.Cloud.SubscriptionId, t.Cloud.ResourceGroup, t.Cloud.Location, t.Cloud.Project, t.Cloud.Cluster, t.Cloud.Environment, t.Cloud.Subnets, t.Cloud.SecurityGroups, t.Cloud.ExecutionRoleArn, t.Cloud.AssignPublicIp, hasCredentials = t.Cloud.CredRef is not null },
            provider = t.Provider is null ? null : new { t.Provider.Kind, t.Provider.Region, t.Provider.ServerId, t.Provider.ServerType, hasCredentials = t.Provider.CredRef is not null },
            registry = t.Registry is null ? null : new { t.Registry.Url, t.Registry.User, hasPassword = t.Registry.PasswordRef is not null },
            domains = new
            {
                kind = t.Domains?.Kind ?? "none",
                npm = t.Domains?.Npm is null ? null : new { t.Domains.Npm.BaseUrl, t.Domains.Npm.Email, t.Domains.Npm.ForwardHost, hasPassword = t.Domains.Npm.PasswordRef is not null },
                azure = t.Domains?.Azure,
            },
        };

        grp.MapGet("/", () => Results.Ok(store.List().Select(Dto)));
        grp.MapGet("/{id}", (string id) => store.Get(id) is { } t ? Results.Ok(Dto(t)) : Results.NotFound());
        grp.MapGet("/kinds", () => Results.Ok(TargetKind.All.Select(k => new
        {
            kind = k,
            compose = TargetKind.IsCompose(k),
            cli = CloudCli.Exe(k),
            label = k switch
            {
                TargetKind.Local => "This machine",
                TargetKind.Ssh => "Docker over SSH",
                TargetKind.DockerTcp => "Docker over TCP (mTLS)",
                TargetKind.K8s => "Kubernetes (Helm)",
                TargetKind.Aca => "Azure Container Apps",
                TargetKind.CloudRun => "Google Cloud Run",
                TargetKind.Ecs => "AWS ECS (Fargate)",
                _ => k,
            },
        })));

        admin.MapPost("/", (TargetRequest b) =>
        {
            if (string.IsNullOrWhiteSpace(b.Name)) return Results.BadRequest(new { message = "name is required" });
            if (!TargetKind.All.Contains(b.Kind)) return Results.BadRequest(new { message = $"unknown kind '{b.Kind}'" });
            if (b.Kind == TargetKind.Local) return Results.BadRequest(new { message = "there is only one local target" });
            var id = store.UniqueId(b.Name!);
            var t = Apply(new DeployTarget(id, b.Name!.Trim(), b.Kind!), b, secrets);
            if (Invalid(t) is { } err) return Results.BadRequest(new { message = err });
            var saved = store.Upsert(t);
            svc.Invalidate(saved.Id);
            return Results.Ok(Dto(svc.Probe(saved)));
        });

        admin.MapPut("/{id}", (string id, TargetRequest b) =>
        {
            if (store.Get(id) is not { } cur) return Results.NotFound();
            var t = Apply(cur with { Name = string.IsNullOrWhiteSpace(b.Name) ? cur.Name : b.Name!.Trim() }, b, secrets);
            if (Invalid(t) is { } err) return Results.BadRequest(new { message = err });
            var saved = store.Upsert(t);
            svc.Invalidate(saved.Id);
            return Results.Ok(Dto(saved));
        });

        admin.MapDelete("/{id}", (string id) =>
        {
            if (id == DeployTarget.LocalId) return Results.BadRequest(new { message = "the local target cannot be removed" });
            if (store.Get(id) is null) return Results.NotFound();
            var inUse = deployments.List().Count(d => d.Target == id);
            if (inUse > 0)
                return Results.Conflict(new { message = $"{inUse} app(s) still run on this target — move or remove them first" });
            svc.Invalidate(id);
            secrets.Delete(store.Get(id)?.Ssh?.KeyRef);
            secrets.Delete(store.Get(id)?.Cloud?.CredRef);
            return store.Delete(id) ? Results.NoContent() : Results.NotFound();
        });

        admin.MapPost("/{id}/default", (string id) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            store.SetDefault(id);
            return Results.Ok(Dto(store.Get(id)!));
        });

        admin.MapPost("/{id}/probe", (string id) =>
            store.Get(id) is { } t ? Results.Ok(Dto(svc.Probe(t))) : Results.NotFound());

        // Test before saving: the same probe, run on an unsaved target.
        admin.MapPost("/test", (TargetRequest b) =>
        {
            if (!TargetKind.All.Contains(b.Kind)) return Results.BadRequest(new { message = $"unknown kind '{b.Kind}'" });
            var draft = Apply(new DeployTarget("probe-" + Guid.NewGuid().ToString("n")[..6], b.Name ?? "test", b.Kind!), b, secrets);
            if (Invalid(draft) is { } err) return Results.Ok(new TargetProbe(false, err));
            var probe = svc.ProbeOnly(draft);
            // A throwaway target leaves throwaway key material behind.
            try { Directory.Delete(svc.TargetDir(draft.Id), true); } catch { }
            secrets.Delete(draft.Ssh?.KeyRef);
            secrets.Delete(draft.Cloud?.CredRef);
            secrets.Delete(draft.Kube?.KubeconfigRef);
            return Results.Ok(probe);
        });

        // Docker on a fresh box, over the same ssh connection the target already uses.
        admin.MapPost("/{id}/install-docker", (string id) =>
            store.Get(id) is { } t ? Results.Ok(provision.InstallDocker(t)) : Results.NotFound());

        // A key pair for a target the user sets up by hand: they paste the public half into the box.
        admin.MapPost("/keygen", () => ProvisionService.GenerateKey() is { } k
            ? Results.Ok(k)
            : Results.BadRequest(new { message = "ssh-keygen is not available on this host" }));

        admin.MapGet("/providers", () => Results.Ok(ProvisionService.Providers));
        admin.MapPost("/provision", async (ProvisionRequest b) => Results.Ok(await provision.CreateAsync(b)));
        admin.MapPost("/{id}/destroy-machine", async (string id) =>
            store.Get(id) is { } t ? Results.Ok(await provision.DestroyAsync(t)) : Results.NotFound());
    }

    private static string? Invalid(DeployTarget t) => t.Kind switch
    {
        TargetKind.Ssh when t.Ssh is null || string.IsNullOrWhiteSpace(t.Ssh.Host) => "host is required",
        TargetKind.Ssh when string.IsNullOrWhiteSpace(t.Ssh!.User) => "user is required",
        TargetKind.Ssh when string.IsNullOrEmpty(t.Ssh!.KeyRef) => "a private key is required (docker over ssh cannot use a password)",
        TargetKind.DockerTcp when string.IsNullOrWhiteSpace(t.DockerHost) => "docker host is required (tcp://host:2376)",
        TargetKind.Aca when string.IsNullOrWhiteSpace(t.Cloud?.ResourceGroup) => "resource group is required",
        TargetKind.CloudRun when string.IsNullOrWhiteSpace(t.Cloud?.Project) => "project is required",
        TargetKind.Ecs when string.IsNullOrWhiteSpace(t.Cloud?.Cluster) => "cluster is required",
        _ => null,
    };

    // Request → target, storing any freshly supplied secret and keeping the old ref when none was sent.
    private static DeployTarget Apply(DeployTarget cur, TargetRequest b, SecretStore secrets)
    {
        var ssh = b.Ssh is null ? cur.Ssh : new TargetSsh(
            b.Ssh.Host ?? cur.Ssh?.Host ?? "",
            b.Ssh.Port ?? cur.Ssh?.Port ?? 22,
            b.Ssh.User ?? cur.Ssh?.User ?? "root",
            secrets.Replace(cur.Ssh?.KeyRef, b.Ssh.PrivateKey, "ssh key"),
            cur.Ssh?.PassphraseRef,
            b.Ssh.HostKey ?? cur.Ssh?.HostKey);

        var tls = b.Tls is null ? cur.Tls : new TargetTls(
            secrets.Replace(cur.Tls?.CaRef, b.Tls.Ca, "docker ca"),
            secrets.Replace(cur.Tls?.CertRef, b.Tls.Cert, "docker cert"),
            secrets.Replace(cur.Tls?.KeyRef, b.Tls.Key, "docker key"));

        var kube = b.Kube is null ? cur.Kube : new TargetKube(
            b.Kube.Context ?? cur.Kube?.Context,
            b.Kube.Namespace ?? cur.Kube?.Namespace,
            secrets.Replace(cur.Kube?.KubeconfigRef, b.Kube.Kubeconfig, "kubeconfig"),
            b.Kube.IngressClass ?? cur.Kube?.IngressClass,
            b.Kube.StorageClass ?? cur.Kube?.StorageClass,
            b.Kube.Expose ?? cur.Kube?.Expose,
            b.Kube.IngressHostPattern ?? cur.Kube?.IngressHostPattern,
            b.Kube.StorageSize ?? cur.Kube?.StorageSize);

        var cloud = b.Cloud is null ? cur.Cloud : new TargetCloud(
            b.Cloud.SubscriptionId ?? cur.Cloud?.SubscriptionId,
            b.Cloud.ResourceGroup ?? cur.Cloud?.ResourceGroup,
            b.Cloud.Location ?? cur.Cloud?.Location,
            b.Cloud.Project ?? cur.Cloud?.Project,
            b.Cloud.Cluster ?? cur.Cloud?.Cluster,
            b.Cloud.Account ?? cur.Cloud?.Account,
            secrets.Replace(cur.Cloud?.CredRef, b.Cloud.Credentials, "cloud credentials"),
            b.Cloud.Environment ?? cur.Cloud?.Environment,
            b.Cloud.Subnets ?? cur.Cloud?.Subnets,
            b.Cloud.SecurityGroups ?? cur.Cloud?.SecurityGroups,
            b.Cloud.ExecutionRoleArn ?? cur.Cloud?.ExecutionRoleArn,
            b.Cloud.AssignPublicIp ?? cur.Cloud?.AssignPublicIp ?? true);

        var registry = b.Registry is null ? cur.Registry : new TargetRegistry(
            b.Registry.Url ?? cur.Registry?.Url,
            b.Registry.User ?? cur.Registry?.User,
            secrets.Replace(cur.Registry?.PasswordRef, b.Registry.Password, "registry password"));

        var domains = b.Domains is null ? cur.Domains : new TargetDomains(
            b.Domains.Kind ?? cur.Domains?.Kind ?? "none",
            b.Domains.Npm is null ? cur.Domains?.Npm : new TargetNpm(
                b.Domains.Npm.BaseUrl ?? cur.Domains?.Npm?.BaseUrl ?? "",
                b.Domains.Npm.Email ?? cur.Domains?.Npm?.Email ?? "",
                secrets.Replace(cur.Domains?.Npm?.PasswordRef, b.Domains.Npm.Password, "npm password"),
                b.Domains.Npm.ForwardHost ?? cur.Domains?.Npm?.ForwardHost ?? ""),
            b.Domains.Azure ?? cur.Domains?.Azure);

        return cur with
        {
            Kind = b.Kind ?? cur.Kind,
            PublicHost = b.PublicHost ?? cur.PublicHost,
            Notes = b.Notes ?? cur.Notes,
            PortFrom = b.PortFrom ?? cur.PortFrom,
            PortTo = b.PortTo ?? cur.PortTo,
            Default = b.Default ?? cur.Default,
            Ssh = ssh, DockerHost = b.DockerHost ?? cur.DockerHost, Tls = tls,
            Kube = kube, Cloud = cloud, Registry = registry, Domains = domains,
        };
    }

    public record SshRequest(string? Host, int? Port, string? User, string? PrivateKey, string? HostKey);
    public record TlsRequest(string? Ca, string? Cert, string? Key);
    public record KubeRequest(string? Context, string? Namespace, string? Kubeconfig, string? IngressClass, string? StorageClass,
        string? Expose = null, string? IngressHostPattern = null, string? StorageSize = null);
    public record CloudRequest(string? SubscriptionId, string? ResourceGroup, string? Location, string? Project,
        string? Cluster, string? Account, string? Credentials, string? Environment,
        string? Subnets = null, string? SecurityGroups = null, string? ExecutionRoleArn = null, bool? AssignPublicIp = null);
    public record RegistryRequest(string? Url, string? User, string? Password);
    public record NpmRequest(string? BaseUrl, string? Email, string? Password, string? ForwardHost);
    public record DomainsRequest(string? Kind, NpmRequest? Npm, TargetAzureDomains? Azure);

    public record TargetRequest(string? Name, string? Kind, string? PublicHost, string? Notes,
        int? PortFrom, int? PortTo, bool? Default,
        SshRequest? Ssh = null, string? DockerHost = null, TlsRequest? Tls = null,
        KubeRequest? Kube = null, CloudRequest? Cloud = null, RegistryRequest? Registry = null,
        DomainsRequest? Domains = null);
}
