using System.Diagnostics;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record PublishResult(bool Ok, string Log, string? ComposeYaml, string? EnvFile, string OutputDir);

// Generates Docker Compose deployment artifacts from a stack using Aspire's own compose publisher.
// Works on a materialized+augmented COPY of the stack (compose-env injected) so the stored model is
// never touched. The process launch goes through an injectable factory so tests never shell `aspire`.
public class PublishService
{
    private const string ComposeEnvName = "aspireui";
    private readonly CodeGenService _gen;
    private readonly Func<string, string, ProcessStartInfo> _commandFactory;

    // factory: (projectCsprojPath, outputDir) -> how to launch the publish. Default = aspire CLI.
    public PublishService(CodeGenService? gen = null, Func<string, string, ProcessStartInfo>? commandFactory = null)
    {
        _gen = gen ?? new CodeGenService();
        _commandFactory = commandFactory ?? DefaultCommand;
    }

    private static ProcessStartInfo DefaultCommand(string csproj, string outDir)
    {
        // ArgumentList (not a hand-quoted Arguments string) so paths carrying a `"` — which the
        // stack-name-derived csproj filename can on Linux — are escaped per-arg and can't inject tokens.
        var psi = new ProcessStartInfo { FileName = "aspire", WorkingDirectory = Path.GetDirectoryName(csproj)! };
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(csproj);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add("--non-interactive");
        return psi;
    }

    public PublishResult Publish(StackModel s, string publishRoot)
    {
        var srcDir = Path.Combine(publishRoot, "src");
        var outDir = Path.Combine(publishRoot, "out");
        Directory.CreateDirectory(outDir);
        _gen.Materialize(s, srcDir, ComposeEnvName);
        var csproj = Directory.GetFiles(srcDir, "*.csproj").FirstOrDefault()
            ?? throw new InvalidOperationException("no csproj materialized");

        var psi = _commandFactory(Path.GetFullPath(csproj), Path.GetFullPath(outDir));
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
                OnLine("Publish timed out after 5 minutes.");
                ok = false;
            }
            else
            {
                proc.WaitForExit(); // flush async readers
                ok = proc.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            OnLine($"Failed to start publish: {ex.Message}");
            ok = false;
        }

        var composePath = Path.Combine(outDir, "docker-compose.yaml");
        var envPath = Path.Combine(outDir, ".env");
        var compose = ok && File.Exists(composePath) ? File.ReadAllText(composePath) : null;
        var env = File.Exists(envPath) ? File.ReadAllText(envPath) : null;
        // Compose artifact is the contract: no yaml means publish didn't actually produce a deployment.
        if (compose is null) ok = false;

        string logText;
        lock (log) logText = string.Join("\n", log);
        return new PublishResult(ok, logText, compose, string.IsNullOrWhiteSpace(env) ? null : env, Path.GetFullPath(outDir));
    }
}
