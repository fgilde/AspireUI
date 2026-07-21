using AspireUI.Server.Endpoints;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Shared so the built-in dashboard panel and the stack endpoints see the same run state.
builder.Services.AddSingleton<ResourceGraphService>();
builder.Services.AddSingleton<RunService>(sp => new RunService(graph: sp.GetRequiredService<ResourceGraphService>()));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        // API-friendly: 401 for XHR instead of redirecting to an HTML login page.
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Env-driven first-run seeding (admin user + optional starter stack) — lets a container come up
// pre-configured without the manual setup wizard. No-op unless the ASPIREUI_* vars are set.
Seeder.Run();

app.UseDefaultFiles();
app.UseStaticFiles();       // serves built SPA from wwwroot (Task 10 copies it here)
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();     // /auth/*, /env/health — anonymous
app.MapStackEndpoints();    // app endpoints — require auth (gated inside)
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { } // expose for WebApplicationFactory
