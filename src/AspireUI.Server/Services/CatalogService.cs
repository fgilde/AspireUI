using System.Reflection;
using System.Text.Json;

namespace AspireUI.Server.Services;

public record CatalogParam(string Name, string Type, bool Required, string? Default, List<string>? Options, string? EnumTypeName, string Label);
public record CatalogOverload(List<CatalogParam> Params);
public record CatalogMethod(string Method, string Label, List<CatalogOverload> Overloads);
public record ResourceType(string AddMethod, string Label, string? Icon, string? Group, List<CatalogOverload> AddOverloads, List<CatalogMethod> Withs);

public class CatalogService
{
    private readonly Assembly[] _assemblies;
    private readonly Dictionary<string, JsonElement> _overlay;

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

    public IReadOnlyList<ResourceType> GetCatalog()
    {
        var methods = _assemblies.SelectMany(SafeTypes)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
            .ToList();

        // WithX methods (receiver IResourceBuilder<W>, returns IResourceBuilder<>)
        var withMethods = methods
            .Where(m => m.Name.StartsWith("With"))
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
            var hidden = over?.TryGetProperty("hidden", out var h) == true
                ? h.EnumerateArray().Select(x => x.GetString()).ToHashSet() : new HashSet<string?>();
            withs = withs.Where(w => !hidden.Contains(w.Method)).ToList();

            result.Add(new ResourceType(
                grp.Key,
                over?.TryGetProperty("label", out var lbl) == true ? lbl.GetString()! : grp.Key[3..],
                over?.TryGetProperty("icon", out var i) == true ? i.GetString() : null,
                over?.TryGetProperty("group", out var g) == true ? g.GetString() : "Other",
                addOverloads, withs));
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
                byName.Add(new CatalogMethod(grp.Key, grp.Key[4..], overloads)); // strip "With"
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
        // Nextended integrations (Slice 3 Task 2): force-load by name since we don't reference
        // a stable public type at compile time here; each assembly's AddX/WithX extensions get
        // picked up by the AppDomain scan below once loaded.
        foreach (var name in new[]
                 {
                     "Nextended.Aspire.Hosting.Supabase",
                     "Nextended.Aspire.Hosting.N8n",
                     "Nextended.Aspire.Hosting.LocalAI",
                 })
        {
            try { Assembly.Load(name); } catch { /* package not present/loadable; catalog just omits it */ }
        }
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Aspire.Hosting") == true
                     || a.GetName().Name?.StartsWith("Nextended.Aspire") == true)
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
