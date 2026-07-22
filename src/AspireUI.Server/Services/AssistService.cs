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

    // Research a URL (github/dockerhub/docs) and, if the thing is runnable as a container app, draft a
    // preset (image/port/env/params/companions/volumes) for review. The model uses the URL + its own
    // knowledge (it does not fetch the page). Returns (ok, reason, preset).
    public async Task<(bool Ok, string? Reason, ContainerPreset? Preset)> AutoPresetAsync(string url, AppSettings settings)
    {
        var system = """
            You turn a project URL (GitHub repo, Docker Hub image, or docs page) into an AspireUI
            container "preset" so it can be dropped onto a canvas. Decide from the URL and your own
            knowledge whether the project can run as a Docker container.

            Respond with ONLY a JSON object, no markdown fences, no prose. Either:
              {"ok": false, "reason": "<short why not>"}
            or a preset object with these fields (omit ones that don't apply):
              {
                "id": "<kebab-id>", "label": "<name>", "group": "Custom",
                "image": "<docker image:tag>", "port": <main http port int>,
                "description": "<one line>",
                "env": [["KEY","value"], ...],
                "params": [{"key":"password","env":"ENV_NAME","default":"...","secret":true}, ...],
                "companions": [{"key":"db","addMethod":"AddContainer","resourceName":"<app>-db","image":"postgres:16","port":5432,"role":"postgres"}, ...],
                "volumes": [["data","/container/path"], ...]
              }
            Rules: only a real, existing image. Put passwords/keys/secrets in "params" (secret:true),
            plain settings in "env". Use companions with a "role" (postgres/redis/mongo/meilisearch/llm)
            for required backends. Prefer an official image. If unsure it can containerize, return ok:false.
            """;
        var raw = await chat.CompleteAsync(system, $"URL: {url}", settings);
        var json = ExtractJsonObject(raw);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ok", out var okp) && okp.ValueKind == JsonValueKind.False)
                return (false, doc.RootElement.TryGetProperty("reason", out var rp) ? rp.GetString() : "Not feasible.", null);
            var preset = JsonSerializer.Deserialize<ContainerPreset>(json, JsonOpts);
            if (preset is null || string.IsNullOrWhiteSpace(preset.Image))
                return (false, "The AI didn't return a usable container image.", null);
            preset = preset with
            {
                Id = string.IsNullOrWhiteSpace(preset.Id) ? "ai-" + Guid.NewGuid().ToString("n")[..6] : preset.Id,
                Label = string.IsNullOrWhiteSpace(preset.Label) ? preset.Id : preset.Label,
                Group = string.IsNullOrWhiteSpace(preset.Group) ? "Custom" : preset.Group,
                Port = preset.Port <= 0 ? 80 : preset.Port,
            };
            return (true, null, preset);
        }
        catch (Exception ex) { return (false, $"Could not parse the AI reply: {ex.Message}", null); }
    }

    private static string ExtractJsonObject(string s)
    {
        int i = s.IndexOf('{'), j = s.LastIndexOf('}');
        return i >= 0 && j > i ? s[i..(j + 1)] : s;
    }

    // Read-only: explain the current stack for someone learning Aspire. Returns Markdown prose
    // (no stack changes, no JSON contract).
    public async Task<string> ExplainAsync(StackModel stack, AppSettings settings)
    {
        var stackJson = JsonSerializer.Serialize(stack, JsonOpts);
        var system = $$"""
            You are a .NET Aspire expert helping a developer understand an AppHost stack.
            Explain the stack below in clear Markdown with short sections: what each resource is and
            does, how they're wired together (references / wait-for ordering), roughly what the
            generated Program.cs looks like, and any practical suggestions or gotchas. Teach — assume
            the reader is still learning Aspire. Do NOT output JSON or code fences around the whole reply.

            Stack (JSON):
            {{stackJson}}
            """;
        return await chat.CompleteAsync(system, "Explain this stack.", settings);
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
