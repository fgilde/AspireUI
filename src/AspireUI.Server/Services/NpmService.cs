using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AspireUI.Server.Services;

// Connection + a proxy-host entry for an external Nginx Proxy Manager (the user's own NPM instance).
public record NpmConfig(bool Enabled, string BaseUrl, string Email, string Password, string ForwardHost);
public record NpmProxyHost(int Id, List<string> DomainNames, string ForwardScheme, string ForwardHost, int ForwardPort, bool Websockets, bool Enabled, int CertificateId = 0, bool SslForced = false);

public static class NpmService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly HttpClient HttpLong = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { Timeout = TimeSpan.FromSeconds(120) };

    private static string Root(NpmConfig c) => c.BaseUrl.TrimEnd('/');

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
                AsBool(n["allow_websocket_upgrade"]),
                n["enabled"] is null ? true : AsBool(n["enabled"]),
                n["certificate_id"]?.GetValue<int>() ?? 0,
                AsBool(n["ssl_forced"])));
        }
        return list;
    }

    public static async Task DeleteAsync(NpmConfig c, int id)
    {
        var token = await TokenAsync(c) ?? throw new InvalidOperationException("NPM authentication failed");
        var res = await Http.SendAsync(Req(HttpMethod.Delete, c, $"/api/nginx/proxy-hosts/{id}", token));
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"NPM delete failed ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}");
    }

    public static async Task SetEnabledAsync(NpmConfig c, int id, bool enabled)
    {
        var token = await TokenAsync(c) ?? throw new InvalidOperationException("NPM authentication failed");
        var res = await Http.SendAsync(Req(HttpMethod.Post, c, $"/api/nginx/proxy-hosts/{id}/{(enabled ? "enable" : "disable")}", token));
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"NPM {(enabled ? "enable" : "disable")} failed ({(int)res.StatusCode})");
    }

    public static async Task<int> RequestCertAsync(NpmConfig c, List<string> domains, string email)
    {
        var token = await TokenAsync(c) ?? throw new InvalidOperationException("NPM authentication failed");
        var body = new
        {
            provider = "letsencrypt",
            domain_names = domains,
            meta = new { letsencrypt_email = email, letsencrypt_agree = true, dns_challenge = false },
        };
        var res = await HttpLong.SendAsync(Req(HttpMethod.Post, c, "/api/nginx/certificates", token, body));
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Let's Encrypt request failed ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}. Make sure the domain already points to this server on port 80.");
        var id = JsonNode.Parse(await res.Content.ReadAsStringAsync())?["id"]?.GetValue<int>();
        return id ?? throw new InvalidOperationException("NPM did not return a certificate id");
    }

    public static async Task<NpmProxyHost> UpsertAsync(NpmConfig c, int? id, List<string> domains, string scheme, string host, int port, bool websockets, int certificateId = 0, bool sslForced = false)
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
            ["certificate_id"] = certificateId,
            ["ssl_forced"] = sslForced,
            ["caching_enabled"] = false,
            ["block_exploits"] = false,
            ["http2_support"] = sslForced,
            ["hsts_enabled"] = false,
            ["hsts_subdomains"] = false,
            ["advanced_config"] = "",
            ["locations"] = Array.Empty<object>(),
            ["meta"] = new { letsencrypt_agree = sslForced, dns_challenge = false },
        };
        var res = await Http.SendAsync(Req(id is > 0 ? HttpMethod.Put : HttpMethod.Post, c,
            id is > 0 ? $"/api/nginx/proxy-hosts/{id}" : "/api/nginx/proxy-hosts", token, body));
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"NPM rejected the request ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}");
        var n = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        return new NpmProxyHost(n["id"]?.GetValue<int>() ?? id ?? 0, domains, scheme, host, port, websockets, true, certificateId, sslForced);
    }
}
