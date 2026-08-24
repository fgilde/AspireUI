using System.Diagnostics;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record PublishFile(string Name, string Content);
public record PublishResult(bool Ok, string Log, string? ArtifactName, string? Artifact, string OutputDir, List<PublishFile> Files);

public class PublishService
{
    private readonly CodeGenService _gen;
    private readonly Func<string, string, string, ProcessStartInfo> _commandFactory;

    private record Target(CodeGenService.PublishEnv? Env, string Primary, bool UsesAspireCli);

    // The publisher packages must move with the rest of Aspire: a stack generated against 13.5 that
    // references a 13.4 publisher fails with MissingMethodException. Versions come from
    // Directory.Packages.props, the same single source codegen uses for everything else.
    private static string Ver(string id, string fallback) =>
        CatalogService.PackageVersions().TryGetValue(id, out var v) ? v : fallback;

    private static readonly Dictionary<string, Target> Targets = new()
    {
        ["compose"]    = new(new("builder.AddDockerComposeEnvironment(\"aspireui\");", "Aspire.Hosting.Docker", Ver("Aspire.Hosting.Docker", "13.5.2")), "docker-compose.yaml", true),
        ["manifest"]   = new(null, "aspire-manifest.json", false),
        ["kubernetes"] = new(new("builder.AddKubernetesEnvironment(\"k8s\");", "Aspire.Hosting.Kubernetes", Ver("Aspire.Hosting.Kubernetes", "13.5.2-preview.1.26421.6")), "values.yaml", true),
        ["bicep"]      = new(new("builder.AddAzureContainerAppEnvironment(\"aca\");", "Aspire.Hosting.Azure.AppContainers", Ver("Aspire.Hosting.Azure.AppContainers", "13.5.2")), "main.bicep", true),
    };

    public static bool IsTarget(string t) => Targets.ContainsKey(t);

    // The publish environment a target adds to a generated stack — also what codegen stamps into the
    // .csproj, which is why it is worth asserting on.
    public static CodeGenService.PublishEnv? EnvFor(string target) =>
        Targets.TryGetValue(target, out var t) ? t.Env : null;

    public PublishService(CodeGenService? gen = null, Func<string, string, string, ProcessStartInfo>? commandFactory = null)
    {
        _gen = gen ?? new CodeGenService();
        _commandFactory = commandFactory ?? DefaultCommand;
    }

    private static ProcessStartInfo DefaultCommand(string target, string csproj, string outDir)
    {
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

    public PublishResult Publish(StackModel s, string publishRoot, string target = "compose", string? cloneSrc = null)
    {
        var t = Targets.TryGetValue(target, out var td) ? td : Targets["compose"];
        var srcDir = Path.Combine(publishRoot, "src");
        var outDir = Path.Combine(publishRoot, "out");
        Directory.CreateDirectory(outDir);
        if (cloneSrc is not null && Directory.Exists(cloneSrc))
        {
            if (Directory.Exists(srcDir)) try { Directory.Delete(srcDir, true); } catch { }
            GitService.CopyTree(cloneSrc, srcDir);
        }
        _gen.Materialize(s, srcDir, t.Env);
        // Run-as-is imports keep their real AppHost in a subfolder; generated stacks put one .csproj at the src root.
        var csproj = s.RunAsIs && !string.IsNullOrEmpty(s.AppHostProject)
            ? Path.Combine(srcDir, s.AppHostProject.Replace('/', Path.DirectorySeparatorChar))
            : Directory.GetFiles(srcDir, "*.csproj").FirstOrDefault();
        if (csproj is null || !File.Exists(csproj))
            throw new InvalidOperationException("no csproj materialized");

        var psi = _commandFactory(target, Path.GetFullPath(csproj), Path.GetFullPath(outDir));
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var log = new List<string>();
        void OnLine(string? line) { if (line is not null) log.Add(line); }

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
                catch { }
            }
        }
        var primary = files.FirstOrDefault(f => f.Name == t.Primary || f.Name.EndsWith("/" + t.Primary));
        if (primary is null) ok = false;

        string logText;
        logText = string.Join("\n", log);
        return new PublishResult(ok, logText, primary?.Name, primary?.Content, Path.GetFullPath(outDir), files);
    }
}
