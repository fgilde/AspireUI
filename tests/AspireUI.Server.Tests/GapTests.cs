using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Tests;

// The parts that make an orchestrator target actually usable: reachable services, persistent volumes,
// and environments whose host names still point at something after renaming.
public class KubeExposeTests
{
    [Fact]
    public void An_emptyDir_volume_becomes_a_claim_of_the_release()
    {
        var yaml = """
              volumes:
                - name: "data"
                  emptyDir: {}
                - name: "cache"
                  emptyDir: {}
            """;
        var (rewritten, names) = OrchestratorService.RewriteEmptyDirs(yaml);
        Assert.Equal(["data", "cache"], names);
        Assert.DoesNotContain("emptyDir", rewritten);
        Assert.Contains("persistentVolumeClaim:", rewritten);
        Assert.Contains("claimName: \"{{ .Release.Name }}-data\"", rewritten);
        Assert.Contains("claimName: \"{{ .Release.Name }}-cache\"", rewritten);
    }

    [Fact]
    public void Anything_that_is_not_an_emptyDir_is_left_alone()
    {
        var yaml = """
              volumes:
                - name: "config"
                  configMap:
                    name: "app-config"
            """;
        var (rewritten, names) = OrchestratorService.RewriteEmptyDirs(yaml);
        Assert.Empty(names);
        Assert.Equal(yaml.Replace("\r\n", "\n"), rewritten);
    }

    [Fact]
    public void The_chart_gets_one_claim_file_and_the_templates_are_rewritten()
    {
        var chart = Path.Combine(Path.GetTempPath(), "aspireui-chart-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(chart, "templates", "web"));
        File.WriteAllText(Path.Combine(chart, "templates", "web", "deployment.yaml"), """
              volumes:
                - name: "data"
                  emptyDir: {}
            """);
        try
        {
            var log = OrchestratorService.PersistVolumes(chart, "longhorn", "20Gi");
            Assert.Contains("longhorn", log);
            var claims = File.ReadAllText(Path.Combine(chart, "templates", "aspireui-claims.yaml"));
            Assert.Contains("kind: \"PersistentVolumeClaim\"", claims);
            Assert.Contains("storageClassName: \"longhorn\"", claims);
            Assert.Contains("storage: \"20Gi\"", claims);
            Assert.Contains("persistentVolumeClaim", File.ReadAllText(Path.Combine(chart, "templates", "web", "deployment.yaml")));
        }
        finally { try { Directory.Delete(chart, true); } catch { } }
    }

    [Fact]
    public void An_ingress_is_generated_per_service_with_the_host_pattern()
    {
        var services = """
        { "items": [
          { "metadata": { "name": "web-service", "labels": { "app.kubernetes.io/component": "web" } },
            "spec": { "ports": [ { "port": 8080 } ] } },
          { "metadata": { "name": "k8s-dashboard-service", "labels": { "app.kubernetes.io/component": "k8s-dashboard" } },
            "spec": { "ports": [ { "port": 18888 } ] } } ] }
        """;
        var yaml = OrchestratorService.IngressManifest(services, "aspireui-abc", "shop", "{service}.apps.example.com", "nginx");
        Assert.Contains("kind: Ingress", yaml);
        Assert.Contains("host: web.apps.example.com", yaml);
        Assert.Contains("ingressClassName: nginx", yaml);
        Assert.Contains("number: 8080", yaml);
        Assert.Contains("app.kubernetes.io/instance: aspireui-abc", yaml);
        // Our own dashboard sidecar never gets a public host.
        Assert.DoesNotContain("dashboard", yaml);
    }

    [Fact]
    public void A_node_port_service_becomes_a_reachable_url()
    {
        var json = """
        { "items": [ { "spec": { "type": "NodePort", "ports": [ { "port": 80, "nodePort": 31234 } ] } } ] }
        """;
        Assert.Equal(["http://10.0.0.5:31234"], OrchestratorService.ParseServiceUrls(json, "10.0.0.5"));
        // Without a node address there is nothing to offer.
        Assert.Empty(OrchestratorService.ParseServiceUrls(json));
    }
}

public class ManagedHostRewriteTests
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["db"] = "shop-db",
        ["web"] = "shop-web",
    };

    [Fact]
    public void A_sibling_service_name_is_rewritten_wherever_it_is_a_host()
    {
        Assert.Equal("Host=shop-db;Port=5432;Database=shop",
            ManagedDeploy.RewriteHostValue("Host=db;Port=5432;Database=shop", Map));
        Assert.Equal("postgres://app:pw@shop-db:5432/shop",
            ManagedDeploy.RewriteHostValue("postgres://app:pw@db:5432/shop", Map));
        Assert.Equal("http://shop-web:8080/health",
            ManagedDeploy.RewriteHostValue("http://web:8080/health", Map));
        Assert.Equal("shop-db", ManagedDeploy.RewriteHostValue("db", Map));
    }

    [Fact]
    public void A_word_that_merely_contains_the_name_is_untouched()
    {
        Assert.Equal("POSTGRES_DB=mydb", ManagedDeploy.RewriteHostValue("POSTGRES_DB=mydb", Map));
        Assert.Equal("dbadmin", ManagedDeploy.RewriteHostValue("dbadmin", Map));
        Assert.Equal("webhook", ManagedDeploy.RewriteHostValue("webhook", Map));
    }

    [Fact]
    public void Every_variable_of_a_service_goes_through_it()
    {
        var env = new Dictionary<string, string> { ["DATABASE_URL"] = "postgres://u:p@db:5432/x", ["PORT"] = "3000" };
        var rewritten = ManagedDeploy.RewriteHosts(env, Map);
        Assert.Equal("postgres://u:p@shop-db:5432/x", rewritten["DATABASE_URL"]);
        Assert.Equal("3000", rewritten["PORT"]);
    }

    [Fact]
    public void Only_http_ports_stay_on_http_ingress()
    {
        Assert.True(ManagedDeploy.IsHttpPort(8080));
        Assert.True(ManagedDeploy.IsHttpPort(80));
        Assert.False(ManagedDeploy.IsHttpPort(5432));   // postgres needs TCP ingress
        Assert.False(ManagedDeploy.IsHttpPort(6379));   // redis too
    }
}

// A stack generated against Aspire 13.5 must not reference a 13.4 publisher: that combination throws
// MissingMethodException inside the Kubernetes publisher.
public class PublisherVersionTests
{
    [Theory]
    [InlineData("compose", "Aspire.Hosting.Docker")]
    [InlineData("kubernetes", "Aspire.Hosting.Kubernetes")]
    [InlineData("bicep", "Aspire.Hosting.Azure.AppContainers")]
    public void The_publisher_package_moves_with_the_rest_of_aspire(string target, string package)
    {
        var expected = CatalogService.PackageVersions()[package];
        var csproj = new CodeGenService().GenerateCsproj(
            new StackModel("s", "demo", "net10.0", [new NodeModel("n1", "web", "AddContainer", "web", [], 0, 0, ["\"nginx\""])], [], [], [], []),
            PublishService.EnvFor(target));
        Assert.Contains($"<PackageReference Include=\"{package}\" Version=\"{expected}\" />", csproj);
    }
}
