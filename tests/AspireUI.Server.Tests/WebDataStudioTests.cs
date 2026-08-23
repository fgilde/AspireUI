using System.Diagnostics;
using Aspire.Hosting;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Nextended.Aspire.Hosting.WebDataStudio;

namespace AspireUI.Server.Tests;

// The builder attaches WebDataStudio as a resource plus a reference edge; codegen turns that into
// AddWebDataStudio(...) + studio.WithReference(db) — the package's own overload, not Aspire's.
public class WebDataStudioTests
{
    private static StackModel Stack() => new("s1", "wds", "net10.0",
        [
            new NodeModel("n1", "db", "AddPostgres", "db", [], 0, 0, []),
            new NodeModel("n2", "webDataStudio", "AddWebDataStudio", "webdatastudio", [], 320, 130, []),
        ],
        [new EdgeModel("e1", "n2", "n1", "reference")], [], [], []);

    [Fact]
    public void ReferenceEdge_BindsThePackagesOwnOverload_NotAspires()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var db = builder.AddPostgres("pg");
        var studio = builder.AddWebDataStudio();

        studio.WithReference(db); // exactly what codegen emits for the edge

        Assert.Contains("PG", studio.Resource.ConnectionNames);
    }

    [Fact]
    public void Generate_EmitsTheStudioAndItsReference()
    {
        var code = new CodeGenService().GenerateProgram(Stack());
        Assert.Contains("using Nextended.Aspire.Hosting.WebDataStudio;", code);
        Assert.Contains("var webDataStudio = builder.AddWebDataStudio(\"webdatastudio\");", code);
        Assert.Contains("webDataStudio.WithReference(db);", code);
    }

    [Fact]
    public void Csproj_ReferencesThePackage()
    {
        var csproj = new CodeGenService().GenerateCsproj(Stack());
        var version = CatalogService.PackageVersions()["Nextended.Aspire.Hosting.WebDataStudio"];
        Assert.Contains($"<PackageReference Include=\"Nextended.Aspire.Hosting.WebDataStudio\" Version=\"{version}\" />", csproj);
    }

    [Fact]
    public void Catalog_ExposesTheStudio_AndHidesTheGenericAttachMethod()
    {
        var catalog = new CatalogService().GetCatalog();
        var studio = Assert.Single(catalog, r => r.AddMethod == "AddWebDataStudio");
        Assert.Equal("WebDataStudio", studio.Label);
        Assert.Equal("webdatastudio", studio.Icon);
        Assert.Equal("Database", studio.Group);
        Assert.Equal("Nextended.Aspire.Hosting.WebDataStudio", studio.Package);
        Assert.False(string.IsNullOrWhiteSpace(studio.Description));

        // WithWebDataStudio<T> is unconstrained, so it would otherwise land on every resource type;
        // the builder offers it as an action on the databases whose engine the package can derive.
        Assert.DoesNotContain(catalog, r => r.Withs.Any(w => w.Method == "WithWebDataStudio"));
    }

    [Fact]
    public void Store_HasTheStandaloneApp()
    {
        var preset = Assert.Single(new CatalogService().GetPresets(), p => p.Id == "webdatastudio");
        Assert.Equal("ghcr.io/fgilde/webdatastudio:latest", preset.Image);
        Assert.Equal(8080, preset.Port);
        Assert.Contains(preset.Params!, p => p.Env == "WDS_PASSWORD" && p.Secret);
        Assert.Contains(preset.Volumes!, v => v[1] == "/data");
        Assert.Equal("/media/webdatastudio/logo.png", preset.Logo);
        Assert.Equal(7, preset.Screenshots!.Count);
    }

    [Fact]
    public void StudioStack_CompilesWithZeroErrors()
    {
        var stack = Stack() with { Id = Guid.NewGuid().ToString("n") };
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-wds-" + Guid.NewGuid());
        try
        {
            new CodeGenService().Materialize(stack, dir);
            var psi = new ProcessStartInfo("dotnet", "build")
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(TimeSpan.FromMinutes(5)), "dotnet build did not finish within 5 minutes");
            Assert.True(proc.ExitCode == 0, $"dotnet build failed (exit {proc.ExitCode}):\n{stdout}\n{stderr}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
