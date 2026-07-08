namespace Xental.Application.Common.Interfaces;

/// <summary>A NUBAN provisioned by the provider (Nomba) for a customer.</summary>
public sealed record ProvisionedVirtualAccount(
    string AccountNumber,
    string BankName,
    string AccountName,
    string? ProviderAccountId);

/// <summary>Resolved account holder name for a bank account (payout pre-check).</summary>
public sealed record BankAccountName(string AccountName, string AccountNumber, string BankCode);

/// <summary>Result of initiating an outbound bank transfer.</summary>
public sealed record TransferResult(bool Success, string? ProviderReference, string? FailureReason);

/// <summary>A bank the provider can pay out to (name + code).</summary>
public sealed record BankInfo(string Name, string Code);

/// <summary>
/// Southbound client for Nomba. Creating a virtual account is scoped to the operator's
/// configured platform sub-account. Implemented in Infrastructure; a mock is used in tests.
/// </summary>
public interface INombaClient
{
    Task<ProvisionedVirtualAccount> CreateVirtualAccountAsync(
        string accountRef, string accountName, string? email, string? phone, CancellationToken ct = default);

    /// <summary>Resolve the account holder's name for a payout (name-check before sending).</summary>
    Task<BankAccountName> LookupBankAccountAsync(string accountNumber, string bankCode, CancellationToken ct = default);

    /// <summary>Initiate an outbound bank transfer (payout), keyed by the merchant tx ref.</summary>
    Task<TransferResult> InitiateTransferAsync(
        string merchantTxRef, long amountKobo, string accountNumber, string bankCode, string? accountName, string? narration, CancellationToken ct = default);

    /// <summary>The provider's list of payable banks (name + code). Empty if unavailable.</summary>
    Task<IReadOnlyList<BankInfo>> GetBanksAsync(CancellationToken ct = default);
}
