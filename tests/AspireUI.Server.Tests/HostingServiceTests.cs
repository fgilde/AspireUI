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

    [Fact]
    public void PublishExposedPorts_publishes_expose_to_host_skipping_dashboard()
    {
        const string yaml = """
            services:
              aspireui-dashboard:
                image: dash
                ports:
                  - "18888"
                expose:
                  - "18889"
              pihole:
                image: pihole/pihole
                expose:
                  - "80"
            """;
        var outp = HostingService.PublishExposedPorts(yaml);
        Assert.Contains("- \"80:80\"", outp);              // app port published to host
        Assert.DoesNotContain("- \"18889:18889\"", outp);   // dashboard left alone
        Assert.Contains("http://localhost:80", HostingService.ParseUrls(outp, "localhost"));
    }

    // The real Aspire compose shape: a dashboard service that already has restart:"always", plus a
    // top-level networks: section whose `aspire:` key must NOT be mistaken for a service.
    private const string AspireShape = """
        services:
          aspireui-dashboard:
            image: "dash"
            ports:
              - "18888"
            networks:
              - "aspire"
            restart: "always"
          it-tools:
            image: "ghcr.io/corentinth/it-tools:latest"
            expose:
              - "80"
            networks:
              - "aspire"
        networks:
          aspire:
            driver: "bridge"
        """;

    [Fact]
    public void AddRestartPolicy_no_duplicate_and_skips_networks()
    {
        var outp = HostingService.AddRestartPolicy(AspireShape);
        // dashboard already has restart:"always" (kept, not doubled); it-tools gets one; networks none.
        var restarts = outp.Split('\n').Count(l => l.Trim().StartsWith("restart:"));
        Assert.Equal(2, restarts);
    }

    [Fact]
    public void FullTransform_publishes_app_port_and_leaves_networks_alone()
    {
        var outp = HostingService.PublishExposedPorts(HostingService.AddRestartPolicy(AspireShape));
        Assert.Contains("- \"80:80\"", outp);                 // it-tools reachable on host
        Assert.DoesNotContain("restart: unless-stopped\ndriver", outp.Replace(" ", "")); // no restart under networks
        Assert.Contains("http://localhost:80", HostingService.ParseUrls(outp, "localhost"));
    }

    [Fact]
    public void VolumeNames_reads_top_level_volumes()
    {
        const string yaml = """
            services:
              db:
                image: postgres
                volumes:
                  - data:/var/lib/postgresql/data
            volumes:
              data:
              cache:
            """;
        var vols = HostingService.VolumeNames(yaml);
        Assert.Contains("data", vols);
        Assert.Contains("cache", vols);
        Assert.Equal(2, vols.Count);   // the service-level `volumes:` mapping is not a top-level volume
    }
}
