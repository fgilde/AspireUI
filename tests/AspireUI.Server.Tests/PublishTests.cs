using System.Diagnostics;
using System.Net;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class PublishServiceTests
{
    private static StackModel Redis() => new("s1", "Demo", "net10.0",
        [new NodeModel("n1", "cache", "AddRedis", "cache", [], 0, 0, [])], [], [], [], []);

    // A cross-platform no-op process; the stub writes the fake artifacts before returning it.
    private static ProcessStartInfo NoOp() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c exit 0" }
        : new ProcessStartInfo { FileName = "/bin/sh", Arguments = "-c \"exit 0\"" };

    private static ProcessStartInfo Fail() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c exit 1" }
        : new ProcessStartInfo { FileName = "/bin/sh", Arguments = "-c \"exit 1\"" };

    [Fact]
    public void Publish_Success_ReadsArtifacts_AndAugmentsSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-pub-" + Guid.NewGuid().ToString("n"));
        var svc = new PublishService(commandFactory: (_, _, outDir) =>
        {
            // Emulate aspire publish: emit the compose artifacts into the output dir.
            File.WriteAllText(Path.Combine(outDir, "docker-compose.yaml"), "services:\n  cache: {}\n");
            File.WriteAllText(Path.Combine(outDir, ".env"), "CACHE_PASSWORD=\n");
            return NoOp();
        });

        var r = svc.Publish(Redis(), root);

        Assert.True(r.Ok);
        Assert.Equal("docker-compose.yaml", r.ArtifactName);
        Assert.Contains("services:", r.Artifact);
        Assert.Contains(r.Files, f => f.Name == ".env" && f.Content.Contains("CACHE_PASSWORD"));
        // The augmented copy carries the compose env + docker package (stored stack untouched).
        Assert.Contains("AddDockerComposeEnvironment", File.ReadAllText(Path.Combine(root, "src", "Program.cs")));
        Assert.Contains("Aspire.Hosting.Docker",
            File.ReadAllText(Directory.GetFiles(Path.Combine(root, "src"), "*.csproj").Single()));

        Directory.Delete(root, true);
    }

    [Fact]
    public void Publish_NoComposeArtifact_IsFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-pub-" + Guid.NewGuid().ToString("n"));
        // Exit 0 but produce nothing -> not a real deployment.
        var svc = new PublishService(commandFactory: (_, _, _) => NoOp());
        var r = svc.Publish(Redis(), root);
        Assert.False(r.Ok);
        Assert.Null(r.Artifact);
        Directory.Delete(root, true);
    }

    [Fact]
    public void Publish_NonZeroExit_IsFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-pub-" + Guid.NewGuid().ToString("n"));
        var svc = new PublishService(commandFactory: (_, _, _) => Fail());
        var r = svc.Publish(Redis(), root);
        Assert.False(r.Ok);
        Directory.Delete(root, true);
    }
}

public class DeployServiceTests
{
    private static ProcessStartInfo Exit(int code) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/c exit {code}" }
        : new ProcessStartInfo { FileName = "/bin/sh", Arguments = $"-c \"exit {code}\"" };

    [Fact]
    public void Up_Success_CapturesLog_AndBuildsComposeArgs()
    {
        string? seenArgs = null;
        var svc = new DeployService((_, args) => { seenArgs = args; return Exit(0); });
        var r = svc.Up("/tmp/whatever");
        Assert.True(r.Ok);
        Assert.Equal("compose up -d", seenArgs);
    }

    [Fact]
    public void Down_BuildsComposeDownArgs()
    {
        string? seenArgs = null;
        var svc = new DeployService((_, args) => { seenArgs = args; return Exit(0); });
        svc.Down("/tmp/whatever");
        Assert.Equal("compose down", seenArgs);
    }

    [Fact]
    public void Up_NonZeroExit_IsFailure()
    {
        var svc = new DeployService((_, _) => Exit(1));
        Assert.False(svc.Up("/tmp/whatever").Ok);
    }
}

[Collection("ServerIntegration")]
public class PublishEndpointTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _c;
    public PublishEndpointTests(TestWebAppFactory f) { _c = f.CreateClient(); }

    [Fact]
    public async Task Publish_UnknownStack_404()
    {
        var r = await _c.PostAsync("/stacks/does-not-exist/publish", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Deploy_UnpublishedStack_409()
    {
        var r = await _c.PostAsync("/stacks/never-published/deploy", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }
}

[Collection("ServerIntegration")]
public class PublishAuthTests : IClassFixture<NoAuthTestFactory>
{
    private readonly HttpClient _c;
    public PublishAuthTests(NoAuthTestFactory f) { _c = f.CreateClient(); }

    [Fact]
    public async Task Publish_WithoutAuth_401()
    {
        var r = await _c.PostAsync("/stacks/x/publish", null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}
