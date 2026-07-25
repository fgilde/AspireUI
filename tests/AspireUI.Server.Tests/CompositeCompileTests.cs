using System.Diagnostics;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Tests;

// Composite/macro extensions must emit as bare statement-nodes, compile with referenced resources.
public class CompositeCompileTests
{
    [Fact]
    public void CompositeNode_ReferencingResource_CompilesWithZeroErrors()
    {
        var supabase = new NodeModel("n1", "supabase", "AddSupabase", "supabase", [], 100, 100, []);
        var obs = new NodeModel("n2", "", "AddObservabilityStack", "Observability", [], 400, 100,
            ["supabase"], Composite: true, Usings: ["Nextended.Aspire.Hosting.Observability"]);
        var stack = new StackModel(Guid.NewGuid().ToString("n"), "composite-compile", "net10.0",
            [supabase, obs], [], [], [], []);

        var code = new CodeGenService().GenerateProgram(stack);
        Assert.Contains("using Nextended.Aspire.Hosting.Observability;", code);
        Assert.Contains("var supabase = builder.AddSupabase(\"supabase\");", code);
        Assert.Contains("builder.AddObservabilityStack(supabase);", code);
        Assert.DoesNotContain("var  = builder.AddObservabilityStack", code);

        var dir = Path.Combine(Path.GetTempPath(), "aspireui-composite-" + Guid.NewGuid());
        try
        {
            new CodeGenService().Materialize(stack, dir);
            var psi = new ProcessStartInfo("dotnet", "build")
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            var completed = proc.WaitForExit(TimeSpan.FromMinutes(5));
            Assert.True(completed, "dotnet build did not finish within 5 minutes");
            Assert.True(proc.ExitCode == 0, $"dotnet build failed (exit {proc.ExitCode}):\n{stdout}\n{stderr}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
