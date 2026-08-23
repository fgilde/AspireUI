using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Tests;

// Deploy targets: the store, the credentials behind them, and the parts of a deploy that differ per
// target (ports, urls, domains). Nothing here talks to a real machine.
public class TargetStoreTests
{
    private static TargetStore Store() => new(":memory:");

    [Fact]
    public void Local_exists_from_the_start_and_is_the_default()
    {
        var s = Store();
        var local = s.Get(DeployTarget.LocalId);
        Assert.NotNull(local);
        Assert.Equal(TargetKind.Local, local!.Kind);
        Assert.True(local.Default);
        Assert.Equal(local.Id, s.DefaultTarget().Id);
    }

    [Fact]
    public void Local_cannot_be_deleted()
    {
        var s = Store();
        Assert.False(s.Delete(DeployTarget.LocalId));
        Assert.NotNull(s.Get(DeployTarget.LocalId));
    }

    [Fact]
    public void An_unknown_id_resolves_to_local_so_a_deployment_always_has_a_home()
    {
        var s = Store();
        Assert.Equal(DeployTarget.LocalId, s.Resolve("gone-yesterday").Id);
        Assert.Equal(DeployTarget.LocalId, s.Resolve(null).Id);
    }

    [Fact]
    public void Ids_come_from_the_name_and_stay_unique()
    {
        var s = Store();
        var a = s.UniqueId("Hetzner Prod!");
        s.Upsert(new DeployTarget(a, "Hetzner Prod!", TargetKind.Ssh));
        var b = s.UniqueId("Hetzner Prod!");
        Assert.Equal("hetzner-prod", a);
        Assert.Equal("hetzner-prod-2", b);
    }

    [Fact]
    public void Only_one_target_is_the_default_and_deleting_it_hands_the_flag_back_to_local()
    {
        var s = Store();
        s.Upsert(new DeployTarget("box", "Box", TargetKind.Ssh, Default: true,
            Ssh: new TargetSsh("10.0.0.5", 22, "root", "sec:x")));
        Assert.Equal("box", s.DefaultTarget().Id);
        Assert.False(s.Get(DeployTarget.LocalId)!.Default);
        s.Delete("box");
        Assert.Equal(DeployTarget.LocalId, s.DefaultTarget().Id);
    }

    [Fact]
    public void Url_host_prefers_the_public_host_then_the_ssh_host()
    {
        Assert.Equal("localhost", new DeployTarget("local", "l", TargetKind.Local).HostForUrls());
        Assert.Equal("10.0.0.5", new DeployTarget("b", "b", TargetKind.Ssh, Ssh: new TargetSsh("10.0.0.5", 22, "root")).HostForUrls());
        Assert.Equal("apps.example.com", new DeployTarget("b", "b", TargetKind.Ssh, PublicHost: "apps.example.com",
            Ssh: new TargetSsh("10.0.0.5", 22, "root")).HostForUrls());
    }
}

public class SecretStoreTests
{
    private static SecretStore New(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "aspireui-sec-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        return new SecretStore(Path.Combine(dir, "test.db"), dir);
    }

