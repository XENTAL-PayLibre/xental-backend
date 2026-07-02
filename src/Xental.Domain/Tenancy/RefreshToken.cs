using Xental.Domain.Common;

namespace Xental.Domain.Tenancy;

/// <summary>
/// A long-lived refresh token for the dashboard session. Only a hash of the token is
/// stored. Tokens are single-use: refreshing consumes the current one and issues a new
/// one (rotation), so a stolen-and-replayed token is detectable and revocable.
/// </summary>
public sealed class RefreshToken : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    private RefreshToken() { } // EF

    public RefreshToken(Guid tenantId, string tokenHash, DateTimeOffset expiresAtUtc)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        TokenHash = DomainException.Require(tokenHash, nameof(tokenHash));
        ExpiresAtUtc = expiresAtUtc;
    }

    public bool IsActive(DateTimeOffset now) =>
        ConsumedAtUtc is null && RevokedAtUtc is null && now < ExpiresAtUtc;

    public void Consume(DateTimeOffset at) => ConsumedAtUtc = at;
    public void Revoke(DateTimeOffset at) => RevokedAtUtc = at;
}
