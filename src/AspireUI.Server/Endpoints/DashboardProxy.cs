using System.Net;
using System.Text;
using AspireUI.Server.Services;
using Yarp.ReverseProxy.Forwarder;

namespace AspireUI.Server.Endpoints;

// Same-origin reverse proxy for the running stack's Aspire dashboard. The dashboard sends
// X-Frame-Options/CSP that block cross-origin embedding; proxying it under /dash/{id}/ (our origin)
// and stripping those headers lets it render inside the Dashboard panel's iframe. WebSockets (Blazor
// SignalR) are forwarded; the HTML document's <base href> is rewritten so its root-absolute asset
// URLs resolve under the proxy subpath.
public static class DashboardProxy
{
    public static void MapDashboardProxy(this WebApplication app)
    {
        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        var run = app.Services.GetRequiredService<RunService>();
        var client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        });
        var config = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };

        app.Map("/dash/{id}/{**rest}", async (HttpContext ctx, string id) =>
        {
            var baseUrl = run.DashboardBase(id);
            if (baseUrl is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            var rest = ctx.Request.RouteValues["rest"] as string ?? "";
            var transformer = new DashTransformer(id, rest);

            // WebSocket upgrades (Blazor _blazor hub) can't be buffered — forward directly.
            if (ctx.WebSockets.IsWebSocketRequest)
            {
                await forwarder.SendAsync(ctx, baseUrl, client, config, transformer);
                return;
            }

            // Buffer so an HTML document can be base-href-rewritten before it goes to the client.
            var original = ctx.Response.Body;
            using var buffer = new MemoryStream();
            ctx.Response.Body = buffer;
            await forwarder.SendAsync(ctx, baseUrl, client, config, transformer);
            ctx.Response.Body = original;
            buffer.Position = 0;

            if ((ctx.Response.ContentType ?? "").Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                var html = await new StreamReader(buffer).ReadToEndAsync();
                html = html.Replace("<base href=\"/\"", $"<base href=\"/dash/{id}/\"")
                           .Replace("<base href='/'", $"<base href='/dash/{id}/'");
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentLength = bytes.Length;
                await original.WriteAsync(bytes);
            }
            else
            {
                await buffer.CopyToAsync(original);
            }
        }).RequireAuthorization();
    }

    private sealed class DashTransformer(string id, string rest) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext ctx, HttpRequestMessage req, string destinationPrefix, CancellationToken ct)
        {
            await base.TransformRequestAsync(ctx, req, destinationPrefix, ct);
            var query = ctx.Request.QueryString.Value ?? "";
            req.RequestUri = new Uri($"{destinationPrefix.TrimEnd('/')}/{rest}{query}");
            req.Headers.Host = null; // derive Host from the destination URI
            _ = id;
        }

        public override ValueTask<bool> TransformResponseAsync(HttpContext ctx, HttpResponseMessage? resp, CancellationToken ct)
        {
            var result = base.TransformResponseAsync(ctx, resp, ct);
            // Strip the framing guards so the iframe can render it.
            ctx.Response.Headers.Remove("X-Frame-Options");
            ctx.Response.Headers.Remove("Content-Security-Policy");
            ctx.Response.Headers.Remove("Content-Security-Policy-Report-Only");
            return result;
        }
    }
}
