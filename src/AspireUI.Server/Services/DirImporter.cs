using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>
/// A directory already filled with source files becomes a stack: an app manifest, a docker-compose
/// file or an Aspire AppHost, whichever it holds. How the directory got filled — a git clone, an
/// upload, a bind mount, a seed — makes no difference here.
/// </summary>
public class DirImporter(ImportService import, ComposeImporter compose)
{
    /// <summary>The compose files named (or the one found), merged, with a .env written for substitution.</summary>
    public static string? MergeComposeYaml(string dir, string[]? files, Dictionary<string, string>? env)
    {
        var paths = files is { Length: > 0 }
            ? files.Select(f => Path.Combine(dir, f)).ToList()
            : (GitService.FindCompose(dir) is { } one ? new List<string> { one } : new List<string>());
        paths = paths.Where(File.Exists).ToList();
        if (paths.Count == 0) return null;
        if (env is { Count: > 0 })
            try { File.WriteAllText(Path.Combine(dir, ".env"), string.Join("\n", env.Select(kv => $"{kv.Key}={kv.Value}"))); } catch { }
        return ComposeImporter.ResolveEnv(ComposeImporter.Merge(paths.Select(File.ReadAllText).ToList()), env);
    }

    /// <summary>AppHost entry point: Program.cs (classic) or AppHost.cs (Aspire 9+), else the first .cs that builds the app.</summary>
    public static string ReadAppHostEntry(string projDir)
    {
        if (!Directory.Exists(projDir)) return "";
        foreach (var name in new[] { "Program.cs", "AppHost.cs" })
        {
            var p = Path.Combine(projDir, name);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        foreach (var cs in Directory.EnumerateFiles(projDir, "*.cs"))
        {
            var text = File.ReadAllText(cs);
            if (text.Contains("CreateBuilder") || text.Contains("DistributedApplication")) return text;
        }
        return "";
    }

    /// <summary>The kind of import a directory asks for when nobody says: manifest, then compose, then AppHost.</summary>
    public static string ModeFor(string dir) =>
        GitService.FindManifest(dir) is not null ? "manifest"
        : GitService.FindComposeFiles(dir).Count > 0 ? "compose" : "apphost";

    public (StackModel? stack, string? error) Build(string sid, string dir, string? mode, string name,
        string[]? files, string[]? services, Dictionary<string, string>? env, Dictionary<string, int>? ports = null)
    {
        var m = string.IsNullOrWhiteSpace(mode) ? ModeFor(dir) : mode!.ToLowerInvariant();
        if (m is "manifest")
        {
            if (GitService.FindManifest(dir) is not { } json) return (null, $"no {GitService.ManifestName} in this repository");
            // No name typed by the user → the app's own label wins over the repo/folder name.
            var (ms, merr) = ManifestImporter.ToStack(sid, string.IsNullOrWhiteSpace(name) ? null : name, json);
            return ms is null ? (null, merr) : (ms, null);
        }
        if (m is "apphost" or "runasis")
        {
            var appHostProject = GitService.FindAppHostRel(dir);
            if (appHostProject is null) return (null, "no .NET Aspire AppHost project found (no .csproj referencing Aspire.Hosting.AppHost)");
            var progDir = Path.Combine(dir, Path.GetDirectoryName(appHostProject.Replace('/', Path.DirectorySeparatorChar)) ?? "");
            var programCs = ReadAppHostEntry(progDir);
            // An imported AppHost runs verbatim (RunAsIs): keep the original files, lock the editor. Nodes are a best-effort
            // parse for display only — real projects use patterns codegen can't round-trip, so we never regenerate over them.
            var s = import.Import(sid, name, programCs, "{}")
                with { RunAsIs = true, AppHostProject = appHostProject, HasSource = true, ExtraFiles = [] };
            return (s, null);
        }
        var yaml = MergeComposeYaml(dir, files, env);
        if (yaml is null) return (null, "no docker-compose file found");
        var (cs, cerr) = compose.Import(sid, name, yaml, services is { Length: > 0 } ? services.ToHashSet() : null, dir, ports);
        return cs is null ? (null, cerr) : (cs with { HasSource = true, ExtraFiles = [] }, null);
    }
}
