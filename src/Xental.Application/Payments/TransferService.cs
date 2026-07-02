using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Common;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>
/// Outbound bank transfers (payouts/settlement). Idempotent on <c>merchantTxRef</c>: the Pending
/// transfer is persisted before the provider is called, so a retried ref never moves money twice.
/// </summary>
public sealed class TransferService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    INombaClient nomba,
    IClock clock)
{
    public Task<BankAccountName> LookupAsync(string accountNumber, string bankCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(bankCode))
            throw new ValidationException("accountNumber and bankCode are required.");
        return nomba.LookupBankAccountAsync(accountNumber.Trim(), bankCode.Trim(), ct);
    }

    public async Task<Transfer> InitiateAsync(
        string merchantTxRef, long amountKobo, string accountNumber, string bankCode, string? narration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(merchantTxRef))
            throw new ValidationException("merchantTxRef is required.");
        if (amountKobo <= 0)
            throw new ValidationException("amountKobo must be positive.");
        if (string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(bankCode))
            throw new ValidationException("accountNumber and bankCode are required.");

        var tenantId = tenantContext.RequireTenantId();
        var reference = merchantTxRef.Trim();

        var existing = await db.Transfers.FirstOrDefaultAsync(t => t.MerchantTxRef == reference, ct);
        if (existing is not null)
            return existing; // idempotent replay

        var transfer = new Transfer(
            tenantId, reference, Money.FromKobo(amountKobo), accountNumber.Trim(), bankCode.Trim(), null, narration);
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(ct); // persist Pending first — the ref is now reserved

        var result = await nomba.InitiateTransferAsync(reference, amountKobo, accountNumber.Trim(), bankCode.Trim(), narration, ct);
        if (result.Success)
            transfer.MarkSucceeded(result.ProviderReference ?? reference, clock.UtcNow);
        else
            transfer.MarkFailed(result.FailureReason ?? "Transfer failed.", clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return transfer;
    }

    public async Task<Transfer> GetAsync(string merchantTxRef, CancellationToken ct = default)
    {
        var reference = (merchantTxRef ?? string.Empty).Trim();
        return await db.Transfers.AsNoTracking().FirstOrDefaultAsync(t => t.MerchantTxRef == reference, ct)
            ?? throw new NotFoundException($"No transfer with ref '{reference}'.");
    }
}
