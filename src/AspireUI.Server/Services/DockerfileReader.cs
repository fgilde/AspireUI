using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

/// <summary>What a Dockerfile says about the image it produces: the port, the volumes, the settings.</summary>
public record DockerfileInfo(int? Port, List<string> Volumes, List<KeyValuePair<string, string>> Env);

/// <summary>
/// Reads the instructions that matter for running an image, from the last stage only — that is the
/// image a build produces, the stages before it are scaffolding. Nothing here executes anything.
/// </summary>
public static class DockerfileReader
{
    public const string FileName = "Dockerfile";

    // Settings that describe the build or the base image, not the application. They are part of the
    // image already and nobody wants them in an install dialog.
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "LANG", "LANGUAGE", "LC_ALL", "DEBIAN_FRONTEND", "PYTHONUNBUFFERED",
        "PYTHONDONTWRITEBYTECODE", "PIP_NO_CACHE_DIR", "PIP_DISABLE_PIP_VERSION_CHECK", "NODE_VERSION",
        "DOTNET_RUNNING_IN_CONTAINER", "DOTNET_VERSION", "ASPNET_VERSION", "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS",
    };

    public static string? Find(string dir)
    {
        var p = Path.Combine(dir, FileName);
        return File.Exists(p) ? p : null;
    }

    public static DockerfileInfo? Read(string dir) => Find(dir) is { } f ? Parse(File.ReadAllText(f)) : null;

    public static DockerfileInfo Parse(string text)
    {
        var lines = Join(text);
        var lastFrom = lines.FindLastIndex(l => l.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase));
        int? port = null;
        var volumes = new List<string>();
        var env = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(Math.Max(0, lastFrom)))
        {
            var sp = line.IndexOf(' ');
            if (sp < 0) continue;
            var (instr, rest) = (line[..sp].ToUpperInvariant(), line[(sp + 1)..].Trim());
            switch (instr)
            {
                case "EXPOSE":
                    foreach (Match m in Regex.Matches(rest, @"(\d{2,5})(?:/(?:tcp|udp))?"))
                        port ??= int.Parse(m.Groups[1].Value);
                    break;
                case "VOLUME":
                    foreach (var v in ListValues(rest))
                        if (v.StartsWith('/') && !volumes.Contains(v)) volumes.Add(v);
                    break;
                case "ENV":
                    foreach (var (k, v) in EnvPairs(rest))
                    {
                        if (Noise.Contains(k) || k.EndsWith("_VERSION", StringComparison.OrdinalIgnoreCase)) continue;
                        if (v.Contains('$')) continue;
                        if (seen.Add(k)) env.Add(new(k, v));
                    }
                    break;
            }
        }
        return new DockerfileInfo(port, volumes, env);
    }

    // Instructions may continue over several lines with a trailing backslash; comments and blank lines go.
    private static List<string> Join(string text)
    {
        var joined = new List<string>();
        var current = "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.EndsWith('\\'))
            {
                current += line[..^1] + " ";
                continue;
            }
            joined.Add((current + line).Trim());
            current = "";
        }
        if (current.Length > 0) joined.Add(current.Trim());
        return joined;
    }

    // `VOLUME /a /b` or `VOLUME ["/a", "/b"]`.
    private static IEnumerable<string> ListValues(string rest)
    {
        if (rest.StartsWith('['))
            return Regex.Matches(rest, "\"([^\"]+)\"").Select(m => m.Groups[1].Value);
        return rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim('"'));
    }

    // `ENV K=v K2="v 2"` or the older `ENV K v with spaces`.
    private static IEnumerable<(string Key, string Value)> EnvPairs(string rest)
    {
        if (!rest.Contains('='))
        {
            var sp = rest.IndexOf(' ');
            if (sp > 0) yield return (rest[..sp], Unquote(rest[(sp + 1)..]));
            yield break;
        }
        foreach (Match m in Regex.Matches(rest, @"([A-Za-z_][A-Za-z0-9_]*)=(""(?:[^""\\]|\\.)*""|'[^']*'|\S*)"))
            yield return (m.Groups[1].Value, Unquote(m.Groups[2].Value));
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        return v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;
    }
}
