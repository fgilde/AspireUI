using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

// The decisions single sign-on makes locally: PKCE, which account a set of claims is, and who is an
// admin. The redirects themselves need a provider, and that is not a unit test.
public class OidcTests
{
    private static (OidcService svc, SettingsStore settings) Fresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-oidc-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "aspireui.db");
        var settings = new SettingsStore(db);
        return (new OidcService(settings, new SecretStore(db, root)), settings);
    }

    private static Dictionary<string, JsonElement> Claims(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Not_configured_is_not_usable()
    {
        var (svc, _) = Fresh();
        Assert.False(svc.Config().Usable);
        svc.Save(new OidcConfig(Enabled: true, Authority: "https://id.example.com"));
        Assert.False(svc.Config().Usable);          // no client id
        svc.Save(new OidcConfig(Enabled: true, Authority: "https://id.example.com", ClientId: "aspireui"));
        Assert.True(svc.Config().Usable);
    }

    [Fact]
    public void The_client_secret_is_kept_in_the_secret_store_and_not_erased_by_a_blank_save()
    {
        var (svc, settings) = Fresh();
        svc.Save(new OidcConfig(Enabled: true, Authority: "https://id.example.com/", ClientId: "aspireui",
            ClientSecret: "the-secret", Label: "Keycloak"));

        Assert.Equal("the-secret", svc.Config().ClientSecret);
        Assert.NotEqual("the-secret", settings.GetValue("OidcClientSecret"));
        // The trailing slash is dropped so discovery does not build a double one.
        Assert.Equal("https://id.example.com", svc.Config().Authority);

        svc.Save(new OidcConfig(Enabled: true, Authority: "https://id.example.com", ClientId: "aspireui",
            ClientSecret: null, Label: "Keycloak"));
        Assert.Equal("the-secret", svc.Config().ClientSecret);
    }

    [Fact]
    public void Pkce_is_a_fresh_verifier_and_the_documented_challenge()
    {
        var a = OidcService.NewVerifier();
        var b = OidcService.NewVerifier();
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('=', a);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);

        // RFC 7636 appendix B's own pair.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            OidcService.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void The_authorize_url_carries_everything_the_provider_needs()
    {
        var (svc, _) = Fresh();
        var c = new OidcConfig(Enabled: true, Authority: "https://id.example.com", ClientId: "aspireui",
            Scopes: "openid profile groups");
        var info = new OidcEndpointsInfo("https://id.example.com/authorize?realm=main",
            "https://id.example.com/token", "https://id.example.com/userinfo", null);

        var url = svc.AuthorizeUrl(info, c, "https://apps.example.com/api/auth/sso/callback", "the-state", "the-verifier");

        Assert.StartsWith("https://id.example.com/authorize?realm=main&", url);   // kept its own query
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=aspireui", url);
        Assert.Contains("state=the-state", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("code_challenge=" + OidcService.Challenge("the-verifier"), url);
        Assert.Contains("scope=openid%20profile%20groups", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Fapps.example.com%2Fapi%2Fauth%2Fsso%2Fcallback", url);
        // The verifier itself never travels in the browser.
        Assert.DoesNotContain("the-verifier", url);
    }

    [Fact]
    public void The_username_claim_is_configurable_and_falls_back_in_a_sane_order()
    {
        var claims = Claims("""{"sub":"abc","email":"kim@example.com","preferred_username":"kim","upn":"kim@corp"}""");

        Assert.Equal("kim", OidcService.Username(claims, null));
        Assert.Equal("kim@corp", OidcService.Username(claims, "upn"));
        // A claim that is not there falls through instead of failing.
        Assert.Equal("kim", OidcService.Username(claims, "nope"));
        Assert.Equal("abc", OidcService.Username(Claims("""{"sub":"abc"}"""), null));
        Assert.Null(OidcService.Username(Claims("""{"nothing":1}"""), null));
    }

    [Fact]
    public void Groups_are_read_whichever_shape_the_provider_uses()
    {
        Assert.Equal(["a", "b"], OidcService.Groups(Claims("""{"groups":["a","b"]}"""), "groups"));
        Assert.Equal(["a", "b"], OidcService.Groups(Claims("""{"groups":"a b"}"""), "groups"));
        Assert.Equal(["a", "b"], OidcService.Groups(Claims("""{"roles":"a,b"}"""), "roles"));
        Assert.Empty(OidcService.Groups(Claims("""{"groups":["a"]}"""), null));
        Assert.Empty(OidcService.Groups(Claims("""{"groups":123}"""), "groups"));
    }

    [Fact]
    public void An_admin_group_decides_who_is_an_admin_and_keycloaks_slash_does_not_get_in_the_way()
    {
        var c = new OidcConfig(GroupsClaim: "groups", AdminGroup: "aspireui-admins");
        Assert.True(OidcService.IsAdmin(Claims("""{"groups":["aspireui-admins","staff"]}"""), c));
        Assert.True(OidcService.IsAdmin(Claims("""{"groups":["/aspireui-admins"]}"""), c));
        Assert.True(OidcService.IsAdmin(Claims("""{"groups":["ASPIREUI-ADMINS"]}"""), c));
        Assert.False(OidcService.IsAdmin(Claims("""{"groups":["staff"]}"""), c));

        // No admin group configured means the provider does not decide it at all.
        Assert.False(OidcService.IsAdmin(Claims("""{"groups":["aspireui-admins"]}"""), new OidcConfig(GroupsClaim: "groups")));
    }

    [Fact]
    public void The_default_permissions_of_a_created_account_are_a_preset_or_a_list()
    {
        Assert.Equal(Perm.Default.OrderBy(x => x), SeedParse.Perms("default").OrderBy(x => x));
        Assert.Equal([Perm.Files], SeedParse.Perms("viewer"));
        Assert.Equal([Perm.Deploy, Perm.Files], SeedParse.Perms("deploy,files"));
    }
}
