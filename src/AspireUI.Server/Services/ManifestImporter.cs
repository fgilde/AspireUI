using System.Text.Json;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// An app author's aspireui-app.json (one app object or an array of them) becomes a stack.
// It is the same JSON as a store preset, so the store, a submitted app and a repo manifest
// all go through PresetBuilder and behave identically.
public static class ManifestImporter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static (List<ContainerPreset> apps, string? error) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (new(), "the manifest is empty");
        try
        {
            var trimmed = json.TrimStart();
            var apps = trimmed.StartsWith('[')
                ? JsonSerializer.Deserialize<List<ContainerPreset>>(json, Json) ?? new()
                : JsonSerializer.Deserialize<ContainerPreset>(json, Json) is { } one ? new() { one } : new();
            var bad = apps.FirstOrDefault(a => string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Image) || a.Port <= 0);
            if (apps.Count == 0) return (new(), "the manifest contains no app");
            if (bad is not null) return (new(), $"app '{bad.Id}': id, image and port are required");
            return (apps, null);
        }
        catch (JsonException e) { return (new(), $"the manifest is not valid JSON: {e.Message}"); }
    }

    public static (StackModel? stack, string? error) ToStack(string stackId, string? name, string json)
    {
        var (apps, error) = Parse(json);
        if (error is not null) return (null, error);
        var app = apps[0];
        var (nodes, edges) = PresetBuilder.Build(app);
        var files = (app.Files ?? new()).Select(f => new ExtraFile(f.Name, f.Content)).ToList();
        var stackName = !string.IsNullOrWhiteSpace(name) ? name!
            : !string.IsNullOrWhiteSpace(app.Label) ? app.Label : app.Id;
        return (new StackModel(stackId, stackName, "net10.0", nodes, edges, new(), files, new(),
            HostingUrlPath: app.UrlPath), null);
    }
}
