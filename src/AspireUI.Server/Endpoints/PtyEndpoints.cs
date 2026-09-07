using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Endpoints;

/// <summary>
/// The WebSocket half of the terminal: one shell per socket, bytes in both directions, and the same
/// permission the one-shot exec has. Text frames carry keystrokes; a frame that is a
/// <c>{"resize":{cols,rows}}</c> object is a window change rather than something to type.
/// </summary>
public static class PtyEndpoints
{
    public static void MapPtyEndpoints(this WebApplication app)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");
        var deployments = app.Services.GetRequiredService<DeploymentStore>();
        var secrets = new SecretStore(dbPath, dataDir);
        var targetStore = new TargetStore(dbPath);
        var wsRoot = Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(dataDir, "workspace");
        var targets = new TargetService(targetStore, secrets, wsRoot);
        var pty = new PtyService();

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/hosting/{id}/pty/available", () => Results.Ok(new { pty = PtyService.CanAllocatePty }))
            .RequirePerm(Perm.Terminal);

        api.MapGet("/hosting/{id}/pty", async (string id, string container, int? cols, int? rows, HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { message = "this endpoint speaks WebSocket" });
            if (deployments.Get(id) is not { } d) return Results.NotFound();
            // The container has to be one of this app's, exactly as for the one-shot terminal.
            if (!PtyService.Allowed(container, d.Project))
                return Results.BadRequest(new { message = "that container does not belong to this app" });

            var env = targets.EnvironmentFor(targetStore.Resolve(d.TargetId));
            var session = pty.Start(container, env, cols ?? 120, rows ?? 30);
            if (session is null) return Results.BadRequest(new { message = "could not start a shell in that container" });

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await PumpAsync(socket, session, ctx.RequestAborted);
            return Results.Empty;
        }).RequirePerm(Perm.Terminal);
    }

    private static async Task PumpAsync(WebSocket socket, PtyService.Session session, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var process = session.Process;

        // Container → browser. stdout and stderr are separate pipes even with a pty, so both are pumped.
        async Task Out(Stream stream)
        {
            var buffer = new byte[4096];
            while (!stop.IsCancellationRequested)
            {
                var n = await PtyService.ReadAvailableAsync(stream, buffer, stop.Token);
                if (n <= 0) break;
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(new ArraySegment<byte>(buffer, 0, n), WebSocketMessageType.Binary, true, stop.Token);
            }
        }

        var pumps = Task.WhenAll(
            Out(process.StandardOutput.BaseStream),
            Out(process.StandardError.BaseStream));

        // Browser → container.
        var input = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(input), stop.Token);
                if (received.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(input, 0, received.Count);

                // Only a real pty has a window to change; without one a resize is nothing to do.
                if (Resize(text) is { } size)
                {
                    if (session.HasTty)
                    {
                        await process.StandardInput.WriteAsync(PtyService.ResizeCommand(size.Cols, size.Rows));
                        await process.StandardInput.FlushAsync(stop.Token);
                    }
                    continue;
                }

                await process.StandardInput.WriteAsync(text);
                await process.StandardInput.FlushAsync(stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            stop.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await pumps.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shell ended", CancellationToken.None); } catch { }
        }
    }

    /// <summary>A resize message, or null when the frame is something the user typed.</summary>
    public static (int Cols, int Rows)? Resize(string frame)
    {
        if (!frame.StartsWith("{\"resize\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var size = doc.RootElement.GetProperty("resize");
            return (size.GetProperty("cols").GetInt32(), size.GetProperty("rows").GetInt32());
        }
        catch { return null; }
    }
}
