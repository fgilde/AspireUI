using System.Globalization;
using System.Text.Json;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>One thing taking up room, and why it may or may not go.</summary>
public record StorageItem(string Kind, string Id, string Name, string Detail, long Bytes, string Reason,
    bool InUse = false, string? UsedBy = null, string? Age = null);

/// <summary>Everything of one kind that can go, with what it adds up to.</summary>
public record StorageGroup(string Kind, string Label, string Explain, long Bytes, List<StorageItem> Items);

public record StorageReport(long TotalBytes, long ReclaimableBytes, long InUseBytes,
    List<StorageGroup> Groups, List<StorageItem> InUse, string? Error = null);

/// <summary>
/// What Docker is holding on to, and which of it is still wanted. `docker system df -v` answers the
/// first question in one call — per-image unique size, per-volume size, and the compose labels that
/// answer the second: anything belonging to a deployment stays, running or stopped, because a
/// stopped app that loses its image and its data has not been stopped, it has been deleted.
/// </summary>
public class StorageService(DeployService deploy, DeploymentStore deployments, StackStore stacks)
{
    public const string Images = "images";
    public const string Containers = "containers";
    public const string Volumes = "volumes";
    public const string BuildCache = "buildcache";

    /// <summary>What auto-clean touches unless somebody says otherwise: never data.</summary>
    public static readonly string[] SafeKinds = [Images, Containers, BuildCache];
    public static readonly string[] AllKinds = [Images, Containers, Volumes, BuildCache];

    public StorageReport Report()
    {
        var raw = deploy.Docker(".", "system df -v --format \"{{json .}}\"");
        var json = raw.Log.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('{'));
        if (!raw.Ok || json is null)
            return new StorageReport(0, 0, 0, [], [], Trim(raw.Log) is { Length: > 0 } m ? m : "docker did not answer");

