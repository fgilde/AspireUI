using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// Provider-agnostic: any OpenAI-compatible chat-completions endpoint (baseUrl configurable via
// AppSettings) works, so no vendor SDK is needed. Injectable for tests (fakes implement this
// directly instead of hitting a network).
public interface IChatClient
{
    Task<string> CompleteAsync(string system, string user, AppSettings s);
}

// Routes each call to the HTTP or CLI backend based on AppSettings.AiKind ("cli" → local agent CLI,
// otherwise the OpenAI-compatible HTTP endpoint).
public class RoutingChatClient(HttpChatClient http, CliChatClient cli) : IChatClient
{
    public Task<string> CompleteAsync(string system, string user, AppSettings s) =>
        string.Equals(s.AiKind, "cli", StringComparison.OrdinalIgnoreCase)
            ? cli.CompleteAsync(system, user, s)
            : http.CompleteAsync(system, user, s);
}

// Talks to a locally-installed agent CLI. Whitelist-only: the tool name maps to a FIXED executable and
// arg layout, and the prompt is passed as a single argv element / stdin (never via a shell), so there's
// no command-injection surface. Anything not in the whitelist is rejected.
public class CliChatClient : IChatClient
{
    // tool -> (executable, leading args, prompt via stdin?). {model} is substituted from AppSettings.AiModel.
    private static readonly Dictionary<string, (string Exe, string[] Args, bool Stdin)> Tools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = ("claude", ["-p"], false),          // Claude Code CLI, headless print mode
        ["gemini"] = ("gemini", ["-p"], false),          // Gemini CLI
        ["llm"]    = ("llm", ["-m", "{model}"], false),   // Simon Willison's llm CLI
        ["ollama"] = ("ollama", ["run", "{model}"], true),// local Ollama; prompt on stdin
        ["codex"]  = ("codex", ["exec"], false),          // OpenAI Codex CLI, non-interactive exec
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
        if (!spec.Stdin) psi.ArgumentList.Add(prompt); // prompt as a single (non-shell) argv element

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

    // Best-effort model discovery per tool. ollama/llm expose a list command; claude/gemini/codex use
    // the account's default model (no list) → empty. Never throws for "no list", only for launch errors.
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
            // Lines look like "OpenAI Chat: gpt-4o (aliases: 4o)"; take the id between ": " and " (".
            return outp.Split('\n').Select(l =>
            {
                var i = l.IndexOf(": ", StringComparison.Ordinal);
                if (i < 0) return null;
                var rest = l[(i + 2)..].Trim();
                var sp = rest.IndexOf(' ');
                return sp > 0 ? rest[..sp] : rest;
            }).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!).Distinct().OrderBy(x => x).ToList();
        }
        return []; // claude/gemini/codex: no list command
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
    public async Task<string> CompleteAsync(string system, string user, AppSettings s)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{s.AiBaseUrl!.TrimEnd('/')}/v1/chat/completions")
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
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    // GET {base}/v1/models — the OpenAI-compatible model list (LocalAI/Ollama/OpenAI all expose it).
    public async Task<List<string>> ListModelsAsync(AppSettings s)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{s.AiBaseUrl!.TrimEnd('/')}/v1/models");
        if (!string.IsNullOrEmpty(s.AiApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AiApiKey);
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).Where(id => id is not null).Select(id => id!)
            .OrderBy(x => x).ToList();
    }
}
