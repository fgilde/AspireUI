using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.AspNetCore.Identity;

namespace AspireUI.Server.Services;

public static class Seeder
{
    private static readonly User HasherUser = new("", "", "", false, "");

    /// <summary>Stack ids the seed asked to deploy; hosting picks the list up once it is running.</summary>
    public const string PendingDeployKey = "seed:deploy-pending";

    // Resolves the real stores + process environment. Called once at startup.
    public static void Run()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        Directory.CreateDirectory(dataDir);
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");
        var workspace = Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(dataDir, "workspace");

        // Everything AspireUI reads is ASPIREUI_-prefixed, so take the lot instead of a whitelist that
        // has to grow with every new knob.
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var k = e.Key?.ToString() ?? "";
            if (k.StartsWith("ASPIREUI_", StringComparison.OrdinalIgnoreCase)) env[k] = e.Value?.ToString();
        }

        var settings = new SettingsStore(dbPath);
        var secrets = new SecretStore(dbPath, dataDir);
        Seed(new UserStore(dbPath), new StackStore(dbPath), settings, env,
            targets: new TargetStore(dbPath),
            secrets: secrets,
            tokens: new ApiTokenStore(dbPath),
            appSources: new AppSourceService(settings, CatalogService.AppSourceCacheDir()),
            catalog: new CatalogService(),
            workspace: workspace);
    }

    // Testable core: pure over the given stores + env map. The stores past `env` are optional so a test
    // can seed only the part it is about.
    public static void Seed(UserStore users, StackStore stacks, SettingsStore settings,
        IReadOnlyDictionary<string, string?> env,
        TargetStore? targets = null, SecretStore? secrets = null, ApiTokenStore? tokens = null,
        AppSourceService? appSources = null, CatalogService? catalog = null, string? workspace = null)
    {
        var doc = DocFrom(env);

        SeedAdmin(users, env);
        SeedUsers(users, doc.Users);
        SeedStack(stacks, env);
        SeedAi(settings, env);
        SeedSettings(settings, env);
        SeedDocSettings(settings, doc.Settings);
        if (targets is not null && secrets is not null) SeedTargets(targets, secrets, doc.Targets);
        if (tokens is not null) SeedTokens(tokens, users, doc.Tokens);
        if (appSources is not null) SeedAppSources(appSources, doc.AppSources);

        var made = new List<string>();
        if (catalog is not null) made.AddRange(SeedApps(stacks, catalog, doc.Apps, doc.Deploy, workspace));
        made.AddRange(SeedStacks(stacks, doc.Stacks, doc.Deploy, workspace));
        if (made.Count > 0) settings.SetValue(PendingDeployKey, JsonSerializer.Serialize(made));
    }

    /// <summary>The seed file (if any) plus everything the short env-var forms describe.</summary>
    public static SeedDoc DocFrom(IReadOnlyDictionary<string, string?> env)
    {
        string? V(string key) => env.GetValueOrDefault(key);

        var doc = new SeedDoc();
        // A file may be a document or a directory holding aspireui.seed.json.
        if (V("ASPIREUI_SEED_FILE") is { Length: > 0 } file)
        {
            var path = Directory.Exists(file) ? Path.Combine(file, "aspireui.seed.json") : file;
            if (File.Exists(path))
                try { if (SeedParse.Doc(File.ReadAllText(path)) is { } fromFile) doc = fromFile; }
                catch (Exception ex) { Console.Error.WriteLine($"seed: {path} could not be read: {ex.Message}"); }
        }

        return SeedDoc.Merge(doc, new SeedDoc(
            Users: SeedParse.Users(V("ASPIREUI_USERS")),
            Targets: SeedParse.Targets(V("ASPIREUI_TARGETS")),
            Stacks: [.. SeedParse.GitStacks(V("ASPIREUI_SEED_GIT")), .. SeedParse.PathStacks(V("ASPIREUI_SEED_DIR"))],
            Apps: SeedParse.Apps(V("ASPIREUI_SEED_APPS")),
            AppSources: SeedParse.AppSources(V("ASPIREUI_APP_SOURCES")),
            Tokens: SeedParse.Tokens(V("ASPIREUI_API_TOKENS")),
            Deploy: SeedParse.Truthy(V("ASPIREUI_SEED_DEPLOY"))));
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

    private static void SeedDocSettings(SettingsStore settings, Dictionary<string, string>? values)
    {
        foreach (var (key, value) in values ?? new())
            if (key.Length > 0 && string.IsNullOrEmpty(settings.GetValue(key)))
                settings.SetValue(key, value);
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

    /// <summary>
    /// Accounts, one at a time and by name: an account that is already there is left exactly as it is,
    /// so a restart with the same configuration changes nothing and a new entry still arrives.
    /// </summary>
    public static void SeedUsers(UserStore users, IEnumerable<SeedUser>? list)
    {
        var hasher = new PasswordHasher<User>();
        foreach (var u in list ?? [])
        {
            if (string.IsNullOrWhiteSpace(u.Username) || string.IsNullOrWhiteSpace(u.Password)) continue;
            if (users.FindByUsername(u.Username) is not null) continue;
            var hash = hasher.HashPassword(HasherUser, u.Password!);
            var created = users.Create(u.Username.Trim(), hash, u.Admin);
            if (!u.Admin)
                users.SetPermissions(created.Id,
                    (u.Permissions ?? Perm.Default.ToList()).Where(Perm.All.Contains).Distinct().ToList());
            var modes = (u.ViewModes ?? []).Where(m => m is "full" or "simple").Distinct().ToList();
            if (modes.Count > 0) users.SetViewModes(created.Id, modes);
            if (u.MustChangePassword) users.SetPassword(created.Id, hash, mustChange: true);
        }
    }

    /// <summary>
    /// Deploy targets. Key material is either the text itself or a path to it — a mounted secret file
    /// is the usual way in, and it goes straight into the secret store, never into the target row.
    /// </summary>
    public static void SeedTargets(TargetStore targets, SecretStore secrets, IEnumerable<SeedTarget>? list)
    {
        foreach (var t in list ?? [])
        {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            if (!TargetKind.All.Contains(t.Kind) || t.Kind == TargetKind.Local) continue;
            if (targets.List().Any(x => string.Equals(x.Name, t.Name.Trim(), StringComparison.OrdinalIgnoreCase))) continue;

            var id = targets.UniqueId(t.Name.Trim());
            var target = new DeployTarget(id, t.Name.Trim(), t.Kind,
                PublicHost: Blank(t.PublicHost), Notes: Blank(t.Notes),
                CreatedAt: DateTime.UtcNow.ToString("O"));

            target = t.Kind switch
            {
                TargetKind.Ssh when !string.IsNullOrWhiteSpace(t.Host) => target with
                {
                    Ssh = new TargetSsh(t.Host!.Trim(), t.Port ?? 22, Blank(t.User) ?? "root",
                        secrets.Put(Material(t.Key), $"ssh key for {t.Name}"),
                        secrets.Put(Blank(t.Passphrase), $"ssh passphrase for {t.Name}"),
                        Blank(t.HostKey)),
                },
                TargetKind.DockerTcp => target with
                {
                    DockerHost = Blank(t.DockerHost) ?? (string.IsNullOrWhiteSpace(t.Host) ? null : $"tcp://{t.Host!.Trim()}:{t.Port ?? 2376}"),
                    Tls = new TargetTls(secrets.Put(Material(t.Ca), $"ca for {t.Name}"),
                        secrets.Put(Material(t.Cert), $"cert for {t.Name}"),
                        secrets.Put(Material(t.TlsKey ?? t.Key), $"tls key for {t.Name}")),
                },
                TargetKind.K8s => target with
                {
                    Kube = new TargetKube(Blank(t.Context), Blank(t.Namespace),
                        secrets.Put(Material(t.Kubeconfig), $"kubeconfig for {t.Name}"),
                        IngressClass: null, StorageClass: Blank(t.StorageClass),
                        Expose: Blank(t.Expose), IngressHostPattern: Blank(t.IngressHost)),
                },
                _ => target,
            };

            var saved = targets.Upsert(target);
            if (t.Default) targets.SetDefault(saved.Id);
        }
    }

    /// <summary>Bearer tokens for automation, by name per user. An unknown username is skipped.</summary>
    public static void SeedTokens(ApiTokenStore tokens, UserStore users, IEnumerable<SeedToken>? list)
    {
        foreach (var t in list ?? [])
        {
            if (string.IsNullOrWhiteSpace(t.Token) || string.IsNullOrWhiteSpace(t.Username)) continue;
            if (users.FindByUsername(t.Username) is not { } u) continue;
            if (tokens.List(u.Id).Any(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase))) continue;
            tokens.Create(u.Id, string.IsNullOrWhiteSpace(t.Name) ? "seeded" : t.Name.Trim(), t.Token.Trim());
        }
    }

    /// <summary>Store sources. Nothing is fetched here — the store refreshes them on demand.</summary>
    public static void SeedAppSources(AppSourceService appSources, IEnumerable<SeedAppSource>? list)
    {
        foreach (var s in list ?? [])
        {
            if (AppSourceService.Validate(s.Name, s.Url) is not null) continue;
            if (appSources.List().Any(x => x.Id == AppSourceService.IdFor(s.Url))) continue;
            appSources.Add(s.Name, s.Url);
        }
    }

    /// <summary>
    /// Apps from the store, by catalog id — the same nodes the install dialog would build, without the
    /// dialog. Returns the stack ids that were created.
    /// </summary>
    public static List<string> SeedApps(StackStore stacks, CatalogService catalog, IEnumerable<SeedApp>? list,
        bool deployAll, string? workspace)
    {
        var made = new List<string>();
        var presets = catalog.GetPresets();
        foreach (var a in list ?? [])
        {
            if (string.IsNullOrWhiteSpace(a.Id)) continue;
            if (presets.FirstOrDefault(p => string.Equals(p.Id, a.Id.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } preset)
            {
                Console.Error.WriteLine($"seed: no app '{a.Id}' in the catalog");
                continue;
            }
            var name = string.IsNullOrWhiteSpace(a.Name) ? preset.Label : a.Name!.Trim();
            if (Exists(stacks, name)) continue;

            var (nodes, edges) = PresetBuilder.Build(preset);
            var id = Guid.NewGuid().ToString("n");
            var stack = new StackModel(id, name, "net10.0", nodes, edges, [],
                preset.Files?.Select(f => new ExtraFile(f.Name, f.Content)).ToList() ?? [], [],
                CreatedAt: DateTime.UtcNow.ToString("O"), CreatedBy: "seed",
                HostingUrlPath: preset.UrlPath);
            Save(stacks, stack, workspace);
            if (a.Deploy ?? deployAll) made.Add(id);
        }
        return made;
    }

    /// <summary>
    /// Stacks from the seed: AddProject nodes, a compose file, a directory or a git repository. Returns
    /// the ids that were created and asked to be deployed.
    /// </summary>
    public static List<string> SeedStacks(StackStore stacks, IEnumerable<SeedStack>? list, bool deployAll, string? workspace)
    {
        var made = new List<string>();
        var dirs = new DirImporter(new ImportService(), new ComposeImporter());
        foreach (var s in list ?? [])
        {
            var id = Guid.NewGuid().ToString("n");
            var dir = workspace is null ? null : Path.Combine(workspace, id);
            StackModel? stack = null;

            if (s.Projects is { Count: > 0 })
            {
                var name = string.IsNullOrWhiteSpace(s.Name) ? "stack" : s.Name!;
                if (Exists(stacks, name)) continue;
                stack = ProjectStack(id, name, s.Projects);
            }
            else if (!string.IsNullOrWhiteSpace(s.Compose))
            {
                var yaml = File.Exists(s.Compose!) ? File.ReadAllText(s.Compose!) : s.Compose!;
                var name = string.IsNullOrWhiteSpace(s.Name)
                    ? (File.Exists(s.Compose!) ? Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(s.Compose!))) ?? "compose app" : "compose app")
                    : s.Name!;
                if (Exists(stacks, name)) continue;
                var (imported, err) = new ComposeImporter().Import(id, name, yaml);
                if (imported is null) { Console.Error.WriteLine($"seed: compose for '{name}' — {err}"); continue; }
                stack = imported;
            }
            else if (!string.IsNullOrWhiteSpace(s.Path))
            {
                var from = s.Path!.Trim();
                var isFile = File.Exists(from);
                var sourceDir = isFile ? Path.GetDirectoryName(Path.GetFullPath(from))! : from;
                if (!Directory.Exists(sourceDir)) { Console.Error.WriteLine($"seed: {from} does not exist"); continue; }
                var name = string.IsNullOrWhiteSpace(s.Name) ? new DirectoryInfo(sourceDir).Name : s.Name!;
                if (Exists(stacks, name)) continue;
                if (dir is null) continue;
                // The seed directory is read-only as far as we are concerned: import a copy, never the original.
                CopyTree(sourceDir, dir);
                var (imported, err) = dirs.Build(id, dir, s.Mode, name, isFile ? [Path.GetFileName(from)] : null, null, null);
                if (imported is null) { Console.Error.WriteLine($"seed: {from} — {err}"); Discard(dir); continue; }
                stack = imported;
            }
            else if (!string.IsNullOrWhiteSpace(s.Git))
            {
                if (dir is null) continue;
                var (repo, cloneError) = GitService.CloneInto(s.Git!, s.Branch, s.Subdir, dir);
                if (cloneError is not null) { Console.Error.WriteLine($"seed: {s.Git} — {cloneError}"); Discard(dir); continue; }
                var name = string.IsNullOrWhiteSpace(s.Name) ? repo ?? "git app" : s.Name!;
                if (Exists(stacks, name)) { Discard(dir); continue; }
                var (imported, err) = dirs.Build(id, dir, s.Mode, name, null, null, null);
                if (imported is null) { Console.Error.WriteLine($"seed: {s.Git} — {err}"); Discard(dir); continue; }
                stack = imported with { FromGit = true };
            }

            if (stack is null) continue;
            Save(stacks, stack with { CreatedAt = DateTime.UtcNow.ToString("O"), CreatedBy = "seed" }, workspace);
            if (s.Deploy ?? deployAll) made.Add(stack.Id);
        }
        return made;
    }

    private static void SeedStack(StackStore stacks, IReadOnlyDictionary<string, string?> env)
    {
        var name = env.GetValueOrDefault("ASPIREUI_SEED_STACK_NAME");
        var projects = env.GetValueOrDefault("ASPIREUI_SEED_STACK_PROJECTS");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(projects)) return;
        if (Exists(stacks, name!)) return;

        var paths = projects.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length == 0) return;
        stacks.Save(ProjectStack(Guid.NewGuid().ToString("n"), name!, paths));
    }

    private static StackModel ProjectStack(string id, string name, IEnumerable<string> paths)
    {
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
        return new StackModel(id, name, "net10.0", nodes, [], [], [], []);
    }

    private static bool Exists(StackStore stacks, string name) =>
        stacks.List().Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void Save(StackStore stacks, StackModel stack, string? workspace)
    {
        stacks.Save(stack);
        if (workspace is not null)
            try { new CodeGenService().Materialize(stack, Path.Combine(workspace, stack.Id)); } catch { }
    }

    private static void Discard(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, d);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".git")) continue;
            Directory.CreateDirectory(Path.Combine(to, rel));
        }
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, f);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".git")) continue;
            File.Copy(f, Path.Combine(to, rel), overwrite: true);
        }
    }

    /// <summary>The value itself, or the contents of the file it points at.</summary>
    private static string? Material(string? value)
    {
        var v = Blank(value);
        if (v is null) return null;
        try { if (File.Exists(v)) return File.ReadAllText(v); } catch { }
        return v;
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static string Sanitize(string name)
    {
        var cleaned = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (cleaned.Length == 0) return "project";
        return char.IsDigit(cleaned[0]) ? "_" + cleaned : char.ToLowerInvariant(cleaned[0]) + cleaned[1..];
    }
}
