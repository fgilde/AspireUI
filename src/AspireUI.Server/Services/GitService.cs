using System.Diagnostics;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public static class GitService
{
    private static readonly string[] ComposeNames =
        { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" };

    private static readonly HashSet<string> SkipDirs =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "dist", "TestResults" };
    private static readonly HashSet<string> TextExt =
        new(StringComparer.OrdinalIgnoreCase) { ".cs", ".csproj", ".fsproj", ".vbproj", ".sln", ".props", ".targets", ".json", ".config", ".cshtml", ".razor", ".xml", ".yml", ".yaml", ".txt", ".md", ".sql", ".env", ".sh", ".ps1", ".editorconfig", ".gitignore", ".http", ".resx" };

    public record RepoInfo(bool HasCompose, bool HasAppHost, string? Name, string? Error);

    public static RepoInfo Inspect(string url, string? branch, string? subdir)
    {
        var dir = TempDir();
        try
        {
            if (Clone(url, branch, dir) is { } err) return new(false, false, null, err);
            var root = RootOf(dir, subdir);
            if (!Directory.Exists(root)) return new(false, false, null, $"subdir '{subdir}' not found");
            var hasCompose = ComposeNames.Any(f => File.Exists(Path.Combine(root, f)));
            var hasAppHost = FindAppHost(root) is not null;
            return new(hasCompose, hasAppHost, RepoName(url), null);
        }
        catch (Exception e) { return new(false, false, null, e.Message); }
        finally { Cleanup(dir); }
    }

    public static (List<ExtraFile>? files, string? appHostProject, string? name, string? error) FetchAppHost(string url, string? branch, string? subdir)
    {
        var dir = TempDir();
        try
        {
            if (Clone(url, branch, dir) is { } err) return (null, null, null, err);
            var root = Path.GetFullPath(RootOf(dir, subdir));
            if (!Directory.Exists(root)) return (null, null, null, $"subdir '{subdir}' not found");
            var appHost = FindAppHost(root);
            if (appHost is null) return (null, null, null, "no .NET Aspire AppHost project found (no .csproj referencing Aspire.Hosting.AppHost)");

            var files = new List<ExtraFile>();
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Split('/').Any(seg => SkipDirs.Contains(seg))) continue;
                var name = Path.GetFileName(file);
                if (!TextExt.Contains(Path.GetExtension(file)) && !name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                if (info.Length > 512 * 1024) continue;
                total += info.Length;
                if (total > 25 * 1024 * 1024 || files.Count > 3000) return (null, null, null, "repository is too large to import as-is");
                files.Add(new ExtraFile(rel, File.ReadAllText(file)));
            }
            var appHostRel = Path.GetRelativePath(root, appHost).Replace('\\', '/');
            return (files, appHostRel, RepoName(url), null);
        }
        catch (Exception e) { return (null, null, null, e.Message); }
        finally { Cleanup(dir); }
    }

    private static string? FindAppHost(string root)
    {
        foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SkipDirs.Contains)) continue;
            var text = File.ReadAllText(csproj);
            if (text.Contains("Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<IsAspireHost>true", StringComparison.OrdinalIgnoreCase))
                return csproj;
        }
        return null;
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "aspireui-git-" + Guid.NewGuid().ToString("n")[..8]);
    private static string RootOf(string dir, string? subdir) => string.IsNullOrWhiteSpace(subdir) ? dir : Path.Combine(dir, subdir!.Replace('\\', '/').Trim('/'));
    private static void Cleanup(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

    private static string? Clone(string url, string? branch, string dir)
    {
        if (string.IsNullOrWhiteSpace(url)) return "no repository URL";
        var args = new List<string> { "clone", "--depth", "1" };
        if (!string.IsNullOrWhiteSpace(branch)) { args.Add("--branch"); args.Add(branch!); }
        args.Add(url); args.Add(dir);
        var (code, log) = Run("git", args, 120_000);
        return code == 0 ? null : $"git clone failed: {Trim(log)}";
    }

    // Shallow-clone repo; return compose YAML + suggested name; cleanup on exit.
    public static (string? yaml, string? name, string? error) FetchCompose(string url, string? branch, string? subdir = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, null, "no repository URL");
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-git-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            var args = new List<string> { "clone", "--depth", "1" };
            if (!string.IsNullOrWhiteSpace(branch)) { args.Add("--branch"); args.Add(branch!); }
            args.Add(url); args.Add(dir);
            var (code, log) = Run("git", args, 120_000);
            if (code != 0) return (null, null, $"git clone failed: {Trim(log)}");

            var root = string.IsNullOrWhiteSpace(subdir) ? dir : Path.Combine(dir, subdir!.Replace('\\', '/').Trim('/'));
            var compose = ComposeNames.Select(f => Path.Combine(root, f)).FirstOrDefault(File.Exists);
            if (compose is null) return (null, null, $"no docker-compose file found{(subdir is null ? " in the repo root" : $" in '{subdir}'")}");
            return (File.ReadAllText(compose), RepoName(url), null);
        }
        catch (Exception e) { return (null, null, e.Message); }
        finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { } }
    }

    // Extract repo name from URL (e.g., github.com/.../immich.git → immich).
    public static string RepoName(string url)
    {
        var last = url.TrimEnd('/').Split('/').LastOrDefault() ?? "git-app";
        return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
    }

    private static string Trim(string s) => s.Length > 600 ? s[^600..] : s;

    private static (int code, string log) Run(string file, List<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (-1, "could not start git");
            var outp = p.StandardOutput.ReadToEndAsync();
            var errp = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return (-1, "git timed out"); }
            return (p.ExitCode, (outp.Result + errp.Result).Trim());
        }
        catch (Exception e) { return (-1, e.Message); }
    }
}
