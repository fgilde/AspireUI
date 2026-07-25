using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class DeploymentStoreTests
{
    private static DeploymentStore NewStore() => new(":memory:");

    [Fact]
    public void Upsert_then_GetByStack_roundtrips()
    {
        var s = NewStore();
        var d = new Deployment("d1", "stack1", "Demo", "/c/dir", "aspireui-stack1", "running",
            new() { "http://localhost:8096" }, "2026-07-23T00:00:00Z", "2026-07-23T00:00:00Z", null);
        s.Upsert(d);
        var got = s.GetByStack("stack1");
        Assert.NotNull(got);
        Assert.Equal("running", got!.State);
        Assert.Equal("aspireui-stack1", got.Project);
        Assert.Single(got.Urls);
    }

    [Fact]
    public void GetByStack_is_unique_last_write_wins()
    {
        var s = NewStore();
        s.Upsert(new Deployment("d1", "stack1", "A", "/c", "p", "deploying", new(), "t", "t", null));
        s.Upsert(new Deployment("d1", "stack1", "A", "/c", "p", "running", new(), "t", "t2", null));
        Assert.Equal("running", s.GetByStack("stack1")!.State);
        Assert.Single(s.List());
    }

    [Fact]
    public void SetState_updates_state_and_error()
    {
        var s = NewStore();
        s.Upsert(new Deployment("d1", "stack1", "A", "/c", "p", "deploying", new(), "t", "t", null));
        s.SetState("d1", "failed", "boom");
        Assert.Equal("failed", s.Get("d1")!.State);
        Assert.Equal("boom", s.Get("d1")!.LastError);
    }

    [Fact]
    public void Delete_removes()
    {
        var s = NewStore();
        s.Upsert(new Deployment("d1", "stack1", "A", "/c", "p", "running", new(), "t", "t", null));
        Assert.True(s.Delete("d1"));
        Assert.Null(s.GetByStack("stack1"));
    }
}
