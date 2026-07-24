using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AspireUI.Server.Services;

// Connection + a proxy-host entry for an external Nginx Proxy Manager (the user's own NPM instance).
public record NpmConfig(bool Enabled, string BaseUrl, string Email, string Password, string ForwardHost);
public record NpmProxyHost(int Id, List<string> DomainNames, string ForwardScheme, string ForwardHost, int ForwardPort, bool Websockets);

// Thin client for the Nginx Proxy Manager API (github.com/NginxProxyManager). Lets AspireUI create /
// read / update the proxy host that fronts a deployed app, so a user can give it a real domain without
// leaving AspireUI. Accepts self-signed certs (NPM is usually reached over http :81 or a self-signed
// cert on the LAN) — it's the user's own infra.
public static class NpmService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    private static string Root(NpmConfig c) => c.BaseUrl.TrimEnd('/');

    // The machine's primary LAN IPv4 — a far better default forward host than "localhost" (NPM usually
    // runs on another box and must reach the apps over the network). Uses a UDP socket's routing
    // decision (no packet is actually sent); null if it can't be determined.
    public static string? LocalIPv4()
    {
        try
        {
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            return (s.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
        }
        catch { return null; }
    }

    private static async Task<string?> TokenAsync(NpmConfig c)
    {
        var res = await Http.PostAsJsonAsync($"{Root(c)}/api/tokens", new { identity = c.Email, secret = c.Password });
        if (!res.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())?["token"]?.GetValue<string>();
    }

    public static async Task<(bool ok, string? error)> TestAsync(NpmConfig c)
    {
        try { return await TokenAsync(c) is not null ? (true, null) : (false, "authentication failed — check URL, email and password"); }
        catch (Exception e) { return (false, e.Message); }
    }

    private static HttpRequestMessage Req(HttpMethod m, NpmConfig c, string path, string token, object? body = null)
    {
        var r = new HttpRequestMessage(m, $"{Root(c)}{path}");
        r.Headers.Add("Authorization", $"Bearer {token}");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    // NPM returns allow_websocket_upgrade as 0/1 or true/false depending on version.
    private static bool AsBool(JsonNode? n) => n is not null && (n.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.Number => n.GetValue<int>() != 0,
        _ => false,
    });

    public static async Task<List<NpmProxyHost>> ListAsync(NpmConfig c)
    {
        var token = await TokenAsync(c) ?? throw new InvalidOperationException("NPM authentication failed");
        var res = await Http.SendAsync(Req(HttpMethod.Get, c, "/api/nginx/proxy-hosts", token));
        res.EnsureSuccessStatusCode();
        var arr = JsonNode.Parse(await res.Content.ReadAsStringAsync())?.AsArray() ?? new();
        var list = new List<NpmProxyHost>();
        foreach (var n in arr)
        {
            if (n is null) continue;
            list.Add(new NpmProxyHost(
                n["id"]!.GetValue<int>(),
                (n["domain_names"]?.AsArray() ?? new()).Where(x => x is not null).Select(x => x!.GetValue<string>()).ToList(),
                n["forward_scheme"]?.GetValue<string>() ?? "http",
                n["forward_host"]?.GetValue<string>() ?? "",
                n["forward_port"]?.GetValue<int>() ?? 0,
                AsBool(n["allow_websocket_upgrade"])));
        }
        return list;
    }

    // Create (id null/0) or update (PUT) a proxy host. Only the essential fields; the rest default to a
    // plain HTTP reverse proxy with no SSL/access-list.
    public static async Task<NpmProxyHost> UpsertAsync(NpmConfig c, int? id, List<string> domains, string scheme, string host, int port, bool websockets)
    {
        var token = await TokenAsync(c) ?? throw new InvalidOperationException("NPM authentication failed");
        var body = new Dictionary<string, object?>
        {
            ["domain_names"] = domains,
            ["forward_scheme"] = scheme,
            ["forward_host"] = host,
            ["forward_port"] = port,
            ["allow_websocket_upgrade"] = websockets,
            ["access_list_id"] = 0,
            ["certificate_id"] = 0,
            ["ssl_forced"] = false,
            ["caching_enabled"] = false,
            ["block_exploits"] = false,
            ["http2_support"] = false,
            ["hsts_enabled"] = false,
            ["hsts_subdomains"] = false,
            ["advanced_config"] = "",
            ["locations"] = Array.Empty<object>(),
            ["meta"] = new { letsencrypt_agree = false, dns_challenge = false },
        };
        var res = await Http.SendAsync(Req(id is > 0 ? HttpMethod.Put : HttpMethod.Post, c,
            id is > 0 ? $"/api/nginx/proxy-hosts/{id}" : "/api/nginx/proxy-hosts", token, body));
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"NPM rejected the request ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}");
        var n = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        return new NpmProxyHost(n["id"]?.GetValue<int>() ?? id ?? 0, domains, scheme, host, port, websockets);
    }
}
