using System.Reflection;
using System.Text.Json;

namespace AspireUI.Server.Services;

// Fields: only for Type="configure" (Action<TOptions>); settable scalar properties as expandable group.
public record CatalogParam(string Name, string Type, bool Required, string? Default, List<string>? Options, string? EnumTypeName, string Label, List<CatalogParam>? Fields = null);
public record CatalogOverload(List<CatalogParam> Params);
public record CatalogMethod(string Method, string Label, List<CatalogOverload> Overloads);
public record ResourceType(string AddMethod, string Label, string? Icon, string? Group, string? Description, List<CatalogOverload> AddOverloads, List<CatalogMethod> Withs,
    bool Composite = false, List<string>? Usings = null, string? Package = null, string? PackageVersion = null,
    string? ResourceTypeName = null, bool SupportsConnectionString = false, bool SupportsEndpoints = false);

// Curated app preset: one-click preconfigured AddContainer (image + endpoint + env) from catalog/presets/container-presets.json.
public record ContainerPreset(string Id, string Label, string Group, string Image, int Port,
    string? Icon, string? Description, List<List<string>>? Env,
    List<PresetCompanion>? Companions,
    List<List<string>>? Volumes,
    List<PresetParam>? Params = null, bool Gpu = false, bool HostNetwork = false,
    bool FixedPort = false,
    List<List<string>>? BindMounts = null,
    List<PresetFile>? Files = null,
    List<string>? Args = null,
    List<string>? RuntimeArgs = null,
    List<string>? Tags = null,
    string? Website = null,
    List<string>? Screenshots = null,
    string? UrlPath = null,
    string? Logo = null, string? Card = null, string? Github = null,
    int? Stars = null, string? License = null, string? Language = null, List<string>? Topics = null,
    string? Submitter = null, string? Source = null);
public record PresetFile(string Name, string Content);
// Companion node in preset; wires env references and offers resource alternatives.
public record PresetCompanion(string Key, string AddMethod, string ResourceName, string? Image, int? Port, List<List<string>>? Env, string? Role);
// Preset param → Aspire parameter or literal env value.
public record PresetParam(string Key, string Env, string? Default, bool Secret = false, string? Name = null);

public class CatalogService
{
    private readonly Assembly[] _assemblies;
    private readonly Dictionary<string, JsonElement> _overlay;

    // Merge every *.json in built-in presets + optional EXTRA_PRESETS_DIR, keyed by preset id.
    // Where refreshed app sources are cached (see AppSourceService) — part of the store like the built-ins.
    public static string AppSourceCacheDir() =>
        Path.Combine(Environment.GetEnvironmentVariable("WORKSPACE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI", "workspace"),
            "_appsources");

