namespace AspireUI.Server.Models;

public record StackModel(
    string Id,
    string Name,
    string TargetFramework,
    List<NodeModel> Nodes,
    List<EdgeModel> Edges,
    List<string> RawStatements,
    List<ExtraFile> ExtraFiles,
    List<PackageRef> ExtraPackages);

public record NodeModel(
    string Id,
    string VarName,       // C# identifier, e.g. "db"
    string AddMethod,     // e.g. "AddPostgres"
    string ResourceName,  // string arg, e.g. "db"
    List<WithCall> WithCalls,
    double X,
    double Y,
    List<string> AddArgs); // positional args after ResourceName, raw C# literals e.g. "\"nginx\""

public record EdgeModel(string Id, string FromNodeId, string ToNodeId, string Kind); // Kind = "reference" | "waitFor"

public record WithCall(string Method, List<string> Args); // Args = raw C# literals, e.g. "\"vol\""

public record ExtraFile(string Name, string Content); // Name = relative path, e.g. "Helpers/Foo.cs"

public record PackageRef(string Id, string Version);
