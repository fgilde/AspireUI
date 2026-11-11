using AspireUI.Server.Models;
using AspireUI.Server.Services;

// A pull from Git rebuilds the stack from the repository. What was changed by hand since the import
// has to survive it, and what the repository changed has to come through.
public class GitRedeployTests
{
    private static NodeModel Service(string name, params (string Key, string Value)[] env) =>
        new("n" + name, name, "AddContainer", name,
            env.Select(e => new WithCall("WithEnvironment", [Q(e.Key), Q(e.Value)])).ToList(),
            60, 60, ["\"acme/" + name + ":1\""]);

    private static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static StackModel Edit(StackModel stack, params string[][] pairs) =>
        HostingService.ApplyEnvUpdates(stack, new Dictionary<string, List<string[]>> { ["napi"] = pairs.ToList() });

    private static StackModel Stack(params NodeModel[] nodes) => new("s1", "demo", "net10.0", nodes.ToList(), [], [], [], []);

    private static Dictionary<string, string> Env(StackModel stack, string resource) =>
        GitRedeploy.EnvByResource(stack).TryGetValue(resource, out var e) ? e : new();

    [Fact]
    public void A_value_edited_after_the_import_survives_the_pull()
    {
        var imported = Stack(Service("api", ("KEY", "from-import"), ("MODE", "prod")));
        var edited = Edit(imported, ["KEY", "rotated"], ["MODE", "prod"]);
        var rebuilt = Stack(Service("api", ("KEY", "from-import"), ("MODE", "prod")));

        var merged = GitRedeploy.Carry(edited, rebuilt, GitRedeploy.EnvByResource(imported));

        Assert.Equal("rotated", Env(merged, "api")["KEY"]);
    }

    [Fact]
    public void A_value_the_repository_changed_comes_through_when_nobody_edited_it()
    {
        var imported = Stack(Service("api", ("MODE", "prod")));
        var rebuilt = Stack(Service("api", ("MODE", "staging")));

        var merged = GitRedeploy.Carry(imported, rebuilt, GitRedeploy.EnvByResource(imported));

        Assert.Equal("staging", Env(merged, "api")["MODE"]);
    }

    [Fact]
    public void Keys_added_and_deleted_by_hand_stay_that_way()
    {
        var imported = Stack(Service("api", ("A", "1"), ("B", "2")));
        var edited = Edit(imported, ["A", "1"], ["MINE", "x"]);
        var rebuilt = Stack(Service("api", ("A", "1"), ("B", "2"), ("NEW", "3")));

        var env = Env(GitRedeploy.Carry(edited, rebuilt, GitRedeploy.EnvByResource(imported)), "api");

        Assert.Equal("x", env["MINE"]);
        Assert.False(env.ContainsKey("B"));
        Assert.Equal("3", env["NEW"]);
    }

    [Fact]
    public void A_key_the_repository_removed_goes_unless_it_was_edited()
    {
        var imported = Stack(Service("api", ("OLD", "1"), ("KEPT", "1")));
        var edited = Edit(imported, ["OLD", "1"], ["KEPT", "changed"]);
        var rebuilt = Stack(Service("api"));

        var env = Env(GitRedeploy.Carry(edited, rebuilt, GitRedeploy.EnvByResource(imported)), "api");

        Assert.False(env.ContainsKey("OLD"));
        Assert.Equal("changed", env["KEPT"]);
    }

    [Fact]
    public void Without_a_baseline_the_current_values_win()
    {
        var current = Stack(Service("api", ("KEY", "rotated")));
        var rebuilt = Stack(Service("api", ("KEY", "from-import"), ("NEW", "1")));

        var env = Env(GitRedeploy.Carry(current, rebuilt, null), "api");

        Assert.Equal("rotated", env["KEY"]);
        Assert.Equal("1", env["NEW"]);
    }

    [Fact]
    public void Layout_and_app_settings_survive_the_pull()
    {
        var current = Stack(Service("api") with { X = 700, Y = 420 }) with
        {
            Limits = new AppLimits(Cpus: 0.5),
            Tags = ["home"],
            Schedules = [new AppSchedule("restart", EveryHours: 24)],
        };
        var rebuilt = Stack(Service("api"), Service("worker"));

        var merged = GitRedeploy.Carry(current, rebuilt, null);

        var api = merged.Nodes.Single(n => n.ResourceName == "api");
        Assert.Equal((700d, 420d), (api.X, api.Y));
        Assert.Equal(0.5, merged.Limits?.Cpus);
        Assert.Equal(["home"], merged.Tags!);
        Assert.Single(merged.Schedules!);
        Assert.Contains(merged.Nodes, n => n.ResourceName == "worker");
    }
}
