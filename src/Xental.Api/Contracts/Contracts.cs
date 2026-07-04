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

/// <summary>Set a sub-merchant's payout account + the operator's platform fee (basis points, 1% = 100).
/// The account name is resolved from the bank, not supplied.</summary>
public sealed record SetSubMerchantPayoutRequest(
    [Required, StringLength(200)] string BankName,
    [Required, StringLength(16)] string BankCode,
    [Required, StringLength(20)] string AccountNumber,
    [Range(0, 10000)] int PlatformFeeBps);

public sealed record SubMerchantResponse(
    Guid Id,
    string Name,
    string Reference,
    string Status,
    bool HasPayoutAccount,
    string? SettlementBankName,
    string? SettlementBankCode,
    string? SettlementAccountNumber,
    string? SettlementAccountName,
    int PlatformFeeBps,
    DateTimeOffset CreatedAtUtc);

public sealed record SubMerchantBalanceResponse(
    Guid SubMerchantId,
    string Reference,
    long CollectedKobo,
    long SettledKobo,
    long PendingKobo,
    int VirtualAccounts);

// ---- Virtual accounts (Phase 2) ----
public sealed record CreateVirtualAccountRequest(
    [Required, StringLength(100, MinimumLength = 1)] string AccountRef,
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [EmailAddress] string? Email,
    string? Phone,
    [Range(0, long.MaxValue)] long? ExpectedAmountKobo,
    DateTimeOffset? ExpiryDateUtc,
    [StringLength(100)] string? SubMerchantRef);

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
    Guid? SubMerchantId,
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

// ---- Onboarding / KYC (dashboard plane) ----
/// <summary>The tenant's onboarding state. Sandbox on signup; Live after KYC + KYB approval.</summary>
public sealed record OnboardingStatusResponse(
    string Tier,                 // Sandbox | Live
    string DeveloperKycStatus,   // NotStarted | InProgress | UnderReview | MoreInfoNeeded | Approved | Rejected
    string BusinessKybStatus,
    bool CanIssueLiveKeys,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionReason);

/// <summary>Developer KYC submission (personal identity + regulatory id + bank + profile).</summary>
public sealed record SubmitDeveloperKycRequest(
    [Required, StringLength(200, MinimumLength = 2)] string FullName,
    [Required] DateOnly DateOfBirth,
    [Required, StringLength(100)] string Country,
    [Required, StringLength(500)] string Address,
    [Required, RegularExpression("^(Bvn|Nin)$", ErrorMessage = "IdType must be 'Bvn' or 'Nin'.")] string IdType,
    [Required, StringLength(11, MinimumLength = 11)] string IdNumber,
    [Required, StringLength(200)] string BankName,
    [Required, StringLength(16)] string BankCode,
    [Required, StringLength(200)] string BankAccountName,
    [Required, StringLength(20)] string BankAccountNumber,
    [Url, StringLength(500)] string? PortfolioUrl,
    [StringLength(2000)] string? ProjectDescription);

/// <summary>Business KYB submission (business info + contact + settlement account).</summary>
public sealed record SubmitBusinessKybRequest(
    [Required, StringLength(200, MinimumLength = 2)] string LegalName,
    [Required, StringLength(50)] string RegistrationNumber,
    [Required, StringLength(100)] string BusinessType,
    [Required, StringLength(100)] string Industry,
    [Required, StringLength(100)] string Country,
    [Required, StringLength(500)] string Address,
    [Required, StringLength(8)] string ContactCountryCode,
    [Required, StringLength(32)] string ContactPhone,
    [Url, StringLength(300)] string? Website,
    [Required, StringLength(200)] string SettlementBankName,
    [Required, StringLength(16)] string SettlementBankCode,
    [Required, StringLength(200)] string SettlementAccountName,
    [Required, StringLength(20)] string SettlementAccountNumber);

/// <summary>Final attestation + submit (moves KYB to Under Review).</summary>
public sealed record SubmitOnboardingRequest(
    [Required] bool AttestationAccepted);

