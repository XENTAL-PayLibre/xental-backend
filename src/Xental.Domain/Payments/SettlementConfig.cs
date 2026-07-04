using Xental.Domain.Common;

namespace Xental.Domain.Payments;

/// <summary>
/// A tenant's settlement preferences: the bank account collected funds are swept to, and
/// whether that sweep happens automatically once an account is fully paid. One per tenant.
/// </summary>
public sealed class SettlementConfig : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string? SettlementAccountNumber { get; private set; }
    public string? SettlementBankCode { get; private set; }
    public string? SettlementAccountName { get; private set; }
    public bool AutoSettle { get; private set; }
    public long MinPayoutKobo { get; private set; }

    private SettlementConfig() { } // EF

    public SettlementConfig(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        TenantId = tenantId;
    }

    public bool CanAutoSettle =>
        AutoSettle && !string.IsNullOrWhiteSpace(SettlementAccountNumber) && !string.IsNullOrWhiteSpace(SettlementBankCode);

    public void Update(string? accountNumber, string? bankCode, string? accountName, bool autoSettle, long minPayoutKobo)
    {
        SettlementAccountNumber = string.IsNullOrWhiteSpace(accountNumber) ? null : accountNumber.Trim();
        SettlementBankCode = string.IsNullOrWhiteSpace(bankCode) ? null : bankCode.Trim();
        SettlementAccountName = string.IsNullOrWhiteSpace(accountName) ? null : accountName.Trim();
        AutoSettle = autoSettle;
        MinPayoutKobo = Math.Max(0, minPayoutKobo);
    }
}
