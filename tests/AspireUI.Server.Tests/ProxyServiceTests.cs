using AspireUI.Server.Services;

public class ProxyServiceTests
{
    [Theory]
    [InlineData("Demo Shop", "demo-shop")]
    [InlineData("Jellyfin!!", "jellyfin")]
    [InlineData("  ", "app")]
    public void Slug_makes_dns_labels(string name, string expected)
        => Assert.Equal(expected, ProxyService.Slug(name));

    [Fact]
    public void BuildCaddyfile_one_site_block_per_route()
    {
        var caddy = ProxyService.BuildCaddyfile(new[] { ("jellyfin", 8096), ("api", 5000) }, "home.example");
        Assert.Contains("jellyfin.home.example {", caddy);
        Assert.Contains("reverse_proxy localhost:8096", caddy);
        Assert.Contains("api.home.example {", caddy);
        Assert.Contains("reverse_proxy localhost:5000", caddy);
    }

    [Fact]
    public void UrlFor_http_for_localhost_https_for_real_domain()
    {
        var local = new ProxyService(new DeployService(), "/tmp/p", "localhost");
        Assert.Equal("http://demo-shop.localhost", local.UrlFor("Demo Shop"));
        var real = new ProxyService(new DeployService(), "/tmp/p", "apps.example.com");
        Assert.Equal("https://demo-shop.apps.example.com", real.UrlFor("Demo Shop"));
    }
}
