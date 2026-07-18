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
            new StackModel("", "MyStack", "net9.0", [], []));
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
    public record ResourceTypeDto(string AddMethod, string Label);
}
