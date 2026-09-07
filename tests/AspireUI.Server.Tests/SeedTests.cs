using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Tests;

// The seed beyond "one admin and one stack": several accounts, deploy targets, api tokens, store
// sources, apps from the catalog and stacks from a compose file.
public class SeedTests
{
    private sealed record Stores(string Db, string Workspace, UserStore Users, StackStore Stacks,
        SettingsStore Settings, TargetStore Targets, SecretStore Secrets, ApiTokenStore Tokens);

    private static Stores Fresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-seed-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "aspireui.db");
        return new Stores(db, Path.Combine(root, "workspace"), new UserStore(db), new StackStore(db),
            new SettingsStore(db), new TargetStore(db), new SecretStore(db, root), new ApiTokenStore(db));
    }

    private static void Seed(Stores s, Dictionary<string, string?> env) =>
        Seeder.Seed(s.Users, s.Stacks, s.Settings, env, s.Targets, s.Secrets, s.Tokens,
            catalog: new CatalogService(), workspace: s.Workspace);

    [Fact]
    public void Users_from_the_short_form_get_their_role_and_permissions()
    {
        var s = Fresh();
        Seed(s, new() { ["ASPIREUI_USERS"] = "boss:bosspassword:admin;ops:opspassword:operator;kim:kimpassword:deploy,files" });

        var boss = s.Users.FindByUsername("boss")!;
        Assert.True(boss.IsAdmin);

        var ops = s.Users.FindByUsername("ops")!;
        Assert.False(ops.IsAdmin);
        Assert.Equal(Perm.Operator.OrderBy(x => x), ops.Permissions!.OrderBy(x => x));

        var kim = s.Users.FindByUsername("kim")!;
        Assert.Equal(new[] { Perm.Deploy, Perm.Files }, kim.Permissions);
        Assert.NotEqual("kimpassword", kim.PasswordHash);
    }

    [Fact]
    public void A_user_without_a_permission_spec_gets_the_default_set()
    {
        var s = Fresh();
        Seed(s, new() { ["ASPIREUI_USERS"] = "dev:devpassword1" });
        Assert.Equal(Perm.Default.OrderBy(x => x), s.Users.FindByUsername("dev")!.Permissions!.OrderBy(x => x));
    }

    [Fact]
    public void Seeding_twice_leaves_an_existing_account_alone()
    {
        var s = Fresh();
        Seed(s, new() { ["ASPIREUI_USERS"] = "dev:devpassword1:viewer" });
        var before = s.Users.FindByUsername("dev")!;
        s.Users.SetPermissions(before.Id, Perm.All.ToList());

        Seed(s, new() { ["ASPIREUI_USERS"] = "dev:devpassword1:viewer;second:secondpassword" });

        Assert.Equal(Perm.All.Length, s.Users.FindByUsername("dev")!.Permissions!.Count);
        Assert.NotNull(s.Users.FindByUsername("second"));
        Assert.Equal(2, s.Users.List().Count);
    }

    [Fact]
    public void Users_can_be_given_as_json_when_the_short_form_will_not_do()
    {
        var s = Fresh();
        Seed(s, new()
        {
            ["ASPIREUI_USERS"] = """
            [{"username":"od:d","password":"pass:with:colons","permissions":["files"],"viewModes":["simple"],"mustChangePassword":true}]
            """,
        });

        var u = s.Users.FindByUsername("od:d")!;
        Assert.Equal(new[] { Perm.Files }, u.Permissions);
        Assert.Equal(new[] { "simple" }, u.ViewModes);
        Assert.True(u.MustChangePassword);
    }

    [Fact]
    public void An_ssh_target_keeps_its_key_in_the_secret_store()
    {
        var s = Fresh();
        var keyFile = Path.Combine(Path.GetDirectoryName(s.Db)!, "id_ed25519");
        File.WriteAllText(keyFile, "-----BEGIN OPENSSH PRIVATE KEY-----\nnot-a-real-key\n");

        Seed(s, new() { ["ASPIREUI_TARGETS"] = $"nas=ssh://deploy@nas.local:2222?key={keyFile}&publicHost=apps.example.com&default=true" });

        var t = Assert.Single(s.Targets.List(), x => x.Name == "nas");
        Assert.Equal(TargetKind.Ssh, t.Kind);
        Assert.Equal("nas.local", t.Ssh!.Host);
        Assert.Equal(2222, t.Ssh.Port);
        Assert.Equal("deploy", t.Ssh.User);
        Assert.Equal("apps.example.com", t.PublicHost);
        Assert.True(t.Default);
        // The row holds a reference, the key itself lives in the secret store.
        Assert.DoesNotContain("OPENSSH", JsonSerializer.Serialize(t));
        Assert.Contains("not-a-real-key", s.Secrets.Resolve(t.Ssh.KeyRef));
    }

    [Fact]
    public void A_tcp_target_gets_its_docker_host_and_certificates()
    {
        var s = Fresh();
        Seed(s, new() { ["ASPIREUI_TARGETS"] = "box=tcp://10.0.0.5:2376?ca=CA-PEM&cert=CERT-PEM&key=KEY-PEM" });

        var t = Assert.Single(s.Targets.List(), x => x.Name == "box");
        Assert.Equal(TargetKind.DockerTcp, t.Kind);
        Assert.Equal("tcp://10.0.0.5:2376", t.DockerHost);
        Assert.Equal("CA-PEM", s.Secrets.Resolve(t.Tls!.CaRef));
        Assert.Equal("CERT-PEM", s.Secrets.Resolve(t.Tls.CertRef));
        Assert.Equal("KEY-PEM", s.Secrets.Resolve(t.Tls.KeyRef));
    }

    [Fact]
    public void A_kubernetes_target_takes_the_context_from_the_uri()
    {
        var s = Fresh();
        Seed(s, new()
        {
            ["ASPIREUI_TARGETS"] = "cluster=k8s://prod-context?namespace=apps&expose=ingress&ingressHost={service}.example.com&storageClass=fast",
        });

        var t = Assert.Single(s.Targets.List(), x => x.Name == "cluster");
        Assert.Equal(TargetKind.K8s, t.Kind);
        Assert.Equal("prod-context", t.Kube!.Context);
        Assert.Equal("apps", t.Kube.Namespace);
        Assert.Equal("ingress", t.Kube.Expose);
        Assert.Equal("{service}.example.com", t.Kube.IngressHostPattern);
        Assert.Equal("fast", t.Kube.StorageClass);
    }

    [Fact]
    public void The_local_target_cannot_be_seeded_over()
    {
        var s = Fresh();
        Seed(s, new() { ["ASPIREUI_TARGETS"] = "mine=local://whatever;ok=ssh://host" });
        Assert.Equal(new[] { "This machine", "ok" }, s.Targets.List().Select(t => t.Name));
    }

    [Fact]
    public void A_seeded_token_authenticates_as_its_user()
    {
        var s = Fresh();
        Seed(s, new()
        {
            ["ASPIREUI_USERS"] = "ci:cipassword1:deploy",
            ["ASPIREUI_API_TOKENS"] = "pipeline:ci:aspireui_seeded_token_value",
        });

        var ci = s.Users.FindByUsername("ci")!;
        Assert.Equal(ci.Id, s.Tokens.ResolveUserId("aspireui_seeded_token_value"));
        Assert.Equal("pipeline", Assert.Single(s.Tokens.List(ci.Id)).Name);

        // Same seed again: still one token, not two.
        Seed(s, new()
        {
            ["ASPIREUI_USERS"] = "ci:cipassword1:deploy",
            ["ASPIREUI_API_TOKENS"] = "pipeline:ci:aspireui_seeded_token_value",
        });
        Assert.Single(s.Tokens.List(ci.Id));
    }

    [Fact]
    public void A_token_for_an_unknown_user_is_skipped()
    {
        var s = Fresh();
        Seed(s, new() { ["ASPIREUI_API_TOKENS"] = "pipeline:nobody:aspireui_x" });
        Assert.Null(s.Tokens.ResolveUserId("aspireui_x"));
    }

    [Fact]
    public void Store_sources_are_registered_but_not_fetched()
    {
        var s = Fresh();
        var appSources = new AppSourceService(s.Settings, Path.Combine(Path.GetDirectoryName(s.Db)!, "cache"));
        Seeder.Seed(s.Users, s.Stacks, s.Settings,
            new Dictionary<string, string?> { ["ASPIREUI_APP_SOURCES"] = "acme=https://apps.acme.test/apps.json" },
            appSources: appSources);

        var src = Assert.Single(appSources.List());
        Assert.Equal("acme", src.Name);
        Assert.Null(src.LastRefresh);
    }

    [Fact]
    public void An_app_from_the_catalog_becomes_a_stack()
    {
        var s = Fresh();
        Seed(s, new() { ["ASPIREUI_SEED_APPS"] = "vaultwarden=Passwords" });

        var stack = Assert.Single(s.Stacks.List());
        Assert.Equal("Passwords", stack.Name);
        Assert.Contains(stack.Nodes, n => n.AddArgs.Any(a => a.Contains("vaultwarden")));
        Assert.Equal("seed", stack.CreatedBy);
        // Nothing was asked to be deployed, so nothing is pending.
        Assert.True(string.IsNullOrEmpty(s.Settings.GetValue(Seeder.PendingDeployKey)));
    }

    [Fact]
    public void An_unknown_app_id_is_skipped_and_the_rest_still_lands()
    {
        var s = Fresh();
        Seed(s, new() { ["ASPIREUI_SEED_APPS"] = "not-an-app,gitea" });
        Assert.Single(s.Stacks.List());
    }

    [Fact]
    public void Seeded_apps_can_ask_to_be_deployed()
    {
        var s = Fresh();
        Seed(s, new() { ["ASPIREUI_SEED_APPS"] = "vaultwarden", ["ASPIREUI_SEED_DEPLOY"] = "true" });

        var pending = JsonSerializer.Deserialize<List<string>>(s.Settings.GetValue(Seeder.PendingDeployKey)!)!;
        Assert.Equal(s.Stacks.List().Single().Id, Assert.Single(pending));
    }

    [Fact]
    public void A_compose_file_in_the_seed_file_becomes_a_stack()
    {
        var s = Fresh();
        var dir = Path.Combine(Path.GetDirectoryName(s.Db)!, "seed");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), """
            services:
              web:
                image: nginx:1.27
                ports:
                  - "8080:80"
            """);
        File.WriteAllText(Path.Combine(dir, "aspireui.seed.json"), """
            {
              "stacks": [{ "name": "Edge", "compose": "COMPOSE_PATH" }],
              "settings": { "PublicHost": "apps.example.com" }
            }
            """.Replace("COMPOSE_PATH", Path.Combine(dir, "docker-compose.yml").Replace("\\", "\\\\")));

        Seed(s, new() { ["ASPIREUI_SEED_FILE"] = dir });

        var stack = Assert.Single(s.Stacks.List());
        Assert.Equal("Edge", stack.Name);
        Assert.Contains(stack.Nodes, n => n.AddArgs.Any(a => a.Contains("nginx")));
        Assert.Equal("apps.example.com", s.Settings.GetValue("PublicHost"));
    }

    [Fact]
    public void A_directory_is_imported_as_a_copy_and_left_untouched()
    {
        var s = Fresh();
        var dir = Path.Combine(Path.GetDirectoryName(s.Db)!, "myapp");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), """
            services:
              api:
                image: caddy:2
            """);

        Seed(s, new() { ["ASPIREUI_SEED_DIR"] = dir });

        var stack = Assert.Single(s.Stacks.List());
        Assert.Equal("myapp", stack.Name);
        Assert.True(stack.HasSource);
        Assert.True(File.Exists(Path.Combine(s.Workspace, stack.Id, "docker-compose.yml")));
        Assert.Single(Directory.GetFiles(dir));
    }

    [Fact]
    public void The_env_vars_and_the_seed_file_add_up()
    {
        var s = Fresh();
        var file = Path.Combine(Path.GetDirectoryName(s.Db)!, "seed.json");
        File.WriteAllText(file, """
            { "users": [{ "username": "fromfile", "password": "filepassword1" }] }
            """);

        Seed(s, new() { ["ASPIREUI_SEED_FILE"] = file, ["ASPIREUI_USERS"] = "fromenv:envpassword1" });

        Assert.NotNull(s.Users.FindByUsername("fromfile"));
        Assert.NotNull(s.Users.FindByUsername("fromenv"));
    }

    [Fact]
    public void A_whole_document_can_arrive_in_one_variable()
    {
        var s = Fresh();
        // What the Aspire integration writes: one JSON document, nothing to mount.
        Seed(s, new()
        {
            ["ASPIREUI_SEED"] = """
            {
              "users": [{ "username": "ops", "password": "opspassword1", "permissions": ["deploy", "files"] }],
              "targets": [{ "name": "nas", "kind": "ssh", "host": "nas.local", "user": "deploy", "key": "KEY" }],
              "tokens": [{ "name": "pipeline", "username": "ops", "token": "aspireui_from_apphost" }],
              "apps": [{ "id": "gitea" }],
              "deploy": true
            }
            """,
        });

        var ops = s.Users.FindByUsername("ops")!;
        Assert.Equal(new[] { Perm.Deploy, Perm.Files }, ops.Permissions);
        Assert.Equal("nas.local", Assert.Single(s.Targets.List(), t => t.Name == "nas").Ssh!.Host);
        Assert.Equal(ops.Id, s.Tokens.ResolveUserId("aspireui_from_apphost"));
        Assert.Single(s.Stacks.List());
        Assert.NotEmpty(s.Settings.GetValue(Seeder.PendingDeployKey)!);
    }

    [Fact]
    public void A_broken_seed_file_does_not_stop_the_rest()
    {
        var s = Fresh();
        var file = Path.Combine(Path.GetDirectoryName(s.Db)!, "seed.json");
        File.WriteAllText(file, "{ not json");

        Seed(s, new() { ["ASPIREUI_SEED_FILE"] = file, ["ASPIREUI_USERS"] = "dev:devpassword1" });

        Assert.NotNull(s.Users.FindByUsername("dev"));
    }
}
