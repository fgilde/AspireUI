using System.Security.Claims;
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace AspireUI.Server.Endpoints;

/// <summary>
/// Single sign-on: start, come back, be signed in. Two browser redirects and one server-to-server
/// call, with the state and the PKCE verifier in a short-lived protected cookie so nothing has to be
/// remembered on the server between them.
/// </summary>
public static class OidcEndpoints
{
    private const string FlowCookie = "aspireui.sso";
    private static readonly User HasherUser = new("", "", "", false, "");

    public static void MapOidcEndpoints(this WebApplication app)
    {
        var users = app.Services.GetRequiredService<UserStore>();
        var settings = app.Services.GetRequiredService<SettingsStore>();
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");
        var oidc = new OidcService(settings, new SecretStore(dbPath, dataDir));
        var flow = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("aspireui.sso");
        var api = app.MapGroup("/api");

        // What the login page needs to know before anybody has logged in.
        api.MapGet("/auth/sso", () =>
        {
            var c = oidc.Config();
            return Results.Ok(new { enabled = c.Usable, label = c.Title });
        });

        api.MapGet("/auth/sso/start", async (HttpContext ctx, string? next) =>
        {
            var c = oidc.Config();
            if (!c.Usable) return Results.BadRequest(new { message = "single sign-on is not configured" });
            var (info, error) = await oidc.DiscoverAsync();
            if (info is null) return Results.BadRequest(new { message = error ?? "the provider could not be reached" });

            var state = Guid.NewGuid().ToString("n");
            var verifier = OidcService.NewVerifier();
            // The flow's own state travels in a cookie, protected and short-lived: the server keeps
            // nothing, and a reply that did not start here cannot be replayed.
            ctx.Response.Cookies.Append(FlowCookie, flow.Protect($"{state}|{verifier}|{next ?? "/"}"), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = ctx.Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/",
            });
            return Results.Redirect(oidc.AuthorizeUrl(info, c, RedirectUri(ctx), state, verifier));
        });

        api.MapGet("/auth/sso/callback", async (HttpContext ctx, string? code, string? state, string? error) =>
        {
            if (!string.IsNullOrWhiteSpace(error)) return Fail(ctx, error!);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state)) return Fail(ctx, "the provider sent no code");

            if (!ctx.Request.Cookies.TryGetValue(FlowCookie, out var sealedFlow)) return Fail(ctx, "that sign-in took too long");
            ctx.Response.Cookies.Delete(FlowCookie);
            string[] parts;
            try { parts = flow.Unprotect(sealedFlow).Split('|'); }
            catch { return Fail(ctx, "that sign-in did not start here"); }
            if (parts.Length != 3 || parts[0] != state) return Fail(ctx, "that sign-in did not start here");

            var c = oidc.Config();
            if (!c.Usable) return Fail(ctx, "single sign-on is not configured");
            var (info, discoverError) = await oidc.DiscoverAsync();
            if (info is null) return Fail(ctx, discoverError ?? "the provider could not be reached");

            var (token, exchangeError) = await oidc.ExchangeAsync(info, c, code!, parts[1], RedirectUri(ctx));
            if (token is null) return Fail(ctx, exchangeError ?? "the code could not be exchanged");

            var (claims, claimsError) = await oidc.UserInfoAsync(info, token);
            if (claims is null) return Fail(ctx, claimsError ?? "the provider returned no claims");

            var username = OidcService.Username(claims, c.UsernameClaim);
            if (string.IsNullOrWhiteSpace(username)) return Fail(ctx, "the provider sent no username");

            var admin = OidcService.IsAdmin(claims, c);
            var user = users.FindByUsername(username!);
            if (user is null)
            {
                if (!c.AutoCreate) return Fail(ctx, $"{username} has no account here");
                // A password nobody knows: this account is the provider's to authenticate, not ours.
                var hash = new PasswordHasher<User>().HashPassword(HasherUser,
                    Guid.NewGuid().ToString("n") + Guid.NewGuid().ToString("n"));
                user = users.Create(username!, hash, admin);
                if (!admin)
                    users.SetPermissions(user.Id, SeedParse.Perms(c.DefaultPermissions ?? "default"));
            }
            else if (user.Disabled)
            {
                return Fail(ctx, "that account is disabled");
            }
            else if (!string.IsNullOrWhiteSpace(c.AdminGroup) && user.IsAdmin != admin)
            {
                // With an admin group configured, the provider owns that decision on every sign-in.
                users.SetAdmin(user.Id, admin);
                user = users.Get(user.Id)!;
            }

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.Username),
                .. user.IsAdmin ? new[] { new Claim(ClaimTypes.Role, "Admin") } : [],
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            var next = parts[2].StartsWith('/') ? parts[2] : "/";
            return Results.Redirect(next);
        });

        // Configuration lives with the other settings and needs the same permission.
        api.MapGet("/settings/sso", () =>
        {
            var c = oidc.Config();
            return Results.Ok(new
            {
                c.Enabled, c.Authority, c.ClientId, c.Scopes, c.Label, c.UsernameClaim, c.GroupsClaim,
                c.AdminGroup, c.AutoCreate, c.DefaultPermissions,
                hasClientSecret = !string.IsNullOrEmpty(c.ClientSecret),
                redirectUri = "/api/auth/sso/callback",
            });
        }).RequireAuthorization().RequirePerm(Perm.Settings);

        api.MapPut("/settings/sso", (OidcConfig body) =>
        {
            oidc.Save(body);
            return Results.NoContent();
        }).RequireAuthorization().RequirePerm(Perm.Settings);

        api.MapPost("/settings/sso/test", async (OidcConfig body) =>
        {
            var (info, error) = await oidc.DiscoverAsync(body.Authority);
            return info is null
                ? Results.BadRequest(new { message = error })
                : Results.Ok(new { info.Authorization, info.Token, info.UserInfo, hasUserInfo = info.UserInfo is not null });
        }).RequireAuthorization().RequirePerm(Perm.Settings);
    }

    // The provider checks this against what is registered, so it has to be the address the browser
    // actually used — not a configured one that may be right for someone else.
    private static string RedirectUri(HttpContext ctx) =>
        $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/auth/sso/callback";

    // A failure is a person in a browser, not an api client: send them back to the login page with
    // something to read.
    private static IResult Fail(HttpContext ctx, string message) =>
        Results.Redirect("/login?ssoError=" + Uri.EscapeDataString(message));
}
