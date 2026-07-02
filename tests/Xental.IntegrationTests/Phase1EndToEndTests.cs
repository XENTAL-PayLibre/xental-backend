using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;

namespace Xental.IntegrationTests;

public class Phase1EndToEndTests
{
    private sealed record DeveloperAuthResponse(Guid TenantId, string Email, bool EmailVerified, string AccessToken, string TokenType, int ExpiresIn);
    private sealed record ApiKeyResponse(Guid Id, string ClientId, string? ClientSecret, string Mode, string Label, string Status);
    private sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);
    private sealed record SubMerchantResponse(Guid Id, string Name, string Reference, string Status, DateTimeOffset CreatedAtUtc);

    private static int _seq;
    private static string NewEmail() => $"dev{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}@example.com";

    private static void Bearer(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task<DeveloperAuthResponse> RegisterDeveloperAsync(HttpClient client, string? email = null)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/developers/register",
            new { name = "Acme Dev", email = email ?? NewEmail(), password = "correct-horse-battery" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DeveloperAuthResponse>())!;
    }

    private static async Task<ApiKeyResponse> CreateApiKeyAsync(HttpClient client, string dashboardToken, string mode = "test")
    {
        Bearer(client, dashboardToken);
        var resp = await client.PostAsJsonAsync("/api/v1/api-keys", new { label = "default", mode });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ApiKeyResponse>())!;
    }

    private static async Task<string> ApiTokenAsync(HttpClient client, ApiKeyResponse key)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/token",
            new { clientId = key.ClientId, clientSecret = key.ClientSecret });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    /// <summary>Register a developer, mint an API key, and return an api-scoped token + client.</summary>
    private static async Task<HttpClient> AuthenticatedApiClientAsync(XentalApiFactory factory, string mode = "test")
    {
        var client = factory.CreateClient();
        var dev = await RegisterDeveloperAsync(client);
        var key = await CreateApiKeyAsync(client, dev.AccessToken, mode);
        Bearer(client, await ApiTokenAsync(client, key));
        return client;
    }

    [Fact]
    public async Task Register_returns_dashboard_token()
    {
        using var factory = new XentalApiFactory();
        var dev = await RegisterDeveloperAsync(factory.CreateClient());
        dev.AccessToken.Should().NotBeNullOrWhiteSpace();
        dev.EmailVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Login_with_valid_credentials_succeeds_and_wrong_password_fails()
    {
        using var factory = new XentalApiFactory();
        var client = factory.CreateClient();
        var email = NewEmail();
        await RegisterDeveloperAsync(client, email);

        var ok = await client.PostAsJsonAsync("/api/v1/developers/login",
            new { email, password = "correct-horse-battery" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        var bad = await client.PostAsJsonAsync("/api/v1/developers/login",
            new { email, password = "wrong-password-here" });
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Creating_an_api_key_requires_a_dashboard_token()
    {
        using var factory = new XentalApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/api-keys", new { label = "x", mode = "test" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_key_create_returns_secret_and_yields_a_working_token()
    {
        using var factory = new XentalApiFactory();
        var client = factory.CreateClient();
        var dev = await RegisterDeveloperAsync(client);

        var key = await CreateApiKeyAsync(client, dev.AccessToken, "live");
        key.ClientId.Should().StartWith("xnt_live");
        key.ClientSecret.Should().StartWith("sk_live");

        var token = await ApiTokenAsync(client, key);
        token.Should().NotBeNullOrWhiteSpace();

        var bad = await client.PostAsJsonAsync("/api/v1/auth/token",
            new { clientId = key.ClientId, clientSecret = "sk_live_wrong" });
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Creating_a_sub_merchant_requires_authentication()
    {
        using var factory = new XentalApiFactory();
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/sub-merchants", new { name = "X", reference = "r1" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dashboard_token_cannot_call_the_api_plane()
    {
        using var factory = new XentalApiFactory();
        var client = factory.CreateClient();
        var dev = await RegisterDeveloperAsync(client);
        Bearer(client, dev.AccessToken); // dashboard scope

        var resp = await client.PostAsJsonAsync("/api/v1/sub-merchants", new { name = "X", reference = "r1" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Api_token_cannot_manage_api_keys()
    {
        using var factory = new XentalApiFactory();
        var client = await AuthenticatedApiClientAsync(factory); // api scope

        var resp = await client.PostAsJsonAsync("/api/v1/api-keys", new { label = "x", mode = "test" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Full_flow_register_key_token_create_and_list()
    {
        using var factory = new XentalApiFactory();
        var client = await AuthenticatedApiClientAsync(factory);

        var create = await client.PostAsJsonAsync("/api/v1/sub-merchants", new { name = "Green School", reference = "sch-001" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var sub = (await create.Content.ReadFromJsonAsync<SubMerchantResponse>())!;
        sub.Status.Should().Be("Active");

        var list = await client.GetFromJsonAsync<List<SubMerchantResponse>>("/api/v1/sub-merchants");
        list.Should().ContainSingle().Which.Reference.Should().Be("sch-001");
    }

    [Fact]
    public async Task Duplicate_reference_for_same_tenant_returns_409()
    {
        using var factory = new XentalApiFactory();
        var client = await AuthenticatedApiClientAsync(factory);

        (await client.PostAsJsonAsync("/api/v1/sub-merchants", new { name = "School A", reference = "dup" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/v1/sub-merchants", new { name = "School B", reference = "dup" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Tenants_cannot_see_each_others_sub_merchants()
    {
        using var factory = new XentalApiFactory();

        var clientA = await AuthenticatedApiClientAsync(factory);
        await clientA.PostAsJsonAsync("/api/v1/sub-merchants", new { name = "A School", reference = "shared" });

        var clientB = await AuthenticatedApiClientAsync(factory);
        var listForB = await clientB.GetFromJsonAsync<List<SubMerchantResponse>>("/api/v1/sub-merchants");
        listForB.Should().BeEmpty("tenant B must not see tenant A's data");

        // B can reuse the same reference (uniqueness is per-tenant).
        (await clientB.PostAsJsonAsync("/api/v1/sub-merchants", new { name = "B School", reference = "shared" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
