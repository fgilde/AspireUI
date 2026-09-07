using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AspireUI.Server.Services;

/// <summary>
/// Signing in with somebody else's identity provider — Keycloak, Authentik, Entra, Google, Zitadel,
/// anything that speaks OpenID Connect. Authorization code with PKCE, and the claims are read from
/// the provider's <c>userinfo</c> endpoint rather than out of a JWT: the access token comes straight
/// back from the token endpoint over TLS, so there is nothing to validate a signature against that we
/// do not already trust, and no JWT library in the login path.
/// </summary>
public record OidcConfig(
    bool Enabled = false,
    string? Authority = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? Scopes = "openid profile email",
    string? Label = null,
    string? UsernameClaim = null,
    string? GroupsClaim = null,
    string? AdminGroup = null,
    bool AutoCreate = true,
    string? DefaultPermissions = null)
{
    public bool Usable => Enabled
        && !string.IsNullOrWhiteSpace(Authority)
        && !string.IsNullOrWhiteSpace(ClientId);

    public string Title => string.IsNullOrWhiteSpace(Label) ? "single sign-on" : Label!.Trim();
}

public record OidcEndpointsInfo(string Authorization, string Token, string? UserInfo, string? EndSession);

public class OidcService(SettingsStore settings, SecretStore secrets)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static (string Authority, OidcEndpointsInfo Info, DateTime At)? _discovered;

    public OidcConfig Config() => new(
        Enabled: (settings.GetValue("OidcEnabled") ?? "false") == "true",
        Authority: settings.GetValue("OidcAuthority"),
        ClientId: settings.GetValue("OidcClientId"),
        ClientSecret: secrets.Resolve(settings.GetValue("OidcClientSecret")),
        Scopes: settings.GetValue("OidcScopes") is { Length: > 0 } s ? s : "openid profile email",
        Label: settings.GetValue("OidcLabel"),
        UsernameClaim: settings.GetValue("OidcUsernameClaim"),
        GroupsClaim: settings.GetValue("OidcGroupsClaim"),
        AdminGroup: settings.GetValue("OidcAdminGroup"),
        AutoCreate: (settings.GetValue("OidcAutoCreate") ?? "true") == "true",
        DefaultPermissions: settings.GetValue("OidcDefaultPermissions"));

    public void Save(OidcConfig c)
    {
        settings.SetValue("OidcEnabled", c.Enabled ? "true" : "false");
        settings.SetValue("OidcAuthority", c.Authority?.Trim().TrimEnd('/'));
        settings.SetValue("OidcClientId", c.ClientId?.Trim());
        settings.SetValue("OidcScopes", c.Scopes?.Trim());
        settings.SetValue("OidcLabel", c.Label?.Trim());
        settings.SetValue("OidcUsernameClaim", c.UsernameClaim?.Trim());
        settings.SetValue("OidcGroupsClaim", c.GroupsClaim?.Trim());
        settings.SetValue("OidcAdminGroup", c.AdminGroup?.Trim());
        settings.SetValue("OidcAutoCreate", c.AutoCreate ? "true" : "false");
        settings.SetValue("OidcDefaultPermissions", c.DefaultPermissions?.Trim());
        // Blank means "keep what is there": the form shows a marker, not the secret.
        if (!string.IsNullOrWhiteSpace(c.ClientSecret))
            settings.SetValue("OidcClientSecret",
                secrets.Replace(settings.GetValue("OidcClientSecret"), c.ClientSecret, "oidc client secret"));
        _discovered = null;
    }

    /// <summary>
    /// The provider's own document, cached for an hour. Everything else is derived from it, so a
    /// provider that moves its endpoints is followed rather than configured twice.
    /// </summary>
    public async Task<(OidcEndpointsInfo? info, string? error)> DiscoverAsync(string? authority = null)
    {
        var root = (authority ?? Config().Authority ?? "").Trim().TrimEnd('/');
        if (root.Length == 0) return (null, "no authority configured");
        if (_discovered is { } cached && cached.Authority == root && DateTime.UtcNow - cached.At < TimeSpan.FromHours(1))
            return (cached.Info, null);

        try
        {
            var url = root.Contains("/.well-known/", StringComparison.OrdinalIgnoreCase)
                ? root : root + "/.well-known/openid-configuration";
            using var res = await Http.GetAsync(url);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (null, $"{(int)res.StatusCode} from {url}");

            using var doc = JsonDocument.Parse(text);
            var root2 = doc.RootElement;
            var info = new OidcEndpointsInfo(
                root2.GetProperty("authorization_endpoint").GetString()!,
                root2.GetProperty("token_endpoint").GetString()!,
                root2.TryGetProperty("userinfo_endpoint", out var ui) ? ui.GetString() : null,
                root2.TryGetProperty("end_session_endpoint", out var es) ? es.GetString() : null);
            _discovered = (root, info, DateTime.UtcNow);
            return (info, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // --- PKCE -------------------------------------------------------------------------------------

    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string AuthorizeUrl(OidcEndpointsInfo info, OidcConfig c, string redirectUri, string state, string verifier)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = c.ClientId ?? "",
            ["redirect_uri"] = redirectUri,
            ["scope"] = c.Scopes ?? "openid profile email",
            ["state"] = state,
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };
        return info.Authorization + (info.Authorization.Contains('?') ? "&" : "?") +
               string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Code for tokens. A public client sends no secret; a confidential one sends it here and nowhere else.</summary>
    public async Task<(string? accessToken, string? error)> ExchangeAsync(OidcEndpointsInfo info, OidcConfig c,
        string code, string verifier, string redirectUri)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = c.ClientId ?? "",
            ["code_verifier"] = verifier,
        };
        if (!string.IsNullOrWhiteSpace(c.ClientSecret)) form["client_secret"] = c.ClientSecret!;

        try
        {
            using var res = await Http.PostAsync(info.Token, new FormUrlEncodedContent(form));
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (null, $"the provider refused the code: {Short(text)}");
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("access_token", out var at) && at.GetString() is { Length: > 0 } token
                ? (token, null)
                : (null, "the provider returned no access token");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(Dictionary<string, JsonElement>? claims, string? error)> UserInfoAsync(OidcEndpointsInfo info, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(info.UserInfo)) return (null, "the provider has no userinfo endpoint");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.UserInfo);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await Http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (null, $"userinfo said {(int)res.StatusCode}: {Short(text)}");
            return (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text, Json), null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Which account these claims are. The username claim is configurable because every provider has
    /// its own idea of what a login name is; without one, the usual suspects are tried in order.
    /// </summary>
    public static string? Username(IReadOnlyDictionary<string, JsonElement> claims, string? preferred)
    {
        foreach (var name in new[] { preferred, "preferred_username", "email", "name", "sub" })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (claims.TryGetValue(name!, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
                return text.Trim();
        }
        return null;
    }

    /// <summary>Group names from a claim that may be a string, a space-separated string, or an array.</summary>
    public static List<string> Groups(IReadOnlyDictionary<string, JsonElement> claims, string? claimName)
    {
        if (string.IsNullOrWhiteSpace(claimName) || !claims.TryGetValue(claimName!, out var value)) return [];
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToList(),
            JsonValueKind.String => value.GetString()!
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            _ => [],
        };
    }

    public static bool IsAdmin(IReadOnlyDictionary<string, JsonElement> claims, OidcConfig c) =>
        !string.IsNullOrWhiteSpace(c.AdminGroup)
        && Groups(claims, c.GroupsClaim).Any(g =>
            g.Equals(c.AdminGroup!.Trim(), StringComparison.OrdinalIgnoreCase)
            // Keycloak writes realm roles as "/admins"; matching the leaf is what people expect.
            || g.TrimStart('/').Equals(c.AdminGroup!.Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase));

    private static string Short(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
