using System.Text.Json;
using AspireUI.Server.Services;

public class AppSourceTests
{
    private static (AppSourceService svc, string cache) New()
    {
        var id = Guid.NewGuid().ToString("n");
        var db = Path.Combine(Path.GetTempPath(), $"aspireui-src-{id}.db");
        var cache = Path.Combine(Path.GetTempPath(), $"aspireui-src-{id}");
        return (new AppSourceService(new SettingsStore(db), cache), cache);
    }

    [Theory]
    [InlineData("", "https://example.com/apps.json", "name")]
    [InlineData("Acme", "", "URL")]
    [InlineData("Acme", "not-a-url", "URL")]
    [InlineData("Acme", "ftp://example.com/apps.json", "http")]
    [InlineData("Acme", "file:///etc/passwd", "http")]
    public void Only_http_urls_with_a_name_are_accepted(string name, string url, string expected)
        => Assert.Contains(expected, AppSourceService.Validate(name, url));

    [Fact]
    public void A_valid_source_passes_validation()
        => Assert.Null(AppSourceService.Validate("Acme", "https://example.com/apps.json"));

    [Fact]
    public void Sources_round_trip_and_reject_duplicates()
    {
        var (svc, _) = New();
        var (added, error) = svc.Add("Acme", "https://example.com/apps.json");
        Assert.Null(error);
        Assert.Equal("Acme", added!.Name);
        Assert.Single(svc.List());

        var (dup, dupError) = svc.Add("Acme again", " https://Example.com/apps.json ");
        Assert.Null(dup);
        Assert.Contains("already", dupError);

        Assert.True(svc.Remove(added.Id));
        Assert.Empty(svc.List());
        Assert.False(svc.Remove(added.Id));
    }

    [Fact]
    public void Apps_from_a_source_carry_its_provenance()
    {
        var source = new AppSource("abc", "Acme apps", "https://example.com/apps.json", "now");
        var apps = new List<ContainerPreset>
        {
            new("a", "A", "Tools", "acme/a:1", 80, null, null, null, null, null),
            new("b", "B", "Tools", "acme/b:1", 80, null, null, null, null, null) { Submitter = "someone else", Source = "https://other" },
        };
        var stamped = AppSourceService.Stamp(apps, source);
        Assert.Equal("Acme apps", stamped[0].Submitter);
        Assert.Equal("https://example.com/apps.json", stamped[0].Source);
        Assert.Equal("someone else", stamped[1].Submitter);      // an app's own provenance wins
        Assert.Equal("https://other", stamped[1].Source);
    }

    [Fact]
    public async Task A_dead_url_is_reported_and_caches_nothing()
    {
        var (svc, cache) = New();
        var (source, _) = svc.Add("Nope", "http://127.0.0.1:1/apps.json");
        var refreshed = await svc.RefreshAsync(source!);
        Assert.NotNull(refreshed.Error);
        Assert.Equal(0, refreshed.Apps);
        Assert.False(File.Exists(svc.CacheFile(source!.Id)));
        Assert.False(Directory.Exists(cache) && Directory.GetFiles(cache).Length > 0);
    }

    [Fact]
    public void The_cache_is_what_the_store_reads()
    {
        // A refreshed source is just a preset file in the cache dir — mirror that and let the
        // catalog read it, which is exactly how a source's apps reach the store after a restart.
        var cache = CatalogService.AppSourceCacheDir();
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, "zzz-source-test.json");
        var source = new AppSource("zzz", "Acme apps", "https://example.com/apps.json", "now");
        var app = new ContainerPreset("zzz-source-app", "Source App", "Tools", "acme/app:1", 8080, null, null, null, null, null);
        File.WriteAllText(file, JsonSerializer.Serialize(AppSourceService.Stamp([app], source), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        try
        {
            var served = Assert.Single(new CatalogService().GetPresets(), p => p.Id == "zzz-source-app");
            Assert.Equal("Acme apps", served.Submitter);
            Assert.Equal("acme/app:1", served.Image);
        }
        finally { File.Delete(file); }
    }
}
