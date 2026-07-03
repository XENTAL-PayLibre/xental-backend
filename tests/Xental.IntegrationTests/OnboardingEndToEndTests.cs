using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Admin;
using Xental.Infrastructure.Persistence;

namespace Xental.IntegrationTests;

public class OnboardingEndToEndTests
{
    private const string Password = "Str0ng-Passw0rd!";
    private static int _seq;
    private static string NewEmail() => $"kyc{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}@example.com";

    private sealed record RegisterResponse(Guid TenantId, string Email, bool EmailVerified, string Message);
    private sealed record ApiKeyResponse(Guid Id, string ClientId, string? ClientSecret, string Mode, string Label, string Status);
    private sealed record StatusResponse(string Tier, string DeveloperKycStatus, string BusinessKybStatus, bool CanIssueLiveKeys);
    private sealed record AdminLoginResponse(string AccessToken, string TokenType, int ExpiresIn, string Email, string Role);

    private static HttpClient NewClient(XentalApiFactory f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<(HttpClient client, Guid tenantId)> DashboardClientAsync(XentalApiFactory f)
    {
        var client = NewClient(f);
        var email = NewEmail();
        var reg = await client.PostAsJsonAsync("/api/v1/developers/register", new { name = "Dev", email, password = Password });
        reg.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenantId = (await reg.Content.ReadFromJsonAsync<RegisterResponse>())!.TenantId;

        var token = FakeEmailSender.VerificationTokenFor(email);
        await client.GetAsync($"/api/v1/developers/verify-email?token={token}");
        var login = await client.PostAsJsonAsync("/api/v1/developers/login", new { email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return (client, tenantId);
    }

    [Fact]
    public async Task Sandbox_can_create_test_keys_but_live_keys_are_gated()
    {
        using var factory = new XentalApiFactory();
        var (client, _) = await DashboardClientAsync(factory);

        var test = await client.PostAsJsonAsync("/api/v1/api-keys", new { label = "test-key", mode = "test" });
        test.StatusCode.Should().Be(HttpStatusCode.Created);

        var live = await client.PostAsJsonAsync("/api/v1/api-keys", new { label = "live-key", mode = "live" });
        live.StatusCode.Should().Be(HttpStatusCode.Forbidden, "live keys need an approved onboarding");
    }

    [Fact]
    public async Task Full_loop_submit_kyc_kyb_admin_approves_then_live_keys_unlock()
    {
        using var factory = new XentalApiFactory();
        var (client, tenantId) = await DashboardClientAsync(factory);

        // 1. Developer KYC.
        var devResp = await client.PostAsJsonAsync("/api/v1/onboarding/developer", new
        {
            fullName = "Ada Obi", dateOfBirth = "1990-01-01", country = "Nigeria", address = "1 Marina, Lagos",
            idType = "Bvn", idNumber = "22222222222",
            bankName = "EMK Bank", bankCode = "011", bankAccountName = "Ada Obi", bankAccountNumber = "0123456789",
            portfolioUrl = "https://github.com/ada", projectDescription = "A payments app",
        });
        devResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Business KYB + 3. documents + 4. submit.
        var bizResp = await client.PostAsJsonAsync("/api/v1/onboarding/business", new
        {
            legalName = "Acme Ltd", registrationNumber = "RC123456", businessType = "LLC", industry = "Finance",
            country = "Nigeria", address = "1 Marina, Lagos", contactCountryCode = "+234", contactPhone = "7035678999",
            website = "https://acme.example",
            settlementBankName = "EMK Bank", settlementBankCode = "011", settlementAccountName = "Acme Ltd", settlementAccountNumber = "0123456789",
        });
        bizResp.StatusCode.Should().Be(HttpStatusCode.OK);

        (await UploadDoc(client, "CertificateOfIncorporation")).Should().Be(HttpStatusCode.NoContent);
        (await UploadDoc(client, "ProofOfAddress")).Should().Be(HttpStatusCode.NoContent);

        var submit = await client.PostAsJsonAsync("/api/v1/onboarding/submit", new { attestationAccepted = true });
        submit.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterSubmit = (await submit.Content.ReadFromJsonAsync<StatusResponse>())!;
        afterSubmit.BusinessKybStatus.Should().Be("UnderReview");
        afterSubmit.Tier.Should().Be("Sandbox");

        // Still sandbox -> live key blocked.
        (await client.PostAsJsonAsync("/api/v1/api-keys", new { label = "live-key", mode = "live" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 5. Seed an admin + log in on the admin plane.
        SeedAdmin(factory, "admin@x.com");
        var adminClient = NewClient(factory);
        var adminLogin = await adminClient.PostAsJsonAsync("/api/v1/admin/auth/login", new { email = "admin@x.com", password = Password });
        adminLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<AdminLoginResponse>())!.AccessToken;
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // 6. Admin sees it under review and approves both tracks.
        var queue = await adminClient.GetAsync("/api/v1/admin/onboarding?status=UnderReview");
        queue.StatusCode.Should().Be(HttpStatusCode.OK);
        (await queue.Content.ReadAsStringAsync()).Should().Contain(tenantId.ToString());

        foreach (var track in new[] { "DeveloperKyc", "BusinessKyb" })
        {
            var approve = await adminClient.PostAsJsonAsync($"/api/v1/admin/onboarding/{tenantId}/approve", new { track });
            approve.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // 7. Tenant is now Live -> live key issues.
        var status = await client.GetFromJsonAsync<StatusResponse>("/api/v1/onboarding");
        status!.Tier.Should().Be("Live");
        status.CanIssueLiveKeys.Should().BeTrue();

        var liveKey = await client.PostAsJsonAsync("/api/v1/api-keys", new { label = "live-key", mode = "live" });
        liveKey.StatusCode.Should().Be(HttpStatusCode.Created);
        (await liveKey.Content.ReadFromJsonAsync<ApiKeyResponse>())!.ClientId.Should().StartWith("xnt_live");
    }

    [Fact]
    public async Task Non_superadmin_cannot_create_admins()
    {
        using var factory = new XentalApiFactory();
        SeedAdmin(factory, "ops@x.com", AdminRole.Admin); // a plain Admin, not SuperAdmin
        var client = NewClient(factory);
        var login = await client.PostAsJsonAsync("/api/v1/admin/auth/login", new { email = "ops@x.com", password = Password });
        var token = (await login.Content.ReadFromJsonAsync<AdminLoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var create = await client.PostAsJsonAsync("/api/v1/admin/admins", new { email = "new@x.com", password = Password, role = "Admin" });
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden, "only SuperAdmin can create admins");
    }

    private static async Task<HttpStatusCode> UploadDoc(HttpClient client, string type)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new byte[512];
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", $"{type}.pdf");
        form.Add(new StringContent(type), "type");
        var resp = await client.PostAsync("/api/v1/onboarding/documents", form);
        return resp.StatusCode;
    }

    private static void SeedAdmin(XentalApiFactory factory, string email, AdminRole role = AdminRole.SuperAdmin)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<XentalDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.AdminUsers.Add(new AdminUser(email, hasher.Hash(Password), role));
        db.SaveChanges();
    }
}
