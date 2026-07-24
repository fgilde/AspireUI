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
}
