using System.Net.NetworkInformation;
using System.Net.Sockets;
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
            ? url : Regex.Replace(url, @"^(\w+://)[^/:]+(?=:\d)", m => m.Groups[1].Value + host);

    // Replace BOTH host and port of a URL with host:port (keeps scheme + path). For dev links pointed at
    // a socat-forwarded LAN endpoint.
    public static string WithHostPort(string url, string host, int port) =>
        string.IsNullOrEmpty(url) ? url : Regex.Replace(url, @"^(\w+://)[^/]+", m => m.Groups[1].Value + host + ":" + port);

    // Candidate LAN IPv4s of this machine (for the "detect" button). Excludes loopback + APIPA. Inside a
    // container this is only the docker-bridge IP — so it's a hint, not gospel; the user can override.
    public static List<string> CandidateIPs()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Where(ip => !ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                .Distinct().ToList();
        }
        catch { return new(); }
    }
}
