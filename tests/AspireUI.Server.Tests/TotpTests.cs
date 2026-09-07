using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AspireUI.Server.Services;

// The second factor: the algorithm on its own, then the login it actually gates.
public class TotpServiceTests
{
    [Fact]
    public void The_rfc_6238_test_vector_matches()
    {
        // RFC 6238 appendix B: the ASCII secret "12345678901234567890" (base32 below), SHA1, 8 digits.
        // With six digits the same vectors keep their last six characters.
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.Equal("287082", TotpService.Code(secret, 59L / 30));                   // 94287082
        Assert.Equal("081804", TotpService.Code(secret, 1111111109L / 30));           // 07081804
        Assert.Equal("050471", TotpService.Code(secret, 1111111111L / 30));           // 14050471
        Assert.Equal("005924", TotpService.Code(secret, 1234567890L / 30));           // 89005924
        Assert.Equal("279037", TotpService.Code(secret, 2000000000L / 30));           // 69279037
        Assert.Equal("353130", TotpService.Code(secret, 20000000000L / 30));          // 65353130
    }

    [Fact]
    public void A_secret_is_base32_and_new_every_time()
    {
        var a = TotpService.NewSecret();
        var b = TotpService.NewSecret();
        Assert.NotEqual(a, b);
        Assert.Equal(32, a.Length);
        Assert.All(a, c => Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
    }

    [Fact]
    public void A_code_is_accepted_a_step_either_side_and_not_beyond()
    {
        var secret = TotpService.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var step = TotpService.StepFor(now);

        Assert.True(TotpService.Verify(secret, TotpService.Code(secret, step), now));
        Assert.True(TotpService.Verify(secret, TotpService.Code(secret, step - 1), now));
        Assert.True(TotpService.Verify(secret, TotpService.Code(secret, step + 1), now));
        Assert.False(TotpService.Verify(secret, TotpService.Code(secret, step + 2), now));
        Assert.False(TotpService.Verify(secret, TotpService.Code(secret, step - 2), now));
    }

    [Fact]
    public void Spaces_are_ignored_and_nonsense_is_refused()
    {
        var secret = TotpService.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var code = TotpService.Code(secret, TotpService.StepFor(now));

        Assert.True(TotpService.Verify(secret, code[..3] + " " + code[3..], now));
        Assert.False(TotpService.Verify(secret, "12345", now));
        Assert.False(TotpService.Verify(secret, "", now));
        Assert.False(TotpService.Verify(null, code, now));
    }

    [Fact]
    public void The_enrolment_uri_says_what_the_apps_need()
    {
        var uri = TotpService.EnrolmentUri("ABCDEF", "kim@example.com");
        Assert.StartsWith("otpauth://totp/AspireUI:kim%40example.com?", uri);
        Assert.Contains("secret=ABCDEF", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
    }

    [Fact]
    public void A_recovery_code_is_kept_as_a_hash_and_spent_once()
    {
        var (plain, hashed) = TotpService.NewRecoveryCodes();
        Assert.Equal(8, plain.Count);
        Assert.All(plain, c => Assert.DoesNotContain(c, hashed));

        // Written down with or without its dash, in either case.
        Assert.True(TotpService.UseRecoveryCode(hashed, plain[0].ToUpperInvariant(), out var left));
        Assert.Equal(7, left.Count);
        Assert.False(TotpService.UseRecoveryCode(left, plain[0], out _));
        Assert.False(TotpService.UseRecoveryCode(left, "not-a-code", out _));
    }
}

// The login flow end to end, on the real cookie handler.
[Collection("ServerIntegration")]
public class TwoFactorLoginTests : IClassFixture<NoAuthTestFactory>
{
    private readonly NoAuthTestFactory _f;
    public TwoFactorLoginTests(NoAuthTestFactory f) => _f = f;

    private sealed record Setup(string Secret, string Uri);
    private sealed record Enabled(List<string> RecoveryCodes);
    private sealed record Challenge(bool TwoFactorRequired, string Ticket);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> AdminAsync()
    {
        var client = _f.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "root", password = "supersecret1" });
        if (setup.StatusCode == HttpStatusCode.Conflict)
        {
            (await client.PostAsJsonAsync("/api/auth/login", new { username = "root", password = "supersecret1" }))
                .EnsureSuccessStatusCode();
            return client;
        }
        setup.EnsureSuccessStatusCode();
        return client;
    }

