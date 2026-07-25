using System.Text.Json;
using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

// Makes dev containers reachable off-box via socat sidecars (--network host) binding PublicHost:port.
public class DevProxyService(DeployService deploy)
{
    public const string Image = "alpine/socat";
    private static bool InContainer => File.Exists("/.dockerenv");
    private static string Prefix(string stackId) => $"aspireui-devfwd-{stackId[..Math.Min(8, stackId.Length)]}";

    // (resource-name, loopback port) for running dev containers matched by container name + 127.0.0.1:<port>.
    public List<(string Resource, int Port)> LoopbackPorts(IEnumerable<string> resourceNames) =>
        ParseLoopbackPorts(deploy.Docker(".", "ps --format \"{{json .}}\"").Log, resourceNames);

    // Parse docker ps JSON output → (resource, loopback port) for containers matching resource names.
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

    // Ensure socat forwarder for each port; idempotent; true if forwarding active.
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
