namespace AspireUI.Server.Models;

public record StackModel(
    string Id,
    string Name,
    string TargetFramework,
    List<NodeModel> Nodes,
    List<EdgeModel> Edges,
    List<string> RawStatements,
    List<ExtraFile> ExtraFiles,
    List<PackageRef> ExtraPackages,
    List<StackNote>? Notes = null,
    List<StackGroup>? Groups = null,
    string? CreatedAt = null,
    string? CreatedBy = null,
    string? HostingUrlPath = null,
    bool RunAsIs = false,
    string? AppHostProject = null);

public record StackNote(string Id, string Text, double X, double Y);
public record StackGroup(string Id, string Label, double X, double Y, double Width, double Height, string? Color);

public record NodeModel(
    string Id,
    string VarName,
    string AddMethod,
    string ResourceName,
    List<WithCall> WithCalls,
    double X,
    double Y,
    List<string> AddArgs,
    bool Composite = false,
    List<string>? Usings = null,
    string? SpawnedBy = null,
    string? Icon = null);

public record EdgeModel(string Id, string FromNodeId, string ToNodeId, string Kind);

public record WithCall(string Method, List<string> Args);

public record ExtraFile(string Name, string Content);

public record PackageRef(string Id, string Version);
