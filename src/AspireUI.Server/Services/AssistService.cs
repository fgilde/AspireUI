using System.Text.Json;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record AssistResult(string Reply, StackModel? Stack, bool Ok);

public class AssistService(IChatClient chat, CatalogService catalog)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<AssistResult> AssistAsync(StackModel stack, string prompt, AppSettings settings)
    {
        var system = BuildSystemPrompt(stack);
        var raw = await chat.CompleteAsync(system, prompt, settings);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var reply = doc.RootElement.GetProperty("reply").GetString() ?? "";
            var parsed = doc.RootElement.GetProperty("stack").Deserialize<StackModel>(JsonOpts);
            // System.Text.Json fills omitted list properties with null rather than failing
            // deserialization (non-nullable annotations aren't enforced at runtime here), so a
            // response that skips e.g. "nodes"/"edges" would otherwise look Ok=true and crash
            // downstream (CodeGenService iterating a null list) outside the endpoint's try/catch.
            // Treat that the same as any other unusable model reply: a parse failure.
            var incomplete = parsed is null || parsed.Nodes is null || parsed.Edges is null
                || parsed.RawStatements is null || parsed.ExtraFiles is null || parsed.ExtraPackages is null;
            return incomplete ? new AssistResult(raw, null, false) : new AssistResult(reply, parsed!, true);
        }
        catch (Exception)
        {
            // Any shape mismatch (bad JSON, missing "reply"/"stack", wrong types) is a parse
            // failure from the model's point of view: surface the raw text so the user can see
            // what came back, and leave the stack untouched.
            return new AssistResult(raw, null, false);
        }
    }

    // ponytail: catalog summary is addMethod + label + group + addParam names only (no withs, no
    // overload details) to keep the system prompt small — the full reflected catalog is ~50
    // resources deep with many With* methods each, which would blow the token budget of a
    // small-context model. Ceiling: if the model still needs With* names, add them per-resource
    // only when that resource is already used in the current stack, not for the whole catalog.
    private string BuildSystemPrompt(StackModel stack)
    {
        var summary = string.Join("\n", catalog.GetCatalog().Select(r =>
        {
            var addParams = r.AddOverloads.SelectMany(o => o.Params).Select(p => p.Name).Distinct();
            return $"- {r.AddMethod} \"{r.Label}\" [{r.Group}] params: {string.Join(", ", addParams)}";
        }));

        var stackJson = JsonSerializer.Serialize(stack, JsonOpts);

        return $$"""
            You edit an Aspire AppHost stack model (a graph of resource nodes/edges) from a
            natural-language request.

            Available resource types (addMethod "label" [group] params: ...):
            {{summary}}

            Current stack (JSON):
            {{stackJson}}

            Rules:
            - Respond with ONLY JSON of the shape {"reply": string, "stack": <StackModel>} - no
              markdown fences, no extra prose outside that JSON.
            - "stack" must be a complete StackModel with the same fields as the current stack
              above (id, name, targetFramework, nodes, edges, rawStatements, extraFiles,
              extraPackages).
            - Preserve existing node/edge id values for anything you didn't change or remove.
            - Only use addMethod values that appear in the catalog above.
            - "reply" is a short, human-readable summary of what you changed.
            """;
    }
}
