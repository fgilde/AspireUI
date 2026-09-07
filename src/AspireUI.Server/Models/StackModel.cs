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
    string? AppHostProject = null,
    bool FromGit = false,
    bool HasSource = false,
    string? ExpireAt = null,
    string? ClonedFrom = null,
    AppLimits? Limits = null,
    List<AppHealthcheck>? Healthchecks = null,
    List<AppSchedule>? Schedules = null);

/// <summary>
/// Something the app should do by itself, on a clock: restart, stop, start, update (pull and
/// recreate), check for updates (report only) or back up. Either daily at
/// <paramref name="AtHour"/>:<paramref name="AtMinute"/> (UTC) or every
/// <paramref name="EveryHours"/> hours. <paramref name="Days"/> narrows a daily schedule to
/// <c>mon,wed,fri</c>. Auto-update is a schedule like any other, which is why there is no second
/// mechanism for it.
/// </summary>
public record AppSchedule(string Action, int? EveryHours = null, int? AtHour = null, int AtMinute = 0,
    string? Days = null, bool Enabled = true);

/// <summary>
/// What a hosted app may use. Applied to every container of the app, because "this app may have half
/// a core" is the question people actually have; per-service caps are what the compose file is for.
/// Null means no limit — the same as not writing the key at all.
/// </summary>
public record AppLimits(double? Cpus = null, int? MemoryMb = null, int? PidsLimit = null, string? Restart = null)
{
    public bool IsEmpty => Cpus is null && MemoryMb is null && PidsLimit is null && string.IsNullOrWhiteSpace(Restart);
}

/// <summary>
/// A health check for one of the app's containers, for images that ship none. <paramref name="Test"/>
/// is a shell command; a zero exit means healthy.
/// </summary>
public record AppHealthcheck(string Service, string Test, int IntervalSec = 30, int TimeoutSec = 5,
    int Retries = 3, int StartPeriodSec = 10);

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
