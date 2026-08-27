using AspireUI.Server.Services;

namespace AspireUI.Server.Tests;

// The base URL a user pastes may or may not carry the version segment. Appending /v1 blindly made
// "https://integrate.api.nvidia.com/v1" into ".../v1/v1/chat/completions", which every provider
// answers with 404.
public class AiBaseUrlTests
{
    [Theory]
    // no path of its own → the version is ours to add
    [InlineData("https://api.openai.com", "https://api.openai.com/v1")]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/v1")]
    // the version (or a provider path) is already there → leave it alone
    [InlineData("https://integrate.api.nvidia.com/v1", "https://integrate.api.nvidia.com/v1")]
    [InlineData("https://integrate.api.nvidia.com/v1/", "https://integrate.api.nvidia.com/v1")]
    [InlineData("http://ollama:11434/v1", "http://ollama:11434/v1")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "https://generativelanguage.googleapis.com/v1beta/openai")]
    [InlineData("https://my-resource.openai.azure.com/openai/deployments/gpt4", "https://my-resource.openai.azure.com/openai/deployments/gpt4")]
    public void The_version_segment_is_added_only_when_it_is_missing(string input, string expected)
        => Assert.Equal(expected, HttpChatClient.ApiRoot(input));

    [Fact]
    public void An_empty_base_url_stays_empty_so_the_caller_can_complain_about_it()
    {
        Assert.Equal("", HttpChatClient.ApiRoot(null));
        Assert.Equal("", HttpChatClient.ApiRoot("   "));
    }
}
