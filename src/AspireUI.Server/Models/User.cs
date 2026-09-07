namespace AspireUI.Server.Models;

public record User(string Id, string Username, string PasswordHash, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false, List<string>? ViewModes = null,
    List<string>? Permissions = null);

public record UserDto(string Id, string Username, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false, List<string>? ViewModes = null,
    List<string>? Permissions = null);

/// <summary>
/// What a non-admin may do. An admin has all of them; a user whose list is <c>null</c> also has all
/// of them, because installs that predate this list had no way to say otherwise and their users were
/// allowed everything an admin was not. An empty list means: may look, may not touch.
/// </summary>
public static class Perm
{
    /// <summary>Open the builder and create, change or delete stacks.</summary>
    public const string OpenEditor = "open-editor";

    /// <summary>Install, start, stop, update, move, undeploy an app; take and restore backups.</summary>
    public const string Deploy = "deploy";

    /// <summary>Change a hosted app's environment, ports and domain.</summary>
    public const string Configure = "configure";

    /// <summary>Browse and download files in an app's volumes.</summary>
    public const string Files = "files";

    /// <summary>Upload, rename, delete and create folders in an app's volumes. Separate, so a read-only file browser is possible.</summary>
    public const string FilesWrite = "files-write";

    /// <summary>Run commands in an app's containers.</summary>
    public const string Terminal = "terminal";

    /// <summary>Add, change and remove deploy targets.</summary>
    public const string Targets = "targets";

    /// <summary>Manage the app store: sources and which apps are hidden.</summary>
    public const string Store = "store";

    /// <summary>Global settings: AI, proxy, notifications, backups, dashboard, import.</summary>
    public const string Settings = "settings";

    /// <summary>Prune the Docker host: unused volumes and images.</summary>
    public const string Docker = "docker";

    /// <summary>Manage users and their permissions.</summary>
    public const string Users = "users";

    /// <summary>Read the activity log: who did what, to which app, and when.</summary>
    public const string Audit = "audit";

    public static readonly string[] All =
    [
        OpenEditor, Deploy, Configure, Files, FilesWrite, Terminal, Targets, Store, Settings, Docker, Users, Audit,
    ];

    /// <summary>Everything an app operator needs, and nothing that reaches past the apps themselves.</summary>
    public static readonly string[] Operator =
    [
        Deploy, Configure, Files, FilesWrite, Terminal, OpenEditor,
    ];

    /// <summary>Look at the apps, their logs and their files. Change nothing.</summary>
    public static readonly string[] Viewer = [Files];

    /// <summary>
    /// What a newly created user gets when nobody says otherwise: exactly what a non-admin could do
    /// before there were permissions for the rest. New users must not inherit "everything" — that
    /// grandfather rule is for accounts that already existed.
    /// </summary>
    public static readonly string[] Default = [OpenEditor, Deploy, Configure];

    /// <summary>True when this user may do <paramref name="perm"/>. See the note on the class.</summary>
    public static bool Has(User? user, string perm) =>
        user is { Disabled: false } && (user.IsAdmin || user.Permissions is null || user.Permissions.Contains(perm));
}
