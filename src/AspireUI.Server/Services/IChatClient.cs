using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public interface IChatClient
{
    Task<string> CompleteAsync(string system, string user, AppSettings s);
}

public class RoutingChatClient(HttpChatClient http, CliChatClient cli) : IChatClient
{
    public Task<string> CompleteAsync(string system, string user, AppSettings s) =>
        string.Equals(s.AiKind, "cli", StringComparison.OrdinalIgnoreCase)
            ? cli.CompleteAsync(system, user, s)
            : http.CompleteAsync(system, user, s);
}

public class CliChatClient : IChatClient
{
    private static readonly Dictionary<string, (string Exe, string[] Args, bool Stdin)> Tools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = ("claude", ["-p"], false),
        ["gemini"] = ("gemini", ["-p"], false),
        ["llm"]    = ("llm", ["-m", "{model}"], false),
        ["ollama"] = ("ollama", ["run", "{model}"], true),
        ["codex"]  = ("codex", ["exec"], false),
    };
    public static IReadOnlyCollection<string> AllowedTools => Tools.Keys;

    public async Task<string> CompleteAsync(string system, string user, AppSettings s)
    {
        var tool = (s.AiCliTool ?? "").Trim();
        if (!Tools.TryGetValue(tool, out var spec))
            throw new InvalidOperationException($"CLI tool '{tool}' is not in the allowed list: {string.Join(", ", Tools.Keys)}.");

        var prompt = $"{system}\n\n{user}";
        var psi = new ProcessStartInfo
        {
            FileName = spec.Exe,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = spec.Stdin,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in spec.Args)
        {
            if (a == "{model}")
            {
                if (string.IsNullOrWhiteSpace(s.AiModel))
                    throw new InvalidOperationException($"CLI tool '{tool}' needs a model set.");
                psi.ArgumentList.Add(s.AiModel);
            }
            else psi.ArgumentList.Add(a);
        }
        if (!spec.Stdin) psi.ArgumentList.Add(prompt);

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) { throw new InvalidOperationException($"Could not launch '{spec.Exe}' — is it installed and on PATH? ({ex.Message})"); }

        if (spec.Stdin) { await proc.StandardInput.WriteAsync(prompt); proc.StandardInput.Close(); }
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw new InvalidOperationException($"'{spec.Exe}' timed out after 120s."); }

        var stdout = await stdoutTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"'{spec.Exe}' exited {proc.ExitCode}: {(await stderrTask).Trim()}");
        return stdout.Trim();
    }

    public async Task<List<string>> ListModelsAsync(AppSettings s)
    {
        var tool = (s.AiCliTool ?? "").Trim().ToLowerInvariant();
        if (tool == "ollama")
        {
            var (code, outp, err) = await RunCaptureAsync("ollama", ["list"]);
            if (code != 0) throw new InvalidOperationException($"'ollama list' failed: {err.Trim()}");
            return outp.Split('\n').Skip(1) // header row
                .Select(l => l.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct().OrderBy(x => x).ToList();
        }
        if (tool == "llm")
        {
            var (code, outp, err) = await RunCaptureAsync("llm", ["models"]);
            if (code != 0) throw new InvalidOperationException($"'llm models' failed: {err.Trim()}");
            return outp.Split('\n').Select(l =>
            {
                var i = l.IndexOf(": ", StringComparison.Ordinal);
                if (i < 0) return null;
                var rest = l[(i + 2)..].Trim();
                var sp = rest.IndexOf(' ');
                return sp > 0 ? rest[..sp] : rest;
            }).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!).Distinct().OrderBy(x => x).ToList();
        }
        return [];
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(string exe, string[] args)
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) { throw new InvalidOperationException($"Could not launch '{exe}' — installed and on PATH? ({ex.Message})"); }
        var outT = proc.StandardOutput.ReadToEndAsync();
        var errT = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw new InvalidOperationException($"'{exe}' timed out."); }
        return (proc.ExitCode, await outT, await errT);
    }
}

public class HttpChatClient(HttpClient http) : IChatClient
{
    // Both spellings are common in the wild: "https://api.openai.com" (no path) and
    // "https://integrate.api.nvidia.com/v1" or ".../v1beta/openai" (version already in the URL).
    // Appending /v1 blindly produced /v1/v1/chat/completions and a 404, so it is only added when the
    // base carries no path of its own.
    public static string ApiRoot(string? baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) return b;
        var path = Uri.TryCreate(b, UriKind.Absolute, out var u) ? u.AbsolutePath.Trim('/') : "";
        return path.Length > 0 ? b : b + "/v1";
    }

    public async Task<string> CompleteAsync(string system, string user, AppSettings s)
    {
        var url = $"{ApiRoot(s.AiBaseUrl)}/chat/completions";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                model = s.AiModel,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
                response_format = new { type = "json_object" },
            }),
        };
        if (!string.IsNullOrEmpty(s.AiApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AiApiKey);

        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) throw Failed(url, s.AiModel, resp);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    // A bare "404 Not Found" says nothing about which URL was called or which model was asked for.
    private static InvalidOperationException Failed(string url, string? model, HttpResponseMessage resp)
    {
        var body = "";
        try { body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(); } catch { }
        if (body.Length > 400) body = body[..400] + "…";
        var hint = resp.StatusCode == System.Net.HttpStatusCode.NotFound
            ? " — check the base URL and whether the model exists at this provider"
            : resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? " — check the API key" : "";
        return new InvalidOperationException(
            $"{(int)resp.StatusCode} {resp.ReasonPhrase} from {url} (model '{model}'){hint}. {body}".Trim());
    }

    public async Task<List<string>> ListModelsAsync(AppSettings s)
    {
        var url = $"{ApiRoot(s.AiBaseUrl)}/models";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(s.AiApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AiApiKey);
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) throw Failed(url, s.AiModel, resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).Where(id => id is not null).Select(id => id!)
            .OrderBy(x => x).ToList();
    }
}
