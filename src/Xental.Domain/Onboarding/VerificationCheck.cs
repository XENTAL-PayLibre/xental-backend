using Xental.Domain.Common;

namespace Xental.Domain.Onboarding;

public enum VerificationKind { Bvn = 1, Nin = 2, Nuban = 3, Cac = 4 }

/// <summary>Result of a single automated check.</summary>
public enum VerificationOutcome
{
    Verified = 1,   // provider confirmed and (where applicable) the name matched
    Mismatch = 2,   // provider responded but the data didn't match — needs a human
    Error = 3,      // provider/network error — needs a human
}

/// <summary>
/// Immutable record of one automated verification (BVN/NIN/NUBAN/CAC). The evidence trail behind an
/// approval/appeal. Carries only a short non-PII summary — never the raw provider payload/PII.
/// </summary>
public sealed class VerificationCheck : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public VerificationKind Kind { get; private set; }
    public VerificationOutcome Outcome { get; private set; }
    public string Provider { get; private set; } = null!;
    public string? Detail { get; private set; }         // e.g. "name match" / "name mismatch" / "not found"
    public DateTimeOffset CheckedAtUtc { get; private set; }

    private VerificationCheck() { } // EF

    public VerificationCheck(Guid tenantId, VerificationKind kind, VerificationOutcome outcome, string provider, string? detail, DateTimeOffset checkedAtUtc)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        Kind = kind;
        Outcome = outcome;
        Provider = DomainException.Require(provider, nameof(provider));
        Detail = detail is { Length: > 300 } ? detail[..300] : detail;
        CheckedAtUtc = checkedAtUtc;
    }

    public bool Passed => Outcome == VerificationOutcome.Verified;
}
