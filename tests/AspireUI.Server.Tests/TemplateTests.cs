using System.Net.Http.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;

public class TemplateTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _c;
    public TemplateTests(WebApplicationFactory<Program> f) => _c = f.CreateClient();

    [Fact]
    public async Task GetTemplates_ListsLocalAiDemo()
    {
        var templates = await _c.GetFromJsonAsync<List<TemplateInfo>>("/templates");
        Assert.Contains(templates!, t => t.Id == "local-ai-demo");
    }

    [Fact]
    public async Task PostFromTemplate_UnknownId_Returns404()
    {
        var res = await _c.PostAsync("/stacks/from-template/does-not-exist", null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task PostFromTemplate_LocalAiDemo_CreatesRunnableStack()
    {
        var res = await _c.PostAsync("/stacks/from-template/local-ai-demo", null);
        res.EnsureSuccessStatusCode();
        var stack = await res.Content.ReadFromJsonAsync<StackModel>();
        Assert.NotNull(stack);
        Assert.True(stack!.Nodes.Count >= 3);

        var gen = new CodeGenService();
        var code = gen.GenerateProgram(stack);
        Assert.Contains("AddOllama", code);
        Assert.Contains("AddN8n", code);
        Assert.Contains("AddLocalAI", code);
        Assert.Contains("ReferenceExpression.Create", code);
        Assert.Contains(".WaitFor(", code);
        Assert.Empty(gen.CompileErrors(code));
    }
}
