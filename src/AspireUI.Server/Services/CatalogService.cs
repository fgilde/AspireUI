using System.Reflection;
using System.Text.Json;

namespace AspireUI.Server.Services;

// Fields: only set for Type=="configure" (an Action<TOptions> param) — the options object's
// settable scalar properties, rendered as an expandable group and emitted as a `o => { o.X = …; }` lambda.
public record CatalogParam(string Name, string Type, bool Required, string? Default, List<string>? Options, string? EnumTypeName, string Label, List<CatalogParam>? Fields = null);
public record CatalogOverload(List<CatalogParam> Params);
public record CatalogMethod(string Method, string Label, List<CatalogOverload> Overloads);
public record ResourceType(string AddMethod, string Label, string? Icon, string? Group, string? Description, List<CatalogOverload> AddOverloads, List<CatalogMethod> Withs,
    // Composite/macro extension: AddX(this IDistributedApplicationBuilder,…) that returns the
    // builder (not a resource). Emitted as a statement-node; Usings/Package/PackageVersion are the
    // namespace + NuGet package the generated stack must pull in for it to compile.
    bool Composite = false, List<string>? Usings = null, string? Package = null, string? PackageVersion = null,
    // CLR name of the resource this AddX produces (e.g. "PostgresResource") — lets the UI offer
    // type-matching resources when filling an IResourceBuilder<T> parameter.
    string? ResourceTypeName = null);

// A curated "app" preset: one click drops a preconfigured AddContainer node (image + HTTP endpoint
// + optional env). Lets us offer cool self-hostable apps (LocalRecall, ComfyUI, SD.Next, …) as
// palette nodes without an Aspire package for each. Read from catalog/presets/container-presets.json.
public record ContainerPreset(string Id, string Label, string Group, string Image, int Port,
    string? Icon, string? Description, List<List<string>>? Env,
    // Optional companion resources dropped + wired alongside the main container (e.g. Postgres/Redis
    // for Immich/Paperless). The main container references + waits-for each. A scaffold to finish, not
    // a guaranteed-working deploy.
    List<PresetCompanion>? Companions,
    // Optional metadata: named data volumes to mount ([name, "/container/path"]) — emitted as
    // WithVolume; and informational flags shown as badges/caveats (Aspire wiring for these is manual).
    List<List<string>>? Volumes, bool Gpu = false, bool HostNetwork = false);
// A companion node in a preset. Key wires env references (`${key}` → its resource name). Role (e.g.
// "postgres"/"redis"/"llm") lets the UI reuse an existing matching resource or offer alternatives
// (Aspire AddX) instead of always dropping this container.
public record PresetCompanion(string Key, string AddMethod, string ResourceName, string? Image, int? Port, List<List<string>>? Env, string? Role);

public class CatalogService
{
    private readonly Assembly[] _assemblies;
    private readonly Dictionary<string, JsonElement> _overlay;

