using AspireUI.Server.Services;

public class PresetBuilderTests
{
    private static ContainerPreset Sample() => new(
        "myapp", "My App", "Apps", "acme/app:1", 8080,
        Icon: "myapp", Description: null,
        Env: new() { new() { "DB_HOST", "${db}" }, new() { "PLAIN", "x" } },
        Companions: new() { new PresetCompanion("db", "AddContainer", "mydb", Image: "postgres:16", Port: 5432, Env: new() { new() { "POSTGRES_DB", "app" } }, Role: "postgres") },
        Volumes: new() { new() { "data", "/data" } },
        Params: new() { new PresetParam("pw", "APP_PW", Default: "secret", Secret: false) });

    [Fact]
    public void Build_main_container_has_image_endpoint_volume_and_expanded_env()
    {
        var (nodes, _) = PresetBuilder.Build(Sample());
        var main = Assert.Single(nodes, n => n.AddMethod == "AddContainer" && n.ResourceName == "myapp");
        Assert.Contains("\"acme/app:1\"", main.AddArgs);
        Assert.Contains(main.WithCalls, w => w.Method == "WithHttpEndpoint" && w.Args.Contains("targetPort: 8080"));
        Assert.Contains(main.WithCalls, w => w.Method == "WithVolume" && w.Args[0] == "\"myapp-data\"");
        // ${db} expands to the companion's resource name
        Assert.Contains(main.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0] == "\"DB_HOST\"" && w.Args[1] == "\"mydb\"");
        Assert.Contains(main.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0] == "\"PLAIN\"" && w.Args[1] == "\"x\"");
        // param wired as an unquoted builder reference
        Assert.Contains(main.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0] == "\"APP_PW\"" && !w.Args[1].StartsWith("\""));
    }

    [Fact]
    public void Build_creates_companion_and_parameter_nodes_with_waitfor()
    {
        var (nodes, edges) = PresetBuilder.Build(Sample());
        var main = nodes.First(n => n.ResourceName == "myapp");

        var db = Assert.Single(nodes, n => n.ResourceName == "mydb");
        Assert.Equal("AddContainer", db.AddMethod);
        Assert.Contains("\"postgres:16\"", db.AddArgs);
        Assert.Contains(db.WithCalls, w => w.Method == "WithHttpEndpoint" && w.Args.Contains("targetPort: 5432"));
        Assert.Equal(main.Id, db.SpawnedBy);

        var param = Assert.Single(nodes, n => n.AddMethod == "AddParameter");
        Assert.Equal(new[] { "\"secret\"", "true", "false" }, param.AddArgs);

        var edge = Assert.Single(edges);
        Assert.Equal("waitFor", edge.Kind);
        Assert.Equal(main.Id, edge.FromNodeId);
        Assert.Equal(db.Id, edge.ToNodeId);
    }
}
