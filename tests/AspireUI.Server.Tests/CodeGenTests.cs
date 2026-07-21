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
        ["var extra = ReferenceExpression.Create($\"{db.Resource}\");"],
        [],
        []);

    [Fact]
    public void Generate_EmitsAddArgs()
    {
        var code = new CodeGenService().GenerateProgram(Fixture());
        Assert.Contains("var web = builder.AddContainer(\"web\", \"nginx\");", code);
    }

    [Fact]
    public void Generate_DeclaresReferencedResourceBeforeItsUse()
    {
        // "grafana" is added first but takes "postgres" (added later) as an Add-arg. The postgres
        // var must still be declared before the grafana line that references it.
        var stack = new StackModel("s", "Demo", "net10.0",
        [
            new NodeModel("n1", "grafana", "AddGrafana", "grafana", [], 0, 0, ["postgres"]),
            new NodeModel("n2", "postgres", "AddPostgres", "postgres", [], 0, 0, []),
        ], [], [], [], []);
        var code = new CodeGenService().GenerateProgram(stack);
        Assert.True(code.IndexOf("var postgres =", StringComparison.Ordinal)
            < code.IndexOf("builder.AddGrafana(\"grafana\", postgres)", StringComparison.Ordinal),
            "postgres must be declared before it's used in the grafana line:\n" + code);
    }

    [Fact]
    public void Generate_EmitsResourceUsings_ForLocalAiStack()
    {
        // Real bug (Slice 4 e2e): generated Program.cs only emitted `using Aspire.Hosting;`,
        // so a stack using AddLocalAI's Known* enums (KnownTextModel etc.) failed CS1061/CS0103.
        // Overlay-driven usings (CatalogService.ResourceUsings) must add the resource's namespace.
        var m = new StackModel("s", "Demo", "net10.0",
            [ new NodeModel("n1", "localai", "AddLocalAI", "localai", [], 0, 0, []) ], [], [], [], []);
        var code = new CodeGenService().GenerateProgram(m);
        Assert.Contains("using Aspire.Hosting;", code);
        Assert.Contains("using Aspire.Hosting.ApplicationModel;", code);
        Assert.Contains("using Nextended.Aspire.Hosting.LocalAI;", code);
        // Usings sit outside/before the marker block (round-trip only parses inside it).
        Assert.True(code.IndexOf("using Nextended.Aspire.Hosting.LocalAI;") < code.IndexOf(CodeGenService.Begin));
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

    private static readonly CodeGenService.PublishEnv ComposeEnv =
        new("builder.AddDockerComposeEnvironment(\"aspireui\");", "Aspire.Hosting.Docker", "13.4.6");

    [Fact]
    public void Generate_Env_InjectsAfterBuilder_OutsideMarkerBlock()
    {
        var code = new CodeGenService().GenerateProgram(Fixture(), ComposeEnv);
        Assert.Contains("builder.AddDockerComposeEnvironment(\"aspireui\");", code);
        var builderIdx = code.IndexOf("var builder = DistributedApplication.CreateBuilder(args);");
        var envIdx = code.IndexOf("builder.AddDockerComposeEnvironment(\"aspireui\");");
        Assert.True(builderIdx < envIdx);
        Assert.True(envIdx < code.IndexOf(CodeGenService.Begin));
    }

    [Fact]
    public void Generate_NoEnv_ByDefault()
    {
        var code = new CodeGenService().GenerateProgram(Fixture());
        Assert.DoesNotContain("AddDockerComposeEnvironment", code);
    }

    [Fact]
    public void Csproj_Env_AddsEnvPackage()
    {
        var m = new StackModel("s", "Demo", "net10.0",
            [ new NodeModel("n1", "cache", "AddRedis", "cache", [], 0, 0, []) ], [], [], [], []);
        var csproj = new CodeGenService().GenerateCsproj(m, ComposeEnv);
        Assert.Contains("""<PackageReference Include="Aspire.Hosting.Docker" Version="13.4.6" />""", csproj);
    }

    [Fact]
    public void Csproj_NoEnvPackage_ByDefault()
    {
        var m = new StackModel("s", "Demo", "net10.0",
            [ new NodeModel("n1", "cache", "AddRedis", "cache", [], 0, 0, []) ], [], [], [], []);
        var csproj = new CodeGenService().GenerateCsproj(m);
        Assert.DoesNotContain("Aspire.Hosting.Docker", csproj);
    }

    [Fact]
    public void Csproj_IncludesResourcePackages()
    {
        var m = new StackModel("s", "Demo", "net10.0",
            [ new NodeModel("n1", "cache", "AddRedis", "cache", [], 0, 0, []) ], [], [], [], []);
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
            [ new NodeModel("n1", "wf", "AddN8n", "wf", [], 0, 0, []) ], [], [], [], []);
        var csproj = new CodeGenService().GenerateCsproj(m);
        Assert.Contains("""<PackageReference Include="Nextended.Aspire.Hosting.N8n" Version="10.1.16" />""", csproj);
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

    [Fact]
    public void Csproj_IncludesExtraPackages()
    {
        var m = new StackModel("s", "Demo", "net10.0",
            [ new NodeModel("n1", "cache", "AddRedis", "cache", [], 0, 0, []) ], [], [],
            [], [new PackageRef("Some.Extra.Pkg", "1.2.3")]);
        var csproj = new CodeGenService().GenerateCsproj(m);
        Assert.Contains("""<PackageReference Include="Some.Extra.Pkg" Version="1.2.3" />""", csproj);
    }

    [Fact]
    public void Csproj_DedupesExtraPackage_AlreadyEmittedByResource()
    {
        var m = new StackModel("s", "Demo", "net10.0",
            [ new NodeModel("n1", "cache", "AddRedis", "cache", [], 0, 0, []) ], [], [],
            [], [new PackageRef("Aspire.Hosting.Redis", "999.0.0"), new PackageRef("Aspire.Hosting.AppHost", "999.0.0")]);
        var csproj = new CodeGenService().GenerateCsproj(m);
        Assert.DoesNotContain("999.0.0", csproj);
    }

    [Fact]
    public void Materialize_WritesExtraFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-test-" + Guid.NewGuid());
        var m = Fixture() with { ExtraFiles = [new ExtraFile("Helpers/Foo.cs", "public class Foo {}")] };
        new CodeGenService().Materialize(m, dir);
        var path = Path.Combine(dir, "Helpers", "Foo.cs");
        Assert.True(File.Exists(path));
        Assert.Equal("public class Foo {}", File.ReadAllText(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Materialize_SkipsExtraFile_WithPathTraversal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-test-" + Guid.NewGuid());
        var m = Fixture() with { ExtraFiles = [new ExtraFile("../../evil.cs", "// evil")] };
        new CodeGenService().Materialize(m, dir);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.cs")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Materialize_ExtraFileNamedProgramCs_DoesNotOverwriteGeneratedProgram()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-test-" + Guid.NewGuid());
        var m = Fixture() with { ExtraFiles = [new ExtraFile("Program.cs", "// evil overwrite")] };
        new CodeGenService().Materialize(m, dir);
        var content = File.ReadAllText(Path.Combine(dir, "Program.cs"));
        Assert.Contains(CodeGenService.Begin, content);
        Assert.DoesNotContain("evil overwrite", content);
        Directory.Delete(dir, true);
    }
}
