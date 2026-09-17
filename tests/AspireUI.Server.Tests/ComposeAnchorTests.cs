using AspireUI.Server.Services;

// Compose files share settings between services with a YAML anchor in a top-level x- block and a
// merge key in each service. The importer reads into a typed model and skipped the x- blocks, so the
// anchors were never registered and every alias below them failed the whole file.
public class ComposeAnchorTests
{
    private const string WithAnchors = """
        x-common-env: &common-env
          TZ: Europe/Berlin
          PUID: "1000"

        x-service: &service
          restart: unless-stopped
          networks: [app]

        services:
          api:
            <<: *service
            image: acme/api:1.2
            ports:
              - "8080:8080"
            environment:
              <<: *common-env
              ROLE: api

          worker:
            <<: *service
            image: acme/api:1.2
            environment: *common-env

        networks:
          app:
        """;

    [Fact]
    public void A_file_that_shares_settings_through_anchors_still_imports()
    {
        var (stack, error) = new ComposeImporter().Import("s1", "demo", WithAnchors);
        Assert.Null(error);
        Assert.Equal(["api", "worker"], stack!.Nodes.Select(n => n.ResourceName));
    }

    [Fact]
    public void What_the_anchor_carries_arrives_at_the_service()
    {
        var (stack, _) = new ComposeImporter().Import("s1", "demo", WithAnchors);
        var api = stack!.Nodes.Single(n => n.ResourceName == "api");

        var env = api.WithCalls.Where(w => w.Method == "WithEnvironment")
            .ToDictionary(w => w.Args[0].Trim('"'), w => w.Args[1].Trim('"'));
        Assert.Equal("Europe/Berlin", env["TZ"]);
        Assert.Equal("1000", env["PUID"]);
        Assert.Equal("api", env["ROLE"]);

        // A whole mapping replaced by an alias is the same settings, arriving as one node.
        var worker = stack.Nodes.Single(n => n.ResourceName == "worker");
        var workerEnv = worker.WithCalls.Where(w => w.Method == "WithEnvironment")
            .ToDictionary(w => w.Args[0].Trim('"'), w => w.Args[1].Trim('"'));
        Assert.Equal("Europe/Berlin", workerEnv["TZ"]);
        Assert.DoesNotContain("ROLE", workerEnv.Keys);
    }

    // YAML wants an anchor declared before it is used and docker compose enforces that, but the parser
    // reads the whole stream before resolving, so a block further down still lands. Being the more
    // forgiving of the two is fine here: the values are the ones the file names either way.
    [Fact]
    public void An_anchor_further_down_the_file_is_resolved_too()
    {
        var (stack, error) = new ComposeImporter().Import("s1", "demo", """
            services:
              api:
                image: acme/api:1.2
                environment:
                  <<: *later

            x-later: &later
              TZ: Europe/Berlin
            """);
        Assert.Null(error);
        var api = Assert.Single(stack!.Nodes);
        Assert.Contains(api.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0] == "\"TZ\"" && w.Args[1] == "\"Europe/Berlin\"");
    }

    [Fact]
    public void Broken_yaml_is_still_reported_as_broken()
    {
        var (stack, error) = new ComposeImporter().Import("s1", "demo", """
            services:
              api:
                image: [unclosed
            """);
        Assert.Null(stack);
        Assert.Contains("compose YAML", error);
    }

}

// Compose interpolation nests: ${A:-ghcr.io/${B:-acme}} is one default built from another, and a
// ${VAR:?message} is a value the file insists on. Left unresolved they end up inside an image name.
public class ComposeInterpolationTests
{
    [Theory]
    [InlineData("${REG:-ghcr.io/${OWNER:-acme}}/app:${TAG:-v1}", "ghcr.io/acme/app:v1")]
    [InlineData("${A:-${B:-${C:-deep}}}", "deep")]
    [InlineData("${HOST:-localhost}:${PORT:-8080}", "localhost:8080")]
    [InlineData("${SECRET:?set SECRET in .env}", "")]
    [InlineData("plain/image:1.2", "plain/image:1.2")]
    public void Nested_defaults_resolve_from_the_inside_out(string input, string expected)
    {
        Assert.Equal(expected, ComposeImporter.ResolveEnv(input, null));
    }

    [Fact]
    public void A_value_the_user_supplied_still_wins()
    {
        var env = new Dictionary<string, string> { ["OWNER"] = "fgilde", ["SECRET"] = "s3cret" };
        Assert.Equal("ghcr.io/fgilde/app:v1", ComposeImporter.ResolveEnv("${REG:-ghcr.io/${OWNER:-acme}}/app:${TAG:-v1}", env));
        Assert.Equal("s3cret", ComposeImporter.ResolveEnv("${SECRET:?set SECRET in .env}", env));
    }

    [Fact]
    public void An_image_built_from_nested_defaults_is_usable()
    {
        var (stack, error) = new ComposeImporter().Import("s1", "demo", """
            services:
              api:
                image: ${REGISTRY:-ghcr.io/${OWNER:-acme}}/api:${TAG:-v1}
                ports: ["8080:8080"]
            """);
        Assert.Null(error);
        Assert.Equal(["\"ghcr.io/acme/api:v1\""], Assert.Single(stack!.Nodes).AddArgs);
    }
}

public class ComposeAlternativeFormTests
{
    [Fact]
    public void An_alternative_appears_only_when_the_variable_is_set()
    {
        const string tuning = "--enable-source-maps${HEAP_MB:+ --max-old-space-size=512}";
        Assert.Equal("--enable-source-maps", ComposeImporter.ResolveEnv(tuning, null));
        Assert.Equal("--enable-source-maps --max-old-space-size=512",
            ComposeImporter.ResolveEnv(tuning, new Dictionary<string, string> { ["HEAP_MB"] = "512" }));
    }
}

// A compose file may hand a service a whole configuration file in one environment value, as a YAML
// block scalar. Written into a C# string literal unescaped, the newlines end the literal and the
// generated AppHost stops compiling.
public class ComposeMultilineValueTests
{
    [Fact]
    public void A_configuration_passed_as_one_value_survives_the_trip_into_code()
    {
        var (stack, error) = new ComposeImporter().Import("s1", "demo", """
            services:
              livekit:
                image: livekit/livekit-server:v1.12.0
                environment:
                  LIVEKIT_CONFIG: |
                    port: 7880
                    rtc:
                      use_external_ip: false
            """);
        Assert.Null(error);
        var value = Assert.Single(stack!.Nodes)
            .WithCalls.Single(w => w.Method == "WithEnvironment").Args[1];

        Assert.DoesNotContain('\n', value);
        Assert.DoesNotContain('\r', value);
        Assert.Contains(@"\n", value);       // the two characters, as a C# literal carries a newline
        Assert.StartsWith("\"", value);
        Assert.EndsWith("\"", value);
    }
}
