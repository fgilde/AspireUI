namespace AspireUI.Server.Models;

// ViewModes = which UI modes the user may use ("full" = builder, "simple" = appliance/app-store).
// Null/empty means both (permissive default); an admin can restrict a user to one.
// Permissions = granted capability tokens (the seed of a future roles/rights system). Currently only
// "open-editor". Null = never configured → permissive default (everything allowed). Non-null = explicit
// grant list, so a token's ABSENCE denies it. Admins are always allowed regardless.
public record User(string Id, string Username, string PasswordHash, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false, List<string>? ViewModes = null,
    List<string>? Permissions = null);

// Never carries the password hash.
public record UserDto(string Id, string Username, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false, List<string>? ViewModes = null,
    List<string>? Permissions = null);

// Known permission tokens (extend as roles/rights grow).
public static class Perm
{
    public const string OpenEditor = "open-editor";
    public static readonly string[] All = { OpenEditor };
}
