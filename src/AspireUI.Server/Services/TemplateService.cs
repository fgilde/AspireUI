using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record TemplateInfo(string Id, string Name, string Description);

// Demo templates — fixed stacks a user can drop in as a starting point.
// "local-ai-demo" mirrors the Nextended AiStack sample (CPU-safe subset: no GPU withs,
// no postgres/github-repo part).
public class TemplateService
{
    private static readonly TemplateInfo LocalAiDemoInfo = new(
        "local-ai-demo",
        "Local AI Demo",
        "Ollama + LocalAI + n8n wired together, mirroring the Nextended AiStack sample.");

    public IReadOnlyList<TemplateInfo> List() => [LocalAiDemoInfo];

    public StackModel? Create(string templateId) => templateId switch
    {
        "local-ai-demo" => LocalAiDemo(),
        _ => null,
    };

    private static string NewId() => "n" + Guid.NewGuid().ToString("n")[..8];

    private static StackModel LocalAiDemo()
    {
        var ollamaId = NewId();
        var localaiId = NewId();
        var n8nId = NewId();

        var ollama = new NodeModel(ollamaId, "ollama", "AddOllama", "ollama",
            [
                new WithCall("WithDataVolume", []),
                new WithCall("AddModel", ["\"llama3.2\""]),
                new WithCall("AddModel", ["\"nomic-embed-text\""]),
            ],
            80, 80, []);

        var localai = new NodeModel(localaiId, "localai", "AddLocalAI", "localai",
            [
                new WithCall("WithDataVolume", []),
                new WithCall("AddModel", ["KnownTextModel.Qwen3_8b"]),
                new WithCall("AddModel", ["KnownEmbeddingModel.BertEmbeddings"]),
                new WithCall("WithOpenWebUI", []),
            ],
            80, 300, []);

        var n8n = new NodeModel(n8nId, "n8n", "AddN8n", "n8n",
            [
                new WithCall("WithTimezone", ["\"Europe/Berlin\""]),
                new WithCall("WithEnvironment", ["\"OPENAI_API_BASE_URL\"", "localAiOpenAiBase"]),
                new WithCall("WithEnvironment", ["\"OPENAI_BASE_URL\"", "localAiOpenAiBase"]),
                new WithCall("WithEnvironment", ["\"OPENAI_API_KEY\"", "\"sk-local\""]),
                new WithCall("WithEnvironment", ["\"OLLAMA_BASE_URL\"", "ollama.Resource.PrimaryEndpoint"]),
                new WithCall("WithOwner", ["\"admin@localhost\"", "\"Test1234!\"", "\"Admin\""]),
            ],
            480, 190, []);

        return new StackModel(
            Guid.NewGuid().ToString("n"),
            "Local AI Demo",
            "net10.0",
            [ollama, localai, n8n],
            [
                new EdgeModel("e" + Guid.NewGuid().ToString("n")[..8], n8nId, ollamaId, "waitFor"),
                new EdgeModel("e" + Guid.NewGuid().ToString("n")[..8], n8nId, localaiId, "waitFor"),
            ],
            ["var localAiOpenAiBase = ReferenceExpression.Create($\"{localai.Resource.HttpEndpoint}/v1\");"],
            [],
            []);
    }
}
