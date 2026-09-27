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
            new WithCall("WithEnvironment", new() { "\"OLD\"", "\"x\"" }),
            new WithCall("WithEnvironment", new() { "\"SECRET\"", "pw" }),
            new WithCall("WithHttpEndpoint", new() { "targetPort: 80" })));
        var outp = HostingService.ApplyEnvUpdates(stack,
            new Dictionary<string, List<string[]>> { ["n1"] = new() { new[] { "NEW", "y" } } });
        var calls = outp.Nodes[0].WithCalls;
        Assert.DoesNotContain(calls, c => c.Args.Contains("\"OLD\""));
        Assert.Contains(calls, c => c.Method == "WithEnvironment" && c.Args[0] == "\"NEW\"" && c.Args[1] == "\"y\"");
        Assert.Contains(calls, c => c.Method == "WithEnvironment" && c.Args[1] == "pw");
        Assert.Contains(calls, c => c.Method == "WithHttpEndpoint");
    }

    [Fact]
    public void ReadLiteralEnv_returns_only_literal_pairs_unquoted()
    {
        var stack = Stack(Node("n1",
            new WithCall("WithEnvironment", new() { "\"KEY\"", "\"val\"" }),
            new WithCall("WithEnvironment", new() { "\"REF\"", "pw" })));
        var env = HostingService.ReadLiteralEnv(stack);
        Assert.Equal(new[] { "KEY", "val" }, env["n1"].Single());
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
        var pnode = new NodeModel("p1", "runtipirootfolderhost", "AddParameter", "runtipi-root-folder-host",
            new(), 0, 0, new() { "\"/data\"", "true", "false" });
        var stack = Stack(pnode);
        try
        {
            HostingService.FillParameterEnv(stack, envPath);
            var txt = File.ReadAllText(envPath);
            Assert.Contains("RUNTIPI_ROOT_FOLDER_HOST=/data", txt);
            Assert.Matches(@"N8N_PG_PASSWORD=aspireui-[0-9a-f]{24}", txt);
            Assert.DoesNotContain("N8N_PG_PASSWORD=\n", txt);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PublishExposedPorts_keeps_internal_port_unpublished()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                expose:
                  - "80"
                  - "9000"
            """;
        var outp = HostingService.PublishExposedPorts(yaml,
            new Dictionary<int, int> { [80] = 20000 }, new HashSet<int> { 9000 });
        Assert.Contains("- \"20000:80\"", outp);
        Assert.DoesNotContain("9000:", outp);
        Assert.DoesNotContain(":9000", outp);
    }

    [Fact]
    public void PortFree_reports_a_bound_port_as_taken()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        try { Assert.False(HostingService.PortFree(port)); }
        finally { l.Stop(); }
    }

    [Fact]
    public void PortFree_reports_a_port_bound_on_all_interfaces_as_taken()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        try { Assert.False(HostingService.PortFree(port)); }
        finally { l.Stop(); }
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
        Assert.Equal(new[] { "http://localhost:20000" }, urls);
    }


    [Fact]
    public void DomainUrls_matches_enabled_proxy_hosts_by_published_port()
    {
        var d = new Deployment("d1", "s1", "App", "dir", "proj", "running",
            new() { "http://10.0.0.5:20000" }, "", "", null, new() { new PortMapping(80, 20000, true) });
        var hosts = new List<NpmProxyHost>
        {
            new(1, new() { "app.example.com" }, "http", "10.0.0.5", 20000, true, true, 3, true),
            new(2, new() { "plain.example.com" }, "http", "10.0.0.5", 20001, true, true),
            new(3, new() { "off.example.com" }, "http", "10.0.0.5", 20000, true, false),
        };
        Assert.Equal(new[] { "https://app.example.com" }, HostingService.DomainUrls(hosts, d));
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
    public void A_number_in_a_command_is_not_a_port()
    {
        const string yaml = """
            services:
              passbolt:
                image: "passbolt/passbolt:latest-ce"
                command:
                  - "/usr/bin/wait-for.sh"
                  - "-t"
                  - "300"
                  - "passbolt-db:3306"
                  - "--"
                  - "/docker-entrypoint.sh"
                expose:
                  - "80"
            """;
        Assert.Equal(new[] { 80 }, HostingService.ExposedAppPorts(yaml));

        var outp = HostingService.PublishExposedPorts(yaml, new Dictionary<int, int> { [80] = 20001 });
        Assert.Contains("- \"20001:80\"", outp);
        Assert.DoesNotContain(":300\"", outp);
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
        Assert.Contains("- \"20000:80\"", outp);
        Assert.DoesNotContain("- \"18889:18889\"", outp);
        Assert.Contains("http://localhost:20000", HostingService.ParseUrls(outp, "localhost"));
    }

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
        var restarts = outp.Split('\n').Count(l => l.Trim().StartsWith("restart:"));
        Assert.Equal(2, restarts);
    }

    [Fact]
    public void FullTransform_publishes_app_port_and_leaves_networks_alone()
    {
        var outp = HostingService.PublishExposedPorts(HostingService.AddRestartPolicy(AspireShape),
            new Dictionary<int, int> { [80] = 20005 });
        Assert.Contains("- \"20005:80\"", outp);
        Assert.DoesNotContain("restart: unless-stopped\ndriver", outp.Replace(" ", ""));
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
        Assert.DoesNotContain("18888:18888", outp);
        Assert.Contains("80", HostingService.ExposedAppPorts(outp).Select(p => p.ToString()));
        Assert.Contains("driver: \"bridge\"", outp);
    }

    [Fact]
    public void ConfigureDashboard_injects_browser_token_when_hosted()
    {
        var outp = HostingService.ConfigureDashboard(DashShape, host: true, token: "s3cret");
        Assert.Contains("Dashboard__Frontend__BrowserToken=s3cret", outp);
        Assert.Contains("18888:18888", outp);
    }

    [Fact]
    public void ConfigureDashboard_enforces_browser_token_authmode_when_hosted()
    {
        var outp = HostingService.ConfigureDashboard(DashShape, host: true, token: "s3cret");
        Assert.Contains("Dashboard__Frontend__AuthMode=BrowserToken", outp);
        Assert.Contains("Dashboard__Frontend__BrowserToken=s3cret", outp);
    }

    private const string N8nShape = """
        services:
          n8n-pg:
            image: "docker.io/library/postgres:18.3"
            environment:
              POSTGRES_USER: "postgres"
              POSTGRES_PASSWORD: "pw"
          n8n:
            image: "n8nio/n8n:1.110.1"
            environment:
              DB_TYPE: "postgresdb"
              DB_POSTGRESDB_HOST: "n8n-pg"
              DB_POSTGRESDB_DATABASE: "n8n"
        networks:
          aspire:
            driver: "bridge"
        """;

    [Fact]
    public void EnsureCompanionDatabases_creates_expected_db_on_postgres_companion()
    {
        var outp = HostingService.EnsureCompanionDatabases(N8nShape);
        Assert.Contains("POSTGRES_DB: \"n8n\"", outp);
        Assert.Contains("POSTGRES_USER: \"postgres\"", outp);
    }

    [Fact]
    public void EnsureCompanionDatabases_does_not_override_existing_or_touch_non_db_services()
    {
        var alreadySet = N8nShape.Replace("POSTGRES_USER: \"postgres\"", "POSTGRES_DB: \"custom\"\n      POSTGRES_USER: \"postgres\"");
        var outp = HostingService.EnsureCompanionDatabases(alreadySet);
        Assert.Contains("POSTGRES_DB: \"custom\"", outp);
        Assert.DoesNotContain("POSTGRES_DB: \"n8n\"", outp);
    }

    [Fact]
    public void EnsureCompanionDatabases_parses_connection_strings()
    {
        const string yaml = """
            services:
              db:
                image: "postgres:16"
                environment:
                  POSTGRES_PASSWORD: "pw"
              app:
                image: "acme/app"
                environment:
                  ConnectionStrings__app: "Host=db;Port=5432;Database=appdb;Username=postgres;Password=pw"
            """;
        var outp = HostingService.EnsureCompanionDatabases(yaml);
        Assert.Contains("POSTGRES_DB: \"appdb\"", outp);
    }

    [Fact]
    public void ExposedAppPorts_lists_non_dashboard_expose_ports()
    {
        var ports = HostingService.ExposedAppPorts(AspireShape);
        Assert.Equal(new[] { 80 }, ports);
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
        Assert.Equal(2, vols.Count);
    }

    // `aspire publish` leaves every bind mount as an empty variable. Filled with the placeholder an
    // unknown parameter gets, docker made a directory where the file belonged and the container that
    // mounts it never started.
    [Fact]
    public void FillBindMountEnv_points_a_bind_mount_at_the_file_that_was_generated_for_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-bindmount-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Caddyfile"), ":8080 { respond \"ok\" }");
            var env = Path.Combine(dir, ".env");
            File.WriteAllText(env, "CADDY_BINDMOUNT_0=\nOTHER=\n");
            var yaml = """
            services:
              caddy:
                image: "docker.io/caddy:2"
                volumes:
                  - type: "bind"
                    target: "/etc/caddy/Caddyfile"
                    source: "${CADDY_BINDMOUNT_0}"
            """;
            var stack = Stack(Node("caddy",
                new WithCall("WithBindMount", new() { "\"./Caddyfile\"", "\"/etc/caddy/Caddyfile\"" })));

            HostingService.FillBindMountEnv(yaml, stack, dir, env);

            var expected = Path.GetFullPath(Path.Combine(dir, "Caddyfile")).Replace('\\', '/');
            Assert.Contains(File.ReadAllLines(env), l => l == $"CADDY_BINDMOUNT_0={expected}");
            Assert.Contains(File.ReadAllLines(env), l => l == "OTHER=");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // An app that hands out its own address cannot know the port until the deployment picks one.
    [Fact]
    public void FillPublicUrls_writes_the_address_the_app_was_published_under()
    {
        var yaml = HostingService.FillPublicUrls(
            "API: __ASPIREUI_URL_8080__/api\nWS: ws://__ASPIREUI_HOST_8080__/ws\nPORT: __ASPIREUI_PORT_8080__\nOTHER: __ASPIREUI_URL_9000__",
            "box.local", new Dictionary<int, int> { [8080] = 21000 });
        Assert.Contains("API: http://box.local:21000/api", yaml);
        Assert.Contains("WS: ws://box.local:21000/ws", yaml);
        Assert.Contains("PORT: 21000", yaml);
        Assert.Contains("OTHER: http://box.local:9000", yaml);
    }

    [Fact]
    public void FillPublicUrls_uses_the_domain_for_the_port_it_was_bound_to()
    {
        const string yaml = """
            URL: __ASPIREUI_URL_8080__
            HOST: __ASPIREUI_HOST_8080__
            PORT: __ASPIREUI_PORT_8080__
            DOMAIN: __ASPIREUI_DOMAIN_8080__
            SCHEME: __ASPIREUI_SCHEME_8080__
            OTHER: __ASPIREUI_URL_7880__
            OTHERDOMAIN: __ASPIREUI_DOMAIN_7880__
            """;
        var ports = new Dictionary<int, int> { [8080] = 20011, [7880] = 20012 };
        string[] Lines(string y) => y.ReplaceLineEndings("\n").Split('\n');

        var bound = Lines(HostingService.FillPublicUrls(yaml, "192.168.1.5", ports, (8080, "https://chat.example.org")));
        Assert.Contains("URL: https://chat.example.org", bound);
        Assert.Contains("HOST: chat.example.org", bound);
        Assert.Contains("PORT: 443", bound);
        Assert.Contains("DOMAIN: chat.example.org", bound);
        Assert.Contains("SCHEME: https", bound);
        Assert.Contains("OTHER: http://192.168.1.5:20012", bound);
        Assert.Contains("OTHERDOMAIN: 192.168.1.5", bound);

        var plain = Lines(HostingService.FillPublicUrls(yaml, "192.168.1.5", ports));
        Assert.Contains("DOMAIN: 192.168.1.5", plain);
        Assert.Contains("SCHEME: http", plain);
        Assert.Contains("URL: http://192.168.1.5:20011", plain);
    }

    // `aspire publish` drops WithContainerRuntimeArgs — they are docker run flags, and a compose file
    // has nowhere to put them. An app that asked for the host's network was getting the bridge.
    [Fact]
    public void ApplyContainerRuntimeArgs_writes_the_flags_a_compose_file_has_a_place_for()
    {
        var stack = Stack(Node("scanner",
            new WithCall("WithContainerRuntimeArgs", new() { "\"--network=host\"", "\"--cap-add=NET_ADMIN\"", "\"--cap-add=NET_RAW\"" })));
        var yaml = """
        services:
          scanner:
            image: "app:1"
            expose:
              - "5883"
            networks:
              - "aspire"
          other:
            image: "other:1"
            expose:
              - "80"
        """;

        var (outp, hostPorts) = HostingService.ApplyContainerRuntimeArgs(yaml, stack);

        Assert.Contains("    network_mode: \"host\"", outp);
        Assert.Contains("    cap_add:", outp);
        Assert.Contains("      - \"NET_ADMIN\"", outp);
        Assert.Contains("      - \"NET_RAW\"", outp);
        // Compose refuses a ports or expose block next to host networking, and the app binds the
        // host's port itself — so that port is what it answers on.
        Assert.DoesNotContain("      - \"5883\"", outp);
        Assert.DoesNotContain("      - \"aspire\"", outp);
        Assert.Equal(5883, hostPorts["scanner"]);
        // The service that asked for nothing keeps everything it had.
        Assert.Contains("      - \"80\"", outp);
        Assert.Equal(1, outp.Split("network_mode").Length - 1);
    }

    [Fact]
    public void ApplyContainerRuntimeArgs_maps_the_separated_forms_and_leaves_a_plain_stack_alone()
    {
        var stack = Stack(Node("box",
            new WithCall("WithContainerRuntimeArgs", new() { "\"--device\"", "\"/dev/kvm\"", "\"--user\"", "\"0:0\"", "\"--pid=host\"" })));
        var (outp, hostPorts) = HostingService.ApplyContainerRuntimeArgs("services:\n  box:\n    image: \"box:1\"\n", stack);
        Assert.Contains("    devices:", outp);
        Assert.Contains("      - \"/dev/kvm\"", outp);
        Assert.Contains("    user: \"0:0\"", outp);
        Assert.Contains("    pid: \"host\"", outp);
        Assert.Empty(hostPorts);

        var plain = "services:\n  box:\n    image: \"box:1\"\n";
        Assert.Equal(plain, HostingService.ApplyContainerRuntimeArgs(plain, Stack(Node("box"))).Yaml);
    }

    [Fact]
    public void A_path_inside_our_own_container_is_handed_to_the_daemon_as_the_path_on_the_host()
    {
        var mounts = HostingService.ParseMounts("""
            [{"Type":"volume","Name":"aspireui-data","Source":"/var/lib/docker/volumes/aspireui-data/_data","Destination":"/data"},
             {"Type":"bind","Source":"/var/run/docker.sock","Destination":"/var/run/docker.sock"},
             {"Type":"bind","Source":"/srv/ws","Destination":"/data/workspace"}]
            """);

        Assert.Equal("/srv/ws/_publish/x/src/Caddyfile", HostingService.ToHostPath("/data/workspace/_publish/x/src/Caddyfile", mounts));
        Assert.Equal("/var/lib/docker/volumes/aspireui-data/_data/aspireui.db", HostingService.ToHostPath("/data/aspireui.db", mounts));
        Assert.Equal("/database/x", HostingService.ToHostPath("/database/x", mounts));
        Assert.Equal("/opt/app", HostingService.ToHostPath("/opt/app", []));
        Assert.Empty(HostingService.ParseMounts("not json"));
    }

    [Fact]
    public void FillBindMountEnv_hands_the_daemon_the_host_path_when_given_a_translation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-bindmount-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Caddyfile"), ":8080 { respond \"ok\" }");
            var env = Path.Combine(dir, ".env");
            File.WriteAllText(env, "CADDY_BINDMOUNT_0=/data/workspace/stale/Caddyfile\n");
            var yaml = """
            services:
              caddy:
                image: "docker.io/caddy:2"
                volumes:
                  - type: "bind"
                    target: "/etc/caddy/Caddyfile"
                    source: "${CADDY_BINDMOUNT_0}"
            """;
            var stack = Stack(Node("caddy",
                new WithCall("WithBindMount", new() { "\"./Caddyfile\"", "\"/etc/caddy/Caddyfile\"" })));

            HostingService.FillBindMountEnv(yaml, stack, dir, env, p => "/host" + p);

            var expected = "/host" + Path.GetFullPath(Path.Combine(dir, "Caddyfile")).Replace('\\', '/');
            Assert.Contains(File.ReadAllLines(env), l => l == $"CADDY_BINDMOUNT_0={expected}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void An_app_that_writes_its_own_address_is_recognised()
    {
        var plain = Stack(Node("web", new WithCall("WithEnvironment", new() { "\"A\"", "\"x\"" })));
        var own = Stack(Node("web", new WithCall("WithEnvironment", new() { "\"ORIGIN\"", "\"__ASPIREUI_URL_8080__\"" })));
        Assert.False(HostingService.WritesOwnAddress(plain));
        Assert.True(HostingService.WritesOwnAddress(own));
        Assert.True(HostingService.WritesOwnAddress(plain with { ExtraFiles = [new ExtraFile("app.toml", "url = \"__ASPIREUI_HOST_80__\"")] }));
    }
}
