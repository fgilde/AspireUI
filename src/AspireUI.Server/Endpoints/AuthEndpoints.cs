using System.Security.Claims;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace AspireUI.Server.Endpoints;

public static class AuthEndpoints
{
    // PasswordHasher<T>'s default (v3) algorithm never reads the user instance — only the
    // password — so a placeholder is fine for hashing/verifying without a real User on hand yet.
    private static readonly User HasherUser = new("", "", "", false, "");

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        Directory.CreateDirectory(dataDir);
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");

        var store = new UserStore(dbPath);
        var hasher = new PasswordHasher<User>();
        var envHealth = new EnvHealth();

        static UserDto ToDto(User u) => new(u.Id, u.Username, u.IsAdmin, u.CreatedAt, u.Disabled, u.MustChangePassword);

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

        static IResult InvalidCredentials() =>
            Results.Json(new { message = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);

        app.MapGet("/auth/status", (HttpContext ctx) =>
        {
            var authenticated = ctx.User.Identity?.IsAuthenticated ?? false;
            UserDto? dto = null;
            if (authenticated)
            {
                var id = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (id is not null && store.Get(id) is { } u) dto = ToDto(u);
            }
            return Results.Ok(new { needsSetup = store.Count() == 0, authenticated, user = dto });
        });

        app.MapPost("/auth/setup", async (HttpContext ctx, AuthRequest body) =>
        {
            if (store.Count() > 0) return Results.Conflict(new { message = "setup already completed" });
            if (string.IsNullOrWhiteSpace(body.Username)) return Results.BadRequest(new { message = "username required" });
            if (body.Password.Length < 8) return Results.BadRequest(new { message = "password must be at least 8 characters" });

            var hash = hasher.HashPassword(HasherUser, body.Password);
            var user = store.Create(body.Username, hash, true);
            await SignInUserAsync(ctx, user);
            return Results.Ok(ToDto(user));
        });

        app.MapPost("/auth/login", async (HttpContext ctx, AuthRequest body) =>
        {
            var user = store.FindByUsername(body.Username);
            if (user is null) return InvalidCredentials();

            var result = hasher.VerifyHashedPassword(HasherUser, user.PasswordHash, body.Password);
            if (result == PasswordVerificationResult.Failed) return InvalidCredentials();
            if (user.Disabled) return Results.Json(new { message = "account is disabled" }, statusCode: StatusCodes.Status403Forbidden);

            await SignInUserAsync(ctx, user);
            return Results.Ok(ToDto(user));
        });

        // Self-service password change (any signed-in user). Verifies the current password; clears the
        // must-change flag on success.
        app.MapPost("/auth/change-password", async (HttpContext ctx, ChangePasswordRequest body) =>
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

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        app.MapGet("/env/health", async () =>
        {
            var r = await envHealth.CheckAsync();
            return Results.Ok(new
            {
                dotnet = new { ok = r.Dotnet.Ok, version = r.Dotnet.Detail },
                docker = new { ok = r.Docker.Ok, detail = r.Docker.Detail },
                git = new { ok = r.Git.Ok, detail = r.Git.Detail },
            });
        });

        // Admin-only user management. Cookie carries Role=Admin for admins (see SignInUserAsync),
        // so gating on that role claim is enough — no separate admin check needed per handler.
        var users = app.MapGroup("/users").RequireAuthorization(policy => policy.RequireRole("Admin"));

        users.MapGet("/", () => Results.Ok(store.List().Select(ToDto)));

        users.MapPost("/", (CreateUserRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Username)) return Results.BadRequest(new { message = "username required" });
            if (body.Password.Length < 8) return Results.BadRequest(new { message = "password must be at least 8 characters" });
            if (store.FindByUsername(body.Username) is not null) return Results.Conflict(new { message = "username already exists" });

            var hash = hasher.HashPassword(HasherUser, body.Password);
            var user = store.Create(body.Username, hash, body.IsAdmin);
            return Results.Ok(ToDto(user));
        });

        users.MapDelete("/{id}", (string id) =>
        {
            var user = store.Get(id);
            if (user is null) return Results.NotFound();
            // Last-admin guard: refuse if deleting this user would drop AdminCount to 0.
            if (user.IsAdmin && store.AdminCount() <= 1)
                return Results.BadRequest(new { message = "cannot delete the last admin" });
            store.Delete(id);
            return Results.NoContent();
        });

        // Admin: set another user's password (optionally forcing a change on their next login).
        users.MapPut("/{id}/password", (string id, SetPasswordRequest body) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            if (body.Password.Length < 8) return Results.BadRequest(new { message = "password must be at least 8 characters" });
            store.SetPassword(id, hasher.HashPassword(HasherUser, body.Password), body.MustChange);
            return Results.NoContent();
        });

        // Admin: enable/disable an account. Guard the last admin so nobody locks everyone out.
        users.MapPut("/{id}/disabled", (string id, SetDisabledRequest body) =>
        {
            var user = store.Get(id);
            if (user is null) return Results.NotFound();
            if (body.Disabled && user.IsAdmin && store.AdminCount() <= 1)
                return Results.BadRequest(new { message = "cannot disable the last admin" });
            store.SetDisabled(id, body.Disabled);
            return Results.NoContent();
        });
    }

    public record AuthRequest(string Username, string Password);
    public record CreateUserRequest(string Username, string Password, bool IsAdmin);
    public record ChangePasswordRequest(string OldPassword, string NewPassword);
    public record SetPasswordRequest(string Password, bool MustChange);
    public record SetDisabledRequest(bool Disabled);
}
