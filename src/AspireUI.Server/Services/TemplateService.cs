using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record TemplateInfo(string Id, string Name, string Description);

// Demo templates — fixed stacks a user can drop in as a starting point.
// "local-ai-demo" mirrors the Nextended AiStack sample (CPU-safe subset: no GPU withs,
// no postgres/github-repo part).
public class TemplateService
{
    public IReadOnlyList<TemplateInfo> List() =>
    [
        new("local-ai-demo", "Local AI Demo", "Ollama + LocalAI + n8n wired together (Nextended AiStack sample)."),
        new("web-backend", "Web backend stack", "Postgres + Redis + RabbitMQ — the usual data/cache/queue trio for an API."),
        new("elastic-kibana", "Elasticsearch + Kibana", "Elasticsearch with a Kibana UI wired to it."),
        new("kafka-ui", "Kafka + UI", "Kafka broker with the provectus Kafka-UI dashboard."),
        new("keycloak-auth", "Keycloak + Postgres", "Keycloak identity server backed by a Postgres database."),
        new("observability", "Observability (Seq)", "A Seq server for structured logs — point your apps at it."),
    ];

    public StackModel? Create(string templateId) => templateId switch
    {
        "local-ai-demo" => LocalAiDemo(),
        "web-backend" => WebBackend(),
        "elastic-kibana" => ElasticKibana(),
        "kafka-ui" => KafkaUi(),
        "keycloak-auth" => KeycloakAuth(),
        "observability" => Observability(),
        _ => null,
    };

    private static string NewId() => "n" + Guid.NewGuid().ToString("n")[..8];
    private static string Eid() => "e" + Guid.NewGuid().ToString("n")[..8];
    private static NodeModel Node(string varName, string add, int x, int y, List<WithCall>? withs = null, List<string>? args = null) =>
        new(NewId(), varName, add, varName, withs ?? [], x, y, args ?? []);
    private static StackModel Stack(string name, List<NodeModel> nodes, List<EdgeModel>? edges = null, List<string>? raws = null) =>
        new(Guid.NewGuid().ToString("n"), name, "net10.0", nodes, edges ?? [], raws ?? [], [], []);

    private static StackModel WebBackend()
    {
        var db = Node("postgres", "AddPostgres", 100, 80);
        var cache = Node("cache", "AddRedis", 100, 220);
        var queue = Node("queue", "AddRabbitMQ", 100, 360);
        return Stack("Web backend", [db, cache, queue]);
    }

    private static StackModel ElasticKibana()
    {
        var es = Node("elastic", "AddElasticsearch", 100, 120);
        var kibana = Node("kibana", "AddContainer", 460, 120,
            [
                new WithCall("WithHttpEndpoint", ["port: 5601", "targetPort: 5601"]),
                new WithCall("WithEnvironment", ["\"ELASTICSEARCH_HOSTS\"", "elastic.GetEndpoint(\"http\")"]),
            ],
            ["\"docker.elastic.co/kibana/kibana:8.15.0\""]);
        return Stack("Elasticsearch + Kibana", [es, kibana],
            [new EdgeModel(Eid(), kibana.Id, es.Id, "waitFor")]);
    }

    private static StackModel KafkaUi()
    {
        var kafka = Node("kafka", "AddKafka", 100, 120);
        var ui = Node("kafkaUi", "AddContainer", 460, 120,
            [
                new WithCall("WithHttpEndpoint", ["port: 8080", "targetPort: 8080"]),
                new WithCall("WithEnvironment", ["\"KAFKA_CLUSTERS_0_NAME\"", "\"local\""]),
                new WithCall("WithEnvironment", ["\"KAFKA_CLUSTERS_0_BOOTSTRAPSERVERS\"", "kafka.GetEndpoint(\"tcp\")"]),
            ],
            ["\"provectuslabs/kafka-ui:latest\""]);
        return Stack("Kafka + UI", [kafka, ui],
            [new EdgeModel(Eid(), ui.Id, kafka.Id, "waitFor")]);
    }

    private static StackModel KeycloakAuth()
    {
        var db = Node("postgres", "AddPostgres", 100, 220);
        var kc = Node("keycloak", "AddKeycloak", 460, 120);
        return Stack("Keycloak + Postgres", [db, kc],
            [
                new EdgeModel(Eid(), kc.Id, db.Id, "waitFor"),
                new EdgeModel(Eid(), kc.Id, db.Id, "reference"),
            ]);
    }

    private static StackModel Observability()
    {
        var seq = Node("seq", "AddSeq", 120, 120);
        return Stack("Observability (Seq)", [seq]);
    }

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
