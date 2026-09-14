using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

/// <summary>
/// Asks a registry whether an image exists, the way a pull would: the manifest endpoint, and the
/// anonymous bearer token the registry hands out when it answers 401. Works for ghcr.io, Docker Hub,
/// quay.io and anything else that speaks the distribution API. Nothing is downloaded.
/// </summary>
public static class ImageRegistry
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private const string Accept =
        "application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, " +
        "application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json";

    public record ImageRef(string Registry, string Repository, string Reference);

    /// <summary>`ghcr.io/owner/app:1.2` → registry, repository, tag or digest. Docker Hub gets its real host and the `library/` prefix.</summary>
    public static ImageRef Parse(string image)
    {
        var s = image.Trim();
        var registry = "registry-1.docker.io";
        var firstSlash = s.IndexOf('/');
        if (firstSlash > 0)
        {
            var head = s[..firstSlash];
            if (head.Contains('.') || head.Contains(':') || head == "localhost")
            {
                registry = head == "docker.io" ? "registry-1.docker.io" : head;
                s = s[(firstSlash + 1)..];
            }
        }
        string reference = "latest";
        var at = s.IndexOf('@');
        if (at > 0) { reference = s[(at + 1)..]; s = s[..at]; }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon > s.LastIndexOf('/')) { reference = s[(colon + 1)..]; s = s[..colon]; }
        }
        if (registry == "registry-1.docker.io" && !s.Contains('/')) s = "library/" + s;
        return new ImageRef(registry, s, reference);
    }

    /// <summary>The image GitHub's own workflows publish for a repository, or null for anything not on github.com.</summary>
    public static string? SuggestFor(string gitUrl)
    {
        var m = Regex.Match(gitUrl.Trim(), @"^(?:https?://|git@)github\.com[/:]([^/\s]+)/([^/\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        return m.Success ? $"ghcr.io/{m.Groups[1].Value.ToLowerInvariant()}/{m.Groups[2].Value.ToLowerInvariant()}:latest" : null;
    }

    public static bool Exists(string image)
    {
        try { return ExistsAsync(image).GetAwaiter().GetResult(); } catch { return false; }
    }

    public static async Task<bool> ExistsAsync(string image, CancellationToken ct = default)
    {
        try
        {
            var r = Parse(image);
            var url = $"https://{r.Registry}/v2/{r.Repository}/manifests/{r.Reference}";
            using var first = await Http.SendAsync(Head(url, null), ct);
            if (first.StatusCode != HttpStatusCode.Unauthorized) return first.IsSuccessStatusCode;

            var token = await TokenAsync(first.Headers.WwwAuthenticate, r, ct);
            if (token is null) return false;
            using var second = await Http.SendAsync(Head(url, token), ct);
            return second.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static HttpRequestMessage Head(string url, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Head, url);
        req.Headers.TryAddWithoutValidation("Accept", Accept);
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    // `WWW-Authenticate: Bearer realm="https://ghcr.io/token",service="ghcr.io",scope="repository:x/y:pull"`
    private static async Task<string?> TokenAsync(HttpHeaderValueCollection<AuthenticationHeaderValue> challenges, ImageRef r, CancellationToken ct)
    {
        var bearer = challenges.FirstOrDefault(c => c.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        if (bearer?.Parameter is null) return null;
        string? P(string name) => Regex.Match(bearer.Parameter, name + "=\"([^\"]*)\"").Groups[1].Value is { Length: > 0 } v ? v : null;
        var realm = P("realm");
        if (realm is null) return null;
        var scope = P("scope") ?? $"repository:{r.Repository}:pull";
        var query = $"{realm}?scope={Uri.EscapeDataString(scope)}" + (P("service") is { } svc ? $"&service={Uri.EscapeDataString(svc)}" : "");
        using var res = await Http.GetAsync(query, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString()
            : doc.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null;
    }
}
