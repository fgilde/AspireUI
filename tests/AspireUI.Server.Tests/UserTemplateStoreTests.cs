using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class UserTemplateStoreTests
{
    private static StackModel Stack() => new("s1", "My Stack", "net10.0",
        [new NodeModel("n1", "db", "AddPostgres", "db", [], 0, 0, [])], [], [], [], []);

    private static UserTemplateStore Store()
    {
        var db = Path.Combine(Path.GetTempPath(), "aspireui-tmpltest-" + Guid.NewGuid().ToString("n") + ".db");
        return new UserTemplateStore(db);
    }

    [Fact]
    public void Save_List_Get_RoundTrips()
    {
        var store = Store();
        store.Save("t1", "Tmpl", "desc", Stack());
        var e = Assert.Single(store.List());
        Assert.Equal("Tmpl", e.Name);
        Assert.Equal("desc", e.Description);
        Assert.Equal("My Stack", store.Get("t1")!.Name);
        Assert.Single(store.Get("t1")!.Nodes);
    }

    [Fact]
    public void Delete_Removes()
    {
        var store = Store();
        store.Save("t1", "Tmpl", "", Stack());
        Assert.True(store.Delete("t1"));
        Assert.Empty(store.List());
        Assert.False(store.Delete("t1"));
    }
}
