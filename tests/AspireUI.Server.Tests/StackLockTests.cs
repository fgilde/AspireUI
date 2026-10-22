using System.Net;
using System.Net.Http.Json;
using AspireUI.Server.Models;

[Collection("ServerIntegration")]
public class StackLockTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    [Fact]
    public async Task A_locked_stack_refuses_changes_until_unlocked()
    {
        var c = f.CreateClient();
        var s = (await (await c.PostAsJsonAsync("/api/stacks", new StackModel("", "Locky", "net10.0", [], [], [], [], [])))
            .Content.ReadFromJsonAsync<StackModel>())!;

        (await c.PutAsJsonAsync($"/api/stacks/{s.Id}/lock", new { needsPassword = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Locked, (await c.PutAsJsonAsync($"/api/stacks/{s.Id}", s)).StatusCode);
        Assert.Equal(HttpStatusCode.Locked, (await c.DeleteAsync($"/api/stacks/{s.Id}")).StatusCode);
        (await c.PutAsJsonAsync($"/api/stacks/{s.Id}/category", new { category = "Media" })).EnsureSuccessStatusCode();

        (await c.PostAsJsonAsync($"/api/stacks/{s.Id}/unlock", new { })).EnsureSuccessStatusCode();
        (await c.PutAsJsonAsync($"/api/stacks/{s.Id}", s)).EnsureSuccessStatusCode();
        var after = (await c.GetFromJsonAsync<List<StackModel>>("/api/stacks"))!.Single(x => x.Id == s.Id);
        Assert.Equal("Media", after!.Category);
        Assert.Null(after.LockedBy);
    }
}
