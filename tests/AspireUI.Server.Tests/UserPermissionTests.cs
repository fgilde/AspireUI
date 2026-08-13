using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class UserPermissionTests
{
    private static UserStore Store() =>
        new(Path.Combine(Path.GetTempPath(), "aspireui-permtest-" + Guid.NewGuid().ToString("n") + ".db"));

    private static bool MayOpenEditor(User u) => u.IsAdmin || u.Permissions is null || u.Permissions.Contains(Perm.OpenEditor);

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
}
