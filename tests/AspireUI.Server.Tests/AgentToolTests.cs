using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Http;

// The agent surface: one list of tools, each with a permission that is actually enforced.
// In the integration collection because the tools read DB_PATH from the environment, and so do the
// test factories — two of those running at once would be one database.
[Collection("ServerIntegration")]
public class AgentToolTests
{
    private static (McpTools tools, UserStore users, User user) Tools(params string[] permissions)
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-agent-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "aspireui.db");
        // The tools read these from the environment, so a test gets its own database and workspace.
        Environment.SetEnvironmentVariable("DB_PATH", db);
        Environment.SetEnvironmentVariable("WORKSPACE_DIR", Path.Combine(root, "workspace"));

        var users = new UserStore(db);
        var created = users.Create("agent", "hash", isAdmin: false);
        users.SetPermissions(created.Id, permissions.ToList());

        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, created.Id)], "test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return (new McpTools(new CatalogService(), new RunService(graph: new ResourceGraphService()), users, accessor),
            users, users.Get(created.Id)!);
    }

    [Fact]
    public void Every_tool_declares_a_permission_and_has_a_snake_case_name()
    {
        Assert.NotEmpty(AgentTools.All);
        Assert.All(AgentTools.All, t =>
        {
            Assert.Contains(t.Permission, Perm.All);
            Assert.Equal(t.Name, t.Name.ToLowerInvariant());
            Assert.DoesNotContain(' ', t.Name);
            Assert.NotEmpty(t.Description);
        });

        // The names MCP clients already use must not change.
        Assert.Contains("list_stacks", AgentTools.All.Select(t => t.Name));
        Assert.Contains("deploy_to_hosting", AgentTools.All.Select(t => t.Name));
        Assert.Equal("search_apps", AgentTools.SnakeCase("SearchApps"));
    }

    [Fact]
    public void A_user_with_nothing_granted_can_call_no_tool_at_all()
    {
        var (tools, _, _) = Tools();   // no permissions

        foreach (var tool in AgentTools.All)
        {
            // Arguments that are shaped right but name nothing that exists: the permission check runs
            // first, so a tool that forgot it would reach its body and fail this test instead.
            var args = new JsonObject();
            foreach (var p in tool.Parameters)
                args[p.Name!] = p.ParameterType == typeof(string) ? "does-not-exist" : null;

            var error = Assert.Throws<TargetInvocationException>(() => AgentTools.Invoke(tool, tools, args));
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Contains("permission", error.InnerException!.Message);
        }
    }

    [Fact]
    public void A_granted_tool_runs_and_an_ungranted_one_next_to_it_does_not()
    {
        var (tools, _, _) = Tools(Perm.OpenEditor);

        // open-editor covers the stack tools…
        var created = AgentTools.Invoke(AgentTools.Find("create_stack")!, tools, new JsonObject { ["name"] = "From an agent" });
        Assert.Contains("From an agent", AgentTools.Result(created));
        Assert.Contains("From an agent", AgentTools.Result(AgentTools.Invoke(AgentTools.Find("list_stacks")!, tools, new JsonObject())));

        // …and not the hosting ones.
        var refused = Assert.Throws<TargetInvocationException>(() =>
            AgentTools.Invoke(AgentTools.Find("start_hosting")!, tools, new JsonObject { ["stackId"] = "x" }));
        Assert.Contains("'deploy' permission", refused.InnerException!.Message);
    }

    [Fact]
    public void The_model_is_only_offered_what_the_user_may_do()
    {
        var (_, users, _) = Tools(Perm.OpenEditor);
        var user = users.FindByUsername("agent")!;

        var offered = AgentTools.SpecsFor(user)
            .Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("create_stack", offered);
        Assert.DoesNotContain("deploy_to_hosting", offered);

        // An admin is offered everything, and a nobody is offered nothing.
        var admin = new User("a", "admin", "hash", true, "now");
        Assert.Equal(AgentTools.All.Count, AgentTools.SpecsFor(admin).Count);
        Assert.Empty(AgentTools.SpecsFor(null));
    }

    [Fact]
    public void A_specification_says_which_arguments_are_required()
    {
        var admin = new User("a", "admin", "hash", true, "now");
        var spec = AgentTools.SpecsFor(admin)
            .First(t => t!["function"]!["name"]!.GetValue<string>() == "install_app")!["function"]!;

        var properties = spec["parameters"]!["properties"]!.AsObject();
        Assert.Contains("appId", properties.Select(kv => kv.Key));
        Assert.Equal("string", properties["appId"]!["type"]!.GetValue<string>());

        var required = spec["parameters"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("appId", required);
        Assert.DoesNotContain("name", required);   // it has a default
    }
}
