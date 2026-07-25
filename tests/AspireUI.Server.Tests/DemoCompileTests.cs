using System.Diagnostics;
using AspireUI.Server.Services;

// E2E: generated Program.cs must emit complete usings for Known* enums/methods in AddLocalAI/AddN8n stacks.
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
