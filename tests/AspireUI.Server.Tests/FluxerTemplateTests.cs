using System.Security.Cryptography;
using AspireUI.Server.Services;

// The Fluxer template is built from the project's own compose file, vendored under
// catalog/templates/fluxer. Refreshing that file is the point at which this has to still hold.
public class FluxerTemplateTests
{
    private static AspireUI.Server.Models.StackModel Build() =>
        new TemplateService().Create(FluxerTemplate.Id)
            ?? throw new InvalidOperationException("the fluxer template did not build");

    [Fact]
    public void It_is_offered_as_a_template()
    {
        Assert.Contains(new TemplateService().List(), t => t.Id == FluxerTemplate.Id);
    }

    [Fact]
    public void Every_service_in_the_compose_file_becomes_a_resource()
    {
        var containers = Build().Nodes.Where(n => n.AddMethod == "AddContainer").ToList();
        Assert.Equal(25, containers.Count);
        foreach (var expected in new[] { "edge", "postgres", "valkey", "nats", "meilisearch", "seaweedfs", "livekit", "api", "gateway", "admin" })
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
    public void The_values_it_refuses_to_start_without_are_parameters()
    {
        var stack = Build();
        var parameters = stack.Nodes.Where(n => n.AddMethod == "AddParameter").ToList();
        Assert.Equal(16, parameters.Count);
        Assert.All(parameters, p => Assert.Equal("true", p.AddArgs[1]));      // secret

        // A parameter is worth nothing if the service still carries the literal: the environment
        // value has to be the parameter's variable, and the ones shared between services the same one.
        var env = stack.Nodes.Where(n => n.AddMethod == "AddContainer")
            .SelectMany(n => n.WithCalls.Where(w => w.Method == "WithEnvironment"))
            .ToList();
        var postgresPassword = Assert.Single(parameters, p => p.ResourceName == "postgres-password");
        var readers = env.Where(w => w.Args[1] == postgresPassword.VarName).ToList();
        Assert.True(readers.Count > 1, "the database password is read by more than one service");
        Assert.DoesNotContain(env, w => w.Args[1] == postgresPassword.AddArgs[0]);
    }

    [Fact]
    public void Each_stack_gets_its_own_secrets()
    {
        string Secret(AspireUI.Server.Models.StackModel s) =>
            s.Nodes.Single(n => n.ResourceName == "postgres-password").AddArgs[0];
        Assert.NotEqual(Secret(Build()), Secret(Build()));
    }

    // Two random strings here and the api and the worker crash-loop: Fluxer checks the shape before
    // it starts. "FLUXER_VAPID_PUBLIC_KEY must be the base64url 65-byte uncompressed P-256 point".
    [Fact]
    public void The_push_keys_are_a_real_p256_pair()
    {
        var stack = Build();
        string Value(string name) => stack.Nodes.Single(n => n.ResourceName == name).AddArgs[0].Trim('"');
        static byte[] FromBase64Url(string s) =>
            Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));

        var pub = FromBase64Url(Value("fluxer-vapid-public-key"));
        var priv = FromBase64Url(Value("fluxer-vapid-private-key"));
        Assert.Equal(65, pub.Length);
        Assert.Equal(0x04, pub[0]);
        Assert.Equal(32, priv.Length);

        // The point is on the curve and belongs to that private scalar.
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = priv,
            Q = new ECPoint { X = pub[1..33], Y = pub[33..] },
        });
        var data = "fluxer"u8.ToArray();
        Assert.True(key.VerifyData(data, key.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
    }

    [Fact]
    public void The_file_the_edge_mounts_travels_with_the_stack()
    {
        var stack = Build();
        Assert.Contains(stack.ExtraFiles, f => f.Name == "Caddyfile" && f.Content.Length > 0);
        var edge = stack.Nodes.Single(n => n.ResourceName == "edge");
        Assert.Contains(edge.WithCalls, w => w.Method == "WithBindMount" && w.Args[0].Contains("Caddyfile"));
    }

    [Fact]
    public void The_edge_serves_plain_http_on_one_port_instead_of_fetching_a_certificate()
    {
        var edge = Build().Nodes.Single(n => n.ResourceName == "edge");
        var endpoints = edge.WithCalls.Where(w => w.Method == "WithHttpEndpoint").SelectMany(w => w.Args).ToList();
        Assert.Contains("targetPort: 8080", endpoints);
        Assert.DoesNotContain(endpoints, a => a.Contains("443"));
    }
}
