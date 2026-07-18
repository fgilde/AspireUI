using System.Reflection;
using System.Text.Json;

namespace AspireUI.Server.Services;

public record CatalogWith(string Method, List<string> Params);
public record ResourceType(string AddMethod, string Label, string? Icon, string? Group, List<CatalogWith> Withs);

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
            var withs = (over?.TryGetProperty("withs", out var w) == true)
                ? w.EnumerateArray().Select(x => new CatalogWith(x.GetString()!, [])).ToList()
                : new List<CatalogWith>();
            result.Add(new ResourceType(
                m.Name,
                over?.GetProperty("label").GetString() ?? m.Name[3..],
                over?.TryGetProperty("icon", out var i) == true ? i.GetString() : null,
                over?.TryGetProperty("group", out var g) == true ? g.GetString() : "Other",
                withs));
        }
        // Dedup by AddMethod (same extension can appear via multiple overloads).
        return result.GroupBy(r => r.AddMethod).Select(gr => gr.First()).OrderBy(r => r.AddMethod).ToList();
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
