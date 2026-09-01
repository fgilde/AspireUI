using System.Text.Json;
using AspireUI.Server.Services;
using YamlDotNet.Serialization;

namespace AspireUI.Server.Tests;

// The packages that put AspireUI itself into other app stores. They are hand-written files nobody
// compiles, so these tests are the only thing between a typo and a rejected store submission.
public class StoreListingTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "AspireUI.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static Dictionary<object, object> Yaml(string path) =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(File.ReadAllText(path));

    private static Dictionary<object, object> Map(object? o) => (Dictionary<object, object>)o!;
    private static List<object> Seq(object? o) => (List<object>)o!;

    [Fact]
    public void The_casaos_package_carries_what_that_store_reads()
    {
        var file = Path.Combine(RepoRoot(), "store", "casaos", "Apps", "AspireUI", "docker-compose.yml");
        var root = Yaml(file);
        Assert.Equal("aspireui", root["name"]?.ToString());

        var meta = Map(root["x-casaos"]);
        var main = meta["main"]!.ToString()!;
        var svc = Map(Map(root["services"])[main]);
        Assert.StartsWith("ghcr.io/fgilde/aspireui", svc["image"]!.ToString());

        // The published port is the one CasaOS opens, so it has to match port_map.
        var port = Map(Seq(svc["ports"])[0]);
        Assert.Equal("8080", port["target"]!.ToString());
        Assert.Equal(meta["port_map"]!.ToString(), port["published"]!.ToString());

        var targets = Seq(svc["volumes"]).Select(v => Map(v)["target"]!.ToString()).ToList();
        Assert.Contains("/data", targets);
        Assert.Contains("/var/run/docker.sock", targets);   // the socket is what this app is for

        Assert.Equal(["amd64", "arm64"], Seq(meta["architectures"]).Select(a => a!.ToString()).ToList());
        foreach (var key in new[] { "id", "author", "category", "icon", "thumbnail", "index", "scheme", "website", "repo", "support" })
            Assert.False(string.IsNullOrWhiteSpace(meta[key]?.ToString()), $"x-casaos.{key} is empty");
        Assert.False(string.IsNullOrWhiteSpace(Map(meta["title"])["en_US"]?.ToString()));
        Assert.False(string.IsNullOrWhiteSpace(Map(meta["tagline"])["en_US"]?.ToString()));
        Assert.Contains("Docker socket", Map(meta["description"])["en_US"]!.ToString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, Seq(meta["screenshot_link"]).Count);

        var perService = Map(svc["x-casaos"]);
        Assert.NotEmpty(Seq(perService["ports"]));
        Assert.NotEmpty(Seq(perService["volumes"]));
    }

    [Fact]
    public void The_casaos_source_has_the_index_files_that_store_expects()
    {
        var dir = Path.Combine(RepoRoot(), "store", "casaos");
        foreach (var name in new[] { "category-list.json", "featured-apps.json", "recommend-list.json", "README.md" })
            Assert.True(File.Exists(Path.Combine(dir, name)), $"{name} is missing");

        using var featured = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "featured-apps.json")));
        var ids = featured.RootElement.EnumerateArray().Select(e => e.GetProperty("appid").GetString()).ToList();
        // The id is the compose project name, or the store lists an app it cannot resolve.
        Assert.Contains("aspireui", ids);
    }

    [Fact]
    public void The_cosmos_servapp_is_valid_once_its_conditionals_are_resolved()
    {
        var dir = Path.Combine(RepoRoot(), "store", "cosmos", "servapps", "AspireUI");
        var compose = StripConditionals(File.ReadAllText(Path.Combine(dir, "cosmos-compose.json")));
        using var doc = JsonDocument.Parse(compose);

        var svc = doc.RootElement.GetProperty("services").GetProperty("{ServiceName}");
        Assert.StartsWith("ghcr.io/fgilde/aspireui", svc.GetProperty("image").GetString());
        Assert.Equal("unless-stopped", svc.GetProperty("restart").GetString());

        var route = svc.GetProperty("routes")[0];
        Assert.Equal("http://{ServiceName}:8080", route.GetProperty("target").GetString());
        Assert.Equal("SERVAPP", route.GetProperty("mode").GetString());

        var volumes = svc.GetProperty("volumes").EnumerateArray()
            .ToDictionary(v => v.GetProperty("target").GetString()!, v => v.GetProperty("type").GetString());
        Assert.Equal("volume", volumes["/data"]);
        Assert.Equal("bind", volumes["/var/run/docker.sock"]);

        // The installer form is filled in before install; both fields have to reach the container.
        var form = doc.RootElement.GetProperty("cosmos-installer").GetProperty("form");
        var names = form.EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Contains("adminUser", names);
        Assert.Contains("adminPassword", names);
    }

    [Fact]
    public void The_cosmos_servapp_has_its_description_and_images()
    {
        var dir = Path.Combine(RepoRoot(), "store", "cosmos", "servapps", "AspireUI");
        using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "description.json")));
        Assert.Equal("AspireUI", d.RootElement.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(d.RootElement.GetProperty("description").GetString()));
        Assert.Contains("<p>", d.RootElement.GetProperty("longDescription").GetString());
        var arch = d.RootElement.GetProperty("supported_architectures").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("amd64", arch);
        Assert.Contains("arm64", arch);
        Assert.NotEmpty(d.RootElement.GetProperty("tags").EnumerateArray());

        Assert.True(File.Exists(Path.Combine(dir, "icon.png")));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(dir, "screenshots"), "*.png").Length);
    }

    // Cosmos resolves {if Context.x} … {/if} before parsing; a false condition drops the block.
    private static string StripConditionals(string text)
    {
        var keep = new List<string>();
        var skipping = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("{if ")) { skipping = true; continue; }
            if (t == "{/if}") { skipping = false; continue; }
            if (!skipping) keep.Add(line);
        }
        return string.Join("\n", keep);
    }

    [Fact]
    public void Bitwarden_is_the_official_server_with_its_database()
    {
        var p = Assert.Single(new CatalogService().GetPresets(), x => x.Id == "bitwarden");
        Assert.Equal("ghcr.io/bitwarden/lite:latest", p.Image);
        Assert.Equal(8080, p.Port);

        // Installation id and key come from bitwarden.com/host; the key is a secret.
        Assert.Contains(p.Params!, x => x.Env == "BW_INSTALLATION_ID");
        Assert.True(Assert.Single(p.Params!, x => x.Env == "BW_INSTALLATION_KEY").Secret);
        Assert.Contains(p.Params!, x => x.Env == "BW_DOMAIN");

        var db = Assert.Single(p.Companions!);
        Assert.StartsWith("mariadb", db.Image);
        Assert.Equal("db", db.Key);
        Assert.Contains(p.Env!, e => e[0] == "BW_DB_SERVER" && e[1] == "${db}");

        // /etc/bitwarden holds the config, the keys and the vault database.
        Assert.Contains("/etc/bitwarden", (p.Volumes ?? new()).Select(v => v[1]));

        var (nodes, edges) = PresetBuilder.Build(p);
        Assert.Single(edges);
        Assert.Contains(nodes, n => n.AddArgs.Contains("\"mariadb:11\""));
    }
}
