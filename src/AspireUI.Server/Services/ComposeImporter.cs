using System.Text.RegularExpressions;
using AspireUI.Server.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AspireUI.Server.Services;

// Import docker-compose.yml into stack: each service→AddContainer; ports→WithHttpEndpoint; depends_on→WaitFor.
public class ComposeImporter
{
    // Overlay-merge multiple compose files (later wins), like `docker compose -f a -f b`. Keeps original short/long syntax.
    public static string Merge(IReadOnlyList<string> yamls)
    {
        if (yamls.Count <= 1) return yamls.Count == 1 ? yamls[0] : "";
        var des = new DeserializerBuilder().Build();
        var ser = new SerializerBuilder().Build();
        object? acc = null;
        foreach (var y in yamls)
        {
            object? cur;
            try { cur = des.Deserialize<object>(y); } catch { continue; }
            if (cur is null) continue;
            acc = acc is null ? cur : DeepMerge(acc, cur);
        }
        return acc is null ? yamls[0] : ser.Serialize(acc);
    }

    private static object DeepMerge(object a, object b)
    {
        if (a is IDictionary<object, object> da && b is IDictionary<object, object> db)
        {
            foreach (var (k, v) in db)
                da[k] = da.TryGetValue(k, out var ex) && ex is not null && v is not null ? DeepMerge(ex, v) : v;
            return da;
        }
        return b;
    }

