using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class ImportTests
{
    private static StackModel Fixture() => new("s1", "Demo", "net10.0",
        [
            new NodeModel("n1", "db", "AddPostgres", "db", [new WithCall("WithDataVolume", [])], 5, 6, []),
            new NodeModel("n2", "cache", "AddRedis", "cache", [], 7, 8, []),
            new NodeModel("n3", "web", "AddContainer", "web", [], 9, 10, ["\"nginx\""])
        ],
        [new EdgeModel("e1", "n1", "n2", "reference")]);

    [Fact]
    public void ImportOfGenerate_EqualsOriginal_IgnoringPositions()
    {
        var m = Fixture();
        var code = new CodeGenService().GenerateProgram(m);
        var sidecar = JsonSerializer.Serialize(
            m.Nodes.ToDictionary(n => n.Id, n => new[] { n.X, n.Y }));

        var back = new ImportService().Import("s1", "Demo", code, sidecar);

        // Compare code-relevant shape: (varName, addMethod, resourceName, withCalls) per node, ignoring ids/xy.
        string Key(NodeModel n) => $"{n.VarName}|{n.AddMethod}|{n.ResourceName}|" +
            string.Join(",", n.AddArgs) + "|" +
            string.Join(",", n.WithCalls.Select(w => w.Method + "(" + string.Join(";", w.Args) + ")"));
        Assert.Equal(m.Nodes.Select(Key).OrderBy(x => x),
                     back.Nodes.Select(Key).OrderBy(x => x));

        // Edges compared by (fromVar -> toVar).
        string EdgeKey(StackModel s, EdgeModel e) =>
            s.Nodes.First(n => n.Id == e.FromNodeId).VarName + "->" +
            s.Nodes.First(n => n.Id == e.ToNodeId).VarName;
        Assert.Equal(m.Edges.Select(e => EdgeKey(m, e)),
                     back.Edges.Select(e => EdgeKey(back, e)));

        // Positions restored from sidecar.
        Assert.Equal(5, back.Nodes.First(n => n.VarName == "db").X);
    }
}