// ---- Admin plane ----
public sealed record AdminLoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password,
    string? TotpCode);

public sealed record AdminLoginResponse(string AccessToken, string TokenType, int ExpiresIn, string Email, string Role);

public sealed record CreateAdminRequest(
    [Required, EmailAddress] string Email,
    [Required, StringLength(128, MinimumLength = 12)] string Password,
    [Required, RegularExpression("^(Admin|SuperAdmin)$", ErrorMessage = "Role must be 'Admin' or 'SuperAdmin'.")] string Role);

/// <summary>Admin review action targeting a track; Reason required for reject / request-info.</summary>
public sealed record ReviewActionRequest(
    [Required, RegularExpression("^(DeveloperKyc|BusinessKyb)$")] string Track,
    string? Reason);

public sealed record MfaEnrollResponse(string OtpAuthUri);

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

// ---- Live Checkout (differentiator) ----
public sealed record CreateCheckoutSessionRequest(
    [Required] string AccountRef,
    [Range(1, 86400)] int? TtlSeconds);

public sealed record CheckoutSessionResponse(
    string Token,
    string SnapshotUrl,
    string StreamUrl,
    DateTimeOffset ExpiresAtUtc,
    CheckoutSnapshotResponse Snapshot);

public sealed record CheckoutSnapshotResponse(
    string AccountRef,
    string AccountNumber,
    string BankName,
    string AccountName,
    string PaymentState,
    long AmountPaidKobo,
    long? ExpectedAmountKobo);

// ---- Split & Escrow settlement (differentiator) ----
public sealed record SplitLegRequest(
    [Required, StringLength(200, MinimumLength = 1)] string BeneficiaryName,
    [Required] string AccountNumber,
    [Required] string BankCode,
    [Required] string Basis,               // "Percentage" | "Flat"
    [Range(0, 10000)] int ShareBps,
    [Range(0, long.MaxValue)] long FlatKobo,
    int Priority);

public sealed record SetSplitsRequest([Required] List<SplitLegRequest> Splits);

public sealed record SplitLegResponse(
    Guid Id, string BeneficiaryName, string AccountNumber, string BankCode,
    string Basis, int ShareBps, long FlatKobo, int Priority, bool Enabled);

public sealed record EscrowHoldRequest(string? ReleaseCondition);

public sealed record EscrowHoldResponse(
    Guid Id, string AccountRef, long AmountKobo, string State, string? ReleaseCondition, DateTimeOffset CreatedAtUtc);

// ---- Money Rules engine (differentiator) ----
public sealed record CreateRuleRequest(
    [Required] string Trigger,             // AnyDeposit | Overpaid | Underpaid | HighRisk | FullyPaid
    [Required] string Action,              // Hold | Notify | ReviewFlag
    [Range(0, long.MaxValue)] long? ThresholdKobo,
    [Range(0, 100)] int? MinRiskScore,
    int Priority);

public sealed record RuleResponse(
    Guid Id, string Trigger, string Action, long? ThresholdKobo, int? MinRiskScore, bool Enabled, int Priority);

// ---- Sandbox simulator (agent layer) ----
public sealed record SimulateDepositRequest(
    [Required] string AccountRef,
    [Range(1, long.MaxValue)] long AmountKobo,
    string? SenderName,
    bool? Reversal);

public sealed record SimulatedDepositResponse(
    string Status, string? Reference, string? Reconciliation, string? PaymentState, string? Reason);

// ---- Team members (dashboard plane) ----
public sealed record AddTeamMemberRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required, EmailAddress] string Email,
    [Required] string Role);   // Admin | Employee | Developer

public sealed record UpdateTeamMemberRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required, EmailAddress] string Email,
    [Required] string Role);

public sealed record TeamMemberResponse(
    Guid Id, string Name, string Email, string Role, string Status, DateTimeOffset CreatedAtUtc);

public sealed record AcceptInviteRequest(
    [Required] string Token,
    [Required, StringLength(128, MinimumLength = 12)] string Password);
