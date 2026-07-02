namespace Xental.Application.Common.Interfaces;

/// <summary>A NUBAN provisioned by the provider (Nomba) for a customer.</summary>
public sealed record ProvisionedVirtualAccount(
    string AccountNumber,
    string BankName,
    string AccountName,
    string? ProviderAccountId);

/// <summary>
/// Southbound client for Nomba. Creating a virtual account is scoped to the operator's
/// configured platform sub-account. Implemented in Infrastructure; a mock is used in tests.
/// </summary>
public interface INombaClient
{
    Task<ProvisionedVirtualAccount> CreateVirtualAccountAsync(
        string accountRef, string accountName, string? email, string? phone, CancellationToken ct = default);
}
