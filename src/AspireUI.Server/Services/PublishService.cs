using System.Diagnostics;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record PublishResult(bool Ok, string Log, string? ArtifactName, string? Artifact, string? EnvFile, string OutputDir);

// Generates deployment artifacts from a stack via Aspire's own publishers, on a materialized COPY of
// the stack so the stored model is never touched. Targets:
//   compose  -> inject a Docker Compose environment + `aspire publish` -> docker-compose.yaml (+ .env)
//   manifest -> `dotnet run -- --publisher manifest` -> aspire-manifest.json (portable descriptor)
// The process launch goes through an injectable factory so tests never shell real tools.
public class PublishService
{
    private const string ComposeEnvName = "aspireui";
    private readonly CodeGenService _gen;
    private readonly Func<string, string, string, ProcessStartInfo> _commandFactory;

    // factory: (target, projectCsprojPath, outputDir) -> how to launch the publish.
    public PublishService(CodeGenService? gen = null, Func<string, string, string, ProcessStartInfo>? commandFactory = null)
    {
        _gen = gen ?? new CodeGenService();
        _commandFactory = commandFactory ?? DefaultCommand;
    }

    private static string ArtifactNameFor(string target) => target == "manifest" ? "aspire-manifest.json" : "docker-compose.yaml";

    private static ProcessStartInfo DefaultCommand(string target, string csproj, string outDir)
    {
        // ArgumentList (not a hand-quoted string) so paths with a `"` can't inject tokens.
        var dir = Path.GetDirectoryName(csproj)!;
        if (target == "manifest")
        {
            var m = new ProcessStartInfo { FileName = "dotnet", WorkingDirectory = dir };
            foreach (var a in new[] { "run", "--project", csproj, "--", "--publisher", "manifest", "--output-path", outDir })
                m.ArgumentList.Add(a);
            return m;
        }
        var psi = new ProcessStartInfo { FileName = "aspire", WorkingDirectory = dir };
        foreach (var a in new[] { "publish", "--project", csproj, "-o", outDir, "--non-interactive" })
            psi.ArgumentList.Add(a);
        return psi;
    }

    public PublishResult Publish(StackModel s, string publishRoot, string target = "compose")
    {
        var srcDir = Path.Combine(publishRoot, "src");
        var outDir = Path.Combine(publishRoot, "out");
        Directory.CreateDirectory(outDir);
        _gen.Materialize(s, srcDir, target == "compose" ? ComposeEnvName : null);
        var csproj = Directory.GetFiles(srcDir, "*.csproj").FirstOrDefault()
            ?? throw new InvalidOperationException("no csproj materialized");

        var psi = _commandFactory(target, Path.GetFullPath(csproj), Path.GetFullPath(outDir));
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

        var artifactName = ArtifactNameFor(target);
        var artifactPath = Path.Combine(outDir, artifactName);
        var envPath = Path.Combine(outDir, ".env");
        var artifact = ok && File.Exists(artifactPath) ? File.ReadAllText(artifactPath) : null;
        var env = target == "compose" && File.Exists(envPath) ? File.ReadAllText(envPath) : null;
        // The artifact file is the contract: its absence means the publish didn't actually produce output.
        if (artifact is null) ok = false;

        string logText;
        lock (log) logText = string.Join("\n", log);
        return new PublishResult(ok, logText, artifactName, artifact, string.IsNullOrWhiteSpace(env) ? null : env, Path.GetFullPath(outDir));
    }
}
