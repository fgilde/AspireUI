using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

[Collection("ServerIntegration")]
public class RetrieveApiTests : IClassFixture<NoAuthTestFactory>, IDisposable
{
    private readonly NoAuthTestFactory _f;
    public RetrieveApiTests(NoAuthTestFactory f) => _f = f;

    public void Dispose()
    {
        foreach (var k in new[] { "ASPIREUI_RETRIEVE_API", "ASPIREUI_RETRIEVE_API_KEY", "ASPIREUI_RETRIEVE_API_HIDDEN" })
            Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public async Task Off_by_default()
    {
        var r = await _f.CreateClient().GetAsync(RetrieveApi.Path);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Anonymous_when_enabled_without_a_key()
    {
        Environment.SetEnvironmentVariable("ASPIREUI_RETRIEVE_API", "true");
        var r = await _f.CreateClient().GetAsync(RetrieveApi.Path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(JsonValueKind.Array, (await r.Content.ReadFromJsonAsync<JsonElement>()).ValueKind);
    }

    [Fact]
    public async Task A_key_is_required_once_set()
    {
        Environment.SetEnvironmentVariable("ASPIREUI_RETRIEVE_API_KEY", "s3cret");
        var client = _f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(RetrieveApi.Path)).StatusCode);

        var wrong = new HttpRequestMessage(HttpMethod.Get, RetrieveApi.Path) { Headers = { { RetrieveApi.KeyHeader, "nope" } } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);

        var right = new HttpRequestMessage(HttpMethod.Get, RetrieveApi.Path) { Headers = { { RetrieveApi.KeyHeader, "s3cret" } } };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(right)).StatusCode);
    }

    [Fact]
    public void Initially_hidden_names_yield_to_an_explicit_choice()
    {
        Environment.SetEnvironmentVariable("ASPIREUI_RETRIEVE_API_HIDDEN", "Grafana; n8n");
        var s = new StackModel("id", "grafana", "net10.0", [], [], [], [], []);
        Assert.True(RetrieveApi.IsHidden(s, s.Name));
        Assert.False(RetrieveApi.IsHidden(s with { RetrieveHidden = false }, s.Name));
        Assert.True(RetrieveApi.IsHidden(null, "N8N"));
        Assert.False(RetrieveApi.IsHidden(null, "other"));
        Assert.True(RetrieveApi.IsHidden(new StackModel("x", "other", "net10.0", [], [], [], [], [], RetrieveHidden: true), "other"));
    }

    [Fact]
    public void A_stack_is_healthy_when_every_visible_resource_is_up()
    {
        LiveResource R(string state, string? style = null, bool hidden = false) =>
            new("r", "r", "Project", state, style, null, [new(null, "http://x", false, false)], hidden, []);
        Assert.True(RetrieveApi.StackHealthy([R("Running", "success"), R("Finished", "info"), R("Starting", hidden: true)]));
        Assert.False(RetrieveApi.StackHealthy([R("Running"), R("Starting")]));
        Assert.False(RetrieveApi.StackHealthy([R("Running"), R("Exited", "error")]));
        Assert.False(RetrieveApi.StackHealthy([]));
    }

    [Fact]
    public void Hosted_health_follows_state_and_health()
    {
        var d = new Deployment("d", "s", "app", "", "", "running", [], "", "", null);
        Assert.True(RetrieveApi.HostedHealthy(d));
        Assert.True(RetrieveApi.HostedHealthy(d with { Health = "ok" }));
        Assert.False(RetrieveApi.HostedHealthy(d with { Health = "unhealthy" }));
        Assert.False(RetrieveApi.HostedHealthy(d with { State = "stopped" }));
    }
}

[Collection("ServerIntegration")]
public class RetrieveApiToggleTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public RetrieveApiToggleTests(TestWebAppFactory f) => _f = f;

    [Fact]
    public async Task Hiding_a_stack_shows_in_the_info_and_can_be_undone()
    {
        Environment.SetEnvironmentVariable("ASPIREUI_RETRIEVE_API", "true");
        try
        {
            var client = _f.CreateClient();
            var s = await (await client.PostAsJsonAsync("/api/stacks", new
            {
                name = "retr", targetFramework = "net10.0", nodes = Array.Empty<object>(), edges = Array.Empty<object>(),
                rawStatements = Array.Empty<string>(), extraFiles = Array.Empty<object>(), extraPackages = Array.Empty<object>(),
            })).Content.ReadFromJsonAsync<JsonElement>();
            var id = s.GetProperty("id").GetString()!;

            async Task<bool> Hidden() => (await client.GetFromJsonAsync<JsonElement>("/api/retrieve/info"))
                .GetProperty("hiddenStackIds").EnumerateArray().Any(e => e.GetString() == id);

            Assert.False(await Hidden());
            (await client.PutAsJsonAsync($"/api/stacks/{id}/retrieve-hidden", new { hidden = true })).EnsureSuccessStatusCode();
            Assert.True(await Hidden());
            (await client.PutAsJsonAsync($"/api/stacks/{id}/retrieve-hidden", new { hidden = false })).EnsureSuccessStatusCode();
            Assert.False(await Hidden());
        }
        finally { Environment.SetEnvironmentVariable("ASPIREUI_RETRIEVE_API", null); }
    }
}
