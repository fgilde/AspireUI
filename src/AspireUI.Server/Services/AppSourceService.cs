using System.Text.Json;

namespace AspireUI.Server.Services;

// An app source is a URL that serves app manifests (one app or an array). Admins add them, refresh
// is manual, and every fetch is cached to disk so the store works offline and a source cannot change
// what is installable behind the user's back.
public record AppSource(string Id, string Name, string Url, string AddedAt, string? LastRefresh = null,
    int Apps = 0, string? Error = null);

public class AppSourceService(SettingsStore settings, string cacheDir)
{
    private const string Key = "AppSources";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const int MaxBytes = 2 * 1024 * 1024;

    public string CacheDir => cacheDir;

    public static string? Validate(string? name, string? url)
    {
        if (string.IsNullOrWhiteSpace(name)) return "give the source a name";
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)) return "the URL is not valid";
        if (u.Scheme is not ("http" or "https")) return "only http and https URLs are supported";
        return null;
    }

    public static string IdFor(string url) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant())))[..12].ToLowerInvariant();

    public List<AppSource> List() =>
        settings.GetValue(Key) is { } raw && !string.IsNullOrWhiteSpace(raw)
            ? JsonSerializer.Deserialize<List<AppSource>>(raw, Json) ?? new()
            : new();

    private void Save(List<AppSource> sources) => settings.SetValue(Key, JsonSerializer.Serialize(sources, Json));

    public (AppSource? source, string? error) Add(string name, string url)
    {
        if (Validate(name, url) is { } invalid) return (null, invalid);
        var trimmed = url.Trim();
        var sources = List();
        var id = IdFor(trimmed);
        if (sources.Any(s => s.Id == id)) return (null, "that URL is already a source");
        var source = new AppSource(id, name.Trim(), trimmed, DateTime.UtcNow.ToString("O"));
        sources.Add(source);
        Save(sources);
        return (source, null);
    }

    public bool Remove(string id)
    {
        var sources = List();
        var gone = sources.RemoveAll(s => s.Id == id) > 0;
        if (!gone) return false;
        Save(sources);
        try { File.Delete(CacheFile(id)); } catch { }
        return true;
    }

    public string CacheFile(string id) => Path.Combine(cacheDir, $"{id}.json");

    // Apps from a source always carry their provenance, so the store can show where they came from
    // even after a restart (the cache file is the only thing GetPresets reads).
    public static List<ContainerPreset> Stamp(IEnumerable<ContainerPreset> apps, AppSource source) =>
        apps.Select(a => a with
        {
            Submitter = string.IsNullOrWhiteSpace(a.Submitter) ? source.Name : a.Submitter,
            Source = string.IsNullOrWhiteSpace(a.Source) ? source.Url : a.Source,
        }).ToList();

    public async Task<AppSource> RefreshAsync(AppSource source)
    {
        var now = DateTime.UtcNow.ToString("O");
        try
        {
            using var res = await Http.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode) return source with { LastRefresh = now, Error = $"HTTP {(int)res.StatusCode}" };
            if (res.Content.Headers.ContentLength is > MaxBytes) return source with { LastRefresh = now, Error = "the response is larger than 2 MB" };
            var body = await res.Content.ReadAsStringAsync();
            if (body.Length > MaxBytes) return source with { LastRefresh = now, Error = "the response is larger than 2 MB" };

            var (apps, error) = ManifestImporter.Parse(body);
            if (error is not null) return source with { LastRefresh = now, Error = error };

            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(CacheFile(source.Id), JsonSerializer.Serialize(Stamp(apps, source), Json));
            return source with { LastRefresh = now, Apps = apps.Count, Error = null };
        }
        catch (Exception e) { return source with { LastRefresh = now, Error = e.Message }; }
    }

    public async Task<List<AppSource>> RefreshAllAsync()
    {
        var refreshed = new List<AppSource>();
        foreach (var s in List()) refreshed.Add(await RefreshAsync(s));
        Save(refreshed);
        return refreshed;
    }
}
