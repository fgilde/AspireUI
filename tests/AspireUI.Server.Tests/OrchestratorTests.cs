using AspireUI.Server.Services;

namespace AspireUI.Server.Tests;

// Targets without a docker socket: reading a cluster's answers and turning a compose file into what a
// managed platform wants. No cluster and no cloud account are touched here.
public class OrchestratorTests
{
    [Fact]
    public void A_crash_looping_pod_is_failing_not_running()
    {
        var json = """
        { "items": [ { "metadata": { "name": "shop-7d9" }, "status": {
            "phase": "Running",
            "containerStatuses": [ { "ready": false, "restartCount": 6,
              "state": { "waiting": { "reason": "CrashLoopBackOff" } } } ] } } ] }
        """;
        var (health, detail) = OrchestratorService.PodHealthOf(json);
        Assert.Equal("failing", health);
        Assert.Contains("CrashLoopBackOff", detail);
    }

    [Fact]
    public void A_pod_that_cannot_pull_its_image_is_failing()
    {
        var json = """
        { "items": [ { "metadata": { "name": "web-1" }, "status": {
            "phase": "Pending",
            "containerStatuses": [ { "ready": false, "restartCount": 0,
              "state": { "waiting": { "reason": "ImagePullBackOff" } } } ] } } ] }
        """;
        Assert.Equal("failing", OrchestratorService.PodHealthOf(json).Health);
    }

    [Fact]
    public void A_pod_that_is_still_coming_up_is_starting_and_a_ready_one_is_ok()
    {
        var starting = """
        { "items": [ { "metadata": { "name": "web-1" }, "status": { "phase": "Running",
            "containerStatuses": [ { "ready": false, "restartCount": 0, "state": { "running": {} } } ] } } ] }
        """;
        Assert.Equal("starting", OrchestratorService.PodHealthOf(starting).Health);

        var ready = """
        { "items": [ { "metadata": { "name": "web-1" }, "status": { "phase": "Running",
            "containerStatuses": [ { "ready": true, "restartCount": 0, "state": { "running": {} } } ] } } ] }
        """;
        Assert.Equal("ok", OrchestratorService.PodHealthOf(ready).Health);
        Assert.Equal("unknown", OrchestratorService.PodHealthOf("""{ "items": [] }""").Health);
    }

    [Fact]
    public void Urls_come_from_ingress_hosts_and_load_balancers()
    {
        var ingress = """
        { "items": [ { "spec": { "tls": [ { "hosts": ["shop.example.com"] } ],
            "rules": [ { "host": "shop.example.com" } ] } } ] }
        """;
        Assert.Equal(["https://shop.example.com"], OrchestratorService.ParseIngressHosts(ingress));

        var svc = """
        { "items": [ { "spec": { "type": "LoadBalancer", "ports": [ { "port": 80 }, { "port": 8443 } ] },
            "status": { "loadBalancer": { "ingress": [ { "ip": "203.0.113.7" } ] } } },
          { "spec": { "type": "ClusterIP", "ports": [ { "port": 5432 } ] } } ] }
        """;
        var urls = OrchestratorService.ParseServiceUrls(svc);
        Assert.Contains("http://203.0.113.7", urls);
        Assert.Contains("http://203.0.113.7:8443", urls);
        Assert.DoesNotContain(urls, u => u.Contains("5432"));
    }

    [Fact]
    public void Pods_become_the_service_list_the_ui_shows()
    {
        var json = """
        { "items": [ { "metadata": { "name": "shop-7d9" }, "status": { "phase": "Running",
            "containerStatuses": [ { "image": "ghcr.io/x/shop:1", "ready": true, "restartCount": 2 } ] } } ] }
        """;
        var list = OrchestratorService.PodServices(json);
        var s = Assert.Single(list);
        Assert.Equal("shop-7d9", s.Name);
        Assert.Equal("ghcr.io/x/shop:1", s.Image);
        Assert.Contains("2 restarts", s.Status);
    }
}

public class ManagedDeployTests
{
    private static string Compose(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), "aspireui-compose-" + Guid.NewGuid().ToString("n")[..8] + ".yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void A_compose_file_becomes_one_service_per_container_without_our_dashboard()
    {
        var path = Compose("""
        services:
          web:
            image: "ghcr.io/acme/web:1.2"
            environment:
              - "DATABASE_URL=postgres://db:5432/app"
              - "PORT=3000"
            ports:
              - "20001:3000"
            volumes:
              - "web-data:/app/storage"
          db:
            image: "postgres:16"
            environment:
              POSTGRES_PASSWORD: "pw"
            expose:
              - "5432"
          aspireui-dashboard:
            image: "mcr.microsoft.com/dotnet/aspire-dashboard:9.0"
        volumes:
          web-data:
        """);
        try
        {
            var services = ManagedDeploy.ReadCompose(path);
            Assert.Equal(2, services.Count);
            var web = services.Single(s => s.Name == "web");
            Assert.Equal("ghcr.io/acme/web:1.2", web.Image);
            Assert.Equal("postgres://db:5432/app", web.Env["DATABASE_URL"]);
            Assert.Contains(3000, web.Ports);
            Assert.True(web.HasVolumes);
            var db = services.Single(s => s.Name == "db");
            Assert.Equal("pw", db.Env["POSTGRES_PASSWORD"]);
            Assert.Contains(5432, db.Ports);
            Assert.False(db.HasVolumes);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void An_ecs_task_definition_carries_image_env_ports_and_logs()
    {
        var svc = new ManagedDeploy.ComposeService("web", "ghcr.io/acme/web:1",
            new Dictionary<string, string> { ["PORT"] = "3000" }, [3000], false);
        var json = ManagedDeploy.TaskDefinition("shop-web", svc.Image, "arn:aws:iam::1:role/ecsTaskExecutionRole", "eu-central-1", svc);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("shop-web", root.GetProperty("family").GetString());
        Assert.Equal("awsvpc", root.GetProperty("networkMode").GetString());
        var c = root.GetProperty("containerDefinitions")[0];
        Assert.Equal("ghcr.io/acme/web:1", c.GetProperty("image").GetString());
        Assert.Equal(3000, c.GetProperty("portMappings")[0].GetProperty("containerPort").GetInt32());
        Assert.Equal("PORT", c.GetProperty("environment")[0].GetProperty("name").GetString());
        Assert.Equal("/ecs/shop-web", c.GetProperty("logConfiguration").GetProperty("options").GetProperty("awslogs-group").GetString());
    }
}
