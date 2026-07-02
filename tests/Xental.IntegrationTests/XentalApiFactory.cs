using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Persistence;

namespace Xental.IntegrationTests;

/// <summary>No-op email sender so tests never make outbound Resend calls.</summary>
internal sealed class FakeEmailSender : IEmailSender
{
    public Task SendEmailVerificationAsync(string toEmail, string verifyLink, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken ct = default) => Task.CompletedTask;
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
