using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

// The activity log, through the pipeline that writes it: a request that changes something leaves a
// row naming who did it and to what, a refused request leaves one too, and reading is not activity.
[Collection("ServerIntegration")]
public class AuditTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public AuditTests(TestWebAppFactory f) => _f = f;

    private sealed record Page(int Total, List<AuditEntry> Entries, int RetainDays);

    private static object NewStack(string name) => new
    {
        name, targetFramework = "net10.0", nodes = Array.Empty<object>(), edges = Array.Empty<object>(),
        rawStatements = Array.Empty<string>(), extraFiles = Array.Empty<object>(), extraPackages = Array.Empty<object>(),
    };

    private static async Task<Page> AuditAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<Page>("/api/audit" + query,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    [Fact]
    public async Task A_change_leaves_a_row_naming_the_user_and_the_stack()
    {
        var client = _f.CreateClient();
        var created = await client.PostAsJsonAsync("/api/stacks", NewStack("Audited"));
        created.EnsureSuccessStatusCode();
        var stack = await created.Content.ReadFromJsonAsync<StackModel>();

        var page = await AuditAsync(client, "?q=stacks");
        var row = Assert.Single(page.Entries, e => e.TargetId == stack!.Id && e.Method == "POST");
        Assert.Equal("test-admin", row.User);
        Assert.Equal("stacks", row.Action);
        Assert.Equal("Audited", row.Target);
        Assert.Equal(200, row.Status);
        Assert.True(row.Ms >= 0);
    }

    [Fact]
    public async Task Reading_is_not_activity()
    {
        var client = _f.CreateClient();
        var before = (await AuditAsync(client)).Total;

        await client.GetAsync("/api/stacks");
        await client.GetAsync("/api/hosting");
        await client.GetAsync("/api/catalog/presets");

        Assert.Equal(before, (await AuditAsync(client)).Total);
    }

    [Fact]
    public async Task A_refused_request_is_logged_with_its_status()
    {
        var client = _f.CreateClient();
        // Refused by the handler, not by a permission — and still worth a row.
        var refused = await client.PostAsJsonAsync("/api/targets", new { name = "nope", kind = "not-a-kind" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var row = (await AuditAsync(client, "?q=targets")).Entries.First(e => e.Method == "POST");
        Assert.Equal("targets", row.Action);
        Assert.Equal(400, row.Status);
    }

    [Fact]
    public async Task The_assistant_and_the_editor_are_not_activity()
    {
        var client = _f.CreateClient();
        var before = (await AuditAsync(client)).Total;

        await client.PostAsJsonAsync("/api/stacks/x/code/diagnostics", new { code = "var x = 1;", offset = 0 });
        await client.PostAsJsonAsync("/api/stacks/x/code/complete", new { code = "var x = 1;", offset = 3 });

        Assert.Equal(before, (await AuditAsync(client)).Total);
    }

    [Fact]
    public async Task Retention_can_be_changed_and_applied()
    {
        var client = _f.CreateClient();
        await client.PostAsJsonAsync("/api/stacks", NewStack("Pruned " + Guid.NewGuid().ToString("n")[..6]));

        var set = await client.PutAsJsonAsync("/api/audit/retention", new { days = 3650 });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal(3650, (await AuditAsync(client)).RetainDays);

        // Nothing is that old yet, so pruning removes nothing and says so.
        var pruned = await client.PostAsync("/api/audit/prune", null);
        pruned.EnsureSuccessStatusCode();
        Assert.Contains("\"removed\":0", await pruned.Content.ReadAsStringAsync());
    }
}

// The store on its own: filtering and the retention cut.
public class AuditStoreTests
{
    private static AuditStore Store() => new(":memory:");

    [Fact]
    public void Newest_first_and_filtered_by_user_or_app()
    {
        var store = Store();
        store.Add("u1", "alice", "POST", "/api/stacks/{id}/hosting/deploy", "hosting deploy", "s1", "Vault", 200, 12);
        store.Add("u2", "bob", "POST", "/api/stacks/{id}/hosting/stop", "hosting stop", "s2", "Gitea", 200, 8);
        store.Add("u1", "alice", "DELETE", "/api/stacks/{id}", "delete stacks", "s1", "Vault", 204, 3);

        Assert.Equal(["delete stacks", "hosting stop", "hosting deploy"], store.List().Select(e => e.Action));
        Assert.Equal(2, store.List(userId: "u1").Count);
        Assert.Equal("Gitea", Assert.Single(store.List(q: "bob")).Target);
        Assert.Equal(2, store.List(targetId: "s1").Count);
    }

    [Fact]
    public void Retention_of_zero_keeps_everything()
    {
        var store = Store();
        store.Add(null, "webhook", "POST", "/api/clone-hook/{token}", "clone-hook", null, null, 200, 40);
        Assert.Equal(0, store.Prune(0));
        Assert.Equal(1, store.Count());
    }
}
