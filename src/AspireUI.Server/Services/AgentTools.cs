using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace AspireUI.Server.Services;

/// <summary>
/// The permission a tool needs. Sits on the tool method so there is one place that says it: the MCP
/// path enforces it, and the chat only offers the model what the person asking may actually do.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NeedsPermAttribute(string perm) : Attribute
{
    public string Perm { get; } = perm;
}

/// <summary>One callable tool, as both MCP and the chat see it.</summary>
public record AgentTool(string Name, string Description, string Permission, MethodInfo Method)
{
    public IReadOnlyList<ParameterInfo> Parameters => Method.GetParameters();
}

/// <summary>
/// The agent tool surface, read off <see cref="McpTools"/> by reflection. One implementation, two
/// front doors: an MCP client over <c>/api/mcp</c>, and the in-app chat. Anything else would be a
/// second list of tools to keep in step with the first.
/// </summary>
public static class AgentTools
{
    private static readonly Lazy<List<AgentTool>> Cached = new(() => typeof(McpTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .Select(m => new AgentTool(
            SnakeCase(m.Name),
            m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? m.Name,
            m.GetCustomAttribute<NeedsPermAttribute>()?.Perm
                ?? throw new InvalidOperationException($"{m.Name} is a tool with no [NeedsPerm]"),
            m))
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToList());

    public static IReadOnlyList<AgentTool> All => Cached.Value;

    public static AgentTool? Find(string name) =>
        All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The same naming the MCP library uses, so a tool has one name wherever it is called from.</summary>
    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The tools this user may use, as OpenAI-style function specifications. Filtering here rather
    /// than refusing later is deliberate: a model that is not offered a tool does not try it, and
    /// does not explain to the user that it was not allowed to.
    /// </summary>
    public static JsonArray SpecsFor(Models.User? user)
    {
        var array = new JsonArray();
        foreach (var tool in All.Where(t => Models.Perm.Has(user, t.Permission)))
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var p in tool.Parameters)
            {
                properties[p.Name!] = new JsonObject
                {
                    ["type"] = JsonType(p.ParameterType),
                    ["description"] = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? p.Name,
                };
                if (!p.HasDefaultValue) required.Add(p.Name!);
            }
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required,
                    },
                },
            });
        }
        return array;
    }

    private static string JsonType(Type t) =>
        t == typeof(bool) ? "boolean"
        : t == typeof(int) || t == typeof(long) ? "integer"
        : t == typeof(double) || t == typeof(float) || t == typeof(decimal) ? "number"
        : "string";

    /// <summary>
    /// Calls a tool with the arguments the model chose. Missing arguments fall back to the method's
    /// own defaults; a model that invents a parameter is ignored rather than being an error, because
    /// the alternative is a conversation about json instead of about the app.
    /// </summary>
    public static object? Invoke(AgentTool tool, McpTools tools, JsonObject? arguments)
    {
        var args = tool.Parameters.Select(p =>
        {
            var raw = arguments?[p.Name!];
            if (raw is null) return p.HasDefaultValue ? p.DefaultValue : null;
            try
            {
                return p.ParameterType == typeof(string) ? raw.GetValue<object>()?.ToString()
                    : raw.Deserialize(p.ParameterType);
            }
            catch { return p.HasDefaultValue ? p.DefaultValue : null; }
        }).ToArray();

        return tool.Method.Invoke(tools, args);
    }

    /// <summary>What the model gets back: the tool's own result as json, or the reason it failed.</summary>
    public static string Result(object? value) => value switch
    {
        null => "null",
        string s => s,
        _ => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };
}
