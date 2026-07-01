using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;

namespace Xental.IntegrationTests;

public class AccountEndpointsTests
{
    private sealed record DeveloperAuthResponse(Guid TenantId, string Email, bool EmailVerified, string AccessToken, string TokenType, int ExpiresIn);
    private sealed record ProfileResponse(Guid TenantId, string Name, string Email, bool EmailVerified, string Status, DateTimeOffset CreatedAtUtc);

    private static int _seq;
    private static string NewEmail() => $"acct{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}@example.com";

    private static async Task<DeveloperAuthResponse> RegisterAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/developers/register",
            new { name = "Acme Dev", email, password = "correct-horse-battery" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DeveloperAuthResponse>())!;
    }

    [Fact]
    public async Task Profile_me_returns_the_account_for_a_dashboard_token()
    {
        using var factory = new XentalApiFactory();
        var client = factory.CreateClient();
        var email = NewEmail();
        var dev = await RegisterAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", dev.AccessToken);

        var profile = await client.GetFromJsonAsync<ProfileResponse>("/api/v1/developers/me");
        profile!.Email.Should().Be(email);
        profile.Status.Should().Be("Active");
    }

    [Fact]
    public async Task Profile_me_requires_authentication()
    {
        using var factory = new XentalApiFactory();
        var resp = await factory.CreateClient().GetAsync("/api/v1/developers/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forgot_password_always_returns_202()
    {
        using var factory = new XentalApiFactory();
        var client = factory.CreateClient();

        // Unknown email — still 202 (no enumeration).
        (await client.PostAsJsonAsync("/api/v1/developers/forgot-password", new { email = "nobody@example.com" }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Known email — also 202.
        var email = NewEmail();
        await RegisterAsync(client, email);
        (await client.PostAsJsonAsync("/api/v1/developers/forgot-password", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Reset_password_with_invalid_token_returns_400()
    {
        using var factory = new XentalApiFactory();
        var resp = await factory.CreateClient().PostAsJsonAsync("/api/v1/developers/reset-password",
            new { token = "bogus-token", newPassword = "brand-new-password-456" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Verify_email_redirects()
    {
        using var factory = new XentalApiFactory();
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var resp = await client.GetAsync("/api/v1/developers/verify-email?token=bogus");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("verified=false");
    }
}
