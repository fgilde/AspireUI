using AspireUI.Server.Services;

public class ManifestImporterTests
{
    private const string App = """
        {
          "id": "my-app",
          "label": "My App",
          "group": "Tools",
          "image": "ghcr.io/acme/my-app:1.4.0",
          "port": 8080,
          "env": [["DB_HOST", "${db}"]],
          "params": [{ "key": "app-secret", "env": "APP_SECRET", "default": "", "secret": true }],
          "companions": [{ "key": "db", "addMethod": "AddContainer", "resourceName": "my-app-db", "image": "postgres:16", "role": "postgres" }],
          "volumes": [["data", "/app/data"]],
          "submitter": "acme",
          "source": "https://github.com/acme/my-app"
        }
        """;

    [Fact]
    public void A_single_app_object_becomes_a_stack()
    {
        var (stack, error) = ManifestImporter.ToStack("s1", null, App);
        Assert.Null(error);
        Assert.NotNull(stack);
        Assert.Equal("My App", stack!.Name);

        var main = stack.Nodes[0];
        Assert.Equal("AddContainer", main.AddMethod);
        Assert.Contains("\"ghcr.io/acme/my-app:1.4.0\"", main.AddArgs);
        Assert.Contains(main.WithCalls, w => w.Method == "WithHttpEndpoint" && w.Args.Any(a => a.Contains("8080")));
        Assert.Contains(main.WithCalls, w => w.Method == "WithVolume" && w.Args[1] == "\"/app/data\"");
        Assert.Contains(main.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0] == "\"DB_HOST\"" && w.Args[1] == "\"my-app-db\"");

        var param = Assert.Single(stack.Nodes, n => n.AddMethod == "AddParameter");
        Assert.True(param.AddArgs[0].Trim('"').Length >= 32);      // secret generated, not shipped
        Assert.Single(stack.Nodes, n => n.ResourceName == "my-app-db");
        Assert.Single(stack.Edges);
    }

    [Fact]
    public void The_stack_name_can_be_overridden()
    {
        var (stack, _) = ManifestImporter.ToStack("s1", "chosen name", App);
        Assert.Equal("chosen name", stack!.Name);
    }

    [Fact]
    public void An_array_manifest_takes_the_first_app()
    {
        var (apps, error) = ManifestImporter.Parse($"[{App}]");
        Assert.Null(error);
        Assert.Single(apps);
        Assert.Equal("my-app", apps[0].Id);
        Assert.Equal("acme", apps[0].Submitter);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{ not json", "valid JSON")]
    [InlineData("[]", "no app")]
    [InlineData("""{ "id": "x", "label": "X", "group": "Tools", "port": 80 }""", "required")]
    [InlineData("""{ "id": "x", "label": "X", "group": "Tools", "image": "x:1" }""", "required")]
    public void A_broken_manifest_reports_why(string json, string expected)
    {
        var (stack, error) = ManifestImporter.ToStack("s1", null, json);
        Assert.Null(stack);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Submitted_apps_in_the_community_folder_are_served_by_the_store()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "catalog", "presets", "community");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "zzz-submitted-test.json");
        File.WriteAllText(file, App.Replace("\"my-app\"", "\"zzz-submitted-test\""));
        try
        {
            var app = Assert.Single(new CatalogService().GetPresets(), p => p.Id == "zzz-submitted-test");
            Assert.Equal("acme", app.Submitter);
            Assert.Equal("https://github.com/acme/my-app", app.Source);
        }
        finally { File.Delete(file); }
    }
}
