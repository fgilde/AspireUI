using AspireUI.Server.Services;

public class HaVeWaPresetTests
{
    [Fact]
    public void A_fresh_install_leaves_the_admin_to_the_setup_wizard()
    {
        var p = new CatalogService().GetPresets().Single(x => x.Id == "havewa");
        var admin = p.Params!.Where(x => x.Key is "admin-email" or "admin-password").ToList();
        Assert.Equal(2, admin.Count);
        Assert.All(admin, x => Assert.Equal("", PresetBuilder.ParamDefault(x)));
    }
}
