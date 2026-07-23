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
        if (proxy is null || !proxy.Enabled) return;
        var routes = store.List()
            .Where(d => d.State == "running")
            .Select(d => (Slug: ProxyService.Slug(d.Name), Port: FirstPort(d.Urls)))
            .Where(r => r.Port is > 0)
            .Select(r => (r.Slug, r.Port!.Value));
        proxy.Reload(routes);
    }

    // Whether line i (a 2-space `name:` header) sits inside the top-level `services:` section — so we
    // never treat `networks:`/`volumes:` sub-keys as services. Returns the service-block end index too.
    private static bool InServicesSection(IReadOnlyList<string> lines, int headerIndex)
    {
        for (var k = headerIndex - 1; k >= 0; k--)
        {
            if (Regex.IsMatch(lines[k], @"^services:\s*$")) return true;
            if (Regex.IsMatch(lines[k], @"^\S")) return false;   // hit another top-level key first
        }
        return false;
    }

    // Add `restart: unless-stopped` to each *service* (skips a service that already declares any
    // `restart:`, e.g. Aspire's `restart: "always"` on the dashboard). Only within `services:`.
    public static string AddRestartPolicy(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var outp = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            outp.Add(lines[i]);
            if (!Regex.IsMatch(lines[i], @"^  (\S[^:]*):\s*$") || !InServicesSection(lines, i)) continue;
            var hasRestart = false;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Regex.IsMatch(lines[j], @"^ {0,2}\S")) break;          // next service / dedent
                if (Regex.IsMatch(lines[j], @"^\s+restart:\s")) { hasRestart = true; break; }
            }
            if (!hasRestart) outp.Add("    restart: unless-stopped");
        }
        return string.Join("\n", outp);
    }

    // Aspire's compose publisher only `expose`s container ports (internal), so a hosted app isn't
    // reachable from the host. For each non-dashboard service that has an `expose:` list, add a
    // `ports:` block publishing each exposed port to the same host port. Idempotent (skips a service
    // that already has `ports:`). ponytail: publishes host ports 1:1 → two deployed apps sharing a
    // port collide; the reverse proxy (Linux) is the real multi-app answer, this makes single/localhost
    // deploys reachable.
    public static string PublishExposedPorts(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var outp = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            outp.Add(lines[i]);
            var svc = Regex.Match(lines[i], @"^  (\S[^:]*):\s*$");
            if (!svc.Success || svc.Groups[1].Value.Contains("dashboard") || !InServicesSection(lines, i)) continue;
            // Scan this service block for expose ports + whether it already publishes ports.
            var ports = new List<string>(); var hasPorts = false;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Regex.IsMatch(lines[j], @"^ {0,2}\S")) break;      // next service / dedent
                if (Regex.IsMatch(lines[j], @"^    ports:\s*$")) hasPorts = true;
                var pm = Regex.Match(lines[j], @"^      -\s*""?(\d+)""?\s*$"); // an expose entry
                if (pm.Success) ports.Add(pm.Groups[1].Value);
            }
            if (!hasPorts && ports.Count > 0)
            {
                outp.Add("    ports:");
                foreach (var p in ports) outp.Add($"      - \"{p}:{p}\"");
            }
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
            var processed = PublishExposedPorts(AddRestartPolicy(File.ReadAllText(path)));
            File.WriteAllText(path, processed);
            var urls = ParseUrls(processed, host);
            // Prepend the friendly proxy URL (…/<slug>.<domain>) when the proxy is active + app has a port.
            if (proxy is { Enabled: true } && FirstPort(urls) is > 0) urls.Insert(0, proxy.UrlFor(stack.Name));
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
    // `up -d` (not `compose start`) so Start also (re)creates containers when a prior deploy failed or
    // was pruned — plain `start` only resumes existing stopped containers and silently no-ops otherwise.
    public void Start(string id) { if (store.Get(id) is { } d) { var r = deploy.UpProject(d.ComposeDir, d.Project); store.SetState(id, r.Ok ? "running" : "failed", r.Ok ? null : r.Log); SyncProxy(); } }
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
