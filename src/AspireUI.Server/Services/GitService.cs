using System.Diagnostics;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public static class GitService
{
    private static readonly string[] ComposeNames =
        { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" };

    private static readonly HashSet<string> SkipDirs =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "dist", "TestResults" };

    // An app author's own AspireUI manifest, next to their code (same JSON as a store preset).
    public const string ManifestName = "aspireui-app.json";

    public record ComposeFileDto(string Path, string Content);
    public record RepoInfo(bool HasCompose, bool HasAppHost, string? Name, string? Error, List<ComposeFileDto>? ComposeFiles = null,
        string? Manifest = null);

    public static string? FindManifest(string dir)
    {
        var p = Path.Combine(dir, ManifestName);
        return File.Exists(p) ? TryRead(p) : null;
    }

    public static RepoInfo Inspect(string url, string? branch, string? subdir, string? token = null)
    {
        var dir = TempDir();
        try
        {
            if (Clone(url, branch, dir, token) is { } err) return new(false, false, null, err);
            var root = RootOf(dir, subdir);
            if (!Directory.Exists(root)) return new(false, false, null, $"subdir '{subdir}' not found");
            var composeFiles = FindComposeFiles(root)
                .Select(f => new ComposeFileDto(f, TryRead(System.IO.Path.Combine(root, f))))
                .ToList();
            var hasAppHost = FindAppHost(root) is not null;
            return new(composeFiles.Count > 0, hasAppHost, RepoName(url), null, composeFiles, FindManifest(root));
        }
        catch (Exception e) { return new(false, false, null, e.Message); }
        finally { Cleanup(dir); }
    }

    public static List<string> FindComposeFiles(string dir) =>
        !Directory.Exists(dir) ? new() : Directory.EnumerateFiles(dir, "*.y*ml")
            .Where(f => System.IO.Path.GetFileName(f).Contains("compose", StringComparison.OrdinalIgnoreCase))
            .Select(f => System.IO.Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string TryRead(string p) { try { return File.ReadAllText(p); } catch { return ""; } }

    // Clone repo (or its subdir) into destDir as real files (byte-exact, incl. binaries), skipping .git/bin/obj/node_modules.
    public static (string? name, string? error) CloneInto(string url, string? branch, string? subdir, string destDir, string? token = null)
    {
        var tmp = TempDir();
        try
        {
            if (Clone(url, branch, tmp, token) is { } err) return (null, err);
            var root = Path.GetFullPath(RootOf(tmp, subdir));
            if (!Directory.Exists(root)) return (null, $"subdir '{subdir}' not found");
            Directory.CreateDirectory(destDir);
            CopyTree(root, destDir);
            return (RepoName(url), null);
        }
        catch (Exception e) { return (null, e.Message); }
        finally { Cleanup(tmp); }
    }

    public static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SkipDirs.Contains)) continue;
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static string? FindCompose(string dir) => ComposeNames.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);

    public static string? FindAppHostRel(string dir)
    {
        var p = FindAppHost(dir);
        return p is null ? null : Path.GetRelativePath(dir, p).Replace('\\', '/');
    }

    private static string? FindAppHost(string root)
    {
        foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SkipDirs.Contains)) continue;
            var text = File.ReadAllText(csproj);
            if (text.Contains("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<IsAspireHost>true", StringComparison.OrdinalIgnoreCase))
                return csproj;
        }
        return null;
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "aspireui-git-" + Guid.NewGuid().ToString("n")[..8]);
    private static string RootOf(string dir, string? subdir) => string.IsNullOrWhiteSpace(subdir) ? dir : Path.Combine(dir, subdir!.Replace('\\', '/').Trim('/'));
    private static void Cleanup(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

    private static (int code, string log) RunClone(string url, string? branch, string dir)
    {
        var args = new List<string> { "clone", "--depth", "1" };
        if (!string.IsNullOrWhiteSpace(branch)) { args.Add("--branch"); args.Add(branch!); }
        args.Add(url); args.Add(dir);
        return Run("git", args, 120_000);
    }

    private static string? Clone(string url, string? branch, string dir, string? token = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return "no repository URL";
        var (code, log) = RunClone(AuthUrl(url, token), branch, dir);
        if (code == 0) return null;
        if (!url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            Cleanup(dir);
            var (code2, log2) = RunClone(AuthUrl(url + ".git", token), branch, dir);
            if (code2 == 0) return null;
            return $"git clone failed: {Scrub(Trim(log2), token)}";
        }
        return $"git clone failed: {Scrub(Trim(log), token)}";
    }

    // Inject a PAT into an https URL for private-repo access: https://<token>@host/… (left as-is if not https or already has creds).
    private static string AuthUrl(string url, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return url;
        const string https = "https://";
        return url.StartsWith(https, StringComparison.OrdinalIgnoreCase) && !url.Contains('@')
            ? https + Uri.EscapeDataString(token) + "@" + url[https.Length..]
            : url;
    }

    private static string Scrub(string s, string? token) =>
        string.IsNullOrEmpty(token) ? s : s.Replace(token, "***").Replace(Uri.EscapeDataString(token), "***");

    public static (List<string>? branches, string? error) ListBranches(string url, string? token = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, "no repository URL");
        var urls = url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? new[] { url } : new[] { url, url + ".git" };
        foreach (var u in urls)
        {
            var (code, log) = Run("git", new List<string> { "ls-remote", "--heads", AuthUrl(u, token) }, 30_000);
            if (code != 0) continue;
            const string prefix = "refs/heads/";
            var branches = log.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Contains(prefix, StringComparison.Ordinal))
                .Select(l => l[(l.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim())
                .Where(b => b.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (branches, null);
        }
        return (null, "could not list branches (private repo or bad URL?)");
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
