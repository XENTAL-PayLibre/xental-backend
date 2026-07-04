using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xental.Application.Billing;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Billing;

/// <summary>
/// Drives recurring billing off the request path: opens each active schedule's next period when the
/// current one elapses, sends "payment due" reminders for freshly-opened periods, and flags overdue
/// ones (notifying the payer + emitting webhooks). Deposit attribution itself happens inline on the
/// reconciliation path; this worker only handles time-based transitions. Idempotent per tick.
/// </summary>
public sealed class BillingWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BillingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Billing poll failed.");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One dunning pass. Exposed for tests; called on each poll tick.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var billing = scope.ServiceProvider.GetRequiredService<BillingService>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.UtcNow;

        var opened = await billing.OpenDuePeriodsAsync(now, ct);
        // Backstop: attribute any deposits the webhook path missed (e.g. it lost an xmin race with the
        // worker). Idempotent via the water-mark, so caught-up schedules are a no-op.
        await billing.AttributePendingAsync(ct);
        var reminded = await billing.SendDueRemindersAsync(now, ct);
        var overdue = await billing.MarkOverdueAsync(now, ct);

        if (opened + reminded + overdue > 0)
            logger.LogInformation("Billing pass: opened {Opened}, reminded {Reminded}, overdue {Overdue}.", opened, reminded, overdue);
    }
}
