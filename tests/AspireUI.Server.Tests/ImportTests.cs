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
        [
            new EdgeModel("e1", "n1", "n2", "reference"),
            new EdgeModel("e2", "n3", "n1", "waitFor")
        ],
        ["var extra = ReferenceExpression.Create($\"{db.Resource}\");"],
        [],
        []);

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

        // Edges compared by (fromVar -> toVar -> kind).
        string EdgeKey(StackModel s, EdgeModel e) =>
            s.Nodes.First(n => n.Id == e.FromNodeId).VarName + "->" +
            s.Nodes.First(n => n.Id == e.ToNodeId).VarName + ":" + e.Kind;
        Assert.Equal(m.Edges.Select(e => EdgeKey(m, e)),
                     back.Edges.Select(e => EdgeKey(back, e)));

        // Raw statements survive verbatim, in order.
        Assert.Equal(m.RawStatements, back.RawStatements);

        // Positions restored from sidecar.
        Assert.Equal(5, back.Nodes.First(n => n.VarName == "db").X);
    }

    [Fact]
    public void FluentChain_ParsesNodeWithChain()
    {
        const string code = """
            var builder = DistributedApplication.CreateBuilder(args);
            var db = builder.AddPostgres("db");
            var web = builder.AddContainer("web", "nginx").WithHttpEndpoint(8080).WithReference(db);
            builder.Build().Run();
            """;

        var m = new ImportService().Import("s1", "Demo", code, "");

        var web = Assert.Single(m.Nodes, n => n.VarName == "web");
        Assert.Equal("AddContainer", web.AddMethod);
        Assert.Equal("web", web.ResourceName);
        Assert.Equal(["\"nginx\""], web.AddArgs);

        var withHttp = Assert.Single(web.WithCalls, w => w.Method == "WithHttpEndpoint");
        Assert.Equal(["8080"], withHttp.Args);

        var db = Assert.Single(m.Nodes, n => n.VarName == "db");
        var edge = Assert.Single(m.Edges);
        Assert.Equal(web.Id, edge.FromNodeId);
        Assert.Equal(db.Id, edge.ToNodeId);
        Assert.Equal("reference", edge.Kind);
    }

    [Fact]
    public void Markerless_UnknownAddIsNode()
    {
        const string code = """
            var builder = DistributedApplication.CreateBuilder(args);
            var x = builder.AddMyCustomThing("x");
            builder.Build().Run();
            """;

        var m = new ImportService().Import("s1", "Demo", code, "");

        var node = Assert.Single(m.Nodes, n => n.VarName == "x");
        Assert.Equal("AddMyCustomThing", node.AddMethod);
        Assert.Equal("x", node.ResourceName);
    }

    [Fact]
    public void Unparseable_becomesRaw()
    {
        const string code = """
            var builder = DistributedApplication.CreateBuilder(args);
            var db = builder.AddPostgres("db");
            var expr = ReferenceExpression.Create($"{db.Resource}");
            builder.Build().Run();
            """;

        var m = new ImportService().Import("s1", "Demo", code, "");

        Assert.Contains("var expr = ReferenceExpression.Create($\"{db.Resource}\");", m.RawStatements);
    }
}
