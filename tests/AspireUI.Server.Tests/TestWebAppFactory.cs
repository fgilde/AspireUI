using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Auto-auth test factory; authenticates all requests as admin via TestAuthHandler.
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
            services.PostConfigure<AuthenticationOptions>(o => o.DefaultScheme = TestAuthHandler.SchemeName);
        });
    }
}

// Opt-out factory for AuthTests; exercises real cookie auth without auto-auth.
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
