using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class CodeGenTests
{
    private static StackModel Fixture() => new("s1", "Demo", "net10.0",
        [
            new NodeModel("n1", "db", "AddPostgres", "db", [new WithCall("WithDataVolume", [])], 0, 0, []),
            new NodeModel("n2", "cache", "AddRedis", "cache", [], 0, 0, []),
            new NodeModel("n3", "web", "AddContainer", "web", [], 0, 0, ["\"nginx\""])
        ],
        [
            new EdgeModel("e1", "n1", "n2", "reference"),
            new EdgeModel("e2", "n3", "n1", "waitFor")
        ],
        ["var extra = ReferenceExpression.Create($\"{db.Resource}\");"]);

    [Fact]
    public void Generate_EmitsAddArgs()
    {
        var code = new CodeGenService().GenerateProgram(Fixture());
        Assert.Contains("var web = builder.AddContainer(\"web\", \"nginx\");", code);
    }

    [Fact]
    public void Generate_EmitsMarkerBlockInCanonicalOrder()
    {
        var code = new CodeGenService().GenerateProgram(Fixture());
        Assert.Contains("// >>> aspireui:begin", code);
        Assert.Contains("var db = builder.AddPostgres(\"db\");", code);
        Assert.Contains("var cache = builder.AddRedis(\"cache\");", code);
        Assert.Contains("var extra = ReferenceExpression.Create($\"{db.Resource}\");", code);
        Assert.Contains("db.WithDataVolume();", code);
        Assert.Contains("db.WithReference(cache);", code);
        Assert.Contains("web.WaitFor(db);", code);
        Assert.Contains("// <<< aspireui:end", code);

        // canonical order: declarations -> raw statements -> withCalls -> edges
        var declIdx = code.IndexOf("var cache = builder.AddRedis");
        var rawIdx = code.IndexOf("var extra = ReferenceExpression.Create");
        var withIdx = code.IndexOf("db.WithDataVolume();");
        var edgeIdx = code.IndexOf("db.WithReference(cache);");
        Assert.True(declIdx >= 0 && rawIdx >= 0 && withIdx >= 0 && edgeIdx >= 0);
        Assert.True(declIdx < rawIdx);
        Assert.True(rawIdx < withIdx);
        Assert.True(withIdx < edgeIdx);
    }

    [Fact]
    public void Csproj_IncludesResourcePackages()
    {
        var m = new StackModel("s", "Demo", "net10.0",
            [ new NodeModel("n1", "cache", "AddRedis", "cache", [], 0, 0, []) ], [], []);
        var csproj = new CodeGenService().GenerateCsproj(m);
        Assert.Contains("Aspire.Hosting.Redis", csproj);
        Assert.Contains("Aspire.Hosting.AppHost", csproj);
    }

    [Fact]
    public void Csproj_UsesOverlayPackageVersion_ForNonAspirePackages()
    {
        // Package map is sourced from the catalog overlay (single source of truth, see
        // CatalogService.ResourcePackages). Nextended packages ship their own version, not
        // AspireVersion, and the overlay's "packageVersion" must win over the default.
        var m = new StackModel("s", "Demo", "net10.0",
            [ new NodeModel("n1", "wf", "AddN8n", "wf", [], 0, 0, []) ], [], []);
        var csproj = new CodeGenService().GenerateCsproj(m);
        Assert.Contains("""<PackageReference Include="Nextended.Aspire.Hosting.N8n" Version="10.1.14" />""", csproj);
    }

    [Fact]
    public void Materialize_WritesFilesAndSidecar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-test-" + Guid.NewGuid());
        new CodeGenService().Materialize(Fixture(), dir);
        Assert.True(File.Exists(Path.Combine(dir, "Program.cs")));
        Assert.True(File.Exists(Path.Combine(dir, "Demo.csproj")));
        var sidecar = JsonSerializer.Deserialize<Dictionary<string, double[]>>(
            File.ReadAllText(Path.Combine(dir, "aspireui.json")));
        Assert.True(sidecar!.ContainsKey("n1"));
        Directory.Delete(dir, true);
    }
}
