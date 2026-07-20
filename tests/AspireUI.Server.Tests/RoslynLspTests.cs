using System.Net;
using System.Net.Http.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class RoslynLspTests
{
    // Program.cs carries `using Aspire.Hosting;` so the AddRedis extension (in that namespace,
    // referenced by the server) is in scope for completion after `builder.`.
    private const string Head = "using Aspire.Hosting;\nvar builder = DistributedApplication.CreateBuilder(args);\n";

    [Fact]
    public async Task Complete_AfterBuilderDot_OffersAddRedis()
    {
        var code = Head + "builder.";
        var items = await new RoslynLspService().CompleteAsync(code, code.Length);
        Assert.Contains(items, c => c.Label.Contains("AddRedis"));
    }

    [Fact]
    public void Diagnostics_FlagsBrokenCode_AndClearsForValid()
    {
        var svc = new RoslynLspService();
        Assert.Contains(svc.Diagnostics("var x = ;"), d => d.Severity == "error");
        Assert.DoesNotContain(svc.Diagnostics(Head + "var x = 1;"), d => d.Severity == "error");
    }
}

[Collection("ServerIntegration")]
public class CodeEndpointTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _c;
    public CodeEndpointTests(TestWebAppFactory f) { _c = f.CreateClient(); }

    [Fact]
    public async Task Complete_ReturnsAddRedis()
    {
        var code = "using Aspire.Hosting;\nvar builder = DistributedApplication.CreateBuilder(args);\nbuilder.";
        var r = await _c.PostAsJsonAsync("/stacks/any/code/complete", new { code, offset = code.Length });
        r.EnsureSuccessStatusCode();
        var items = await r.Content.ReadFromJsonAsync<List<CompletionItemDto>>();
        Assert.Contains(items!, i => i.Label.Contains("AddRedis"));
    }

    [Fact]
    public async Task Save_RoundTripsValidProgram_AndPersists()
    {
        var create = await _c.PostAsJsonAsync("/stacks", new StackModel("", "CodeSave", "net10.0", [], [], [], [], []));
        var id = (await create.Content.ReadFromJsonAsync<StackModel>())!.Id;
        var code = "using Aspire.Hosting;\nvar builder = DistributedApplication.CreateBuilder(args);\nvar cache = builder.AddRedis(\"cache\");\nbuilder.Build().Run();";
        var r = await _c.PostAsJsonAsync($"/stacks/{id}/code/save", new { name = "CodeSave", code });
        r.EnsureSuccessStatusCode();
        var stack = await r.Content.ReadFromJsonAsync<StackModel>();
        Assert.Contains(stack!.Nodes, n => n.AddMethod == "AddRedis");
    }

    [Fact]
    public async Task Save_PreservesExtraPackages()
    {
        var create = await _c.PostAsJsonAsync("/stacks", new StackModel("", "KeepExtras", "net10.0",
            [], [], [], [], [new PackageRef("Some.Extra.Pkg", "1.2.3")]));
        var id = (await create.Content.ReadFromJsonAsync<StackModel>())!.Id;
        var code = "using Aspire.Hosting;\nvar builder = DistributedApplication.CreateBuilder(args);\nvar cache = builder.AddRedis(\"cache\");\nbuilder.Build().Run();";
        var r = await _c.PostAsJsonAsync($"/stacks/{id}/code/save", new { name = "KeepExtras", code });
        r.EnsureSuccessStatusCode();
        var stack = await r.Content.ReadFromJsonAsync<StackModel>();
        Assert.Contains(stack!.ExtraPackages, p => p.Id == "Some.Extra.Pkg");
    }

    [Fact]
    public async Task Save_UnknownStack_404()
    {
        var r = await _c.PostAsJsonAsync("/stacks/nope/code/save", new { name = "x", code = "var a=1;" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}

[Collection("ServerIntegration")]
public class CodeAuthTests : IClassFixture<NoAuthTestFactory>
{
    private readonly HttpClient _c;
    public CodeAuthTests(NoAuthTestFactory f) { _c = f.CreateClient(); }

    [Fact]
    public async Task Complete_WithoutAuth_401()
    {
        var r = await _c.PostAsJsonAsync("/stacks/x/code/complete", new { code = "", offset = 0 });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}
