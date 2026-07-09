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

/// <summary>Step 1 login response: a code was emailed; verify it to finish signing in.</summary>
public sealed record LoginChallengeResponse(string Email, DateTimeOffset ExpiresAtUtc, string Message)
{
    /// <summary>Always true — dashboard login requires an emailed code.</summary>
    public bool OtpRequired => true;
}

/// <summary>Step 2 login: the emailed one-time code.</summary>
public sealed record VerifyLoginOtpRequest(
    [Required, EmailAddress] string Email,
    [Required, StringLength(6, MinimumLength = 6)] string Code);

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
    string? BrandName,
    bool EmailVerified,
    string Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>Set the public brand/product name payers see (checkout, payment instructions).</summary>
public sealed record SetBrandNameRequest(
    [MaxLength(120)] string? BrandName);

public sealed record ForgotPasswordRequest(
    [Required, EmailAddress] string Email);

public sealed record ResetPasswordRequest(
    [Required] string Token,
    [Required, StringLength(128)] string NewPassword);

/// <summary>Change the signed-in account's password (requires the current password).</summary>
public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
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
    // Optional — the server generates a unique customer reference when omitted.
    [StringLength(100)] string? AccountRef,
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
    string? CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
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
    string? SenderAccountNumber,
    string? SenderBankCode,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? ReconciledAtUtc);

/// <summary>Pay-ins summary for the dashboard cards.</summary>
public sealed record TransactionSummaryResponse(
    int Total,
    long TotalPayinsKobo,
    int Successful,
    int Failed,
    int PendingReview,
    long SuccessfulKobo,
    long NetCreditedKobo);

/// <summary>Refund an overpayment. All fields optional — omit to refund the payer's captured source account.</summary>
public sealed record RefundOverpaymentRequest(
    [StringLength(20)] string? AccountNumber,
    [StringLength(16)] string? BankCode,
    [StringLength(200)] string? AccountName);

public sealed record RefundResponse(
    string Status, string TransferRef, long AmountKobo,
    string DestinationAccountNumber, string DestinationBankCode, string? ProviderReference);

// ---- Transfers / payouts (Phase 5) ----
public sealed record BankLookupRequest(
    [Required] string AccountNumber,
    [Required] string BankCode);

public sealed record BankLookupResponse(string AccountName, string AccountNumber, string BankCode);

/// <summary>A selectable bank for payout/settlement UIs (name shown, code sent behind the scenes).</summary>
public sealed record BankResponse(string Name, string Code);

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

// ---- Collections Intelligence (differentiator) ----
public sealed record AgingBucketResponse(string Label, int Accounts, long OutstandingKobo);
public sealed record AgingReportResponse(long TotalOutstandingKobo, IReadOnlyList<AgingBucketResponse> Buckets);

public sealed record ForecastWeekResponse(DateTimeOffset WeekStartUtc, long ScheduledKobo);
public sealed record CashFlowForecastResponse(
    int Days, long ScheduledDueKobo, double DailyRunRateKobo,
    long RunRateProjectedKobo, long ProjectedTotalKobo, IReadOnlyList<ForecastWeekResponse> Weeks);

public sealed record CustomerScoreResponse(
    string CustomerRef, string CustomerName, long ExpectedKobo, long PaidKobo, long OutstandingKobo,
    double CollectionRatePct, int Deposits, int DuePeriods, int LatePeriods, int Score, string Rating);

// ---- Copilot (agent plane) ----
public sealed record CopilotAskRequest([Required, StringLength(500, MinimumLength = 1)] string Prompt);

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
    string Brand,
    string PaymentState,
    long AmountPaidKobo,
    long? ExpectedAmountKobo);

// ---- Recurring billing (push model) ----

/// <summary>Create a recurring-billing schedule on a reusable virtual account.</summary>
public sealed record CreateBillingScheduleRequest(
    [Required] string AccountRef,
    /// <summary>Weekly | Monthly | Quarterly | Yearly.</summary>
    [Required] string Interval,
    [Range(1, long.MaxValue)] long AmountKobo,
    [Range(0, 3650)] int DueOffsetDays = 0,
    [MaxLength(200)] string? Description = null,
    [MaxLength(100)] string? Reference = null);

/// <summary>Set the expected amount for the next cycle (variable billing).</summary>
public sealed record SetNextAmountRequest(
    [Range(1, long.MaxValue)] long AmountKobo);

public sealed record BillingScheduleResponse(
    Guid Id,
    string Reference,
    string AccountRef,
    string Interval,
    string Status,
    long NextAmountKobo,
    int DueOffsetDays,
    int PeriodsGenerated,
    long CarryCreditKobo,
    DateTimeOffset? CurrentPeriodEndUtc,
    string? Description,
    DateTimeOffset CreatedAtUtc);

public sealed record BillingPeriodResponse(
    Guid Id,
    int Sequence,
    string Status,
    long ExpectedAmountKobo,
    long AmountAttributedKobo,
    long OutstandingKobo,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    DateTimeOffset DueDateUtc,
    DateTimeOffset? PaidAtUtc);

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

// ---- Programmable Payment Flows ----
public sealed record CreateFlowRequest(
    [Required, StringLength(120, MinimumLength = 1)] string Name,
    [Required] string Trigger,
    [Required, MinLength(1)] List<string> Actions,
    [Range(0, long.MaxValue)] long? MinAmountKobo,
    [Range(0, 100)] int? MinRiskScore,
    int Priority);

public sealed record SetFlowEnabledRequest(bool Enabled);

public sealed record FlowResponse(
    Guid Id, string Name, string Trigger, IReadOnlyList<string> Actions,
    long? MinAmountKobo, int? MinRiskScore, bool Enabled, int Priority, DateTimeOffset CreatedAtUtc);

public sealed record FlowRunResponse(
    Guid Id, Guid FlowId, string FlowName, string Trigger, string? AccountRef, string? TransactionRef, string Outcome, DateTimeOffset CreatedAtUtc);

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
