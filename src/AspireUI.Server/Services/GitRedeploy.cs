using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>
/// A pull from Git rebuilds the stack from the repository, and the rebuilt stack knows only what the
/// repository and the import dialog said. Everything changed since — an environment value edited in
/// Hosting, a resource limit, a schedule, where a node sits on the canvas — used to be thrown away by
/// every redeploy, so a rotated secret quietly went back to the one typed on the first import.
/// </summary>
public static class GitRedeploy
{
    /// <summary>The literal environment of every service, by resource name.</summary>
    public static Dictionary<string, Dictionary<string, string>> EnvByResource(StackModel stack)
    {
        var byId = HostingService.ReadLiteralEnv(stack);
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var n in stack.Nodes)
        {
            if (!byId.TryGetValue(n.Id, out var pairs)) continue;
            var env = result.TryGetValue(n.ResourceName, out var seen) ? seen : result[n.ResourceName] = new();
            foreach (var p in pairs) env[p[0]] = p[1];
        }
        return result;
    }

    /// <summary>
    /// Carries what was changed by hand onto the stack rebuilt from Git. <paramref name="baseline"/> is
    /// the environment the previous import or pull produced; a value that still matches it was never
    /// touched, so the repository's new value wins, and one that differs was edited and is kept. A key
    /// added by hand stays, a key deleted by hand stays deleted. Without a baseline — a stack imported
    /// before it was recorded — every value already there is treated as edited.
    /// </summary>
    public static StackModel Carry(StackModel existing, StackModel rebuilt,
        IReadOnlyDictionary<string, Dictionary<string, string>>? baseline)
    {
        var current = EnvByResource(existing);
        var before = EnvByResource(rebuilt);
        var oldNodes = existing.Nodes.GroupBy(n => n.ResourceName).ToDictionary(g => g.Key, g => g.First());

        var env = new Dictionary<string, List<string[]>>();
        foreach (var n in rebuilt.Nodes)
        {
            if (!oldNodes.ContainsKey(n.ResourceName)) continue;
            var mine = current.TryGetValue(n.ResourceName, out var c) ? c : new();
            var repo = before.TryGetValue(n.ResourceName, out var r) ? r : new();
            var last = baseline is not null && baseline.TryGetValue(n.ResourceName, out var b) ? b : null;
            env[n.Id] = Merge(mine, repo, last).Select(kv => new[] { kv.Key, kv.Value }).ToList();
        }

        var merged = HostingService.ApplyEnvUpdates(rebuilt, env);
        var nodes = merged.Nodes.Select(n => oldNodes.TryGetValue(n.ResourceName, out var o) ? n with { X = o.X, Y = o.Y } : n).ToList();
        return merged with
        {
            Nodes = nodes,
            Notes = existing.Notes,
            Groups = existing.Groups,
            HostingUrlPath = existing.HostingUrlPath,
            ExpireAt = existing.ExpireAt,
            ClonedFrom = existing.ClonedFrom,
            Limits = existing.Limits,
            Healthchecks = existing.Healthchecks,
            Schedules = existing.Schedules,
            Tags = existing.Tags,
            HookToken = existing.HookToken,
            PublicOrigin = existing.PublicOrigin,
            PublicOriginPort = existing.PublicOriginPort,
            RetrieveHidden = existing.RetrieveHidden,
        };
    }

    private static List<KeyValuePair<string, string>> Merge(Dictionary<string, string> mine,
        Dictionary<string, string> repo, Dictionary<string, string>? last)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in repo)
        {
            if (mine.TryGetValue(key, out var kept))
            {
                var untouched = last is not null && last.TryGetValue(key, out var was) && was == kept;
                result.Add(new(key, untouched ? value : kept));
            }
            else if (last is null || !last.ContainsKey(key)) result.Add(new(key, value));   // new in the repository
        }
        foreach (var (key, value) in mine)
        {
            if (repo.ContainsKey(key)) continue;
            // Gone from the repository: drop it only if nobody changed it since it came from there.
            var fromRepo = last is not null && last.TryGetValue(key, out var was) && was == value;
            if (!fromRepo) result.Add(new(key, value));
        }
        return result;
    }
}
