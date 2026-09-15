using AspireUI.Server.Services;

// One store entry, several places to pull it from: the official image and a fork. The app is the
// same — same port, same volumes, same env — only the image changes, so a source is exactly that.
public class PresetSourcesTests
{
    private static ContainerPreset TwoSources() => new("thing", "Thing", "Tools", "ghcr.io/upstream/thing:latest", 8080,
        null, null, null, null, null,
        Github: "https://github.com/upstream/thing",
        Sources:
        [
            new PresetSource("official", "Official", "ghcr.io/upstream/thing:latest", "https://github.com/upstream/thing", Default: true),
            new PresetSource("fork", "A fork", "ghcr.io/someone/thing:latest", "https://github.com/someone/thing", "Adds x."),
        ]);

    [Fact]
    public void Every_apps_sources_are_consistent()
    {
        foreach (var p in new CatalogService().GetPresets().Where(p => p.Sources is { Count: > 0 }))
        {
            var sources = p.Sources!;
            Assert.Equal(sources.Count, sources.Select(s => s.Id.ToLowerInvariant()).Distinct().Count());
            var def = Assert.Single(sources, s => s.Default);
            Assert.Equal(p.Image, def.Image);
            Assert.All(sources, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
            Assert.All(sources, s => Assert.True(s.Image.LastIndexOf(':') > s.Image.LastIndexOf('/'), $"{p.Id}/{s.Id}: image has no tag"));
        }
    }

    [Fact]
    public void Installing_from_a_source_swaps_the_image_and_nothing_else()
    {
        var p = TwoSources();
        Assert.Same(p, PresetBuilder.ForSource(p, null));
        Assert.Null(PresetBuilder.ForSource(p, "nope"));

        var fork = PresetBuilder.ForSource(p, "fork")!;
        Assert.Equal("ghcr.io/someone/thing:latest", fork.Image);
        Assert.Equal("https://github.com/someone/thing", fork.Github);
        Assert.Equal(p.Port, fork.Port);

        var (nodes, _) = PresetBuilder.Build(fork);
        var main = nodes.Single(n => n.ResourceName == "thing");
        Assert.Equal("AddContainer", main.AddMethod);
        Assert.Contains("\"ghcr.io/someone/thing:latest\"", main.AddArgs);
    }

    [Fact]
    public void The_seed_short_form_names_the_source()
    {
        var apps = SeedParse.Apps("metube@fgilde=My Tube; gitea");
        Assert.Equal(2, apps.Count);
        Assert.Equal(("metube", "My Tube", "fgilde"), (apps[0].Id, apps[0].Name, apps[0].Source));
        Assert.Equal(("gitea", null, null), (apps[1].Id, apps[1].Name, apps[1].Source));
    }

    [Fact]
    public void MeTube_offers_the_fork()
    {
        var p = Assert.Single(new CatalogService().GetPresets(), x => x.Id == "metube");
        Assert.Equal(["official", "fgilde"], p.Sources!.Select(s => s.Id));
        Assert.Equal("ghcr.io/fgilde/metube:latest", PresetBuilder.ForSource(p, "fgilde")!.Image);
    }
}

// An app id becomes an Aspire resource name, and Aspire only takes one that starts with an ASCII
// letter — a digit gets through the store and then fails the build on deploy, far from the cause.
public class PresetIdTests
{
    [Fact]
    public void Every_apps_id_starts_with_a_letter()
    {
        foreach (var p in new CatalogService().GetPresets())
            Assert.Matches("^[a-z][a-z0-9-]*$", p.Id);
    }

    [Fact]
    public void A_manifest_whose_id_starts_with_a_digit_is_refused_with_a_reason()
    {
        var (apps, error) = ManifestImporter.Parse("""
            { "id": "9router", "label": "9Router", "group": "AI Apps", "image": "decolua/9router:latest", "port": 20128 }
            """);
        Assert.Empty(apps);
        Assert.Contains("must start with a letter", error);
    }
}
