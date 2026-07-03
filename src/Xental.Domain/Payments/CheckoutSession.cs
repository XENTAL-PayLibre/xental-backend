using Xental.Domain.Common;

namespace Xental.Domain.Payments;

/// <summary>
/// A Live Checkout subscription scoped to a single <see cref="VirtualAccount"/>. Whoever holds
/// the opaque, expiring <see cref="Token"/> can read that account's reconciliation status and
/// subscribe to its live stream <b>without authenticating</b> — so a payer watches
/// "Payment received ✓" land in real time. Reveals only payment state, never PII. Additive and
/// opt-in: created on demand, and its existence never touches the money path.
/// </summary>
public sealed class CheckoutSession : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid VirtualAccountId { get; private set; }
    public string Token { get; private set; } = null!;
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    private CheckoutSession() { } // EF

    public CheckoutSession(Guid tenantId, Guid virtualAccountId, string token, DateTimeOffset expiresAtUtc)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (virtualAccountId == Guid.Empty) throw new DomainException("VirtualAccountId is required.");
        TenantId = tenantId;
        VirtualAccountId = virtualAccountId;
        Token = DomainException.Require(token, nameof(token));
        ExpiresAtUtc = expiresAtUtc;
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;
}
