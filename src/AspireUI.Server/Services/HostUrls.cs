using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

// Rewrite loopback URLs (localhost/127.0.0.1/0.0.0.0/[::1]) to reachable host (PublicHost/request IP/LAN IP).
public static partial class HostUrls
{
    [GeneratedRegex(@"://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])(?=[:/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Loopback();

    public static string Rewrite(string url, string? host) =>
        string.IsNullOrWhiteSpace(host) || string.IsNullOrEmpty(url) ? url : Loopback().Replace(url, $"://{host}");

    public static bool IsIpLiteral(string host) => Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}$");

    // Force host when :port follows (direct link); preserve proxy URLs (port-less); keep port + path.
    public static string ForceHost(string url, string? host) =>
        string.IsNullOrWhiteSpace(host) || string.IsNullOrEmpty(url)
            ? url : Regex.Replace(url, @"^(\w+://)[^/:]+(?=:\d)", m => m.Groups[1].Value + host);

    // Replace host and port with host:port; keep scheme + path; for socat-forwarded LAN endpoints.
    public static string WithHostPort(string url, string host, int port) =>
        string.IsNullOrEmpty(url) ? url : Regex.Replace(url, @"^(\w+://)[^/]+", m => m.Groups[1].Value + host + ":" + port);

    // Candidate LAN IPv4s (exclude loopback + APIPA); inside container, only docker-bridge IP.
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
        catch { return []; }
    }
}
