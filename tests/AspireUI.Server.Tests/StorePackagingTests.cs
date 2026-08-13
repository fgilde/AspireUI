using System.Xml.Linq;
using AspireUI.Server.Services;

public class StorePackagingTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "AspireUI.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Theory]
    [InlineData("yuvomi", "ghcr.io/ulsklyc/yuvomi:latest", 3000, "SESSION_SECRET")]
    [InlineData("lx-family-planner", "ghcr.io/laxxx-lab/lx-family-planner:latest", 3001, "APP_SECRET")]
    [InlineData("videola", "ghcr.io/fgilde/videola:latest", 7331, "VIDEOLA_TOKEN")]
    public void Secret_backed_app_presets_are_installable(string id, string image, int port, string secretEnv)
    {
        var p = Assert.Single(new CatalogService().GetPresets(), x => x.Id == id);
        Assert.Equal(image, p.Image);
        Assert.Equal(port, p.Port);
        Assert.NotEmpty(p.Volumes ?? new());
        var secret = Assert.Single(p.Params ?? new(), x => x.Env == secretEnv);
        Assert.True(secret.Secret);

        var (nodes, _) = PresetBuilder.Build(p);
        var main = nodes[0];
        Assert.Equal("AddContainer", main.AddMethod);
        Assert.Contains(main.WithCalls, w => w.Method == "WithHttpEndpoint" && w.Args.Any(a => a.Contains(port.ToString())));
        Assert.Contains(main.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0].Contains(secretEnv));
    }

    [Fact]
    public void Empty_secret_param_gets_a_random_value()
    {
        var p = new PresetParam("secret", "APP_SECRET", "", Secret: true);
        var a = PresetBuilder.ParamDefault(p);
        var b = PresetBuilder.ParamDefault(p);
        Assert.True(a.Length >= 32);
        Assert.NotEqual(a, b);
        Assert.Equal("fixed", PresetBuilder.ParamDefault(p with { Default = "fixed" }));
        Assert.Equal("", PresetBuilder.ParamDefault(p with { Secret = false }));
    }

    [Fact]
    public void Unraid_template_and_profile_are_valid()
    {
        var root = RepoRoot();
        var profile = XDocument.Load(Path.Combine(root, "ca_profile.xml"));
        Assert.Equal("CommunityApplications", profile.Root!.Name.LocalName);
        Assert.False(string.IsNullOrWhiteSpace(profile.Root.Element("Profile")?.Value));

        var t = XDocument.Load(Path.Combine(root, "templates", "aspireui.xml"));
        Assert.Equal("Container", t.Root!.Name.LocalName);
        Assert.Equal("ghcr.io/fgilde/aspireui:latest", t.Root.Element("Repository")!.Value);
        Assert.NotNull(t.Root.Element("Project"));
        Assert.Contains("[PORT:8080]", t.Root.Element("WebUI")!.Value);
        var cfg = t.Root.Elements("Config").ToList();
        Assert.Contains(cfg, c => (string?)c.Attribute("Type") == "Port" && (string?)c.Attribute("Target") == "8080");
        Assert.Contains(cfg, c => (string?)c.Attribute("Target") == "/data");
        Assert.Contains(cfg, c => (string?)c.Attribute("Target") == "/var/run/docker.sock");
        Assert.Contains(cfg, c => (string?)c.Attribute("Target") == "ASPIREUI_ADMIN_PASSWORD" && (string?)c.Attribute("Mask") == "true");
    }

    [Fact]
    public void Umbrel_package_matches_the_community_store_rules()
    {
        var root = RepoRoot();
        var store = File.ReadAllText(Path.Combine(root, "umbrel-app-store.yml"));
        Assert.Contains("id: \"fgilde\"", store);

        var appDir = Path.Combine(root, "fgilde-aspireui");
        var manifest = File.ReadAllLines(Path.Combine(appDir, "umbrel-app.yml"));
        Assert.Contains("id: fgilde-aspireui", manifest);            // app id must start with the store id
        Assert.Contains("port: 5158", manifest);
        Assert.Contains("path: \"\"", manifest);
        Assert.Contains("deterministicPassword: true", manifest);

        var compose = File.ReadAllText(Path.Combine(appDir, "docker-compose.yml"));
        Assert.Contains("APP_HOST: fgilde-aspireui_server_1", compose);
        Assert.Contains("APP_PORT: 8080", compose);
        Assert.Matches(@"image: ghcr\.io/fgilde/aspireui:\S+@sha256:[0-9a-f]{64}", compose);
        Assert.Contains("${APP_DATA_DIR}/data:/data", compose);
        Assert.Contains("ASPIREUI_ADMIN_PASSWORD: ${APP_PASSWORD}", compose);
    }
}
