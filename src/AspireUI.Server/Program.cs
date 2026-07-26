using AspireUI.Server.Endpoints;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024); // folder/zip imports can be large

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");

builder.Services.AddSingleton<ResourceGraphService>();
builder.Services.AddSingleton<RunService>(sp => new RunService(graph: sp.GetRequiredService<ResourceGraphService>()));
builder.Services.AddSingleton(_ => new ApiTokenStore(dbPath));
builder.Services.AddSingleton(_ => new UserStore(dbPath));
builder.Services.AddSingleton(_ => new CatalogService());
builder.Services.AddMcpServer().WithHttpTransport().WithTools<McpTools>();
builder.Services.AddHostedService<BackupSchedulerService>();

builder.Services.AddAuthentication("smart")
    .AddPolicyScheme("smart", "smart", o => o.ForwardDefaultSelector = ctx =>
        ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? ApiKeyAuthenticationHandler.Scheme : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.Scheme, null)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
    {
        o.Cookie.Name = Environment.GetEnvironmentVariable("ASPIREUI_COOKIE_NAME") ?? "aspireui.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

var app = builder.Build();

Seeder.Run();

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("AspireUI API").WithTheme(ScalarTheme.Purple));
app.MapMcp("/api/mcp").RequireAuthorization();

app.UseDefaultFiles();
Action<Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext> cacheHeaders = ctx =>
{
    if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
        ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
};
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = cacheHeaders });
var mediaDir = Path.Combine(AppContext.BaseDirectory, "catalog", "media");
if (Directory.Exists(mediaDir))
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(mediaDir),
        RequestPath = "/media",
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=604800",
    });
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();
app.MapStackEndpoints();
app.MapMethods("/api/{**rest}", new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "PATCH" }, () => Results.NotFound());
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = cacheHeaders });

app.Run();

public partial class Program { } // expose for WebApplicationFactory