    [Fact]
    public void A_value_goes_in_as_a_ref_and_comes_back_out()
    {
        var s = New(out var dir);
        try
        {
            var r = s.Put("-----BEGIN OPENSSH PRIVATE KEY-----", "ssh key");
            Assert.StartsWith("sec:", r);
            Assert.Equal("-----BEGIN OPENSSH PRIVATE KEY-----", s.Resolve(r));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void The_database_never_holds_the_plaintext()
    {
        var s = New(out var dir);
        try
        {
            s.Put("hunter2-the-password");
            using var fs = new FileStream(Path.Combine(dir, "test.db"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            Assert.DoesNotContain("hunter2-the-password", text);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void An_env_or_file_reference_keeps_the_secret_out_of_the_database()
    {
        var s = New(out var dir);
        try
        {
            Environment.SetEnvironmentVariable("ASPIREUI_TEST_SECRET", "from-env");
            Assert.Equal("from-env", s.Resolve("env:ASPIREUI_TEST_SECRET"));
            var file = Path.Combine(dir, "token.txt");
            File.WriteAllText(file, "from-file");
            Assert.Equal("from-file", s.Resolve("file:" + file));
            // Storing a ref keeps the indirection instead of copying the value.
            Assert.Equal("env:ASPIREUI_TEST_SECRET", s.Put("env:ASPIREUI_TEST_SECRET"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPIREUI_TEST_SECRET", null);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Replacing_keeps_the_old_value_when_nothing_new_was_sent()
    {
        var s = New(out var dir);
        try
        {
            var first = s.Put("one");
            Assert.Equal(first, s.Replace(first, null));
            var second = s.Replace(first, "two");
            Assert.NotEqual(first, second);
            Assert.Equal("two", s.Resolve(second));
            Assert.Null(s.Resolve(first));   // the old one is gone
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class TargetServiceTests
{
    private static (TargetService svc, TargetStore store, string dir) New()
    {
        Environment.SetEnvironmentVariable("ASPIREUI_NO_SSH_INCLUDE", "1");
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-tgt-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "test.db");
        var store = new TargetStore(db);
        return (new TargetService(store, new SecretStore(db, dir), dir), store, dir);
    }

    [Fact]
    public void Local_needs_no_environment_at_all()
    {
        var (svc, store, dir) = New();
        try { Assert.Empty(svc.EnvironmentFor(store.Get(DeployTarget.LocalId)!)); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void An_ssh_target_becomes_a_docker_host_and_an_ssh_config()
    {
        var (svc, store, dir) = New();
        try
        {
            var secrets = new SecretStore(Path.Combine(dir, "test.db"), dir);
            var keyRef = secrets.Put("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----");
            var t = store.Upsert(new DeployTarget("box", "Box", TargetKind.Ssh,
                Ssh: new TargetSsh("10.0.0.5", 2222, "deploy", keyRef)));
            var env = svc.EnvironmentFor(t);
            Assert.Equal("ssh://aspireui-box", env["DOCKER_HOST"]);
            var cfg = File.ReadAllText(Path.Combine(svc.TargetDir("box"), "config"));
            Assert.Contains("HostName 10.0.0.5", cfg);
            Assert.Contains("Port 2222", cfg);
            Assert.Contains("User deploy", cfg);
            Assert.Contains("IdentityFile", cfg);
            Assert.Contains("BatchMode yes", cfg);
            Assert.True(File.Exists(Path.Combine(svc.TargetDir("box"), "id_key")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_tcp_target_gets_the_daemon_address_and_its_certificates()
    {
        var (svc, store, dir) = New();
        try
        {
            var secrets = new SecretStore(Path.Combine(dir, "test.db"), dir);
            var t = store.Upsert(new DeployTarget("nas", "NAS", TargetKind.DockerTcp,
                DockerHost: "tcp://10.0.0.9:2376",
                Tls: new TargetTls(secrets.Put("ca"), secrets.Put("cert"), secrets.Put("key"))));
            var env = svc.EnvironmentFor(t);
            Assert.Equal("tcp://10.0.0.9:2376", env["DOCKER_HOST"]);
            Assert.Equal("1", env["DOCKER_TLS_VERIFY"]);
            Assert.True(File.Exists(Path.Combine(env["DOCKER_CERT_PATH"], "ca.pem")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Published_ports_are_read_off_the_daemons_own_container_list()
    {
        var used = TargetService.ParsePublishedPorts(
            "0.0.0.0:20001->3000/tcp, [::]:20001->3000/tcp\n0.0.0.0:8080->80/tcp\n\n5432/tcp");
        Assert.Contains(20001, used);
        Assert.Contains(8080, used);
        Assert.DoesNotContain(5432, used);   // not published, only exposed
    }

    [Fact]
    public void A_runner_is_reused_until_the_target_changes()
    {
        var (svc, store, dir) = New();
        try
        {
            var t = store.Upsert(new DeployTarget("box", "Box", TargetKind.Ssh, Ssh: new TargetSsh("10.0.0.5", 22, "root")));
            var first = svc.Runner(t);
            Assert.Same(first, svc.Runner(store.Get("box")!));
            var changed = store.Upsert(store.Get("box")! with { Ssh = new TargetSsh("10.0.0.6", 22, "root") });
            Assert.NotSame(first, svc.Runner(changed));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class TargetPortTests
{
    [Fact]
    public void A_remote_target_allocates_from_its_own_range_without_binding_anything_here()
    {
        var used = new HashSet<int> { 30000, 30001 };
        var a = HostingService.AllocateHostPort(used, 30000, 30010, free: _ => true);
        var b = HostingService.AllocateHostPort(used, 30000, 30010, free: _ => true);
        Assert.Equal(30002, a);
        Assert.Equal(30003, b);
    }

    [Fact]
    public void An_exhausted_range_says_so_instead_of_reusing_a_port()
    {
        var used = new HashSet<int> { 40000, 40001 };
        Assert.Throws<InvalidOperationException>(() => HostingService.AllocateHostPort(used, 40000, 40001, free: _ => true));
    }
}

public class DomainTargetTests
{
    private static (DomainService svc, TargetStore store, SettingsStore settings, string dir) New()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-dom-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "test.db");
        var store = new TargetStore(db);
        var settings = new SettingsStore(db);
        return (new DomainService(store, new SecretStore(db, dir), settings), store, settings, dir);
    }

    [Fact]
    public void The_old_global_npm_settings_become_the_local_targets_domain_configuration()
    {
        var (svc, store, settings, dir) = New();
        try
        {
            settings.SetValue("NpmEnabled", "true");
            settings.SetValue("NpmBaseUrl", "http://npm.local:81");
            settings.SetValue("NpmEmail", "me@example.com");
            settings.SetValue("NpmPassword", "secret");
            svc.MigrateGlobalNpm();
            var local = store.Get(DeployTarget.LocalId)!;
            Assert.Equal(DomainService.KindNpm, svc.KindOf(local));
            Assert.True(svc.Configured(local));
            var npm = svc.Npm(local)!;
            Assert.Equal("http://npm.local:81", npm.BaseUrl);
            Assert.Equal("secret", npm.Password);
            // Running it again must not wipe what is already there.
            svc.MigrateGlobalNpm();
            Assert.Equal(DomainService.KindNpm, svc.KindOf(store.Get(DeployTarget.LocalId)!));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Without_a_provider_a_target_has_no_domains()
    {
        var (svc, store, _, dir) = New();
        try
        {
            var t = store.Upsert(new DeployTarget("box", "Box", TargetKind.Ssh, Ssh: new TargetSsh("10.0.0.5", 22, "root")));
            Assert.False(svc.Configured(t));
            Assert.Equal(DomainService.KindNone, svc.KindOf(t));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_remote_targets_proxy_forwards_to_the_targets_own_address()
    {
        var (svc, store, _, dir) = New();
        try
        {
            var t = store.Upsert(new DeployTarget("box", "Box", TargetKind.Ssh, PublicHost: "10.0.0.5",
                Ssh: new TargetSsh("10.0.0.5", 22, "root"), Domains: new TargetDomains(DomainService.KindManual)));
            Assert.Equal("10.0.0.5", svc.ForwardHost(t, "aspireui.example.com"));
            // A fixed forward host wins, for a proxy that reaches the box under another name.
            var fixedHost = store.Upsert(t with { Domains = new TargetDomains(DomainService.KindNpm, new TargetNpm("http://npm", "a@b", null, "box.lan")) });
            Assert.Equal("box.lan", svc.ForwardHost(fixedHost, "aspireui.example.com"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class DeploymentTargetTests
{
    [Fact]
    public void A_deployment_without_a_target_belongs_to_this_machine()
    {
        var d = new Deployment("d1", "s1", "app", "/dir", "proj", "running", new(), "now", "now", null);
        Assert.Equal(DeployTarget.LocalId, d.Target);
    }

    [Fact]
    public void The_target_survives_a_round_trip_through_the_store()
    {
        var store = new DeploymentStore(":memory:");
        store.Upsert(new Deployment("d1", "s1", "app", "/dir", "proj", "running", new(), "now", "now", null,
            TargetId: "hetzner-1"));
        Assert.Equal("hetzner-1", store.Get("d1")!.TargetId);
        Assert.Equal("hetzner-1", store.Get("d1")!.Target);
    }
}
