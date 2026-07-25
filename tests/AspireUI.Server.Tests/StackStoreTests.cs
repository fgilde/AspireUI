using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class StackStoreTests
{
    [Fact]
    public void SaveGet_RoundTrips()
    {
        var store = new StackStore(":memory:");
        var s = new StackModel("s1", "demo", "net9.0",
            [new NodeModel("n1", "db", "AddPostgres", "db", [], 10, 20, [])],
            [], [], [], []);
        store.Save(s);
        var got = store.Get("s1");
        Assert.Equal("demo", got!.Name);
        Assert.Single(got.Nodes);
        Assert.Equal("db", got.Nodes[0].VarName);
    }

    [Fact]
    public void Delete_Removes()
    {
        var store = new StackStore(":memory:");
        store.Save(new StackModel("s1", "d", "net9.0", [], [], [], [], []));
        store.Delete("s1");
        Assert.Null(store.Get("s1"));
    }
}
