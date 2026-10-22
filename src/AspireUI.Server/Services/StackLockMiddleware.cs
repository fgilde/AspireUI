namespace AspireUI.Server.Services;

public class StackLockMiddleware(RequestDelegate next, StackStore stacks, DeploymentStore deployments)
{
    private static readonly string[] Allowed = ["unlock", "lock", "category", "duplicate"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (LockedStack(ctx) is { } s)
        {
            ctx.Response.StatusCode = StatusCodes.Status423Locked;
            await ctx.Response.WriteAsJsonAsync(new { message = $"\"{s.Name}\" is locked by {s.LockedBy} — unlock it first" });
            return;
        }
        await next(ctx);
    }

    private Models.StackModel? LockedStack(HttpContext ctx)
    {
        var parts = (ctx.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "api") return null;
        var terminal = parts.Length > 3 && parts[3] == "pty";
        if (!terminal && (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)
            || HttpMethods.IsOptions(ctx.Request.Method))) return null;
        if (parts.Length == 4 && Allowed.Contains(parts[3])) return null;
        var stackId = parts[1] switch
        {
            "stacks" => parts[2],
            "hosting" => deployments.Get(parts[2])?.StackId,
            _ => null,
        };
        return stackId is null ? null : stacks.Get(stackId) is { LockedBy: not null } s ? s : null;
    }
}
