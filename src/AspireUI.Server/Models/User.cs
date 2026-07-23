namespace AspireUI.Server.Models;

// ViewModes = which UI modes the user may use ("full" = builder, "simple" = appliance/app-store).
// Null/empty means both (permissive default); an admin can restrict a user to one.
public record User(string Id, string Username, string PasswordHash, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false, List<string>? ViewModes = null);

// Never carries the password hash.
public record UserDto(string Id, string Username, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false, List<string>? ViewModes = null);
