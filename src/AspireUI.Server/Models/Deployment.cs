namespace AspireUI.Server.Models;

public record Deployment(string Id, string StackId, string Name, string ComposeDir, string Project,
    string State, List<string> Urls, string CreatedAt, string UpdatedAt, string? LastError,
    List<PortMapping>? Ports = null, List<string>? Domains = null);

public record PortMapping(int Container, int Host, bool Public);

public record BackupInfo(string Stamp, string CreatedAt, List<BackupVol> Volumes);
public record BackupVol(string Name, long Size);
