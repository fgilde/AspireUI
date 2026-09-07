using System.Net;
using System.Net.Http.Json;
using AspireUI.Server.Models;

// The real thing end to end: a logged-in user with a permission list, against the endpoints that
// check it. Runs on the cookie handler (NoAuthTestFactory) because the auto-auth factory is admin.
[Collection("ServerIntegration")]
public class EndpointPermissionTests : IClassFixture<NoAuthTestFactory>
{
    private readonly NoAuthTestFactory _f;
    public EndpointPermissionTests(NoAuthTestFactory f) => _f = f;

    private async Task<HttpClient> AdminAsync()
    {
        var client = _f.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "root", password = "supersecret1" });
        if (setup.StatusCode == HttpStatusCode.Conflict)
        {
            var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "root", password = "supersecret1" });
            login.EnsureSuccessStatusCode();
            return client;
        }
        setup.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<(HttpClient Client, UserDto User)> UserAsync(HttpClient admin, string name, params string[] perms)
    {
        var created = await admin.PostAsJsonAsync("/api/users",
            new { username = name, password = "userpassword1", isAdmin = false, permissions = perms });
        created.EnsureSuccessStatusCode();
        var dto = (await created.Content.ReadFromJsonAsync<UserDto>())!;

        var client = _f.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = name, password = "userpassword1" });
        login.EnsureSuccessStatusCode();
        return (client, dto);
    }

    [Fact]
    public async Task A_user_with_no_permissions_may_look_and_nothing_else()
    {
        var admin = await AdminAsync();
        var (user, _) = await UserAsync(admin, "looker");

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/hosting")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/stacks")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/docker/images")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/store/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PostAsJsonAsync("/api/stacks", new { name = "nope", nodes = Array.Empty<object>(), edges = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PostAsJsonAsync("/api/stacks/whatever/hosting/deploy", new { })).StatusCode);
    }

    [Fact]
    public async Task A_granted_permission_opens_exactly_its_endpoints()
    {
        var admin = await AdminAsync();
        var (user, _) = await UserAsync(admin, "dockerhand", Perm.Docker);

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/docker/images")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/store/sources")).StatusCode);
    }

    [Fact]
    public async Task Revoking_a_permission_takes_effect_without_a_new_login()
    {
        var admin = await AdminAsync();
        var (user, dto) = await UserAsync(admin, "shortlived", Perm.Docker);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/docker/images")).StatusCode);

        var revoke = await admin.PutAsJsonAsync($"/api/users/{dto.Id}/permissions", new { permissions = Array.Empty<string>() });
        revoke.EnsureSuccessStatusCode();

        // Same cookie, same client: the policy reads the store per request, not the sign-in claims.
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/docker/images")).StatusCode);
    }

    [Fact]
    public async Task A_user_manager_cannot_hand_out_what_it_does_not_have()
    {
        var admin = await AdminAsync();
        var (manager, _) = await UserAsync(admin, "manager", Perm.Users);
        var (_, victim) = await UserAsync(admin, "victim");

        var grant = await manager.PutAsJsonAsync($"/api/users/{victim.Id}/permissions",
            new { permissions = new[] { Perm.Docker, Perm.Users } });
        grant.EnsureSuccessStatusCode();

        var after = (await manager.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Single(u => u.Id == victim.Id);
        Assert.Equal(new[] { Perm.Users }, after.Permissions);
    }

    [Fact]
    public async Task A_user_manager_cannot_touch_an_admin_or_make_one()
    {
        var admin = await AdminAsync();
        var (manager, _) = await UserAsync(admin, "manager2", Perm.Users);
        var root = (await manager.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Single(u => u.Username == "root");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await manager.PutAsJsonAsync($"/api/users/{root.Id}/password", new { password = "hijacked1", mustChange = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await manager.PutAsJsonAsync($"/api/users/{root.Id}/disabled", new { disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync($"/api/users/{root.Id}")).StatusCode);

        var newAdmin = await manager.PostAsJsonAsync("/api/users",
            new { username = "sneaky", password = "userpassword1", isAdmin = true });
        Assert.Equal(HttpStatusCode.Forbidden, newAdmin.StatusCode);
    }
}