        return Classify(json,
            deployments.List().Select(d => d.Project).Where(p => !string.IsNullOrWhiteSpace(p)),
            PlannedVolumes(),
            self => new DockerService(deploy).IsProtectedContainer(self.Id, self.Name),
            CacheReclaimable());
    }

    /// <summary>
    /// What the build cache really gives back. Its records share layers, so adding their sizes up
    /// promises more than deleting them returns — and a number that is too big is worse than no
    /// number. Docker works the overlap out itself in the summary.
    /// </summary>
    private long? CacheReclaimable()
    {
        var r = deploy.Docker(".", "system df --format \"{{json .}}\"");
        if (!r.Ok) return null;
        foreach (var line in r.Log.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                var e = JsonDocument.Parse(line).RootElement;
                if (S(e, "Type") is not "Build Cache") continue;
                // "21.45GB" on its own, or "185.8MB (92%)" for the kinds that report a share.
                return Bytes(S(e, "Reclaimable").Split(' ')[0]);
            }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary>
    /// The whole decision, over docker's own output and nothing else — which is what makes it testable
    /// without a docker to ask. Everything that stays, stays because one of these says so.
    /// </summary>
    public static StorageReport Classify(string json, IEnumerable<string> deploymentProjects,
        IEnumerable<string> plannedVolumes, Func<(string Id, string Name), bool> isSelf, long? cacheReclaimable = null)
    {
        JsonElement df;
        try { df = JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException e) { return new StorageReport(0, 0, 0, [], [], "could not read docker's disk usage: " + e.Message); }

        var projects = deploymentProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = plannedVolumes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var (imageGroup, imagesInUse) = ImageGroup(df);
        var (containerGroup, containersInUse) = ContainerGroup(df, projects, isSelf);
        var (volumeGroup, volumesInUse) = VolumeGroup(df, projects, planned);
        var cacheGroup = CacheGroup(df, cacheReclaimable);

        var groups = new List<StorageGroup> { imageGroup, containerGroup, volumeGroup, cacheGroup };
        var inUse = imagesInUse.Concat(containersInUse).Concat(volumesInUse).ToList();
        return new StorageReport(
            groups.Sum(g => g.Bytes) + inUse.Sum(i => i.Bytes),
            groups.Sum(g => g.Bytes),
            inUse.Sum(i => i.Bytes),
            groups, inUse);
    }

    /// <summary>
    /// Volume names every saved stack asks for. A stack that has never been started has no containers
    /// and no labels, and its volume looks exactly like an abandoned one — this is what tells them apart.
    /// </summary>
    private HashSet<string> PlannedVolumes()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stacks.List())
            foreach (var n in s.Nodes)
                foreach (var w in n.WithCalls.Where(w => w.Method is "WithVolume" && w.Args.Count > 0))
                    names.Add(w.Args[0].Trim('"'));
        return names;
    }

    private static (StorageGroup, List<StorageItem>) ImageGroup(JsonElement df)
    {
        var free = new List<StorageItem>();
        var kept = new List<StorageItem>();
        foreach (var e in Array(df, "Images"))
        {
            var repo = S(e, "Repository");
            var tag = S(e, "Tag");
            var name = repo is "<none>" or "" ? "<untagged>" : tag is "<none>" or "" ? repo : $"{repo}:{tag}";
            // UniqueSize is what actually comes back: layers shared with another image do not.
            var bytes = Bytes(S(e, "UniqueSize") is { Length: > 0 } u && u != "N/A" ? u : S(e, "Size"));
            var used = int.TryParse(S(e, "Containers"), out var c) && c > 0;
            var item = new StorageItem(Images, S(e, "ID"), name, S(e, "Size"), bytes,
                used ? $"{c} container(s) use it" : repo is "<none>" ? "untagged leftover of a build" : "no container uses it",
                used, Age: S(e, "CreatedSince"));
            (used ? kept : free).Add(item);
        }
        return (new StorageGroup(Images, "Images", "Images no container refers to. Pulling one again costs only the download.",
            free.Sum(i => i.Bytes), [.. free.OrderByDescending(i => i.Bytes)]), kept);
    }

    private static (StorageGroup, List<StorageItem>) ContainerGroup(JsonElement df, HashSet<string> projects, Func<(string, string), bool> isSelf)
    {
        var free = new List<StorageItem>();
        var kept = new List<StorageItem>();
        foreach (var e in Array(df, "Containers"))
        {
            var id = S(e, "ID");
            var name = S(e, "Names");
            var state = S(e, "State");
            var project = Label(S(e, "Labels"), "com.docker.compose.project");
            var hosted = project is { Length: > 0 } p && projects.Contains(p);
            var running = state is "running" or "restarting";
            var self = isSelf((id, name));
            var bytes = Bytes(S(e, "Size"));

            if (running || hosted || self)
            {
                kept.Add(new StorageItem(Containers, id, name, S(e, "Image"), bytes,
                    self ? "AspireUI itself" : running ? "running" : $"belongs to the hosted app '{project}'",
                    true, project, S(e, "CreatedAt")));
                continue;
            }
            free.Add(new StorageItem(Containers, id, name, S(e, "Image"), bytes,
                $"stopped, and no hosted app claims it", Age: S(e, "CreatedAt")));
        }
        return (new StorageGroup(Containers, "Stopped containers", "Containers that are not running and belong to no hosted app.",
            free.Sum(i => i.Bytes), [.. free.OrderByDescending(i => i.Bytes)]), kept);
    }

    private static (StorageGroup, List<StorageItem>) VolumeGroup(JsonElement df, HashSet<string> projects, HashSet<string> planned)
    {
        var free = new List<StorageItem>();
        var kept = new List<StorageItem>();
        foreach (var e in Array(df, "Volumes"))
        {
            var name = S(e, "Name");
            var links = int.TryParse(S(e, "Links"), out var l) ? l : 0;
            var project = Label(S(e, "Labels"), "com.docker.compose.project");
            var hosted = project is { Length: > 0 } p && projects.Contains(p);
            var wanted = planned.Any(v => name.Equals(v, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + v, StringComparison.OrdinalIgnoreCase));
            var bytes = Bytes(S(e, "Size"));
            var anonymous = Label(S(e, "Labels"), "com.docker.volume.anonymous") is not null;

            if (links > 0 || hosted || wanted || DockerService.IsProtectedVolume(name))
            {
                kept.Add(new StorageItem(Volumes, name, name, S(e, "Driver"), bytes,
                    DockerService.IsProtectedVolume(name) ? "AspireUI's own data"
                    : links > 0 ? $"attached to {links} container(s)"
                    : hosted ? $"belongs to the hosted app '{project}'"
                    : "a stack on this instance asks for it", true, project));
                continue;
            }
            free.Add(new StorageItem(Volumes, name, name, S(e, "Driver"), bytes,
                anonymous ? "anonymous volume, nothing attached" : "nothing attached, and no stack asks for it"));
        }
        return (new StorageGroup(Volumes, "Unused volumes",
            "Volumes nothing is attached to and no stack asks for. This is data: deleting one does not come back.",
            free.Sum(i => i.Bytes), [.. free.OrderByDescending(i => i.Bytes)]), kept);
    }

    private static StorageGroup CacheGroup(JsonElement df, long? reclaimable)
    {
        var items = new List<StorageItem>();
        long bytes = 0;
        var count = 0;
        foreach (var e in Array(df, "BuildCache"))
        {
            if (S(e, "InUse") is "true") continue;
            bytes += Bytes(S(e, "Size"));
            count++;
        }
        // Docker's own figure where there is one: the records overlap, so their sum is too big.
        if (reclaimable is { } exact) bytes = exact;
        if (count > 0)
            items.Add(new StorageItem(BuildCache, "all", $"{count} cache record(s)", "layers kept from earlier builds", bytes,
                "a cache: the next build fills it again"));
        return new StorageGroup(BuildCache, "Build cache", "What earlier image builds left behind. Rebuilding is slower once, then it is back.",
            bytes, items);
    }

    /// <summary>Removes exactly what was asked for, and says what each one gave back.</summary>
    public (int Removed, long Bytes, List<string> Failed) Remove(string kind, IReadOnlyList<string> ids, StorageReport? known = null)
    {
        var report = known ?? Report();
        var byId = report.Groups.FirstOrDefault(g => g.Kind == kind)?.Items.ToDictionary(i => i.Id, StringComparer.Ordinal) ?? [];
        var removed = 0;
        long bytes = 0;
        var failed = new List<string>();

        foreach (var id in ids)
        {
            // Only ever what this report offered: an id that is not in it is one that is in use.
            if (!byId.TryGetValue(id, out var item)) { failed.Add($"{id}: not offered for removal"); continue; }
            var r = kind switch
            {
                Images => deploy.Docker(".", $"rmi {Arg(id)}"),
                Containers => deploy.Docker(".", $"rm -f {Arg(id)}"),
                Volumes => deploy.Docker(".", $"volume rm {Arg(id)}"),
                BuildCache => deploy.Docker(".", "builder prune -af"),
                _ => new DeployResult(false, $"unknown kind '{kind}'"),
            };
            if (r.Ok) { removed++; bytes += item.Bytes; }
            else failed.Add($"{item.Name}: {Trim(r.Log)}");
        }
        return (removed, bytes, failed);
    }

    // ---- reading docker's numbers ------------------------------------------------------------

    private static IEnumerable<JsonElement> Array(JsonElement df, string name) =>
        df.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string S(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>One label out of docker's comma-separated list. Null when it is not there at all.</summary>
    public static string? Label(string labels, string key)
    {
        foreach (var part in labels.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            if (name.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return eq < 0 ? "" : part[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Docker prints sizes for people: "350MB", "1.2GB", "0B", and "N/A" where it does not know. The
    /// decimal units are powers of a thousand, the ones with an i are powers of 1024.
    /// </summary>
    public static long Bytes(string size)
    {
        var s = size.Trim();
        if (s.Length == 0 || s == "N/A") return 0;
        var i = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or ',')) i++;
        if (i == 0) return 0;
        if (!double.TryParse(s[..i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        var unit = s[i..].Trim().TrimEnd('B', 'b');
        double factor = unit.ToLowerInvariant() switch
        {
            "" => 1,
            "k" => 1_000, "m" => 1_000_000, "g" => 1_000_000_000, "t" => 1_000_000_000_000,
            "ki" => 1024, "mi" => 1024d * 1024, "gi" => 1024d * 1024 * 1024, "ti" => 1024d * 1024 * 1024 * 1024,
            _ => 1,
        };
        return (long)(n * factor);
    }

    private static string Arg(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or '/' or ':').ToArray());
    private static string Trim(string log) => log.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
}
