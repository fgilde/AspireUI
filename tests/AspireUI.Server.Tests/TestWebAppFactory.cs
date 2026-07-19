using Microsoft.AspNetCore.Mvc.Testing;

// StackEndpoints reads DB_PATH/WORKSPACE_DIR from the environment at host build time, falling
// back to the developer's real %LocalAppData%\AspireUI when unset. Point every integration test
// at an isolated temp DB/workspace instead, so running the suite never touches (or overwrites)
// the developer's saved AI settings. Env vars are set in the constructor, before the lazy host
// build triggered by the first CreateClient()/Server access.
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    public readonly string DbPath;
    public readonly string WorkspaceDir;

    public TestWebAppFactory()
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        DbPath = Path.Combine(root, "aspireui.db");
        WorkspaceDir = Path.Combine(root, "workspace");

        Environment.SetEnvironmentVariable("DB_PATH", DbPath);
        Environment.SetEnvironmentVariable("WORKSPACE_DIR", WorkspaceDir);
    }
}
