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

// Uses NoAuthTestFactory to exercise real auth flow (no auto-auth like other integration tests).
[Collection("ServerIntegration")]
public class AuthTests : IClassFixture<NoAuthTestFactory>
{
    private readonly NoAuthTestFactory _f;
    public AuthTests(NoAuthTestFactory f) => _f = f;

    [Fact]
    public async Task FullAuthFlow_SetupLoginLogout_AndAppEndpointGate()
    {
        var freshClient = _f.CreateClient();

        var status = await freshClient.GetFromJsonAsync<AuthStatusDto>("/api/auth/status");
        Assert.True(status!.NeedsSetup);
        Assert.False(status.Authenticated);

        var unauthed = await freshClient.GetAsync("/api/stacks");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthed.StatusCode);

        var setup = await freshClient.PostAsJsonAsync("/api/auth/setup", new { username = "admin", password = "supersecret1" });
        setup.EnsureSuccessStatusCode();
        var created = await setup.Content.ReadFromJsonAsync<UserDto>();
        Assert.Equal("admin", created!.Username);
        Assert.True(created.IsAdmin);

        var afterSetup = await freshClient.GetFromJsonAsync<AuthStatusDto>("/api/auth/status");
        Assert.False(afterSetup!.NeedsSetup);
        Assert.True(afterSetup.Authenticated);

        var authedStacks = await freshClient.GetAsync("/api/stacks");
        Assert.Equal(HttpStatusCode.OK, authedStacks.StatusCode);

        var secondSetup = await _f.CreateClient()
            .PostAsJsonAsync("/api/auth/setup", new { username = "admin2", password = "supersecret1" });
        Assert.Equal(HttpStatusCode.Conflict, secondSetup.StatusCode);

        var badLogin = await _f.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrongpassword" });
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);

        var loginClient = _f.CreateClient();
        var goodLogin = await loginClient.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "supersecret1" });
        goodLogin.EnsureSuccessStatusCode();
        var loginStatus = await loginClient.GetFromJsonAsync<AuthStatusDto>("/api/auth/status");
        Assert.True(loginStatus!.Authenticated);

        var logout = await loginClient.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await loginClient.GetFromJsonAsync<AuthStatusDto>("/api/auth/status");
        Assert.False(afterLogout!.Authenticated);
    }

    [Fact]
    public async Task EnvHealth_DotnetOk_OnThisBox()
    {
        var health = await _f.CreateClient().GetFromJsonAsync<JsonElement>("/api/env/health");
        Assert.True(health.GetProperty("dotnet").GetProperty("ok").GetBoolean());
        Assert.True(health.TryGetProperty("git", out _));
    }

    private record AuthStatusDto(bool NeedsSetup, bool Authenticated, UserDto? User);
}
