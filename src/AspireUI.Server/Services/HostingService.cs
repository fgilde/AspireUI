using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record ServiceStatus(string Name, string Service, string Image, string State, string Status, string Ports);

public class HostingService(DeploymentStore store, PublishService publish, DeployService deploy,
    ProxyService? proxy = null, TargetService? targets = null, OrchestratorService? orchestrator = null)
{
    public static string Project(string stackId) => "aspireui-" + stackId[..Math.Min(8, stackId.Length)];

    // Every docker command for a deployment goes to the daemon of its target; without a TargetService
    // (tests, local-only paths) that is the ambient docker of this machine.
    private static readonly DeployTarget LocalFallback = new(DeployTarget.LocalId, "This machine", TargetKind.Local, true);
    private DeployService R(Deployment d) => targets?.Runner(d.Target) ?? deploy;
    private DeployService R(DeployTarget t) => targets?.Runner(t) ?? deploy;
    public DeployTarget TargetOf(Deployment d) => targets?.Resolve(d.TargetId) ?? LocalFallback;
    public DeployTarget TargetById(string? id) => targets?.Resolve(id) ?? LocalFallback;
    public bool IsOrchestrated(Deployment d) => TargetKind.IsOrchestrator(TargetOf(d).Kind);

    private Deployment OrchestratorDeploy(StackModel stack, string publishRoot, string id, DeployTarget target, string? cloneSrc)
        => orchestrator is null
            ? Fail(id, $"'{target.Name}' is a {target.Kind} target, which this instance cannot deploy to")
            : orchestrator.Deploy(stack, publishRoot, id, target, cloneSrc);

    private Deployment Fail(string id, string message)
    {
        store.SetState(id, "failed", message);
        return store.Get(id)!;
    }

    private static int? FirstPort(IEnumerable<string> urls)
    {
        foreach (var u in urls)
        {
            var m = Regex.Match(u, @"://[^/:]+:(\d+)");
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

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

    private static bool InServicesSection(IReadOnlyList<string> lines, int headerIndex)
    {
        for (var k = headerIndex - 1; k >= 0; k--)
        {
            if (Regex.IsMatch(lines[k], @"^services:\s*$")) return true;
            if (Regex.IsMatch(lines[k], @"^\S")) return false;   // hit another top-level key first
        }
        return false;
    }

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
                if (Regex.IsMatch(lines[j], @"^ {0,2}\S")) break;
                if (Regex.IsMatch(lines[j], @"^\s+restart:\s")) { hasRestart = true; break; }
            }
            if (!hasRestart) outp.Add("    restart: unless-stopped");
        }
        return string.Join("\n", outp);
    }

    // Aspire publishes AddDockerfile resources as `image: ${X_IMAGE}` with no build section; add one so compose builds locally instead of trying to pull.
    public static string InjectDockerfileBuilds(string yaml, StackModel stack, string srcDir)
    {
        var builds = new Dictionary<string, (string ctx, string? df)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in stack.Nodes.Where(n => n.AddMethod == "AddDockerfile"))
        {
            var key = Regex.Replace(n.ResourceName.ToUpperInvariant(), "[^A-Z0-9]", "_") + "_IMAGE";
            var args = n.AddArgs ?? new();
            var ctx = args.Count > 0 ? args[0].Trim().Trim('"') : ".";
            var df = args.Count > 1 ? args[1].Trim().Trim('"') : null;
            builds[key] = (ctx, df);
        }
        if (builds.Count == 0) return yaml;

        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^    image:\s*""?\$\{([A-Za-z0-9_]+)\}""?\s*$");
            if (!m.Success || !builds.TryGetValue(m.Groups[1].Value, out var b)) continue;
            var abs = Path.GetFullPath(Path.Combine(srcDir, b.ctx.Replace('/', Path.DirectorySeparatorChar))).Replace('\\', '/');
            var ins = new List<string> { "    build:", $"      context: \"{abs}\"" };
            if (b.df is not null) ins.Add($"      dockerfile: \"{b.df}\"");
            lines.InsertRange(i + 1, ins);
            i += ins.Count;
        }
        return string.Join("\n", lines);
    }

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

    public static string PublishExposedPorts(string yaml, IReadOnlyDictionary<int, int> hostByContainer,
        IReadOnlySet<int>? keepInternal = null)
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
            var publish = ports.Where(p => keepInternal is null || !keepInternal.Contains(p)).ToList();
            if (!hasPorts && publish.Count > 0)
            {
                outp.Add("    ports:");
                foreach (var p in publish)
                    outp.Add($"      - \"{(hostByContainer.TryGetValue(p, out var h) ? h : p)}:{p}\"");
            }
        }
        return string.Join("\n", outp);
    }

    public static bool PortFree(int p)
    {
        try { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p); l.Start(); l.Stop(); return true; }
        catch { return false; }
    }

    public static int AllocateHostPort(ISet<int> used) => AllocateHostPort(used, 20000, 29999, PortFree);

    // A remote target cannot be probed by binding a socket here, so for those "free" is only what the
    // target's own daemon does not already publish — the caller passes that in as `used`.
    public static int AllocateHostPort(ISet<int> used, int from, int to, Func<int, bool>? free = null)
    {
        if (from <= 0) from = 20000;
        if (to < from) to = from + 999;
        for (var p = from; p <= to; p++)
        {
            if (used.Contains(p)) continue;
            if (free is not null && !free(p)) continue;
            used.Add(p);
            return p;
        }
        throw new InvalidOperationException($"no free host port in {from}-{to}");
    }

    public static void FillParameterEnv(StackModel stack, string envPath)
    {
        if (!File.Exists(envPath)) return;
        var known = new Dictionary<string, string>();
        foreach (var n in stack.Nodes.Where(n => n.AddMethod == "AddParameter" && n.AddArgs.Count > 0))
        {
            var key = Regex.Replace(n.ResourceName.ToUpperInvariant(), "[^A-Z0-9]", "_");
            var val = Unquote(n.AddArgs[0]);
            if (!string.IsNullOrEmpty(val)) known[key] = val;
        }
        var lines = File.ReadAllLines(envPath);
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^([A-Za-z0-9_]+)=\s*$");
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var val = known.TryGetValue(key, out var v) ? v
                : "aspireui-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
            lines[i] = $"{key}={val}"; changed = true;
        }
        if (changed) File.WriteAllText(envPath, string.Join("\n", lines));
    }

    public static List<string> ParseUrls(string yaml, string host)
    {
        var urls = new List<string>();
        foreach (Match m in Regex.Matches(yaml, @"-\s*""?(\d+):\d+""?"))
            urls.Add($"http://{host}:{m.Groups[1].Value}");
        return urls.Distinct().ToList();
    }

    public static List<string> UrlsFromServices(IEnumerable<ServiceStatus> svcs, string host) =>
        svcs.Where(s => !s.Name.Contains("dashboard") && !s.Service.Contains("dashboard"))
            .SelectMany(s => s.Ports.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim().Split(':')[0])
            .Where(p => int.TryParse(p, out _))
            .Distinct()
            .Select(p => $"http://{host}:{p}")
            .ToList();

    public static List<string> DomainUrls(IEnumerable<NpmProxyHost> hosts, Deployment d)
    {
        var ports = (d.Ports ?? new()).Where(p => p.Public && p.Host > 0).Select(p => p.Host).ToHashSet();
        foreach (var u in d.Urls)
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri)) ports.Add(uri.Port);
        return hosts.Where(h => h.Enabled && ports.Contains(h.ForwardPort))
            .SelectMany(h => h.DomainNames.Select(n => $"{(h.SslForced || h.CertificateId > 0 ? "https" : "http")}://{n}"))
            .Distinct()
            .ToList();
    }

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
            var entries = new[]
            {
                "      - \"Dashboard__Frontend__AuthMode=BrowserToken\"",
                $"      - \"Dashboard__Frontend__BrowserToken={token}\"",
            };
            var envIdx = block.FindIndex(l => Regex.IsMatch(l, @"^    environment:\s*$"));
            if (envIdx >= 0) block.InsertRange(envIdx + 1, entries);
            else { block.Insert(1, "    environment:"); block.InsertRange(2, entries); }
        }
        var result = new List<string>();
        result.AddRange(lines.GetRange(0, hdr));
        result.AddRange(block);
        result.AddRange(lines.GetRange(end, lines.Count - end));
        return string.Join("\n", result);
    }

    public static string EnsureCompanionDatabases(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var dbByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in ServiceBlocks(lines))
        {
            var text = string.Join("\n", lines.GetRange(b.Start, b.End - b.Start));
            foreach (Match m in Regex.Matches(text, @"Host=(?<h>[A-Za-z0-9_.-]+)[^""\n]*?Database=(?<d>[A-Za-z0-9_]+)", RegexOptions.IgnoreCase))
                Register(dbByHost, m.Groups["h"].Value, m.Groups["d"].Value);
            foreach (Match m in Regex.Matches(text, @"Database=(?<d>[A-Za-z0-9_]+)[^""\n]*?Host=(?<h>[A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase))
                Register(dbByHost, m.Groups["h"].Value, m.Groups["d"].Value);
            var host = Regex.Match(text, @"^\s*-?\s*""?[A-Z0-9_]*HOST[A-Z0-9_]*""?\s*[:=]\s*""?(?<v>[A-Za-z0-9_.-]+)""?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var db = Regex.Match(text, @"^\s*-?\s*""?[A-Z0-9_]*(DATABASE|_DB)""?\s*[:=]\s*""?(?<v>[A-Za-z0-9_]+)""?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (host.Success && db.Success) Register(dbByHost, host.Groups["v"].Value, db.Groups["v"].Value);
        }
        if (dbByHost.Count == 0) return yaml;

        foreach (var name in ServiceBlocks(lines).Where(b => dbByHost.ContainsKey(b.Name)).Select(b => b.Name).ToList())
        {
            var b = ServiceBlocks(lines).First(x => x.Name == name);   // re-find: earlier inserts shift indices
            var text = string.Join("\n", lines.GetRange(b.Start, b.End - b.Start));
            var image = Regex.Match(text, @"image:\s*""?(?<i>[^""\n]+)", RegexOptions.IgnoreCase).Groups["i"].Value.ToLowerInvariant();
            var key = image.Contains("postgres") ? "POSTGRES_DB"
                : image.Contains("mysql") || image.Contains("mariadb") ? "MYSQL_DATABASE" : null;
            if (key is null || Regex.IsMatch(text, $@"\b{key}\b")) continue;   // not a DB image, or app already set it
            InsertEnvPair(lines, b, key, dbByHost[name]);
        }
        return string.Join("\n", lines);
    }

    private static void Register(Dictionary<string, string> map, string host, string db)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(db)) return;
        if (db is "postgres" or "mysql" or "root") return;                    // default DBs already exist
        if (!map.ContainsKey(host)) map[host] = db;
    }

    // Ordered service blocks under the top-level `services:` key: (name, first line, one-past-last line).
    private static List<(string Name, int Start, int End)> ServiceBlocks(List<string> lines)
    {
        var res = new List<(string, int, int)>();
        var svc = lines.FindIndex(l => Regex.IsMatch(l, @"^services:\s*$"));
        if (svc < 0) return res;
        for (var i = svc + 1; i < lines.Count; i++)
        {
            if (Regex.IsMatch(lines[i], @"^\S")) break;
            var m = Regex.Match(lines[i], @"^  (?<n>[A-Za-z0-9_.-]+):\s*$");
            if (!m.Success) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
                if (Regex.IsMatch(lines[j], @"^ {0,2}\S")) { end = j; break; }
            res.Add((m.Groups["n"].Value, i, end));
        }
        return res;
    }

    private static void InsertEnvPair(List<string> lines, (string Name, int Start, int End) b, string key, string val)
    {
        var envIdx = -1;
        for (var i = b.Start + 1; i < b.End; i++) if (Regex.IsMatch(lines[i], @"^    environment:\s*$")) { envIdx = i; break; }
        if (envIdx < 0) { lines.InsertRange(b.Start + 1, new[] { "    environment:", $"      {key}: \"{val}\"" }); return; }
        var list = envIdx + 1 < b.End && Regex.IsMatch(lines[envIdx + 1], @"^      - ");   // list vs mapping style
        lines.Insert(envIdx + 1, list ? $"      - \"{key}={val}\"" : $"      {key}: \"{val}\"");
    }

    public Deployment Deploy(StackModel stack, string publishRoot, string host = "localhost",
        bool hostDashboard = true, string? dashboardToken = null, string? cloneSrc = null, string? targetId = null)
    {
        var project = Project(stack.Id);
        var now = DateTime.UtcNow.ToString("O");
        var existing = store.GetByStack(stack.Id);
        var id = existing?.Id ?? "dep" + Guid.NewGuid().ToString("n")[..8];
        // Redeploying an app keeps it where it is unless a target was asked for explicitly.
        var target = TargetById(targetId ?? existing?.TargetId ?? targets?.Resolve(null).Id);
        // Only this machine's URLs may use the request's host; a remote box has its own address.
        if (!target.IsLocal) host = target.HostForUrls();
        store.Upsert(new Deployment(id, stack.Id, stack.Name, existing?.ComposeDir ?? "", project, "deploying",
            existing?.Urls ?? new(), existing?.CreatedAt ?? now, now, null, existing?.Ports, TargetId: target.Id));
        if (!TargetKind.IsCompose(target.Kind))
            return OrchestratorDeploy(stack, publishRoot, id, target, cloneSrc);
        var runner = R(target);
        try
        {
            var pub = publish.Publish(stack, publishRoot, "compose", cloneSrc);
            if (!pub.Ok) { store.SetState(id, "failed", pub.Log); return store.Get(id)!; }
            var path = Path.Combine(pub.OutputDir, "docker-compose.yaml");
            var raw = InjectDockerfileBuilds(EnsureCompanionDatabases(ConfigureDashboard(AddRestartPolicy(File.ReadAllText(path)), hostDashboard, dashboardToken)), stack, Path.Combine(publishRoot, "src"));
            var needsBuild = stack.Nodes.Any(n => n.AddMethod == "AddDockerfile");
            // Ports are per machine: what other apps on *this* target use, plus whatever else that
            // daemon already publishes (containers we did not create).
            var used = new HashSet<int>(store.List().Where(x => x.Id != id && x.Target == target.Id)
                .SelectMany(x => (x.Ports ?? new()).Where(p => p.Public).Select(p => p.Host)));
            if (!target.IsLocal && targets is not null)
                foreach (var p in targets.UsedPortsOn(target)) used.Add(p);
            bool Free(int p) => target.IsLocal ? PortFree(p) : !used.Contains(p);
            var prev = (existing?.Ports ?? new()).ToDictionary(p => p.Container);
            var chosen = new List<PortMapping>();
            var portMap = new Dictionary<int, int>();
            var keepInternal = new HashSet<int>();
            foreach (var cp in ExposedAppPorts(raw).Distinct())
            {
                if (prev.TryGetValue(cp, out var pm) && !pm.Public) { keepInternal.Add(cp); chosen.Add(new(cp, 0, false)); continue; }
                var pinned = prev.TryGetValue(cp, out var pp) && pp.Public && pp.Host > 0 ? pp.Host : 0;
                var hostPort = pinned > 0 && !used.Contains(pinned) && Free(pinned)
                    ? pinned : AllocateHostPort(used, target.PortFrom, target.PortTo, Free);
                used.Add(hostPort); portMap[cp] = hostPort; chosen.Add(new(cp, hostPort, true));
            }
            var processed = PublishExposedPorts(raw, portMap, keepInternal);
            File.WriteAllText(path, processed);
            FillParameterEnv(stack, Path.Combine(pub.OutputDir, ".env"));
            var up = runner.UpProject(pub.OutputDir, project, needsBuild);
            var urls = up.Ok ? UrlsFromServices(ParseServices(runner.Ps(pub.OutputDir, project).Log), host) : new();
            if (urls.Count == 0) urls = ParseUrls(processed, host);
            if (!string.IsNullOrWhiteSpace(stack.HostingUrlPath))
                urls = urls.Select(u => Regex.IsMatch(u, @"://[^/]+:\d+$") ? u + stack.HostingUrlPath : u).ToList();
            if (proxy is { Enabled: true } && FirstPort(urls) is > 0) urls.Insert(0, proxy.UrlFor(stack.Name));
            // Don't report green while the containers are still booting or already crash-looping.
            var (health, detail) = up.Ok ? Settle(pub.OutputDir, project, runner: runner) : ("unknown", null);
            store.Upsert(store.Get(id)! with
            {
                ComposeDir = pub.OutputDir, Urls = urls, Ports = chosen,
                State = up.Ok ? "running" : "failed", LastError = up.Ok ? null : up.Log,
                Health = health, HealthDetail = detail,
                UpdatedAt = DateTime.UtcNow.ToString("O"),
            });
            if (up.Ok) SyncProxy();
        }
        catch (Exception ex) { store.SetState(id, "failed", ex.Message); }
        return store.Get(id)!;
    }

    public Deployment? Update(string id)
    {
        if (store.Get(id) is not { } d) return null;
        store.SetState(id, "deploying");
        if (IsOrchestrated(d) && orchestrator is not null)
        {
            var res = orchestrator.Restart(d);
            store.SetState(id, res.Ok ? "running" : "failed", res.Ok ? null : res.Log);
            return store.Get(id);
        }
        var pull = R(d).PullProject(d.ComposeDir, d.Project);
        var up = R(d).UpProject(d.ComposeDir, d.Project);
        store.SetState(id, up.Ok ? "running" : "failed", up.Ok ? null : $"{pull.Log}\n{up.Log}");
        if (up.Ok) SyncProxy();
        return store.Get(id);
    }

    public List<ServiceStatus> Services(string id)
    {
        if (store.Get(id) is not { } d) return new();
        if (IsOrchestrated(d)) return orchestrator?.Services(d) ?? new();
        return ParseServices(R(d).Ps(d.ComposeDir, d.Project).Log);
    }

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
        catch { return list; }
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

    // "running" only says compose started the containers. This says whether they actually work:
    // a container that keeps restarting, exited, or reports unhealthy makes the whole app broken,
    // which is what a user sees as "it says running but nothing answers".
    // The bundled aspire-dashboard sidecar is ours, not the app — it never decides the app's state.
    public static List<ServiceStatus> AppContainers(IEnumerable<ServiceStatus> services) =>
        services.Where(s => !s.Name.Contains("dashboard") && !s.Service.Contains("dashboard")).ToList();

    public static (string Health, string? Detail) HealthOf(IEnumerable<ServiceStatus> services)
    {
        var list = AppContainers(services);
        if (list.Count == 0) return ("unknown", null);

        string? Health(ServiceStatus s) => Regex.Match(s.Status, @"\((healthy|unhealthy|health: starting|starting)\)").Groups[1].Value is { Length: > 0 } h ? h : null;
        string Name(ServiceStatus s) => string.IsNullOrWhiteSpace(s.Service) ? s.Name : s.Service;

        var broken = list.FirstOrDefault(s => s.State.Contains("restarting", StringComparison.OrdinalIgnoreCase)
                                           || Regex.IsMatch(s.Status, @"Restarting", RegexOptions.IgnoreCase));
        if (broken is not null) return ("failing", $"{Name(broken)} keeps restarting — {broken.Status}");

        var dead = list.FirstOrDefault(s => s.State.Contains("exited", StringComparison.OrdinalIgnoreCase)
                                         || s.State.Contains("dead", StringComparison.OrdinalIgnoreCase));
        if (dead is not null) return ("failing", $"{Name(dead)} stopped — {dead.Status}");

        var unhealthy = list.FirstOrDefault(s => Health(s) == "unhealthy");
        if (unhealthy is not null) return ("unhealthy", $"{Name(unhealthy)} reports unhealthy — {unhealthy.Status}");

        var starting = list.FirstOrDefault(s => Health(s) is "starting" or "health: starting");
        if (starting is not null) return ("starting", $"{Name(starting)} is still starting up");

        return ("ok", null);
    }

    // After `compose up` the containers exist but may still be booting, migrating or crash-looping.
    // Wait for a verdict instead of reporting green immediately.
    public (string Health, string? Detail) Settle(string composeDir, string project, int timeoutSeconds = 45,
        DeployService? runner = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var okStreak = 0;
        (string Health, string? Detail) last = ("unknown", null);
        while (true)
        {
            last = HealthOf(ParseServices((runner ?? deploy).Ps(composeDir, project).Log));
            if (last.Health is "failing" or "unhealthy") return last;    // a verdict worth trusting at once
            // "running" right after `up` means "has not crashed yet" — only a stable ok counts.
            okStreak = last.Health == "ok" ? okStreak + 1 : 0;
            if (okStreak >= 3 || DateTime.UtcNow >= deadline) return last;
            Thread.Sleep(2000);
        }
    }

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
                if (Regex.IsMatch(line, @"^\S")) break;
                var m = Regex.Match(line, @"^  (\S[^:]*):");
                if (m.Success) names.Add(m.Groups[1].Value.Trim());
            }
        }
        return names;
    }

    public record VolInfo(string Name, long SizeMb);
    public record VolEntry(string Name, bool Dir, long Size);

    public List<string> VolumesOf(string id)
    {
        if (store.Get(id) is not { } d) return new();
        var p = Path.Combine(d.ComposeDir, "docker-compose.yaml");
        return File.Exists(p) ? VolumeNames(File.ReadAllText(p)) : new();
    }

    public List<VolInfo> VolumeSizes(string id)
    {
        if (store.Get(id) is not { } d) return new();
        return VolumesOf(id).Select(v =>
        {
            var kb = R(d).VolumeDu($"{d.Project}_{v}").Log.Split('\t', ' ').FirstOrDefault();
            return new VolInfo(v, long.TryParse(kb, out var k) ? k / 1024 : 0);
        }).ToList();
    }

    public List<VolEntry> BrowseVolume(string id, string vol, string relPath)
    {
        if (store.Get(id) is not { } d || !VolumesOf(id).Contains(vol)) return new();
        var r = R(d).VolumeLs($"{d.Project}_{vol}", relPath);
        var list = new List<VolEntry>();
        foreach (var raw in r.Log.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith("total ")) continue;
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 9) continue;
            var name = string.Join(' ', cols[8..]);
            if (name is "." or "..") continue;
            var isDir = line[0] == 'd';
            if (line[0] == 'l' && name.Contains(" -> ")) name = name[..name.IndexOf(" -> ", StringComparison.Ordinal)];
            long.TryParse(cols[4], out var size);
            list.Add(new VolEntry(name, isDir, size));
        }
        return list.OrderByDescending(e => e.Dir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public (byte[]? data, string? error) ReadVolumeFile(string id, string vol, string relPath)
    {
        if (store.Get(id) is not { } d || !VolumesOf(id).Contains(vol)) return (null, "no such volume");
        return R(d).VolumeCat($"{d.Project}_{vol}", relPath);
    }

    // Streamed out of the volume rather than written through a bind mount: a mount lands on the
    // *daemon's* host, which is another machine as soon as the target is remote. stdout comes back here.
    public string? Backup(string id, string backupsRoot)
    {
        if (store.Get(id) is not { } d) return null;
        var composePath = Path.Combine(d.ComposeDir, "docker-compose.yaml");
        if (!File.Exists(composePath)) return null;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(backupsRoot, d.StackId, stamp);
        Directory.CreateDirectory(dir);
        var wrote = 0;
        foreach (var vol in VolumeNames(File.ReadAllText(composePath)))
        {
            var file = Path.Combine(dir, vol + ".tgz");
            DeployResult r;
            using (var gz = new System.IO.Compression.GZipStream(File.Create(file), System.IO.Compression.CompressionLevel.Optimal))
                r = R(d).VolumeTarOutTo($"{d.Project}_{vol}", gz);
            if (!r.Ok || new FileInfo(file).Length <= 32)
            {
                try { File.Delete(file); } catch { }
                File.AppendAllText(Path.Combine(dir, "errors.log"), $"{vol}: {(r.Log.Length > 0 ? r.Log : "empty")}" + Environment.NewLine);
                continue;
            }
            wrote++;
        }
        return wrote > 0 ? dir : null;
    }

    private static readonly Regex StampRe = new(@"^\d{8}-\d{6}$");
    private static string StampPath(Deployment d, string root, string stamp) => Path.Combine(root, d.StackId, stamp);

    public List<BackupInfo> ListBackups(string id, string backupsRoot)
    {
        if (store.Get(id) is not { } d) return new();
        var root = Path.Combine(backupsRoot, d.StackId);
        if (!Directory.Exists(root)) return new();
        var list = new List<BackupInfo>();
        foreach (var dir in Directory.GetDirectories(root).OrderByDescending(x => x))
        {
            var stamp = Path.GetFileName(dir);
            var vols = Directory.GetFiles(dir, "*.tgz")
                .Select(f => new BackupVol(Path.GetFileNameWithoutExtension(f), new FileInfo(f).Length)).ToList();
            if (vols.Count == 0) continue;
            var iso = DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
                ? dt.ToString("O") : stamp;
            list.Add(new BackupInfo(stamp, iso, vols));
        }
        return list;
    }

    public bool Restore(string id, string backupsRoot, string stamp)
    {
        if (store.Get(id) is not { } d || !StampRe.IsMatch(stamp)) return false;
        var dir = StampPath(d, backupsRoot, stamp);
        if (!Directory.Exists(dir)) return false;
        var composePath = Path.Combine(d.ComposeDir, "docker-compose.yaml");
        var current = File.Exists(composePath) ? VolumeNames(File.ReadAllText(composePath)).ToHashSet() : new();
        store.SetState(id, "deploying");
        try
        {
            R(d).StopProject(d.ComposeDir, d.Project);
            foreach (var f in Directory.GetFiles(dir, "*.tgz"))
            {
                var vol = Path.GetFileNameWithoutExtension(f);
                if (current.Count > 0 && !current.Contains(vol)) continue;
                using var gz = new System.IO.Compression.GZipStream(File.OpenRead(f), System.IO.Compression.CompressionMode.Decompress);
                using var buf = new MemoryStream();
                gz.CopyTo(buf);
                buf.Position = 0;
                R(d).VolumeTarIn($"{d.Project}_{vol}", buf);
            }
            var up = R(d).UpProject(d.ComposeDir, d.Project);
            store.SetState(id, up.Ok ? "running" : "failed", up.Ok ? null : up.Log);
            SyncProxy();
            return up.Ok;
        }
        catch (Exception ex) { store.SetState(id, "failed", ex.Message); return false; }
    }

    public bool DeleteBackup(string id, string backupsRoot, string stamp)
    {
        if (store.Get(id) is not { } d || !StampRe.IsMatch(stamp)) return false;
        var dir = StampPath(d, backupsRoot, stamp);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, true);
        return true;
    }

    public string? BackupDir(string id, string backupsRoot, string stamp)
    {
        if (store.Get(id) is not { } d || !StampRe.IsMatch(stamp)) return null;
        var dir = StampPath(d, backupsRoot, stamp);
        return Directory.Exists(dir) ? dir : null;
    }

    public record MoveResult(bool Ok, string Log, Deployment? Deployment);

    // Moves a running app to another machine: data first (with the app stopped, so it is consistent),
    // then the deployment itself, then the source is torn down. On any failure before the source is
    // removed the app is started again where it was, so a failed move never loses an app.
    public MoveResult Move(StackModel stack, string publishRoot, string host, bool hostDashboard,
        string? dashboardToken, string? cloneSrc, string toTargetId, bool withData = true)
    {
        if (store.GetByStack(stack.Id) is not { } d) return new MoveResult(false, "this stack is not deployed", null);
        var from = TargetOf(d);
        var to = TargetById(toTargetId);
        if (from.Id == to.Id) return new MoveResult(false, $"it already runs on {to.Name}", d);
        // Same machine under two names: the compose project is one and the same there, so "moving" would
        // only tear the app down. Nothing to do but say so.
        if (targets is not null && TargetKind.IsCompose(from.Kind) && TargetKind.IsCompose(to.Kind)
            && targets.DaemonId(from) is { Length: > 0 } fromId && targets.DaemonId(to) == fromId)
            return new MoveResult(false, $"'{from.Name}' and '{to.Name}' are the same docker daemon — the app already runs there", d);
        var log = new StringBuilder();
        var vols = VolumesOf(d.Id);
        var moveData = withData && vols.Count > 0 && TargetKind.IsCompose(from.Kind) && TargetKind.IsCompose(to.Kind);
        if (withData && !moveData && vols.Count > 0)
            log.AppendLine($"note: {vols.Count} volume(s) are not moved — {(TargetKind.IsCompose(from.Kind) ? to.Name : from.Name)} has no docker volumes");

        var sourceDir = d.ComposeDir;
        var sourceProject = d.Project;
        try
        {
            if (TargetKind.IsCompose(from.Kind))
            {
                log.AppendLine($"stopping {d.Name} on {from.Name}");
                R(from).StopProject(sourceDir, sourceProject);
            }

            var moved = Deploy(stack, publishRoot, host, hostDashboard, dashboardToken, cloneSrc, to.Id);
            if (moved.State == "failed")
            {
                // Put it back the way it was: the source is still there, untouched.
                if (TargetKind.IsCompose(from.Kind)) R(from).UpProject(sourceDir, sourceProject);
                store.Upsert(moved with { TargetId = from.Id, ComposeDir = sourceDir, Project = sourceProject, State = "running" });
                return new MoveResult(false, log + Environment.NewLine + "deploy on the new target failed, the app was left where it was:" + Environment.NewLine + (moved.LastError ?? ""), store.Get(d.Id));
            }

            if (moveData)
            {
                log.AppendLine($"transferring {vols.Count} volume(s)");
                R(to).StopProject(moved.ComposeDir, moved.Project);
                foreach (var v in vols)
                {
                    var r = DeployService.TransferVolume(R(from), $"{sourceProject}_{v}", R(to), $"{moved.Project}_{v}");
                    log.AppendLine($"  {v}: {(r.Ok ? "ok" : "failed — " + r.Log)}");
                    if (!r.Ok)
                    {
                        R(from).UpProject(sourceDir, sourceProject);
                        return new MoveResult(false, log.ToString(), store.Get(d.Id));
                    }
                }
                var up = R(to).UpProject(moved.ComposeDir, moved.Project);
                log.Append(up.Log);
            }

            if (TargetKind.IsCompose(from.Kind))
            {
                log.AppendLine($"removing it from {from.Name}");
                R(from).DownProject(sourceDir, sourceProject, volumes: moveData);
            }
            var (health, detail) = TargetKind.IsCompose(to.Kind)
                ? Settle(moved.ComposeDir, moved.Project, runner: R(to)) : (moved.Health ?? "ok", moved.HealthDetail);
            store.Upsert(store.Get(d.Id)! with { Health = health, HealthDetail = detail, UpdatedAt = DateTime.UtcNow.ToString("O") });
            SyncProxy();
            return new MoveResult(true, log.ToString(), store.Get(d.Id));
        }
        catch (Exception e)
        {
            try { if (TargetKind.IsCompose(from.Kind)) R(from).UpProject(sourceDir, sourceProject); } catch { }
            return new MoveResult(false, log + Environment.NewLine + e.Message, store.Get(d.Id));
        }
    }

    // Copies the data of one deployment into another (a freshly deployed clone on another target).
    public DeployResult CopyData(Deployment from, Deployment to)
    {
        var fromT = TargetOf(from);
        var toT = TargetOf(to);
        if (!TargetKind.IsCompose(fromT.Kind) || !TargetKind.IsCompose(toT.Kind))
            return new DeployResult(false, "data can only be copied between docker targets");
        if (targets is not null && targets.DaemonId(fromT) is { Length: > 0 } sameId && targets.DaemonId(toT) == sameId
            && from.Project == to.Project)
            return new DeployResult(false, "source and destination are the same docker daemon and the same project");
        var vols = VolumesOf(from.Id);
        if (vols.Count == 0) return new DeployResult(true, "no volumes to copy");
        var log = new StringBuilder();
        R(fromT).StopProject(from.ComposeDir, from.Project);
        R(toT).StopProject(to.ComposeDir, to.Project);
        var ok = true;
        foreach (var v in vols)
        {
            var r = DeployService.TransferVolume(R(fromT), $"{from.Project}_{v}", R(toT), $"{to.Project}_{v}");
            log.AppendLine($"{v}: {(r.Ok ? "ok" : "failed — " + r.Log)}");
            ok &= r.Ok;
        }
        R(fromT).UpProject(from.ComposeDir, from.Project);
        R(toT).UpProject(to.ComposeDir, to.Project);
        return new DeployResult(ok, log.ToString());
    }

    public void ReconcileOnStartup()
    {
        var any = false;
        foreach (var d in store.List().Where(x => x.State == "running"))
        {
            try
            {
                if (IsOrchestrated(d)) continue;   // nothing to restart: the platform keeps them running
                if (File.Exists(Path.Combine(d.ComposeDir, "docker-compose.yaml"))) { R(d).UpProject(d.ComposeDir, d.Project); any = true; }
            }
            catch { }
        }
        if (any) SyncProxy();
    }

    public void Stop(string id)
    {
        if (store.Get(id) is not { } d) return;
        if (IsOrchestrated(d) && orchestrator is not null)
        {
            var r = orchestrator.Scale(d, 0);
            store.SetState(id, r.Ok ? "stopped" : "failed", r.Ok ? null : r.Log);
            return;
        }
        R(d).StopProject(d.ComposeDir, d.Project);
        store.SetState(id, "stopped");
        SyncProxy();
    }
    public void Start(string id)
    {
        if (store.Get(id) is not { } d) return;
        store.SetState(id, "deploying");
        if (IsOrchestrated(d) && orchestrator is not null)
        {
            var s = orchestrator.Scale(d, 1);
            store.SetState(id, s.Ok ? "running" : "failed", s.Ok ? null : s.Log);
            return;
        }
        var r = R(d).UpProject(d.ComposeDir, d.Project);
        store.SetState(id, r.Ok ? "running" : "failed", r.Ok ? null : r.Log);
        SyncProxy();
    }
    public void Undeploy(string id, bool wipe = false)
    {
        if (store.Get(id) is not { } d) return;
        if (IsOrchestrated(d) && orchestrator is not null) orchestrator.Remove(d);
        else R(d).DownProject(d.ComposeDir, d.Project, wipe);
        store.Delete(id);
        SyncProxy();
    }

    public Deployment? Refresh(string id)
    {
        if (store.Get(id) is not { } d) return null;
        if (d.State is "deploying") return d;
        if (IsOrchestrated(d)) return orchestrator?.Refresh(d) ?? d;
        var ps = R(d).Ps(d.ComposeDir, d.Project);
        var services = ParseServices(ps.Log);
        // Only the app's own containers count: a project where just our dashboard sidecar is up is not running.
        var running = ps.Ok && AppContainers(services).Any(s =>
            s.State.Contains("running", StringComparison.OrdinalIgnoreCase) ||
            s.State.Contains("restarting", StringComparison.OrdinalIgnoreCase));
        var (health, detail) = running ? HealthOf(services) : ("unknown", (string?)null);
        var next = d.State is "failed" && !running ? "failed" : running ? "running" : "stopped";
        if (next != d.State || health != d.Health || detail != d.HealthDetail)
            store.Upsert(d with { State = next, Health = health, HealthDetail = detail, UpdatedAt = DateTime.UtcNow.ToString("O") });
        return store.Get(id);
    }
}
