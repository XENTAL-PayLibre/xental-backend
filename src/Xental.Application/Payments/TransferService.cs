using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xental.Application.Common;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Common;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>
/// Outbound bank transfers (payouts/settlement). Idempotent on <c>merchantTxRef</c>: the Pending
/// transfer is persisted before the provider is called, so a retried ref never moves money twice.
/// Enforces the per-tenant daily payout cap.
/// </summary>
public sealed class TransferService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    INombaClient nomba,
    IOptions<TierLimitOptions> limits,
    IClock clock)
{
    private readonly TierLimitOptions _limits = limits.Value;

    public Task<BankAccountName> LookupAsync(string accountNumber, string bankCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(bankCode))
            throw new ValidationException("accountNumber and bankCode are required.");
        return nomba.LookupBankAccountAsync(accountNumber.Trim(), bankCode.Trim(), ct);
    }

    /// <summary>The provider's payable-bank list. Returns empty if the provider is unavailable so the
    /// caller can fall back to a built-in list.</summary>
    public async Task<IReadOnlyList<BankInfo>> GetBanksAsync(CancellationToken ct = default)
    {
        try { return await nomba.GetBanksAsync(ct); }
        catch (NombaIntegrationException) { return Array.Empty<BankInfo>(); }
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

        // Per-tenant daily payout cap (0 = unlimited). Checked before reserving the ref.
        if (_limits.DailyPayoutCapKobo > 0)
        {
            var startOfDayUtc = new DateTimeOffset(clock.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
            var spentToday = await db.Transfers
                .Where(t => t.TenantId == tenantId && t.Status == TransferStatus.Success && t.CreatedAtUtc >= startOfDayUtc)
                .SumAsync(t => (long?)t.AmountKobo, ct) ?? 0;
            if (spentToday + amountKobo > _limits.DailyPayoutCapKobo)
                throw new ValidationException("Daily payout limit exceeded. Contact support to raise your limit.");
        }

        var transfer = new Transfer(
            tenantId, reference, Money.FromKobo(amountKobo), accountNumber.Trim(), bankCode.Trim(), null, narration);
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(ct); // persist Pending first — the ref is now reserved

        var result = await nomba.InitiateTransferAsync(reference, amountKobo, accountNumber.Trim(), bankCode.Trim(), null, narration, ct);
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

    /// <summary>The tenant's payouts, most recent first (dashboard/API list).</summary>
    public async Task<IReadOnlyList<Transfer>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        return await db.Transfers.AsNoTracking()
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
    }
}
