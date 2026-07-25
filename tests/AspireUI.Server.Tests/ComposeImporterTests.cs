using AspireUI.Server.Services;

public class ComposeImporterTests
{
    private const string Yaml = """
services:
  db:
    image: postgres:16
    environment:
      POSTGRES_PASSWORD: secret
  web:
    image: nginx:latest
    ports:
      - "8080:80"
    environment:
      - API_URL=http://db:5432
    depends_on:
      - db
""";

    [Fact]
    public void Import_MapsServicesToContainers_WithPortsEnvAndDependsOn()
    {
        var (stack, error) = new ComposeImporter().Import("s1", "Compose", Yaml);
        Assert.Null(error);
        Assert.NotNull(stack);
        Assert.Equal(2, stack!.Nodes.Count);
        Assert.All(stack.Nodes, n => Assert.Equal("AddContainer", n.AddMethod));

        var web = stack.Nodes.First(n => n.ResourceName == "web");
        Assert.Contains("\"nginx:latest\"", web.AddArgs);
        Assert.Contains(web.WithCalls, w => w.Method == "WithHttpEndpoint" && w.Args.Contains("port: 8080") && w.Args.Contains("targetPort: 80"));
        Assert.Contains(web.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0] == "\"API_URL\"");

        var db = stack.Nodes.First(n => n.ResourceName == "db");
        Assert.Contains(db.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0] == "\"POSTGRES_PASSWORD\"");

        Assert.Contains(stack.Edges, e => e.FromNodeId == web.Id && e.ToNodeId == db.Id && e.Kind == "waitFor");
    }

    [Fact]
    public void Import_EmptyOrInvalid_ReturnsError()
    {
        Assert.NotNull(new ComposeImporter().Import("s", "x", "not: valid: compose: here").error
            ?? new ComposeImporter().Import("s", "x", "version: '3'").error);
    }

    [Fact]
    public void Import_BuildService_MapsToAddDockerfile()
    {
        const string yaml = """
            services:
              api:
                build:
                  context: ./api
                  dockerfile: Dockerfile.prod
                ports:
                  - "5000:5000"
              worker:
                build: ./worker
            """;
        var (stack, error) = new ComposeImporter().Import("s", "x", yaml);
        Assert.Null(error);
        Assert.NotNull(stack);

        var api = stack!.Nodes.First(n => n.ResourceName == "api");
        Assert.Equal("AddDockerfile", api.AddMethod);
        Assert.Equal(new[] { "\"./api\"", "\"Dockerfile.prod\"" }, api.AddArgs);
        Assert.Contains(api.WithCalls, w => w.Method == "WithHttpEndpoint");

        var worker = stack.Nodes.First(n => n.ResourceName == "worker");
        Assert.Equal("AddDockerfile", worker.AddMethod);
        Assert.Equal(new[] { "\"./worker\"" }, worker.AddArgs);
    }
}
