using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Admin;
using Xental.Domain.Common;
using Xental.Domain.Onboarding;

namespace Xental.Application.Onboarding;

public sealed record BusinessKybInput(
    string LegalName, string RegistrationNumber, string BusinessType, string Industry,
    string Country, string Address, string ContactCountryCode, string ContactPhone, string? Website,
    string SettlementBankName, string SettlementBankCode, string SettlementAccountName, string SettlementAccountNumber);

/// <summary>
/// Business KYB: persists the company + settlement details (running CAC + settlement-NUBAN checks),
/// stores uploaded documents (validated, hashed, in object storage), and — once both documents are
/// present and the applicant attests — moves the KYB track to Under Review for an admin to approve.
/// </summary>
public sealed class BusinessKybService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    IIdentityVerifier identity,
    INombaClient nomba,
    IDocumentStorage storage,
    IClock clock,
    IEmailSender mailer)
{
    private const long MaxDocumentBytes = 10 * 1024 * 1024; // 10 MB
    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/jpeg", "image/jpg", "image/png" };

    public async Task SaveBusinessAsync(BusinessKybInput input, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        Validate(input);
        var now = clock.UtcNow;

        var kyb = await db.BusinessKybs.FirstOrDefaultAsync(k => k.TenantId == tenantId, ct);
        if (kyb is null) { kyb = new BusinessKyb(tenantId); db.BusinessKybs.Add(kyb); }
        kyb.Update(
            input.LegalName.Trim(), input.RegistrationNumber.Trim(), input.BusinessType.Trim(), input.Industry.Trim(),
            input.Country.Trim(), input.Address.Trim(), input.ContactCountryCode.Trim(), input.ContactPhone.Trim(), input.Website,
            input.SettlementBankName.Trim(), input.SettlementBankCode.Trim(), input.SettlementAccountName.Trim(), input.SettlementAccountNumber.Trim());

        await RunCacCheckAsync(tenantId, input, now, ct);
        await RunSettlementNubanCheckAsync(tenantId, input, now, ct);

        var app = await GetOrCreateAppAsync(tenantId, ct);
        app.MarkTrackInProgress(OnboardingTrack.BusinessKyb);
        await db.SaveChangesAsync(ct);
    }

    public async Task UploadDocumentAsync(KycDocumentType type, Stream content, string contentType, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        if (!AllowedContentTypes.Contains(contentType))
            throw new ValidationException("Document must be a PDF, JPG or PNG.");

        // Read into memory (bounded) so we can hash + size-check before storing.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) throw new ValidationException("Document is empty.");
        if (buffer.Length > MaxDocumentBytes) throw new ValidationException("Document exceeds the 10 MB limit.");
        buffer.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(buffer, ct)).ToLowerInvariant();
        buffer.Position = 0;

        var size = buffer.Length; // capture before upload — the storage client may consume the stream
        var objectKey = $"kyc/{tenantId:N}/{type}/{Guid.NewGuid():N}";
        await storage.PutAsync(objectKey, buffer, contentType, ct);

        // Replace any previous document of the same type.
        var existing = await db.KycDocuments.Where(d => d.TenantId == tenantId && d.Type == type).ToListAsync(ct);
        db.KycDocuments.RemoveRange(existing);
        db.KycDocuments.Add(new KycDocument(tenantId, type, objectKey, hash, contentType, size));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Final step: require both documents + attestation, then move to Under Review.</summary>
    public async Task SubmitAsync(bool attestationAccepted, string? ip, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        if (!attestationAccepted)
            throw new ValidationException("You must accept the attestation to submit.");

        var kyb = await db.BusinessKybs.FirstOrDefaultAsync(k => k.TenantId == tenantId, ct)
            ?? throw new ValidationException("Complete the business details before submitting.");

        var docTypes = await db.KycDocuments.Where(d => d.TenantId == tenantId).Select(d => d.Type).ToListAsync(ct);
        if (!docTypes.Contains(KycDocumentType.CertificateOfIncorporation) || !docTypes.Contains(KycDocumentType.ProofOfAddress))
            throw new ValidationException("Upload both the Certificate of Incorporation and Proof of Address before submitting.");

        var now = clock.UtcNow;
        kyb.Attest(now, ip);
        var app = await GetOrCreateAppAsync(tenantId, ct);
        app.SubmitTrack(OnboardingTrack.BusinessKyb, now);
        await db.SaveChangesAsync(ct);

        // Best-effort: alert every active admin that a KYB is awaiting review.
        var adminEmails = await db.AdminUsers
            .Where(a => a.Status == AdminStatus.Active)
            .Select(a => a.Email)
            .ToListAsync(ct);
        foreach (var adminEmail in adminEmails)
            await mailer.SendOnboardingReviewAlertAsync(adminEmail, "Business KYB", kyb.LegalName, ct);
    }

    private async Task RunCacCheckAsync(Guid tenantId, BusinessKybInput input, DateTimeOffset now, CancellationToken ct)
    {
        VerificationOutcome outcome; string detail;
        try
        {
            var company = await identity.VerifyCacAsync(input.RegistrationNumber, ct);
            (outcome, detail) = !company.Found
                ? (VerificationOutcome.Error, "not found")
                : NameMatcher.IsMatch(company.CompanyName, input.LegalName)
                    ? (VerificationOutcome.Verified, "name match")
                    : (VerificationOutcome.Mismatch, "name mismatch");
        }
        catch { (outcome, detail) = (VerificationOutcome.Error, "provider error"); }
        db.VerificationChecks.Add(new VerificationCheck(tenantId, VerificationKind.Cac, outcome, "dojah", detail, now));
    }

    private async Task RunSettlementNubanCheckAsync(Guid tenantId, BusinessKybInput input, DateTimeOffset now, CancellationToken ct)
    {
        VerificationOutcome outcome; string detail;
        try
        {
            var bank = await nomba.LookupBankAccountAsync(input.SettlementAccountNumber, input.SettlementBankCode, ct);
            (outcome, detail) = NameMatcher.IsMatch(bank.AccountName, input.LegalName)
                ? (VerificationOutcome.Verified, "name match")
                : (VerificationOutcome.Mismatch, "name mismatch");
        }
        catch { (outcome, detail) = (VerificationOutcome.Error, "lookup error"); }
        db.VerificationChecks.Add(new VerificationCheck(tenantId, VerificationKind.Nuban, outcome, "nomba", detail, now));
    }

    private async Task<OnboardingApplication> GetOrCreateAppAsync(Guid tenantId, CancellationToken ct)
    {
        var app = await db.OnboardingApplications.FirstOrDefaultAsync(a => a.TenantId == tenantId, ct);
        if (app is null) { app = new OnboardingApplication(tenantId); db.OnboardingApplications.Add(app); }
        return app;
    }

    private static void Validate(BusinessKybInput i)
    {
        if (string.IsNullOrWhiteSpace(i.LegalName)) throw new ValidationException("Business legal name is required.");
        if (string.IsNullOrWhiteSpace(i.RegistrationNumber)) throw new ValidationException("Registration (RC) number is required.");
        if (string.IsNullOrWhiteSpace(i.SettlementAccountNumber) || string.IsNullOrWhiteSpace(i.SettlementBankCode))
            throw new ValidationException("Settlement account number and bank code are required.");
    }
}
