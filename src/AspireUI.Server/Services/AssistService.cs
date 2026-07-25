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
            var incomplete = parsed is null || parsed.Nodes is null || parsed.Edges is null
                || parsed.RawStatements is null || parsed.ExtraFiles is null || parsed.ExtraPackages is null;
            if (incomplete) return new AssistResult(raw, null, false);
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
            return new AssistResult(raw, null, false);
        }
    }

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

    public async Task<(bool Ok, string? Reason, string? Code)> RewriteCodeAsync(string currentCode, string prompt, AppSettings settings)
    {
        var addMethods = string.Join(", ", catalog.GetCatalog().Select(r => r.AddMethod).Distinct().OrderBy(x => x));
        var system = $$"""
            You edit a .NET Aspire AppHost Program.cs to satisfy the user's request. Return ONLY the full
            modified Program.cs — no markdown fences, no prose, no explanation. Keep it compilable and
            keep the parts the user didn't ask to change. Only use AddX methods from this catalog:
            {{addMethods}}.

            Current Program.cs:
            {{currentCode}}
            """;
        var raw = (await chat.CompleteAsync(system, prompt, settings)).Trim();
        var code = StripFences(raw);
        if (string.IsNullOrWhiteSpace(code) || !code.Contains("builder", StringComparison.Ordinal))
            return (false, "The AI didn't return usable code.", null);
        return (true, null, code);
    }

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
