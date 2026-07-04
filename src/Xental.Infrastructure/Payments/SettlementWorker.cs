using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Common;
using Xental.Domain.Merchants;
using Xental.Domain.Payments;

namespace Xental.Infrastructure.Payments;

/// <summary>
/// Closes the loop: sweeps the net collected amount (deposits less provider fees) out of virtual
/// accounts to wherever they should settle — the tenant's settlement account, a split plan, or a
/// sub-merchant's payout account (minus the operator's platform fee). Fixed-amount accounts settle
/// once fully paid; reusable/open accounts settle incrementally against a watermark. Idempotent per
/// settlement round (<c>settle-{id}-{net}</c>), threshold-aware, and off the webhook hot path.
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
        var opts = scope.ServiceProvider.GetService<IOptions<SettlementOptions>>()?.Value ?? new SettlementOptions();
        var alerter = scope.ServiceProvider.GetService<IErrorAlerter>();
        var now = clock.UtcNow;

        // Master kill switch: pause all real payouts without a redeploy.
        if (!opts.PayoutsEnabled)
            return;

        // Candidates: active accounts with gross beyond the settled water-mark. Fixed-amount accounts
        // only once complete; open/reusable accounts whenever they have accrued something new.
        var due = await db.VirtualAccounts.IgnoreQueryFilters()
            .Where(v => v.Status == VirtualAccountStatus.Active && v.AmountPaidKobo > v.SettledUpToKobo
                && (v.ExpectedAmountKobo == null
                    || v.PaymentState == PaymentState.FullyPaid
                    || v.PaymentState == PaymentState.Overpaid))
            .Take(BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0)
            return;

        foreach (var account in due)
        {
            // Simulated (test-mode) NUBANs are credited by the sandbox simulator but hold no real
            // money — never initiate a real payout for them.
            if (account.ProviderAccountId is not null && account.ProviderAccountId.StartsWith("sandbox-", StringComparison.Ordinal))
                continue;

            // Escrow (Feature 1): an active hold parks the funds — skip until it is released.
            var held = await db.EscrowHolds.IgnoreQueryFilters()
                .AnyAsync(e => e.VirtualAccountId == account.Id && e.State == EscrowState.Held, ct);
            if (held)
                continue;

            // Net collected so far = credited inflows minus reversed ones. A reversal stores a positive
            // NetCreditKobo (Reconciliation=Reversed), so it must be SUBTRACTED, not summed in.
            var credited = await db.Transactions.IgnoreQueryFilters()
                .Where(t => t.VirtualAccountId == account.Id && t.Reconciliation != ReconciliationStatus.Reversed)
                .SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0;
            var reversed = await db.Transactions.IgnoreQueryFilters()
                .Where(t => t.VirtualAccountId == account.Id && t.Reconciliation == ReconciliationStatus.Reversed)
                .SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0;
            var net = credited - reversed;
            var unsettled = account.UnsettledKobo(net);
            if (unsettled <= 0)
                continue;

            // Payout cap: never auto-move more than the configured ceiling in one round — hold + alert.
            if (opts.MaxPayoutKobo > 0 && unsettled > opts.MaxPayoutKobo)
            {
                logger.LogWarning("Settlement of {Kobo} kobo for {AccountRef} exceeds the payout cap ({Cap}) — held for manual review.",
                    unsettled, account.Reference, opts.MaxPayoutKobo);
                if (alerter is not null)
                    await alerter.NotifyOperationalAsync("Settlement held: over payout cap",
                        $"Account {account.Reference} has {unsettled} kobo to settle, above the {opts.MaxPayoutKobo} kobo cap. Held for manual review.",
                        $"cap-{account.Id}", ct);
                continue;
            }

            var config = await db.SettlementConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == account.TenantId, ct);

            if (account.SubMerchantId is Guid subId)
                await SettleToSubMerchantAsync(db, nomba, account, subId, config, net, unsettled, alerter, now, ct);
            else
                await SettleToTenantAsync(db, nomba, account, config, net, unsettled, alerter, now, ct);
        }
    }

    /// <summary>Tenant-owned account: sweep to the tenant's settlement account or split plan.</summary>
    private async Task SettleToTenantAsync(
        IApplicationDbContext db, INombaClient nomba, VirtualAccount account, SettlementConfig? config,
        long net, long unsettled, IErrorAlerter? alerter, DateTimeOffset now, CancellationToken ct)
    {
        if (config is null || !config.CanAutoSettle)
            return; // not opted in / not configured — leave unsettled
        if (unsettled < config.MinPayoutKobo)
            return; // below payout threshold — wait

        // Split settlement (Feature 1): account-specific legs override tenant-wide legs.
        var splits = await db.SettlementSplits.IgnoreQueryFilters()
            .Where(s => s.TenantId == account.TenantId && s.Enabled
                && (s.VirtualAccountId == null || s.VirtualAccountId == account.Id))
            .ToListAsync(ct);
        var effective = splits.Where(s => s.VirtualAccountId == account.Id).ToList();
        if (effective.Count == 0)
            effective = splits.Where(s => s.VirtualAccountId == null).ToList();

        if (effective.Count == 0)
            await SettleSingleAsync(db, nomba, account, config.SettlementAccountNumber!, config.SettlementBankCode!,
                config.SettlementAccountName, unsettled, net, "settlement", alerter, now, ct);
        else
            await SettleLegsAsync(db, nomba, account, effective, unsettled, net, alerter, now, ct);
    }

    /// <summary>Sub-merchant-owned account: sweep to the sub-merchant's payout account, skimming the
    /// operator's platform fee first when configured.</summary>
    private async Task SettleToSubMerchantAsync(
        IApplicationDbContext db, INombaClient nomba, VirtualAccount account, Guid subMerchantId, SettlementConfig? config,
        long net, long unsettled, IErrorAlerter? alerter, DateTimeOffset now, CancellationToken ct)
    {
        var sub = await db.SubMerchants.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == subMerchantId, ct);
        if (sub is null || !sub.HasPayoutAccount)
        {
            logger.LogWarning("Account {AccountRef} routed to sub-merchant {SubId} but it has no payout account — leaving unsettled.",
                account.Reference, subMerchantId);
            return;
        }

        var minPayout = config?.MinPayoutKobo ?? 0;
        if (unsettled < minPayout)
            return;

        var operatorHasAccount = config is not null
            && !string.IsNullOrWhiteSpace(config.SettlementAccountNumber)
            && !string.IsNullOrWhiteSpace(config.SettlementBankCode);

        if (sub.PlatformFeeBps > 0 && operatorHasAccount)
        {
            // Two legs: the sub-merchant takes the remainder (priority 0, absorbs rounding dust), the
            // operator takes the fee (priority 1). Reuse the tested exact-sum allocator.
            var subLeg = new SettlementSplit(account.TenantId, account.Id,
                sub.SettlementAccountName ?? sub.Name, sub.SettlementAccountNumber!, sub.SettlementBankCode!,
                SplitBasis.Percentage, 10000 - sub.PlatformFeeBps, 0, priority: 0);
            var feeLeg = new SettlementSplit(account.TenantId, account.Id,
                config!.SettlementAccountName ?? "Platform fee", config.SettlementAccountNumber!, config.SettlementBankCode!,
                SplitBasis.Percentage, sub.PlatformFeeBps, 0, priority: 1);
            await SettleLegsAsync(db, nomba, account, new List<SettlementSplit> { subLeg, feeLeg }, unsettled, net, alerter, now, ct);
        }
        else
        {
            if (sub.PlatformFeeBps > 0 && !operatorHasAccount)
                logger.LogWarning("Platform fee for sub-merchant {SubRef} waived — operator has no settlement account configured.", sub.Reference);
            await SettleSingleAsync(db, nomba, account, sub.SettlementAccountNumber!, sub.SettlementBankCode!,
                sub.SettlementAccountName, unsettled, net, $"payout to {sub.Reference}", alerter, now, ct);
        }
    }

    /// <summary>One transfer of <paramref name="amountKobo"/> to a single destination. Idempotent on
    /// <c>settle-{id}-{batchKey}</c>; advances the account's water-mark to <paramref name="batchKey"/> on success.</summary>
    private async Task SettleSingleAsync(
        IApplicationDbContext db, INombaClient nomba, VirtualAccount account,
        string destAccountNumber, string destBankCode, string? destAccountName,
        long amountKobo, long batchKey, string label, IErrorAlerter? alerter, DateTimeOffset now, CancellationToken ct)
    {
        var merchantRef = $"settle-{account.Id:N}-{batchKey}";
        var existing = await db.Transfers.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.MerchantTxRef == merchantRef, ct);
        if (existing is not null)
        {
            if (existing.Status == TransferStatus.Success) { account.MarkSettledUpTo(batchKey, now); await db.SaveChangesAsync(ct); }
            return; // failed ones await manual retry; don't re-initiate
        }

        var transfer = new Transfer(account.TenantId, merchantRef, Money.FromKobo(amountKobo),
            destAccountNumber, destBankCode, destAccountName, $"Xental {label} for {account.Reference}");
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(ct); // reserve the ref before calling the provider

        var result = await nomba.InitiateTransferAsync(merchantRef, amountKobo, destAccountNumber, destBankCode, transfer.Narration, ct);
        if (result.Success)
        {
            transfer.MarkSucceeded(result.ProviderReference ?? merchantRef, now);
            account.MarkSettledUpTo(batchKey, now);
            logger.LogInformation("Settled {AccountRef}: {Kobo} kobo -> {Account} ({Label})", account.Reference, amountKobo, destAccountNumber, label);
        }
        else
        {
            transfer.MarkFailed(result.FailureReason ?? "settlement failed", now);
            logger.LogWarning("Settlement failed for {AccountRef}: {Reason}", account.Reference, result.FailureReason);
            if (alerter is not null)
                await alerter.NotifyOperationalAsync("Settlement failed",
                    $"Payout of {amountKobo} kobo for {account.Reference} to {destAccountNumber} failed: {result.FailureReason}. It will not auto-retry.",
                    $"settle-fail-{account.Id}-{batchKey}", ct);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fan <paramref name="amountKobo"/> out across the legs as N idempotent transfers
    /// (<c>settle-{id}-{batchKey}-{i}</c>). The allocator guarantees the legs sum to exactly the amount;
    /// a misconfiguration that exceeds it aborts without moving money. The water-mark advances only once
    /// every leg has succeeded.
    /// </summary>
    private async Task SettleLegsAsync(
        IApplicationDbContext db, INombaClient nomba, VirtualAccount account, List<SettlementSplit> splits,
        long amountKobo, long batchKey, IErrorAlerter? alerter, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<SplitCalculator.Leg> legs;
        try { legs = SplitCalculator.Allocate(amountKobo, splits); }
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

            var merchantRef = $"settle-{account.Id:N}-{batchKey}-{i}";
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
                if (alerter is not null)
                    await alerter.NotifyOperationalAsync("Settlement leg failed",
                        $"Split leg {i} ({leg.AmountKobo} kobo) for {account.Reference} to {leg.Split.BeneficiaryAccountNumber} failed: {result.FailureReason}.",
                        $"settle-fail-{account.Id}-{batchKey}-{i}", ct);
            }
            await db.SaveChangesAsync(ct);
        }

        if (allSucceeded)
        {
            account.MarkSettledUpTo(batchKey, now);
            await db.SaveChangesAsync(ct);
        }
    }
}
