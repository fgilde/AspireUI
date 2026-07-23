using System.Diagnostics;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

// Guards the class of bug where a companion-preset generated code that didn't compile (a plain
// container builder passed to WithReference/WithEnvironment). Mirrors what the frontend
// buildPresetNodes emits — AddContainer + WithHttpEndpoint + WithEnvironment("K","hostname") + a
// waitFor edge — and does a REAL `dotnet build` to prove the shape is semantically valid.
// (Runtime/functional correctness of each app — volumes, exact env — is a scaffold, not auto-tested.)
public class PresetCompileTests
{
    [Fact]
    public void CompanionPresetShape_CompilesWithZeroErrors()
    {
        var main = new NodeModel("n1", "immich", "AddContainer", "immich",
            [
                new WithCall("WithHttpEndpoint", ["targetPort: 2283"]),
                new WithCall("WithEnvironment", ["\"DB_HOSTNAME\"", "\"immich-db\""]),   // companion resource name, quoted
                new WithCall("WithEnvironment", ["\"REDIS_HOSTNAME\"", "\"immich-redis\""]),
                // urlPath: proves the WithUrlForEndpoint call buildPresetNodes emits actually compiles.
                new WithCall("WithUrlForEndpoint", ["\"http\"", "url => url.Url = \"/web\""]),
            ], 60, 60, ["\"ghcr.io/immich-app/immich-server:release\""]);
        var db = new NodeModel("n2", "immichdb", "AddContainer", "immich-db",
            [new WithCall("WithEnvironment", ["\"POSTGRES_PASSWORD\"", "\"immich\""])], 380, 40,
            ["\"tensorchord/pgvecto-rs:pg16-v0.2.0\""]);
        var redis = new NodeModel("n3", "immichredis", "AddContainer", "immich-redis",
            [new WithCall("WithHttpEndpoint", ["targetPort: 6379"])], 380, 170, ["\"redis:7\""]);
        var stack = new StackModel(Guid.NewGuid().ToString("n"), "preset-compile", "net10.0",
            [main, db, redis],
            [new EdgeModel("e1", "n1", "n2", "waitFor"), new EdgeModel("e2", "n1", "n3", "waitFor")],
            [], [], []);

        var code = new CodeGenService().GenerateProgram(stack);
        Assert.Contains("builder.AddContainer(\"immich\", \"ghcr.io/immich-app/immich-server:release\")", code);
        Assert.Contains("immich.WithEnvironment(\"DB_HOSTNAME\", \"immich-db\")", code);
        Assert.Contains("immich.WaitFor(", code);
        Assert.Contains(".WithUrlForEndpoint(\"http\", url => url.Url = \"/web\")", code);
        Assert.DoesNotContain(".WithReference(", code); // plain containers must not be WithReference'd

        var dir = Path.Combine(Path.GetTempPath(), "aspireui-preset-" + Guid.NewGuid());
        try
        {
            new CodeGenService().Materialize(stack, dir);
            var psi = new ProcessStartInfo("dotnet", "build") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEnd(); var se = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(TimeSpan.FromMinutes(5)), "build timed out");
            Assert.True(p.ExitCode == 0, $"generated preset code failed to build:\n{so}\n{se}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
