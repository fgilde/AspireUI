using System.Collections.Concurrent;
using Aspire.DashboardService.Proto.V1;
using Grpc.Core;
using Grpc.Net.Client;
// `Resource` is ambiguous (proto message vs Aspire.Hosting.ApplicationModel.Resource pulled in by
// implicit usings) — alias the proto one.
using ProtoResource = Aspire.DashboardService.Proto.V1.Resource;

namespace AspireUI.Server.Services;

// One live resource as the running AppHost reports it over the aspire.v1.DashboardService gRPC
// "resource service" — the same feed the Aspire dashboard renders. Parent is the resource this one
// nests under (e.g. supabase-db -> supabase), derived from the "Parent" relationship.
public record LiveUrl(string? Name, string Url, bool IsInternal, bool IsInactive);
public record LiveResource(string Name, string DisplayName, string Type, string? State, string? StateStyle,
    string? Parent, List<LiveUrl> Urls, bool Hidden);

// Connects to a running stack's resource service and keeps an in-memory snapshot of its resources,
// updated from the WatchResources stream. Endpoint URL + api key are set deterministically by
// RunService when it launches the AppHost, so we can attach our own client.
public class ResourceGraphService : IDisposable
{
    static ResourceGraphService()
        // Allow gRPC over plaintext HTTP/2 (h2c): local runs use an http:// resource-service
        // endpoint (ASPIRE_ALLOW_UNSECURED_TRANSPORT), not TLS.
        => AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

    private const string ApiKeyHeader = "x-resource-service-api-key";

    private class Watch
    {
        public readonly ConcurrentDictionary<string, LiveResource> Resources = new();
        public CancellationTokenSource Cts = new();
        public GrpcChannel? Channel;
    }

    private readonly ConcurrentDictionary<string, Watch> _watches = new();

    public void Start(string id, string endpointUrl, string apiKey)
    {
        Stop(id); // replace any prior watch for this stack
        var w = new Watch();
        _watches[id] = w;
        _ = Task.Run(() => WatchLoop(w, endpointUrl, apiKey, w.Cts.Token));
    }

    public void Stop(string id)
    {
        if (_watches.TryRemove(id, out var w))
        {
            try { w.Cts.Cancel(); } catch { }
            try { w.Channel?.Dispose(); } catch { }
        }
    }

    public IReadOnlyList<LiveResource> GetResources(string id) =>
        _watches.TryGetValue(id, out var w) ? w.Resources.Values.ToList() : [];

    // Resilient watch: the AppHost's resource service isn't up the instant `dotnet run` starts, and
    // the stream can drop while the stack is still starting — so keep retrying until this watch is
    // stopped (stack stopped) rather than giving up on the first connection failure.
    private static async Task WatchLoop(Watch w, string endpointUrl, string apiKey, CancellationToken ct)
    {
        var headers = new Metadata { { ApiKeyHeader, apiKey } };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                w.Channel = GrpcChannel.ForAddress(endpointUrl);
                var client = new DashboardService.DashboardServiceClient(w.Channel);
                using var call = client.WatchResources(new WatchResourcesRequest(), headers, cancellationToken: ct);
                await foreach (var update in call.ResponseStream.ReadAllAsync(ct))
                    Apply(w, update);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // connection refused / stream error while starting — back off and retry
                try { await Task.Delay(1000, ct); } catch { return; }
            }
        }
    }

    private static void Apply(Watch w, WatchResourcesUpdate update)
    {
        if (update.KindCase == WatchResourcesUpdate.KindOneofCase.InitialData)
        {
            w.Resources.Clear();
            foreach (var r in update.InitialData.Resources) w.Resources[r.Name] = Map(r);
        }
        else if (update.KindCase == WatchResourcesUpdate.KindOneofCase.Changes)
        {
            foreach (var c in update.Changes.Value)
            {
                if (c.KindCase == WatchResourcesChange.KindOneofCase.Upsert) w.Resources[c.Upsert.Name] = Map(c.Upsert);
                else if (c.KindCase == WatchResourcesChange.KindOneofCase.Delete) w.Resources.TryRemove(c.Delete.ResourceName, out _);
            }
        }
    }

    // public for unit tests (mapping proto -> LiveResource, incl. parent-relationship extraction).
    public static LiveResource Map(ProtoResource r) => new(
        r.Name,
        r.DisplayName,
        r.ResourceType,
        r.HasState ? r.State : null,
        r.HasStateStyle ? r.StateStyle : null,
        r.Relationships.FirstOrDefault(rel => string.Equals(rel.Type, "Parent", StringComparison.OrdinalIgnoreCase))?.ResourceName,
        r.Urls.Select(u => new LiveUrl(u.HasEndpointName ? u.EndpointName : null, u.FullUrl, u.IsInternal, u.IsInactive)).ToList(),
        r.IsHidden);

    public void Dispose()
    {
        foreach (var id in _watches.Keys.ToList()) Stop(id);
    }
}
