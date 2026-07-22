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
            if (incomplete) return new AssistResult(raw, null, false);
            // The model often omits per-node list props (addArgs/withCalls) → null after deserialize.
            // Coerce so codegen/canvas never hit a null list.
            parsed = parsed! with
            {
                Nodes = parsed.Nodes.Select(n => n with
                {
                    AddArgs = n.AddArgs ?? [],
                    WithCalls = (n.WithCalls ?? []).Select(w => w with { Args = w.Args ?? [] }).ToList(),
                }).ToList(),
            };
            return new AssistResult(reply, parsed, true);
        }
        catch (Exception)
        {
            // Any shape mismatch (bad JSON, missing "reply"/"stack", wrong types) is a parse
            // failure from the model's point of view: surface the raw text so the user can see
            // what came back, and leave the stack untouched.
            return new AssistResult(raw, null, false);
        }
    }

    // Research a project (URL + fetched page/README content) and write the Aspire C# builder statements
    // to add + wire it — open-minded: a GitHub repo → AddGithubRepository (built & run directly, no
    // published image needed), a known image → AddContainer, plus companions (AddPostgres/…) and env/
    // reference/waitFor wiring. Returns the raw C# body; the caller parses it into nodes/edges via the
    // normal import path. Returns (ok, reason, code).
    public async Task<(bool Ok, string? Reason, string? Code)> AutoAddCodeAsync(string url, string context, AppSettings settings)
    {
        var addMethods = string.Join(", ", catalog.GetCatalog().Select(r => r.AddMethod).Distinct().OrderBy(x => x));
        var system = $$"""
            You extend a .NET Aspire AppHost. Given a project (URL + fetched content below), write ONLY
            the C# builder statements that add and wire it. No `var builder = ...`, no `builder.Build()`,
            no using directives, no markdown fences, no prose.

            Be resourceful and open-minded — you do NOT need a published Docker image:
            - A GitHub repo you can build & run directly: builder.AddGithubRepository("name", "<repo url>")
              then .WithHttpEndpoint(...), .WithExternalHttpEndpoints(), .WithEnvironment(...), .WaitFor(...).
            - A known published image: builder.AddContainer("name", "image:tag").WithHttpEndpoint(targetPort: N).
            - Add required backends as companions and wire them: e.g.
                var db = builder.AddPostgres("postgres").AddDatabase("appdb");
                app.WithReference(db).WaitFor(db);
              or set a connection string via .WithEnvironment("DATABASE_URL", ...).
            - Read the fetched content to infer the framework, ports, needed services and env vars.

            Only use AddX methods from this catalog: {{addMethods}}.
            Prefer the simplest wiring that would actually run. If it is genuinely impossible, output ONLY
            one line: // CANNOT: <short reason>.

            Project URL: {{url}}

            Fetched content (truncated):
            {{context}}
            """;
        var raw = (await chat.CompleteAsync(system, $"Write the Aspire builder statements for: {url}", settings)).Trim();
        var code = StripFences(raw);
        if (code.TrimStart().StartsWith("// CANNOT", StringComparison.OrdinalIgnoreCase))
            return (false, code.TrimStart()["// CANNOT:".Length..].Trim().TrimStart(':').Trim(), null);
        if (string.IsNullOrWhiteSpace(code) || !code.Contains(".Add", StringComparison.Ordinal))
            return (false, "The AI didn't produce usable Aspire code.", null);
        return (true, null, code);
    }

    // Some models wrap code in ```csharp ... ``` fences despite instructions; strip them.
    private static string StripFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl < 0) return s;
        var body = s[(firstNl + 1)..];
        var lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (lastFence >= 0 ? body[..lastFence] : body).Trim();
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
