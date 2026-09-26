using AspireUI.Server.Services;

public class HookCallTests
{
    private static Hook WithParams(params HookParam[] p) => new("t", "git", "g", Params: p.ToList());

    [Fact]
    public void Body_wins_over_query_and_non_objects_are_ignored()
    {
        var q = new[] { KeyValuePair.Create("branch", "main"), KeyValuePair.Create("x", "1") };
        var a = HookCall.MergeArgs(q, """{"branch":"dev","n":5,"b":true}""");
        Assert.Equal("dev", a["branch"]);
        Assert.Equal("1", a["x"]);
        Assert.Equal("5", a["n"]);
        Assert.Equal("true", a["b"]);
        Assert.Equal("main", HookCall.MergeArgs(q, "[1,2]")["branch"]);
        Assert.Equal("main", HookCall.MergeArgs(q, "")["branch"]);
        Assert.Equal("main", HookCall.MergeArgs(q, "{not json")["branch"]);
    }

    [Fact]
    public void Disabled_globally_or_per_hook_is_refused()
    {
        var h = new Hook("t", "clone", "c");
        Assert.Null(HookCall.CheckEnabled(new HookSettings(), h));
        Assert.Equal(new HookFailure(503, "all hooks disabled"), HookCall.CheckEnabled(new HookSettings(Enabled: false), h));
        Assert.Equal(new HookFailure(503, "hook disabled"), HookCall.CheckEnabled(new HookSettings(), h with { Enabled = false }));
    }

    [Fact]
    public void Each_mode_resolves_its_own_way()
    {
        var h = WithParams(new("repo", "fixed", "https://x/r.git"), new("branch", "required"),
            new("LEVEL", "optional", "info"), new("SECRET", "generated", Secret: true));
        var (v, f) = HookCall.ResolveParams(h, new Dictionary<string, string> { ["branch"] = "dev", ["repo"] = "ignored" });
        Assert.Null(f);
        Assert.Equal("https://x/r.git", v["repo"]);
        Assert.Equal("dev", v["branch"]);
        Assert.Equal("info", v["LEVEL"]);
        Assert.Equal(48, v["SECRET"].Length);

        var (v2, _) = HookCall.ResolveParams(h, new Dictionary<string, string> { ["branch"] = "x", ["LEVEL"] = "debug" });
        Assert.Equal("debug", v2["LEVEL"]);
        Assert.NotEqual(v["SECRET"], v2["SECRET"]);
    }

    [Fact]
    public void A_missing_or_empty_required_parameter_is_a_400()
    {
        var h = WithParams(new HookParam("branch", "required"));
        Assert.Equal(new HookFailure(400, "missing parameter: branch"), HookCall.ResolveParams(h, new Dictionary<string, string>()).Failure);
        Assert.Equal(new HookFailure(400, "missing parameter: branch"),
            HookCall.ResolveParams(h, new Dictionary<string, string> { ["branch"] = "" }).Failure);
    }

    [Fact]
    public void Resources_below_the_floor_are_a_507()
    {
        var s = new HookSettings(MinDiskGb: 5, MinRamGb: 1);
        Assert.Null(HookCall.CheckResources(s, "local", (6000, 2048)));
        Assert.Null(HookCall.CheckResources(s, "k8s", null));
        Assert.Equal(new HookFailure(507, "not enough disk on local: 3.2 GB free, 5 GB required"),
            HookCall.CheckResources(s, "local", (3277, 2048)));
        Assert.Equal(new HookFailure(507, "not enough memory on local: 0.5 GB free, 1 GB required"),
            HookCall.CheckResources(s, "local", (6000, 512)));
        Assert.Equal(new HookFailure(503, "cannot check resources on local — is Docker running there?"),
            HookCall.CheckResources(s, "local", (null, 2048)));
    }

    [Fact]
    public void A_repo_from_the_call_must_be_https_and_never_gets_the_token_of_another_host()
    {
        var h = new Hook("t", "git", "g", Repo: "https://github.com/acme/private.git", AuthToken: "ghp_secret");

        Assert.Equal(("https://github.com/acme/private.git", "ghp_secret", (HookFailure?)null),
            HookCall.GitSource(h, new Dictionary<string, string>()));
        Assert.Equal(("https://github.com/acme/other.git", "ghp_secret", (HookFailure?)null),
            HookCall.GitSource(h, new Dictionary<string, string> { ["repo"] = "https://github.com/acme/other.git" }));
        Assert.Equal(("https://evil.example/x.git", (string?)null, (HookFailure?)null),
            HookCall.GitSource(h, new Dictionary<string, string> { ["repo"] = "https://evil.example/x.git" }));
        Assert.Equal(new HookFailure(400, "repo must be an https:// URL"),
            HookCall.GitSource(h, new Dictionary<string, string> { ["repo"] = "file:///etc" }).Failure);
        Assert.Equal(new HookFailure(400, "repo must be an https:// URL"),
            HookCall.GitSource(h, new Dictionary<string, string> { ["repo"] = "--upload-pack=x" }).Failure);
        Assert.Equal(new HookFailure(400, "missing parameter: repo"),
            HookCall.GitSource(h with { Repo = null }, new Dictionary<string, string>()).Failure);
    }

    [Fact]
    public void Errors_for_anonymous_callers_are_cut_to_their_tail()
    {
        var log = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var tail = HookCall.Tail(log);
        Assert.StartsWith("…", tail);
        Assert.EndsWith("line 100", tail);
        Assert.DoesNotContain("line 70" + '\n', tail);
        Assert.Equal("short", HookCall.Tail("short"));
        Assert.True(HookCall.Tail(new string('x', 5000)).Length <= 2001);
    }
}
