using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace AspireUI.Server.Services;

public static class NotifyService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, string> LastState = new();

    private static readonly HashSet<string> Notable = new() { "running", "failed", "stopped" };

    public static bool ShouldNotify(string? prev, string next) => prev is not null && prev != next && Notable.Contains(next);

    public static void OnDeploy(SettingsStore settings, Models.Deployment d)
    {
        var prev = LastState.TryGetValue(d.Id, out var p) ? p : null;
        LastState[d.Id] = d.State;
        if (!ShouldNotify(prev, d.State)) return;
        var (emoji, verb) = d.State switch
        {
            "running" => ("✅", "is running"),
            "failed" => ("❌", "failed"),
            "stopped" => ("⏹️", "stopped"),
            _ => ("ℹ️", d.State),
        };
        var title = $"{emoji} {d.Name} {verb}";
        var detail = d.State == "failed" && !string.IsNullOrWhiteSpace(d.LastError)
            ? "\n\n" + Tail(d.LastError!, 8) : "";
        _ = DispatchAll(settings, title, detail);
    }

    public static async Task<(bool sent, string? error)> DispatchAll(SettingsStore settings, string title, string body = "")
    {
        var url = settings.GetValue("NotifyWebhookUrl");
        var tgToken = settings.GetValue("NotifyTelegramToken");
        var tgChat = settings.GetValue("NotifyTelegramChat");
        var any = false; string? firstError = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            any = true;
            var (ok, err) = await SendAsync(url!, title, body);
            firstError ??= ok ? null : $"webhook: {err}";
        }
        if (!string.IsNullOrWhiteSpace(tgToken) && !string.IsNullOrWhiteSpace(tgChat))
        {
            any = true;
            var (ok, err) = await SendTelegramAsync(tgToken!, tgChat!, title + body);
            firstError ??= ok ? null : $"telegram: {err}";
        }
        return (any, any ? firstError : "no channel configured");
    }

    public static async Task<(bool ok, string? error)> SendTelegramAsync(string token, string chatId, string text)
    {
        try
        {
            var res = await Http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = chatId, text, disable_web_page_preview = true });
            return res.IsSuccessStatusCode ? (true, null) : (false, $"Telegram returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        }
        catch (Exception e) { return (false, e.Message); }
    }

    private static string Tail(string s, int lines)
    {
        var rows = s.Trim().Split('\n');
        return string.Join("\n", rows[^Math.Min(lines, rows.Length)..]);
    }

    public static async Task<(bool ok, string? error)> SendAsync(string url, string title, string body = "")
    {
        try
        {
            var text = title + body;
            object payload = url.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase)
                             || url.Contains("discordapp.com/api/webhooks", StringComparison.OrdinalIgnoreCase)
                ? new { content = text }
                : new { text, title, body };
            var res = await Http.PostAsJsonAsync(url, payload);
            return res.IsSuccessStatusCode ? (true, null) : (false, $"webhook returned {(int)res.StatusCode}");
        }
        catch (Exception e) { return (false, e.Message); }
    }
}
