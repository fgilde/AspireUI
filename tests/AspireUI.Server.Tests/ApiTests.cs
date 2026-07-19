using System.Net.Http.Json;
using AspireUI.Server.Models;
using Microsoft.AspNetCore.Mvc.Testing;

public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _c;
    public ApiTests(WebApplicationFactory<Program> f) => _c = f.CreateClient();

    [Fact]
    public async Task CreateThenGet_Works()
    {
        var create = await _c.PostAsJsonAsync("/stacks",
            new StackModel("", "MyStack", "net9.0", [], [], [], [], []));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<StackModel>();
        Assert.False(string.IsNullOrEmpty(created!.Id));

        var got = await _c.GetFromJsonAsync<StackModel>($"/stacks/{created.Id}");
        Assert.Equal("MyStack", got!.Name);
    }

    [Fact]
    public async Task Catalog_ReturnsList()
    {
        var cat = await _c.GetFromJsonAsync<List<ResourceTypeDto>>("/catalog");
        Assert.NotNull(cat);
    }

    [Fact]
    public async Task Catalog_ContainsCoreResourceTypes()
    {
        var cat = await _c.GetFromJsonAsync<List<ResourceTypeDto>>("/catalog");
        Assert.Contains(cat!, r => r.AddMethod == "AddRedis");
        Assert.Contains(cat!, r => r.AddMethod == "AddPostgres");
        Assert.Contains(cat!, r => r.AddMethod == "AddContainer");
    }

    [Fact]
    public async Task Preview_ReturnsGeneratedCode()
    {
        var create = await _c.PostAsJsonAsync("/stacks",
            new StackModel("", "PrevStack", "net10.0", [], [], [], [], []));
        var created = await create.Content.ReadFromJsonAsync<StackModel>();
        var code = await _c.GetStringAsync($"/stacks/{created!.Id}/preview");
        Assert.Contains("DistributedApplication.CreateBuilder", code);
        Assert.Contains("aspireui:begin", code);
    }
    [Fact]
    public async Task Packages_GroupsByOverlayMappedPackage()
    {
        var redis = new NodeModel("n1", "cache", "AddRedis", "cache", [], 0, 0, []);
        var n8n = new NodeModel("n2", "flow", "AddN8n", "flow", [], 0, 0, []);
        var create = await _c.PostAsJsonAsync("/stacks",
            new StackModel("", "PkgStack", "net10.0", [redis, n8n], [], [], [], []));
        var created = await create.Content.ReadFromJsonAsync<StackModel>();

        var packages = await _c.GetFromJsonAsync<List<PackageDto>>($"/stacks/{created!.Id}/packages");

        Assert.Contains(packages!, p => p.Id == "Aspire.Hosting.AppHost" && p.Version == "13.4.6" && p.Resources.Count == 0);
        Assert.Contains(packages!, p => p.Id == "Aspire.Hosting.Redis" && p.Version == "13.4.6" && p.Resources.SequenceEqual(["cache"]));
        Assert.Contains(packages!, p => p.Id == "Nextended.Aspire.Hosting.N8n" && p.Version == "10.1.14" && p.Resources.SequenceEqual(["flow"]));
    }

    [Fact]
    public async Task Packages_UnknownStack_Returns404()
    {
        var resp = await _c.GetAsync("/stacks/does-not-exist/packages");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    public record ResourceTypeDto(string AddMethod, string Label);
    public record PackageDto(string Id, string Version, List<string> Resources);
}
