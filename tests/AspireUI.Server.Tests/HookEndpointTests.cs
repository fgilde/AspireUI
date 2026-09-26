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
}
