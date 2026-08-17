using AspireUI.Server.Models;
using AspireUI.Server.Services;

// "running" is not the same as "works": a crash-looping or unhealthy container is what a user sees as
// "AspireUI says the app runs, but nothing answers".
public class HostingHealthTests
{
    private static ServiceStatus Svc(string service, string state, string status) =>
        new($"proj-{service}-1", service, "acme/img:1", state, status, "20000:3000");

    [Fact]
    public void All_running_and_healthy_is_ok()
    {
        var (health, detail) = HostingService.HealthOf([
            Svc("app", "running", "Up 2 minutes (healthy)"),
            Svc("db", "running", "Up 2 minutes (healthy)"),
        ]);
        Assert.Equal("ok", health);
        Assert.Null(detail);
    }

    [Fact]
    public void A_restart_loop_is_failing_and_names_the_container()
    {
        var (health, detail) = HostingService.HealthOf([
            Svc("app", "restarting", "Restarting (1) 5 seconds ago"),
            Svc("db", "running", "Up 3 minutes (healthy)"),
        ]);
        Assert.Equal("failing", health);
        Assert.Contains("app", detail);
        Assert.Contains("Restarting", detail);
    }

    [Fact]
    public void An_exited_container_is_failing()
    {
        var (health, detail) = HostingService.HealthOf([Svc("app", "exited", "Exited (1) 10 seconds ago")]);
        Assert.Equal("failing", health);
        Assert.Contains("stopped", detail);
    }

    [Fact]
    public void Unhealthy_beats_starting_but_loses_to_a_restart_loop()
    {
        Assert.Equal("unhealthy", HostingService.HealthOf([
            Svc("app", "running", "Up 1 minute (unhealthy)"),
            Svc("db", "running", "Up 1 minute (health: starting)"),
        ]).Health);

        Assert.Equal("failing", HostingService.HealthOf([
            Svc("app", "running", "Up 1 minute (unhealthy)"),
            Svc("db", "restarting", "Restarting (1) 2 seconds ago"),
        ]).Health);
    }

    [Fact]
    public void Still_starting_is_not_ok_yet()
    {
        var (health, detail) = HostingService.HealthOf([Svc("app", "running", "Up 3 seconds (health: starting)")]);
        Assert.Equal("starting", health);
        Assert.Contains("starting", detail);
    }

    [Fact]
    public void The_bundled_dashboard_never_decides_the_apps_health()
    {
        var (health, _) = HostingService.HealthOf([
            Svc("app", "running", "Up 2 minutes (healthy)"),
            Svc("aspireui-dashboard", "restarting", "Restarting (1) 1 second ago"),
        ]);
        Assert.Equal("ok", health);
    }

    [Fact]
    public void No_containers_means_unknown()
        => Assert.Equal("unknown", HostingService.HealthOf([]).Health);

    [Fact]
    public void Health_round_trips_through_the_store()
    {
        var db = Path.Combine(Path.GetTempPath(), "aspireui-health-" + Guid.NewGuid().ToString("n") + ".db");
        var store = new DeploymentStore(db);
        var d = new Deployment("d1", "s1", "App", "dir", "proj", "running", ["http://host:20000"], "now", "now", null,
            [new PortMapping(3000, 20000, true)], null, "failing", "app keeps restarting — Restarting (1)");
        store.Upsert(d);

        var read = store.Get("d1")!;
        Assert.Equal("failing", read.Health);
        Assert.Contains("keeps restarting", read.HealthDetail);
    }
}
