namespace AspireUI.Server.Models;

public record Deployment(string Id, string StackId, string Name, string ComposeDir, string Project,
    string State, List<string> Urls, string CreatedAt, string UpdatedAt, string? LastError,
    List<PortMapping>? Ports = null, List<string>? Domains = null,
    string? Health = null, string? HealthDetail = null,
    // Where this runs. Null means "local" — which is also every deployment made before targets existed.
    string? TargetId = null,
    // Filled in when a deployment is read for the UI, never stored.
    string? TargetName = null, string? TargetKind = null, bool? TargetCompose = null)
{
    public string Target => string.IsNullOrWhiteSpace(TargetId) ? DeployTarget.LocalId : TargetId!;
}

public record PortMapping(int Container, int Host, bool Public);

public record BackupInfo(string Stamp, string CreatedAt, List<BackupVol> Volumes);
public record BackupVol(string Name, long Size);
