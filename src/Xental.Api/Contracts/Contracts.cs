using System.ComponentModel.DataAnnotations;

namespace Xental.Api.Contracts;

// ---- Developer account (dashboard plane) ----
// Password rules (length + complexity) are enforced server-side by PasswordPolicy,
// which returns a descriptive message; the DTO only caps the max length.
public sealed record RegisterDeveloperRequest(
    [Required, StringLength(200, MinimumLength = 2)] string Name,
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(128)] string Password);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

/// <summary>Register response — no token; the account must verify its email first.</summary>
public sealed record RegisterResponse(
    Guid TenantId,
    string Email,
    bool EmailVerified,
    string Message);

/// <summary>
/// Login/refresh/OAuth response. Tokens are NOT here — they are set as HttpOnly, Secure
/// cookies (xnt_access, xnt_refresh). The body only carries who is logged in.
/// </summary>
public sealed record SessionResponse(
    Guid TenantId,
    string Email,
    bool EmailVerified);

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
    [Required, StringLength(128)] string NewPassword);

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

// ---- Virtual accounts (Phase 2) ----
public sealed record CreateVirtualAccountRequest(
    [Required, StringLength(100, MinimumLength = 1)] string AccountRef,
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [EmailAddress] string? Email,
    string? Phone,
    [Range(0, long.MaxValue)] long? ExpectedAmountKobo,
    DateTimeOffset? ExpiryDateUtc);

public sealed record VirtualAccountResponse(
    Guid Id,
    string AccountRef,
    string AccountNumber,
    string BankName,
    string AccountName,
    long? ExpectedAmountKobo,
    long AmountPaidKobo,
    long DeficitKobo,
    long OverpaymentKobo,
    string Status,
    string PaymentState,
    DateTimeOffset? ExpiryDateUtc,
    DateTimeOffset CreatedAtUtc);
