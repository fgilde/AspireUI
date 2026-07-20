using System.Diagnostics;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record PublishFile(string Name, string Content);
public record PublishResult(bool Ok, string Log, string? ArtifactName, string? Artifact, string OutputDir, List<PublishFile> Files);

// Generates deployment artifacts from a stack via Aspire's own publishers, on a materialized COPY of
// the stack so the stored model is never touched. Targets:
//   compose    -> Docker Compose environment  -> docker-compose.yaml (+ .env)
//   manifest   -> `dotnet run -- --publisher manifest` -> aspire-manifest.json (portable descriptor)
//   kubernetes -> Kubernetes environment      -> a Helm chart (Chart.yaml, values.yaml, templates/*)
//   bicep      -> Azure Container Apps env     -> main.bicep + per-resource .bicep
// The process launch goes through an injectable factory so tests never shell real tools.
public class PublishService
{
    private readonly CodeGenService _gen;
    private readonly Func<string, string, string, ProcessStartInfo> _commandFactory;

    private record Target(CodeGenService.PublishEnv? Env, string Primary, bool UsesAspireCli);

    // AspireVersion-pinned env packages. Kubernetes is preview-only at 13.4.x; ACA/Docker are stable.
    private static readonly Dictionary<string, Target> Targets = new()
    {
        ["compose"]    = new(new("builder.AddDockerComposeEnvironment(\"aspireui\");", "Aspire.Hosting.Docker", "13.4.6"), "docker-compose.yaml", true),
        ["manifest"]   = new(null, "aspire-manifest.json", false),
        ["kubernetes"] = new(new("builder.AddKubernetesEnvironment(\"k8s\");", "Aspire.Hosting.Kubernetes", "13.4.6-preview.1.26319.6"), "values.yaml", true),
        ["bicep"]      = new(new("builder.AddAzureContainerAppEnvironment(\"aca\");", "Aspire.Hosting.Azure.AppContainers", "13.4.6"), "main.bicep", true),
    };

    public static bool IsTarget(string t) => Targets.ContainsKey(t);

    public PublishService(CodeGenService? gen = null, Func<string, string, string, ProcessStartInfo>? commandFactory = null)
    {
        _gen = gen ?? new CodeGenService();
        _commandFactory = commandFactory ?? DefaultCommand;
    }

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
        var t = Targets.TryGetValue(target, out var td) ? td : Targets["compose"];
        var srcDir = Path.Combine(publishRoot, "src");
        var outDir = Path.Combine(publishRoot, "out");
        Directory.CreateDirectory(outDir);
        _gen.Materialize(s, srcDir, t.Env);
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
            else { proc.WaitForExit(); ok = proc.ExitCode == 0; }
        }
        catch (Exception ex)
        {
            OnLine($"Failed to start publish: {ex.Message}");
            ok = false;
        }

        // Collect every generated file (relative path) for the download bundle; pick the primary to display.
        var files = new List<PublishFile>();
        if (Directory.Exists(outDir))
        {
            foreach (var f in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(outDir, f).Replace('\\', '/');
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > 512 * 1024) { files.Add(new PublishFile(rel, $"[skipped: {fi.Length / 1024} KB]")); continue; }
                    files.Add(new PublishFile(rel, File.ReadAllText(f)));
                }
                catch { /* unreadable/binary — skip */ }
            }
        }
        var primary = files.FirstOrDefault(f => f.Name == t.Primary || f.Name.EndsWith("/" + t.Primary));
        if (primary is null) ok = false;

        string logText;
        lock (log) logText = string.Join("\n", log);
        return new PublishResult(ok, logText, primary?.Name, primary?.Content, Path.GetFullPath(outDir), files);
    }
}
