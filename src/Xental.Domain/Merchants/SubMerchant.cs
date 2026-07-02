using Xental.Domain.Common;

namespace Xental.Domain.Merchants;

public enum SubMerchantStatus
{
    Active = 1,
    Suspended = 2,
}

/// <summary>
/// A sub-merchant (e.g. a school) owned by a tenant. This is an INTERNAL Xental
/// concept — it groups customers, tracks per-school balances, and holds payout
/// details. It is NOT a Nomba object: Xental collects for every sub-merchant
/// through the operator's single platform Nomba account, and attributes funds via
/// its own ledger. <see cref="Reference"/> is the tenant's stable key, unique per tenant.
/// </summary>
public sealed class SubMerchant : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Reference { get; private set; } = null!;
    public SubMerchantStatus Status { get; private set; }

    private SubMerchant() { } // EF

    public SubMerchant(Guid tenantId, string name, string reference)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        Name = DomainException.Require(name, nameof(name));
        Reference = DomainException.Require(reference, nameof(reference));
        Status = SubMerchantStatus.Active;
    }

    public void Suspend() => Status = SubMerchantStatus.Suspended;

    public void Reactivate() => Status = SubMerchantStatus.Active;
}
