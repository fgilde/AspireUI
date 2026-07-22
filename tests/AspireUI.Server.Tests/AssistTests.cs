using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

// Fake chat client: returns whatever Response is set to. Never touches the network, so tests
// stay deterministic and offline.
public class FakeChatClient : IChatClient
{
    public string Response = "";
    public Task<string> CompleteAsync(string system, string user, AppSettings s) => Task.FromResult(Response);
}

[Collection("ServerIntegration")]
public class AssistTests : IClassFixture<TestWebAppFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly FakeChatClient _fake = new();
    private readonly HttpClient _c;

    public AssistTests(TestWebAppFactory factory)
    {
        var f = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IChatClient>(_fake)));
        _c = f.CreateClient();
    }

    private async Task<StackModel> CreateStackAsync(string name)
    {
        var create = await _c.PostAsJsonAsync("/api/stacks", new StackModel("", name, "net10.0", [], [], [], [], []));
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<StackModel>())!;
    }

    [Fact]
    public async Task Assist_AppliesReturnedStack_AndReturnsReply()
    {
        await _c.PutAsJsonAsync("/api/settings", new AppSettings("https://fake.example.com", "key", "gpt-4", "Fake"));
        var stack = await CreateStackAsync("AssistStack1");

        var withCache = stack with
        {
            Nodes = [.. stack.Nodes, new NodeModel("n-cache", "cache", "AddRedis", "cache", [], 0, 0, [])],
        };
        _fake.Response = JsonSerializer.Serialize(new { reply = "added", stack = withCache }, JsonOpts);

        var resp = await _c.PostAsJsonAsync($"/api/stacks/{stack.Id}/assist", new { prompt = "add a redis cache" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("added", body.GetProperty("reply").GetString());

        var got = await _c.GetFromJsonAsync<StackModel>($"/api/stacks/{stack.Id}");
        Assert.Contains(got!.Nodes, n => n.AddMethod == "AddRedis" && n.ResourceName == "cache");
    }

    [Fact]
    public async Task Assist_MalformedJson_Returns422_AndLeavesStackUnchanged()
    {
        await _c.PutAsJsonAsync("/api/settings", new AppSettings("https://fake.example.com", "key", "gpt-4", "Fake"));
        var stack = await CreateStackAsync("AssistStack2");

        _fake.Response = "not valid json {{{";

        var resp = await _c.PostAsJsonAsync($"/api/stacks/{stack.Id}/assist", new { prompt = "add a redis cache" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var got = await _c.GetFromJsonAsync<StackModel>($"/api/stacks/{stack.Id}");
        Assert.Empty(got!.Nodes);
    }

    [Fact]
    public async Task Assist_IncompleteStack_Returns422_AndLeavesStackUnchanged()
    {
        await _c.PutAsJsonAsync("/api/settings", new AppSettings("https://fake.example.com", "key", "gpt-4", "Fake"));
        var stack = await CreateStackAsync("AssistStack4");

        // Model omits nodes/edges/etc entirely - System.Text.Json fills those list properties
        // with null rather than failing to parse, so this must be treated as a parse failure
        // (422), not passed through to CodeGenService (which would NRE on the null lists -> 500).
        _fake.Response = """{"reply":"x","stack":{"id":"s","name":"n","targetFramework":"net10.0"}}""";

        var resp = await _c.PostAsJsonAsync($"/api/stacks/{stack.Id}/assist", new { prompt = "add a redis cache" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var got = await _c.GetFromJsonAsync<StackModel>($"/api/stacks/{stack.Id}");
        Assert.Empty(got!.Nodes);
    }

    [Fact]
    public async Task Assist_NoAiConfigured_Returns400()
    {
        await _c.PutAsJsonAsync("/api/settings", new AppSettings("", "", "", ""));
        var stack = await CreateStackAsync("AssistStack3");

        var resp = await _c.PostAsJsonAsync($"/api/stacks/{stack.Id}/assist", new { prompt = "add a redis cache" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Assist_UnknownStack_Returns404()
    {
        await _c.PutAsJsonAsync("/api/settings", new AppSettings("https://fake.example.com", "key", "gpt-4", "Fake"));

        var resp = await _c.PostAsJsonAsync("/api/stacks/does-not-exist/assist", new { prompt = "add a redis cache" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
