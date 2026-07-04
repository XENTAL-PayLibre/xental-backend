using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xental.IntegrationTests;

/// <summary>
/// Agent-layer sandbox simulator: drive a deposit through the real reconciliation engine with zero
/// money, and confirm the account resolves. Also serves as HTTP-level proof of deposit→reconcile.
/// </summary>
public class SandboxSimulatorTests
{
    private const string Password = "Str0ng-Passw0rd!";
    private static int _seq;
    private static string NewEmail() => $"sbx{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}@example.com";

    private sealed record ApiKeyResponse(Guid Id, string ClientId, string? ClientSecret, string Mode, string Label, string Status);
    private sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);
    private sealed record VaResponse(Guid Id, string AccountRef, string AccountNumber, string PaymentState);
    private sealed record SimResponse(string Status, string? Reference, string? Reconciliation, string? PaymentState, string? Reason);

    private static HttpClient NewClient(XentalApiFactory f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<HttpClient> ApiClientAsync(XentalApiFactory f, string mode)
    {
        var dash = NewClient(f);
        var email = NewEmail();
        await dash.PostAsJsonAsync("/api/v1/developers/register", new { name = "Dev", email, password = Password });
        var token = FakeEmailSender.VerificationTokenFor(email);
        await dash.GetAsync($"/api/v1/developers/verify-email?token={token}");
        await DashboardLogin.CompleteAsync(dash, email, Password);
        var key = (await (await dash.PostAsJsonAsync("/api/v1/api-keys", new { label = "key", mode })).Content.ReadFromJsonAsync<ApiKeyResponse>())!;
        var access = (await (await dash.PostAsJsonAsync("/api/v1/auth/token", new { clientId = key.ClientId, clientSecret = key.ClientSecret })).Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        var api = NewClient(f);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return api;
    }

    [Fact]
    public async Task Simulated_exact_deposit_reconciles_to_fully_paid()
    {
        using var f = new XentalApiFactory();
        var api = await ApiClientAsync(f, "test");
        await api.PostAsJsonAsync("/api/v1/virtual-accounts", new { accountRef = "sim-1", name = "Payer", expectedAmountKobo = 500000L });

        var sim = await api.PostAsJsonAsync("/api/v1/sandbox/simulate/deposit", new { accountRef = "sim-1", amountKobo = 500000L });
        sim.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await sim.Content.ReadFromJsonAsync<SimResponse>())!;
        body.Status.Should().Be("Processed");
        body.PaymentState.Should().Be("FullyPaid");

        var va = await (await api.GetAsync("/api/v1/virtual-accounts/sim-1")).Content.ReadFromJsonAsync<VaResponse>();
        va!.PaymentState.Should().Be("FullyPaid");
    }

    [Fact]
    public async Task Simulating_an_unknown_account_returns_404()
    {
        using var f = new XentalApiFactory();
        var api = await ApiClientAsync(f, "test");
        var sim = await api.PostAsJsonAsync("/api/v1/sandbox/simulate/deposit", new { accountRef = "nope", amountKobo = 100000L });
        sim.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Simulated_overpayment_sets_overpaid_state()
    {
        using var f = new XentalApiFactory();
        var api = await ApiClientAsync(f, "test");
        await api.PostAsJsonAsync("/api/v1/virtual-accounts", new { accountRef = "sim-3", name = "Payer", expectedAmountKobo = 100000L });

        var sim = await api.PostAsJsonAsync("/api/v1/sandbox/simulate/deposit", new { accountRef = "sim-3", amountKobo = 150000L });
        var body = (await sim.Content.ReadFromJsonAsync<SimResponse>())!;
        body.PaymentState.Should().Be("Overpaid");
        body.Reconciliation.Should().Be("Overpaid");
    }
}
