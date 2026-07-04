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

            // Round identity is the *stable* from-boundary (the current water-mark), NOT `net`.
            // `net` grows with every new deposit, so keying the settlement ref on it would re-key an
            // in-flight round after a deposit and re-pay legs that already succeeded. The water-mark
            // only advances when a round completes, so it is a stable key for the life of the round.
            var roundKey = account.SettledUpToKobo;

            if (account.SubMerchantId is Guid subId)
                await SettleToSubMerchantAsync(db, nomba, account, subId, config, roundKey, unsettled, opts, alerter, now, ct);
            else
                await SettleToTenantAsync(db, nomba, account, config, roundKey, unsettled, opts, alerter, now, ct);
        }
    }

    /// <summary>Tenant-owned account: sweep to the tenant's settlement account or split plan.</summary>
    private async Task SettleToTenantAsync(
        IApplicationDbContext db, INombaClient nomba, VirtualAccount account, SettlementConfig? config,
        long roundKey, long unsettled, SettlementOptions opts, IErrorAlerter? alerter, DateTimeOffset now, CancellationToken ct)
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
                config.SettlementAccountName, unsettled, roundKey, "settlement", opts, alerter, now, ct);
        else
            await SettleLegsAsync(db, nomba, account, effective, unsettled, roundKey, opts, alerter, now, ct);
    }

    /// <summary>Sub-merchant-owned account: sweep to the sub-merchant's payout account, skimming the
    /// operator's platform fee first when configured.</summary>
    private async Task SettleToSubMerchantAsync(
        IApplicationDbContext db, INombaClient nomba, VirtualAccount account, Guid subMerchantId, SettlementConfig? config,
        long roundKey, long unsettled, SettlementOptions opts, IErrorAlerter? alerter, DateTimeOffset now, CancellationToken ct)
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
            await SettleLegsAsync(db, nomba, account, new List<SettlementSplit> { subLeg, feeLeg }, unsettled, roundKey, opts, alerter, now, ct);
        }
        else
        {
            if (sub.PlatformFeeBps > 0 && !operatorHasAccount)
                logger.LogWarning("Platform fee for sub-merchant {SubRef} waived — operator has no settlement account configured.", sub.Reference);
            await SettleSingleAsync(db, nomba, account, sub.SettlementAccountNumber!, sub.SettlementBankCode!,
                sub.SettlementAccountName, unsettled, roundKey, $"payout to {sub.Reference}", opts, alerter, now, ct);
        }
    }

    /// <summary>One transfer of <paramref name="amountKobo"/> to a single destination. Idempotent on the
    /// stable round key <c>settle-{id}-{roundKey}</c> (the from-water-mark), so a deposit landing mid-round
    /// never re-keys or re-pays it. On success the water-mark advances by the amount actually sent
    /// (<paramref name="roundKey"/> + transfer amount) — never to a larger, newer <c>net</c>.</summary>
    private async Task SettleSingleAsync(
        IApplicationDbContext db, INombaClient nomba, VirtualAccount account,
        string destAccountNumber, string destBankCode, string? destAccountName,
        long amountKobo, long roundKey, string label, SettlementOptions opts, IErrorAlerter? alerter, DateTimeOffset now, CancellationToken ct)
    {
        var merchantRef = $"settle-{account.Id:N}-{roundKey}";
        var existing = await db.Transfers.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.MerchantTxRef == merchantRef, ct);

        Transfer transfer;
        if (existing is not null)
        {
            // Already paid — advance the water-mark by what was actually sent (not the current net).
            if (existing.Status == TransferStatus.Success) { account.MarkSettledUpTo(roundKey + existing.AmountKobo, now); await db.SaveChangesAsync(ct); return; }
            if (!ShouldRetry(existing, opts, now))
            {
                await AlertIfStalePendingAsync(existing, account, roundKey, opts, alerter, now, ct);
                return; // in-flight, backing off, or retries exhausted — leave for the next tick / a human
            }
            existing.BeginRetry();
            transfer = existing;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Retrying settlement for {AccountRef} (attempt {Attempt}).", account.Reference, existing.RetryCount);
        }
        else
        {
            transfer = new Transfer(account.TenantId, merchantRef, Money.FromKobo(amountKobo),
                destAccountNumber, destBankCode, destAccountName, $"Xental {label} for {account.Reference}");
            db.Transfers.Add(transfer);
            await db.SaveChangesAsync(ct); // reserve the ref before calling the provider
        }

        // Always send the transfer's own amount (stable across retries), never a recomputed newer amount.
        var result = await nomba.InitiateTransferAsync(merchantRef, transfer.AmountKobo, destAccountNumber, destBankCode, destAccountName, transfer.Narration, ct);
        if (result.Success)
        {
            transfer.MarkSucceeded(result.ProviderReference ?? merchantRef, now);
            account.MarkSettledUpTo(roundKey + transfer.AmountKobo, now);
            logger.LogInformation("Settled {AccountRef}: {Kobo} kobo -> {Account} ({Label})", account.Reference, transfer.AmountKobo, destAccountNumber, label);
        }
        else
        {
            transfer.MarkFailed(result.FailureReason ?? "settlement failed", now);
            logger.LogWarning("Settlement failed for {AccountRef}: {Reason}", account.Reference, result.FailureReason);
            // Only alert once auto-retry can no longer recover it — otherwise the next tick will try again.
            if (alerter is not null && RetriesExhausted(transfer, opts))
                await alerter.NotifyOperationalAsync("Settlement failed (retries exhausted)",
                    $"Payout of {transfer.AmountKobo} kobo for {account.Reference} to {destAccountNumber} failed after {transfer.RetryCount} retries: {result.FailureReason}. Held for manual review.",
                    $"settle-fail-{account.Id}-{roundKey}", ct);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Whether a failed transfer is eligible for another auto-attempt now: retries remain and
    /// the (linearly-growing) back-off since the last failure has elapsed.</summary>
    private static bool ShouldRetry(Transfer existing, SettlementOptions opts, DateTimeOffset now)
    {
        if (existing.Status != TransferStatus.Failed) return false;
        if (opts.MaxPayoutRetries <= 0 || existing.RetryCount >= opts.MaxPayoutRetries) return false;
        var waited = existing.CompletedAtUtc is { } last ? now - last : TimeSpan.Zero;
        var backoff = TimeSpan.FromMinutes(Math.Max(1, opts.RetryBackoffMinutes) * (existing.RetryCount + 1));
        return waited >= backoff;
    }

    private static bool RetriesExhausted(Transfer transfer, SettlementOptions opts) =>
        opts.MaxPayoutRetries <= 0 || transfer.RetryCount >= opts.MaxPayoutRetries;

    /// <summary>A transfer stuck in <c>Pending</c> past the grace window likely died in-flight (crash
    /// between reserving the ref and recording the provider result). We don't auto-retry it — the
    /// provider may have actually sent it, so re-initiating could double-pay — but we alert an operator
    /// (throttled) to reconcile it against Nomba by its merchant ref, rather than leaving it silent.</summary>
    private static async Task AlertIfStalePendingAsync(
        Transfer transfer, VirtualAccount account, long roundKey, SettlementOptions opts, IErrorAlerter? alerter, DateTimeOffset now, CancellationToken ct)
    {
        if (alerter is null || transfer.Status != TransferStatus.Pending)
            return;
        var age = now - (transfer.UpdatedAtUtc ?? transfer.CreatedAtUtc);
        if (age < TimeSpan.FromMinutes(Math.Max(1, opts.StalePayoutMinutes)))
            return;
        await alerter.NotifyOperationalAsync("Settlement stuck in PENDING",
            $"Payout {transfer.MerchantTxRef} ({transfer.AmountKobo} kobo) for {account.Reference} has been PENDING for over {opts.StalePayoutMinutes} minutes — it may have died in-flight. Verify at Nomba whether it sent, then resolve it manually (mark failed to retry, or succeeded to advance the water-mark).",
            $"settle-pending-{account.Id}-{roundKey}", ct);
    }

    /// <summary>
    /// Fan <paramref name="amountKobo"/> out across the legs as N idempotent transfers keyed on the stable
    /// round key (<c>settle-{id}-{roundKey}-{i}</c>). The allocator guarantees the legs sum to exactly the
    /// amount; a misconfiguration that exceeds it aborts without moving money. Legs already created keep
    /// their original amount across retries, so a deposit landing mid-round never re-pays a leg. The
    /// water-mark advances only once every leg has succeeded, by the total actually sent.
    /// </summary>
    private async Task SettleLegsAsync(
        IApplicationDbContext db, INombaClient nomba, VirtualAccount account, List<SettlementSplit> splits,
        long amountKobo, long roundKey, SettlementOptions opts, IErrorAlerter? alerter, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<SplitCalculator.Leg> legs;
        try { legs = SplitCalculator.Allocate(amountKobo, splits); }
        catch (DomainException ex)
        {
            logger.LogWarning("Split settlement for {AccountRef} skipped — {Reason}", account.Reference, ex.Message);
            return; // needs the tenant to fix their split plan; never settle partially
        }

        var allSucceeded = true;
        var sentTotal = 0L;
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            if (leg.AmountKobo <= 0)
                continue;

            var merchantRef = $"settle-{account.Id:N}-{roundKey}-{i}";
            var existing = await db.Transfers.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.MerchantTxRef == merchantRef, ct);

            Transfer transfer;
            if (existing is not null)
            {
                if (existing.Status == TransferStatus.Success)
                {
                    sentTotal += existing.AmountKobo; // count already-paid legs toward the round total
                    continue; // leg already paid — don't double-pay
                }
                if (!ShouldRetry(existing, opts, now)) { await AlertIfStalePendingAsync(existing, account, roundKey, opts, alerter, now, ct); allSucceeded = false; continue; }
                existing.BeginRetry();
                transfer = existing;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Retrying split leg {Leg} for {AccountRef} (attempt {Attempt}).", i, account.Reference, existing.RetryCount);
            }
            else
            {
                transfer = new Transfer(account.TenantId, merchantRef, Money.FromKobo(leg.AmountKobo),
                    leg.Split.BeneficiaryAccountNumber, leg.Split.BeneficiaryBankCode, leg.Split.BeneficiaryName,
                    $"Xental split {i + 1}/{legs.Count} for {account.Reference}");
                db.Transfers.Add(transfer);
                await db.SaveChangesAsync(ct); // reserve the leg ref before calling the provider
            }

            // Send the leg transfer's own (stable) amount, never a recomputed one.
            var result = await nomba.InitiateTransferAsync(merchantRef, transfer.AmountKobo,
                leg.Split.BeneficiaryAccountNumber, leg.Split.BeneficiaryBankCode, leg.Split.BeneficiaryName, transfer.Narration, ct);
            if (result.Success)
            {
                transfer.MarkSucceeded(result.ProviderReference ?? merchantRef, now);
                sentTotal += transfer.AmountKobo;
                logger.LogInformation("Split leg {Leg} for {AccountRef}: {Kobo} kobo -> {Account}", i, account.Reference, transfer.AmountKobo, leg.Split.BeneficiaryAccountNumber);
            }
            else
            {
                transfer.MarkFailed(result.FailureReason ?? "split settlement failed", now);
                allSucceeded = false;
                logger.LogWarning("Split leg {Leg} for {AccountRef} failed: {Reason}", i, account.Reference, result.FailureReason);
                if (alerter is not null && RetriesExhausted(transfer, opts))
                    await alerter.NotifyOperationalAsync("Settlement leg failed (retries exhausted)",
                        $"Split leg {i} ({transfer.AmountKobo} kobo) for {account.Reference} to {leg.Split.BeneficiaryAccountNumber} failed after {transfer.RetryCount} retries: {result.FailureReason}.",
                        $"settle-fail-{account.Id}-{roundKey}-{i}", ct);
            }
            await db.SaveChangesAsync(ct);
        }

        if (allSucceeded)
        {
            account.MarkSettledUpTo(roundKey + sentTotal, now);
            await db.SaveChangesAsync(ct);
        }
    }
}
