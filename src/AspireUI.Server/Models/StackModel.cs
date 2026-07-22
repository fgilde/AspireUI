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
    // Canvas-only annotations — never affect the generated code. Sticky notes + labeled boundary
    // rectangles the user draws to document/organize the graph. Optional so all existing constructor
    // calls (templates, importers, tests, AI) keep working.
    List<StackNote>? Notes = null,
    List<StackGroup>? Groups = null,
    // Creation metadata (set once when a stack is first created; preserved on edits). Shown on the
    // overview cards. Never affects generated code.
    string? CreatedAt = null,
    string? CreatedBy = null);

public record StackNote(string Id, string Text, double X, double Y);
public record StackGroup(string Id, string Label, double X, double Y, double Width, double Height, string? Color);

public record NodeModel(
    string Id,
    string VarName,       // C# identifier, e.g. "db"
    string AddMethod,     // e.g. "AddPostgres"
    string ResourceName,  // string arg, e.g. "db"
    List<WithCall> WithCalls,
    double X,
    double Y,
    List<string> AddArgs, // positional args after ResourceName, raw C# literals e.g. "\"nginx\""
    // Composite "setup"/macro builder-extension (e.g. Nextended's AddObservabilityStack): emitted
    // as a bare statement `builder.AddX(args)` — no `var`, no name arg, returns the builder not a
    // resource. AddArgs then holds ALL args (resource-reference varNames, configure lambda, …).
    bool Composite = false,
    // Extra `using` namespaces this node's statement needs (composite nodes carry their own, since
    // discovered macro extensions aren't in the overlay's AddMethod->usings map).
    List<string>? Usings = null,
    // Canvas-only: id of the app node that dropped this one as a preset companion. Lets smart-delete
    // recognize "the app + exactly its companions" as a unit. Never affects generated code.
    string? SpawnedBy = null,
    // Canvas-only: icon key (a preset's brand icon) so a preset-dropped AddContainer shows the app's
    // real icon instead of the generic Docker one. Purely visual; never affects generated code.
    string? Icon = null);

public record EdgeModel(string Id, string FromNodeId, string ToNodeId, string Kind); // Kind = "reference" | "waitFor"

public record WithCall(string Method, List<string> Args); // Args = raw C# literals, e.g. "\"vol\""

public record ExtraFile(string Name, string Content); // Name = relative path, e.g. "Helpers/Foo.cs"

public record PackageRef(string Id, string Version);
