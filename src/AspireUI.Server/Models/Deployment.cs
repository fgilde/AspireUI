namespace AspireUI.Server.Models;

// A stack deployed persistently into hosting (a long-lived docker-compose project), tracked so it
// survives AppHost restarts and can be managed. State: deploying|running|stopped|failed.
public record Deployment(string Id, string StackId, string Name, string ComposeDir, string Project,
    string State, List<string> Urls, string CreatedAt, string UpdatedAt, string? LastError);
