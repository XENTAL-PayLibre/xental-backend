using Xental.Domain.Common;

namespace Xental.Domain.Onboarding;

public enum GovIdType { Bvn = 1, Nin = 2 }

/// <summary>
/// The individual operating a developer account (KYC track). One per tenant. The government id
/// number (BVN/NIN) is PII: it is stored <b>encrypted</b> (the application layer protects it before
/// it reaches this entity — the domain only holds opaque ciphertext) alongside a non-reversible
/// hash used for dedup/reference.
/// </summary>
public sealed class DeveloperKyc : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string FullName { get; private set; } = null!;
    public DateOnly DateOfBirth { get; private set; }
    public string Country { get; private set; } = null!;
    public string Address { get; private set; } = null!;

    public GovIdType IdType { get; private set; }
    public string IdNumberEncrypted { get; private set; } = null!; // ciphertext (never the raw BVN/NIN)
    public string IdNumberHash { get; private set; } = null!;       // SHA-256, for dedup/reference

    public string BankName { get; private set; } = null!;
    public string BankCode { get; private set; } = null!;
    public string BankAccountName { get; private set; } = null!;
    public string BankAccountNumber { get; private set; } = null!;

    public string? PortfolioUrl { get; private set; }
    public string? ProjectDescription { get; private set; }

    private DeveloperKyc() { } // EF

    public DeveloperKyc(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        TenantId = tenantId;
    }

    /// <summary>
    /// Set/replace the KYC fields. <paramref name="idNumberEncrypted"/> and
    /// <paramref name="idNumberHash"/> are produced by the application layer (encryption/hashing) —
    /// the raw id number never enters the domain.
    /// </summary>
    public void Update(
        string fullName, DateOnly dateOfBirth, string country, string address,
        GovIdType idType, string idNumberEncrypted, string idNumberHash,
        string bankName, string bankCode, string bankAccountName, string bankAccountNumber,
        string? portfolioUrl, string? projectDescription)
    {
        FullName = DomainException.Require(fullName, nameof(fullName));
        if (dateOfBirth == default) throw new DomainException("Date of birth is required.");
        DateOfBirth = dateOfBirth;
        Country = DomainException.Require(country, nameof(country));
        Address = DomainException.Require(address, nameof(address));
        IdType = idType;
        IdNumberEncrypted = DomainException.Require(idNumberEncrypted, nameof(idNumberEncrypted));
        IdNumberHash = DomainException.Require(idNumberHash, nameof(idNumberHash));
        BankName = DomainException.Require(bankName, nameof(bankName));
        BankCode = DomainException.Require(bankCode, nameof(bankCode));
        BankAccountName = DomainException.Require(bankAccountName, nameof(bankAccountName));
        BankAccountNumber = DomainException.Require(bankAccountNumber, nameof(bankAccountNumber));
        PortfolioUrl = string.IsNullOrWhiteSpace(portfolioUrl) ? null : portfolioUrl.Trim();
        ProjectDescription = string.IsNullOrWhiteSpace(projectDescription) ? null : projectDescription.Trim();
    }
}
