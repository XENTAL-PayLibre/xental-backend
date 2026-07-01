using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Authentication;
using Xental.Application.Common.Interfaces;
using Xental.Application.Tenancy;
using Xental.Infrastructure.Configuration;

namespace Xental.Api.Controllers;

/// <summary>
/// Developer account lifecycle (the dashboard plane): registration, login, email
/// verification, password reset, and profile. Register and login return a
/// <c>dashboard</c>-scoped JWT used to manage the account and API keys.
/// </summary>
[ApiController]
[Route("api/v1/developers")]
public sealed class DevelopersController(
    DeveloperRegistrationService registration,
    DeveloperAuthService auth,
    DeveloperProfileService profiles,
    EmailVerificationService emailVerification,
    PasswordResetService passwordReset,
    ITenantContext tenant,
    IOptions<AppOptions> app) : ControllerBase
{
    /// <summary>Create a developer account and email a verification link.</summary>
    /// <response code="201">Account created; body carries the dashboard access token.</response>
    /// <response code="409">An account with this email already exists.</response>
    [AllowAnonymous]
    [HttpPost("register")]
    [ProducesResponseType(typeof(DeveloperAuthResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeveloperAuthResponse>> Register(RegisterDeveloperRequest request, CancellationToken ct)
    {
        var result = await registration.RegisterAsync(request.Name, request.Email, request.Password, ct);

        // Best-effort: a failed email must not fail registration (user can resend).
        await emailVerification.SendAsync(result.TenantId, ct);

        var response = new DeveloperAuthResponse(
            result.TenantId, result.Email, result.EmailVerified,
            result.DashboardToken.Token, "Bearer", result.DashboardToken.ExpiresInSeconds);
        return Created($"/api/v1/developers/{result.TenantId}", response);
    }

    /// <summary>Log in with email + password. Returns a dashboard token.</summary>
    /// <response code="200">Authenticated; body carries the dashboard access token.</response>
    /// <response code="401">Invalid email or password.</response>
    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType(typeof(DeveloperAuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DeveloperAuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request.Email, request.Password, ct);
        var response = new DeveloperAuthResponse(
            result.TenantId, result.Email, result.EmailVerified,
            result.Token.Token, "Bearer", result.Token.ExpiresInSeconds);
        return Ok(response);
    }

    /// <summary>The current account's profile.</summary>
    [Authorize(Policy = AuthPolicies.Dashboard)]
    [HttpGet("me")]
    [ProducesResponseType(typeof(DeveloperProfileResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeveloperProfileResponse>> Me(CancellationToken ct)
    {
        var p = await profiles.GetAsync(tenant.RequireTenantId(), ct);
        return Ok(new DeveloperProfileResponse(p.TenantId, p.Name, p.Email, p.EmailVerified, p.Status, p.CreatedAtUtc));
    }

    /// <summary>Magic-link target: verify the account's email, then redirect to the app.</summary>
    /// <response code="302">Redirects to the app with <c>?verified=true|false</c>.</response>
    [AllowAnonymous]
    [HttpGet("verify-email")]
    public async Task<IActionResult> VerifyEmail([FromQuery] string token, CancellationToken ct)
    {
        var ok = await emailVerification.VerifyAsync(token, ct);
        var baseUrl = app.Value.BaseUrl.TrimEnd('/');
        return Redirect($"{baseUrl}/email-verified?verified={(ok ? "true" : "false")}");
    }

    /// <summary>Re-send the verification email to the current account.</summary>
    [Authorize(Policy = AuthPolicies.Dashboard)]
    [HttpPost("resend-verification")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResendVerification(CancellationToken ct)
    {
        await emailVerification.SendAsync(tenant.RequireTenantId(), ct);
        return Accepted();
    }

    /// <summary>Request a password-reset link. Always returns 202 (no account enumeration).</summary>
    [AllowAnonymous]
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await passwordReset.RequestAsync(request.Email, ct);
        return Accepted();
    }

    /// <summary>Set a new password from a reset token.</summary>
    /// <response code="204">Password updated.</response>
    /// <response code="400">Token invalid/expired, or password too weak.</response>
    [AllowAnonymous]
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        var ok = await passwordReset.ResetAsync(request.Token, request.NewPassword, ct);
        return ok ? NoContent() : BadRequest(new { title = "Invalid or expired reset link." });
    }
}
