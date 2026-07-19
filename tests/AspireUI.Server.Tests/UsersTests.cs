using System.Net;
using System.Net.Http.Json;
using AspireUI.Server.Models;

// Uses NoAuthTestFactory (no auto-auth) to exercise the real admin-gated /users flow: setup
// signs in as admin, then a real login as a created non-admin proves the role gate.
[Collection("ServerIntegration")]
public class UsersTests : IClassFixture<NoAuthTestFactory>
{
    private readonly NoAuthTestFactory _f;
    public UsersTests(NoAuthTestFactory f) => _f = f;

    // The NoAuthTestFactory fixture (and its DB) is shared across every [Fact] in this class, so
    // only the first test method to run performs the actual /auth/setup; later ones find the
    // admin already exists (409) and log in instead.
    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _f.CreateClient();
        var setup = await client.PostAsJsonAsync("/auth/setup", new { username = "admin", password = "supersecret1" });
        if (setup.StatusCode == HttpStatusCode.Conflict)
        {
            var login = await client.PostAsJsonAsync("/auth/login", new { username = "admin", password = "supersecret1" });
            login.EnsureSuccessStatusCode();
            return client;
        }
        setup.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Admin_CanListCreateAndDeleteUsers()
    {
        var admin = await AdminClientAsync();

        var create = await admin.PostAsJsonAsync("/users", new { username = "alice", password = "alicepass1", isAdmin = false });
        create.EnsureSuccessStatusCode();
        var alice = await create.Content.ReadFromJsonAsync<UserDto>();
        Assert.Equal("alice", alice!.Username);
        Assert.False(alice.IsAdmin);
        // UserDto has no PasswordHash field at all -> nothing to leak; the raw payload must not
        // carry a hash either.
        var raw = await create.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alicepass1", raw);

        var list = await admin.GetFromJsonAsync<List<UserDto>>("/users");
        Assert.Contains(list!, u => u.Username == "alice");
        Assert.Contains(list!, u => u.Username == "admin");

        var delete = await admin.DeleteAsync($"/users/{alice.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var afterDelete = await admin.GetFromJsonAsync<List<UserDto>>("/users");
        Assert.DoesNotContain(afterDelete!, u => u.Username == "alice");
    }

    [Fact]
    public async Task CreateUser_DuplicateUsername_Returns409()
    {
        var admin = await AdminClientAsync();

        var first = await admin.PostAsJsonAsync("/users", new { username = "bob", password = "bobpassword", isAdmin = false });
        first.EnsureSuccessStatusCode();

        var dup = await admin.PostAsJsonAsync("/users", new { username = "bob", password = "differentpass", isAdmin = false });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task DeleteLastAdmin_Returns400()
    {
        var admin = await AdminClientAsync();

        var status = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/auth/status");
        var adminId = status.GetProperty("user").GetProperty("id").GetString();

        var delete = await admin.DeleteAsync($"/users/{adminId}");
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_GetUsers_Returns403()
    {
        var admin = await AdminClientAsync();
        var create = await admin.PostAsJsonAsync("/users", new { username = "carol", password = "carolpassword", isAdmin = false });
        create.EnsureSuccessStatusCode();

        var nonAdmin = _f.CreateClient();
        var login = await nonAdmin.PostAsJsonAsync("/auth/login", new { username = "carol", password = "carolpassword" });
        login.EnsureSuccessStatusCode();

        var forbidden = await nonAdmin.GetAsync("/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
