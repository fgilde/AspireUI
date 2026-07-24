using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AspireUI.Server.Services;

// Authenticates `Authorization: Bearer <personal-access-token>` requests as the token's owning user
// (same claims a cookie login issues). Used by the REST API + MCP so agents/tools can call AspireUI.
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ApiTokenStore tokens, UserStore users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var token = auth["Bearer ".Length..].Trim();
        if (tokens.ResolveUserId(token) is not { } userId || users.Get(userId) is not { } u || u.Disabled)
            return Task.FromResult(AuthenticateResult.Fail("invalid or revoked token"));
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, u.Id), new(ClaimTypes.Name, u.Username) };
        if (u.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
}
