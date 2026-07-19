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

    [Fact]
    public async Task ImportBundle_ParsesProgramCsprojAndExtraFiles()
    {
        var files = new List<BundleFile>
        {
            new("Program.cs", """
                var builder = DistributedApplication.CreateBuilder(args);
                builder.AddRedis("cache");
                builder.Build().Run();
                """),
            new("Demo.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" Version="13.4.6" />
                    <PackageReference Include="Some.Pkg" Version="1.2.3" />
                  </ItemGroup>
                </Project>
                """),
            new("Helpers.cs", "public static class Helpers { }"),
        };

        var resp = await _c.PostAsJsonAsync("/stacks/import-bundle",
            new ImportBundleRequestDto("BundleStack", files, null));
        resp.EnsureSuccessStatusCode();
        var stack = await resp.Content.ReadFromJsonAsync<StackModel>();

        Assert.Contains(stack!.Nodes, n => n.AddMethod == "AddRedis" && n.ResourceName == "cache");
        Assert.Contains(stack.ExtraPackages, p => p.Id == "Some.Pkg" && p.Version == "1.2.3");
        Assert.Contains(stack.ExtraFiles, f => f.Name == "Helpers.cs");

        // ExtraPackages only show up in the generated .csproj, not the catalog-driven /packages
        // endpoint, so check the materialized file on disk directly.
        var workspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AspireUI", "workspace", stack.Id);
        var csprojFile = Directory.GetFiles(workspace, "*.csproj").Single();
        Assert.Contains("Some.Pkg", await File.ReadAllTextAsync(csprojFile));
        Assert.True(File.Exists(Path.Combine(workspace, "Helpers.cs")));
    }

    [Fact]
    public async Task ImportBundle_MsBuildPropertyVersion_FallsBackToAspireVersion()
    {
        var files = new List<BundleFile>
        {
            new("Program.cs", """
                var builder = DistributedApplication.CreateBuilder(args);
                builder.AddRedis("cache");
                builder.Build().Run();
                """),
            new("Demo.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" Version="13.4.6" />
                    <PackageReference Include="Foo" Version="$(AspireVersion)" />
                  </ItemGroup>
                </Project>
                """),
        };

        var resp = await _c.PostAsJsonAsync("/stacks/import-bundle",
            new ImportBundleRequestDto("BundleStack2", files, null));
        resp.EnsureSuccessStatusCode();
        var stack = await resp.Content.ReadFromJsonAsync<StackModel>();

        // The literal "$(AspireVersion)" is meaningless outside the source csproj — must be
        // resolved to the concrete Aspire version we generate against instead.
        Assert.Contains(stack!.ExtraPackages, p => p.Id == "Foo" && p.Version == "13.4.6");
        Assert.DoesNotContain(stack.ExtraPackages, p => p.Id == "Foo" && p.Version == "$(AspireVersion)");
    }

    [Fact]
    public async Task ImportBundle_NoAppHostProgram_Returns422()
    {
        var files = new List<BundleFile> { new("Foo.cs", "public class Foo { }") };
        var resp = await _c.PostAsJsonAsync("/stacks/import-bundle",
            new ImportBundleRequestDto("NoProgram", files, null));
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    public record ResourceTypeDto(string AddMethod, string Label);
    public record PackageDto(string Id, string Version, List<string> Resources);
    public record BundleFile(string Path, string Content);
    public record ImportBundleRequestDto(string Name, List<BundleFile> Files, string? ProgramPath);
}
