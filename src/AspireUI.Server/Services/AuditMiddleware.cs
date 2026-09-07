using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing.Patterns;

namespace AspireUI.Server.Services;

/// <summary>
/// Writes the activity log. Every request under /api that changes something gets a row: who, what,
/// which app, whether it worked and how long it took.
/// <para>
/// It sits in the pipeline rather than in the endpoints on purpose — one place that cannot forget a
/// route, instead of a log call in eighty handlers that has to be remembered in the eighty-first.
/// The route template is the action, so the log stays readable without a table of strings to keep in
/// step with the routes.
/// </para>
/// </summary>
public class AuditMiddleware(RequestDelegate next, AuditStore audit, StackStore stacks,
    DeploymentStore deployments, SettingsStore settings)
{
    // Reading is not activity, and neither is asking the editor what a symbol means.
    private static readonly string[] Ignored =
    [
        "/api/auth/status", "/api/auth/login", "/api/auth/logout",
        "/api/stacks/{id}/code/complete", "/api/stacks/{id}/code/hover",
        "/api/stacks/{id}/code/signature", "/api/stacks/{id}/code/diagnostics",
        "/api/settings/test-ai", "/api/settings/ai-models", "/api/catalog/auto-preset",
        "/api/stacks/{id}/explain", "/api/stacks/{id}/assist", "/api/stacks/{id}/assist-code",
        "/api/git/inspect", "/api/git/branches", "/api/hosting/npm/test", "/api/hosting/notify/test",
        "/api/hosting/{id}/compose-services", "/api/stacks/{id}/hosting/check-updates",
        "/api/mcp",
    ];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!Wanted(ctx)) { await next(ctx); return; }

        var sw = Stopwatch.StartNew();
        try { await next(ctx); }
        finally
        {
            sw.Stop();
            try { Write(ctx, (int)sw.ElapsedMilliseconds); } catch { }
        }
    }

    private static bool Wanted(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api")) return false;
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)
            || HttpMethods.IsOptions(ctx.Request.Method)) return false;
        return !Ignored.Contains(Route(ctx), StringComparer.OrdinalIgnoreCase);
    }

    private static string Route(HttpContext ctx) =>
        ctx.GetEndpoint() is Microsoft.AspNetCore.Routing.RouteEndpoint { RoutePattern: RoutePattern p }
            ? "/" + p.RawText?.TrimStart('/')
            : ctx.Request.Path.Value ?? "";

    private void Write(HttpContext ctx, int ms)
    {
        var id = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var name = ctx.User.Identity?.Name;
        // An anonymous webhook is still somebody doing something; say so instead of leaving it blank.
        var who = !string.IsNullOrEmpty(name) ? name : id is null ? "webhook" : id;

        var route = Route(ctx);
        var (targetId, target) = Subject(ctx);
        audit.Add(id, who, ctx.Request.Method, route, Action(ctx.Request.Method, route), targetId, target,
            ctx.Response.StatusCode, ms);
        audit.PruneOccasionally(int.TryParse(settings.GetValue("AuditRetainDays"), out var d) ? d : 90);
    }

    /// <summary>
    /// Names what a request created, for the routes where the subject cannot be in the url because it
    /// does not exist yet. Everything else is read from the route.
    /// </summary>
    public static void Names(HttpContext ctx, string? id, string? name)
    {
        if (!string.IsNullOrEmpty(id)) ctx.Items["audit.id"] = id;
        if (!string.IsNullOrEmpty(name)) ctx.Items["audit.name"] = name;
    }

    /// <summary>The app or stack the request was about, by whichever id the route carries.</summary>
    private (string? Id, string? Name) Subject(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue("audit.id", out var created) && created is string createdId)
            return (createdId, ctx.Items.TryGetValue("audit.name", out var n) ? n as string : null);

        foreach (var key in new[] { "id", "stackId", "vol" })
        {
            if (ctx.Request.RouteValues.TryGetValue(key, out var raw) && raw?.ToString() is { Length: > 0 } value)
            {
                if (stacks.Get(value) is { } stack) return (value, stack.Name);
                if (deployments.Get(value) is { } dep) return (dep.StackId, dep.Name);
                return (value, null);
            }
        }
        return (null, null);
    }

    /// <summary>A short verb+noun for the list: <c>POST /api/stacks/{id}/hosting/deploy</c> reads as "hosting deploy".</summary>
    private static string Action(string method, string route)
    {
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "api" && !p.StartsWith('{')).ToList();
        var verb = method switch
        {
            "DELETE" => "delete",
            "PUT" or "PATCH" => "change",
            _ => "",
        };
        var noun = string.Join(" ", parts);
        return string.IsNullOrEmpty(verb) ? noun : $"{verb} {noun}".Trim();
    }
}
