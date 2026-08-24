using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// Targets without a docker socket. A Kubernetes target takes the Helm chart that `aspire publish`
// already produces; the managed platforms (Container Apps, Cloud Run, ECS) take the compose output and
// create one service per container through their own CLI.
//
// What these targets cannot do, and the UI says so: no volume browser, no compose terminal, no host
// port mapping. Status, logs, a shell into a running instance, start/stop and removal all work.
public class OrchestratorService(DeploymentStore store, PublishService publish, TargetService targets, SecretStore secrets)
{
    public Deployment Deploy(StackModel stack, string publishRoot, string id, DeployTarget target, string? cloneSrc)
    {
        try
        {
            return target.Kind switch
            {
                TargetKind.K8s => DeployHelm(stack, publishRoot, id, target, cloneSrc),
                TargetKind.Aca => ManagedDeploy.Aca(this, stack, publishRoot, id, target, cloneSrc),
                TargetKind.CloudRun => ManagedDeploy.CloudRun(this, stack, publishRoot, id, target, cloneSrc),
                TargetKind.Ecs => ManagedDeploy.Ecs(this, stack, publishRoot, id, target, cloneSrc),
                _ => Fail(id, $"target kind '{target.Kind}' cannot be deployed"),
            };
        }
        catch (Exception e) { return Fail(id, e.Message); }
    }

    internal Deployment Fail(string id, string message)
    {
        store.SetState(id, "failed", message);
        return store.Get(id)!;
    }

    internal Deployment Save(string id, bool ok, List<string> urls, string log, string? health = null, string? detail = null)
    {
        var d = store.Get(id)!;
        store.Upsert(d with
        {
            State = ok ? "running" : "failed",
            Urls = urls,
            LastError = ok ? null : Tail(log),
            Health = health ?? (ok ? "ok" : "failing"),
            HealthDetail = detail,
            UpdatedAt = DateTime.UtcNow.ToString("O"),
        });
        return store.Get(id)!;
    }

    internal static string Tail(string log, int lines = 40)
    {
        var all = (log ?? "").Split('\n');
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }

    internal string Release(Deployment d) => "aspireui-" + d.StackId[..Math.Min(8, d.StackId.Length)];
    internal SecretStore Secrets => secrets;
    internal TargetService Targets => targets;
    internal DeploymentStore Store => store;
    internal PublishService Publisher => publish;

    // ---------- Kubernetes ----------

    private Deployment DeployHelm(StackModel stack, string publishRoot, string id, DeployTarget target, string? cloneSrc)
    {
        var log = new StringBuilder();
        var pub = publish.Publish(stack, publishRoot, "kubernetes", cloneSrc);
        log.Append(pub.Log);
        if (!pub.Ok) return Fail(id, Tail(log.ToString()));
        var chart = FindChart(pub.OutputDir);
        if (chart is null) return Fail(id, "the published output has no Helm chart (Chart.yaml)");
        // The generated chart keeps every volume in an emptyDir, which a pod restart empties. With a
        // storage class on the target they become real claims instead.
        if (target.Kube?.StorageClass is { Length: > 0 } sc)
            log.Append(PersistVolumes(chart, sc, target.Kube.StorageSize ?? "8Gi"));

        var d = store.Get(id)!;
        var release = Release(d);
        var ns = target.Kube?.Namespace is { Length: > 0 } n ? n : "default";
        var env = targets.KubeEnv(target);
        var args = TargetService.KubeArgs(target, []).ToList();
        var helm = new List<string> { "upgrade", "--install", release, chart, "--namespace", ns, "--create-namespace", "--wait", "--timeout", "5m" };
        if (target.Kube?.Context is { Length: > 0 } ctx) { helm.Add("--kube-context"); helm.Add(ctx); }
        var up = Cli.Run("helm", helm.ToArray(), env, timeoutMs: 600_000);
        log.AppendLine("helm " + string.Join(' ', helm));
        log.Append(up.Log);
        store.Upsert(store.Get(id)! with { ComposeDir = chart, Project = release });
        if (!up.Ok) return Fail(id, Tail(log.ToString()));

        // ...and it has no Ingress and only ClusterIP services, so nothing is reachable until we say how.
        log.Append(Expose(target, release, ns, stack.Name));
        var urls = IngressUrls(target, release, ns);
        var (health, detail) = PodHealth(target, release, ns);
        return Save(id, true, urls, log.ToString(), health, detail);
    }

