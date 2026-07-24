using System.ComponentModel;
using System.Text.Json;
using AspireUI.Server.Models;
using ModelContextProtocol.Server;

namespace AspireUI.Server.Services;

// MCP tools that let an agent inspect and drive AspireUI over the /mcp endpoint (Bearer-token auth).
// Stores are cheap to construct and back onto the same SQLite file, so each tool builds what it needs;
// CatalogService (expensive reflection) + RunService (shared in-memory run state) are injected.
[McpServerToolType]
public class McpTools(CatalogService catalog, RunService run)
{
    private static string DataDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
    private static string DbPath() => Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(DataDir(), "aspireui.db");
    private static string WsRoot() => Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(DataDir(), "workspace");
    private static string Dir(string id) => Path.Combine(WsRoot(), id);
    private static string PublishRoot(string id) => Path.Combine(WsRoot(), "_publish", id);
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

    [McpServerTool, Description("Create a new, empty stack. Returns its id.")]
    public object CreateStack([Description("Stack name")] string name)
    {
        var s = new StackModel(Guid.NewGuid().ToString("n"), string.IsNullOrWhiteSpace(name) ? "New stack" : name,
            "net10.0", new(), new(), new(), new(), new(), CreatedAt: DateTime.UtcNow.ToString("O"), CreatedBy: "mcp");
        Stacks().Save(s);
        return new { stackId = s.Id, s.Name };
    }

    [McpServerTool, Description("Install a curated catalog app as a NEW stack (with its companion services + parameters), the same way the app store does. Find the appId with search_apps. Returns the new stack id.")]
    public object InstallApp([Description("Catalog app id, e.g. 'immich'")] string appId, [Description("Optional stack name (defaults to the app label)")] string? name = null)
    {
        var p = catalog.GetPresets().FirstOrDefault(x => x.Id.Equals(appId, StringComparison.OrdinalIgnoreCase));
        if (p is null) return new { error = $"no app '{appId}' — use search_apps to find one" };
        var (nodes, edges) = PresetBuilder.Build(p);
        var files = (p.Files ?? new()).Select(f => new ExtraFile(f.Name, f.Content)).ToList();
        var s = new StackModel(Guid.NewGuid().ToString("n"), string.IsNullOrWhiteSpace(name) ? p.Label : name!,
            "net10.0", nodes, edges, new(), files, new(),
            CreatedAt: DateTime.UtcNow.ToString("O"), CreatedBy: "mcp", HostingUrlPath: p.UrlPath);
        Stacks().Save(s);
        return new { stackId = s.Id, s.Name, resources = nodes.Count };
    }

    [McpServerTool, Description("Add an Aspire resource (by add-method, e.g. 'AddPostgres', 'AddRedis') to an existing stack.")]
    public object AddResource([Description("Stack id")] string stackId, [Description("Aspire add-method, e.g. AddPostgres")] string addMethod, [Description("Resource name (defaults from the method)")] string? name = null)
    {
        var store = Stacks();
        if (store.Get(stackId) is not { } s) return new { error = "no such stack" };
        var baseName = addMethod.StartsWith("Add") ? addMethod[3..] : addMethod;
        var rn = string.IsNullOrWhiteSpace(name) ? char.ToLowerInvariant(baseName[0]) + baseName[1..] : name!;
        var varName = System.Text.RegularExpressions.Regex.Replace(rn, "[^A-Za-z0-9_]", "");
        var node = new NodeModel("n" + Guid.NewGuid().ToString("n")[..8], varName.Length == 0 ? "res" : varName,
            addMethod, rn, new(), 60 + s.Nodes.Count * 40, 60 + s.Nodes.Count * 40, new() { JsonSerializer.Serialize(rn) });
        store.Save(s with { Nodes = s.Nodes.Append(node).ToList() });
        return new { stackId, added = rn };
    }

    [McpServerTool, Description("Delete a stack by id (also undeploys it from hosting if deployed).")]
    public object DeleteStack([Description("Stack id")] string stackId)
    {
        if (Deps().GetByStack(stackId) is { } d) Hosting().Undeploy(d.Id);
        Stacks().Delete(stackId);
        return new { deleted = stackId };
    }

    [McpServerTool, Description("Deploy a stack to hosting (install & run it persistently). Blocks while images pull. Returns the deployment state + URLs.")]
    public object DeployToHosting([Description("Stack id")] string stackId)
    {
        if (Stacks().Get(stackId) is not { } s) return new { error = "no such stack" };
        new CodeGenService().Materialize(s, Dir(stackId));
        var set = new SettingsStore(DbPath());
        var dep = Hosting().Deploy(s, PublishRoot(stackId), "localhost",
            (set.GetValue("HostDashboard") ?? "true") == "true", set.GetValue("DashboardToken"));
        return new { dep.State, dep.Urls, dep.LastError };
    }

    [McpServerTool, Description("Run a stack in dev mode (dotnet run on the generated AppHost). Returns the run status.")]
    public object RunStack([Description("Stack id")] string stackId)
    {
        if (Stacks().Get(stackId) is not { } s) return new { error = "no such stack" };
        new CodeGenService().Materialize(s, Dir(stackId));
        return run.Start(stackId, Path.GetFullPath(Dir(stackId)));
    }

    [McpServerTool, Description("Stop a dev run started with run_stack.")]
    public object StopRun([Description("Stack id")] string stackId) => run.Stop(stackId);
}
