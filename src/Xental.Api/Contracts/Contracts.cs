using System.ComponentModel.DataAnnotations;

namespace Xental.Api.Contracts;

// ---- Developer account (dashboard plane) ----
public sealed record RegisterDeveloperRequest(
    [Required, StringLength(200, MinimumLength = 2)] string Name,
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(128, MinimumLength = 12)] string Password);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

/// <summary>Returned by register + login — a dashboard access token.</summary>
public sealed record DeveloperAuthResponse(
    Guid TenantId,
    string Email,
    bool EmailVerified,
    string AccessToken,
    string TokenType,
    int ExpiresIn);

/// <summary>The current developer account's profile (GET /developers/me).</summary>
public sealed record DeveloperProfileResponse(
    Guid TenantId,
    string Name,
    string Email,
    bool EmailVerified,
    string Status,
    DateTimeOffset CreatedAtUtc);

public sealed record ForgotPasswordRequest(
    [Required, EmailAddress] string Email);

public sealed record ResetPasswordRequest(
    [Required] string Token,
    [Required, StringLength(128, MinimumLength = 12)] string NewPassword);

// ---- API keys (created in the dashboard, used by the integration) ----
public sealed record CreateApiKeyRequest(
    [Required, StringLength(100, MinimumLength = 2)] string Label,
    [Required, RegularExpression("^(test|live)$", ErrorMessage = "Mode must be 'test' or 'live'.")] string Mode);

/// <summary>ClientSecret is present ONLY in the create response (shown once).</summary>
public sealed record ApiKeyResponse(
    Guid Id,
    string ClientId,
    string? ClientSecret,
    string Mode,
    string Label,
    string Status,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset CreatedAtUtc);

// ---- API auth (integration plane): client-credentials -> API JWT ----
public sealed record TokenRequest(
    [Required] string ClientId,
    [Required] string ClientSecret);

public sealed record TokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn);

// ---- Sub-merchants ----
public sealed record CreateSubMerchantRequest(
    [Required, StringLength(200, MinimumLength = 2)] string Name,
    [Required, StringLength(100, MinimumLength = 1)] string Reference);

public sealed record SubMerchantResponse(
    Guid Id,
    string Name,
    string Reference,
    string Status,
    DateTimeOffset CreatedAtUtc);
