using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Xental.Api.Auth;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Authentication;
using Xental.Application.Common.Interfaces;
using Xental.Application.Tenancy;
using Xental.Infrastructure.Configuration;

namespace Xental.Api.Controllers;

/// <summary>
/// Developer account lifecycle (the dashboard plane): registration, email verification,
/// login (verified accounts only), session refresh, logout, password reset, and profile.
/// Sessions are cookie-based: login sets HttpOnly+Secure <c>xnt_access</c> (short-lived)
/// and <c>xnt_refresh</c> (rotating) cookies — tokens are never returned in the body.
/// Sensitive endpoints are rate-limited.
/// </summary>
[ApiController]
[Route("api/v1/developers")]
[EnableRateLimiting("auth")]
public sealed class DevelopersController(
    DeveloperRegistrationService registration,
    DeveloperAuthService auth,
    DeveloperProfileService profiles,
    EmailVerificationService emailVerification,
    PasswordResetService passwordReset,
    AuthCookieWriter cookies,
    ITenantContext tenant,
    IOptions<AppOptions> app) : ControllerBase
{
    /// <summary>Create an account and email a verification link. Does NOT sign in.</summary>
    /// <response code="201">Account created; verify the emailed link before logging in.</response>
    /// <response code="409">An account with this email already exists.</response>
    [AllowAnonymous]
    [HttpPost("register")]
    [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegisterResponse>> Register(RegisterDeveloperRequest request, CancellationToken ct)
    {
        var result = await registration.RegisterAsync(request.Name, request.Email, request.Password, ct);
        await emailVerification.SendAsync(result.TenantId, ct); // best-effort
        return Created($"/api/v1/developers/{result.TenantId}", new RegisterResponse(
            result.TenantId, result.Email, result.EmailVerified,
            "Account created. Check your email for a verification link to activate your account."));
    }

    /// <summary>Log in with email + password (verified accounts only). Sets session cookies.</summary>
    /// <response code="200">Authenticated; session cookies set.</response>
    /// <response code="401">Invalid email or password.</response>
    /// <summary>Step 1 of login: verify the password and email a one-time code. No session yet —
    /// call <c>login/verify</c> with the code to finish. Returns 202 on success.</summary>
    /// <response code="202">Password OK; a login code was emailed. Verify it to get a session.</response>
    /// <response code="401">Invalid email or password.</response>
    /// <response code="403">Email not verified.</response>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginChallengeResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<LoginChallengeResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var challenge = await auth.BeginLoginAsync(request.Email, request.Password, ct);
        return Accepted(new LoginChallengeResponse(challenge.Email, challenge.ExpiresAtUtc,
            "A login code was sent to your email. Enter it to finish signing in."));
    }

    /// <summary>Step 2 of login: verify the emailed code and start the session (sets cookies).</summary>
    /// <response code="200">Verified; session cookies set.</response>
    /// <response code="401">Invalid or expired code.</response>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login/verify")]
    [ProducesResponseType(typeof(SessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessionResponse>> VerifyLoginOtp(VerifyLoginOtpRequest request, CancellationToken ct)
    {
        var session = await auth.VerifyLoginOtpAsync(request.Email, request.Code, ct);
        cookies.SetSession(Response, session);
        return Ok(new SessionResponse(session.TenantId, session.Email, session.EmailVerified));
    }

    /// <summary>Rotate the session using the refresh cookie. Sets fresh cookies.</summary>
    /// <response code="200">Rotated; new session cookies set.</response>
    /// <response code="401">Missing/invalid/expired refresh cookie.</response>
    [AllowAnonymous]
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(SessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessionResponse>> Refresh(CancellationToken ct)
    {
        var refresh = Request.Cookies[AuthCookieWriter.RefreshCookie] ?? string.Empty;
        var session = await auth.RefreshAsync(refresh, ct);
        cookies.SetSession(Response, session);
        return Ok(new SessionResponse(session.TenantId, session.Email, session.EmailVerified));
    }

    /// <summary>Revoke the refresh token and clear the session cookies.</summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(Request.Cookies[AuthCookieWriter.RefreshCookie] ?? string.Empty, ct);
        cookies.Clear(Response);
        return NoContent();
    }

    /// <summary>The current account's profile.</summary>
    [Authorize(Policy = AuthPolicies.Dashboard)]
    [HttpGet("me")]
    [ProducesResponseType(typeof(DeveloperProfileResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeveloperProfileResponse>> Me(CancellationToken ct)
    {
        var p = await profiles.GetAsync(tenant.RequireTenantId(), ct);
        return Ok(ToResponse(p));
    }

    /// <summary>Set the public brand/product name payers see on checkout and payment instructions.</summary>
    [Authorize(Policy = AuthPolicies.Dashboard)]
    [HttpPut("me/brand")]
    [ProducesResponseType(typeof(DeveloperProfileResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeveloperProfileResponse>> SetBrand(SetBrandNameRequest request, CancellationToken ct)
    {
        var p = await profiles.SetBrandNameAsync(tenant.RequireTenantId(), request.BrandName, ct);
        return Ok(ToResponse(p));
    }

    private static DeveloperProfileResponse ToResponse(DeveloperProfile p) =>
        new(p.TenantId, p.Name, p.Email, p.BrandName, p.EmailVerified, p.Status, p.CreatedAtUtc);

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

    /// <summary>Set a new password from a reset token (must meet the strong-password policy).</summary>
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
