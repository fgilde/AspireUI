using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class UserPermissionTests
{
    private static UserStore Store() =>
        new(Path.Combine(Path.GetTempPath(), "aspireui-permtest-" + Guid.NewGuid().ToString("n") + ".db"));

    private static bool MayOpenEditor(User u) => Perm.Has(u, Perm.OpenEditor);

    [Fact]
    public void A_new_user_keeps_every_permission()
    {
        var users = Store();
        var u = users.FindByUsername(users.Create("dev", "hash", isAdmin: false).Username)!;
        Assert.Null(u.Permissions);
        Assert.True(MayOpenEditor(u));
    }

    [Fact]
    public void Clearing_the_permissions_revokes_the_editor()
    {
        var users = Store();
        var created = users.Create("appuser", "hash", isAdmin: false);
        users.SetPermissions(created.Id, new List<string>());

        var u = users.Get(created.Id)!;
        Assert.NotNull(u.Permissions);
        Assert.Empty(u.Permissions!);
        Assert.False(MayOpenEditor(u));
    }

    [Fact]
    public void Granting_the_editor_permission_round_trips()
    {
        var users = Store();
        var created = users.Create("builder", "hash", isAdmin: false);
        users.SetPermissions(created.Id, new List<string> { Perm.OpenEditor });

        var u = users.Get(created.Id)!;
        Assert.Equal(new[] { Perm.OpenEditor }, u.Permissions);
        Assert.True(MayOpenEditor(u));
    }

    [Fact]
    public void View_modes_default_to_both_when_never_set()
    {
        var users = Store();
        var created = users.Create("dev", "hash", isAdmin: false);
        Assert.Null(users.Get(created.Id)!.ViewModes);

        users.SetViewModes(created.Id, new List<string> { "simple" });
        Assert.Equal(new[] { "simple" }, users.Get(created.Id)!.ViewModes);
    }

    [Fact]
    public void A_disabled_user_has_no_permission_left()
    {
        var users = Store();
        var created = users.Create("gone", "hash", isAdmin: false);
        users.SetPermissions(created.Id, Perm.All.ToList());
        users.SetDisabled(created.Id, true);

        var u = users.Get(created.Id)!;
        Assert.All(Perm.All, p => Assert.False(Perm.Has(u, p)));
    }

    [Fact]
    public void An_admin_has_every_permission_whatever_the_list_says()
    {
        var users = Store();
        var created = users.Create("boss", "hash", isAdmin: true);
        users.SetPermissions(created.Id, new List<string>());

        var u = users.Get(created.Id)!;
        Assert.All(Perm.All, p => Assert.True(Perm.Has(u, p)));
    }

    [Fact]
    public void The_presets_only_contain_real_permissions()
    {
        Assert.All(Perm.Operator, p => Assert.Contains(p, Perm.All));
        Assert.All(Perm.Viewer, p => Assert.Contains(p, Perm.All));
        Assert.DoesNotContain(Perm.Users, Perm.Operator);
        Assert.DoesNotContain(Perm.Settings, Perm.Operator);
        Assert.Equal(Perm.All.Length, Perm.All.Distinct().Count());
    }

    [Fact]
    public void Nobody_is_nothing()
    {
        Assert.All(Perm.All, p => Assert.False(Perm.Has(null, p)));
    }
}
