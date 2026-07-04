using Xental.Domain.Common;

namespace Xental.Domain.Payments;

/// <summary>How a split leg's amount is computed from the net collected amount.</summary>
public enum SplitBasis { Percentage = 1, Flat = 2 }

/// <summary>
/// One beneficiary leg in a tenant's split-settlement plan. When a virtual account is settled and
/// splits exist, the net collected amount is fanned out across these legs (a platform-fee skim is
/// just another leg). A leg is either a <see cref="SplitBasis.Percentage"/> (basis points of net)
/// or a <see cref="SplitBasis.Flat"/> kobo amount. Legs with a null <see cref="VirtualAccountId"/>
/// apply tenant-wide; a leg bound to an account overrides the tenant default for it. Additive and
/// opt-in — no legs means the settlement worker's current single-sweep runs unchanged.
/// </summary>
public sealed class SettlementSplit : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid? VirtualAccountId { get; private set; }     // null = tenant-wide default
    public string BeneficiaryName { get; private set; } = null!;
    public string BeneficiaryAccountNumber { get; private set; } = null!;
    public string BeneficiaryBankCode { get; private set; } = null!;
    public SplitBasis Basis { get; private set; }
    public int ShareBps { get; private set; }               // basis points (1% = 100) when Percentage
    public long FlatKobo { get; private set; }              // fixed kobo when Flat
    public int Priority { get; private set; }               // lower = paid/rounded first
    public bool Enabled { get; private set; }

    private SettlementSplit() { } // EF

    public SettlementSplit(
        Guid tenantId, Guid? virtualAccountId, string beneficiaryName,
        string beneficiaryAccountNumber, string beneficiaryBankCode,
        SplitBasis basis, int shareBps, long flatKobo, int priority)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (basis == SplitBasis.Percentage && shareBps is < 0 or > 10000)
            throw new DomainException("Percentage share must be between 0 and 10000 basis points.");
        if (basis == SplitBasis.Flat && flatKobo < 0)
            throw new DomainException("Flat amount cannot be negative.");
        TenantId = tenantId;
        VirtualAccountId = virtualAccountId;
        BeneficiaryName = DomainException.Require(beneficiaryName, nameof(beneficiaryName));
        BeneficiaryAccountNumber = DomainException.Require(beneficiaryAccountNumber, nameof(beneficiaryAccountNumber));
        BeneficiaryBankCode = DomainException.Require(beneficiaryBankCode, nameof(beneficiaryBankCode));
        Basis = basis;
        ShareBps = shareBps;
        FlatKobo = flatKobo;
        Priority = priority;
        Enabled = true;
    }

    /// <summary>This leg's share of <paramref name="netKobo"/> before remainder allocation.</summary>
    public long BaseAmountKobo(long netKobo) => Basis == SplitBasis.Percentage
        ? (long)((decimal)netKobo * ShareBps / 10000m)
        : Math.Min(FlatKobo, netKobo);
}
