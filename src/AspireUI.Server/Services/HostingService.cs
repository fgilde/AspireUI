using System.Text.RegularExpressions;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// Turns a stack into a tracked, persistent compose deployment (install & forget), separate from the
// ephemeral dev Run path. Deploy = publish → post-process compose (restart policy) → up -d.
public class HostingService(DeploymentStore store, PublishService publish, DeployService deploy, ProxyService? proxy = null)
{
    public static string Project(string stackId) => "aspireui-" + stackId[..Math.Min(8, stackId.Length)];

    // First EXPLICIT host port in the urls (ignores proxy-style urls like http://app.localhost which
    // carry no ":port"), so proxy routing picks the real published port.
    private static int? FirstPort(IEnumerable<string> urls)
    {
        foreach (var u in urls)
        {
            var m = Regex.Match(u, @"://[^/:]+:(\d+)");
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

    // Rebuild the reverse-proxy config from every running deployment that publishes a port.
    private void SyncProxy()
    {
        if (proxy is null) return;
        var routes = store.List()
            .Where(d => d.State == "running")
            .Select(d => (Slug: ProxyService.Slug(d.Name), Port: FirstPort(d.Urls)))
            .Where(r => r.Port is > 0)
            .Select(r => (r.Slug, r.Port!.Value));
        proxy.Reload(routes);
    }

    // Add `restart: unless-stopped` under each service (2-space service indent → 4-space property).
    // Idempotent: skips a service that already has a restart line.
    public static string AddRestartPolicy(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var outp = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            outp.Add(lines[i]);
            var m = Regex.Match(lines[i], @"^  (\S[^:]*):\s*$");   // a service header: `  web:`
            if (!m.Success) continue;
            var hasRestart = false;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Regex.IsMatch(lines[j], @"^  \S")) break;      // next service / dedent
                if (lines[j].Trim() == "restart: unless-stopped") { hasRestart = true; break; }
            }
            if (!hasRestart) outp.Add("    restart: unless-stopped");
        }
        return string.Join("\n", outp);
    }

    // Emit http://host:HOSTPORT for each published `- "HOST:CONTAINER"` mapping.
    public static List<string> ParseUrls(string yaml, string host)
    {
        var urls = new List<string>();
        foreach (Match m in Regex.Matches(yaml, @"-\s*""?(\d+):\d+""?"))
            urls.Add($"http://{host}:{m.Groups[1].Value}");
        return urls.Distinct().ToList();
    }

    public Deployment Deploy(StackModel stack, string publishRoot, string host = "localhost")
    {
        var project = Project(stack.Id);
        var now = DateTime.UtcNow.ToString("O");
        var existing = store.GetByStack(stack.Id);
        var id = existing?.Id ?? "dep" + Guid.NewGuid().ToString("n")[..8];
        store.Upsert(new Deployment(id, stack.Id, stack.Name, existing?.ComposeDir ?? "", project, "deploying",
            existing?.Urls ?? new(), existing?.CreatedAt ?? now, now, null));
        try
        {
            var pub = publish.Publish(stack, publishRoot, "compose");
            if (!pub.Ok) { store.SetState(id, "failed", pub.Log); return store.Get(id)!; }
            var path = Path.Combine(pub.OutputDir, "docker-compose.yaml");
            var yaml = File.ReadAllText(path);
            File.WriteAllText(path, AddRestartPolicy(yaml));
            var urls = ParseUrls(yaml, host);
            // Prepend the friendly proxy URL (…/<slug>.<domain>) when a proxy is configured + app has a port.
            if (proxy is not null && FirstPort(urls) is > 0) urls.Insert(0, proxy.UrlFor(stack.Name));
            var up = deploy.UpProject(pub.OutputDir, project);
            store.Upsert(store.Get(id)! with
            {
                ComposeDir = pub.OutputDir, Urls = urls,
                State = up.Ok ? "running" : "failed", LastError = up.Ok ? null : up.Log,
                UpdatedAt = DateTime.UtcNow.ToString("O"),
            });
            if (up.Ok) SyncProxy();
        }
        catch (Exception ex) { store.SetState(id, "failed", ex.Message); }
        return store.Get(id)!;
    }

    // Pull newer images + recreate (in place). Keeps the same project/volumes/URLs.
    public Deployment? Update(string id)
    {
        if (store.Get(id) is not { } d) return null;
        store.SetState(id, "deploying");
        var pull = deploy.PullProject(d.ComposeDir, d.Project);
        var up = deploy.UpProject(d.ComposeDir, d.Project);
        store.SetState(id, up.Ok ? "running" : "failed", up.Ok ? null : $"{pull.Log}\n{up.Log}");
        if (up.Ok) SyncProxy();
        return store.Get(id);
    }

    // Top-level `volumes:` keys → compose v2 names them `<project>_<key>`.
    public static List<string> VolumeNames(string yaml)
    {
        var names = new List<string>();
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var inVolumes = false;
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^volumes:\s*$")) { inVolumes = true; continue; }
            if (inVolumes)
            {
                if (Regex.IsMatch(line, @"^\S")) break;             // dedent → section ended
                var m = Regex.Match(line, @"^  (\S[^:]*):");
                if (m.Success) names.Add(m.Groups[1].Value.Trim());
            }
        }
        return names;
    }

    // Snapshot each named volume to a tgz in backupsRoot/<stackId>/<timestamp>/. Best-effort (docker).
    public string? Backup(string id, string backupsRoot)
    {
        if (store.Get(id) is not { } d) return null;
        var composePath = Path.Combine(d.ComposeDir, "docker-compose.yaml");
        if (!File.Exists(composePath)) return null;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(backupsRoot, d.StackId, stamp);
        Directory.CreateDirectory(dir);
        foreach (var vol in VolumeNames(File.ReadAllText(composePath)))
        {
            var full = $"{d.Project}_{vol}";
            deploy.Docker(dir, $"run --rm -v {full}:/data -v \"{Path.GetFullPath(dir)}\":/backup alpine tar czf /backup/{vol}.tgz -C /data .");
        }
        return dir;
    }

    // On host/AppHost start, bring back everything that was "running" (compose restart policy handles
    // container-level restarts, but the AppHost process must re-issue `up -d` after its own restart).
    public void ReconcileOnStartup()
    {
        var any = false;
        foreach (var d in store.List().Where(x => x.State == "running"))
        {
            try { if (File.Exists(Path.Combine(d.ComposeDir, "docker-compose.yaml"))) { deploy.UpProject(d.ComposeDir, d.Project); any = true; } }
            catch { /* best-effort */ }
        }
        if (any) SyncProxy();
    }

    public void Stop(string id) { if (store.Get(id) is { } d) { deploy.StopProject(d.ComposeDir, d.Project); store.SetState(id, "stopped"); SyncProxy(); } }
    public void Start(string id) { if (store.Get(id) is { } d) { deploy.StartProject(d.ComposeDir, d.Project); store.SetState(id, "running"); SyncProxy(); } }
    public void Undeploy(string id) { if (store.Get(id) is { } d) { deploy.DownProject(d.ComposeDir, d.Project); store.Delete(id); SyncProxy(); } }

    // Best-effort reconcile from `docker compose ps` (exit!=0 or no running container → stopped).
    public Deployment? Refresh(string id)
    {
        if (store.Get(id) is not { } d) return null;
        if (d.State is "deploying" or "failed") return d;
        var ps = deploy.Ps(d.ComposeDir, d.Project);
        var running = ps.Ok && ps.Log.Contains("\"State\":\"running\"", StringComparison.OrdinalIgnoreCase);
        var next = running ? "running" : "stopped";
        if (next != d.State) store.SetState(id, next);
        return store.Get(id);
    }
}
