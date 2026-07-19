using System.Diagnostics;

namespace AspireUI.Server.Services;

public record DeployResult(bool Ok, string Log);

// Runs `docker compose up -d` / `down` in a previously-published output directory. Process launch
// goes through an injectable factory so tests never shell real docker.
public class DeployService
{
    private readonly Func<string, string, ProcessStartInfo> _commandFactory;

    // factory: (workdir, dockerArgs) -> how to launch docker.
    public DeployService(Func<string, string, ProcessStartInfo>? commandFactory = null)
        => _commandFactory = commandFactory ?? DefaultCommand;

    private static ProcessStartInfo DefaultCommand(string workdir, string args) => new()
    {
        FileName = "docker",
        Arguments = args,
        WorkingDirectory = workdir,
    };

    public DeployResult Up(string outputDir) => Run(outputDir, "compose up -d");
    public DeployResult Down(string outputDir) => Run(outputDir, "compose down");

    private DeployResult Run(string workdir, string args)
    {
        var psi = _commandFactory(workdir, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var log = new List<string>();
        void OnLine(string? line) { if (line is not null) lock (log) log.Add(line); }

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);

        bool ok;
        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(300_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                OnLine("docker compose timed out after 5 minutes.");
                ok = false;
            }
            else { proc.WaitForExit(); ok = proc.ExitCode == 0; }
        }
        catch (Exception ex)
        {
            OnLine($"Failed to run docker: {ex.Message}");
            ok = false;
        }

        string logText;
        lock (log) logText = string.Join("\n", log);
        return new DeployResult(ok, logText);
    }
}
