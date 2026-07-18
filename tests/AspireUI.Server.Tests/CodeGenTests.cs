using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class CodeGenTests
{
    private static StackModel Fixture() => new("s1", "Demo", "net9.0",
        [
            new NodeModel("n1", "db", "AddPostgres", "db", [new WithCall("WithDataVolume", [])], 0, 0),
            new NodeModel("n2", "cache", "AddRedis", "cache", [], 0, 0)
        ],
        [new EdgeModel("e1", "n1", "n2", "reference")]);

    [Fact]
    public void Generate_EmitsMarkerBlockInCanonicalOrder()
    {
        var code = new CodeGenService().GenerateProgram(Fixture());
        Assert.Contains("// >>> aspireui:begin", code);
        Assert.Contains("var db = builder.AddPostgres(\"db\");", code);
        Assert.Contains("var cache = builder.AddRedis(\"cache\");", code);
        Assert.Contains("db.WithDataVolume();", code);
        Assert.Contains("db.WithReference(cache);", code);
        Assert.Contains("// <<< aspireui:end", code);
        // declarations precede modifications
        Assert.True(code.IndexOf("var cache =") < code.IndexOf("db.WithDataVolume();"));
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
