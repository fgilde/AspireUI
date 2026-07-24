using System.Text.Json;
using System.Text.RegularExpressions;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// Server-side port of the frontend's buildPresetNodes (web/src/model.ts) for the DEFAULT case: a fresh
// stack (no existing nodes, no per-companion choices) — every companion is its default container, every
// param a new AddParameter. Used by the MCP install_app tool so agents can install an app the same way
// the app store does. Keep in sync with the TS version.
public static class PresetBuilder
{
    private static string J(string s) => JsonSerializer.Serialize(s);
    private static string Sanitize(string name)
    {
        var c = Regex.Replace(name ?? "", "[^A-Za-z0-9_]", "");
        var s = Regex.IsMatch(c, "^[0-9]") ? "_" + c : c;
        return s.Length == 0 ? "resource" : s;
    }
    private static string? IconForImage(string? img)
    {
        var i = (img ?? "").ToLowerInvariant();
        if (Regex.IsMatch(i, "postgres|pgvecto|vectorchord")) return "AddPostgres";
        if (i.Contains("redis")) return "AddRedis";
        if (i.Contains("mongo")) return "AddMongoDB";
        if (i.Contains("meilisearch")) return "AddMeilisearch";
        if (i.Contains("localai")) return "AddLocalAI";
        if (i.Contains("ollama")) return "AddOllama";
        return null;
    }

    public static (List<NodeModel> Nodes, List<EdgeModel> Edges) Build(ContainerPreset p)
    {
        var taken = new HashSet<string>();
        string Uniq(string b) { var n = b; var i = 2; while (!taken.Add(n)) n = $"{b}{i++}"; return n; }
        string Nid() => "n" + Guid.NewGuid().ToString("n")[..8];
        string Eid() => "e" + Guid.NewGuid().ToString("n")[..8];

        var mainName = Uniq(p.Id);
        var mainId = Nid();
        var keyName = new Dictionary<string, string> { ["__main"] = mainName };

        var companions = p.Companions ?? new();
        var plans = new List<(PresetCompanion C, string Name, string TargetId)>();
        foreach (var c in companions)
        {
            var rn = Uniq(!string.IsNullOrEmpty(c.ResourceName) ? c.ResourceName : c.Key);
            keyName[c.Key] = rn;
            plans.Add((c, rn, Nid()));
        }

        var knownKeys = keyName.Keys.ToHashSet();
        List<WithCall> ExpandEnv(List<List<string>>? env)
        {
            var outc = new List<WithCall>();
            foreach (var pair in env ?? new())
            {
                if (pair.Count < 2) continue;
                string k = pair[0], v = pair[1];
                var refs = Regex.Matches(v, @"\$\{([^}]+)\}").Select(m => m.Groups[1].Value).ToList();
                if (refs.Any(r => !knownKeys.Contains(r))) continue;
                var val = v;
                foreach (var kv in keyName) val = val.Replace("${" + kv.Key + "}", kv.Value);
                outc.Add(new WithCall("WithEnvironment", new() { J(k), J(val) }));
            }
            return outc;
        }

        // Params (all new AddParameter nodes; env references them by unquoted varName).
        var paramNodes = new List<NodeModel>();
        var paramEnvCalls = new List<WithCall>();
        var prms = p.Params ?? new();
        for (var i = 0; i < prms.Count; i++)
        {
            var param = prms[i];
            var pname = Uniq(!string.IsNullOrEmpty(param.Name) ? param.Name! : $"{p.Id}-{param.Key}");
            var varName = Sanitize(pname);
            paramEnvCalls.Add(new WithCall("WithEnvironment", new() { J(param.Env), varName }));
            paramNodes.Add(new NodeModel(Nid(), varName, "AddParameter", pname, new(), 380, 40 + (companions.Count + i) * 130,
                new() { J(param.Default ?? ""), "true", "false" }, SpawnedBy: mainId));
        }

        var main = new List<WithCall>
        {
            new("WithHttpEndpoint", p.FixedPort ? new() { $"port: {p.Port}", $"targetPort: {p.Port}" } : new() { $"targetPort: {p.Port}" }),
        };
        if (!string.IsNullOrEmpty(p.UrlPath)) main.Add(new WithCall("WithUrlForEndpoint", new() { "\"http\"", $"url => url.Url = {J(p.UrlPath!)}" }));
        if (p.Gpu) main.Add(new WithCall("WithContainerRuntimeArgs", new() { "\"--gpus\"", "\"all\"" }));
        if (p.RuntimeArgs is { Count: > 0 }) main.Add(new WithCall("WithContainerRuntimeArgs", p.RuntimeArgs.Select(J).ToList()));
        if (p.Args is { Count: > 0 }) main.Add(new WithCall("WithArgs", p.Args.Select(J).ToList()));
        foreach (var v in p.Volumes ?? new()) if (v.Count >= 2) main.Add(new WithCall("WithVolume", new() { J($"{mainName}-{v[0]}"), J(v[1]) }));
        foreach (var b in p.BindMounts ?? new())
        {
            if (b.Count < 2) continue;
            var args = new List<string> { J(b[0]), J(b[1]) };
            if (b.Count > 2 && b[2] == "ro") args.Add("isReadOnly: true");
            main.Add(new WithCall("WithBindMount", args));
        }
        main.AddRange(ExpandEnv(p.Env));
        main.AddRange(paramEnvCalls);

        var nodes = new List<NodeModel> { new(mainId, Sanitize(mainName), "AddContainer", mainName, main, 60, 60, new() { J(p.Image) }, Icon: p.Icon) };
        nodes.AddRange(paramNodes);
        var edges = new List<EdgeModel>();
        for (var i = 0; i < plans.Count; i++)
        {
            var (c, name, targetId) = plans[i];
            var wc = new List<WithCall>();
            if (c.Port is int port) wc.Add(new WithCall("WithHttpEndpoint", new() { $"targetPort: {port}" }));
            wc.AddRange(ExpandEnv(c.Env));
            nodes.Add(new NodeModel(targetId, Sanitize(name), c.AddMethod, name, wc, 380, 40 + i * 130,
                c.Image != null ? new() { J(c.Image) } : new(), SpawnedBy: mainId, Icon: IconForImage(c.Image)));
            edges.Add(new EdgeModel(Eid(), mainId, targetId, "waitFor"));
        }
        return (nodes, edges);
    }
}
