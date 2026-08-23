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

        var urls = IngressUrls(target, release, ns);
        var (health, detail) = PodHealth(target, release, ns);
        return Save(id, true, urls, log.ToString(), health, detail);
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
        if (svc.Ok) urls.AddRange(ParseServiceUrls(svc.Log));
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

    // LoadBalancer services carry a reachable address; NodePort at least tells the user the port.
    public static List<string> ParseServiceUrls(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var spec = item.GetProperty("spec");
                var type = spec.TryGetProperty("type", out var ty) ? ty.GetString() : "ClusterIP";
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
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
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
            var pod = Cli.Run("kubectl", TargetService.KubeArgs(t, ["get", "pods", "-l", $"app.kubernetes.io/instance={d.Project}",
                "-o", "jsonpath={.items[0].metadata.name}"]), targets.KubeEnv(t));
            if (!pod.Ok || string.IsNullOrWhiteSpace(pod.Log)) return new DeployResult(false, "no pod to exec into");
            return Cli.Run("kubectl", TargetService.KubeArgs(t, ["exec", pod.Log.Trim(), "--", "sh", "-c", cmd]), targets.KubeEnv(t));
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

    public static List<ServiceStatus> PodServices(string json)
    {
        var list = new List<ServiceStatus>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var pod in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var name = pod.GetProperty("metadata").GetProperty("name").GetString() ?? "pod";
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
            return Cli.Run("helm", args.ToArray(), targets.KubeEnv(t), timeoutMs: 300_000);
        }
        return ManagedDeploy.Remove(this, d, t);
    }

    // Volume contents on an orchestrator target: through the pod, not through a docker socket.
    public (byte[]? data, string? error) TarOut(Deployment d, string path)
    {
        var t = targets.Resolve(d.TargetId);
        if (t.Kind != TargetKind.K8s) return (null, "this target has no browsable storage");
        var pod = Cli.Run("kubectl", TargetService.KubeArgs(t, ["get", "pods", "-l", $"app.kubernetes.io/instance={d.Project}",
            "-o", "jsonpath={.items[0].metadata.name}"]), targets.KubeEnv(t));
        if (!pod.Ok || string.IsNullOrWhiteSpace(pod.Log)) return (null, "no pod");
        var r = Cli.Run("kubectl", TargetService.KubeArgs(t, ["exec", pod.Log.Trim(), "--", "tar", "cf", "-", "-C", path, "."]),
            targets.KubeEnv(t), timeoutMs: 600_000);
        return r.Ok ? (Encoding.UTF8.GetBytes(r.Log), null) : (null, r.Log);
    }

    internal static string SafeName(string s) =>
        Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-') is { Length: > 0 } v ? v : "app";
}
