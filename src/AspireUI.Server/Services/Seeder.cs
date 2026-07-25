using AspireUI.Server.Models;
using Microsoft.AspNetCore.Identity;

namespace AspireUI.Server.Services;

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
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var k = e.Key?.ToString() ?? "";
            if (k.StartsWith("ASPIREUI_SET_", StringComparison.OrdinalIgnoreCase)) env[k] = e.Value?.ToString();
        }
        Seed(new UserStore(dbPath), new StackStore(dbPath), new SettingsStore(dbPath), env);
    }

    // Testable core: pure over the given stores + env map.
    public static void Seed(UserStore users, StackStore stacks, SettingsStore settings, IReadOnlyDictionary<string, string?> env)
    {
        SeedAdmin(users, env);
        SeedStack(stacks, env);
        SeedAi(settings, env);
        SeedSettings(settings, env);
    }

    // Seed settings from ASPIREUI_SET_<Key> env vars (all keys unless ASPIREUI_SET_FORCE=true).
    public static void SeedSettings(SettingsStore settings, IReadOnlyDictionary<string, string?> env)
    {
        const string prefix = "ASPIREUI_SET_";
        var force = string.Equals(env.GetValueOrDefault(prefix + "FORCE"), "true", StringComparison.OrdinalIgnoreCase);
        foreach (var (envKey, value) in env)
        {
            if (!envKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var key = envKey[prefix.Length..];
            if (key.Length == 0 || key.Equals("FORCE", StringComparison.OrdinalIgnoreCase) || value is null) continue;
            if (!force && !string.IsNullOrEmpty(settings.GetValue(key))) continue;
            settings.SetValue(key, value);
        }
    }

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
