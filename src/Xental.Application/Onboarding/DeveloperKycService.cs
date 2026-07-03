using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Common;
using Xental.Domain.Onboarding;

namespace Xental.Application.Onboarding;

/// <summary>Applicant-submitted developer KYC fields (the raw id is used once then discarded).</summary>
public sealed record DeveloperKycInput(
    string FullName, DateOnly DateOfBirth, string Country, string Address,
    GovIdType IdType, string IdNumber,
    string BankName, string BankCode, string BankAccountName, string BankAccountNumber,
    string? PortfolioUrl, string? ProjectDescription);

/// <summary>
/// Handles developer-KYC submission: persists the record (id number encrypted at rest), runs the
/// automated checks (BVN/NIN via <see cref="IIdentityVerifier"/>, NUBAN name-match via
/// <see cref="INombaClient"/>), records the evidence, and moves the track to Under Review. A human
/// always signs off — automation never auto-approves Live.
/// </summary>
public sealed class DeveloperKycService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    ISecretProtector protector,
    IIdentityVerifier identity,
    INombaClient nomba,
    IClock clock)
{
    public async Task SubmitAsync(DeveloperKycInput input, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        Validate(input);
        var now = clock.UtcNow;

        // ---- persist KYC (id number: encrypted + hashed; raw is never stored) ----
        var kyc = await db.DeveloperKycs.FirstOrDefaultAsync(k => k.TenantId == tenantId, ct);
        if (kyc is null) { kyc = new DeveloperKyc(tenantId); db.DeveloperKycs.Add(kyc); }
        kyc.Update(
            input.FullName.Trim(), input.DateOfBirth, input.Country.Trim(), input.Address.Trim(),
            input.IdType, protector.Protect(input.IdNumber), Sha256Hex(input.IdNumber),
            input.BankName.Trim(), input.BankCode.Trim(), input.BankAccountName.Trim(), input.BankAccountNumber.Trim(),
            input.PortfolioUrl, input.ProjectDescription);

        // ---- automated checks (evidence for the human reviewer) ----
        await RunIdentityCheckAsync(tenantId, input, now, ct);
        await RunNubanCheckAsync(tenantId, input, now, ct);

        // ---- move the track to Under Review (a human signs off Live) ----
        var app = await db.OnboardingApplications.FirstOrDefaultAsync(a => a.TenantId == tenantId, ct);
        if (app is null) { app = new OnboardingApplication(tenantId); db.OnboardingApplications.Add(app); }
        app.SubmitTrack(OnboardingTrack.DeveloperKyc, now);

        await db.SaveChangesAsync(ct);
    }

    private async Task RunIdentityCheckAsync(Guid tenantId, DeveloperKycInput input, DateTimeOffset now, CancellationToken ct)
    {
        var kind = input.IdType == GovIdType.Bvn ? VerificationKind.Bvn : VerificationKind.Nin;
        IdentityResult result;
        try
        {
            result = input.IdType == GovIdType.Bvn
                ? await identity.VerifyBvnAsync(input.IdNumber, ct)
                : await identity.VerifyNinAsync(input.IdNumber, ct);
        }
        catch
        {
            db.VerificationChecks.Add(new VerificationCheck(tenantId, kind, VerificationOutcome.Error, "dojah", "provider error", now));
            return;
        }

        var (outcome, detail) = !result.Found
            ? (VerificationOutcome.Error, "not found")
            : NameMatcher.IsMatch(result.FullName, input.FullName)
                ? (VerificationOutcome.Verified, "name match")
                : (VerificationOutcome.Mismatch, "name mismatch");
        db.VerificationChecks.Add(new VerificationCheck(tenantId, kind, outcome, "dojah", detail, now));
    }

    private async Task RunNubanCheckAsync(Guid tenantId, DeveloperKycInput input, DateTimeOffset now, CancellationToken ct)
    {
        VerificationOutcome outcome;
        string detail;
        try
        {
            var bank = await nomba.LookupBankAccountAsync(input.BankAccountNumber, input.BankCode, ct);
            // The bank account name must match the applicant's legal name (financial accountability).
            (outcome, detail) = NameMatcher.IsMatch(bank.AccountName, input.FullName)
                ? (VerificationOutcome.Verified, "name match")
                : (VerificationOutcome.Mismatch, "name mismatch");
        }
        catch
        {
            (outcome, detail) = (VerificationOutcome.Error, "lookup error");
        }
        db.VerificationChecks.Add(new VerificationCheck(tenantId, VerificationKind.Nuban, outcome, "nomba", detail, now));
    }

    private static void Validate(DeveloperKycInput i)
    {
        if (string.IsNullOrWhiteSpace(i.FullName)) throw new ValidationException("Full name is required.");
        if (string.IsNullOrWhiteSpace(i.IdNumber)) throw new ValidationException("BVN/NIN is required.");
        if (i.IdType == GovIdType.Bvn && i.IdNumber.Trim().Length != 11) throw new ValidationException("BVN must be 11 digits.");
        if (i.IdType == GovIdType.Nin && i.IdNumber.Trim().Length != 11) throw new ValidationException("NIN must be 11 digits.");
        if (string.IsNullOrWhiteSpace(i.BankAccountNumber) || string.IsNullOrWhiteSpace(i.BankCode))
            throw new ValidationException("Bank account number and bank code are required.");
        if (i.DateOfBirth == default) throw new ValidationException("Date of birth is required.");
        if (i.DateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-18)))
            throw new ValidationException("Applicant must be at least 18 years old.");
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
}
