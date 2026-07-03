using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Common;
using Xental.Domain.Payments;

namespace Xental.Infrastructure.Payments;

/// <summary>
/// Closes the loop: when a virtual account is fully paid and the tenant has auto-settle on,
/// sweeps the net collected amount (deposits less provider fees) to their settlement bank
/// account. Idempotent per account (a single <c>settle-{id}</c> transfer), threshold-aware, and
/// decoupled from the webhook hot path.
/// </summary>
public sealed class SettlementWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SettlementWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 25;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Settlement poll failed.");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Runs a single settlement pass. Exposed for tests; called on each poll tick.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var nomba = scope.ServiceProvider.GetRequiredService<INombaClient>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.UtcNow;

        var due = await db.VirtualAccounts.IgnoreQueryFilters()
            .Where(v => v.PaymentState == PaymentState.FullyPaid && v.SettledAtUtc == null && v.Status == VirtualAccountStatus.Active)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0)
            return;

        foreach (var account in due)
        {
            // Escrow (Feature 1): an active hold parks the funds — skip until it is released.
            var held = await db.EscrowHolds.IgnoreQueryFilters()
                .AnyAsync(e => e.VirtualAccountId == account.Id && e.State == EscrowState.Held, ct);
            if (held)
                continue;

            var config = await db.SettlementConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == account.TenantId, ct);
            if (config is null || !config.CanAutoSettle)
                continue; // not opted in / not configured — leave unsettled

            var net = await db.Transactions.IgnoreQueryFilters()
                .Where(t => t.VirtualAccountId == account.Id)
                .SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0;
            if (net <= 0 || net < config.MinPayoutKobo)
                continue; // below payout threshold — wait

            // Split settlement (Feature 1): account-specific legs override tenant-wide legs.
            var splits = await db.SettlementSplits.IgnoreQueryFilters()
                .Where(s => s.TenantId == account.TenantId && s.Enabled
                    && (s.VirtualAccountId == null || s.VirtualAccountId == account.Id))
                .ToListAsync(ct);
            var effective = splits.Where(s => s.VirtualAccountId == account.Id).ToList();
            if (effective.Count == 0)
                effective = splits.Where(s => s.VirtualAccountId == null).ToList();

            if (effective.Count == 0)
                await SettleSingleAsync(db, nomba, account, config, net, now, ct);
            else
                await SettleSplitAsync(db, nomba, account, effective, net, now, ct);
        }
    }

    /// <summary>Original behaviour: one sweep of the whole net to the tenant's settlement account.</summary>
    private async Task SettleSingleAsync(IApplicationDbContext db, INombaClient nomba, VirtualAccount account, SettlementConfig config, long net, DateTimeOffset now, CancellationToken ct)
    {
        var merchantRef = $"settle-{account.Id:N}";
        var existing = await db.Transfers.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.MerchantTxRef == merchantRef, ct);
        if (existing is not null)
        {
            if (existing.Status == TransferStatus.Success) { account.MarkSettled(now); await db.SaveChangesAsync(ct); }
            return; // failed ones await manual retry; don't re-initiate
        }

        var transfer = new Transfer(account.TenantId, merchantRef, Money.FromKobo(net),
            config.SettlementAccountNumber!, config.SettlementBankCode!, config.SettlementAccountName,
            $"Xental settlement for {account.Reference}");
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(ct); // reserve the ref before calling the provider

        var result = await nomba.InitiateTransferAsync(merchantRef, net, config.SettlementAccountNumber!, config.SettlementBankCode!, transfer.Narration, ct);
        if (result.Success)
        {
            transfer.MarkSucceeded(result.ProviderReference ?? merchantRef, now);
            account.MarkSettled(now);
            logger.LogInformation("Auto-settled {AccountRef}: {NetKobo} kobo -> {Account}", account.Reference, net, config.SettlementAccountNumber);
        }
        else
        {
            transfer.MarkFailed(result.FailureReason ?? "settlement failed", now);
            logger.LogWarning("Auto-settlement failed for {AccountRef}: {Reason}", account.Reference, result.FailureReason);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fan the net out across the split legs as N idempotent transfers (<c>settle-{id}-{i}</c>). The
    /// allocator guarantees the legs sum to exactly net (all-or-nothing); a misconfiguration that
    /// exceeds net aborts the whole settlement without moving any money. The account is marked
    /// settled only once every leg has succeeded.
    /// </summary>
    private async Task SettleSplitAsync(IApplicationDbContext db, INombaClient nomba, VirtualAccount account, List<SettlementSplit> splits, long net, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<SplitCalculator.Leg> legs;
        try { legs = SplitCalculator.Allocate(net, splits); }
        catch (DomainException ex)
        {
            logger.LogWarning("Split settlement for {AccountRef} skipped — {Reason}", account.Reference, ex.Message);
            return; // needs the tenant to fix their split plan; never settle partially
        }

        var allSucceeded = true;
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            if (leg.AmountKobo <= 0)
                continue;

            var merchantRef = $"settle-{account.Id:N}-{i}";
            var existing = await db.Transfers.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.MerchantTxRef == merchantRef, ct);
            if (existing is not null)
            {
                if (existing.Status != TransferStatus.Success) allSucceeded = false;
                continue; // already attempted — don't double-pay this leg
            }

            var transfer = new Transfer(account.TenantId, merchantRef, Money.FromKobo(leg.AmountKobo),
                leg.Split.BeneficiaryAccountNumber, leg.Split.BeneficiaryBankCode, leg.Split.BeneficiaryName,
                $"Xental split {i + 1}/{legs.Count} for {account.Reference}");
            db.Transfers.Add(transfer);
            await db.SaveChangesAsync(ct); // reserve the leg ref before calling the provider

            var result = await nomba.InitiateTransferAsync(merchantRef, leg.AmountKobo,
                leg.Split.BeneficiaryAccountNumber, leg.Split.BeneficiaryBankCode, transfer.Narration, ct);
            if (result.Success)
            {
                transfer.MarkSucceeded(result.ProviderReference ?? merchantRef, now);
                logger.LogInformation("Split leg {Leg} for {AccountRef}: {Kobo} kobo -> {Account}", i, account.Reference, leg.AmountKobo, leg.Split.BeneficiaryAccountNumber);
            }
            else
            {
                transfer.MarkFailed(result.FailureReason ?? "split settlement failed", now);
                allSucceeded = false;
                logger.LogWarning("Split leg {Leg} for {AccountRef} failed: {Reason}", i, account.Reference, result.FailureReason);
            }
            await db.SaveChangesAsync(ct);
        }

        if (allSucceeded)
        {
            account.MarkSettled(now);
            await db.SaveChangesAsync(ct);
        }
    }
}
