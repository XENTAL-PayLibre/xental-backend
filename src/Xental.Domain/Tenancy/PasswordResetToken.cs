using Xental.Domain.Common;

namespace Xental.Domain.Tenancy;

/// <summary>
/// A single-use, expiring token backing a password-reset link. Only the hash of the
/// token is stored; the raw value lives only in the emailed link. Valid until it is
/// consumed or expires.
/// </summary>
public sealed class PasswordResetToken : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    private PasswordResetToken() { } // EF

    public PasswordResetToken(Guid tenantId, string tokenHash, DateTimeOffset expiresAtUtc)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        TokenHash = DomainException.Require(tokenHash, nameof(tokenHash));
        ExpiresAtUtc = expiresAtUtc;
    }

    public bool IsConsumed => ConsumedAtUtc is not null;
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;
    public bool IsUsable(DateTimeOffset now) => !IsConsumed && !IsExpired(now);

    public void Consume(DateTimeOffset at) => ConsumedAtUtc = at;
}
