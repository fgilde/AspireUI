namespace AspireUI.Server.Services;

/// <summary>
/// Where this instance keeps its things. Injected rather than read from the environment at every use
/// so a caller — a test, a second instance in one process — can be pointed somewhere else without
/// changing the environment underneath everybody.
/// </summary>
public record InstancePaths(string Db, string Workspace)
{
    public static InstancePaths FromEnvironment()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        return new InstancePaths(
            Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"),
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(dataDir, "workspace"));
    }
}
