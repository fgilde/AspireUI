using System.Net.Http.Headers;
using System.Net.Http.Json;
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
}
