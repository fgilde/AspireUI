using AspireUI.Server.Services;
using AspireUI.Server.Models;

public class HostingServiceTests
{
    private static NodeModel Node(string id, params WithCall[] calls) =>
        new(id, id, "AddContainer", id, calls.ToList(), 0, 0, new() { "\"nginx\"" });
    private static StackModel Stack(params NodeModel[] nodes) =>
        new("s1", "S1", "net10.0", nodes.ToList(), new(), new(), new(), new());

    [Fact]
    public void ApplyEnvUpdates_replaces_literal_env_keeps_param_and_other_calls()
    {
        var stack = Stack(Node("n1",
            new WithCall("WithEnvironment", new() { "\"OLD\"", "\"x\"" }),         // literal → dropped
            new WithCall("WithEnvironment", new() { "\"SECRET\"", "pw" }),         // param ref → kept
            new WithCall("WithHttpEndpoint", new() { "targetPort: 80" })));        // other → kept
        var outp = HostingService.ApplyEnvUpdates(stack,
            new Dictionary<string, List<string[]>> { ["n1"] = new() { new[] { "NEW", "y" } } });
        var calls = outp.Nodes[0].WithCalls;
        Assert.DoesNotContain(calls, c => c.Args.Contains("\"OLD\""));
        Assert.Contains(calls, c => c.Method == "WithEnvironment" && c.Args[0] == "\"NEW\"" && c.Args[1] == "\"y\"");
        Assert.Contains(calls, c => c.Method == "WithEnvironment" && c.Args[1] == "pw");   // param kept
        Assert.Contains(calls, c => c.Method == "WithHttpEndpoint");                        // other kept
    }

    [Fact]
    public void ReadLiteralEnv_returns_only_literal_pairs_unquoted()
    {
        var stack = Stack(Node("n1",
            new WithCall("WithEnvironment", new() { "\"KEY\"", "\"val\"" }),
            new WithCall("WithEnvironment", new() { "\"REF\"", "pw" })));
        var env = HostingService.ReadLiteralEnv(stack);
        Assert.Equal(new[] { "KEY", "val" }, env["n1"].Single());   // only the literal, unquoted
    }

    [Fact]
    public void ParseServices_reads_ndjson_and_publishers()
    {
        const string ps = """
            {"Name":"proj-web-1","Service":"web","Image":"nginx","State":"running","Status":"Up 2m","Publishers":[{"PublishedPort":20000,"TargetPort":80}]}
            {"Name":"proj-db-1","Service":"db","Image":"postgres","State":"running","Status":"Up 2m","Publishers":[]}
            """;
        var svcs = HostingService.ParseServices(ps);
        Assert.Equal(2, svcs.Count);
        Assert.Equal("20000:80", svcs[0].Ports);
        Assert.Equal("", svcs[1].Ports);
    }

    [Fact]
    public void ParseServices_tolerates_garbage() => Assert.Empty(HostingService.ParseServices("docker: error\n"));

    [Fact]
    public void FillParameterEnv_fills_known_value_and_generates_for_unknown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-env-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, ".env");
        File.WriteAllText(envPath, "# Parameter runtipi-root-folder-host\nRUNTIPI_ROOT_FOLDER_HOST=\n\n# Parameter n8n-pg-password\nN8N_PG_PASSWORD=\n");
        // Stack has an AddParameter node for the runtipi one (value "/data"); n8n's is NOT in the model.
        var pnode = new NodeModel("p1", "runtipirootfolderhost", "AddParameter", "runtipi-root-folder-host",
            new(), 0, 0, new() { "\"/data\"", "true", "false" });
        var stack = Stack(pnode);
        try
        {
            HostingService.FillParameterEnv(stack, envPath);
            var txt = File.ReadAllText(envPath);
            Assert.Contains("RUNTIPI_ROOT_FOLDER_HOST=/data", txt);          // known value used
            Assert.Matches(@"N8N_PG_PASSWORD=aspireui-[0-9a-f]{24}", txt);    // unknown → deterministic secret
            Assert.DoesNotContain("N8N_PG_PASSWORD=\n", txt);                 // no longer empty
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UrlsFromServices_uses_published_ports_skips_dashboard()
    {
        var svcs = new List<ServiceStatus>
        {
            new("p-web-1", "web", "nginx", "running", "Up", "20000:80"),
            new("p-dashboard-1", "aspireui-dashboard", "dash", "running", "Up", "18888:18888"),
            new("p-db-1", "db", "postgres", "running", "Up", ""),
        };
        var urls = HostingService.UrlsFromServices(svcs, "localhost");
        Assert.Equal(new[] { "http://localhost:20000" }, urls);   // web only; dashboard + port-less db skipped
    }


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
        var outp = HostingService.PublishExposedPorts(yaml, new Dictionary<int, int> { [80] = 20000 });
        Assert.Contains("- \"20000:80\"", outp);            // app port published to allocated host port
        Assert.DoesNotContain("- \"18889:18889\"", outp);   // dashboard not published
        Assert.Contains("http://localhost:20000", HostingService.ParseUrls(outp, "localhost"));
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
        var outp = HostingService.PublishExposedPorts(HostingService.AddRestartPolicy(AspireShape),
            new Dictionary<int, int> { [80] = 20005 });
        Assert.Contains("- \"20005:80\"", outp);              // it-tools reachable on allocated host port
        Assert.DoesNotContain("restart: unless-stopped\ndriver", outp.Replace(" ", "")); // no restart under networks
        Assert.Contains("http://localhost:20005", HostingService.ParseUrls(outp, "localhost"));
    }

    private const string DashShape = """
        services:
          aspireui-dashboard:
            image: "dash"
            environment:
              - "ASPNETCORE_ENVIRONMENT=Production"
            ports:
              - "18888:18888"
            networks:
              - "aspire"
          web:
            image: "nginx"
            expose:
              - "80"
        networks:
          aspire:
            driver: "bridge"
        """;

    [Fact]
    public void ConfigureDashboard_unpublishes_when_not_hosted()
    {
        var outp = HostingService.ConfigureDashboard(DashShape, host: false, token: null);
        Assert.DoesNotContain("18888:18888", outp);       // dashboard port dropped
        Assert.Contains("80", HostingService.ExposedAppPorts(outp).Select(p => p.ToString())); // app untouched
        Assert.Contains("driver: \"bridge\"", outp);       // networks intact
    }

    [Fact]
    public void ConfigureDashboard_injects_browser_token_when_hosted()
    {
        var outp = HostingService.ConfigureDashboard(DashShape, host: true, token: "s3cret");
        Assert.Contains("Dashboard__Frontend__BrowserToken=s3cret", outp);
        Assert.Contains("18888:18888", outp);              // still published
    }

    [Fact]
    public void ExposedAppPorts_lists_non_dashboard_expose_ports()
    {
        var ports = HostingService.ExposedAppPorts(AspireShape);
        Assert.Equal(new[] { 80 }, ports);   // it-tools' 80; dashboard skipped
    }

    [Fact]
    public void AllocateHostPort_gives_distinct_ports_avoiding_used()
    {
        var used = new HashSet<int> { 20000 };
        var a = HostingService.AllocateHostPort(used);
        var b = HostingService.AllocateHostPort(used);
        Assert.NotEqual(20000, a);
        Assert.NotEqual(a, b);
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