    // Interpolate ${VAR} / ${VAR:-default} with supplied values (blank/missing → default → empty).
    public static string ResolveEnv(string yaml, IReadOnlyDictionary<string, string>? env) =>
        Regex.Replace(yaml, @"\$\{([A-Za-z0-9_]+)(?::-([^}]*))?\}", m =>
        {
            var name = m.Groups[1].Value;
            if (env is not null && env.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v)) return v;
            return m.Groups[2].Success ? m.Groups[2].Value : "";
        });

    private sealed class ComposeFile { public Dictionary<string, ComposeService>? Services { get; set; } }
    private sealed class ComposeService
    {
        public string? Image { get; set; }
        public object? Build { get; set; }
        public List<string>? Ports { get; set; }
        public List<object>? Expose { get; set; }
        public object? Environment { get; set; }
        public object? DependsOn { get; set; }
        public object? Command { get; set; }
        public List<string>? Volumes { get; set; }
    }

    private static (string? context, string? dockerfile) ReadBuild(object? build) => build switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => (s, null),
        IDictionary<object, object> d => (
            d.TryGetValue("context", out var c) ? c?.ToString() : ".",
            d.TryGetValue("dockerfile", out var f) ? f?.ToString() : null),
        _ => (null, null),
    };

    // Last resort for a service that declares no ports and no expose: the EXPOSE of the Dockerfile it
    // builds from. Without any port the imported app runs but is unreachable — compose files that put
    // the app behind their own reverse proxy (Caddy/nginx) look exactly like that.
    public static int? DockerfilePort(string? srcDir, string? context, string? dockerfile)
    {
        if (string.IsNullOrWhiteSpace(srcDir)) return null;
        var dir = Path.GetFullPath(Path.Combine(srcDir, (context ?? ".").Replace('\\', '/').TrimStart('.', '/')));
        var file = Path.Combine(dir, string.IsNullOrWhiteSpace(dockerfile) ? "Dockerfile" : dockerfile!);
        if (!File.Exists(file)) return null;
        int? port = null;
        foreach (var line in File.ReadLines(file))
        {
            var m = Regex.Match(line.Trim(), @"^EXPOSE\s+(\d{2,5})", RegexOptions.IgnoreCase);
            if (m.Success) port ??= int.Parse(m.Groups[1].Value);   // first EXPOSE wins
        }
        return port;
    }

    public (StackModel? stack, string? error) Import(string id, string name, string yaml, IReadOnlySet<string>? include = null,
        string? srcDir = null, IReadOnlyDictionary<string, int>? servicePorts = null)
    {
        ComposeFile? file;
        try
        {
            file = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<ComposeFile>(yaml);
        }
        catch (Exception ex) { return (null, "Could not parse compose YAML: " + ex.Message); }

        if (file?.Services is not { Count: > 0 }) return (null, "No services found in the compose file.");

        var nodes = new List<NodeModel>();
        var nameToId = new Dictionary<string, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (var (svc, def) in file.Services)
        {
            if (include is not null && !include.Contains(svc)) continue;
            var varName = UniqueVar(Sanitize(svc), used);
            var withs = new List<WithCall>();
            var endpointTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in def.Ports ?? [])
            {
                var (host, target) = SplitPort(p);
                if (target is null) continue;
                var args = new List<string> { $"targetPort: {target}" };
                if (host is not null && host != target) args.Insert(0, $"port: {host}");
                withs.Add(new WithCall("WithHttpEndpoint", args));
                endpointTargets.Add(target);
            }
            foreach (var e in ReadExpose(def.Expose))
                if (endpointTargets.Add(e))
                    withs.Add(new WithCall("WithHttpEndpoint", [$"targetPort: {e}"]));
            foreach (var (k, v) in ReadEnv(def.Environment))
                withs.Add(new WithCall("WithEnvironment", [Quote(k), Quote(v)]));
            foreach (var vol in def.Volumes ?? [])
            {
                var parts = vol.Split(':');
                if (parts.Length < 2) continue;
                var (src, dst) = (parts[0], parts[1]);
                withs.Add(src.StartsWith('.') || src.StartsWith('/') || src.Contains('\\')
                    ? new WithCall("WithBindMount", [Quote(src), Quote(dst)])
                    : new WithCall("WithVolume", [Quote(src), Quote(dst)]));
            }
            var cmdArgs = ReadCommand(def.Command);
            if (cmdArgs.Count > 0) withs.Add(new WithCall("WithArgs", cmdArgs.Select(Quote).ToList()));

            string addMethod;
            List<string> addArgs;
            var (buildContext, buildFile) = ReadBuild(def.Build);
            if (!string.IsNullOrWhiteSpace(def.Image))
            {
                addMethod = "AddContainer";
                addArgs = [Quote(def.Image!)];
            }
            else
            {
                if (buildContext is null) continue;
                addMethod = "AddDockerfile";
                addArgs = buildFile is null ? [Quote(buildContext)] : [Quote(buildContext), Quote(buildFile)];
            }

            if (endpointTargets.Count == 0)
            {
                var fallback = servicePorts is not null && servicePorts.TryGetValue(svc, out var chosen) && chosen > 0
                    ? chosen
                    : DockerfilePort(srcDir, buildContext, buildFile);
                if (fallback is > 0) withs.Insert(0, new WithCall("WithHttpEndpoint", [$"targetPort: {fallback}"]));
            }

            var id2 = "n" + Guid.NewGuid().ToString("n")[..8];
            nameToId[svc] = id2;
            nodes.Add(new NodeModel(id2, varName, addMethod, svc, withs, 60 + (i % 3) * 320, 60 + (i / 3) * 200, addArgs));
            i++;
        }

        var edges = new List<EdgeModel>();
        foreach (var (svc, def) in file.Services)
            foreach (var dep in ReadDependsOn(def.DependsOn))
                if (nameToId.TryGetValue(svc, out var from) && nameToId.TryGetValue(dep, out var to))
                    edges.Add(new EdgeModel("e" + Guid.NewGuid().ToString("n")[..8], from, to, "waitFor"));

        return (new StackModel(id, name, "net10.0", nodes, edges, [], [], []), null);
    }

    private static (string? host, string? target) SplitPort(string p)
    {
        var spec = p.Split('/')[0].Trim();
        var parts = spec.Split(':');
        var nums = parts.Where(x => int.TryParse(x, out _)).ToArray();
        if (nums.Length == 0) return (null, null);
        if (nums.Length == 1) return (nums[0], nums[0]);
        return (nums[^2], nums[^1]);
    }

    private static IEnumerable<(string, string)> ReadEnv(object? env)
    {
        if (env is List<object> list)
            foreach (var item in list)
            {
                var s = item?.ToString() ?? "";
                var eq = s.IndexOf('=');
                if (eq > 0) yield return (s[..eq], s[(eq + 1)..]);
            }
        else if (env is Dictionary<object, object> map)
            foreach (var (k, v) in map)
                yield return (k?.ToString() ?? "", v?.ToString() ?? "");
    }

    private static IEnumerable<string> ReadDependsOn(object? dep)
    {
        if (dep is List<object> list) foreach (var d in list) yield return d?.ToString() ?? "";
        else if (dep is Dictionary<object, object> map) foreach (var k in map.Keys) yield return k?.ToString() ?? "";
    }

    private static IEnumerable<string> ReadExpose(List<object>? expose)
    {
        foreach (var e in expose ?? [])
        {
            var s = (e?.ToString() ?? "").Split('/')[0].Trim();
            if (s.Length > 0 && int.TryParse(s, out _)) yield return s;
        }
    }

    private static List<string> ReadCommand(object? cmd) => cmd switch
    {
        List<object> list => list.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToList(),
        string s when s.Trim().Length > 0 => s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
        _ => [],
    };

    private static string Quote(string s) => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    private static string Sanitize(string s)
    {
        var cleaned = new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (cleaned.Length == 0 || char.IsDigit(cleaned[0])) cleaned = "r" + cleaned;
        return char.ToLowerInvariant(cleaned[0]) + cleaned[1..];
    }
    private static string UniqueVar(string baseName, HashSet<string> used)
    {
        var n = baseName; var i = 2;
        while (!used.Add(n)) n = baseName + i++;
        return n;
    }
}
