using System.Diagnostics;
using AspireUI.Server.Services;

// Slice 4 Task 7: e2e caught generated Program.cs emitting only `using Aspire.Hosting;`, so any
// stack using AddLocalAI/AddN8n (Known* enums, WithTimezone/WithOwner) failed CS1061/CS0103 in a
// real `dotnet build`. This is the verification that closes the loop: materialize the actual
// "local-ai-demo" template to a temp dir and shell a real `dotnet build` against it. Restoring the
// Aspire/Nextended/Ollama packages can take a while on a cold cache, hence the long timeout.
public class DemoCompileTests
{
    [Fact]
    public void LocalAiDemo_MaterializedProject_CompilesWithZeroErrors()
    {
        var stack = new TemplateService().Create("local-ai-demo")!;
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-democompile-" + Guid.NewGuid());
        try
        {
            new CodeGenService().Materialize(stack, dir);

            var psi = new ProcessStartInfo("dotnet", "build")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            var completed = proc.WaitForExit(TimeSpan.FromMinutes(5));

            Assert.True(completed, "dotnet build did not finish within 5 minutes");
            Assert.True(proc.ExitCode == 0,
                $"dotnet build failed (exit {proc.ExitCode}):\n{stdout}\n{stderr}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
