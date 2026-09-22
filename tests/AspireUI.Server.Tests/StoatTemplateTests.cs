using System.Security.Cryptography;
using System.Text;
using AspireUI.Server.Services;

// The Stoat template is built from the project's own compose file, vendored under
// catalog/templates/stoat. Refreshing that file is the point at which this has to still hold.
public class StoatTemplateTests
{
    private static AspireUI.Server.Models.StackModel Build() =>
        new TemplateService().Create(StoatTemplate.Id)
            ?? throw new InvalidOperationException("the stoat template did not build");

    [Fact]
    public void It_is_offered_as_a_template_with_something_to_show_in_the_store()
    {
        var t = Assert.Single(new TemplateService().List(), t => t.Id == StoatTemplate.Id);
        Assert.Equal("stoat", t.Icon);
        Assert.NotNull(t.Logo);
        Assert.NotEmpty(t.Screenshots!);
        Assert.NotNull(t.Github);
    }

    [Fact]
    public void Every_service_in_the_compose_file_becomes_a_resource()
    {
        var containers = Build().Nodes.Where(n => n.AddMethod == "AddContainer").ToList();
        Assert.Equal(13, containers.Count);
        foreach (var expected in new[] { "caddy", "web", "api", "events", "autumn", "january", "gifbox", "crond",
                                         "pushd", "database", "redis", "rabbit", "minio" })
            Assert.Contains(containers, n => n.ResourceName == expected);
    }

    [Fact]
    public void Nothing_is_left_for_the_shell_to_expand()
    {
        foreach (var n in Build().Nodes)
            foreach (var arg in n.AddArgs.Concat(n.WithCalls.SelectMany(w => w.Args)))
                Assert.DoesNotContain("${", arg);
    }

    [Fact]
    public void The_values_that_must_not_be_shared_between_installations_are_parameters()
    {
        var stack = Build();
        var parameters = stack.Nodes.Where(n => n.AddMethod == "AddParameter").ToList();
        Assert.Equal(3, parameters.Count);
        Assert.All(parameters, p => Assert.Equal("true", p.AddArgs[1]));      // secret

        var env = stack.Nodes.Where(n => n.AddMethod == "AddContainer")
            .SelectMany(n => n.WithCalls.Where(w => w.Method == "WithEnvironment"))
            .ToList();
        var fileKey = Assert.Single(parameters, p => p.ResourceName == "stoat-files-encryption-key");
        Assert.True(env.Count(w => w.Args[1] == fileKey.VarName) > 1, "the file key is read by more than one service");
        Assert.DoesNotContain(env, w => w.Args[1] == fileKey.AddArgs[0]);
    }

    [Fact]
    public void Each_stack_gets_its_own_secrets()
    {
        string Secret(AspireUI.Server.Models.StackModel s) =>
            s.Nodes.Single(n => n.ResourceName == "stoat-files-encryption-key").AddArgs[0];
        Assert.NotEqual(Secret(Build()), Secret(Build()));
    }

    // pushd reads the private half as base64 of the PEM `openssl ecparam -genkey` writes and the
    // public half as the base64url point — the wrong shape and the notification daemon never starts.
    [Fact]
    public void The_push_keys_are_a_real_p256_pair()
    {
        var stack = Build();
        string Value(string name) => stack.Nodes.Single(n => n.ResourceName == name).AddArgs[0].Trim('"');
        static byte[] FromBase64Url(string s) =>
            Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));

        var pub = FromBase64Url(Value("stoat-vapid-public-key"));
        Assert.Equal(65, pub.Length);
        Assert.Equal(0x04, pub[0]);

        var pem = Encoding.UTF8.GetString(FromBase64Url(Value("stoat-vapid-private-key")));
        Assert.StartsWith("-----BEGIN EC PRIVATE KEY-----", pem);

        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        var q = key.ExportParameters(false).Q;
        Assert.Equal(pub[1..33], q.X);
        Assert.Equal(pub[33..], q.Y);
    }

    [Fact]
    public void The_file_the_edge_mounts_travels_with_the_stack()
    {
        var stack = Build();
        Assert.Contains(stack.ExtraFiles, f => f.Name == "Caddyfile" && f.Content.Length > 0);
        var caddy = stack.Nodes.Single(n => n.ResourceName == "caddy");
        Assert.Contains(caddy.WithCalls, w => w.Method == "WithBindMount" && w.Args[0].Contains("Caddyfile"));
    }

    // Every URL the server hands out has to be one a browser can reach, and the port the edge is
    // published under is picked at deploy time — so what the stack carries is the placeholder.
    [Fact]
    public void The_addresses_it_hands_out_are_filled_in_when_it_is_published()
    {
        var stack = Build();
        var caddy = stack.Nodes.Single(n => n.ResourceName == "caddy");
        var endpoint = Assert.Single(caddy.WithCalls, w => w.Method == "WithHttpEndpoint");
        Assert.Equal(["targetPort: 8080"], endpoint.Args);
        Assert.DoesNotContain(caddy.WithCalls, w => w.Args.Any(a => a.Contains("443")));

        var api = stack.Nodes.Single(n => n.ResourceName == "api");
        var host = Assert.Single(api.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0] == "\"REVOLT__HOSTS__API\"");
        Assert.Equal("\"__ASPIREUI_URL_8080__/api\"", host.Args[1]);

        var published = AspireUI.Server.Services.HostingService.FillPublicUrls(
            "app: __ASPIREUI_URL_8080__/api ws: __ASPIREUI_HOST_8080__", "box", new Dictionary<int, int> { [8080] = 20001 });
        Assert.Equal("app: http://box:20001/api ws: box:20001", published);
    }

    // A bucket is a directory under minio's data path, and making it on the way up is what replaces
    // upstream's one-shot mc container: without it every attachment fails on a missing bucket.
    [Fact]
    public void The_bucket_is_made_before_minio_serves()
    {
        var minio = Build().Nodes.Single(n => n.ResourceName == "minio");
        Assert.Contains(minio.WithCalls, w => w.Method == "WithEntrypoint" && w.Args[0] == "\"/bin/sh\"");
        Assert.Contains(minio.WithCalls, w => w.Method == "WithArgs" && w.Args.Any(a => a.Contains("revolt-uploads")));
    }
}