    private static string Now(string secret) => TotpService.Code(secret, TotpService.StepFor(DateTimeOffset.UtcNow));

    [Fact]
    public async Task Turning_it_on_needs_a_working_code_and_hands_out_recovery_codes()
    {
        var client = await AdminAsync();

        var setup = await (await client.PostAsync("/api/auth/2fa/setup", null)).Content.ReadFromJsonAsync<Setup>(Json);
        Assert.NotNull(setup);
        Assert.Contains("otpauth://totp/AspireUI:root", setup!.Uri);

        var wrong = await client.PostAsJsonAsync("/api/auth/2fa/enable", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/auth/2fa/enable", new { code = Now(setup.Secret) });
        ok.EnsureSuccessStatusCode();
        var codes = await ok.Content.ReadFromJsonAsync<Enabled>(Json);
        Assert.Equal(8, codes!.RecoveryCodes.Count);

        // The session that turned it on stays signed in, and now says so.
        var status = await client.GetStringAsync("/api/auth/status");
        Assert.Contains("\"twoFactor\":true", status);

        // A fresh login stops at the code, and the same code gets in.
        var fresh = _f.CreateClient();
        var first = await fresh.PostAsJsonAsync("/api/auth/login", new { username = "root", password = "supersecret1" });
        first.EnsureSuccessStatusCode();
        var challenge = await first.Content.ReadFromJsonAsync<Challenge>(Json);
        Assert.True(challenge!.TwoFactorRequired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync("/api/stacks")).StatusCode);

        var bad = await fresh.PostAsJsonAsync("/api/auth/login/2fa", new { ticket = challenge.Ticket, code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var second = await fresh.PostAsJsonAsync("/api/auth/login/2fa", new { ticket = challenge.Ticket, code = Now(setup.Secret) });
        second.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/stacks")).StatusCode);

        // A recovery code works once.
        var recovery = _f.CreateClient();
        var ch2 = await (await recovery.PostAsJsonAsync("/api/auth/login", new { username = "root", password = "supersecret1" }))
            .Content.ReadFromJsonAsync<Challenge>(Json);
        var used = await recovery.PostAsJsonAsync("/api/auth/login/2fa", new { ticket = ch2!.Ticket, code = codes.RecoveryCodes[0] });
        used.EnsureSuccessStatusCode();

        var again = _f.CreateClient();
        var ch3 = await (await again.PostAsJsonAsync("/api/auth/login", new { username = "root", password = "supersecret1" }))
            .Content.ReadFromJsonAsync<Challenge>(Json);
        var reused = await again.PostAsJsonAsync("/api/auth/login/2fa", new { ticket = ch3!.Ticket, code = codes.RecoveryCodes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        // Turning it off needs the password, not just the session.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/2fa/disable", new { password = "wrong-password" })).StatusCode);
        (await client.PostAsJsonAsync("/api/auth/2fa/disable", new { password = "supersecret1" })).EnsureSuccessStatusCode();

        var plain = _f.CreateClient();
        var direct = await plain.PostAsJsonAsync("/api/auth/login", new { username = "root", password = "supersecret1" });
        direct.EnsureSuccessStatusCode();
        Assert.DoesNotContain("twoFactorRequired", await direct.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_ticket_from_somewhere_else_is_not_a_login()
    {
        var fresh = _f.CreateClient();
        var forged = await fresh.PostAsJsonAsync("/api/auth/login/2fa", new { ticket = "not-a-real-ticket", code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }
}
