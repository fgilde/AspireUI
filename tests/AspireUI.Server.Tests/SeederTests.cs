using AspireUI.Server.Services;

namespace AspireUI.Server.Tests;

public class SeederTests
{
    // Own temp DB per test: the ":memory:" stores share one fixed-name cache process-wide, which
    // would cross-contaminate these count/lookup assertions.
    private static (UserStore, StackStore) Stores()
    {
        var db = Path.Combine(Path.GetTempPath(), "aspireui-seedtest-" + Guid.NewGuid().ToString("n") + ".db");
        return (new UserStore(db), new StackStore(db));
    }

    [Fact]
    public void SeedsAdminAndStack_FromEnv()
    {
        var (users, stacks) = Stores();
        Seeder.Seed(users, stacks, new Dictionary<string, string?>
        {
            ["ASPIREUI_ADMIN_USERNAME"] = "admin",
            ["ASPIREUI_ADMIN_PASSWORD"] = "hunter2hunter",
            ["ASPIREUI_SEED_STACK_NAME"] = "My Stack",
            ["ASPIREUI_SEED_STACK_PROJECTS"] = @"C:\src\Api\Api.csproj; C:\src\Worker\Worker.csproj",
        });

        var admin = users.FindByUsername("admin");
        Assert.NotNull(admin);
        Assert.True(admin!.IsAdmin);
        Assert.NotEqual("hunter2hunter", admin.PasswordHash); // stored hashed, never plaintext

        var stack = Assert.Single(stacks.List());
        Assert.Equal("My Stack", stack.Name);
        Assert.Equal(2, stack.Nodes.Count);
        Assert.All(stack.Nodes, n => Assert.Equal("AddProject", n.AddMethod));
        Assert.Contains(stack.Nodes, n => n.AddArgs.Single().Contains("Api.csproj"));
    }

    [Fact]
    public void NoEnv_SeedsNothing()
    {
        var (users, stacks) = Stores();
        Seeder.Seed(users, stacks, new Dictionary<string, string?>());
        Assert.Equal(0, users.Count());
        Assert.Empty(stacks.List());
    }

    [Fact]
    public void Idempotent_SkipsWhenUserOrStackExists()
    {
        var (users, stacks) = Stores();
        var env = new Dictionary<string, string?>
        {
            ["ASPIREUI_ADMIN_USERNAME"] = "admin",
            ["ASPIREUI_ADMIN_PASSWORD"] = "hunter2hunter",
            ["ASPIREUI_SEED_STACK_NAME"] = "My Stack",
            ["ASPIREUI_SEED_STACK_PROJECTS"] = @"C:\src\Api\Api.csproj",
        };
        Seeder.Seed(users, stacks, env);
        Seeder.Seed(users, stacks, env); // second run must not duplicate

        Assert.Equal(1, users.Count());
        Assert.Single(stacks.List());
    }
}