    // emptyDir -> PersistentVolumeClaim, one claim per named volume, written next to the templates.
    public static string PersistVolumes(string chartDir, string storageClass, string size)
    {
        var dir = Path.Combine(chartDir, "templates");
        if (!Directory.Exists(dir)) return "";
        var claims = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(dir, "*.yaml", SearchOption.AllDirectories))
        {
            var (rewritten, names) = RewriteEmptyDirs(File.ReadAllText(file));
            if (names.Count == 0) continue;
            File.WriteAllText(file, rewritten);
            foreach (var n in names) claims.Add(n);
        }
        if (claims.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var name in claims) sb.Append(ClaimManifest(name, storageClass, size));
        File.WriteAllText(Path.Combine(dir, "aspireui-claims.yaml"), sb.ToString());
        return $"volumes made persistent on storage class {storageClass} ({size}): {string.Join(", ", claims)}" + Environment.NewLine;
    }

    // Turns an emptyDir volume into a claim reference and reports the volume names it changed.
    public static (string Yaml, List<string> Volumes) RewriteEmptyDirs(string yaml)
    {
        var names = new List<string>();
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        for (var i = 0; i < lines.Count - 1; i++)
        {
            var m = Regex.Match(lines[i], "^(\\s*)- name: \"?([A-Za-z0-9][A-Za-z0-9._-]*)\"?\\s*$");
            if (!m.Success) continue;
            var empty = Regex.Match(lines[i + 1], "^(\\s*)emptyDir: \\{\\}\\s*$");
            if (!empty.Success) continue;
            var name = m.Groups[2].Value;
            names.Add(name);
            lines[i + 1] = empty.Groups[1].Value + "persistentVolumeClaim:";
            lines.Insert(i + 2, empty.Groups[1].Value + "  claimName: \"{{ .Release.Name }}-" + name + "\"");
            i += 2;
        }
        return (string.Join("\n", lines), names);
    }

    private static string ClaimManifest(string name, string storageClass, string size) =>
        "---" + Environment.NewLine +
        "apiVersion: \"v1\"" + Environment.NewLine +
        "kind: \"PersistentVolumeClaim\"" + Environment.NewLine +
        "metadata:" + Environment.NewLine +
        "  name: \"{{ .Release.Name }}-" + name + "\"" + Environment.NewLine +
        "  labels:" + Environment.NewLine +
        "    app.kubernetes.io/instance: \"{{ .Release.Name }}\"" + Environment.NewLine +
        "spec:" + Environment.NewLine +
        "  accessModes:" + Environment.NewLine +
        "    - \"ReadWriteOnce\"" + Environment.NewLine +
        "  storageClassName: \"" + storageClass + "\"" + Environment.NewLine +
        "  resources:" + Environment.NewLine +
        "    requests:" + Environment.NewLine +
        "      storage: \"" + size + "\"" + Environment.NewLine + Environment.NewLine;

    // The chart publishes ClusterIP services only. Depending on the target that becomes a NodePort, a
    // LoadBalancer or an Ingress per service; anything else stays cluster-internal, and we say so.
    private string Expose(DeployTarget t, string release, string ns, string stackName)
    {
        var mode = (t.Kube?.Expose ?? "clusterip").ToLowerInvariant();
        if (mode is "clusterip" or "" or "none")
            return "services stay ClusterIP - set \"expose\" on the target to publish them" + Environment.NewLine;
        var env = targets.KubeEnv(t);
        var scoped = t with { Kube = (t.Kube ?? new TargetKube()) with { Namespace = ns } };
        if (mode is "nodeport" or "loadbalancer")
        {
            var type = mode == "nodeport" ? "NodePort" : "LoadBalancer";
            // `kubectl patch` takes no label selector, so the names come first.
            var names = Cli.Run("kubectl", TargetService.KubeArgs(scoped, ["get", "svc", "-l",
                $"app.kubernetes.io/instance={release}", "-o", "jsonpath={.items[*].metadata.name}"]), env);
            if (!names.Ok) return "could not list services: " + names.Log + Environment.NewLine;
            var patched = new List<string>();
            foreach (var svcName in names.Log.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (svcName.Contains("dashboard", StringComparison.OrdinalIgnoreCase)) continue;
                var r = Cli.Run("kubectl", TargetService.KubeArgs(scoped, ["patch", "svc", svcName,
                    "-p", "{\"spec\":{\"type\":\"" + type + "\"}}"]), env);
                patched.Add(r.Ok ? svcName : $"{svcName}: {r.Log.Trim()}");
            }
            return $"exposing as {type}: {(patched.Count == 0 ? "no service to expose" : string.Join(", ", patched))}" + Environment.NewLine;
        }
        if (mode != "ingress") return $"unknown expose mode '{mode}'" + Environment.NewLine;

        if (t.Kube?.IngressHostPattern is not { Length: > 0 } pattern)
            return "expose=ingress needs a host pattern on the target, e.g. {service}.apps.example.com" + Environment.NewLine;
        var svcJson = Cli.Run("kubectl", TargetService.KubeArgs(scoped,
            ["get", "svc", "-l", $"app.kubernetes.io/instance={release}", "-o", "json"]), env);
        if (!svcJson.Ok) return "could not list services: " + svcJson.Log + Environment.NewLine;
        var manifest = IngressManifest(svcJson.Log, release, SafeName(stackName), pattern, t.Kube?.IngressClass);
        if (manifest.Length == 0) return "no service with a port to expose" + Environment.NewLine;
        var apply = Cli.Run("kubectl", TargetService.KubeArgs(scoped, ["apply", "-f", "-"]), env, stdin: manifest);
        return "ingress: " + (apply.Ok ? apply.Log.Trim() : apply.Log) + Environment.NewLine;
    }

    // One Ingress per service that has a port; {app} and {service} are substituted in the host pattern.
    public static string IngressManifest(string servicesJson, string release, string app, string hostPattern, string? ingressClass)
    {
        var sb = new StringBuilder();
        try
        {
            using var doc = JsonDocument.Parse(servicesJson);
            foreach (var svc in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var meta = svc.GetProperty("metadata");
                var name = meta.GetProperty("name").GetString() ?? "";
                if (name.Contains("dashboard", StringComparison.OrdinalIgnoreCase)) continue;
                if (!svc.GetProperty("spec").TryGetProperty("ports", out var ports) || ports.GetArrayLength() == 0) continue;
                var port = ports[0].GetProperty("port").GetInt32();
                var service = meta.TryGetProperty("labels", out var labels)
                    && labels.TryGetProperty("app.kubernetes.io/component", out var comp)
                    ? comp.GetString() ?? name : name;
                var host = hostPattern.Replace("{app}", app).Replace("{service}", SafeName(service)).Trim();
                sb.AppendLine("---");
                sb.AppendLine("apiVersion: networking.k8s.io/v1");
                sb.AppendLine("kind: Ingress");
                sb.AppendLine("metadata:");
                sb.AppendLine($"  name: {name}-ingress");
                sb.AppendLine("  labels:");
                sb.AppendLine($"    app.kubernetes.io/instance: {release}");
                sb.AppendLine("spec:");
                if (!string.IsNullOrWhiteSpace(ingressClass)) sb.AppendLine($"  ingressClassName: {ingressClass}");
                sb.AppendLine("  rules:");
                sb.AppendLine($"    - host: {host}");
                sb.AppendLine("      http:");
                sb.AppendLine("        paths:");
                sb.AppendLine("          - path: /");
                sb.AppendLine("            pathType: Prefix");
                sb.AppendLine("            backend:");
                sb.AppendLine("              service:");
                sb.AppendLine($"                name: {name}");
                sb.AppendLine("                port:");
                sb.AppendLine($"                  number: {port}");
            }
        }
        catch { return ""; }
        return sb.ToString();
    }

    private static string? FindChart(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var chart = Directory.GetFiles(dir, "Chart.yaml", SearchOption.AllDirectories).FirstOrDefault();
        return chart is null ? null : Path.GetDirectoryName(chart);
    }

    internal List<string> IngressUrls(DeployTarget t, string release, string ns)
    {
        var urls = new List<string>();
        var env = targets.KubeEnv(t);
        var ing = Cli.Run("kubectl", TargetService.KubeArgs(t with { Kube = (t.Kube ?? new TargetKube()) with { Namespace = ns } },
            ["get", "ingress", "-l", $"app.kubernetes.io/instance={release}", "-o", "json"]), env);
        if (ing.Ok) urls.AddRange(ParseIngressHosts(ing.Log));
        var svc = Cli.Run("kubectl", TargetService.KubeArgs(t with { Kube = (t.Kube ?? new TargetKube()) with { Namespace = ns } },
            ["get", "svc", "-l", $"app.kubernetes.io/instance={release}", "-o", "json"]), env);
        if (svc.Ok) urls.AddRange(ParseServiceUrls(svc.Log, NodeHost(t)));
        return urls.Distinct().ToList();
    }

    public static List<string> ParseIngressHosts(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var tls = item.GetProperty("spec").TryGetProperty("tls", out var t) && t.GetArrayLength() > 0;
                if (!item.GetProperty("spec").TryGetProperty("rules", out var rules)) continue;
                foreach (var rule in rules.EnumerateArray())
                    if (rule.TryGetProperty("host", out var h) && h.GetString() is { Length: > 0 } host)
                        urls.Add($"{(tls ? "https" : "http")}://{host}");
            }
        }
        catch { }
        return urls;
    }

    // Where a NodePort is reached: what the target says, else any node's external (then internal) address.
    private string? NodeHost(DeployTarget t)
    {
        if (!string.IsNullOrWhiteSpace(t.PublicHost)) return t.PublicHost!.Trim();
        var r = Cli.Run("kubectl", TargetService.KubeArgs(t, ["get", "nodes", "-o",
            "jsonpath={.items[0].status.addresses[?(@.type=='ExternalIP')].address}"]), targets.KubeEnv(t));
        if (r.Ok && r.Log.Trim() is { Length: > 0 } ext) return ext.Trim().Split(' ')[0];
        var int_ = Cli.Run("kubectl", TargetService.KubeArgs(t, ["get", "nodes", "-o",
            "jsonpath={.items[0].status.addresses[?(@.type=='InternalIP')].address}"]), targets.KubeEnv(t));
        return int_.Ok && int_.Log.Trim().Length > 0 ? int_.Log.Trim().Split(' ')[0] : null;
    }

    // LoadBalancer services carry a reachable address; a NodePort is reached on any node's address.
    public static List<string> ParseServiceUrls(string json, string? nodeHost = null)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var spec = item.GetProperty("spec");
                var type = spec.TryGetProperty("type", out var ty) ? ty.GetString() : "ClusterIP";
                if (type == "NodePort" && nodeHost is { Length: > 0 })
                {
                    foreach (var port in spec.GetProperty("ports").EnumerateArray())
                        if (port.TryGetProperty("nodePort", out var np) && np.GetInt32() > 0)
                            urls.Add($"http://{nodeHost}:{np.GetInt32()}");
                    continue;
                }
                if (type != "LoadBalancer") continue;
                if (!item.TryGetProperty("status", out var st) || !st.TryGetProperty("loadBalancer", out var lb)
                    || !lb.TryGetProperty("ingress", out var ing) || ing.GetArrayLength() == 0) continue;
                var addr = ing[0].TryGetProperty("ip", out var ip) ? ip.GetString()
                    : ing[0].TryGetProperty("hostname", out var hn) ? hn.GetString() : null;
                if (addr is null) continue;
                foreach (var port in spec.GetProperty("ports").EnumerateArray())
                {
                    var p = port.GetProperty("port").GetInt32();
                    urls.Add(p is 443 ? $"https://{addr}" : p is 80 ? $"http://{addr}" : $"http://{addr}:{p}");
                }
            }
        }
        catch { }
        return urls;
    }

    internal (string Health, string? Detail) PodHealth(DeployTarget t, string release, string ns)
    {
        var env = targets.KubeEnv(t);
        var r = Cli.Run("kubectl", TargetService.KubeArgs(t with { Kube = (t.Kube ?? new TargetKube()) with { Namespace = ns } },
            ["get", "pods", "-l", $"app.kubernetes.io/instance={release}", "-o", "json"]), env);
        return r.Ok ? PodHealthOf(r.Log) : ("unknown", null);
    }

    // Same verdicts the compose path gives: a crash-looping or unready pod is not "running".
    public static (string Health, string? Detail) PodHealthOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Our own dashboard sidecar is not the app: it never decides whether the app is healthy.
            var items = doc.RootElement.GetProperty("items").EnumerateArray()
                .Where(p => !PodName(p).Contains("dashboard", StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Count == 0) return ("unknown", null);
            foreach (var pod in items)
            {
                var name = pod.GetProperty("metadata").GetProperty("name").GetString() ?? "pod";
                var status = pod.GetProperty("status");
                var phase = status.TryGetProperty("phase", out var ph) ? ph.GetString() : null;
                if (!status.TryGetProperty("containerStatuses", out var cs)) continue;
                foreach (var c in cs.EnumerateArray())
                {
                    var restarts = c.TryGetProperty("restartCount", out var rc) ? rc.GetInt32() : 0;
                    if (c.TryGetProperty("state", out var st) && st.TryGetProperty("waiting", out var w))
                    {
                        var reason = w.TryGetProperty("reason", out var rs) ? rs.GetString() : "waiting";
                        if (reason is "CrashLoopBackOff" or "ImagePullBackOff" or "ErrImagePull" or "CreateContainerConfigError")
                            return ("failing", $"{name}: {reason}");
                    }
                    if (phase == "Failed") return ("failing", $"{name}: pod failed");
                    if (c.TryGetProperty("ready", out var rd) && !rd.GetBoolean())
                        return restarts > 2 ? ("unhealthy", $"{name}: not ready after {restarts} restarts") : ("starting", $"{name} is still starting up");
                }
            }
            return ("ok", null);
        }
        catch { return ("unknown", null); }
    }

    // ---------- shared operations ----------

    public DeployResult Logs(Deployment d, int tail = 200)
    {
        var t = targets.Resolve(d.TargetId);
        return t.Kind switch
        {
            TargetKind.K8s => Cli.Run("kubectl", TargetService.KubeArgs(t, ["logs", "-l", $"app.kubernetes.io/instance={d.Project}",
                "--all-containers", "--tail", tail.ToString()]), targets.KubeEnv(t)),
            _ => ManagedDeploy.Logs(this, d, t, tail),
        };
    }

    public DeployResult Exec(Deployment d, string cmd)
    {
        var t = targets.Resolve(d.TargetId);
        if (t.Kind == TargetKind.K8s)
        {
            // The app's pod, not our dashboard sidecar — that image has no shell at all.
            if (FirstAppPod(t, d.Project) is not { Length: > 0 } pod) return new DeployResult(false, "no running pod of this app to exec into");
            return Cli.Run("kubectl", TargetService.KubeArgs(t, ["exec", pod, "--", "sh", "-c", cmd]), targets.KubeEnv(t));
        }
        return ManagedDeploy.Exec(this, d, t, cmd);
    }

    public Deployment? Refresh(Deployment d)
    {
        var t = targets.Resolve(d.TargetId);
        if (t.Kind == TargetKind.K8s)
        {
            var ns = t.Kube?.Namespace is { Length: > 0 } n ? n : "default";
            var (health, detail) = PodHealth(t, d.Project, ns);
            var urls = IngressUrls(t, d.Project, ns);
            var state = health switch { "unknown" => "stopped", "failing" => "failed", _ => "running" };
            if (state != d.State || health != d.Health || detail != d.HealthDetail || !urls.SequenceEqual(d.Urls))
                store.Upsert(d with { State = state, Health = health, HealthDetail = detail,
                    Urls = urls.Count > 0 ? urls : d.Urls, UpdatedAt = DateTime.UtcNow.ToString("O") });
            return store.Get(d.Id);
        }
        return ManagedDeploy.Refresh(this, d, t);
    }

    public DeployResult Scale(Deployment d, int replicas)
    {
        var t = targets.Resolve(d.TargetId);
        if (t.Kind == TargetKind.K8s)
            return Cli.Run("kubectl", TargetService.KubeArgs(t, ["scale", "deployment", "-l",
                $"app.kubernetes.io/instance={d.Project}", $"--replicas={replicas}"]), targets.KubeEnv(t));
        return ManagedDeploy.Scale(this, d, t, replicas);
    }

    // What the UI's service list shows for a target without compose: pods, or the managed services.
    public List<ServiceStatus> Services(Deployment d)
    {
        var t = targets.Resolve(d.TargetId);
        if (t.Kind == TargetKind.K8s)
        {
            var r = Cli.Run("kubectl", TargetService.KubeArgs(t, ["get", "pods", "-l", $"app.kubernetes.io/instance={d.Project}",
                "-o", "json"]), targets.KubeEnv(t));
            return r.Ok ? PodServices(r.Log) : new();
        }
        return ManagedDeploy.Services(this, d, t);
    }

    private static string PodName(JsonElement pod) =>
        pod.TryGetProperty("metadata", out var m) && m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

    public static List<ServiceStatus> PodServices(string json)
    {
        var list = new List<ServiceStatus>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var pod in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var name = pod.GetProperty("metadata").GetProperty("name").GetString() ?? "pod";
                if (name.Contains("dashboard", StringComparison.OrdinalIgnoreCase)) continue;
                var status = pod.GetProperty("status");
                var phase = status.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "" : "";
                var image = "";
                var restarts = 0;
                if (status.TryGetProperty("containerStatuses", out var cs) && cs.GetArrayLength() > 0)
                {
                    image = cs[0].TryGetProperty("image", out var im) ? im.GetString() ?? "" : "";
                    restarts = cs[0].TryGetProperty("restartCount", out var rc) ? rc.GetInt32() : 0;
                }
                list.Add(new ServiceStatus(name, name, image, phase.ToLowerInvariant(),
                    restarts > 0 ? $"{phase} ({restarts} restarts)" : phase, ""));
            }
        }
        catch { }
        return list;
    }

    // Pull the newest image for the same tag / roll the workload.
    public DeployResult Restart(Deployment d)
    {
        var t = targets.Resolve(d.TargetId);
        if (t.Kind == TargetKind.K8s)
            return Cli.Run("kubectl", TargetService.KubeArgs(t, ["rollout", "restart", "deployment", "-l",
                $"app.kubernetes.io/instance={d.Project}"]), targets.KubeEnv(t), timeoutMs: 300_000);
        return ManagedDeploy.Restart(this, d, t);
    }

    public DeployResult Remove(Deployment d)
    {
        var t = targets.Resolve(d.TargetId);
        if (t.Kind == TargetKind.K8s)
        {
            var ns = t.Kube?.Namespace is { Length: > 0 } n ? n : "default";
            var args = new List<string> { "uninstall", d.Project, "--namespace", ns };
            if (t.Kube?.Context is { Length: > 0 } ctx) { args.Add("--kube-context"); args.Add(ctx); }
            var r = Cli.Run("helm", args.ToArray(), targets.KubeEnv(t), timeoutMs: 300_000);
            // The Ingress objects are ours, not the release's, so helm leaves them behind.
            var scoped = t with { Kube = (t.Kube ?? new TargetKube()) with { Namespace = ns } };
            var ing = Cli.Run("kubectl", TargetService.KubeArgs(scoped, ["delete", "ingress", "-l",
                $"app.kubernetes.io/instance={d.Project}", "--ignore-not-found"]), targets.KubeEnv(t));
            return new DeployResult(r.Ok, r.Log + Environment.NewLine + ing.Log);
        }
        return ManagedDeploy.Remove(this, d, t);
    }

    // Volume contents on an orchestrator target: through the pod, not through a docker socket.
    public (byte[]? data, string? error) TarOut(Deployment d, string path)
    {
        var t = targets.Resolve(d.TargetId);
        if (t.Kind != TargetKind.K8s) return (null, "this target has no browsable storage");
        if (FirstAppPod(t, d.Project) is not { Length: > 0 } pod) return (null, "no running pod of this app");
        var r = Cli.Run("kubectl", TargetService.KubeArgs(t, ["exec", pod, "--", "tar", "cf", "-", "-C", path, "."]),
            targets.KubeEnv(t), timeoutMs: 600_000);
        return r.Ok ? (Encoding.UTF8.GetBytes(r.Log), null) : (null, r.Log);
    }

    // First running pod of a release that is not our dashboard sidecar.
    private string? FirstAppPod(DeployTarget t, string release)
    {
        var r = Cli.Run("kubectl", TargetService.KubeArgs(t, ["get", "pods", "-l",
            $"app.kubernetes.io/instance={release}", "-o", "json"]), targets.KubeEnv(t));
        if (!r.Ok) return null;
        return PickAppPod(r.Log);
    }

    public static string? PickAppPod(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pods = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(p => (Name: PodName(p),
                    Running: p.TryGetProperty("status", out var st) && st.TryGetProperty("phase", out var ph) && ph.GetString() == "Running"))
                .Where(p => p.Name.Length > 0 && !p.Name.Contains("dashboard", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return pods.FirstOrDefault(p => p.Running).Name ?? pods.FirstOrDefault().Name;
        }
        catch { return null; }
    }

    internal static string SafeName(string s) =>
        Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-') is { Length: > 0 } v ? v : "app";
}
