using System.Net;
using System.Net.Http.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class HostingLockTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    private readonly HttpClient _c;
    public HostingLockTests(TestWebAppFactory f) { _f = f; _c = f.CreateClient(); }

    private static StackModel EmptyStack(string name) => new(
        "", name, "net10.0", new(), new(), new(), new(), new());

    [Fact]
    public async Task Mutations_409_while_running_then_ok_after_stop()
    {
        var created = await (await _c.PostAsJsonAsync("/api/stacks", EmptyStack("Locked")))
            .Content.ReadFromJsonAsync<StackModel>();
        var id = created!.Id;

        // Seed a RUNNING deployment for it directly in the shared DB the server reads.
        var store = new DeploymentStore(_f.DbPath);
        store.Upsert(new Deployment("dep1", id, "Locked", "/c", HostingService.Project(id), "running",
            new(), "t", "t", null));

        var put = await _c.PutAsJsonAsync($"/api/stacks/{id}", created with { Name = "changed" });
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);

        var got = await _c.GetFromJsonAsync<StackWithDeployment>($"/api/stacks/{id}");
        Assert.Equal("running", got!.Deployment?.State);

        store.SetState("dep1", "stopped");
        var put2 = await _c.PutAsJsonAsync($"/api/stacks/{id}", created with { Name = "changed" });
        Assert.Equal(HttpStatusCode.OK, put2.StatusCode);
    }

    private record StackWithDeployment(string Id, DeploymentDto? Deployment);
    private record DeploymentDto(string State);
}
