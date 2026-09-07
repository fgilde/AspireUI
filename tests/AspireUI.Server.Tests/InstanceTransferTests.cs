using System.IO.Compression;
using System.Text;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

// Taking a whole instance out and putting it back into another one.
public class InstanceTransferTests
{
    private sealed record Stores(string Root, UserStore Users, StackStore Stacks, SettingsStore Settings,
        TargetStore Targets, DeploymentStore Deployments, SecretStore Secrets);

    private static Stores Fresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-transfer-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "aspireui.db");
        return new Stores(root, new UserStore(db), new StackStore(db), new SettingsStore(db),
            new TargetStore(db), new DeploymentStore(db), new SecretStore(db, root));
    }

    private static StackModel Stack(string name) =>
        new(Guid.NewGuid().ToString("n"), name, "net10.0", [], [], [], [], []);

    private static Stores Filled()
    {
        var s = Fresh();
        s.Users.Create("boss", "hash-of-a-password", isAdmin: true);
        var kim = s.Users.Create("kim", "another-hash", isAdmin: false);
        s.Users.SetPermissions(kim.Id, [Perm.Deploy, Perm.Files]);
        s.Users.SetViewModes(kim.Id, ["simple"]);

        s.Stacks.Save(Stack("Edge"));
        s.Stacks.Save(Stack("Tools"));
        s.Settings.SetValue("PublicHost", "apps.example.com");
        s.Settings.SetValue("NpmPassword", "proxy-secret");
        s.Settings.SetValue("BackupLastRun", DateTime.UtcNow.ToString("O"));

        var id = s.Targets.UniqueId("nas");
        s.Targets.Upsert(new DeployTarget(id, "nas", TargetKind.Ssh,
            Ssh: new TargetSsh("nas.local", 22, "deploy", s.Secrets.Put("PRIVATE-KEY-TEXT", "key"))));
        return s;
    }

    private static InstanceExport Collect(Stores s, bool secrets = false) =>
        InstanceTransfer.Collect(s.Users, s.Stacks, s.Settings, s.Targets, s.Deployments, null,
            s.Secrets, secrets, "test");

    [Fact]
    public void An_export_carries_accounts_stacks_targets_and_settings()
    {
        var doc = Collect(Filled());

        Assert.Equal(InstanceTransfer.FormatVersion, doc.Version);
        Assert.Equal(["boss", "kim"], doc.Users!.Select(u => u.Username).OrderBy(x => x));
        Assert.Equal("another-hash", doc.Users!.Single(u => u.Username == "kim").PasswordHash);
        Assert.Equal(new[] { Perm.Deploy, Perm.Files }, doc.Users!.Single(u => u.Username == "kim").Permissions);
        Assert.Equal(2, doc.Stacks!.Count);
        Assert.Equal("apps.example.com", doc.Settings!["PublicHost"]);
        Assert.Contains(doc.Targets!, t => t.Name == "nas");
    }

    [Fact]
    public void Secrets_and_machine_local_values_stay_behind_unless_asked_for()
    {
        var s = Filled();

        var plain = Collect(s);
        Assert.False(plain.ContainsSecrets);
        Assert.DoesNotContain("NpmPassword", plain.Settings!.Keys);
        Assert.DoesNotContain("BackupLastRun", plain.Settings!.Keys);
        // A reference is useless without this machine's key, so it does not travel either.
        Assert.Null(plain.Targets!.Single(t => t.Name == "nas").Ssh!.KeyRef);

        var withSecrets = Collect(s, secrets: true);
        Assert.True(withSecrets.ContainsSecrets);
        Assert.Equal("proxy-secret", withSecrets.Settings!["NpmPassword"]);
        Assert.Equal("PRIVATE-KEY-TEXT", withSecrets.Targets!.Single(t => t.Name == "nas").Ssh!.KeyRef);
    }

    [Fact]
    public void An_export_round_trips_through_the_file()
    {
        var bytes = InstanceTransfer.Write(Collect(Filled()));
        var (doc, error) = InstanceTransfer.Read(new MemoryStream(bytes));

        Assert.Null(error);
        Assert.NotNull(doc);
        Assert.Equal(2, doc!.Stacks!.Count);

        // The archive is readable by anything, not only by us.
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, e => e.FullName == InstanceTransfer.DocumentName);
    }

    [Fact]
    public void A_bare_json_document_is_accepted_too()
    {
        var bytes = InstanceTransfer.Write(Collect(Filled()));
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(InstanceTransfer.DocumentName)!.Open());
        var json = reader.ReadToEnd();

        var (doc, error) = InstanceTransfer.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Null(error);
        Assert.Equal(2, doc!.Stacks!.Count);
    }

    [Fact]
    public void A_newer_format_is_refused_rather_than_half_understood()
    {
        var doc = Collect(Filled()) with { Version = InstanceTransfer.FormatVersion + 1 };
        var (read, error) = InstanceTransfer.Read(new MemoryStream(InstanceTransfer.Write(doc)));
        Assert.Null(read);
        Assert.Contains("newer AspireUI", error);
    }

    [Fact]
    public void Nonsense_is_refused_with_a_reason()
    {
        var (doc, error) = InstanceTransfer.Read(new MemoryStream(Encoding.UTF8.GetBytes("{ not json")));
        Assert.Null(doc);
        Assert.NotNull(error);
    }

    [Fact]
    public void An_import_into_an_empty_instance_brings_everything()
    {
        var doc = Collect(Filled(), secrets: true);
        var target = Fresh();

        var report = InstanceTransfer.Apply(doc, target.Users, target.Stacks, target.Settings,
            target.Targets, null, target.Secrets);

        Assert.Equal(2, report.Users);
        Assert.Equal(2, report.Stacks);
        Assert.Equal(1, report.Targets);
        Assert.Empty(report.Skipped);

        var kim = target.Users.FindByUsername("kim")!;
        Assert.Equal("another-hash", kim.PasswordHash);
        Assert.Equal(new[] { Perm.Deploy, Perm.Files }, kim.Permissions);
        Assert.Equal(new[] { "simple" }, kim.ViewModes);
        Assert.Equal("apps.example.com", target.Settings.GetValue("PublicHost"));

        // Key material that travelled as text is put back into the secret store, not left in the row.
        var nas = target.Targets.List().Single(t => t.Name == "nas");
        Assert.NotEqual("PRIVATE-KEY-TEXT", nas.Ssh!.KeyRef);
        Assert.Equal("PRIVATE-KEY-TEXT", target.Secrets.Resolve(nas.Ssh.KeyRef));
    }

    [Fact]
    public void An_import_is_a_merge_and_keeps_what_is_already_there()
    {
        var doc = Collect(Filled());
        var target = Fresh();
        target.Users.Create("kim", "the-hash-that-is-already-here", isAdmin: false);
        target.Stacks.Save(Stack("Edge"));
        target.Settings.SetValue("PublicHost", "mine.example.com");

        var report = InstanceTransfer.Apply(doc, target.Users, target.Stacks, target.Settings,
            target.Targets, null, target.Secrets);

        Assert.Equal("the-hash-that-is-already-here", target.Users.FindByUsername("kim")!.PasswordHash);
        Assert.Equal("mine.example.com", target.Settings.GetValue("PublicHost"));
        Assert.Equal(2, target.Stacks.List().Count);            // Edge kept, Tools added
        Assert.Contains("user kim", report.Skipped);
        Assert.Contains("stack Edge", report.Skipped);
        Assert.Contains("setting PublicHost", report.Skipped);
    }

    [Fact]
    public void Overwrite_replaces_what_the_merge_would_have_kept()
    {
        var doc = Collect(Filled());
        var target = Fresh();
        target.Users.Create("kim", "the-old-hash", isAdmin: false);
        target.Settings.SetValue("PublicHost", "mine.example.com");

        InstanceTransfer.Apply(doc, target.Users, target.Stacks, target.Settings, target.Targets, null,
            target.Secrets, overwrite: true);

        Assert.Equal("another-hash", target.Users.FindByUsername("kim")!.PasswordHash);
        Assert.Equal("apps.example.com", target.Settings.GetValue("PublicHost"));
    }

    [Fact]
    public void The_local_target_of_the_receiving_instance_is_left_alone()
    {
        var doc = Collect(Filled());
        var target = Fresh();
        InstanceTransfer.Apply(doc, target.Users, target.Stacks, target.Settings, target.Targets, null, target.Secrets);

        var local = target.Targets.List().Single(t => t.IsLocal);
        Assert.Equal("This machine", local.Name);
    }
}
