using AspireUI.Server.Models;
using Microsoft.AspNetCore.Identity;

namespace AspireUI.Server.Services;

// One-shot startup seeding driven by environment variables, so a container (e.g. AspireUI launched
// as a resource inside someone's stack) can come up pre-configured — no manual first-run wizard.
//
//   ASPIREUI_ADMIN_USERNAME / ASPIREUI_ADMIN_PASSWORD
//       Create the admin user if no users exist yet (idempotent — skipped once anyone exists).
//   ASPIREUI_SEED_STACK_NAME + ASPIREUI_SEED_STACK_PROJECTS
//       Create a stack of that name (once) with one AddProject node per project path in
//       ASPIREUI_SEED_STACK_PROJECTS (';' or ',' separated). Skipped if a stack with that name exists.
public static class Seeder
{
    private static readonly User HasherUser = new("", "", "", false, "");

    // Resolves the real stores + process environment. Called once at startup.
    public static void Run()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        Directory.CreateDirectory(dataDir);
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");
        var env = new Dictionary<string, string?>
        {
            ["ASPIREUI_ADMIN_USERNAME"] = Environment.GetEnvironmentVariable("ASPIREUI_ADMIN_USERNAME"),
            ["ASPIREUI_ADMIN_PASSWORD"] = Environment.GetEnvironmentVariable("ASPIREUI_ADMIN_PASSWORD"),
            ["ASPIREUI_SEED_STACK_NAME"] = Environment.GetEnvironmentVariable("ASPIREUI_SEED_STACK_NAME"),
            ["ASPIREUI_SEED_STACK_PROJECTS"] = Environment.GetEnvironmentVariable("ASPIREUI_SEED_STACK_PROJECTS"),
            ["ASPIREUI_AI_BASE_URL"] = Environment.GetEnvironmentVariable("ASPIREUI_AI_BASE_URL"),
            ["ASPIREUI_AI_MODEL"] = Environment.GetEnvironmentVariable("ASPIREUI_AI_MODEL"),
            ["ASPIREUI_AI_API_KEY"] = Environment.GetEnvironmentVariable("ASPIREUI_AI_API_KEY"),
        };
        Seed(new UserStore(dbPath), new StackStore(dbPath), new SettingsStore(dbPath), env);
    }

    // Testable core: pure over the given stores + env map.
    public static void Seed(UserStore users, StackStore stacks, SettingsStore settings, IReadOnlyDictionary<string, string?> env)
    {
        SeedAdmin(users, env);
        SeedStack(stacks, env);
        SeedAi(settings, env);
    }

    // Configure the built-in assistant from env on first run (only if not already set), so an
    // env-provisioned container (e.g. AddAspireUI().WithAi(...)) comes up with AI ready.
    private static void SeedAi(SettingsStore settings, IReadOnlyDictionary<string, string?> env)
    {
        var url = env.GetValueOrDefault("ASPIREUI_AI_BASE_URL");
        var model = env.GetValueOrDefault("ASPIREUI_AI_MODEL");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model)) return;
        var cur = settings.Get();
        if (!string.IsNullOrWhiteSpace(cur.AiBaseUrl)) return; // don't override a configured install
        settings.Save(cur with { AiBaseUrl = url, AiModel = model, AiApiKey = env.GetValueOrDefault("ASPIREUI_AI_API_KEY") });
    }

    private static void SeedAdmin(UserStore users, IReadOnlyDictionary<string, string?> env)
    {
        var user = env.GetValueOrDefault("ASPIREUI_ADMIN_USERNAME");
        var pass = env.GetValueOrDefault("ASPIREUI_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)) return;
        if (users.Count() > 0) return; // never override an existing install
        var hash = new PasswordHasher<User>().HashPassword(HasherUser, pass);
        users.Create(user, hash, isAdmin: true);
    }

    private static void SeedStack(StackStore stacks, IReadOnlyDictionary<string, string?> env)
    {
        var name = env.GetValueOrDefault("ASPIREUI_SEED_STACK_NAME");
        var projects = env.GetValueOrDefault("ASPIREUI_SEED_STACK_PROJECTS");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(projects)) return;
        if (stacks.List().Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) return;

        var paths = projects.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nodes = new List<NodeModel>();
        var used = new HashSet<string>();
        var i = 0;
        foreach (var path in paths)
        {
            var baseName = Sanitize(Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\')));
            var varName = baseName;
            while (!used.Add(varName)) varName = $"{baseName}{used.Count}";
            var literal = "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            nodes.Add(new NodeModel("n" + Guid.NewGuid().ToString("n")[..8], varName, "AddProject", varName,
                [], 80 + i % 3 * 260, 80 + i / 3 * 140, [literal]));
            i++;
        }
        if (nodes.Count == 0) return;
        stacks.Save(new StackModel(Guid.NewGuid().ToString("n"), name!, "net10.0", nodes, [], [], [], []));
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (cleaned.Length == 0) return "project";
        return char.IsDigit(cleaned[0]) ? "_" + cleaned : char.ToLowerInvariant(cleaned[0]) + cleaned[1..];
    }
}
