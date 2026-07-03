using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Onboarding;
using Xental.Domain.Onboarding;

namespace Xental.Api.Controllers;

/// <summary>
/// KYC/KYB onboarding for the developer account (dashboard plane). Signup already grants Sandbox
/// (test keys); this is the path to Live. Submission + document endpoints are added in later phases.
/// </summary>
[ApiController]
[Route("api/v1/onboarding")]
[Authorize(Policy = AuthPolicies.Dashboard)]
public sealed class OnboardingController(
    OnboardingService onboarding,
    DeveloperKycService developerKyc,
    BusinessKybService businessKyb) : ControllerBase
{
    /// <summary>Current onboarding status + tier + whether live keys can be issued yet.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(OnboardingStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OnboardingStatusResponse>> Get(CancellationToken ct)
    {
        var app = await onboarding.GetOrCreateAsync(ct);
        return Ok(ToStatus(app));
    }

    /// <summary>
    /// Submit developer KYC. Runs the automated BVN/NIN + NUBAN name-match checks and moves the
    /// track to Under Review (an admin signs off before Live is granted).
    /// </summary>
    [HttpPost("developer")]
    [ProducesResponseType(typeof(OnboardingStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OnboardingStatusResponse>> SubmitDeveloper(SubmitDeveloperKycRequest req, CancellationToken ct)
    {
        var idType = req.IdType.Equals("Bvn", StringComparison.OrdinalIgnoreCase) ? GovIdType.Bvn : GovIdType.Nin;
        await developerKyc.SubmitAsync(new DeveloperKycInput(
            req.FullName, req.DateOfBirth, req.Country, req.Address, idType, req.IdNumber,
            req.BankName, req.BankCode, req.BankAccountName, req.BankAccountNumber,
            req.PortfolioUrl, req.ProjectDescription), ct);
        return Ok(ToStatus(await onboarding.GetOrCreateAsync(ct)));
    }

    /// <summary>Submit business KYB details (business info + settlement account). Runs CAC + NUBAN checks.</summary>
    [HttpPost("business")]
    [ProducesResponseType(typeof(OnboardingStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OnboardingStatusResponse>> SubmitBusiness(SubmitBusinessKybRequest req, CancellationToken ct)
    {
        await businessKyb.SaveBusinessAsync(new BusinessKybInput(
            req.LegalName, req.RegistrationNumber, req.BusinessType, req.Industry, req.Country, req.Address,
            req.ContactCountryCode, req.ContactPhone, req.Website,
            req.SettlementBankName, req.SettlementBankCode, req.SettlementAccountName, req.SettlementAccountNumber), ct);
        return Ok(ToStatus(await onboarding.GetOrCreateAsync(ct)));
    }

    /// <summary>Upload a KYB document (Cert of Incorporation or Proof of Address). PDF/JPG/PNG, ≤10 MB.</summary>
    [HttpPost("documents")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadDocument([FromForm] string type, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A file is required." });
        if (!Enum.TryParse<KycDocumentType>(type, ignoreCase: true, out var docType))
            return BadRequest(new { error = "type must be 'CertificateOfIncorporation' or 'ProofOfAddress'." });

        await using var stream = file.OpenReadStream();
        await businessKyb.UploadDocumentAsync(docType, stream, file.ContentType, ct);
        return NoContent();
    }

    /// <summary>Attest and submit the KYB track for admin review (requires both documents present).</summary>
    [HttpPost("submit")]
    [ProducesResponseType(typeof(OnboardingStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OnboardingStatusResponse>> Submit(SubmitOnboardingRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await businessKyb.SubmitAsync(req.AttestationAccepted, ip, ct);
        return Ok(ToStatus(await onboarding.GetOrCreateAsync(ct)));
    }

    private static OnboardingStatusResponse ToStatus(Xental.Domain.Onboarding.OnboardingApplication app) => new(
        app.Tier.ToString(),
        app.DeveloperKycStatus.ToString(),
        app.BusinessKybStatus.ToString(),
        CanIssueLiveKeys: app.Tier == KycTier.Live,
        app.SubmittedAtUtc,
        app.DecidedAtUtc,
        app.DecisionReason);
}
