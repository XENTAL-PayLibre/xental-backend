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

// ---- Transactions (Phase 5) ----
public sealed record TransactionResponse(
    Guid Id,
    string Reference,
    Guid? VirtualAccountId,
    long AmountKobo,
    long FeeKobo,
    long NetCreditKobo,
    string Status,
    string Reconciliation,
    string? Reason,
    int RiskScore,
    string? TransferName,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? ReconciledAtUtc);

// ---- Transfers / payouts (Phase 5) ----
public sealed record BankLookupRequest(
    [Required] string AccountNumber,
    [Required] string BankCode);

public sealed record BankLookupResponse(string AccountName, string AccountNumber, string BankCode);

public sealed record CreateTransferRequest(
    [Required, StringLength(100, MinimumLength = 1)] string MerchantTxRef,
    [Range(1, long.MaxValue)] long AmountKobo,
    [Required] string AccountNumber,
    [Required] string BankCode,
    string? Narration);

public sealed record TransferResponse(
    Guid Id,
    string MerchantTxRef,
    long AmountKobo,
    string RecipientAccountNumber,
    string RecipientBankCode,
    string Status,
    string? ProviderReference,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

// ---- Outbound webhooks (Phase 4) ----
public sealed record CreateWebhookEndpointRequest([Required, Url] string Url);

public sealed record WebhookEndpointResponse(Guid Id, string Url, bool Active, DateTimeOffset CreatedAtUtc);

/// <summary>Signing secret is present only in the create response (shown once).</summary>
public sealed record WebhookEndpointCreatedResponse(Guid Id, string Url, string SigningSecret);

public sealed record WebhookDeliveryResponse(
    Guid Id,
    Guid EndpointId,
    string EventType,
    string Status,
    int Attempts,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset CreatedAtUtc);

// ---- Settlement settings (Phase 6) ----
/// <summary>Update the tenant's settlement bank account + auto-settle preferences.</summary>
public sealed record UpdateSettlementRequest(
    [StringLength(20)] string? SettlementAccountNumber,
    [StringLength(16)] string? SettlementBankCode,
    [StringLength(200)] string? SettlementAccountName,
    bool AutoSettle,
    [Range(0, long.MaxValue)] long MinPayoutKobo);

public sealed record SettlementConfigResponse(
    string? SettlementAccountNumber,
    string? SettlementBankCode,
    string? SettlementAccountName,
    bool AutoSettle,
    long MinPayoutKobo,
    bool CanAutoSettle);

// ---- Insights (analytics) ----
public sealed record InsightsResponse(
    int VirtualAccounts,
    int Deposits,
    long TotalCollectedKobo,
    long ExpectedKobo,
    long OutstandingDeficitKobo,
    double CollectionRatePct,
    int Reconciled,
    int Underpaid,
    int Overpaid,
    int PendingReview,
    int HighRisk,
    int FullyPaidAccounts,
    int PartiallyPaidAccounts);
