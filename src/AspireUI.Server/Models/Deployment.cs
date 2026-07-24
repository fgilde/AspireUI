namespace AspireUI.Server.Models;

// A stack deployed persistently into hosting (a long-lived docker-compose project), tracked so it
// survives AppHost restarts and can be managed. State: deploying|running|stopped|failed.
// Ports: the container→host port map chosen at deploy, persisted so it stays STABLE across redeploys
// (previously re-randomized every time) and so the user can pin a host port or mark a port internal.
public record Deployment(string Id, string StackId, string Name, string ComposeDir, string Project,
    string State, List<string> Urls, string CreatedAt, string UpdatedAt, string? LastError,
    List<PortMapping>? Ports = null);

// One exposed container port and how it's published: Host is the host port when Public; when !Public the
// port stays internal to the compose network (not published, no host mapping).
public record PortMapping(int Container, int Host, bool Public);
