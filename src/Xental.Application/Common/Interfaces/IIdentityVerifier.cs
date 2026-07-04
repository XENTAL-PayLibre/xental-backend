namespace Xental.Application.Common.Interfaces;

/// <summary>Government-record identity returned by a BVN/NIN lookup.</summary>
public sealed record IdentityResult(
    bool Found,
    string? FirstName,
    string? LastName,
    DateOnly? DateOfBirth)
{
    public string? FullName => Found ? $"{FirstName} {LastName}".Trim() : null;
    public static IdentityResult NotFound() => new(false, null, null, null);
}

/// <summary>Company record returned by a CAC/RC-number lookup.</summary>
public sealed record CompanyResult(bool Found, string? CompanyName, string? RcNumber)
{
    public static CompanyResult NotFound() => new(false, null, null);
}

/// <summary>
/// Southbound identity-verification client (Dojah). Data checks only — no biometrics/liveness;
/// documents are reviewed by an admin. Implemented in Infrastructure; a fake is used in tests.
/// Implementations must never throw for a "not found" result and must not log the raw id.
/// </summary>
public interface IIdentityVerifier
{
    Task<IdentityResult> VerifyBvnAsync(string bvn, CancellationToken ct = default);
    Task<IdentityResult> VerifyNinAsync(string nin, CancellationToken ct = default);
    Task<CompanyResult> VerifyCacAsync(string rcNumber, CancellationToken ct = default);
}
