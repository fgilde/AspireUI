using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// StackEndpoints reads DB_PATH/WORKSPACE_DIR from the environment at host build time, falling
// back to the developer's real %LocalAppData%\AspireUI when unset. Point every integration test
// at an isolated temp DB/workspace instead, so running the suite never touches (or overwrites)
// the developer's saved AI settings. Env vars are set in the constructor, before the lazy host
// build triggered by the first CreateClient()/Server access.
//
// Program.cs now requires authentication on all app endpoints. Every pre-existing integration
// test (ApiTests, AssistTests, SettingsTests, TemplateTests, ...) predates auth and asserts
// against these endpoints with a plain, cookie-less HttpClient. Rather than touch every one of
// those tests, this factory registers a TEST-ONLY authentication scheme (TestAuthHandler, below)
// that authenticates every request as a fixed admin, and makes it the default scheme — so those
// tests keep passing completely unmodified. AuthTests, which needs to exercise the real
// cookie/login flow (including 401s for unauthenticated requests), uses NoAuthTestFactory
// instead, which skips this registration entirely.
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    public readonly string DbPath;
    public readonly string WorkspaceDir;
    private readonly bool _autoAuth;

    public TestWebAppFactory() : this(autoAuth: true) { }

    protected TestWebAppFactory(bool autoAuth)
    {
        _autoAuth = autoAuth;
        var root = Path.Combine(Path.GetTempPath(), "aspireui-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        DbPath = Path.Combine(root, "aspireui.db");
        WorkspaceDir = Path.Combine(root, "workspace");

        Environment.SetEnvironmentVariable("DB_PATH", DbPath);
        Environment.SetEnvironmentVariable("WORKSPACE_DIR", WorkspaceDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (!_autoAuth) return;
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            // PostConfigure runs after Program.cs's own AddAuthentication(cookie scheme) call,
            // regardless of registration order, so this reliably wins and every request
            // authenticates via TestAuthHandler instead of the real cookie scheme.
            services.PostConfigure<AuthenticationOptions>(o => o.DefaultScheme = TestAuthHandler.SchemeName);
        });
    }
}

// Opt-out variant for AuthTests: no auto-auth, so requests exercise the real cookie
// setup/login/logout flow and unauthenticated app-endpoint requests genuinely 401.
public class NoAuthTestFactory : TestWebAppFactory
{
    public NoAuthTestFactory() : base(autoAuth: false) { }
}

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestScheme";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-admin"),
            new Claim(ClaimTypes.Name, "test-admin"),
            new Claim(ClaimTypes.Role, "Admin"),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
