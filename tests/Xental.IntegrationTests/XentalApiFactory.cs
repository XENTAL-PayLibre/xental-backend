using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Persistence;

namespace Xental.IntegrationTests;

/// <summary>
/// Captures the links that would be emailed (keyed by recipient) so tests can drive the
/// magic-link flows without a real mail provider. No outbound calls are made.
/// </summary>
internal sealed class FakeEmailSender : IEmailSender
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Verify = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Reset = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Invite = new();

    public Task SendEmailVerificationAsync(string toEmail, string verifyLink, CancellationToken ct = default)
    {
        Verify[toEmail] = verifyLink;
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken ct = default)
    {
        Reset[toEmail] = resetLink;
        return Task.CompletedTask;
    }

    public Task SendTeamInviteAsync(string toEmail, string inviteLink, string accountName, CancellationToken ct = default)
    {
        Invite[toEmail] = inviteLink;
        return Task.CompletedTask;
    }

    public Task SendOperationalAlertAsync(string toEmail, string subject, string html, CancellationToken ct = default) =>
        Task.CompletedTask;

    public static string? VerificationTokenFor(string email) =>
        Verify.TryGetValue(email, out var link) ? TokenFromLink(link) : null;

    public static string? ResetTokenFor(string email) =>
        Reset.TryGetValue(email, out var link) ? TokenFromLink(link) : null;

    public static string? InviteTokenFor(string email) =>
        Invite.TryGetValue(email, out var link) ? TokenFromLink(link) : null;

    private static string? TokenFromLink(string link)
    {
        var q = link.IndexOf("token=", StringComparison.Ordinal);
        return q < 0 ? null : Uri.UnescapeDataString(link[(q + "token=".Length)..]);
    }
}

/// <summary>
/// Boots the real API with the database swapped for an isolated SQLite in-memory
/// instance. One factory == one DB, so tests are isolated from each other.
/// (Phase 1 makes no outbound Nomba calls.)
/// </summary>
public sealed class XentalApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-0123456789-abcdefghijk");
        // Test client speaks http, so cookies must not be Secure; and don't rate-limit tests.
        builder.UseSetting("Auth:CookieSecure", "false");
        builder.UseSetting("RateLimiting:Disabled", "true");

        builder.ConfigureServices(services =>
        {
            // Strip every EF option/config for the context (incl. EF Core 9/10's
            // IDbContextOptionsConfiguration<T> that carries the Npgsql provider),
            // plus any Npgsql services, then add SQLite.
            var toRemove = services.Where(d =>
                    (d.ServiceType.FullName?.Contains("DbContextOptions") ?? false)
                    || d.ServiceType == typeof(XentalDbContext)
                    || (d.ServiceType.FullName?.Contains("Npgsql") ?? false)
                    || (d.ImplementationType?.FullName?.Contains("Npgsql") ?? false))
                .ToList();
            foreach (var descriptor in toRemove)
                services.Remove(descriptor);

            services.AddDbContext<XentalDbContext>(o => o.UseSqlite(_connection));

            // Never send real email in tests.
            services.AddScoped<IEmailSender, FakeEmailSender>();

            // Replace external integrations with deterministic in-memory fakes.
            Replace(services, ServiceDescriptor.Scoped<IIdentityVerifier, FakeIdentityVerifier>());
            Replace(services, ServiceDescriptor.Scoped<INombaClient, FakeNombaClient>());
            Replace(services, ServiceDescriptor.Singleton<IDocumentStorage, FakeDocumentStorage>());

            // Create the schema on the shared connection.
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<XentalDbContext>().Database.EnsureCreated();
        });
    }

    private static void Replace(IServiceCollection services, ServiceDescriptor descriptor)
    {
        for (var i = services.Count - 1; i >= 0; i--)
            if (services[i].ServiceType == descriptor.ServiceType) services.RemoveAt(i);
        services.Add(descriptor);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

// ---- Deterministic fakes for external integrations (identity, payments, storage) ----

internal sealed class FakeIdentityVerifier : IIdentityVerifier
{
    public Task<IdentityResult> VerifyBvnAsync(string bvn, CancellationToken ct = default) =>
        Task.FromResult(new IdentityResult(true, "Ada", "Obi", new DateOnly(1990, 1, 1)));
    public Task<IdentityResult> VerifyNinAsync(string nin, CancellationToken ct = default) =>
        Task.FromResult(new IdentityResult(true, "Ada", "Obi", new DateOnly(1990, 1, 1)));
    public Task<CompanyResult> VerifyCacAsync(string rcNumber, CancellationToken ct = default) =>
        Task.FromResult(new CompanyResult(true, "Acme Ltd", rcNumber));
}

internal sealed class FakeNombaClient : INombaClient
{
    public Task<ProvisionedVirtualAccount> CreateVirtualAccountAsync(string accountRef, string accountName, string? email, string? phone, CancellationToken ct = default) =>
        Task.FromResult(new ProvisionedVirtualAccount("1234567890", "Test Bank", accountName, "prov-" + accountRef));
    public Task<BankAccountName> LookupBankAccountAsync(string accountNumber, string bankCode, CancellationToken ct = default) =>
        Task.FromResult(new BankAccountName("Ada Obi", accountNumber, bankCode));
    public Task<TransferResult> InitiateTransferAsync(string merchantTxRef, long amountKobo, string accountNumber, string bankCode, string? accountName, string? narration, CancellationToken ct = default) =>
        Task.FromResult(new TransferResult(true, "prov-" + merchantTxRef, null));
}

internal sealed class FakeDocumentStorage : IDocumentStorage
{
    public async Task PutAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct); // drain
    }
    public Task<Uri> CreateDownloadUrlAsync(string objectKey, TimeSpan ttl, CancellationToken ct = default) =>
        Task.FromResult(new Uri($"https://storage.test/{objectKey}"));
}
