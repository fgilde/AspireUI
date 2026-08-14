using System.Net.Http.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;

[Collection("ServerIntegration")]
public class ApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _c;
    private readonly TestWebAppFactory _f;
    public ApiTests(TestWebAppFactory f) { _f = f; _c = f.CreateClient(); }

    [Fact]
    public async Task CreateThenGet_Works()
    {
        var create = await _c.PostAsJsonAsync("/api/stacks",
            new StackModel("", "MyStack", "net9.0", [], [], [], [], []));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<StackModel>();
        Assert.False(string.IsNullOrEmpty(created!.Id));

        var got = await _c.GetFromJsonAsync<StackModel>($"/api/stacks/{created.Id}");
        Assert.Equal("MyStack", got!.Name);
    }

    [Fact]
    public async Task Catalog_ReturnsList()
    {
        var cat = await _c.GetFromJsonAsync<List<ResourceTypeDto>>("/api/catalog");
        Assert.NotNull(cat);
    }

    [Fact]
    public async Task Catalog_ContainsCoreResourceTypes()
    {
        var cat = await _c.GetFromJsonAsync<List<ResourceTypeDto>>("/api/catalog");
        Assert.Contains(cat!, r => r.AddMethod == "AddRedis");
        Assert.Contains(cat!, r => r.AddMethod == "AddPostgres");
        Assert.Contains(cat!, r => r.AddMethod == "AddContainer");
    }

    [Fact]
    public async Task Preview_ReturnsGeneratedCode()
    {
        var create = await _c.PostAsJsonAsync("/api/stacks",
            new StackModel("", "PrevStack", "net10.0", [], [], [], [], []));
        var created = await create.Content.ReadFromJsonAsync<StackModel>();
        var code = await _c.GetStringAsync($"/api/stacks/{created!.Id}/preview");
        Assert.Contains("DistributedApplication.CreateBuilder", code);
        Assert.Contains("aspireui:begin", code);
    }
    [Fact]
    public async Task Packages_GroupsByOverlayMappedPackage()
    {
        var redis = new NodeModel("n1", "cache", "AddRedis", "cache", [], 0, 0, []);
        var n8n = new NodeModel("n2", "flow", "AddN8n", "flow", [], 0, 0, []);
        var create = await _c.PostAsJsonAsync("/api/stacks",
            new StackModel("", "PkgStack", "net10.0", [redis, n8n], [], [], [], []));
        var created = await create.Content.ReadFromJsonAsync<StackModel>();

        var packages = await _c.GetFromJsonAsync<List<PackageDto>>($"/api/stacks/{created!.Id}/packages");

        Assert.Contains(packages!, p => p.Id == "Aspire.Hosting.AppHost" && p.Version == "13.4.6" && p.Resources.Count == 0);
        Assert.Contains(packages!, p => p.Id == "Aspire.Hosting.Redis" && p.Version == "13.4.6" && p.Resources.SequenceEqual(["cache"]));
        var n8nVersion = CatalogService.PackageVersions()["Nextended.Aspire.Hosting.N8n"];
        Assert.Contains(packages!, p => p.Id == "Nextended.Aspire.Hosting.N8n" && p.Version == n8nVersion && p.Resources.SequenceEqual(["flow"]));
    }

    [Fact]
    public async Task Packages_UnknownStack_Returns404()
    {
        var resp = await _c.GetAsync("/api/stacks/does-not-exist/packages");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task LocalImport_AppHost_RunAsIs_KeepsFilesVerbatim()
    {
        // Modern Aspire (9+) uses AppHost.cs (not Program.cs) as the entry point, in a subfolder.
        var sources = new List<SourceFileDto>
        {
            new("aspire/Demo.AppHost/AppHost.cs", B64("""
                var builder = DistributedApplication.CreateBuilder(args);
                builder.AddRedis("cache");
                builder.Build().Run();
                """)),
            new("aspire/Demo.AppHost/Demo.AppHost.csproj", B64("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <Sdk Name="Aspire.AppHost.Sdk" Version="13.4.6" />
                  <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" Version="13.4.6" />
                    <PackageReference Include="Some.Real.Pkg" Version="1.2.3" />
                  </ItemGroup>
                </Project>
                """)),
        };

        var resp = await _c.PostAsJsonAsync("/api/import/local", new LocalImportRequestDto("AppHostStack", "apphost", sources));
        resp.EnsureSuccessStatusCode();
        var stack = await resp.Content.ReadFromJsonAsync<StackModel>();

        Assert.True(stack!.RunAsIs);
        Assert.Equal("aspire/Demo.AppHost/Demo.AppHost.csproj", stack.AppHostProject);
        Assert.Contains(stack.Nodes, n => n.AddMethod == "AddRedis" && n.ResourceName == "cache");

        var workspace = Path.Combine(_f.WorkspaceDir, stack.Id);
        // Original files kept verbatim (real package refs preserved), no generated project overwriting them.
        Assert.Contains("Some.Real.Pkg", await File.ReadAllTextAsync(Path.Combine(workspace, "aspire/Demo.AppHost/Demo.AppHost.csproj")));
        Assert.True(File.Exists(Path.Combine(workspace, "aspire/Demo.AppHost/AppHost.cs")));
    }

    [Fact]
    public async Task LocalImport_NoAppHostNoCompose_Returns422()
    {
        var sources = new List<SourceFileDto> { new("Foo.cs", B64("public class Foo { }")) };
        var resp = await _c.PostAsJsonAsync("/api/import/local", new LocalImportRequestDto("NoProgram", null, sources));
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task LocalImport_Compose_MapsServices()
    {
        var sources = new List<SourceFileDto>
        {
            new("docker-compose.yml", B64("""
                services:
                  cache:
                    image: redis:7
                    ports:
                      - "6379:6379"
                """)),
        };
        var resp = await _c.PostAsJsonAsync("/api/import/local", new LocalImportRequestDto("ComposeLocal", "compose", sources));
        resp.EnsureSuccessStatusCode();
        var stack = await resp.Content.ReadFromJsonAsync<StackModel>();
        Assert.Contains(stack!.Nodes, n => n.AddMethod == "AddContainer" && n.ResourceName == "cache");
    }

    public record ResourceTypeDto(string AddMethod, string Label);
    public record PackageDto(string Id, string Version, List<string> Resources);
    public record SourceFileDto(string Path, string Content);
    public record LocalImportRequestDto(string? Name, string? Mode, List<SourceFileDto> Sources, string[]? Files = null, string[]? Services = null, Dictionary<string, string>? Env = null);
}
