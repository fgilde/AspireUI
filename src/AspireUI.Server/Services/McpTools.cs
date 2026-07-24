using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AspireUI.Server.Services;

// MCP tools that let an agent inspect and drive AspireUI over the /mcp endpoint (Bearer-token auth).
// Stores are cheap to construct and back onto the same SQLite file, so each tool builds what it needs;
// CatalogService (expensive reflection) is injected as a singleton.
[McpServerToolType]
public class McpTools(CatalogService catalog)
{
    private static string DataDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
    private static string DbPath() => Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(DataDir(), "aspireui.db");
    private static string WsRoot() => Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(DataDir(), "workspace");
    private static StackStore Stacks() => new(DbPath());
    private static DeploymentStore Deps() => new(DbPath());
    private static HostingService Hosting()
    {
        var deploy = new DeployService();
        var proxy = new ProxyService(deploy, Path.Combine(WsRoot(), "_proxy"), Environment.GetEnvironmentVariable("HOSTING_BASE_DOMAIN") ?? "localhost");
        return new HostingService(Deps(), new PublishService(new CodeGenService()), deploy, proxy);
    }

    [McpServerTool, Description("List all AspireUI stacks (id, name, resource count).")]
    public object ListStacks() => Stacks().List().Select(s => new { s.Id, s.Name, resources = s.Nodes.Count }).ToList();

    [McpServerTool, Description("Get one stack by id, including its resources (nodes) and edges.")]
    public object? GetStack([Description("Stack id")] string id) => Stacks().Get(id);

    [McpServerTool, Description("Search the curated app catalog (self-hostable container apps). Empty query returns the first 50.")]
    public object SearchApps([Description("Substring matched against label, group and description")] string query = "")
        => catalog.GetPresets()
            .Where(p => string.IsNullOrEmpty(query) || $"{p.Label} {p.Group} {p.Description}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p => new { p.Id, p.Label, p.Group, p.Description, p.Website })
            .Take(50).ToList();

    [McpServerTool, Description("List hosting deployments (installed apps) with their state and URLs.")]
    public object ListHosting() => Deps().List().Select(d => new { d.StackId, d.Name, d.State, d.Urls }).ToList();

    [McpServerTool, Description("Start or retry a hosted app, identified by its stack id. Returns the resulting state.")]
    public string StartHosting([Description("Stack id")] string stackId)
    {
        if (Deps().GetByStack(stackId) is not { } d) return "no deployment for that stack";
        Hosting().Start(d.Id);
        return Deps().Get(d.Id)?.State ?? "unknown";
    }

    [McpServerTool, Description("Stop a running hosted app, identified by its stack id.")]
    public string StopHosting([Description("Stack id")] string stackId)
    {
        if (Deps().GetByStack(stackId) is not { } d) return "no deployment for that stack";
        Hosting().Stop(d.Id);
        return "stopped";
    }

    [McpServerTool, Description("Return the recent docker compose logs of a hosted app, identified by its stack id.")]
    public string HostingLogs([Description("Stack id")] string stackId)
    {
        if (Deps().GetByStack(stackId) is not { } d) return "no deployment for that stack";
        return new DeployService().Logs(d.ComposeDir, d.Project).Log;
    }
}
