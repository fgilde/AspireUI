using AspireUI.Server.Services;

public class PresetMergeTests
{
    [Fact]
    public void GetPresets_merges_extra_presets_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-extra-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "extra.json"), """
            [ { "id": "zzz-community-app", "label": "Community App", "group": "Custom",
                "image": "acme/community:latest", "port": 9999, "tags": ["community"] } ]
            """);
        var prev = Environment.GetEnvironmentVariable("EXTRA_PRESETS_DIR");
        Environment.SetEnvironmentVariable("EXTRA_PRESETS_DIR", dir);
        try
        {
            var presets = new CatalogService().GetPresets();
            var added = Assert.Single(presets, p => p.Id == "zzz-community-app");
            Assert.Equal("acme/community:latest", added.Image);
            Assert.Contains("community", added.Tags ?? new());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXTRA_PRESETS_DIR", prev);
            Directory.Delete(dir, true);
        }
    }
}
