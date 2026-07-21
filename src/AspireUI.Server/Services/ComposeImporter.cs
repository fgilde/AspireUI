using AspireUI.Server.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AspireUI.Server.Services;

// Import a docker-compose.yml into a stack: each service becomes an AddContainer node, with
// ports -> WithHttpEndpoint, environment -> WithEnvironment, and depends_on -> WaitFor edges.
public class ComposeImporter
{
    private sealed class ComposeFile { public Dictionary<string, ComposeService>? Services { get; set; } }
    private sealed class ComposeService
    {
        public string? Image { get; set; }
        public List<string>? Ports { get; set; }
        public object? Environment { get; set; }         // list ("K=V") or map (K: V)
        public object? DependsOn { get; set; }            // list or map (long syntax)
        public List<string>? Command { get; set; }
    }

    public (StackModel? stack, string? error) Import(string id, string name, string yaml)
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
            var varName = UniqueVar(Sanitize(svc), used);
            var withs = new List<WithCall>();
            foreach (var p in def.Ports ?? [])
            {
                var (host, target) = SplitPort(p);
                if (host is null) continue;
                var args = new List<string> { $"port: {host}" };
                if (target is not null && target != host) args.Add($"targetPort: {target}");
                withs.Add(new WithCall("WithHttpEndpoint", args));
            }
            foreach (var (k, v) in ReadEnv(def.Environment))
                withs.Add(new WithCall("WithEnvironment", [Quote(k), Quote(v)]));

            var id2 = "n" + Guid.NewGuid().ToString("n")[..8];
            nameToId[svc] = id2;
            var addArgs = string.IsNullOrWhiteSpace(def.Image) ? new List<string>() : [Quote(def.Image!)];
            nodes.Add(new NodeModel(id2, varName, "AddContainer", svc, withs, 60 + (i % 3) * 320, 60 + (i / 3) * 200, addArgs));
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
        // "8080:80", "127.0.0.1:8080:80", "8080", "8080:80/tcp"
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
