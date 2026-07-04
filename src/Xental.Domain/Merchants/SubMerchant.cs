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

    // Payout account: where this sub-merchant's collected funds are settled. Set via the API and
    // (optionally) verified against the bank with a NUBAN name-match before use.
    public string? SettlementBankName { get; private set; }
    public string? SettlementBankCode { get; private set; }
    public string? SettlementAccountNumber { get; private set; }
    public string? SettlementAccountName { get; private set; }

    /// <summary>Operator's commission on this sub-merchant's collections, in basis points (1% = 100).</summary>
    public int PlatformFeeBps { get; private set; }

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

    /// <summary>True once a payout bank account is configured (required before funds can be settled).</summary>
    public bool HasPayoutAccount =>
        !string.IsNullOrWhiteSpace(SettlementAccountNumber) && !string.IsNullOrWhiteSpace(SettlementBankCode);

    /// <summary>Set/replace the payout account and platform fee. <paramref name="accountName"/> is the
    /// bank-verified name when available (falls back to the supplied name otherwise).</summary>
    public void SetPayout(string bankName, string bankCode, string accountNumber, string accountName, int platformFeeBps)
    {
        if (platformFeeBps is < 0 or > 10000)
            throw new DomainException("Platform fee must be between 0 and 10000 basis points.");
        SettlementBankName = DomainException.Require(bankName, nameof(bankName)).Trim();
        SettlementBankCode = DomainException.Require(bankCode, nameof(bankCode)).Trim();
        SettlementAccountNumber = DomainException.Require(accountNumber, nameof(accountNumber)).Trim();
        SettlementAccountName = DomainException.Require(accountName, nameof(accountName)).Trim();
        PlatformFeeBps = platformFeeBps;
    }

    public void Rename(string name) => Name = DomainException.Require(name, nameof(name)).Trim();

    public void Suspend() => Status = SubMerchantStatus.Suspended;

    public void Reactivate() => Status = SubMerchantStatus.Active;
}
