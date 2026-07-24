using AspireUI.Server.Services;

namespace AspireUI.Server.Tests;

public class SeederTests
{
    // Own temp DB per test: the ":memory:" stores share one fixed-name cache process-wide, which
    // would cross-contaminate these count/lookup assertions.
    private static (UserStore, StackStore, SettingsStore) Stores()
    {
        var db = Path.Combine(Path.GetTempPath(), "aspireui-seedtest-" + Guid.NewGuid().ToString("n") + ".db");
        return (new UserStore(db), new StackStore(db), new SettingsStore(db));
    }

    [Fact]
    public void SeedsAdminAndStack_FromEnv()
    {
        var (users, stacks, settings) = Stores();
        Seeder.Seed(users, stacks, settings, new Dictionary<string, string?>
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
    public void SeedSettings_fills_generic_keys_and_respects_force()
    {
        var (_, _, settings) = Stores();
        Seeder.SeedSettings(settings, new Dictionary<string, string?>
        {
            ["ASPIREUI_SET_NpmBaseUrl"] = "http://npm:81",
            ["ASPIREUI_SET_NpmEnabled"] = "true",
        });
        Assert.Equal("http://npm:81", settings.GetValue("NpmBaseUrl"));
        Assert.Equal("true", settings.GetValue("NpmEnabled"));

        // Without FORCE, an existing value is kept; with FORCE it's overwritten.
        Seeder.SeedSettings(settings, new Dictionary<string, string?> { ["ASPIREUI_SET_NpmBaseUrl"] = "http://other:81" });
        Assert.Equal("http://npm:81", settings.GetValue("NpmBaseUrl"));
        Seeder.SeedSettings(settings, new Dictionary<string, string?> { ["ASPIREUI_SET_NpmBaseUrl"] = "http://other:81", ["ASPIREUI_SET_FORCE"] = "true" });
        Assert.Equal("http://other:81", settings.GetValue("NpmBaseUrl"));
    }

    [Fact]
    public void NoEnv_SeedsNothing()
    {
        var (users, stacks, settings) = Stores();
        Seeder.Seed(users, stacks, settings, new Dictionary<string, string?>());
        Assert.Equal(0, users.Count());
        Assert.Empty(stacks.List());
    }

    [Fact]
    public void SeedsAi_FromEnv_OnlyWhenUnset()
    {
        var (users, stacks, settings) = Stores();
        var env = new Dictionary<string, string?>
        {
            ["ASPIREUI_AI_BASE_URL"] = "http://ollama:11434/v1",
            ["ASPIREUI_AI_MODEL"] = "llama3.2",
        };
        Seeder.Seed(users, stacks, settings, env);
        Assert.Equal("http://ollama:11434/v1", settings.Get().AiBaseUrl);
        Assert.Equal("llama3.2", settings.Get().AiModel);

        // Second run with a different url must NOT override the now-configured install.
        Seeder.Seed(users, stacks, settings, new Dictionary<string, string?>
        { ["ASPIREUI_AI_BASE_URL"] = "http://other/v1", ["ASPIREUI_AI_MODEL"] = "x" });
        Assert.Equal("http://ollama:11434/v1", settings.Get().AiBaseUrl);
    }

    [Fact]
    public void Idempotent_SkipsWhenUserOrStackExists()
    {
        var (users, stacks, settings) = Stores();
        var env = new Dictionary<string, string?>
        {
            ["ASPIREUI_ADMIN_USERNAME"] = "admin",
            ["ASPIREUI_ADMIN_PASSWORD"] = "hunter2hunter",
            ["ASPIREUI_SEED_STACK_NAME"] = "My Stack",
            ["ASPIREUI_SEED_STACK_PROJECTS"] = @"C:\src\Api\Api.csproj",
        };
        Seeder.Seed(users, stacks, settings, env);
        Seeder.Seed(users, stacks, settings, env); // second run must not duplicate

        Assert.Equal(1, users.Count());
        Assert.Single(stacks.List());
    }
}
