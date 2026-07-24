using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

// One place to turn a loopback URL (localhost / 127.0.0.1 / 0.0.0.0 / [::1]) into one that's reachable
// from the browser. Dev-run + live-resource links come back as localhost from Aspire's point of view,
// which is useless when AspireUI runs on another machine (a server / container). Callers resolve the
// target host once (a PublicHost setting, else the request IP, else the server's LAN IP) and pass it in.
public static partial class HostUrls
{
    [GeneratedRegex(@"://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])(?=[:/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Loopback();

    public static string Rewrite(string url, string? host) =>
        string.IsNullOrWhiteSpace(host) || string.IsNullOrEmpty(url) ? url : Loopback().Replace(url, $"://{host}");

    public static bool IsIpLiteral(string host) => Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}$");

    // Force the host of a URL to `host`, but ONLY when a :port follows (so a port-based direct link like
    // http://172.17.0.2:20000 becomes http://<host>:20000, while a port-less proxy URL like
    // https://app.example.com is left alone). Preserves the port + path.
    public static string ForceHost(string url, string? host) =>
        string.IsNullOrWhiteSpace(host) || string.IsNullOrEmpty(url)
            ? url : Regex.Replace(url, @"^(\w+://)[^/:]+(?=:\d)", $"$1{host}");
}
