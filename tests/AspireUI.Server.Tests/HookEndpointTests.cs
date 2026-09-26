using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AspireUI.Server.Services;

[Collection("ServerIntegration")]
public class HookEndpointTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public HookEndpointTests(TestWebAppFactory f) => _f = f;

    private HookStore Hooks() => new(new SettingsStore(_f.DbPath));

    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Unknown_token_is_404_with_the_error_shape()
    {
        var r = await _f.CreateClient().PostAsync("/api/hook/nope", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var b = await Body(r);
        Assert.False(b.GetProperty("success").GetBoolean());
        Assert.Equal("unknown hook", b.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_disabled_hook_is_refused_before_anything_is_built()
    {
        var client = _f.CreateClient();
        Hooks().Save(new Hook("disabledhook", "store", "x", Enabled: false, AppId: "immich"));
        var before = (await client.GetFromJsonAsync<JsonElement>("/api/stacks")).GetArrayLength();

        var r = await client.PostAsync("/api/hook/disabledhook", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("hook disabled", (await Body(r)).GetProperty("error").GetString());
        Assert.Equal(before, (await client.GetFromJsonAsync<JsonElement>("/api/stacks")).GetArrayLength());
    }

    [Fact]
    public async Task A_missing_required_parameter_is_400()
    {
        Hooks().Save(new Hook("needsbranch", "git", "g", Repo: "https://example.invalid/r.git",
            Params: [new("branch", "required")]));
        var r = await _f.CreateClient().PostAsJsonAsync("/api/hook/needsbranch", new { other = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("missing parameter: branch", (await Body(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_old_clone_hook_path_runs_the_same_pipeline()
    {
        Hooks().Save(new Hook("oldpath", "clone", "c", Enabled: false, SourceStackId: "s"));
        var r = await _f.CreateClient().PostAsync("/api/clone-hook/oldpath", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact]
    public async Task Hooks_can_be_created_listed_changed_and_deleted()
    {
        var client = _f.CreateClient();
        var created = await client.PostAsJsonAsync("/api/hooks", new
        {
            kind = "git", name = "Preview", repo = "https://example.invalid/r.git", authToken = "ghp_x",
            @params = new[] { new { key = "branch", mode = "required", value = (string?)null, secret = false }, new { key = "API_KEY", mode = "fixed", value = (string?)"s3cret", secret = true } },
        });
        created.EnsureSuccessStatusCode();
        var token = (await Body(created)).GetProperty("token").GetString()!;

        var list = await client.GetFromJsonAsync<JsonElement>("/api/hooks");
        var row = list.GetProperty("hooks").EnumerateArray().Single(r => r.GetProperty("hook").GetProperty("token").GetString() == token);
        Assert.Equal($"/api/hook/{token}", row.GetProperty("webhookPath").GetString());
        Assert.Equal(HookStore.Masked, row.GetProperty("hook").GetProperty("authToken").GetString());
        Assert.Equal(HookStore.Masked, row.GetProperty("hook").GetProperty("params")[1].GetProperty("value").GetString());

        var node = System.Text.Json.Nodes.JsonNode.Parse(row.GetProperty("hook").GetRawText())!;
        node["name"] = "Renamed";
        var put = await client.PutAsync($"/api/hooks/{token}",
            new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        put.EnsureSuccessStatusCode();
        var stored = Hooks().Get(token)!;
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal("ghp_x", stored.AuthToken);
        Assert.Equal("s3cret", stored.Params![1].Value);
        Assert.Equal("git", stored.Kind);

        (await client.PostAsJsonAsync($"/api/hooks/{token}/enabled", new { enabled = false })).EnsureSuccessStatusCode();
        Assert.False(Hooks().Get(token)!.Enabled);

        (await client.DeleteAsync($"/api/hooks/{token}")).EnsureSuccessStatusCode();
        Assert.Null(Hooks().Get(token));
    }

    [Fact]
    public async Task Settings_route_is_not_taken_for_a_token()
    {
        var client = _f.CreateClient();
        var r = await client.PutAsJsonAsync("/api/hooks/settings", new { enabled = true, minDiskGb = 7.5, minRamGb = 2 });
        r.EnsureSuccessStatusCode();
        Assert.Equal(7.5, Hooks().Settings().MinDiskGb);
        (await client.PutAsJsonAsync("/api/hooks/settings", new { enabled = true, minDiskGb = 5, minRamGb = 1 })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Invalid_hooks_are_rejected()
    {
        var client = _f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/hooks", new { kind = "nope", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/hooks", new { kind = "clone", name = "x", sourceStackId = "missing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/hooks", new { kind = "store", name = "x", appId = "no-such-app" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/hooks", new { kind = "git", name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Regenerating_moves_instances_to_the_new_token()
    {
        var client = _f.CreateClient();
        var s = await (await client.PostAsJsonAsync("/api/stacks", new
        {
            name = "src", targetFramework = "net10.0", nodes = Array.Empty<object>(), edges = Array.Empty<object>(),
            rawStatements = Array.Empty<string>(), extraFiles = Array.Empty<object>(), extraPackages = Array.Empty<object>(),
        })).Content.ReadFromJsonAsync<JsonElement>();
        var sid = s.GetProperty("id").GetString()!;
        var stacks = new StackStore(_f.DbPath);
        stacks.Save(stacks.Get(sid)! with { HookToken = "regen1" });
        Hooks().Save(new Hook("regen1", "clone", "c", SourceStackId: sid));

        var fresh = (await Body(await client.PostAsync("/api/hooks/regen1/regenerate", null))).GetProperty("token").GetString()!;

        Assert.Equal(fresh, stacks.Get(sid)!.HookToken);
        Assert.Null(Hooks().Get("regen1"));
    }
}
