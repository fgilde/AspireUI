using AspireUI.Server.Models;
using AspireUI.Server.Services;

// Limits and health checks are written into the published compose file, so this is about the yaml.
public class AppRuntimeTests
{
    private const string Yaml = """
        services:
          web:
            image: "nginx:1.27"
            restart: unless-stopped
            ports:
              - "8080:80"
          db:
            image: "postgres:16"
            restart: unless-stopped
          aspire-dashboard:
            image: "mcr.microsoft.com/dotnet/aspire-dashboard:9.0"
            restart: unless-stopped
        volumes:
          data:
        """;

    [Fact]
    public void Nothing_set_leaves_the_file_untouched()
    {
        Assert.Equal(Yaml, HostingService.ApplyRuntime(Yaml, null, null));
        Assert.Equal(Yaml, HostingService.ApplyRuntime(Yaml, new AppLimits(), []));
    }

    [Fact]
    public void Limits_land_on_every_service_of_the_app_but_not_on_our_dashboard()
    {
        var outp = HostingService.ApplyRuntime(Yaml, new AppLimits(Cpus: 1.5, MemoryMb: 512, PidsLimit: 200), null);
        var lines = outp.Split('\n');

        foreach (var service in new[] { "web", "db" })
        {
            var i = Array.FindIndex(lines, l => l == $"  {service}:");
            var block = lines.Skip(i + 1).TakeWhile(l => l.StartsWith("    ")).ToList();
            Assert.Contains("    cpus: 1.5", block);
            Assert.Contains("    mem_limit: 512m", block);
            Assert.Contains("    pids_limit: 200", block);
        }

        var dash = Array.FindIndex(lines, l => l == "  aspire-dashboard:");
        Assert.DoesNotContain("    mem_limit: 512m", lines.Skip(dash + 1).TakeWhile(l => l.StartsWith("    ")));
    }

    [Fact]
    public void A_cap_the_compose_file_already_has_is_left_alone()
    {
        var capped = Yaml.Replace("""
              db:
                image: "postgres:16"
            """, """
              db:
                mem_limit: 64m
                image: "postgres:16"
            """);
        var outp = HostingService.ApplyRuntime(capped, new AppLimits(MemoryMb: 512), null);
        Assert.Contains("mem_limit: 64m", outp);
        Assert.DoesNotContain("mem_limit: 512m", outp.Split("  aspire-dashboard:")[0].Split("  db:")[1]);
    }

    [Fact]
    public void A_restart_policy_replaces_the_one_that_is_there()
    {
        var outp = HostingService.ApplyRuntime(Yaml, new AppLimits(Restart: "always"), null);
        Assert.DoesNotContain("unless-stopped", outp);
        Assert.Equal(3, outp.Split("restart: always").Length - 1);
    }

    [Fact]
    public void A_health_check_goes_to_the_service_it_names()
    {
        var outp = HostingService.ApplyRuntime(Yaml, null,
            [new AppHealthcheck("db", "pg_isready -U postgres", IntervalSec: 10, TimeoutSec: 3, Retries: 5, StartPeriodSec: 20)]);
        var lines = outp.Split('\n');
        var i = Array.FindIndex(lines, l => l == "  db:");
        var block = lines.Skip(i + 1).TakeWhile(l => l.StartsWith("    ")).ToList();

        Assert.Contains("    healthcheck:", block);
        Assert.Contains("      test: [\"CMD-SHELL\", \"pg_isready -U postgres\"]", block);
        Assert.Contains("      interval: 10s", block);
        Assert.Contains("      timeout: 3s", block);
        Assert.Contains("      retries: 5", block);
        Assert.Contains("      start_period: 20s", block);

        var web = Array.FindIndex(lines, l => l == "  web:");
        Assert.DoesNotContain("    healthcheck:", lines.Skip(web + 1).TakeWhile(l => l.StartsWith("    ")));
    }

    [Fact]
    public void A_health_check_the_image_already_defines_is_not_replaced()
    {
        var withCheck = Yaml.Replace("""
              web:
                image: "nginx:1.27"
            """, """
              web:
                healthcheck:
                  test: ["CMD", "true"]
                image: "nginx:1.27"
            """);
        var outp = HostingService.ApplyRuntime(withCheck, null, [new AppHealthcheck("web", "curl -f localhost")]);
        Assert.DoesNotContain("CMD-SHELL", outp);
    }

    [Fact]
    public void A_quote_in_the_test_command_cannot_break_the_yaml()
    {
        var outp = HostingService.ApplyRuntime(Yaml, null, [new AppHealthcheck("web", "sh -c \"exit 0\"")]);
        Assert.Contains("""test: ["CMD-SHELL", "sh -c \"exit 0\""]""", outp);
    }
}
