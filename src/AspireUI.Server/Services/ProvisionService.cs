using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record ProvisionProvider(string Kind, string Label, string Auth, string DefaultRegion, string DefaultSize,
    string DefaultImage, string DefaultUser, string[] Regions, string[] Sizes, string Docs);

public record ProvisionRequest(string Provider, string Name, string? Credentials, string? Region, string? Size,
    string? Image, string? SshUser, string? ResourceGroup, string? Project, string? Zone, bool MakeDefault = false);

public record ProvisionResult(bool Ok, string Log, string? TargetId = null, string? Host = null);

public record KeyPair(string PrivateKey, string PublicKey);

// Creates a machine at a provider, installs docker on it and registers it as an ssh target. Everything
// after creation is provider-agnostic: the box is then just "docker over ssh" like any other.
//
// The REST providers (Hetzner, Linode, DigitalOcean) are called directly — one endpoint each, an API
// token is all they need. The three hyperscalers are driven through their own CLI, because their auth
// and image plumbing is a world of its own and their CLI already solves it.
public class ProvisionService(TargetStore targets, TargetService svc, SecretStore secrets)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static readonly ProvisionProvider[] Providers =
    [
        new("hetzner", "Hetzner Cloud", "token", "nbg1", "cx22", "ubuntu-24.04", "root",
            ["nbg1", "fsn1", "hel1", "ash", "hil", "sin"], ["cx22", "cx32", "cx42", "cax11", "cax21", "cax31"],
            "https://console.hetzner.cloud/projects → Security → API tokens (read & write)"),
        new("digitalocean", "DigitalOcean", "token", "fra1", "s-1vcpu-2gb", "ubuntu-24-04-x64", "root",
            ["fra1", "nyc3", "ams3", "lon1", "sfo3", "sgp1", "syd1"], ["s-1vcpu-1gb", "s-1vcpu-2gb", "s-2vcpu-4gb", "s-4vcpu-8gb"],
            "https://cloud.digitalocean.com/account/api/tokens (write scope)"),
        new("linode", "Akamai Linode", "token", "eu-central", "g6-standard-1", "linode/ubuntu24.04", "root",
            ["eu-central", "de-fra-2", "us-east", "us-ord", "ap-south"], ["g6-nanode-1", "g6-standard-1", "g6-standard-2", "g6-standard-4"],
            "https://cloud.linode.com/profile/tokens (Linodes: read/write)"),
        new("azure", "Azure VM", "cli", "westeurope", "Standard_B2s", "Ubuntu2404", "azureuser",
            ["westeurope", "northeurope", "germanywestcentral", "eastus", "westus3"], ["Standard_B1ms", "Standard_B2s", "Standard_B2ms", "Standard_D2s_v5"],
            "az login, or a service principal as tenant:appId:secret"),
        new("aws", "AWS EC2", "cli", "eu-central-1", "t3.small", "ubuntu-24.04", "ubuntu",
            ["eu-central-1", "eu-west-1", "us-east-1", "us-west-2", "ap-southeast-1"], ["t3.micro", "t3.small", "t3.medium", "t4g.small"],
            "aws configure, or accessKeyId:secretAccessKey"),
        new("gcp", "Google Compute Engine", "cli", "europe-west3-a", "e2-small", "ubuntu-2404-lts", "ubuntu",
            ["europe-west3-a", "europe-west1-b", "us-central1-a", "us-east1-b", "asia-southeast1-a"], ["e2-micro", "e2-small", "e2-medium", "n2-standard-2"],
            "gcloud auth login, or a service-account key JSON"),
    ];

    // cloud-init: docker from the official convenience script, which is what every one of these images wants.
    private const string CloudInit = """
        #cloud-config
        package_update: true
        runcmd:
          - [ sh, -c, "curl -fsSL https://get.docker.com | sh" ]
          - [ sh, -c, "systemctl enable --now docker" ]
        """;

    public static KeyPair? GenerateKey(string comment = "aspireui")
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-keygen-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "id_ed25519");
        try
        {
            var r = Cli.Run("ssh-keygen", ["-t", "ed25519", "-N", "", "-C", comment, "-f", path], timeoutMs: 30_000);
            if (!r.Ok || !File.Exists(path)) return null;
            return new KeyPair(File.ReadAllText(path), File.ReadAllText(path + ".pub").Trim());
        }
        catch { return null; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    public async Task<ProvisionResult> CreateAsync(ProvisionRequest req)
    {
        var log = new StringBuilder();
        var p = Providers.FirstOrDefault(x => x.Kind == req.Provider);
        if (p is null) return new ProvisionResult(false, $"unknown provider '{req.Provider}'");
        if (string.IsNullOrWhiteSpace(req.Name)) return new ProvisionResult(false, "name is required");
        if (p.Auth == "token" && string.IsNullOrWhiteSpace(req.Credentials))
            return new ProvisionResult(false, $"an API token is required — {p.Docs}");

        var key = GenerateKey("aspireui-" + TargetStore.Slug(req.Name));
        if (key is null) return new ProvisionResult(false, "could not generate an ssh key — is ssh-keygen installed?");
        log.AppendLine("generated an ed25519 key pair for this machine");

        var region = Blank(req.Region) ?? p.DefaultRegion;
        var size = Blank(req.Size) ?? p.DefaultSize;
        var image = Blank(req.Image) ?? p.DefaultImage;
        var user = Blank(req.SshUser) ?? p.DefaultUser;
        var name = TargetStore.Slug(req.Name);

        (string? Ip, string? ServerId, string? KeyId, string Log) created;
        try
        {
            created = p.Kind switch
            {
                "hetzner" => await HetznerAsync(req, name, region, size, image, key.PublicKey),
                "digitalocean" => await DigitalOceanAsync(req, name, region, size, image, key.PublicKey),
                "linode" => await LinodeAsync(req, name, region, size, image, key.PublicKey),
                "azure" => AzureVm(req, name, region, size, image, user, key.PublicKey),
                "aws" => AwsVm(req, name, region, size, key.PublicKey),
                "gcp" => GcpVm(req, name, region, size, image, user, key.PublicKey),
                _ => (null, null, null, "unsupported"),
            };
        }
        catch (Exception e) { return new ProvisionResult(false, log + "\n" + e.Message); }

        log.Append(created.Log);
        if (string.IsNullOrWhiteSpace(created.Ip))
            return new ProvisionResult(false, log.ToString());
        log.AppendLine($"machine is up at {created.Ip}");

        var id = targets.UniqueId(req.Name);
        var keyRef = secrets.Put(key.PrivateKey, $"ssh key for {req.Name}");
        var credRef = p.Auth == "token" ? secrets.Put(req.Credentials, $"{p.Label} token") : null;
        var target = targets.Upsert(new DeployTarget(id, req.Name.Trim(), TargetKind.Ssh,
            Default: req.MakeDefault,
            PublicHost: created.Ip,
            Ssh: new TargetSsh(created.Ip!, 22, user, keyRef),
            Provider: new TargetProvider(p.Kind, credRef, region, created.ServerId, size, image, created.KeyId),
            Domains: new TargetDomains("manual")));
        svc.Invalidate(target.Id);

        // cloud-init is still working while we get here; wait for the daemon rather than failing early.
        var wait = WaitForDocker(target, TimeSpan.FromMinutes(5));
        log.Append(wait.Log);
        if (!wait.Ok)
        {
            var install = InstallDocker(target);
            log.Append(install.Log);
            wait = WaitForDocker(target, TimeSpan.FromMinutes(2));
            log.Append(wait.Log);
        }
        var probed = svc.Probe(targets.Get(target.Id)!);
        return new ProvisionResult(probed.Probe?.Ok == true, log.ToString(), target.Id, created.Ip);
    }

    public DeployResult InstallDocker(DeployTarget t)
    {
        if (t.Kind != TargetKind.Ssh || t.Ssh is null) return new DeployResult(false, "only an ssh target can be set up from here");
        svc.EnvironmentFor(t);   // makes sure the ssh config and key file exist
        var cfg = Path.Combine(svc.TargetDir(t.Id), "config");
        var r = Cli.Run("ssh", ["-F", cfg, svc.SshAlias(t),
            "command -v docker >/dev/null 2>&1 || curl -fsSL https://get.docker.com | sh; sudo systemctl enable --now docker 2>/dev/null || systemctl enable --now docker"],
            timeoutMs: 600_000);
        return new DeployResult(r.Ok, "installing docker over ssh:\n" + r.Log + "\n");
    }

    public DeployResult WaitForDocker(DeployTarget t, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        DeployResult last = new(false, "");
        var tries = 0;
        while (DateTime.UtcNow < deadline)
        {
            tries++;
            last = svc.Runner(t).Version();
            if (last.Ok) return new DeployResult(true, $"docker answered after {tries} attempt(s)\n");
            Thread.Sleep(10_000);
        }
        return new DeployResult(false, $"docker did not answer within {budget.TotalMinutes:0} minute(s): {last.Log}\n");
    }

    public async Task<ProvisionResult> DestroyAsync(DeployTarget t)
    {
        if (t.Provider is not { } prov || string.IsNullOrWhiteSpace(prov.ServerId))
            return new ProvisionResult(false, "this target was not created by AspireUI, so there is no machine to destroy");
        var token = secrets.Resolve(prov.CredRef);
        var log = new StringBuilder();
        try
        {
            switch (prov.Kind)
            {
                case "hetzner":
                    log.Append(await SendAsync(HttpMethod.Delete, $"https://api.hetzner.cloud/v1/servers/{prov.ServerId}", token));
                    if (prov.SshKeyId is { Length: > 0 } hk)
                        log.Append(await SendAsync(HttpMethod.Delete, $"https://api.hetzner.cloud/v1/ssh_keys/{hk}", token));
                    break;
                case "digitalocean":
                    log.Append(await SendAsync(HttpMethod.Delete, $"https://api.digitalocean.com/v2/droplets/{prov.ServerId}", token));
                    if (prov.SshKeyId is { Length: > 0 } dk)
                        log.Append(await SendAsync(HttpMethod.Delete, $"https://api.digitalocean.com/v2/account/keys/{dk}", token));
                    break;
                case "linode":
                    log.Append(await SendAsync(HttpMethod.Delete, $"https://api.linode.com/v4/linode/instances/{prov.ServerId}", token));
                    break;
                case "azure":
                    log.Append(Cli.Run("az", ["vm", "delete", "--yes", "-g", t.Cloud?.ResourceGroup ?? prov.Region ?? "", "-n", prov.ServerId!],
                        CloudCli.EnvFor(t with { Kind = TargetKind.Aca }, secrets), timeoutMs: 600_000).Log);
                    break;
                case "aws":
                    log.Append(Cli.Run("aws", ["ec2", "terminate-instances", "--instance-ids", prov.ServerId!, "--region", prov.Region ?? "", "--output", "json"],
                        CloudCli.EnvFor(t with { Kind = TargetKind.Ecs }, secrets), timeoutMs: 300_000).Log);
                    break;
                case "gcp":
                    log.Append(Cli.Run("gcloud", ["compute", "instances", "delete", prov.ServerId!, "--zone", prov.Region ?? "", "--quiet"],
                        CloudCli.EnvFor(t with { Kind = TargetKind.CloudRun }, secrets), timeoutMs: 600_000).Log);
                    break;
                default:
                    return new ProvisionResult(false, $"cannot destroy a '{prov.Kind}' machine");
            }
        }
        catch (Exception e) { return new ProvisionResult(false, log + "\n" + e.Message); }
        targets.Delete(t.Id);
        secrets.Delete(t.Ssh?.KeyRef);
        secrets.Delete(prov.CredRef);
        svc.Invalidate(t.Id);
        return new ProvisionResult(true, log.ToString());
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static async Task<string> SendAsync(HttpMethod m, string url, string? token, object? body = null)
    {
        using var req = new HttpRequestMessage(m, url);
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return $"{m} {url} → {(int)res.StatusCode}\n";
    }

    private static async Task<(JsonNode? Body, int Status, string Log)> ApiAsync(HttpMethod m, string url, string? token, object? body = null)
    {
        using var req = new HttpRequestMessage(m, url);
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        var log = $"{m} {url} → {(int)res.StatusCode}" + (res.IsSuccessStatusCode ? "\n" : $": {Shorten(text)}\n");
        JsonNode? node = null;
        try { node = JsonNode.Parse(text); } catch { }
        return (node, (int)res.StatusCode, log);
    }

    private static string Shorten(string s) => s.Length > 400 ? s[..400] + "…" : s;

    private async Task<(string? Ip, string? ServerId, string? KeyId, string Log)> HetznerAsync(
        ProvisionRequest req, string name, string region, string size, string image, string pubKey)
    {
        var log = new StringBuilder();
        var (keyBody, _, keyLog) = await ApiAsync(HttpMethod.Post, "https://api.hetzner.cloud/v1/ssh_keys", req.Credentials,
            new { name = $"aspireui-{name}-{DateTime.UtcNow:yyyyMMddHHmmss}", public_key = pubKey });
        log.Append(keyLog);
        var keyId = keyBody?["ssh_key"]?["id"]?.ToString();
        if (keyId is null) return (null, null, null, log + "could not register the ssh key\n");

        var (body, _, createLog) = await ApiAsync(HttpMethod.Post, "https://api.hetzner.cloud/v1/servers", req.Credentials,
            new { name, server_type = size, image, location = region, ssh_keys = new[] { keyId }, user_data = CloudInit, start_after_create = true });
        log.Append(createLog);
        var id = body?["server"]?["id"]?.ToString();
        if (id is null) return (null, null, keyId, log.ToString());
        var ip = body?["server"]?["public_net"]?["ipv4"]?["ip"]?.ToString();
        for (var i = 0; i < 30 && string.IsNullOrWhiteSpace(ip); i++)
        {
            await Task.Delay(5000);
            var (poll, _, _) = await ApiAsync(HttpMethod.Get, $"https://api.hetzner.cloud/v1/servers/{id}", req.Credentials);
            ip = poll?["server"]?["public_net"]?["ipv4"]?["ip"]?.ToString();
        }
        return (ip, id, keyId, log.ToString());
    }

    private async Task<(string? Ip, string? ServerId, string? KeyId, string Log)> DigitalOceanAsync(
        ProvisionRequest req, string name, string region, string size, string image, string pubKey)
    {
        var log = new StringBuilder();
        var (keyBody, _, keyLog) = await ApiAsync(HttpMethod.Post, "https://api.digitalocean.com/v2/account/keys", req.Credentials,
            new { name = $"aspireui-{name}-{DateTime.UtcNow:yyyyMMddHHmmss}", public_key = pubKey });
        log.Append(keyLog);
        var keyId = keyBody?["ssh_key"]?["id"]?.ToString();
        if (keyId is null) return (null, null, null, log + "could not register the ssh key\n");

        var (body, _, createLog) = await ApiAsync(HttpMethod.Post, "https://api.digitalocean.com/v2/droplets", req.Credentials,
            new { name, region, size, image, ssh_keys = new[] { keyId }, user_data = CloudInit, monitoring = true });
        log.Append(createLog);
        var id = body?["droplet"]?["id"]?.ToString();
        if (id is null) return (null, null, keyId, log.ToString());
        string? ip = null;
        for (var i = 0; i < 40 && string.IsNullOrWhiteSpace(ip); i++)
        {
            await Task.Delay(5000);
            var (poll, _, _) = await ApiAsync(HttpMethod.Get, $"https://api.digitalocean.com/v2/droplets/{id}", req.Credentials);
            ip = poll?["droplet"]?["networks"]?["v4"]?.AsArray()
                .FirstOrDefault(n => n?["type"]?.ToString() == "public")?["ip_address"]?.ToString();
        }
        return (ip, id, keyId, log.ToString());
    }

    private async Task<(string? Ip, string? ServerId, string? KeyId, string Log)> LinodeAsync(
        ProvisionRequest req, string name, string region, string size, string image, string pubKey)
    {
        var log = new StringBuilder();
        var rootPass = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var (body, _, createLog) = await ApiAsync(HttpMethod.Post, "https://api.linode.com/v4/linode/instances", req.Credentials,
            new { label = name, region, type = size, image, root_pass = rootPass, authorized_keys = new[] { pubKey },
                  metadata = new { user_data = Convert.ToBase64String(Encoding.UTF8.GetBytes(CloudInit)) } });
        log.Append(createLog);
        var id = body?["id"]?.ToString();
        if (id is null)
        {
            // Not every Linode region carries the metadata service; retry without cloud-init and install over ssh.
            var (retry, _, retryLog) = await ApiAsync(HttpMethod.Post, "https://api.linode.com/v4/linode/instances", req.Credentials,
                new { label = name, region, type = size, image, root_pass = rootPass, authorized_keys = new[] { pubKey } });
            log.Append(retryLog);
            id = retry?["id"]?.ToString();
            body = retry;
            if (id is null) return (null, null, null, log.ToString());
        }
        var ip = body?["ipv4"]?.AsArray().FirstOrDefault()?.ToString();
        for (var i = 0; i < 40 && string.IsNullOrWhiteSpace(ip); i++)
        {
            await Task.Delay(5000);
            var (poll, _, _) = await ApiAsync(HttpMethod.Get, $"https://api.linode.com/v4/linode/instances/{id}", req.Credentials);
            ip = poll?["ipv4"]?.AsArray().FirstOrDefault()?.ToString();
        }
        return (ip, id, null, log.ToString());
    }

    // The hyperscalers: their own CLI, their own login. We write the cloud-init file and read the IP back.
    private (string? Ip, string? ServerId, string? KeyId, string Log) AzureVm(
        ProvisionRequest req, string name, string region, string size, string image, string user, string pubKey)
    {
        var env = CloudCli.EnvFor(new DeployTarget("probe", name, TargetKind.Aca,
            Cloud: new TargetCloud(CredRef: secrets.Put(req.Credentials, "azure credentials"), Location: region)), secrets);
        var rg = Blank(req.ResourceGroup) ?? $"aspireui-{name}";
        var log = new StringBuilder();
        log.Append(Cli.Run("az", ["group", "create", "-n", rg, "-l", region, "-o", "none"], env, timeoutMs: 300_000).Log);
        var init = TempFile(CloudInit);
        var pub = TempFile(pubKey);
        var create = Cli.Run("az", ["vm", "create", "-g", rg, "-n", name, "--image", image, "--size", size,
            "--admin-username", user, "--ssh-key-values", pub, "--custom-data", init, "--public-ip-sku", "Standard", "-o", "json"],
            env, timeoutMs: 900_000);
        log.Append(create.Log.Length > 800 ? "az vm create finished\n" : create.Log);
        if (!create.Ok) return (null, null, null, log.ToString());
        string? ip = null;
        try
        {
            using var doc = JsonDocument.Parse(create.Log);
            ip = doc.RootElement.TryGetProperty("publicIpAddress", out var v) ? v.GetString() : null;
        }
        catch { }
        return (ip, name, null, log.ToString());
    }

    private (string? Ip, string? ServerId, string? KeyId, string Log) AwsVm(
        ProvisionRequest req, string name, string region, string size, string pubKey)
    {
        var env = CloudCli.EnvFor(new DeployTarget("probe", name, TargetKind.Ecs,
            Cloud: new TargetCloud(CredRef: secrets.Put(req.Credentials, "aws credentials"), Location: region)), secrets);
        var log = new StringBuilder();
        // The current Ubuntu LTS AMI for this region, straight from Canonical's public SSM parameter.
        var ami = Cli.Run("aws", ["ssm", "get-parameter", "--region", region, "--output", "text", "--query", "Parameter.Value",
            "--name", "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id"], env);
        log.Append(ami.Ok ? $"ami: {ami.Log.Trim()}\n" : ami.Log);
        if (!ami.Ok) return (null, null, null, log.ToString());
        var keyName = $"aspireui-{name}";
        log.Append(Cli.Run("aws", ["ec2", "import-key-pair", "--region", region, "--key-name", keyName,
            "--public-key-material", "fileb://" + TempFile(pubKey), "--output", "json"], env).Log);
        var run = Cli.Run("aws", ["ec2", "run-instances", "--region", region, "--image-id", ami.Log.Trim(),
            "--instance-type", size, "--key-name", keyName, "--user-data", "file://" + TempFile(CloudInit),
            "--tag-specifications", $"ResourceType=instance,Tags=[{{Key=Name,Value={name}}}]", "--output", "json"], env, timeoutMs: 300_000);
        if (!run.Ok) return (null, null, null, log + run.Log);
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(run.Log);
            id = doc.RootElement.GetProperty("Instances")[0].GetProperty("InstanceId").GetString();
        }
        catch { }
        if (id is null) return (null, null, null, log + "could not read the instance id\n");
        log.AppendLine($"instance {id} starting");
        string? ip = null;
        for (var i = 0; i < 40 && string.IsNullOrWhiteSpace(ip); i++)
        {
            Thread.Sleep(5000);
            var d = Cli.Run("aws", ["ec2", "describe-instances", "--region", region, "--instance-ids", id,
                "--query", "Reservations[0].Instances[0].PublicIpAddress", "--output", "text"], env);
            ip = d.Ok && d.Log.Trim() is { Length: > 0 } s && s != "None" ? s.Trim() : null;
        }
        return (ip, id, keyName, log.ToString());
    }

    private (string? Ip, string? ServerId, string? KeyId, string Log) GcpVm(
        ProvisionRequest req, string name, string zone, string size, string image, string user, string pubKey)
    {
        var env = CloudCli.EnvFor(new DeployTarget("probe", name, TargetKind.CloudRun,
            Cloud: new TargetCloud(CredRef: secrets.Put(req.Credentials, "gcp credentials"), Project: req.Project, Location: zone)), secrets);
        var log = new StringBuilder();
        var keys = TempFile($"{user}:{pubKey}");
        var init = TempFile(CloudInit);
        var create = Cli.Run("gcloud", ["compute", "instances", "create", name, "--zone", zone, "--machine-type", size,
            "--image-family", image, "--image-project", "ubuntu-os-cloud", "--metadata-from-file",
            $"ssh-keys={keys},user-data={init}", "--format", "json"], env, timeoutMs: 900_000);
        log.Append(create.Ok ? "gcloud compute instances create finished\n" : create.Log);
        if (!create.Ok) return (null, null, null, log.ToString());
        string? ip = null;
        try
        {
            using var doc = JsonDocument.Parse(create.Log);
            ip = doc.RootElement[0].GetProperty("networkInterfaces")[0].GetProperty("accessConfigs")[0]
                .GetProperty("natIP").GetString();
        }
        catch { }
        return (ip, name, null, log.ToString());
    }

    private static string TempFile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-provision");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("n")[..8] + ".txt");
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return path;
    }
}
