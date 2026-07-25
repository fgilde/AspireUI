using System.Diagnostics;

namespace AspireUI.Server.Services;

// Deploy-from-Git: shallow-clone public repo + extract docker-compose; ponytail: public/compose only.
public static class GitService
{
    private static readonly string[] ComposeNames =
        { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" };

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
