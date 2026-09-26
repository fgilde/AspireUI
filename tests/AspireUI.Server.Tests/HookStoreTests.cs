using AspireUI.Server.Services;

public class HookStoreTests
{
    private static SettingsStore NewSettings() =>
        new(Path.Combine(Path.GetTempPath(), "hookstore-" + Guid.NewGuid().ToString("n") + ".db"));

    [Fact]
    public void A_saved_hook_comes_back_and_is_listed_once()
    {
        var hooks = new HookStore(NewSettings());
        var h = new Hook("t1", "store", "Immich", AppId: "immich", Params: [new("DB_PASSWORD", "generated", Secret: true)]);

        hooks.Save(h);
        hooks.Save(h with { Name = "Immich 2" });

        var back = Assert.Single(hooks.List());
        Assert.Equal("Immich 2", back.Name);
        Assert.Equal("generated", back.Params![0].Mode);
        Assert.Equal(back, hooks.Get("t1") with { Params = back.Params });
    }

    [Fact]
    public void Deleting_removes_hook_and_index_entry()
    {
        var hooks = new HookStore(NewSettings());
        hooks.Save(new Hook("t1", "clone", "a", SourceStackId: "s"));
        hooks.Delete("t1");
        Assert.Null(hooks.Get("t1"));
        Assert.Empty(hooks.List());
    }

    [Fact]
    public void Regenerate_moves_the_hook_to_a_new_token()
    {
        var hooks = new HookStore(NewSettings());
        hooks.Save(new Hook("old", "clone", "a", SourceStackId: "s"));
        var fresh = hooks.Regenerate("old");
        Assert.NotEqual("old", fresh.Token);
        Assert.Null(hooks.Get("old"));
        Assert.Equal("a", hooks.Get(fresh.Token)!.Name);
        Assert.Single(hooks.List());
    }

    [Fact]
    public void Settings_default_and_roundtrip()
    {
        var hooks = new HookStore(NewSettings());
        Assert.Equal(new HookSettings(true, 5, 1), hooks.Settings());
        hooks.SaveSettings(new HookSettings(false, 2.5, 0.5));
        Assert.Equal(new HookSettings(false, 2.5, 0.5), hooks.Settings());
    }

    [Fact]
    public void Old_clone_hooks_migrate_once_and_keep_their_token()
    {
        var settings = NewSettings();
        settings.SetValue("clonehook:abc", """{"sourceStackId":"s1","expireDays":3,"bindDomain":true,"domainFormat":"x-{id}.example.com","targetId":null}""");
        settings.SetValue("clonehooks:s1", """["abc"]""");
        var hooks = new HookStore(settings);

        Assert.Equal(1, hooks.MigrateCloneHooks(id => id == "s1" ? "Blog" : null));
        Assert.Equal(0, hooks.MigrateCloneHooks(_ => "x"));

        var h = hooks.Get("abc")!;
        Assert.Equal("clone", h.Kind);
        Assert.Equal("Blog", h.Name);
        Assert.Equal("s1", h.SourceStackId);
        Assert.Equal(3, h.ExpireDays);
        Assert.True(h.BindDomain);
        Assert.Null(settings.GetValue("clonehook:abc"));
        Assert.Null(settings.GetValue("clonehooks:s1"));
    }

    [Fact]
    public void Masked_secrets_are_kept_on_update_and_real_values_replace_them()
    {
        var existing = new Hook("t", "git", "g", AuthToken: "ghp_real",
            Params: [new("API_KEY", "fixed", "real", true), new("OTHER", "fixed", "old", true), new("NAME", "fixed", "n")]);
        var masked = HookStore.Mask(existing);
        Assert.Equal(HookStore.Masked, masked.AuthToken);
        Assert.Equal(HookStore.Masked, masked.Params![0].Value);
        Assert.Equal("n", masked.Params[2].Value);

        var incoming = masked with { Params = [masked.Params[0], masked.Params[1] with { Value = "new" }, masked.Params[2]] };
        var merged = HookStore.KeepSecrets(incoming, existing);
        Assert.Equal("ghp_real", merged.AuthToken);
        Assert.Equal("real", merged.Params![0].Value);
        Assert.Equal("new", merged.Params[1].Value);
    }

    [Fact]
    public void A_secret_stays_masked_after_its_mode_changes()
    {
        var existing = new Hook("t", "store", "s", Params: [new("PW", "fixed", "s3cret", true)]);
        var fromUi = HookStore.Mask(existing) with { Params = [new("PW", "required", HookStore.Masked, true)] };

        var merged = HookStore.KeepSecrets(fromUi, existing);

        Assert.Null(merged.Params![0].Value);
        var withLeftover = existing with { Params = [new("PW", "generated", "s3cret", true)] };
        Assert.Equal(HookStore.Masked, HookStore.Mask(withLeftover).Params![0].Value);
    }
}
