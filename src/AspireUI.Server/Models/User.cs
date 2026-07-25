namespace AspireUI.Server.Models;

public record User(string Id, string Username, string PasswordHash, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false, List<string>? ViewModes = null,
    List<string>? Permissions = null);

public record UserDto(string Id, string Username, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false, List<string>? ViewModes = null,
    List<string>? Permissions = null);

public static class Perm
{
    public const string OpenEditor = "open-editor";
    public static readonly string[] All = { OpenEditor };
}
