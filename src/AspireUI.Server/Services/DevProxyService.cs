using System.Text.Json;
using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

// Makes a DEV run's app containers reachable off-box. Aspire/DCP publishes dev container ports to the
// host's LOOPBACK (127.0.0.1:X) — fine on your own machine, useless when AspireUI runs in a container
// on a server. For each such port we start a tiny socat sidecar (--network host) that binds the box's
// LAN IP (PublicHost) on the same port and forwards to 127.0.0.1:X, so <PublicHost>:X becomes reachable.
// Torn down on stop. Only active when AspireUI itself runs in a container and a PublicHost is set.
public class DevProxyService(DeployService deploy)
{
    public const string Image = "alpine/socat";
    private static bool InContainer => File.Exists("/.dockerenv");
    private static string Prefix(string stackId) => $"aspireui-devfwd-{stackId[..Math.Min(8, stackId.Length)]}";

    // (resource-name, loopback host port) for the running dev containers of this stack: matched by the
    // container name starting with a resource name and a 127.0.0.1:<port> publish.
    public List<(string Resource, int Port)> LoopbackPorts(IEnumerable<string> resourceNames) =>
        ParseLoopbackPorts(deploy.Docker(".", "ps --format \"{{json .}}\"").Log, resourceNames);

    // Pure parse of `docker ps --format {{json .}}`: containers whose name matches a resource name and
    // publish a 127.0.0.1:<port> mapping (Aspire dev containers) → (resource, loopback port).
    public static List<(string Resource, int Port)> ParseLoopbackPorts(string psOutput, IEnumerable<string> resourceNames)
    {
        var names = resourceNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        var found = new List<(string, int)>();
        foreach (var line in psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonElement e; try { e = JsonDocument.Parse(line).RootElement; } catch { continue; }
            var cname = e.TryGetProperty("Names", out var n) ? n.GetString() ?? "" : "";
            var ports = e.TryGetProperty("Ports", out var p) ? p.GetString() ?? "" : "";
            var res = names.FirstOrDefault(r => cname == r || cname.StartsWith(r + "-", StringComparison.OrdinalIgnoreCase));
            if (res is null) continue;
            var m = Regex.Match(ports, @"127\.0\.0\.1:(\d+)->");
            if (m.Success) found.Add((res, int.Parse(m.Groups[1].Value)));
        }
        return found;
    }

    private HashSet<string> RunningForwarders(string stackId) =>
        deploy.Docker(".", $"ps --format \"{{{{.Names}}}}\" --filter name={Prefix(stackId)}").Log
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    // Ensure a socat forwarder exists for each port. Idempotent. Returns true if forwarding is active.
    public bool Ensure(string stackId, string? publicHost, IEnumerable<int> ports)
    {
        if (!InContainer || string.IsNullOrWhiteSpace(publicHost)) return false;
        var running = RunningForwarders(stackId);
        foreach (var port in ports.Distinct())
        {
            var name = $"{Prefix(stackId)}-{port}";
            if (running.Contains(name)) continue;
            deploy.Docker(".", $"rm -f {name}");   // clear a dead one with the same name
            deploy.Docker(".", $"run -d --name {name} --network host --restart unless-stopped {Image} " +
                $"TCP-LISTEN:{port},bind={publicHost},fork,reuseaddr TCP:127.0.0.1:{port}");
        }
        return true;
    }

    public void Teardown(string stackId)
    {
        if (!InContainer) return;
        var ids = deploy.Docker(".", $"ps -aq --filter name={Prefix(stackId)}").Log
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids) deploy.Docker(".", $"rm -f {id}");
    }
}
