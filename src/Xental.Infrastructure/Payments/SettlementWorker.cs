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
            var config = await db.SettlementConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == account.TenantId, ct);
            if (config is null || !config.CanAutoSettle)
                continue; // not opted in / not configured — leave unsettled

            var merchantRef = $"settle-{account.Id:N}";
            var existing = await db.Transfers.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.MerchantTxRef == merchantRef, ct);
            if (existing is not null)
            {
                if (existing.Status == TransferStatus.Success) { account.MarkSettled(now); await db.SaveChangesAsync(ct); }
                continue; // failed ones await manual retry; don't re-initiate
            }

            var net = await db.Transactions.IgnoreQueryFilters()
                .Where(t => t.VirtualAccountId == account.Id)
                .SumAsync(t => (long?)t.NetCreditKobo, ct) ?? 0;
            if (net <= 0 || net < config.MinPayoutKobo)
                continue; // below payout threshold — wait

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
    }
}
