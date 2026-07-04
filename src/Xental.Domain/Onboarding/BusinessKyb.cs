using Xental.Domain.Common;

namespace Xental.Domain.Onboarding;

/// <summary>
/// The company registering on the infrastructure (KYB track). One per tenant. Holds business
/// identity, contact, and the settlement bank account that outbound payouts route to once Live.
/// Documents (Cert of Incorporation, Proof of Address) are separate <see cref="KycDocument"/> rows.
/// </summary>
public sealed class BusinessKyb : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string LegalName { get; private set; } = null!;
    public string RegistrationNumber { get; private set; } = null!; // RC number
    public string BusinessType { get; private set; } = null!;
    public string Industry { get; private set; } = null!;
    public string Country { get; private set; } = null!;
    public string Address { get; private set; } = null!;

    public string ContactCountryCode { get; private set; } = null!;
    public string ContactPhone { get; private set; } = null!;
    public string? Website { get; private set; }

    public string SettlementBankName { get; private set; } = null!;
    public string SettlementBankCode { get; private set; } = null!;
    public string SettlementAccountName { get; private set; } = null!;
    public string SettlementAccountNumber { get; private set; } = null!;

    public bool AttestationAccepted { get; private set; }
    public DateTimeOffset? AttestationAtUtc { get; private set; }
    public string? AttestationIp { get; private set; }

    private BusinessKyb() { } // EF

    public BusinessKyb(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        TenantId = tenantId;
    }

    /// <summary>Set/replace the business + settlement fields (steps 1 & 3 of the wizard).</summary>
    public void Update(
        string legalName, string registrationNumber, string businessType, string industry,
        string country, string address, string contactCountryCode, string contactPhone, string? website,
        string settlementBankName, string settlementBankCode, string settlementAccountName, string settlementAccountNumber)
    {
        LegalName = DomainException.Require(legalName, nameof(legalName));
        RegistrationNumber = DomainException.Require(registrationNumber, nameof(registrationNumber));
        BusinessType = DomainException.Require(businessType, nameof(businessType));
        Industry = DomainException.Require(industry, nameof(industry));
        Country = DomainException.Require(country, nameof(country));
        Address = DomainException.Require(address, nameof(address));
        ContactCountryCode = DomainException.Require(contactCountryCode, nameof(contactCountryCode));
        ContactPhone = DomainException.Require(contactPhone, nameof(contactPhone));
        Website = string.IsNullOrWhiteSpace(website) ? null : website.Trim();
        SettlementBankName = DomainException.Require(settlementBankName, nameof(settlementBankName));
        SettlementBankCode = DomainException.Require(settlementBankCode, nameof(settlementBankCode));
        SettlementAccountName = DomainException.Require(settlementAccountName, nameof(settlementAccountName));
        SettlementAccountNumber = DomainException.Require(settlementAccountNumber, nameof(settlementAccountNumber));
    }

    /// <summary>Record the applicant's final attestation (step 4). Required before submission.</summary>
    public void Attest(DateTimeOffset at, string? ip)
    {
        AttestationAccepted = true;
        AttestationAtUtc = at;
        AttestationIp = ip;
    }
}
