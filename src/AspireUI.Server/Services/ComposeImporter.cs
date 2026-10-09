using System.Text.RegularExpressions;
using AspireUI.Server.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AspireUI.Server.Services;

// Import docker-compose.yml into stack: each service→AddContainer; ports→WithHttpEndpoint; depends_on→WaitFor.
public class ComposeImporter
{
    // Compose files share settings through a YAML anchor in a top-level x- block and a merge key in
    // each service. The typed model below knows nothing of those blocks, and a skipped node registers
    // no anchor, so every alias under it would fail the file. Reading the document untyped registers
    // each anchor and the merging parser expands each <<:, leaving a plain document to read.
    private static object? ReadGraph(string yaml) =>
        new DeserializerBuilder().Build()
            .Deserialize<object>(new MergingParser(new Parser(new StringReader(yaml))));

    // A shared node would be written back as an anchor and aliases, and the typed pass skips the block
    // the anchor sits in all over again. DisableAliases writes it out in full instead.
    private static readonly ISerializer Flat = new SerializerBuilder().DisableAliases().Build();

    /// <summary>The same document with its anchors and merge keys resolved.</summary>
    public static string Plain(string yaml) => ReadGraph(yaml) is { } graph ? Flat.Serialize(graph) : yaml;

    // Overlay-merge multiple compose files (later wins), like `docker compose -f a -f b`. Keeps original short/long syntax.
    public static string Merge(IReadOnlyList<string> yamls)
    {
        if (yamls.Count <= 1) return yamls.Count == 1 ? yamls[0] : "";
        object? acc = null;
        foreach (var y in yamls)
        {
            object? cur;
            try { cur = ReadGraph(y); } catch { continue; }
            if (cur is null) continue;
            acc = acc is null ? cur : DeepMerge(acc, cur);
        }
        return acc is null ? yamls[0] : Flat.Serialize(acc);
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

    // Compose's four forms: ${VAR}, ${VAR:-default} (this if unset), ${VAR:?message} (a complaint,
    // never a value) and ${VAR:+alternative} (this only if set). A supplied value wins where one is
    // wanted. The text between the braces may itself hold another ${...}, as an image name usually
    // does, so the pattern takes only brace-free bodies — the innermost — and the loop works outward.
    //
    // $$ comes first, because it is how a compose file writes a dollar that is not its own: an
    // embedded shell script is full of them. Reading $${VAR:+x} as an interpolation and answering it
    // with nothing rewrites the script — which is how a one-shot service ended up running a line its
    // author never wrote.
    private static readonly Regex Interpolation =
        new(@"\$\$|\$\{([A-Za-z0-9_]+)(?:(:[-?+])([^{}]*))?\}", RegexOptions.Compiled);

    public static string ResolveEnv(string yaml, IReadOnlyDictionary<string, string>? env)
    {
        for (var pass = 0; pass < 10; pass++)
        {
            var next = Interpolation.Replace(yaml, m =>
            {
                if (m.Value == "$$") return m.Value;
                string? v = null;
                env?.TryGetValue(m.Groups[1].Value, out v);
                var supplied = string.IsNullOrEmpty(v) ? null : v;
                return m.Groups[2].Value switch
                {
                    ":+" => supplied is null ? "" : m.Groups[3].Value,
                    ":-" => supplied ?? m.Groups[3].Value,
                    _ => supplied ?? "",
                };
            });
            if (next == yaml) return yaml;
            yaml = next;
        }
        return yaml;
    }

    private sealed class ComposeFile { public Dictionary<string, ComposeService>? Services { get; set; } }
    private sealed class ComposeService
    {
        public string? Image { get; set; }
        public object? Build { get; set; }
        public List<string>? Ports { get; set; }
        public List<object>? Expose { get; set; }
        public object? Environment { get; set; }
        public object? EnvFile { get; set; }
        public object? DependsOn { get; set; }
        public object? Command { get; set; }
        public object? Entrypoint { get; set; }
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
        string? srcDir = null, IReadOnlyDictionary<string, int>? servicePorts = null, IReadOnlyDictionary<string, string>? env = null)
    {
        ComposeFile? file;
        yaml = ResolveEnv(yaml, null);
        try
        {
            file = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<ComposeFile>(Plain(yaml));
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
            var declared = ReadEnv(def.Environment).ToList();
            foreach (var (k, v) in declared)
                withs.Add(new WithCall("WithEnvironment", [Quote(k), Quote(v)]));
            foreach (var (k, v) in ReadEnvFiles(srcDir, def.EnvFile, env))
                if (!declared.Any(d => d.Item1 == k))
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
            // An entrypoint replaces the image's own, and the command is what follows it — the pair
            // is how a one-shot service (create a bucket, run a migration) says what it does.
            var entrypoint = ReadCommand(def.Entrypoint);
            var cmdArgs = ReadCommand(def.Command);
            if (entrypoint.Count > 0)
            {
                withs.Add(new WithCall("WithEntrypoint", [Quote(entrypoint[0])]));
                cmdArgs = [.. entrypoint.Skip(1), .. cmdArgs];
            }
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

    /// <summary>
    /// The generated values went into the compose as literals, which is the only way it parses. Here
    /// they become parameters instead: every environment value that is one of them points at the
    /// parameter node, so the same secret stays one secret across the services that share it.
    /// </summary>
    public static (List<NodeModel> Services, List<NodeModel> Parameters) AsParameters(
        List<NodeModel> services, IReadOnlyDictionary<string, string> secrets, int x = 1240)
    {
        var used = new HashSet<string>();
        var parameters = new List<NodeModel>();
        var byValue = new Dictionary<string, string>();          // generated value → parameter variable

        var i = 0;
        foreach (var (key, value) in secrets)
        {
            var resourceName = key.ToLowerInvariant().Replace('_', '-');
            var varName = ParameterVar(resourceName);
            if (!used.Add(varName)) continue;
            byValue[value] = varName;
            parameters.Add(new NodeModel("n" + Guid.NewGuid().ToString("n")[..8], varName, "AddParameter", resourceName,
                [], x, 60 + i++ * 90, [Quote(value), "true", "false"]));
        }

        var rewritten = services.Select(n => n with
        {
            WithCalls = n.WithCalls.Select(w => w.Method == "WithEnvironment" && w.Args.Count == 2
                && byValue.TryGetValue(w.Args[1].Trim('"'), out var variable)
                    ? w with { Args = [w.Args[0], variable] }
                    : w).ToList(),
        }).ToList();

        return (rewritten, parameters);
    }

    private static string ParameterVar(string name)
    {
        var c = new string((name ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        return c.Length == 0 ? "parameter" : char.IsAsciiDigit(c[0]) ? "_" + c : c;
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

    public static IEnumerable<(string, string)> ReadEnvFiles(string? srcDir, object? envFile, IReadOnlyDictionary<string, string>? env)
    {
        var names = envFile switch
        {
            string s => [s],
            List<object> list => list.Select(x => x is Dictionary<object, object> d && d.TryGetValue("path", out var p) ? p?.ToString() : x?.ToString()),
            _ => [],
        };
        var values = new Dictionary<string, string>();
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            foreach (var path in new[] { name + ".example", name! })
            {
                var full = Path.Combine(srcDir ?? "", path!);
                if (srcDir is null || !File.Exists(full)) continue;
                foreach (var (k, v) in ParseDotEnv(File.ReadAllText(full))) values[k] = v;
            }
        foreach (var k in values.Keys.ToList())
            if (env is not null && env.TryGetValue(k, out var given) && given.Length > 0) values[k] = given;
        return values.Where(kv => kv.Value.Length > 0).Select(kv => (kv.Key, kv.Value));
    }

    public static IEnumerable<(string, string)> ParseDotEnv(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ")) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (line.StartsWith('#') || eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            else if (value.IndexOf(" #") is var hash and >= 0) value = value[..hash].TrimEnd();
            yield return (line[..eq].Trim(), value);
        }
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

    // A value may be a whole configuration file, handed over as a YAML block scalar. Its newlines
    // end a C# string literal, so everything that cannot stand inside one goes in escaped.
    internal static string Quote(string s) => "\"" + s
        .Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")
        + "\"";

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
