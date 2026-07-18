using System.Reflection;
using System.Text.Json;

namespace AspireUI.Server.Services;

public record CatalogParam(string Name, string Type, bool Required, string? Default, List<string>? Options, string Label);
public record CatalogWith(string Method, string Label, List<CatalogParam> Params);
public record ResourceType(string AddMethod, string Label, string? Icon, string? Group, List<CatalogParam> AddParams, List<CatalogWith> Withs);

public class CatalogService
{
    private readonly Assembly[] _assemblies;
    private readonly Dictionary<string, JsonElement> _overlay;

    public CatalogService(params Assembly[] assemblies)
    {
        _assemblies = assemblies.Length > 0 ? assemblies : LoadDefault();
        _overlay = LoadOverlays();
    }

    public IReadOnlyList<ResourceType> GetCatalog()
    {
        var result = new List<ResourceType>();
        foreach (var asm in _assemblies)
        foreach (var type in SafeTypes(asm))
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!m.Name.StartsWith("Add")) continue;
            if (!ReturnsResourceBuilder(m.ReturnType)) continue;
            var p = m.GetParameters();
            if (p.Length == 0 || !IsAppBuilder(p[0].ParameterType)) continue; // extension on IDistributedApplicationBuilder

            var over = _overlay.TryGetValue(m.Name, out var o) ? o : (JsonElement?)null;
            result.Add(new ResourceType(
                m.Name,
                over?.TryGetProperty("label", out var lbl) == true ? lbl.GetString()! : m.Name[3..],
                over?.TryGetProperty("icon", out var i) == true ? i.GetString() : null,
                over?.TryGetProperty("group", out var g) == true ? g.GetString() : "Other",
                ParseParams(over, "addParams"),
                ParseWiths(over)));
        }
        // Dedup by AddMethod (same extension can appear via multiple overloads).
        return result.GroupBy(r => r.AddMethod).Select(gr => gr.First()).OrderBy(r => r.AddMethod).ToList();
    }

    private static List<CatalogParam> ParseParams(JsonElement? over, string prop)
    {
        var list = new List<CatalogParam>();
        if (over?.TryGetProperty(prop, out var arr) == true)
            foreach (var p in arr.EnumerateArray()) list.Add(ReadParam(p));
        return list;
    }

    private static CatalogParam ReadParam(JsonElement p) => new(
        p.GetProperty("name").GetString()!,
        p.TryGetProperty("type", out var t) ? t.GetString()! : "string",
        p.TryGetProperty("required", out var r) && r.GetBoolean(),
        p.TryGetProperty("default", out var d) ? d.GetString() : null,
        p.TryGetProperty("options", out var o) ? o.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
        p.TryGetProperty("label", out var l) ? l.GetString()! : p.GetProperty("name").GetString()!);

    private static List<CatalogWith> ParseWiths(JsonElement? over)
    {
        var list = new List<CatalogWith>();
        if (over?.TryGetProperty("withs", out var arr) == true)
            foreach (var w in arr.EnumerateArray())
                list.Add(new CatalogWith(
                    w.GetProperty("method").GetString()!,
                    w.TryGetProperty("label", out var l) ? l.GetString()! : w.GetProperty("method").GetString()!,
                    w.TryGetProperty("params", out var ps) ? ps.EnumerateArray().Select(ReadParam).ToList() : []));
        return list;
    }

    private static bool ReturnsResourceBuilder(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("IResourceBuilder");

    private static bool IsAppBuilder(Type t) => t.Name == "IDistributedApplicationBuilder";

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
    }

    private static Assembly[] LoadDefault()
    {
        // PackageReference alone doesn't load an assembly into the AppDomain until code
        // touches a type from it. Force-load each hosting integration we ship a catalog
        // overlay for, then scan whatever "Aspire.Hosting*" assemblies are now loaded.
        _ = typeof(Aspire.Hosting.IDistributedApplicationBuilder).Assembly;
        _ = typeof(Aspire.Hosting.RedisBuilderExtensions).Assembly;
        _ = typeof(Aspire.Hosting.PostgresBuilderExtensions).Assembly;

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Aspire.Hosting") == true)
            .ToArray();
    }

    private Dictionary<string, JsonElement> LoadOverlays()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "catalog");
        var map = new Dictionary<string, JsonElement>();
        if (!Directory.Exists(dir)) return map;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(f));
            foreach (var prop in doc.RootElement.EnumerateObject())
                map[prop.Name] = prop.Value.Clone();
        }
        return map;
    }
}