    public IReadOnlyList<ContainerPreset> GetPresets()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "catalog", "presets", "container-presets.json");
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ContainerPreset>>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }
        catch { return []; }
    }

    public CatalogService(params Assembly[] assemblies)
    {
        _assemblies = assemblies.Length > 0 ? assemblies : LoadDefault();
        _overlay = LoadOverlays();
    }

    // Single source of truth for resource->NuGet-package mapping, read from the same overlay
    // JSON used for labels/icons. CodeGenService (which has no reflection catalog) calls this
    // instead of hardcoding its own map. Value is (package id, optional version override for
    // packages that don't ship at AspireVersion, e.g. the Nextended integrations).
    public static IReadOnlyDictionary<string, (string Id, string? Version)> ResourcePackages()
    {
        var map = new Dictionary<string, (string, string?)>();
        foreach (var (name, entry) in LoadOverlays())
        {
            if (entry.TryGetProperty("package", out var pkg))
            {
                var version = entry.TryGetProperty("packageVersion", out var v) ? v.GetString() : null;
                map[name] = (pkg.GetString()!, version);
            }
        }
        return map;
    }

    // Single source of truth for resource->extra-`using` mapping, mirroring ResourcePackages above:
    // same overlay JSON, this time the "usings" array (namespaces beyond Aspire.Hosting/
    // Aspire.Hosting.ApplicationModel that the resource's Add/With methods and enum args live in).
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

        // WithX/AddX methods on the resource builder itself (receiver IResourceBuilder<W>,
        // returns IResourceBuilder<>) - e.g. WithEnvironment, but also fluent capabilities like
        // ollama.AddModel("llama3.2") or pg.AddDatabase("db"). Distinct from the top-level AddX
        // resource discovery below, whose receiver is IDistributedApplicationBuilder.
        var withMethods = methods
            .Where(m => m.Name.StartsWith("With") || m.Name.StartsWith("Add"))
            .Where(m => m.GetParameters().Length >= 1 && IsResourceBuilder(m.GetParameters()[0].ParameterType))
            .Where(m => ReturnsResourceBuilder(m.ReturnType))
            .ToList();

        // AddX grouped by name -> (renderable overloads, TResource)
        var adds = methods
            .Where(m => m.Name.StartsWith("Add"))
            .Where(m => ReturnsResourceBuilder(m.ReturnType))
            .Where(m => { var p = m.GetParameters(); return p.Length >= 2 && IsAppBuilder(p[0].ParameterType) && p[1].ParameterType == typeof(string); })
            .ToList();

        var result = new List<ResourceType>();
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
            // Resource-level opt-out: internal/builder helpers (AddWithAutoNaming, AddDockerfileFactory,
            // …) are extension methods that match the AddX shape but aren't user-facing resources.
            if (over?.TryGetProperty("exclude", out var ex) == true && ex.GetBoolean()) continue;
            var hidden = over?.TryGetProperty("hidden", out var h) == true
                ? h.EnumerateArray().Select(x => x.GetString()).ToHashSet() : new HashSet<string?>();
            withs = withs.Where(w => !hidden.Contains(w.Method)).ToList();

            result.Add(new ResourceType(
                grp.Key,
                over?.TryGetProperty("label", out var lbl) == true ? lbl.GetString()! : grp.Key[3..],
                over?.TryGetProperty("icon", out var i) == true ? i.GetString() : null,
                over?.TryGetProperty("group", out var g) == true ? g.GetString() : "Other",
                over?.TryGetProperty("description", out var d) == true ? d.GetString() : null,
                addOverloads, withs, ResourceTypeName: tResource?.Name));
        }

        // Composite/macro extensions: AddX(this IDistributedApplicationBuilder, …) that RETURN the
        // builder (not a resource) — helpers wiring several resources at once (e.g. Nextended's
        // AddObservabilityStack). Exposed as statement-nodes. Only overloads whose non-receiver
        // params are all renderable (scalars / enums / resource-references / an options lambda)
        // survive; pure options-object overloads drop out (ReadOverload returns null → no overload).
        var pkgVersions = ResourcePackages().Values; // reverse-lookup a package's version by id
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

    // A WithX applies to tResource if its receiver IResourceBuilder<W> accepts it.
    private static bool WithApplies(MethodInfo w, Type tResource)
    {
        var recv = w.GetParameters()[0].ParameterType;
        if (!IsResourceBuilder(recv)) return false;
        var wArg = recv.GetGenericArguments()[0];
        if (!wArg.IsGenericParameter)
            return wArg.IsAssignableFrom(tResource);
        // generic method: tResource must satisfy the type-parameter constraints
        foreach (var c in wArg.GetGenericParameterConstraints())
        {
            if (c.IsGenericType) { if (!ConstraintLooselyMet(c, tResource)) return false; }
            else if (!c.IsAssignableFrom(tResource)) return false;
        }
        return true;
    }

    private static bool ConstraintLooselyMet(Type constraint, Type tResource)
    {
        // Best-effort for generic constraints (e.g. IResourceWithConnectionString): match by
        // the constraint's generic type definition against tResource's implemented interfaces/bases.
        var def = constraint.GetGenericTypeDefinition();
        return tResource.GetInterfaces().Any(ifc => ifc.IsGenericType && ifc.GetGenericTypeDefinition() == def)
            || (tResource.BaseType?.IsGenericType == true && tResource.BaseType.GetGenericTypeDefinition() == def);
    }

    // Read a renderable overload from params after the receiver/name. Truncate at the first
    // non-renderable param IF it's optional (rest use defaults); a required non-renderable param
    // makes the whole overload unusable -> null.
    private static CatalogOverload? ReadOverload(IEnumerable<ParameterInfo> ps)
    {
        var list = new List<CatalogParam>();
        foreach (var p in ps)
        {
            // Action<TOptions> configure param (e.g. AddGithubRepository's o => o.GitRef = …): render
            // the options object's settable scalar props as a group instead of dropping the param.
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
        // IResourceBuilder<TRes> param (e.g. a composite's `supabase` arg): rendered as a picker of
        // the stack's existing nodes; enumType carries the resource CLR type name as a hint.
        if (IsResourceBuilder(t)) return ("resourceRef", null, ResourceArg(t)?.Name);
        return null;
    }

    private static List<CatalogOverload> DedupOverloads(List<CatalogOverload> ovs)
    {
        string Sig(CatalogOverload o) => string.Join(",", o.Params.Select(p => p.Name + ":" + p.Type));
        return ovs.GroupBy(Sig).Select(g => g.First()).OrderBy(o => o.Params.Count).ToList();
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
        // Nextended integrations (Slice 3 Task 2) + Ollama/GitHub integrations (Slice 4 Task 2):
        // force-load by name since we don't reference a stable public type at compile time here;
        // each assembly's AddX/WithX extensions get picked up by the AppDomain scan below once loaded.
        foreach (var name in new[]
                 {
                     "Nextended.Aspire.Hosting.Supabase",
                     "Nextended.Aspire.Hosting.N8n",
                     "Nextended.Aspire.Hosting.LocalAI",
                     "Nextended.Aspire.Hosting.Grafana",
                     "Nextended.Aspire.Hosting.AspireUI",
                     "Nextended.Aspire",
                     "CommunityToolkit.Aspire.Hosting.Ollama",
                     // Catalog breadth (referenced in the .csproj; force-load so their AddX reflect in):
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
            try { Assembly.Load(name); } catch { /* package not present/loadable; catalog just omits it */ }
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
