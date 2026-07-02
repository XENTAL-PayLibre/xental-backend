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

    public Task SendOperationalAlertAsync(string toEmail, string subject, string html, CancellationToken ct = default) =>
        Task.CompletedTask;

    public static string? VerificationTokenFor(string email) =>
        Verify.TryGetValue(email, out var link) ? TokenFromLink(link) : null;

    public static string? ResetTokenFor(string email) =>
        Reset.TryGetValue(email, out var link) ? TokenFromLink(link) : null;

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

            // Create the schema on the shared connection.
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<XentalDbContext>().Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
