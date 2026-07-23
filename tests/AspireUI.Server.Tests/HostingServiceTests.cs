using AspireUI.Server.Services;

public class HostingServiceTests
{
    private const string Compose = """
        services:
          web:
            image: nginx
            ports:
              - "8096:80"
          api:
            image: acme/api
            ports:
              - "5000:5000"
        """;

    [Fact]
    public void AddRestartPolicy_adds_unless_stopped_to_each_service()
    {
        var outp = HostingService.AddRestartPolicy(Compose);
        var count = outp.Split('\n').Count(l => l.Trim() == "restart: unless-stopped");
        Assert.Equal(2, count);
    }

    [Fact]
    public void AddRestartPolicy_is_idempotent()
    {
        var once = HostingService.AddRestartPolicy(Compose);
        var twice = HostingService.AddRestartPolicy(once);
        Assert.Equal(once.Split('\n').Count(l => l.Trim() == "restart: unless-stopped"),
                     twice.Split('\n').Count(l => l.Trim() == "restart: unless-stopped"));
    }

    [Fact]
    public void ParseUrls_maps_host_ports()
    {
        var urls = HostingService.ParseUrls(Compose, "localhost");
        Assert.Contains("http://localhost:8096", urls);
        Assert.Contains("http://localhost:5000", urls);
    }
}
