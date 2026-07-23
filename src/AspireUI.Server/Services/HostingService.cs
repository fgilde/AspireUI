using System.Text.Json;
using System.Text.RegularExpressions;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// One service (container) of a deployed compose project, as reported by `docker compose ps`.
public record ServiceStatus(string Name, string Service, string Image, string State, string Status, string Ports);

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

    // Container ports that non-dashboard services `expose` (the ones an app actually serves on),
    // in appearance order — used to allocate host ports.
    public static List<int> ExposedAppPorts(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var result = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var svc = Regex.Match(lines[i], @"^  (\S[^:]*):\s*$");
            if (!svc.Success || svc.Groups[1].Value.Contains("dashboard") || !InServicesSection(lines, i)) continue;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Regex.IsMatch(lines[j], @"^ {0,2}\S")) break;
                var pm = Regex.Match(lines[j], @"^      -\s*""?(\d+)""?\s*$");
                if (pm.Success) result.Add(int.Parse(pm.Groups[1].Value));
            }
        }
        return result;
    }

    // Aspire's compose publisher only `expose`s container ports (internal), so a hosted app isn't
    // reachable from the host. For each non-dashboard service, add a `ports:` block mapping each
    // exposed container port to the ALLOCATED host port from `hostByContainer` (so multiple apps that
    // share a container port, e.g. two :80 apps, get distinct host ports). Skips a service that already
    // publishes ports.
    public static string PublishExposedPorts(string yaml, IReadOnlyDictionary<int, int> hostByContainer)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var outp = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            outp.Add(lines[i]);
            var svc = Regex.Match(lines[i], @"^  (\S[^:]*):\s*$");
            if (!svc.Success || svc.Groups[1].Value.Contains("dashboard") || !InServicesSection(lines, i)) continue;
            var ports = new List<int>(); var hasPorts = false;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Regex.IsMatch(lines[j], @"^ {0,2}\S")) break;
                if (Regex.IsMatch(lines[j], @"^    ports:\s*$")) hasPorts = true;
                var pm = Regex.Match(lines[j], @"^      -\s*""?(\d+)""?\s*$");
                if (pm.Success) ports.Add(int.Parse(pm.Groups[1].Value));
            }
            if (!hasPorts && ports.Count > 0)
            {
                outp.Add("    ports:");
                foreach (var p in ports)
                    outp.Add($"      - \"{(hostByContainer.TryGetValue(p, out var h) ? h : p)}:{p}\"");
            }
        }
        return string.Join("\n", outp);
    }

    // Pick a free host port in 20000..29999 not already claimed (by another deployment or this run) and
    // not currently bound on the host.
    public static int AllocateHostPort(ISet<int> used)
    {
        for (var p = 20000; p <= 29999; p++)
        {
            if (used.Contains(p)) continue;
            try { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p); l.Start(); l.Stop(); }
            catch { continue; }   // in use on the host
            used.Add(p);
            return p;
        }
        throw new InvalidOperationException("no free host port in 20000-29999");
    }

    // Emit http://host:HOSTPORT for each published `- "HOST:CONTAINER"` mapping.
    public static List<string> ParseUrls(string yaml, string host)
    {
        var urls = new List<string>();
        foreach (Match m in Regex.Matches(yaml, @"-\s*""?(\d+):\d+""?"))
            urls.Add($"http://{host}:{m.Groups[1].Value}");
        return urls.Distinct().ToList();
    }

    // Authoritative URLs from what docker ACTUALLY published (`compose ps` publishers) — catches ports
    // that our static compose parse misses (e.g. project/package resources whose ports Aspire emits in a
    // shape ParseUrls doesn't match). Skips the Aspire dashboard.
    public static List<string> UrlsFromServices(IEnumerable<ServiceStatus> svcs, string host) =>
        svcs.Where(s => !s.Name.Contains("dashboard") && !s.Service.Contains("dashboard"))
            .SelectMany(s => s.Ports.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim().Split(':')[0])
            .Where(p => int.TryParse(p, out _))
            .Distinct()
            .Select(p => $"http://{host}:{p}")
            .ToList();

    // Configure the Aspire dashboard service in a hosting compose: when the admin doesn't host it, drop
    // its published `ports:` so it's not reachable; when a browser token is set, inject it so AspireUI can
    // hand out a one-click login link (works everywhere — no reverse proxy needed).
    public static string ConfigureDashboard(string yaml, bool host, string? token)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var hdr = -1;
        for (var i = 0; i < lines.Count; i++)
            if (Regex.IsMatch(lines[i], @"^  \S*dashboard\S*:\s*$") && InServicesSection(lines, i)) { hdr = i; break; }
        if (hdr < 0) return yaml;
        var end = lines.Count;
        for (var i = hdr + 1; i < lines.Count; i++) if (Regex.IsMatch(lines[i], @"^ {0,2}\S")) { end = i; break; }
        var block = lines.GetRange(hdr, end - hdr);
        if (!host)
        {
            var outb = new List<string>();
            for (var i = 0; i < block.Count; i++)
            {
                if (Regex.IsMatch(block[i], @"^    ports:\s*$")) { i++; while (i < block.Count && Regex.IsMatch(block[i], @"^      ")) i++; i--; continue; }
                outb.Add(block[i]);
            }
            block = outb;
        }
        else if (!string.IsNullOrWhiteSpace(token))
        {
            var entry = $"      - \"Dashboard__Frontend__BrowserToken={token}\"";
            var envIdx = block.FindIndex(l => Regex.IsMatch(l, @"^    environment:\s*$"));
            if (envIdx >= 0) block.Insert(envIdx + 1, entry);
            else { block.Insert(1, "    environment:"); block.Insert(2, entry); }
        }
        var result = new List<string>();
        result.AddRange(lines.GetRange(0, hdr));
        result.AddRange(block);
        result.AddRange(lines.GetRange(end, lines.Count - end));
        return string.Join("\n", result);
    }

    public Deployment Deploy(StackModel stack, string publishRoot, string host = "localhost",
        bool hostDashboard = true, string? dashboardToken = null)
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
            var raw = ConfigureDashboard(AddRestartPolicy(File.ReadAllText(path)), hostDashboard, dashboardToken);
            // Allocate a distinct free host port per exposed container port so multiple apps that share a
            // container port (e.g. two :80 apps) don't collide on the host. Ports already claimed by other
            // deployments are off-limits.
            var used = new HashSet<int>(store.List().Where(x => x.Id != id).SelectMany(x => x.Urls)
                .Select(u => Regex.Match(u, @"://[^/:]+:(\d+)")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)));
            var portMap = new Dictionary<int, int>();
            foreach (var cp in ExposedAppPorts(raw).Distinct()) portMap[cp] = AllocateHostPort(used);
            var processed = PublishExposedPorts(raw, portMap);
            File.WriteAllText(path, processed);
            var up = deploy.UpProject(pub.OutputDir, project);
            // Prefer the ports docker actually published (`compose ps`); fall back to the static parse.
            var urls = up.Ok ? UrlsFromServices(ParseServices(deploy.Ps(pub.OutputDir, project).Log), host) : new();
            if (urls.Count == 0) urls = ParseUrls(processed, host);
            // Prepend the friendly proxy URL (…/<slug>.<domain>) when the proxy is active + app has a port.
            if (proxy is { Enabled: true } && FirstPort(urls) is > 0) urls.Insert(0, proxy.UrlFor(stack.Name));
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

    // The per-service status of a deployment, from `docker compose ps` — used for the resource tree.
    public List<ServiceStatus> Services(string id)
        => store.Get(id) is { } d ? ParseServices(deploy.Ps(d.ComposeDir, d.Project).Log) : new();

    // Parse `docker compose ps --format json` — tolerates both a JSON array and newline-delimited
    // objects (the format changed across compose versions).
    public static List<ServiceStatus> ParseServices(string psJson)
    {
        var list = new List<ServiceStatus>();
        var text = psJson.Trim();
        if (text.Length == 0) return list;
        var elements = new List<JsonElement>();
        try
        {
            if (text.StartsWith("["))
                using (var doc = JsonDocument.Parse(text))
                    elements.AddRange(doc.RootElement.EnumerateArray().Select(e => e.Clone()));
            else
                foreach (var line in text.Split('\n'))
                    if (line.Trim().StartsWith("{"))
                        using (var doc = JsonDocument.Parse(line.Trim()))
                            elements.Add(doc.RootElement.Clone());
        }
        catch { return list; }   // unparseable (e.g. docker error text) → empty tree, not a crash
        foreach (var e in elements)
        {
            string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var ports = "";
            if (e.TryGetProperty("Publishers", out var pubs) && pubs.ValueKind == JsonValueKind.Array)
                ports = string.Join(", ", pubs.EnumerateArray()
                    .Select(p => ((p.TryGetProperty("PublishedPort", out var pp) ? pp.GetInt32() : 0),
                                  (p.TryGetProperty("TargetPort", out var tp) ? tp.GetInt32() : 0)))
                    .Where(x => x.Item1 > 0).Select(x => $"{x.Item1}:{x.Item2}").Distinct());
            list.Add(new ServiceStatus(S("Name"), S("Service"), S("Image"), S("State"), S("Status"), ports));
        }
        return list;
    }

    // Replace each named node's LITERAL environment variables with `env[nodeId]` (a list of [key,value]
    // pairs), keeping parameter-backed env (value is a bare varName, not a quoted string) and every other
    // WithCall. Used by the hosting "configure" dialog (stop → apply → redeploy).
    public static StackModel ApplyEnvUpdates(StackModel stack, IReadOnlyDictionary<string, List<string[]>> env)
    {
        var nodes = stack.Nodes.Select(n =>
            env.TryGetValue(n.Id, out var pairs) ? n with { WithCalls = ReplaceLiteralEnv(n.WithCalls, pairs) } : n).ToList();
        return stack with { Nodes = nodes };
    }

    private static bool IsLiteralEnv(WithCall w) =>
        w.Method == "WithEnvironment" && w.Args.Count == 2 && w.Args[1].StartsWith("\"");

    private static List<WithCall> ReplaceLiteralEnv(List<WithCall> calls, List<string[]> pairs)
    {
        var kept = calls.Where(w => !IsLiteralEnv(w)).ToList();
        foreach (var p in pairs.Where(p => p.Length == 2 && !string.IsNullOrWhiteSpace(p[0])))
            kept.Add(new WithCall("WithEnvironment", new() { JsonSerializer.Serialize(p[0]), JsonSerializer.Serialize(p[1]) }));
        return kept;
    }

    // Per-resource config the hosting "configure" dialog edits: name, image, and current literal env.
    // Skips parameter/connection-string nodes (nothing to configure there).
    public record NodeConfig(string NodeId, string Name, string AddMethod, string Image, List<string[]> Env);
    public static List<NodeConfig> NodeConfigs(StackModel stack)
    {
        var env = ReadLiteralEnv(stack);
        return stack.Nodes
            .Where(n => !n.AddMethod.StartsWith("AddParameter") && !n.AddMethod.StartsWith("AddConnectionString"))
            .Select(n => new NodeConfig(n.Id, n.ResourceName, n.AddMethod,
                n.AddArgs.FirstOrDefault() is { } a && a.StartsWith("\"") ? Unquote(a) : "",
                env.TryGetValue(n.Id, out var e) ? e : new()))
            .ToList();
    }

    // The current LITERAL env vars per node (nodeId → [[key,value]…]) — what the configure dialog shows.
    public static Dictionary<string, List<string[]>> ReadLiteralEnv(StackModel stack)
    {
        var result = new Dictionary<string, List<string[]>>();
        foreach (var n in stack.Nodes)
        {
            var pairs = n.WithCalls.Where(IsLiteralEnv)
                .Select(w => new[] { Unquote(w.Args[0]), Unquote(w.Args[1]) }).ToList();
            if (pairs.Count > 0) result[n.Id] = pairs;
        }
        return result;
    }

    private static string Unquote(string literal)
    {
        try { return JsonSerializer.Deserialize<string>(literal) ?? literal; }
        catch { return literal.Trim('"'); }
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
