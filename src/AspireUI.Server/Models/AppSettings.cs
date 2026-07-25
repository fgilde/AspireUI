namespace AspireUI.Server.Models;

public record AppSettings(string? AiBaseUrl, string? AiApiKey, string? AiModel, string? AiProviderLabel,
    string? AiKind = null, string? AiCliTool = null);