    public IReadOnlyList<ContainerPreset> GetPresets()
    {
        var byId = new Dictionary<string, ContainerPreset>(StringComparer.OrdinalIgnoreCase);
        // A file holds one app or an array of them — same manifest an author ships in their own repo.
        void Load(string file)
        {
            try
            {
                foreach (var p in ManifestImporter.Parse(File.ReadAllText(file)).apps)
                    if (!string.IsNullOrWhiteSpace(p.Id)) byId[p.Id] = p;
            }
            catch { }
        }
        // Recursive so submitted apps can live in their own folder (catalog/presets/community/<id>.json).
        void LoadDir(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories)
                         .Where(f => !Path.GetFileName(f).EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x, StringComparer.Ordinal)) Load(f);
        }
        LoadDir(Path.Combine(AppContext.BaseDirectory, "catalog", "presets"));
        LoadDir(AppSourceCacheDir());
        LoadDir(Environment.GetEnvironmentVariable("EXTRA_PRESETS_DIR"));
        return byId.Values.ToList();
    }

    public CatalogService(params Assembly[] assemblies)
    {
        _assemblies = assemblies.Length > 0 ? assemblies : LoadDefault();
        _overlay = LoadOverlays();
    }

    // Single source of truth for NuGet versions: Directory.Packages.props (CPM), copied next to the app.
    // Nextended assemblies report AssemblyVersion 1.0.0, so reflection can't give the package version —
    // parsing the props file is the only reliable source, and keeps codegen versions from ever drifting.
    private static IReadOnlyDictionary<string, string>? _pkgVersions;
    public static IReadOnlyDictionary<string, string> PackageVersions()
    {
        if (_pkgVersions is not null) return _pkgVersions;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Directory.Packages.props");
            var doc = System.Xml.Linq.XDocument.Load(file);
            foreach (var pv in doc.Descendants("PackageVersion"))
            {
                var id = pv.Attribute("Include")?.Value;
                var v = pv.Attribute("Version")?.Value;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(v)) map[id] = v;
            }
        }
        catch { }
        return _pkgVersions = map;
    }

    // Resource→NuGet mapping from overlay JSON; version resolved from Directory.Packages.props.
    public static IReadOnlyDictionary<string, (string Id, string? Version)> ResourcePackages()
    {
        var versions = PackageVersions();
        var map = new Dictionary<string, (string, string?)>();
        foreach (var (name, entry) in LoadOverlays())
        {
            if (entry.TryGetProperty("package", out var pkg))
            {
                var id = pkg.GetString()!;
                map[name] = (id, versions.TryGetValue(id, out var v) ? v : null);
            }
        }
        return map;
    }

    // Resource→extra-using mapping from overlay JSON; namespaces for Add/With methods and enum args.
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ResourceUsings()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (name, entry) in LoadOverlays())
        {
            if (entry.TryGetProperty("usings", out var usings))
                map[name] = usings.EnumerateArray().Select(u => u.GetString()!).ToList();
        }
        return map;
    }

    public IReadOnlyList<ResourceType> GetCatalog()
    {
        var methods = _assemblies.SelectMany(SafeTypes)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
            .ToList();

        var withMethods = methods
            .Where(m => m.Name.StartsWith("With") || m.Name.StartsWith("Add"))
            .Where(m => m.GetParameters().Length >= 1 && IsResourceBuilder(m.GetParameters()[0].ParameterType))
            .Where(m => ReturnsResourceBuilder(m.ReturnType))
            .ToList();

        var adds = methods
            .Where(m => m.Name.StartsWith("Add"))
            .Where(m => ReturnsResourceBuilder(m.ReturnType))
            .Where(m => { var p = m.GetParameters(); return p.Length >= 2 && IsAppBuilder(p[0].ParameterType) && p[1].ParameterType == typeof(string); })
            .ToList();

        var result = new List<ResourceType>();
        var pkgVersions = ResourcePackages().Values; // reverse-lookup a package's version by id
        foreach (var grp in adds.GroupBy(m => m.Name))
        {
            var addOverloads = new List<CatalogOverload>();
            foreach (var m in grp)
            {
                var ov = ReadOverload(m.GetParameters().Skip(2)); // skip builder + name
                if (ov is not null) addOverloads.Add(ov);
            }
            addOverloads = DedupOverloads(addOverloads);
            if (addOverloads.Count == 0) addOverloads.Add(new CatalogOverload(new())); // name-only

            var tResource = grp.Select(m => ResourceArg(m.ReturnType)).FirstOrDefault(t => t is not null);
            var withs = tResource is null ? new List<CatalogMethod>() : BuildWiths(withMethods, tResource);

            var over = _overlay.TryGetValue(grp.Key, out var o) ? o : (JsonElement?)null;
            if (over?.TryGetProperty("exclude", out var ex) == true && ex.GetBoolean()) continue;
            var hidden = over?.TryGetProperty("hidden", out var h) == true
                ? h.EnumerateArray().Select(x => x.GetString()).ToHashSet() : new HashSet<string?>();
            withs = withs.Where(w => !hidden.Contains(w.Method)).ToList();

            var decl = grp.First().DeclaringType;
            var usings = new List<string>();
            if (decl?.Namespace is { } nsp) usings.Add(nsp);
            var pkgId = decl?.Assembly.GetName().Name;
            var pkgVer = pkgId is null ? null : (pkgVersions.FirstOrDefault(p => p.Id == pkgId).Version ?? CodeGenService.AspireVersion);

            var ifaces = tResource?.GetInterfaces() ?? Array.Empty<Type>();
            result.Add(new ResourceType(
                grp.Key,
                over?.TryGetProperty("label", out var lbl) == true ? lbl.GetString()! : grp.Key[3..],
                over?.TryGetProperty("icon", out var i) == true ? i.GetString() : null,
                over?.TryGetProperty("group", out var g) == true ? g.GetString() : "Other",
                over?.TryGetProperty("description", out var d) == true ? d.GetString() : null,
                addOverloads, withs, Usings: usings, Package: pkgId, PackageVersion: pkgVer,
                ResourceTypeName: tResource?.Name,
                SupportsConnectionString: ifaces.Any(x => x.Name == "IResourceWithConnectionString"),
                SupportsEndpoints: ifaces.Any(x => x.Name == "IResourceWithEndpoints")));
        }

        var composites = methods
            .Where(m => m.Name.StartsWith("Add"))
            .Where(m => IsAppBuilder(m.ReturnType))
            .Where(m => { var p = m.GetParameters(); return p.Length >= 1 && IsAppBuilder(p[0].ParameterType); })
            .ToList();
        foreach (var grp in composites.GroupBy(m => m.Name))
        {
            var over = _overlay.TryGetValue(grp.Key, out var o) ? o : (JsonElement?)null;
            if (over?.TryGetProperty("exclude", out var ex) == true && ex.GetBoolean()) continue;

            var ovs = new List<CatalogOverload>();
            foreach (var m in grp)
            {
                var ov = ReadOverload(m.GetParameters().Skip(1)); // skip builder receiver only
                if (ov is not null) ovs.Add(ov);
            }
            ovs = DedupOverloads(ovs);
            if (ovs.Count == 0) continue; // no renderable overload (options-object-only)

            var decl = grp.First().DeclaringType;
            var usings = new List<string>();
            if (decl?.Namespace is { } nsp) usings.Add(nsp);
            var pkgId = decl?.Assembly.GetName().Name;
            var pkgVer = pkgVersions.FirstOrDefault(p => p.Id == pkgId).Version ?? CodeGenService.AspireVersion;

            result.Add(new ResourceType(
                grp.Key,
                over?.TryGetProperty("label", out var lbl) == true ? lbl.GetString()! : Humanize(grp.Key[3..]),
                over?.TryGetProperty("icon", out var i) == true ? i.GetString() : null,
                over?.TryGetProperty("group", out var g) == true ? g.GetString() : "Setup",
                over?.TryGetProperty("description", out var d) == true ? d.GetString() : null,
                ovs, [], Composite: true, Usings: usings, Package: pkgId, PackageVersion: pkgVer));
        }

        return result.OrderBy(r => r.Group).ThenBy(r => r.AddMethod).ToList();
    }

    private static List<CatalogMethod> BuildWiths(List<MethodInfo> withMethods, Type tResource)
    {
        var applicable = withMethods.Where(w => WithApplies(w, tResource));
        var byName = new List<CatalogMethod>();
        foreach (var grp in applicable.GroupBy(w => w.Name))
        {
            var overloads = new List<CatalogOverload>();
            foreach (var w in grp)
            {
                var ov = ReadOverload(w.GetParameters().Skip(1)); // skip receiver
                if (ov is not null) overloads.Add(ov);
            }
            overloads = DedupOverloads(overloads);
            if (overloads.Count > 0)
            {
                var prefixLen = grp.Key.StartsWith("With") ? 4 : grp.Key.StartsWith("Add") ? 3 : 0;
                byName.Add(new CatalogMethod(grp.Key, grp.Key[prefixLen..], overloads)); // strip "With"/"Add"
            }
        }
        return byName.OrderBy(m => m.Method).ToList();
    }

    private static bool WithApplies(MethodInfo w, Type tResource)
    {
        var recv = w.GetParameters()[0].ParameterType;
        if (!IsResourceBuilder(recv)) return false;
        var wArg = recv.GetGenericArguments()[0];
        if (!wArg.IsGenericParameter)
            return wArg.IsAssignableFrom(tResource);
        foreach (var c in wArg.GetGenericParameterConstraints())
        {
            if (c.IsGenericType) { if (!ConstraintLooselyMet(c, tResource)) return false; }
            else if (!c.IsAssignableFrom(tResource)) return false;
        }
        return true;
    }

    private static bool ConstraintLooselyMet(Type constraint, Type tResource)
    {
        var def = constraint.GetGenericTypeDefinition();
        return tResource.GetInterfaces().Any(ifc => ifc.IsGenericType && ifc.GetGenericTypeDefinition() == def)
            || (tResource.BaseType?.IsGenericType == true && tResource.BaseType.GetGenericTypeDefinition() == def);
    }

    private static CatalogOverload? ReadOverload(IEnumerable<ParameterInfo> ps)
    {
        var list = new List<CatalogParam>();
        foreach (var p in ps)
        {
            if (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Action<>))
            {
                var optType = p.ParameterType.GetGenericArguments()[0];
                var fields = optType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(pr => pr.CanWrite)
                    .Select(pr => (pr, cc: Classify(pr.PropertyType)))
                    .Where(x => x.cc is not null)
                    .Select(x => new CatalogParam(x.pr.Name, x.cc!.Value.type, false, null,
                        x.cc.Value.options, x.cc.Value.enumType, Humanize(x.pr.Name)))
                    .ToList();
                if (fields.Count > 0)
                    list.Add(new CatalogParam(p.Name ?? "configure", "configure", false, null, null, null, "Options", fields));
                continue;
            }
            var c = Classify(p.ParameterType);
            if (c is null)
            {
                if (p.IsOptional || p.HasDefaultValue) break; // truncate; remaining are defaults
                return null;                                    // required non-renderable
            }
            var required = !(p.HasDefaultValue || p.IsOptional
                || Nullable.GetUnderlyingType(p.ParameterType) is not null);
            list.Add(new CatalogParam(
                p.Name ?? "arg", c.Value.type, required,
                p.HasDefaultValue ? p.DefaultValue?.ToString() : null,
                c.Value.options, c.Value.enumType,
                Humanize(p.Name ?? "arg")));
        }
        return new CatalogOverload(list);
    }

    private static (string type, List<string>? options, string? enumType)? Classify(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return ("string", null, null);
        if (t == typeof(bool)) return ("bool", null, null);
        if (t == typeof(int) || t == typeof(long) || t == typeof(short))
            return ("int", null, null);
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            return ("number", null, null);
        if (t.IsEnum) return ("enum", Enum.GetNames(t).ToList(), t.Name);
        if (IsResourceBuilder(t)) return ("resourceRef", null, ResourceArg(t)?.Name);
        return null;
    }

    private static List<CatalogOverload> DedupOverloads(List<CatalogOverload> ovs)
    {
        string Sig(CatalogOverload o) => string.Join(",", o.Params.Select(p => p.Name + ":" + p.Type));
        return ovs.GroupBy(Sig).Select(g => g.First())
                  .GroupBy(o => o.Params.Count)
                  .Select(g => g.OrderByDescending(o => o.Params.Count(p => p.Type != "bool")).First())
                  .OrderBy(o => o.Params.Count).ToList();
    }

    private static string Humanize(string name) =>
        string.Concat(name.Select((ch, idx) => idx > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString()))
              is var s ? char.ToUpper(s[0]) + s[1..] : name;

    private static bool ReturnsResourceBuilder(Type t) => IsResourceBuilder(t);
    private static bool IsResourceBuilder(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("IResourceBuilder");
    private static Type? ResourceArg(Type t) => IsResourceBuilder(t) ? t.GetGenericArguments()[0] : null;
    private static bool IsAppBuilder(Type t) => t.Name == "IDistributedApplicationBuilder";

    private static IEnumerable<Type> SafeTypes(Assembly a)
    { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; } }

    private static Assembly[] LoadDefault()
    {
        _ = typeof(Aspire.Hosting.IDistributedApplicationBuilder).Assembly;
        _ = typeof(Aspire.Hosting.RedisBuilderExtensions).Assembly;
        _ = typeof(Aspire.Hosting.PostgresBuilderExtensions).Assembly;
        foreach (var name in new[]
                 {
                     "Nextended.Aspire.Hosting.Supabase",
                     "Nextended.Aspire.Hosting.N8n",
                     "Nextended.Aspire.Hosting.Php",
                     "Nextended.Aspire.Hosting.LocalAI",
                     "Nextended.Aspire.Hosting.Grafana",
                     "Nextended.Aspire.Hosting.AspireUI",
                     "Nextended.Aspire",
                     "CommunityToolkit.Aspire.Hosting.Ollama",
                     "Aspire.Hosting.SqlServer", "Aspire.Hosting.MySql", "Aspire.Hosting.MongoDB",
                     "Aspire.Hosting.Kafka", "Aspire.Hosting.RabbitMQ", "Aspire.Hosting.Nats",
                     "Aspire.Hosting.Elasticsearch", "Aspire.Hosting.Keycloak", "Aspire.Hosting.Seq",
                     "Aspire.Hosting.Valkey", "Aspire.Hosting.Garnet", "Aspire.Hosting.Qdrant",
                     "Aspire.Hosting.Milvus", "Aspire.Hosting.Azure.CosmosDB",
                     "CommunityToolkit.Aspire.Hosting.Java", "CommunityToolkit.Aspire.Hosting.ActiveMQ",
                     "CommunityToolkit.Aspire.Hosting.Golang", "CommunityToolkit.Aspire.Hosting.Dapr",
                     "Aspire.Hosting.Yarp", "Aspire.Hosting.Oracle", "Aspire.Hosting.Python",
                     "Aspire.Hosting.Azure.Storage", "Aspire.Hosting.Azure.ServiceBus", "Aspire.Hosting.Azure.KeyVault",
                     "Aspire.Hosting.Azure.ApplicationInsights", "Aspire.Hosting.Azure.CognitiveServices", "Aspire.Hosting.Maui",
                     "CommunityToolkit.Aspire.Hosting.MinIO", "CommunityToolkit.Aspire.Hosting.Meilisearch",
                     "CommunityToolkit.Aspire.Hosting.RavenDB", "CommunityToolkit.Aspire.Hosting.MailPit",
                     "CommunityToolkit.Aspire.Hosting.Adminer", "CommunityToolkit.Aspire.Hosting.Ngrok",
                     "CommunityToolkit.Aspire.Hosting.Bun", "CommunityToolkit.Aspire.Hosting.Deno", "CommunityToolkit.Aspire.Hosting.Rust",
                 })
        {
            try { Assembly.Load(name); } catch { }
        }
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Aspire.Hosting") == true
                     || a.GetName().Name?.StartsWith("Nextended.Aspire") == true
                     || a.GetName().Name?.StartsWith("CommunityToolkit.Aspire") == true)
            .ToArray();
    }

    private static Dictionary<string, JsonElement> LoadOverlays()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "catalog");
        var map = new Dictionary<string, JsonElement>();
        if (!Directory.Exists(dir)) return map;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(f));
            foreach (var prop in doc.RootElement.EnumerateObject()) map[prop.Name] = prop.Value.Clone();
        }
        return map;
    }
}
