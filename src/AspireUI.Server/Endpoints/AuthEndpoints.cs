using System.Security.Claims;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace AspireUI.Server.Endpoints;

public static class AuthEndpoints
{
    private static readonly User HasherUser = new("", "", "", false, "");

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        Directory.CreateDirectory(dataDir);
        var store = app.Services.GetRequiredService<UserStore>();
        // The half-finished login between password and code travels as a protected ticket rather than
        // as a session: nothing is signed in until the second factor is in.
        var tickets = app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("aspireui.2fa");
        var hasher = new PasswordHasher<User>();
        var envHealth = new EnvHealth();
        var api = app.MapGroup("/api");

        static UserDto ToDto(User u) => new(u.Id, u.Username, u.IsAdmin, u.CreatedAt, u.Disabled, u.MustChangePassword,
            u.ViewModes ?? new() { "full", "simple" },
            u.IsAdmin ? Perm.All.ToList() : u.Permissions ?? Perm.All.ToList(),
            u.TotpEnabled);

        static async Task SignInUserAsync(HttpContext ctx, User user)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id),
                new(ClaimTypes.Name, user.Username),
            };
            if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        }

        static List<string> ViewModesOf(List<string>? modes)
        {
            var m = (modes ?? new()).Where(x => x is "full" or "simple").Distinct().ToList();
            return m.Count == 0 ? new() { "full", "simple" } : m;
        }

        // A user manager can hand out only what they hold themselves, or granting permissions would be
        // a way to collect them.
        List<string> Grantable(HttpContext ctx, List<string>? wanted)
        {
            var actor = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } aid ? store.Get(aid) : null;
            return (wanted ?? new()).Where(Perm.All.Contains).Distinct()
                .Where(p => IsAdminActor(ctx) || Perm.Has(actor, p)).ToList();
        }

        static IResult InvalidCredentials() =>
            Results.Json(new { message = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);

        api.MapGet("/auth/status", async (HttpContext ctx) =>
        {
            var authenticated = ctx.User.Identity?.IsAuthenticated ?? false;
            UserDto? dto = null;
            if (authenticated)
            {
                var id = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (id is not null && store.Get(id) is { Disabled: false } u) dto = ToDto(u);
                else
                {
                    // Cookie without a live user (deleted, disabled, or a different database behind the
                    // same host): sign out instead of leaving a session with no user and no permissions.
                    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    authenticated = false;
                }
            }
            return Results.Ok(new { needsSetup = store.Count() == 0, authenticated, user = dto });
        });

        api.MapPost("/auth/setup", async (HttpContext ctx, AuthRequest body) =>
        {
            if (store.Count() > 0) return Results.Conflict(new { message = "setup already completed" });
            if (string.IsNullOrWhiteSpace(body.Username)) return Results.BadRequest(new { message = "username required" });
            if (body.Password.Length < 8) return Results.BadRequest(new { message = "password must be at least 8 characters" });

            var hash = hasher.HashPassword(HasherUser, body.Password);
            var user = store.Create(body.Username, hash, true);
            await SignInUserAsync(ctx, user);
            return Results.Ok(ToDto(user));
        });

        api.MapPost("/auth/login", async (HttpContext ctx, AuthRequest body) =>
        {
            var user = store.FindByUsername(body.Username);
            if (user is null) return InvalidCredentials();

            var result = hasher.VerifyHashedPassword(HasherUser, user.PasswordHash, body.Password);
            if (result == PasswordVerificationResult.Failed) return InvalidCredentials();
            if (user.Disabled) return Results.Json(new { message = "account is disabled" }, statusCode: StatusCodes.Status403Forbidden);

            if (user.TotpEnabled)
                return Results.Ok(new
                {
                    twoFactorRequired = true,
                    ticket = tickets.Protect($"{user.Id}|{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}"),
                });

            await SignInUserAsync(ctx, user);
            return Results.Ok(ToDto(user));
        });

        // Second half of a login: the ticket says which account passed the password, the code proves
        // the phone. A recovery code is accepted here too and is then spent.
        api.MapPost("/auth/login/2fa", async (HttpContext ctx, TwoFactorLoginRequest body) =>
        {
            string payload;
            try { payload = tickets.Unprotect(body.Ticket ?? ""); }
            catch { return InvalidCredentials(); }

            var parts = payload.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[1], out var expires)
                || DateTimeOffset.FromUnixTimeSeconds(expires) < DateTimeOffset.UtcNow)
                return Results.Json(new { message = "that took too long — sign in again" }, statusCode: StatusCodes.Status401Unauthorized);

            if (store.Get(parts[0]) is not { Disabled: false } user || !user.TotpEnabled) return InvalidCredentials();

            if (TotpService.Verify(user.TotpSecret, body.Code))
            {
                await SignInUserAsync(ctx, user);
                return Results.Ok(ToDto(user));
            }
            if (TotpService.UseRecoveryCode(user.RecoveryCodes ?? new(), body.Code, out var left))
            {
                store.SetTotp(user.Id, user.TotpSecret, true, left);
                await SignInUserAsync(ctx, user);
                return Results.Ok(new { user = ToDto(user), recoveryCodeUsed = true, recoveryCodesLeft = left.Count });
            }
            return Results.Json(new { message = "that code is not right" }, statusCode: StatusCodes.Status401Unauthorized);
        });

        api.MapPost("/auth/change-password", async (HttpContext ctx, ChangePasswordRequest body) =>
        {
            var id = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (id is null || store.Get(id) is not { } user) return Results.Unauthorized();
            if (hasher.VerifyHashedPassword(HasherUser, user.PasswordHash, body.OldPassword) == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { message = "current password is incorrect" });
            if (body.NewPassword.Length < 8) return Results.BadRequest(new { message = "password must be at least 8 characters" });
            store.SetPassword(id, hasher.HashPassword(HasherUser, body.NewPassword), mustChange: false);
            await Task.CompletedTask;
            return Results.NoContent();
        }).RequireAuthorization();

        // Enrolment is two steps on purpose: a secret nobody has proved yet must not lock anybody out.
        api.MapPost("/auth/2fa/setup", (HttpContext ctx) =>
        {
            if (ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is not { } id || store.Get(id) is not { } user)
                return Results.Unauthorized();
            if (user.TotpEnabled) return Results.Conflict(new { message = "two-factor is already on" });

            var secret = TotpService.NewSecret();
            store.SetTotp(user.Id, secret, enabled: false, recoveryCodes: null);
            return Results.Ok(new { secret, uri = TotpService.EnrolmentUri(secret, user.Username) });
        }).RequireAuthorization();

        api.MapPost("/auth/2fa/enable", (HttpContext ctx, TwoFactorCodeRequest body) =>
        {
            if (ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is not { } id || store.Get(id) is not { } user)
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(user.TotpSecret)) return Results.BadRequest(new { message = "start the setup first" });
            if (!TotpService.Verify(user.TotpSecret, body.Code))
                return Results.BadRequest(new { message = "that code is not right — check the clock on your phone" });

            var (plain, hashed) = TotpService.NewRecoveryCodes();
            store.SetTotp(user.Id, user.TotpSecret, enabled: true, recoveryCodes: hashed);
            // The only time these are ever readable.
            return Results.Ok(new { recoveryCodes = plain });
        }).RequireAuthorization();

        api.MapPost("/auth/2fa/disable", (HttpContext ctx, TwoFactorDisableRequest body) =>
        {
            if (ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is not { } id || store.Get(id) is not { } user)
                return Results.Unauthorized();
            // Turning it off needs the password again: a borrowed session must not be able to.
            if (hasher.VerifyHashedPassword(HasherUser, user.PasswordHash, body.Password ?? "") == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { message = "that password is not right" });

            store.SetTotp(user.Id, null, enabled: false, recoveryCodes: null);
            return Results.NoContent();
        }).RequireAuthorization();

        api.MapPost("/auth/2fa/recovery-codes", (HttpContext ctx, TwoFactorDisableRequest body) =>
        {
            if (ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is not { } id || store.Get(id) is not { } user)
                return Results.Unauthorized();
            if (!user.TotpEnabled) return Results.BadRequest(new { message = "two-factor is not on" });
            if (hasher.VerifyHashedPassword(HasherUser, user.PasswordHash, body.Password ?? "") == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { message = "that password is not right" });

            var (plain, hashed) = TotpService.NewRecoveryCodes();
            store.SetTotp(user.Id, user.TotpSecret, true, hashed);
            return Results.Ok(new { recoveryCodes = plain });
        }).RequireAuthorization();

        api.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        api.MapGet("/env/health", async () =>
        {
            var r = await envHealth.CheckAsync();
            return Results.Ok(new
            {
                dotnet = new { ok = r.Dotnet.Ok, version = r.Dotnet.Detail },
                docker = new { ok = r.Docker.Ok, detail = r.Docker.Detail },
                git = new { ok = r.Git.Ok, detail = r.Git.Detail },
            });
        });

        // Managing users is a permission of its own, but everything that could turn a user manager into
        // an admin stays with the admins: the admin flag, and anyone who already is an admin.
        var users = api.MapGroup("/users").RequirePerm(Perm.Users);
        bool IsAdminActor(HttpContext ctx) => ctx.User.IsInRole("Admin");
        IResult? AdminsOnly(HttpContext ctx, string id) =>
            !IsAdminActor(ctx) && store.Get(id) is { IsAdmin: true }
                ? Results.Json(new { message = "only an admin can change an admin account" }, statusCode: StatusCodes.Status403Forbidden)
                : null;

        users.MapGet("/", () => Results.Ok(store.List().Select(ToDto)));

        users.MapPost("/", (CreateUserRequest body, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(body.Username)) return Results.BadRequest(new { message = "username required" });
            if (body.Password.Length < 8) return Results.BadRequest(new { message = "password must be at least 8 characters" });
            if (store.FindByUsername(body.Username) is not null) return Results.Conflict(new { message = "username already exists" });
            if (body.IsAdmin && !IsAdminActor(ctx))
                return Results.Json(new { message = "only an admin can create an admin" }, statusCode: StatusCodes.Status403Forbidden);

            var hash = hasher.HashPassword(HasherUser, body.Password);
            var user = store.Create(body.Username, hash, body.IsAdmin);
            if (!body.IsAdmin)
                store.SetPermissions(user.Id, Grantable(ctx, body.Permissions ?? Perm.Default.ToList()));
            if (body.ViewModes is { Count: > 0 }) store.SetViewModes(user.Id, ViewModesOf(body.ViewModes));
            return Results.Ok(ToDto(store.Get(user.Id)!));
        });

        users.MapDelete("/{id}", (string id, HttpContext ctx) =>
        {
            var user = store.Get(id);
            if (user is null) return Results.NotFound();
            if (AdminsOnly(ctx, id) is { } denied) return denied;
            if (user.IsAdmin && store.AdminCount() <= 1)
                return Results.BadRequest(new { message = "cannot delete the last admin" });
            store.Delete(id);
            return Results.NoContent();
        });

        users.MapPut("/{id}/password", (string id, SetPasswordRequest body, HttpContext ctx) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            if (AdminsOnly(ctx, id) is { } denied) return denied;
            if (body.Password.Length < 8) return Results.BadRequest(new { message = "password must be at least 8 characters" });
            store.SetPassword(id, hasher.HashPassword(HasherUser, body.Password), body.MustChange);
            return Results.NoContent();
        });

        users.MapPut("/{id}/view-modes", (string id, SetViewModesRequest body, HttpContext ctx) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            if (AdminsOnly(ctx, id) is { } denied) return denied;
            store.SetViewModes(id, ViewModesOf(body.Modes));
            return Results.NoContent();
        });

        users.MapPut("/{id}/permissions", (string id, SetPermissionsRequest body, HttpContext ctx) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            if (AdminsOnly(ctx, id) is { } denied) return denied;
            store.SetPermissions(id, Grantable(ctx, body.Permissions));
            return Results.NoContent();
        });

        users.MapPut("/{id}/admin", (string id, SetAdminRequest body) =>
        {
            var user = store.Get(id);
            if (user is null) return Results.NotFound();
            if (!body.IsAdmin && user.IsAdmin && store.AdminCount() <= 1)
                return Results.BadRequest(new { message = "cannot demote the last admin" });
            store.SetAdmin(id, body.IsAdmin);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // The way back for somebody whose phone is gone and whose recovery codes are gone with it.
        users.MapDelete("/{id}/2fa", (string id, HttpContext ctx) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            if (AdminsOnly(ctx, id) is { } denied) return denied;
            store.SetTotp(id, null, enabled: false, recoveryCodes: null);
            return Results.NoContent();
        });

        users.MapPut("/{id}/disabled", (string id, SetDisabledRequest body, HttpContext ctx) =>
        {
            var user = store.Get(id);
            if (user is null) return Results.NotFound();
            if (AdminsOnly(ctx, id) is { } denied) return denied;
            if (body.Disabled && user.IsAdmin && store.AdminCount() <= 1)
                return Results.BadRequest(new { message = "cannot disable the last admin" });
            store.SetDisabled(id, body.Disabled);
            return Results.NoContent();
        });
    }

    public record AuthRequest(string Username, string Password);
    public record CreateUserRequest(string Username, string Password, bool IsAdmin,
        List<string>? Permissions = null, List<string>? ViewModes = null);
    public record ChangePasswordRequest(string OldPassword, string NewPassword);
    public record SetPasswordRequest(string Password, bool MustChange);
    public record SetDisabledRequest(bool Disabled);
    public record SetAdminRequest(bool IsAdmin);
    public record SetViewModesRequest(List<string>? Modes);
    public record SetPermissionsRequest(List<string>? Permissions);
    public record TwoFactorLoginRequest(string? Ticket, string? Code);
    public record TwoFactorCodeRequest(string? Code);
    public record TwoFactorDisableRequest(string? Password);
}
