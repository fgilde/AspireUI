using AspireUI.Server.Services;

public class DevProxyTests
{
    // Real shape from a PVE dev run: it-tools published to loopback; a hosting sibling on 0.0.0.0 must
    // NOT match (that one's already LAN-reachable), and the AspireUI container itself is irrelevant.
    private const string Ps = """
        {"Names":"it-tools-spvrxadq","Ports":"127.0.0.1:32773->80/tcp"}
        {"Names":"aspireui-c6156c42-it-tools-1","Ports":"0.0.0.0:20000->80/tcp, [::]:20000->80/tcp"}
        {"Names":"aspireui","Ports":"0.0.0.0:8080->8080/tcp"}
        """;

    [Fact]
    public void ParseLoopbackPorts_matches_dev_container_by_resource_name_and_loopback_publish()
    {
        var got = DevProxyService.ParseLoopbackPorts(Ps, new[] { "it-tools" });
        var one = Assert.Single(got);
        Assert.Equal("it-tools", one.Resource);
        Assert.Equal(32773, one.Port);
    }

    [Fact]
    public void ParseLoopbackPorts_ignores_unknown_resources_and_garbage()
    {
        Assert.Empty(DevProxyService.ParseLoopbackPorts(Ps, new[] { "redis" }));
        Assert.Empty(DevProxyService.ParseLoopbackPorts("docker: error\n", new[] { "it-tools" }));
    }

    [Theory]
    [InlineData("http://localhost:42129", "192.168.178.63", 32773, "http://192.168.178.63:32773")]
    [InlineData("http://localhost:42129/path", "192.168.178.63", 32773, "http://192.168.178.63:32773/path")]
    public void WithHostPort_swaps_host_and_port(string url, string host, int port, string expected)
        => Assert.Equal(expected, HostUrls.WithHostPort(url, host, port));
}
