using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Admin;
using Xental.Domain.Admin;
using Xental.Domain.Onboarding;

namespace Xental.Api.Controllers;

/// <summary>Admin authentication (email + password + TOTP MFA). Issues a bearer admin token.</summary>
[ApiController]
[Route("api/v1/admin/auth")]
[AllowAnonymous]
public sealed class AdminAuthController(AdminAuthService auth) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(AdminLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AdminLoginResponse>> Login(AdminLoginRequest req, CancellationToken ct)
    {
        var result = await auth.LoginAsync(req.Email, req.Password, req.TotpCode, ct);
        return Ok(new AdminLoginResponse(
            result.Token.Token, "Bearer", result.Token.ExpiresInSeconds, result.Email, result.Role));
    }
}

/// <summary>Admin onboarding/KYC review (cross-tenant, audited). Any admin.</summary>
[ApiController]
[Route("api/v1/admin/onboarding")]
[Authorize(Policy = AuthPolicies.Admin)]
public sealed class AdminOnboardingController(AdminOnboardingService review) : ControllerBase
{
    /// <summary>Onboarding applications, optionally filtered to a track status (e.g. UnderReview).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AdminOnboardingSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AdminOnboardingSummary>>> List([FromQuery] string? status, CancellationToken ct)
    {
        TrackStatus? filter = Enum.TryParse<TrackStatus>(status, ignoreCase: true, out var s) ? s : null;
        return Ok(await review.ListAsync(filter, ct));
    }

    /// <summary>Full detail for one tenant: fields, auto-check results, and document download URLs.</summary>
    [HttpGet("{tenantId:guid}")]
    [ProducesResponseType(typeof(AdminOnboardingDetail), StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminOnboardingDetail>> Detail(Guid tenantId, CancellationToken ct)
        => Ok(await review.GetDetailAsync(tenantId, ct));

    [HttpPost("{tenantId:guid}/approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Approve(Guid tenantId, ReviewActionRequest req, CancellationToken ct)
    {
        await review.ApproveAsync(tenantId, Track(req.Track), ct);
        return NoContent();
    }

    [HttpPost("{tenantId:guid}/reject")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reject(Guid tenantId, ReviewActionRequest req, CancellationToken ct)
    {
        await review.RejectAsync(tenantId, Track(req.Track), req.Reason ?? "", ct);
        return NoContent();
    }

    [HttpPost("{tenantId:guid}/request-info")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RequestInfo(Guid tenantId, ReviewActionRequest req, CancellationToken ct)
    {
        await review.RequestMoreInfoAsync(tenantId, Track(req.Track), req.Reason ?? "", ct);
        return NoContent();
    }

    private static OnboardingTrack Track(string t) =>
        t.Equals("DeveloperKyc", StringComparison.OrdinalIgnoreCase) ? OnboardingTrack.DeveloperKyc : OnboardingTrack.BusinessKyb;
}

/// <summary>Admin reconciliation console — exception buckets + failed-settlement retry (audited).</summary>
[ApiController]
[Route("api/v1/admin/reconciliation")]
[Authorize(Policy = AuthPolicies.Admin)]
public sealed class AdminReconciliationController(AdminReconciliationService recon) : ControllerBase
{
    /// <summary>Counts per exception bucket (headline tiles).</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ReconSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReconSummary>> Summary(CancellationToken ct) => Ok(await recon.SummaryAsync(ct));

    /// <summary>Transactions in a bucket: review|unknown|overpaid|underpaid|highrisk|reversals.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ReconTransactionView>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ReconTransactionView>>> List([FromQuery] string bucket, [FromQuery] int take = 200, CancellationToken ct = default)
        => Ok(await recon.ListAsync(bucket, take, ct));

    /// <summary>Fully-paid accounts whose auto-settlement failed and awaits retry.</summary>
    [HttpGet("settlements/failed")]
    [ProducesResponseType(typeof(IReadOnlyList<FailedSettlementView>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<FailedSettlementView>>> FailedSettlements(CancellationToken ct)
        => Ok(await recon.ListFailedSettlementsAsync(ct));

    /// <summary>Retry a failed settlement (the worker re-attempts the sweep next cycle).</summary>
    [HttpPost("settlements/{virtualAccountId:guid}/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RetrySettlement(Guid virtualAccountId, CancellationToken ct)
    {
        await recon.RetrySettlementAsync(virtualAccountId, ct);
        return NoContent();
    }
}

/// <summary>Admin management: SuperAdmin creates admins; any admin enrolls their own MFA.</summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AuthPolicies.Admin)]
public sealed class AdminManagementController(AdminManagementService management) : ControllerBase
{
    /// <summary>Create an admin (SuperAdmin only).</summary>
    [HttpPost("admins")]
    [Authorize(Policy = AuthPolicies.SuperAdmin)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAdmin(CreateAdminRequest req, CancellationToken ct)
    {
        var role = req.Role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase) ? AdminRole.SuperAdmin : AdminRole.Admin;
        var id = await management.CreateAdminAsync(req.Email, req.Password, role, ct);
        return Created($"/api/v1/admin/admins/{id}", new { id });
    }

    /// <summary>Enroll TOTP MFA for the current admin — returns the otpauth URI for the QR code.</summary>
    [HttpPost("mfa/enroll")]
    [ProducesResponseType(typeof(MfaEnrollResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MfaEnrollResponse>> EnrollMfa(CancellationToken ct)
        => Ok(new MfaEnrollResponse(await management.EnrollMfaAsync(ct)));
}
