using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Aspire.DashboardService.Proto.V1;
using Grpc.Core;
using Grpc.Net.Client;
// `Resource` is ambiguous (proto message vs Aspire.Hosting.ApplicationModel.Resource pulled in by
// implicit usings) — alias the proto one.
using ProtoResource = Aspire.DashboardService.Proto.V1.Resource;
using ProtoCommandState = Aspire.DashboardService.Proto.V1.ResourceCommandState;

namespace AspireUI.Server.Services;

public record LiveUrl(string? Name, string Url, bool IsInternal, bool IsInactive);
public record LiveCommand(string Name, string DisplayName, bool Enabled, string? ConfirmationMessage, string? IconName);
public record LiveResource(string Name, string DisplayName, string Type, string? State, string? StateStyle,
    string? Parent, List<LiveUrl> Urls, bool Hidden, List<LiveCommand> Commands);

public class ResourceGraphService : IDisposable
{
    static ResourceGraphService()
        => AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

    private const string ApiKeyHeader = "x-resource-service-api-key";

    private class Watch
    {
        public readonly ConcurrentDictionary<string, LiveResource> Resources = new();
        public CancellationTokenSource Cts = new();
        public GrpcChannel? Channel;
        public string Endpoint = "";
        public string ApiKey = "";
    }

    private readonly ConcurrentDictionary<string, Watch> _watches = new();

    public void Start(string id, string endpointUrl, string apiKey)
    {
        Stop(id); // replace any prior watch for this stack
        var w = new Watch { Endpoint = endpointUrl, ApiKey = apiKey };
        _watches[id] = w;
        _ = Task.Run(() => WatchLoop(w, endpointUrl, apiKey, w.Cts.Token));
    }

    public async IAsyncEnumerable<ConsoleLogLine> StreamLogsAsync(
        string id, string resourceName, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_watches.TryGetValue(id, out var w)) yield break;
        using var channel = GrpcChannel.ForAddress(w.Endpoint);
        var client = new DashboardService.DashboardServiceClient(channel);
        var headers = new Metadata { { ApiKeyHeader, w.ApiKey } };
        using var call = client.WatchResourceConsoleLogs(
            new WatchResourceConsoleLogsRequest { ResourceName = resourceName }, headers, cancellationToken: ct);
        await foreach (var update in call.ResponseStream.ReadAllAsync(ct))
            foreach (var line in update.LogLines)
                yield return line;
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

    public static LiveResource Map(ProtoResource r) => new(
        r.Name,
        r.DisplayName,
        r.ResourceType,
        r.HasState ? r.State : null,
        r.HasStateStyle ? r.StateStyle : null,
        r.Relationships.FirstOrDefault(rel => string.Equals(rel.Type, "Parent", StringComparison.OrdinalIgnoreCase))?.ResourceName,
        r.Urls.Select(u => new LiveUrl(u.HasEndpointName ? u.EndpointName : null, u.FullUrl, u.IsInternal, u.IsInactive)).ToList(),
        r.IsHidden,
        r.Commands.Where(c => c.State != ProtoCommandState.Hidden)
            .Select(c => new LiveCommand(c.Name, c.DisplayName, c.State == ProtoCommandState.Enabled,
                c.HasConfirmationMessage ? c.ConfirmationMessage : null, c.HasIconName ? c.IconName : null)).ToList());

    public async Task<(bool ok, string? message)> ExecuteCommandAsync(
        string id, string resourceName, string resourceType, string commandName, CancellationToken ct)
    {
        if (!_watches.TryGetValue(id, out var w)) return (false, "stack not running");
        using var channel = GrpcChannel.ForAddress(w.Endpoint);
        var client = new DashboardService.DashboardServiceClient(channel);
        var headers = new Metadata { { ApiKeyHeader, w.ApiKey } };
        var resp = await client.ExecuteResourceCommandAsync(new ResourceCommandRequest
        {
            CommandName = commandName, ResourceName = resourceName, ResourceType = resourceType,
        }, headers, cancellationToken: ct);
        return (resp.Kind == ResourceCommandResponseKind.Succeeded, resp.HasMessage ? resp.Message : null);
    }

    public void Dispose()
    {
        foreach (var id in _watches.Keys.ToList()) Stop(id);
    }
}
