using AspireUI.Server.Services;

public class HostUrlsTests
{
    [Theory]
    [InlineData("http://localhost:8080/x", "192.168.1.5", "http://192.168.1.5:8080/x")]
    [InlineData("http://127.0.0.1:34815", "192.168.1.5", "http://192.168.1.5:34815")]
    [InlineData("http://0.0.0.0:5000/", "10.0.0.9", "http://10.0.0.9:5000/")]
    [InlineData("https://example.com/y", "192.168.1.5", "https://example.com/y")]   // non-loopback untouched
    [InlineData("http://localhostings:80", "1.2.3.4", "http://localhostings:80")]   // must not match a prefix
    public void Rewrite_replaces_only_loopback_hosts(string url, string host, string expected)
        => Assert.Equal(expected, HostUrls.Rewrite(url, host));

    [Fact]
    public void Rewrite_no_op_when_host_empty() => Assert.Equal("http://localhost:80", HostUrls.Rewrite("http://localhost:80", ""));

    [Theory]
    [InlineData("192.168.1.5", true)]
    [InlineData("hosting.example.com", false)]
    [InlineData("localhost", false)]
    public void IsIpLiteral_detects_ipv4(string host, bool expected) => Assert.Equal(expected, HostUrls.IsIpLiteral(host));
}
