namespace AspireUI.Server.Models;

public record User(string Id, string Username, string PasswordHash, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false);

// Never carries the password hash.
public record UserDto(string Id, string Username, bool IsAdmin, string CreatedAt,
    bool Disabled = false, bool MustChangePassword = false);
