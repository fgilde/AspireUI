using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// Turns a target into something that can run commands.
//
// For the compose kinds that means environment for the docker CLI: nothing for local, an ssh alias for
// ssh (docker's own ssh transport, with connection multiplexing so a status poll is cheap), TLS paths
// for a TCP daemon. Key material is written out of the secret store right before use, owner-only.
public class TargetService(TargetStore targets, SecretStore secrets, string workspaceRoot)
{
    private readonly ConcurrentDictionary<string, (string Stamp, DeployService Runner)> _runners = new();

    // For the places that build their own services from the environment (background jobs, MCP tools).
    public static TargetService FromEnvironment()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        var db = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");
        var ws = Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(dataDir, "workspace");
        return new TargetService(new TargetStore(db), new SecretStore(db, dataDir), ws);
    }

    public string TargetDir(string id) => Path.Combine(workspaceRoot, "_targets", id);

    public DeployTarget Resolve(string? id) => targets.Resolve(id);

    // One runner per target, rebuilt when the target changes (host, key, certificates).
    public DeployService Runner(DeployTarget t)
    {
        var stamp = t.UpdatedAt ?? "";
        if (_runners.TryGetValue(t.Id, out var cached) && cached.Stamp == stamp) return cached.Runner;
        var runner = new DeployService(env: EnvironmentFor(t));
        _runners[t.Id] = (stamp, runner);
        return runner;
    }

    public DeployService Runner(string? id) => Runner(Resolve(id));

    public void Invalidate(string id) => _runners.TryRemove(id, out _);

    public IReadOnlyDictionary<string, string> EnvironmentFor(DeployTarget t)
    {
        var env = new Dictionary<string, string>();
        switch (t.Kind)
        {
            case TargetKind.Ssh when t.Ssh is { } ssh:
                env["DOCKER_HOST"] = "ssh://" + SshAlias(t);
                WriteSshConfig(t, ssh);
                // The docker CLI shells out to ssh, which needs to find our per-target config.
                env["GIT_SSH_COMMAND"] = SshCommand(t);
                break;
            case TargetKind.DockerTcp when !string.IsNullOrWhiteSpace(t.DockerHost):
                env["DOCKER_HOST"] = t.DockerHost!;
                if (t.Tls is { } tls && WriteTls(t, tls) is { } certDir)
                {
                    env["DOCKER_TLS_VERIFY"] = "1";
                    env["DOCKER_CERT_PATH"] = certDir;
                }
                break;
        }
        return env;
    }

    public string SshAlias(DeployTarget t) => "aspireui-" + t.Id;

    // ssh reads our generated file only if we tell it to, and docker passes no options of its own.
    private string SshCommand(DeployTarget t) => $"ssh -F \"{Path.Combine(TargetDir(t.Id), "config").Replace('\\', '/')}\"";

    // A private ssh config per target: identity, host key policy and a multiplexed control socket, so
    // `docker compose ps` on a remote box does not pay for a new TCP+auth handshake every few seconds.
    private void WriteSshConfig(DeployTarget t, TargetSsh ssh)
    {
        var dir = TargetDir(t.Id);
        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "id_key");
        if (secrets.Resolve(ssh.KeyRef) is { Length: > 0 } key)
        {
            var normalized = key.Replace("\r\n", "\n").TrimEnd() + "\n";
            if (!File.Exists(keyPath) || File.ReadAllText(keyPath) != normalized)
            {
                File.WriteAllText(keyPath, normalized);
                FileGuard.OwnerOnly(keyPath);
            }
        }
        var known = Path.Combine(dir, "known_hosts");
        if (!string.IsNullOrWhiteSpace(ssh.HostKey))
        {
            var line = ssh.HostKey!.Trim() + "\n";
            if (!File.Exists(known) || File.ReadAllText(known) != line) File.WriteAllText(known, line);
        }
        var sb = new StringBuilder();
        sb.Append("Host ").Append(SshAlias(t)).Append('\n');
        sb.Append("  HostName ").Append(ssh.Host).Append('\n');
        sb.Append("  User ").Append(string.IsNullOrWhiteSpace(ssh.User) ? "root" : ssh.User).Append('\n');
        sb.Append("  Port ").Append(ssh.Port <= 0 ? 22 : ssh.Port).Append('\n');
        sb.Append("  BatchMode yes\n");
        sb.Append("  IdentitiesOnly yes\n");
        if (File.Exists(keyPath)) sb.Append("  IdentityFile \"").Append(keyPath.Replace('\\', '/')).Append("\"\n");
        sb.Append("  UserKnownHostsFile \"").Append(known.Replace('\\', '/')).Append("\"\n");
        // accept-new pins the key on first contact and fails on a later change (unlike "no", which never checks).
        sb.Append("  StrictHostKeyChecking ").Append(File.Exists(known) ? "yes" : "accept-new").Append('\n');
        sb.Append("  ServerAliveInterval 20\n  ConnectTimeout 15\n");
        if (!OperatingSystem.IsWindows())
        {
            sb.Append("  ControlMaster auto\n");
            sb.Append("  ControlPath ").Append(Path.Combine(dir, "cm-%r@%h:%p").Replace('\\', '/')).Append('\n');
            sb.Append("  ControlPersist 120\n");
        }
        var cfg = Path.Combine(dir, "config");
        var text = sb.ToString();
        if (!File.Exists(cfg) || File.ReadAllText(cfg) != text) File.WriteAllText(cfg, text);
        // docker's ssh transport runs plain `ssh <alias>`, which reads the user's config, not ours —
        // so the alias has to exist there too. We keep our own file as the single source and include it.
        EnsureUserSshInclude();
    }

    public void EnsureUserSshInclude()
    {
        // Tests build throwaway targets; they must not edit the developer's own ssh config.
        if (Environment.GetEnvironmentVariable("ASPIREUI_NO_SSH_INCLUDE") == "1") return;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sshDir = Path.Combine(home, ".ssh");
            Directory.CreateDirectory(sshDir);
            var userCfg = Path.Combine(sshDir, "config");
            var glob = Path.Combine(workspaceRoot, "_targets", "*", "config").Replace('\\', '/');
            var include = "Include \"" + glob + "\"";
            var existing = File.Exists(userCfg) ? File.ReadAllText(userCfg) : "";
            if (existing.Contains(include, StringComparison.Ordinal)) return;
            // Include has to come before any Host block to apply globally.
            File.WriteAllText(userCfg, include + "\n" + existing);
        }
        catch { }
    }

    private string? WriteTls(DeployTarget t, TargetTls tls)
    {
        var dir = Path.Combine(TargetDir(t.Id), "certs");
        var files = new (string Name, string? Ref)[] { ("ca.pem", tls.CaRef), ("cert.pem", tls.CertRef), ("key.pem", tls.KeyRef) };
        if (files.Any(f => string.IsNullOrEmpty(f.Ref))) return null;
        Directory.CreateDirectory(dir);
        foreach (var (name, r) in files)
        {
            var pem = secrets.Resolve(r);
            if (string.IsNullOrWhiteSpace(pem)) return null;
            var path = Path.Combine(dir, name);
            var text = pem.Replace("\r\n", "\n").TrimEnd() + "\n";
            if (!File.Exists(path) || File.ReadAllText(path) != text) { File.WriteAllText(path, text); FileGuard.OwnerOnly(path); }
        }
        return dir;
    }

    // Asks the target what it is. Also the connection test the UI runs before saving a target.
    public DeployTarget Probe(DeployTarget t)
    {
        var probe = ProbeOnly(t);
        var saved = targets.Get(t.Id);
        if (saved is not null) targets.Upsert(saved with { Probe = probe });
        return targets.Get(t.Id) ?? t with { Probe = probe };
    }

    public TargetProbe ProbeOnly(DeployTarget t)
    {
        var now = DateTime.UtcNow.ToString("O");
        try
        {
            if (TargetKind.IsCompose(t.Kind))
            {
                var runner = t.Id == DeployTarget.LocalId && _runners.IsEmpty
                    ? new DeployService(env: EnvironmentFor(t)) : Runner(t);
                var v = runner.Version();
                if (!v.Ok) return new TargetProbe(false, FirstLine(v.Log), CheckedAt: now);
                string? server = null;
                try
                {
                    using var doc = JsonDocument.Parse(v.Log);
                    if (doc.RootElement.TryGetProperty("Server", out var s) && s.TryGetProperty("Version", out var sv))
                        server = sv.GetString();
                    if (server is null && doc.RootElement.TryGetProperty("Client", out var c) && c.TryGetProperty("Version", out var cv))
                        server = cv.GetString();
                }
                catch { }
                var info = runner.Info("{{.OperatingSystem}}|{{.Architecture}}|{{.ServerVersion}}|{{.ID}}");
                var parts = (info.Ok ? info.Log : "").Split('|');
                var compose = runner.ComposeVersion();
                return new TargetProbe(true, null,
                    Version: server ?? (parts.Length > 2 ? parts[2] : null),
                    Compose: compose.Ok ? compose.Log.Trim() : null,
                    Arch: parts.Length > 1 ? Normalize(parts[1]) : null,
                    Os: parts.Length > 0 ? parts[0].Trim() : null,
                    DiskFreeMb: DiskFreeMb(runner),
                    CheckedAt: now,
                    DaemonId: parts.Length > 3 ? parts[3].Trim() : null);
            }
            if (t.Kind == TargetKind.K8s)
            {
                var r = Cli.Run("kubectl", KubeArgs(t, ["version", "-o", "json"]), env: KubeEnv(t));
                if (!r.Ok) return new TargetProbe(false, FirstLine(r.Log), CheckedAt: now);
                var nodes = Cli.Run("kubectl", KubeArgs(t, ["get", "nodes", "-o", "jsonpath={.items[0].status.nodeInfo.architecture}"]), env: KubeEnv(t));
                var helm = Cli.Run("helm", ["version", "--short"]);
                return new TargetProbe(true, null, Version: ServerGitVersion(r.Log), Compose: helm.Ok ? "helm " + helm.Log.Trim() : null,
                    Arch: nodes.Ok ? Normalize(nodes.Log) : null, Os: "kubernetes", CheckedAt: now);
            }
            var cloud = CloudCli.Probe(t, secrets);
            return cloud with { CheckedAt = now };
        }
        catch (Exception e) { return new TargetProbe(false, e.Message, CheckedAt: now); }
    }

    private static string? ServerGitVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("serverVersion", out var s) && s.TryGetProperty("gitVersion", out var g)
                ? g.GetString() : null;
        }
        catch { return null; }
    }

    private static long? DiskFreeMb(DeployService runner)
    {
        // Asking in a container is the only way that also works for a daemon on another machine.
        var r = runner.Docker("", "run --rm alpine df -Pk /");
        if (!r.Ok) return null;
        var line = r.Log.Split('\n').LastOrDefault(l => l.Trim().Length > 0);
        var cols = (line ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return cols.Length >= 4 && long.TryParse(cols[3], out var kb) ? kb / 1024 : null;
    }

    public static string[] KubeArgs(DeployTarget t, string[] args)
    {
        var list = new List<string>();
        if (t.Kube?.Context is { Length: > 0 } ctx) { list.Add("--context"); list.Add(ctx); }
        if (t.Kube?.Namespace is { Length: > 0 } ns) { list.Add("-n"); list.Add(ns); }
        list.AddRange(args);
        return list.ToArray();
    }

    public IReadOnlyDictionary<string, string> KubeEnv(DeployTarget t)
    {
        var env = new Dictionary<string, string>();
        if (t.Kube?.KubeconfigRef is { Length: > 0 } r && secrets.Resolve(r) is { Length: > 0 } cfg)
        {
            var dir = TargetDir(t.Id);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "kubeconfig");
            var text = cfg.Replace("\r\n", "\n");
            if (!File.Exists(path) || File.ReadAllText(path) != text) { File.WriteAllText(path, text); FileGuard.OwnerOnly(path); }
            env["KUBECONFIG"] = path;
        }
        return env;
    }

    private static string Normalize(string arch) => arch.Trim().ToLowerInvariant() switch
    {
        "x86_64" or "amd64" => "amd64",
        "aarch64" or "arm64" => "arm64",
        var other => other,
    };

    private static string FirstLine(string s) =>
        s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "failed";

    // Which docker daemon a target really talks to. Two targets can point at the same machine (local and
    // an ssh alias for it, say), and moving an app "between" them would tear down what it just started.
    public string? DaemonId(DeployTarget t)
    {
        if (!TargetKind.IsCompose(t.Kind)) return null;
        if (t.Probe?.DaemonId is { Length: > 0 } known) return known;
        var r = Runner(t).Info("{{.ID}}");
        return r.Ok && r.Log.Trim() is { Length: > 0 } id ? id : null;
    }

    // Host ports already in use on the target, read from the daemon itself.
    public ISet<int> UsedPortsOn(DeployTarget t)
    {
        if (!TargetKind.IsCompose(t.Kind)) return new HashSet<int>();
        var r = Runner(t).UsedPorts();
        return r.Ok ? ParsePublishedPorts(r.Log) : new HashSet<int>();
    }

    // `docker ps --format {{.Ports}}` lines: "0.0.0.0:20001->3000/tcp, [::]:20001->3000/tcp".
    public static ISet<int> ParsePublishedPorts(string psOutput)
    {
        var used = new HashSet<int>();
        foreach (Match m in Regex.Matches(psOutput ?? "", @"(?:^|,|\s)(?:[\d.:\[\]a-fA-F]*:)?(\d+)->"))
            if (int.TryParse(m.Groups[1].Value, out var p)) used.Add(p);
        return used;
    }
}
