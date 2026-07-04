using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xental.IntegrationTests;

/// <summary>
/// Live Checkout (Feature 2): an integrator mints a session token for a virtual account, then a
/// payer reads its reconciliation status anonymously by that token.
/// </summary>
public class CheckoutEndToEndTests
{
    private const string Password = "Str0ng-Passw0rd!";
    private static int _seq;
    private static string NewEmail() => $"chk{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}@example.com";

    private sealed record RegisterResponse(Guid TenantId, string Email, bool EmailVerified, string Message);
    private sealed record ApiKeyResponse(Guid Id, string ClientId, string? ClientSecret, string Mode, string Label, string Status);
    private sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);
    private sealed record VaResponse(Guid Id, string AccountRef, string AccountNumber);
    private sealed record SessionResponse(string Token, string SnapshotUrl, string StreamUrl, DateTimeOffset ExpiresAtUtc, SnapshotResponse Snapshot);
    private sealed record SnapshotResponse(string AccountRef, string AccountNumber, string BankName, string AccountName, string PaymentState, long AmountPaidKobo, long? ExpectedAmountKobo);

    private static HttpClient NewClient(XentalApiFactory f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<HttpClient> ApiClientAsync(XentalApiFactory f)
    {
        var dash = NewClient(f);
        var email = NewEmail();
        (await dash.PostAsJsonAsync("/api/v1/developers/register", new { name = "Dev", email, password = Password }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var token = FakeEmailSender.VerificationTokenFor(email);
        await dash.GetAsync($"/api/v1/developers/verify-email?token={token}");
        await DashboardLogin.CompleteAsync(dash, email, Password);
        var keyResp = await dash.PostAsJsonAsync("/api/v1/api-keys", new { label = "key", mode = "test" });
        var key = (await keyResp.Content.ReadFromJsonAsync<ApiKeyResponse>())!;
        var tokenResp = await dash.PostAsJsonAsync("/api/v1/auth/token", new { clientId = key.ClientId, clientSecret = key.ClientSecret });
        var access = (await tokenResp.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;

        var api = NewClient(f);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return api;
    }

    private static async Task<VaResponse> CreateAccountAsync(HttpClient api, string accountRef, long? expectedKobo = null)
    {
        var resp = await api.PostAsJsonAsync("/api/v1/virtual-accounts",
            new { accountRef, name = "Ada Payer", email = (string?)null, phone = (string?)null, expectedAmountKobo = expectedKobo, expiryDateUtc = (DateTimeOffset?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<VaResponse>())!;
    }

    [Fact]
    public async Task Create_session_then_read_snapshot_anonymously()
    {
        using var f = new XentalApiFactory();
        var api = await ApiClientAsync(f);
        await CreateAccountAsync(api, "inv-100", expectedKobo: 500000);

        var create = await api.PostAsJsonAsync("/api/v1/checkout/sessions", new { accountRef = "inv-100", ttlSeconds = (int?)null });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var session = (await create.Content.ReadFromJsonAsync<SessionResponse>())!;
        session.Token.Should().StartWith("chk_");
        session.Snapshot.PaymentState.Should().Be("Unpaid");
        session.Snapshot.ExpectedAmountKobo.Should().Be(500000);

        // Anonymous client (no auth header) can read the snapshot by token.
        var anon = NewClient(f);
        var snap = await anon.GetAsync(session.SnapshotUrl);
        snap.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await snap.Content.ReadFromJsonAsync<SnapshotResponse>())!;
        body.AccountRef.Should().Be("inv-100");
        body.PaymentState.Should().Be("Unpaid");
    }

    [Fact]
    public async Task Unknown_token_returns_404()
    {
        using var f = new XentalApiFactory();
        var anon = NewClient(f);
        (await anon.GetAsync("/api/v1/checkout/chk_does-not-exist")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Session_creation_requires_a_known_account()
    {
        using var f = new XentalApiFactory();
        var api = await ApiClientAsync(f);
        var resp = await api.PostAsJsonAsync("/api/v1/checkout/sessions", new { accountRef = "nope", ttlSeconds = (int?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
