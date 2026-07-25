using System.Net.Http.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;

public class SettingsStoreTests
{
    [Fact]
    public void SaveGet_RoundTrips()
    {
        var store = new SettingsStore(":memory:");
        store.Save(new AppSettings("https://api.example.com", "sk-secret", "gpt-4", "OpenAI"));

        var got = store.Get();

        Assert.Equal("https://api.example.com", got.AiBaseUrl);
        Assert.Equal("sk-secret", got.AiApiKey);
        Assert.Equal("gpt-4", got.AiModel);
        Assert.Equal("OpenAI", got.AiProviderLabel);
    }

    [Fact]
    public void Get_WhenNothingSaved_ReturnsAllNulls()
    {
        var store = new SettingsStore(":memory:");
        var got = store.Get();
        Assert.Null(got.AiBaseUrl);
        Assert.Null(got.AiApiKey);
        Assert.Null(got.AiModel);
        Assert.Null(got.AiProviderLabel);
    }
}

[Collection("ServerIntegration")]
public class SettingsEndpointTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _c;
    public SettingsEndpointTests(TestWebAppFactory f) => _c = f.CreateClient();

    [Fact]
    public async Task Get_WithNoKeyStored_ReturnsNullKey()
    {
        // Ensure a clean slate for this key regardless of test order.
        await _c.PutAsJsonAsync("/api/settings", new AppSettings("", "", "", ""));

        var got = await _c.GetFromJsonAsync<AppSettings>("/api/settings");
        Assert.True(string.IsNullOrEmpty(got!.AiApiKey));
    }

    [Fact]
    public async Task Put_ThenGet_MasksStoredKey()
    {
        var put = await _c.PutAsJsonAsync("/api/settings",
            new AppSettings("https://api.example.com", "sk-real-secret", "gpt-4", "OpenAI"));
        put.EnsureSuccessStatusCode();

        var got = await _c.GetFromJsonAsync<AppSettings>("/api/settings");

        Assert.Equal("***", got!.AiApiKey);
        Assert.Equal("https://api.example.com", got.AiBaseUrl);
        Assert.Equal("gpt-4", got.AiModel);
    }

    [Fact]
    public async Task Put_WithMaskedPlaceholder_KeepsStoredKey()
    {
        await _c.PutAsJsonAsync("/api/settings",
            new AppSettings("https://api.example.com", "sk-original", "gpt-4", "OpenAI"));

        // Simulate the UI re-submitting the masked placeholder unchanged.
        var put = await _c.PutAsJsonAsync("/api/settings",
            new AppSettings("https://api.example.com", "***", "gpt-4-turbo", "OpenAI"));
        put.EnsureSuccessStatusCode();

        var got = await _c.GetFromJsonAsync<AppSettings>("/api/settings");
        Assert.Equal("***", got!.AiApiKey); // still masked = still stored
        Assert.Equal("gpt-4-turbo", got.AiModel); // other fields still updated
    }

    [Fact]
    public async Task Put_WithEmptyKey_ClearsStoredKey()
    {
        await _c.PutAsJsonAsync("/api/settings",
            new AppSettings("https://api.example.com", "sk-original", "gpt-4", "OpenAI"));

        var put = await _c.PutAsJsonAsync("/api/settings",
            new AppSettings("https://api.example.com", "", "gpt-4", "OpenAI"));
        put.EnsureSuccessStatusCode();

        var got = await _c.GetFromJsonAsync<AppSettings>("/api/settings");
        Assert.True(string.IsNullOrEmpty(got!.AiApiKey));
    }
}
