using System.Text.Json;

namespace AspireUI.Server.Services;

public record DockerImage(string Id, string Repository, string Tag, string Size, string Created);
public record DockerVolume(string Name, string Driver, bool Protected);
public record DockerContainer(string Id, string Name, string Image, string State, string Status, bool Protected);

// Read + housekeep the Docker artifacts AspireUI creates through the mounted socket (dev-run + hosting
// pull images and create containers/volumes). Lets the user see and prune them. Protects AspireUI's own
// container + data volume so cleanup can't nuke the running instance.
public class DockerService(DeployService deploy)
{
    // The current AspireUI container id (when running in Docker) — never offer to remove it.
    private static string SelfId => Environment.GetEnvironmentVariable("HOSTNAME") ?? "";
    public static bool IsProtectedVolume(string name) => name is "aspireui-data";
    public bool IsProtectedContainer(string id, string name) =>
        (SelfId.Length > 0 && id.StartsWith(SelfId, StringComparison.OrdinalIgnoreCase)) || name is "aspireui";

    private IEnumerable<JsonElement> Json(string args)
    {
        var r = deploy.Docker(".", args);
        foreach (var line in r.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line[0] != '{') continue;   // skip stray warnings
            JsonElement? el = null;
            try { el = JsonDocument.Parse(line).RootElement.Clone(); } catch { }
            if (el is { } e) yield return e;
        }
    }
    private static string S(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

    public List<DockerImage> Images() => Json("images --format \"{{json .}}\"")
        .Select(e => new DockerImage(S(e, "ID"), S(e, "Repository"), S(e, "Tag"), S(e, "Size"), S(e, "CreatedSince")))
        .ToList();

    public List<DockerVolume> Volumes() => Json("volume ls --format \"{{json .}}\"")
        .Select(e => S(e, "Name")).Where(n => n.Length > 0)
        .Select(n => new DockerVolume(n, "", IsProtectedVolume(n)))
        .ToList();

    public List<DockerContainer> Containers() => Json("ps -a --format \"{{json .}}\"")
        .Select(e => new DockerContainer(S(e, "ID"), S(e, "Names"), S(e, "Image"), S(e, "State"), S(e, "Status"),
            IsProtectedContainer(S(e, "ID"), S(e, "Names"))))
        .ToList();

    public (bool ok, string log) RemoveImage(string id) { var r = deploy.Docker(".", $"rmi {Sanitize(id)}"); return (r.Ok, r.Log); }
    public (bool ok, string log) RemoveContainer(string id)
    {
        if (SelfId.Length > 0 && id.StartsWith(SelfId, StringComparison.OrdinalIgnoreCase)) return (false, "refusing to remove the AspireUI container itself");
        var r = deploy.Docker(".", $"rm -f {Sanitize(id)}");
        return (r.Ok, r.Log);
    }
    public (bool ok, string log) RemoveVolume(string name)
    {
        if (IsProtectedVolume(name)) return (false, "refusing to remove AspireUI's own data volume");
        var r = deploy.Docker(".", $"volume rm {Sanitize(name)}");
        return (r.Ok, r.Log);
    }
    // Safe prunes only: dangling images / stopped containers. NOT volumes (would delete app data).
    public (bool ok, string log) Prune(string kind)
    {
        var cmd = kind switch { "images" => "image prune -f", "containers" => "container prune -f", _ => "" };
        if (cmd.Length == 0) return (false, "unknown prune kind");
        var r = deploy.Docker(".", cmd);
        return (r.Ok, r.Log);
    }

    // Only allow the shapes docker ids/names take — no shell metacharacters reach the arg.
    private static string Sanitize(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or '/' or ':').ToArray());
}
