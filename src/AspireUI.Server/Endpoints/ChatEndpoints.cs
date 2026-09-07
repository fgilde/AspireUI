using System.Security.Claims;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Endpoints;

/// <summary>
/// The in-app chat: sessions per account, and answers that may operate the instance through the
/// agent tools — with the asking user's permissions and nobody else's.
/// </summary>
public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");
        var chats = new ChatStore(dbPath);
        var users = app.Services.GetRequiredService<UserStore>();
        var settings = app.Services.GetRequiredService<SettingsStore>();

        var api = app.MapGroup("/api").RequireAuthorization();

        static string? Uid(HttpContext ctx) => ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // What the floating button needs to decide whether to show itself at all.
        api.MapGet("/chat/status", (HttpContext ctx) =>
        {
            var s = settings.Get();
            var user = Uid(ctx) is { } id ? users.Get(id) : null;
            return Results.Ok(new
            {
                configured = AgentChatService.Configured(s),
                tools = AgentChatService.CanUseTools(s),
                model = s.AiModel,
                provider = s.AiProviderLabel,
                // What this user could have done, so the panel can say it instead of the model.
                toolCount = AgentTools.SpecsFor(user).Count,
                totalTools = AgentTools.All.Count,
            });
        });

        api.MapGet("/chat/sessions", (HttpContext ctx) =>
            Uid(ctx) is { } id ? Results.Ok(chats.List(id)) : Results.Unauthorized());

        api.MapPost("/chat/sessions", (HttpContext ctx, NewChatRequest? body) =>
            Uid(ctx) is { } id ? Results.Ok(chats.Create(id, body?.Title)) : Results.Unauthorized());

        api.MapGet("/chat/sessions/{id}", (string id, HttpContext ctx) =>
        {
            if (Uid(ctx) is not { } uid) return Results.Unauthorized();
            return chats.Get(id, uid) is { } session
                ? Results.Ok(new { session, messages = chats.Messages(id) })
                : Results.NotFound();
        });

        api.MapDelete("/chat/sessions/{id}", (string id, HttpContext ctx) =>
        {
            if (Uid(ctx) is not { } uid) return Results.Unauthorized();
            return chats.Delete(id, uid) ? Results.NoContent() : Results.NotFound();
        });

        api.MapPut("/chat/sessions/{id}", (string id, NewChatRequest body, HttpContext ctx) =>
        {
            if (Uid(ctx) is not { } uid) return Results.Unauthorized();
            if (chats.Get(id, uid) is null) return Results.NotFound();
            chats.Rename(id, uid, body.Title ?? "New chat");
            return Results.NoContent();
        });

        // One turn. The tools run inside this request, so a deploy the model decides on happens
        // before the answer comes back — which is what makes the answer able to talk about it.
        api.MapPost("/chat/sessions/{id}/messages", async (string id, ChatAskRequest body, HttpContext ctx,
            McpTools agent, IChatClient chat, IToolChatClient toolChat, CancellationToken ct) =>
        {
            if (Uid(ctx) is not { } uid) return Results.Unauthorized();
            if (chats.Get(id, uid) is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Prompt)) return Results.BadRequest(new { message = "say something" });

            var service = new AgentChatService(chats, settings, toolChat, chat);
            try
            {
                var answer = await service.AskAsync(users.Get(uid), id, body.Prompt.Trim(), agent, ct);
                return Results.Ok(new { answer.Reply, answer.Tools, answer.ToolsAvailable });
            }
            catch (Exception ex)
            {
                // The model's own failures are the interesting ones: a wrong base url, a model that
                // does not exist, no key. Handing the text back beats a red 500 with nothing in it.
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    public record NewChatRequest(string? Title);
    public record ChatAskRequest(string Prompt);
}
