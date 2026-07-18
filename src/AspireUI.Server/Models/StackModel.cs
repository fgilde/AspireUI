namespace AspireUI.Server.Models;

public record StackModel(
    string Id,
    string Name,
    string TargetFramework,
    List<NodeModel> Nodes,
    List<EdgeModel> Edges);

public record NodeModel(
    string Id,
    string VarName,       // C# identifier, e.g. "db"
    string AddMethod,     // e.g. "AddPostgres"
    string ResourceName,  // string arg, e.g. "db"
    List<WithCall> WithCalls,
    double X,
    double Y);

public record EdgeModel(string Id, string FromNodeId, string ToNodeId, string Kind); // Kind = "reference"

public record WithCall(string Method, List<string> Args); // Args = raw C# literals, e.g. "\"vol\""
