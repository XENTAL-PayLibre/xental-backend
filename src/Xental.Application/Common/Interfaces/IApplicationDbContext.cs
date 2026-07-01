using Microsoft.EntityFrameworkCore;
using Xental.Domain.Merchants;
using Xental.Domain.Tenancy;

namespace Xental.Application.Common.Interfaces;

/// <summary>Persistence abstraction the application layer depends on.</summary>
public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<ApiKey> ApiKeys { get; }
    DbSet<EmailVerificationToken> EmailVerificationTokens { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<ExternalLogin> ExternalLogins { get; }
    DbSet<SubMerchant> SubMerchants { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
