using System.Diagnostics;

namespace AspireUI.Server.Services;

public record ToolStatus(bool Ok, string Detail);
public record EnvHealthResult(ToolStatus Dotnet, ToolStatus Docker, ToolStatus Git);

public class EnvHealth
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Fixed commands only, never user input.
    public async Task<EnvHealthResult> CheckAsync()
    {
        var dotnet = await RunAsync("dotnet", "--version");
        var docker = await RunAsync("docker", "info");
        // git is needed by AddGithubRepository resources (Aspire clones the repo at run time).
        var git = await RunAsync("git", "--version");
        return new EnvHealthResult(dotnet, docker, git);
    }

    private static async Task<ToolStatus> RunAsync(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new ToolStatus(false, "failed to start");

            using var cts = new CancellationTokenSource(Timeout);
            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* best effort */ }
                return new ToolStatus(false, "timed out");
            }

            return proc.ExitCode == 0
                ? new ToolStatus(true, stdout.Trim().Split('\n')[0].Trim())
                : new ToolStatus(false, "exit code " + proc.ExitCode);
        }
        catch (Exception ex)
        {
            return new ToolStatus(false, ex.Message);
        }
    }
}
