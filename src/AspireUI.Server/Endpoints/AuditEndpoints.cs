using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        var audit = app.Services.GetRequiredService<AuditStore>();
        var settings = app.Services.GetRequiredService<SettingsStore>();
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/audit", (int? limit, int? offset, string? q, string? user, string? stack) =>
            Results.Ok(new
            {
                total = audit.Count(),
                entries = audit.List(limit ?? 200, offset ?? 0, q, user, stack),
                retainDays = int.TryParse(settings.GetValue("AuditRetainDays"), out var d) ? d : 90,
            })).RequirePerm(Perm.Audit);

        api.MapPut("/audit/retention", (RetentionRequest b) =>
        {
            settings.SetValue("AuditRetainDays", Math.Clamp(b.Days, 0, 3650).ToString());
            return Results.NoContent();
        }).RequirePerm(Perm.Settings);

        // Everything older than the retention, now, instead of at the next couple of hundred requests.
        api.MapPost("/audit/prune", () =>
            Results.Ok(new { removed = audit.Prune(int.TryParse(settings.GetValue("AuditRetainDays"), out var d) ? d : 90) }))
            .RequirePerm(Perm.Settings);
    }

    public record RetentionRequest(int Days);
}
