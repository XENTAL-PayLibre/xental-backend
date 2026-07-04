using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xental.IntegrationTests;

public class Phase1EndToEndTests
{
    private const string Password = "Str0ng-Passw0rd!";

    private sealed record RegisterResponse(Guid TenantId, string Email, bool EmailVerified, string Message);
    private sealed record SessionResponse(Guid TenantId, string Email, bool EmailVerified);
    private sealed record ApiKeyResponse(Guid Id, string ClientId, string? ClientSecret, string Mode, string Label, string Status);
    private sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);
    private sealed record SubMerchantResponse(Guid Id, string Name, string Reference, string Status, DateTimeOffset CreatedAtUtc);

    private static int _seq;
    private static string NewEmail() => $"dev{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}@example.com";

    private static HttpClient NewClient(XentalApiFactory f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task RegisterAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/developers/register", new { name = "Dev", email, password = Password });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task VerifyAsync(HttpClient client, string email)
    {
        var token = FakeEmailSender.VerificationTokenFor(email);
        token.Should().NotBeNull("registration should have queued a verification email");
        var resp = await client.GetAsync($"/api/v1/developers/verify-email?token={token}");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("verified=true");
    }

    /// <summary>Register + verify + login, returning a client whose cookies carry the session.</summary>
    private static async Task<HttpClient> DashboardClientAsync(XentalApiFactory f, string email)
    {
        var client = NewClient(f);
        await RegisterAsync(client, email);
        await VerifyAsync(client, email);
        await DashboardLogin.CompleteAsync(client, email, Password);
        return client;
    }

    /// <summary>A client authenticated on the API plane (Bearer api token) for a fresh account.</summary>
    private static async Task<HttpClient> ApiClientAsync(XentalApiFactory f, string mode = "test")
    {
        var dash = await DashboardClientAsync(f, NewEmail());
        var keyResp = await dash.PostAsJsonAsync("/api/v1/api-keys", new { label = "key", mode });
        keyResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var key = (await keyResp.Content.ReadFromJsonAsync<ApiKeyResponse>())!;
        var tokenResp = await dash.PostAsJsonAsync("/api/v1/auth/token", new { clientId = key.ClientId, clientSecret = key.ClientSecret });
        var token = (await tokenResp.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;

        var api = NewClient(f);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return api;
    }

    [Fact]
    public async Task Register_does_not_log_in_and_login_needs_verification()
    {
        using var f = new XentalApiFactory();
        var client = NewClient(f);
        var email = NewEmail();

        var reg = await client.PostAsJsonAsync("/api/v1/developers/register", new { name = "Dev", email, password = Password });
        reg.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await reg.Content.ReadFromJsonAsync<RegisterResponse>())!;
        body.EmailVerified.Should().BeFalse();
        reg.Headers.Contains("Set-Cookie").Should().BeFalse("registration must not start a session");

        // Login before verifying -> 403.
        var early = await client.PostAsJsonAsync("/api/v1/developers/login", new { email, password = Password });
        early.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Weak_password_is_rejected()
    {
        using var f = new XentalApiFactory();
        var resp = await NewClient(f).PostAsJsonAsync("/api/v1/developers/register",
            new { name = "Dev", email = NewEmail(), password = "weakpass" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Verify_then_login_sets_session_and_me_works()
    {
        using var f = new XentalApiFactory();
        var email = NewEmail();
        var client = await DashboardClientAsync(f, email);

        var me = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/developers/me");
        me!["email"].ToString().Should().Be(email);
    }

    [Fact]
    public async Task Login_marks_session_cookie_SameSite_None_for_cross_site_dev()
    {
        using var f = new XentalApiFactory();
        // Mirror the staging config: the FE dev server (localhost:3000) is a different
        // site, so the session cookie must be SameSite=None to be sent cross-site.
        using var configured = f.WithWebHostBuilder(b => b.UseSetting("Auth:CookieSameSite", "None"));
        var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var email = NewEmail();
        await RegisterAsync(client, email);
        await VerifyAsync(client, email);
        var begin = await client.PostAsJsonAsync("/api/v1/developers/login", new { email, password = Password });
        begin.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var code = FakeEmailSender.OtpFor(email);
        var login = await client.PostAsJsonAsync("/api/v1/developers/login/verify", new { email, code });

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(AuthCookieWriter_AccessName));
        access.ToLowerInvariant().Should().Contain("samesite=none");
        // None is rejected by browsers without Secure, so Secure must be forced on even
        // though CookieSecure is false in the test host.
        access.ToLowerInvariant().Should().Contain("secure");
    }

    private const string AuthCookieWriter_AccessName = "xnt_access";

    [Fact]
    public async Task Login_from_dev_insecure_origin_emits_host_only_lax_cookie()
    {
        using var f = new XentalApiFactory();
        // Staging-style config (cross-site None + domain) but with localhost:3000 whitelisted as a
        // dev-insecure origin so a local Next.js proxy can store/read the relayed HttpOnly cookie.
        using var configured = f.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:CookieSameSite", "None");
            b.UseSetting("Auth:CookieDomain", ".staging.xental.online");
            b.UseSetting("Auth:DevInsecureCookieOrigins", "http://localhost:3000");
        });
        var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var email = NewEmail();
        await RegisterAsync(client, email);
        await VerifyAsync(client, email);

        var begin = await client.PostAsJsonAsync("/api/v1/developers/login", new { email, password = Password });
        begin.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var code = FakeEmailSender.OtpFor(email);

        // Cookies are set on the verify step, so the dev-insecure Origin must be sent there.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/developers/login/verify")
        {
            Content = JsonContent.Create(new { email, code }),
        };
        req.Headers.Add("Origin", "http://localhost:3000");
        var login = await client.SendAsync(req);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var access = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(AuthCookieWriter_AccessName)).ToLowerInvariant();
        access.Should().Contain("samesite=lax");
        access.Should().NotContain("domain=");  // host-only so the dev proxy's host owns it
        access.Should().NotContain("secure");   // plain http on localhost
    }

    [Fact]
    public async Task Wrong_password_is_unauthorized()
    {
        using var f = new XentalApiFactory();
        var email = NewEmail();
        var client = NewClient(f);
        await RegisterAsync(client, email);
        await VerifyAsync(client, email);

        var resp = await client.PostAsJsonAsync("/api/v1/developers/login", new { email, password = "Wr0ng-Passw0rd!" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_rotates_and_logout_ends_the_session()
    {
        using var f = new XentalApiFactory();
        var client = await DashboardClientAsync(f, NewEmail());

        (await client.PostAsync("/api/v1/developers/refresh", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/developers/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsync("/api/v1/developers/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/v1/developers/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_requires_a_session()
    {
        using var f = new XentalApiFactory();
        (await NewClient(f).GetAsync("/api/v1/developers/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_key_requires_a_dashboard_session()
    {
        using var f = new XentalApiFactory();
        var resp = await NewClient(f).PostAsJsonAsync("/api/v1/api-keys", new { label = "x", mode = "test" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Full_flow_key_token_submerchant()
    {
        using var f = new XentalApiFactory();
        var api = await ApiClientAsync(f);

        var create = await api.PostAsJsonAsync("/api/v1/sub-merchants", new { name = "Green School", reference = "sch-001" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var list = await api.GetFromJsonAsync<List<SubMerchantResponse>>("/api/v1/sub-merchants");
        list.Should().ContainSingle().Which.Reference.Should().Be("sch-001");
    }

    [Fact]
    public async Task Api_token_cannot_manage_keys_and_dashboard_cannot_call_api()
    {
        using var f = new XentalApiFactory();

        var api = await ApiClientAsync(f);
        (await api.PostAsJsonAsync("/api/v1/api-keys", new { label = "x", mode = "test" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The dashboard plane can now reach shared read/manage endpoints (sub-merchants, etc.), but
        // key-mode-bound API-only actions like provisioning a NUBAN stay API-token only.
        var dash = await DashboardClientAsync(f, NewEmail());
        (await dash.PostAsJsonAsync("/api/v1/virtual-accounts", new { accountRef = "r1", name = "X" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Tenants_cannot_see_each_others_sub_merchants()
    {
        using var f = new XentalApiFactory();

        var a = await ApiClientAsync(f);
        await a.PostAsJsonAsync("/api/v1/sub-merchants", new { name = "A School", reference = "shared" });

        var b = await ApiClientAsync(f);
        var listForB = await b.GetFromJsonAsync<List<SubMerchantResponse>>("/api/v1/sub-merchants");
        listForB.Should().BeEmpty("tenant B must not see tenant A's data");
        (await b.PostAsJsonAsync("/api/v1/sub-merchants", new { name = "B School", reference = "shared" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Forgot_and_reset_password_flow()
    {
        using var f = new XentalApiFactory();
        var email = NewEmail();
        var client = NewClient(f);
        await RegisterAsync(client, email);

        (await client.PostAsJsonAsync("/api/v1/developers/forgot-password", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        var token = FakeEmailSender.ResetTokenFor(email);
        token.Should().NotBeNull();

        // Weak new password -> 400.
        (await client.PostAsJsonAsync("/api/v1/developers/reset-password", new { token, newPassword = "weak" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Strong new password -> 204.
        (await client.PostAsJsonAsync("/api/v1/developers/reset-password", new { token, newPassword = "N3w-Passw0rd!" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
