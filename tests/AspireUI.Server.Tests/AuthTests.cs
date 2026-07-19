using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Identity;

public class PasswordHasherTests
{
    private static readonly User Placeholder = new("", "", "", false, "");

    [Fact]
    public void HashPassword_NeverEqualsPlaintext()
    {
        var hasher = new PasswordHasher<User>();
        var hash = hasher.HashPassword(Placeholder, "correct horse battery staple");
        Assert.NotEqual("correct horse battery staple", hash);
    }

    [Fact]
    public void VerifyHashedPassword_CorrectPassword_Succeeds()
    {
        var hasher = new PasswordHasher<User>();
        var hash = hasher.HashPassword(Placeholder, "correct horse battery staple");
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(Placeholder, hash, "correct horse battery staple"));
    }

    [Fact]
    public void VerifyHashedPassword_WrongPassword_Fails()
    {
        var hasher = new PasswordHasher<User>();
        var hash = hasher.HashPassword(Placeholder, "correct horse battery staple");
        Assert.Equal(PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(Placeholder, hash, "wrong password"));
    }
}

// Uses NoAuthTestFactory (no auto-auth) to exercise the real cookie setup/login/401 flow — the
// opposite of every other integration test class, which relies on TestWebAppFactory's auto-auth
// so they keep passing unmodified now that app endpoints require authentication.
[Collection("ServerIntegration")]
public class AuthTests : IClassFixture<NoAuthTestFactory>
{
    private readonly NoAuthTestFactory _f;
    public AuthTests(NoAuthTestFactory f) => _f = f;

    [Fact]
    public async Task FullAuthFlow_SetupLoginLogout_AndAppEndpointGate()
    {
        var freshClient = _f.CreateClient();

        // Fresh DB: no users yet.
        var status = await freshClient.GetFromJsonAsync<AuthStatusDto>("/auth/status");
        Assert.True(status!.NeedsSetup);
        Assert.False(status.Authenticated);

        // App endpoint without a session -> 401.
        var unauthed = await freshClient.GetAsync("/stacks");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthed.StatusCode);

        // First-run setup creates the admin and signs in (cookie carried by this client).
        var setup = await freshClient.PostAsJsonAsync("/auth/setup", new { username = "admin", password = "supersecret1" });
        setup.EnsureSuccessStatusCode();
        var created = await setup.Content.ReadFromJsonAsync<UserDto>();
        Assert.Equal("admin", created!.Username);
        Assert.True(created.IsAdmin);

        var afterSetup = await freshClient.GetFromJsonAsync<AuthStatusDto>("/auth/status");
        Assert.False(afterSetup!.NeedsSetup);
        Assert.True(afterSetup.Authenticated);

        // App endpoint now works with the setup-issued cookie.
        var authedStacks = await freshClient.GetAsync("/stacks");
        Assert.Equal(HttpStatusCode.OK, authedStacks.StatusCode);

        // Second setup attempt -> 409 (users table no longer empty).
        var secondSetup = await _f.CreateClient()
            .PostAsJsonAsync("/auth/setup", new { username = "admin2", password = "supersecret1" });
        Assert.Equal(HttpStatusCode.Conflict, secondSetup.StatusCode);

        // Login: wrong password -> 401 generic.
        var badLogin = await _f.CreateClient()
            .PostAsJsonAsync("/auth/login", new { username = "admin", password = "wrongpassword" });
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);

        // Login: right password -> 200 + authenticated.
        var loginClient = _f.CreateClient();
        var goodLogin = await loginClient.PostAsJsonAsync("/auth/login", new { username = "admin", password = "supersecret1" });
        goodLogin.EnsureSuccessStatusCode();
        var loginStatus = await loginClient.GetFromJsonAsync<AuthStatusDto>("/auth/status");
        Assert.True(loginStatus!.Authenticated);

        // Logout clears the session.
        var logout = await loginClient.PostAsync("/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await loginClient.GetFromJsonAsync<AuthStatusDto>("/auth/status");
        Assert.False(afterLogout!.Authenticated);
    }

    [Fact]
    public async Task EnvHealth_DotnetOk_OnThisBox()
    {
        var health = await _f.CreateClient().GetFromJsonAsync<JsonElement>("/env/health");
        Assert.True(health.GetProperty("dotnet").GetProperty("ok").GetBoolean());
    }

    private record AuthStatusDto(bool NeedsSetup, bool Authenticated, UserDto? User);
}
