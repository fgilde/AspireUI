using AspireUI.Server.Endpoints;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Shared so the dashboard reverse proxy and the stack endpoints see the same run state.
builder.Services.AddSingleton<RunService>();
builder.Services.AddHttpForwarder();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        // API-friendly: 401/403 instead of redirecting to an HTML login page.
        // API-friendly for XHR (JSON 401). But the dashboard iframe is a top-level browser
        // navigation — if the session lapsed, redirect it to the SPA login instead of a bare 401.
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/dash")) { ctx.Response.Redirect("/login"); return Task.CompletedTask; }
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();       // serves built SPA from wwwroot (Task 10 copies it here)
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();     // /auth/*, /env/health — anonymous
app.MapStackEndpoints();    // app endpoints — require auth (gated inside)
app.MapDashboardProxy();    // /dash/{id}/** — same-origin reverse proxy to the running dashboard
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { } // expose for WebApplicationFactory
