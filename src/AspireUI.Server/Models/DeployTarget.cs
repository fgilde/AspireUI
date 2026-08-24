namespace AspireUI.Server.Models;

// Where a stack or app can be deployed. "local" is this machine's docker: it always exists, is never
// deletable, and every deployment made before targets existed belongs to it (TargetId null == local).
//
// Kinds:
//   local     — the docker daemon this process talks to
//   ssh       — a docker daemon over ssh (DOCKER_HOST=ssh://…): any VM, NAS or bare-metal box
//   dockerTcp — a docker daemon over TCP, mTLS certificates
//   k8s       — a Kubernetes cluster, deployed with helm and driven with kubectl
//   aca       — Azure Container Apps, via the az CLI
//   cloudrun  — Google Cloud Run, via the gcloud CLI
//   ecs       — AWS ECS/Fargate, via the aws CLI
public static class TargetKind
{
    public const string Local = "local";
    public const string Ssh = "ssh";
    public const string DockerTcp = "dockerTcp";
    public const string K8s = "k8s";
    public const string Aca = "aca";
    public const string CloudRun = "cloudrun";
    public const string Ecs = "ecs";

    public static readonly string[] All = [Local, Ssh, DockerTcp, K8s, Aca, CloudRun, Ecs];

    // Compose kinds run docker compose, so they get the full hosting feature set.
    public static bool IsCompose(string kind) => kind is Local or Ssh or DockerTcp;
    // Orchestrator kinds have no docker socket: no volume browser, no compose terminal, no host ports.
    public static bool IsOrchestrator(string kind) => !IsCompose(kind);
}

// Key auth only, on purpose: docker's own ssh transport shells out to ssh, and a password prompt has
// nowhere to go. The wizard can generate the pair and show the public half to paste into the box.
public record TargetSsh(string Host, int Port, string User, string? KeyRef = null, string? PassphraseRef = null,
    string? HostKey = null);

// mTLS material for a TCP daemon; each field a secret ref holding the PEM.
public record TargetTls(string? CaRef, string? CertRef, string? KeyRef);

// Set when AspireUI created the machine itself, so it can also destroy it again.
public record TargetProvider(string Kind, string? CredRef = null, string? Region = null, string? ServerId = null,
    string? ServerType = null, string? Image = null, string? SshKeyId = null);

public record TargetRegistry(string? Url, string? User = null, string? PasswordRef = null);

// How this target publishes a domain. "npm" is a Nginx Proxy Manager instance (what local has used all
// along), "azure" binds a custom domain on a Container Apps environment, "manual" means the user points
// DNS at the target themselves and we only show what to point where.
public record TargetDomains(string Kind = "none", TargetNpm? Npm = null, TargetAzureDomains? Azure = null);
public record TargetNpm(string BaseUrl, string Email, string? PasswordRef, string ForwardHost);
public record TargetAzureDomains(string? ResourceGroup, string? Environment, string? SubscriptionId);

// How a Helm release is made reachable and persistent. The chart `aspire publish` produces has
// ClusterIP services, no Ingress and emptyDir volumes — these settings are what AspireUI adds on top.
//   Expose: clusterip (nothing, internal only) | nodeport | loadbalancer | ingress
//   IngressHostPattern: {app} and {service} are substituted, e.g. "{service}.apps.example.com"
//   StorageClass: set it and emptyDir volumes become PersistentVolumeClaims of that class
public record TargetKube(string? Context = null, string? Namespace = null, string? KubeconfigRef = null,
    string? IngressClass = null, string? StorageClass = null,
    string? Expose = null, string? IngressHostPattern = null, string? StorageSize = null);

public record TargetCloud(string? SubscriptionId = null, string? ResourceGroup = null, string? Location = null,
    string? Project = null, string? Cluster = null, string? Account = null, string? CredRef = null,
    string? Environment = null,
    // ECS needs the network it runs in and a role that may pull images and write logs.
    string? Subnets = null, string? SecurityGroups = null, string? ExecutionRoleArn = null, bool AssignPublicIp = true);

public record TargetProbe(bool Ok, string? Error = null, string? Version = null, string? Compose = null,
    string? Arch = null, string? Os = null, long? DiskFreeMb = null, string? CheckedAt = null,
    // The daemon's own id: two targets with the same one are the same machine, whatever they are called.
    string? DaemonId = null);

public record DeployTarget(
    string Id,
    string Name,
    string Kind,
    bool Default = false,
    string? PublicHost = null,
    TargetSsh? Ssh = null,
    string? DockerHost = null,
    TargetTls? Tls = null,
    TargetKube? Kube = null,
    TargetCloud? Cloud = null,
    TargetProvider? Provider = null,
    TargetRegistry? Registry = null,
    TargetDomains? Domains = null,
    int PortFrom = 20000,
    int PortTo = 29999,
    TargetProbe? Probe = null,
    string? Notes = null,
    string? CreatedAt = null,
    string? UpdatedAt = null)
{
    public const string LocalId = "local";
    public bool IsLocal => Id == LocalId;

    // Host that app URLs are built from: what the user set, else the ssh/tcp host, else this machine.
    public string HostForUrls() =>
        !string.IsNullOrWhiteSpace(PublicHost) ? PublicHost!.Trim()
        : Ssh is { Host: { Length: > 0 } h } ? h
        : DockerHost is { Length: > 0 } dh && Uri.TryCreate(dh, UriKind.Absolute, out var u) ? u.Host
        : "localhost";
}
