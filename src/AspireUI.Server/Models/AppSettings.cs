namespace AspireUI.Server.Models;

// AiKind selects the assistant backend: "http" (default) = any OpenAI-compatible chat endpoint;
// "cli" = a locally-installed agent CLI (AiCliTool names one from a fixed whitelist, see CliChatClient).
public record AppSettings(string? AiBaseUrl, string? AiApiKey, string? AiModel, string? AiProviderLabel,
    string? AiKind = null, string? AiCliTool = null);
