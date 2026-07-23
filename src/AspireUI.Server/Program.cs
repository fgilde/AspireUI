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
        // Cookies ignore the port, so two AspireUI instances on localhost (e.g. one running the other
        // as a resource) would clobber each other's session with a shared cookie name. Make the name
        // overridable per instance (the AddAspireUI extension sets ASPIREUI_COOKIE_NAME) so both stay
        // logged in independently.
        o.Cookie.Name = Environment.GetEnvironmentVariable("ASPIREUI_COOKIE_NAME") ?? "aspireui.auth";
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
// Serve the built SPA. Content-hashed assets (index-<hash>.js) may cache forever, but index.html must
// NOT be cached — otherwise the browser keeps loading the OLD hashed bundle after a redeploy and never
// picks up new code. So: long cache for /assets, no-cache for index.html.
Action<Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext> cacheHeaders = ctx =>
{
    if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
        ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
};
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = cacheHeaders });
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();     // /auth/*, /env/health — anonymous
app.MapStackEndpoints();    // app endpoints — require auth (gated inside)
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = cacheHeaders });

app.Run();

public partial class Program { } // expose for WebApplicationFactory
