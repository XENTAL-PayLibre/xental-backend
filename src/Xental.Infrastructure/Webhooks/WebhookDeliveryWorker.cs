using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Webhooks;

namespace Xental.Infrastructure.Webhooks;

/// <summary>
/// Delivers queued outbound webhook events at-least-once: signs each payload with the
/// endpoint's secret (HMAC-SHA256 → <c>x-xental-signature</c>), re-checks the URL for SSRF just
/// before sending, POSTs it, and on failure reschedules with exponential backoff up to the cap
/// (then dead-letters). Runs on a scoped DbContext per poll.
/// </summary>
public sealed class WebhookDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpFactory,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    private const int MaxAttempts = 8;
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeliverDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Webhook delivery poll failed.");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DeliverDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var urlGuard = scope.ServiceProvider.GetRequiredService<IOutboundUrlGuard>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.UtcNow;

        var due = await db.WebhookDeliveries
            .IgnoreQueryFilters()
            .Where(d => (d.Status == WebhookDeliveryStatus.Pending || d.Status == WebhookDeliveryStatus.Failed)
                        && d.NextAttemptAtUtc != null && d.NextAttemptAtUtc <= now)
            .OrderBy(d => d.NextAttemptAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0)
            return;

        var endpointIds = due.Select(d => d.EndpointId).Distinct().ToList();
        var endpoints = await db.WebhookEndpoints.IgnoreQueryFilters()
            .Where(e => endpointIds.Contains(e.Id)).ToListAsync(ct);
        var byId = endpoints.ToDictionary(e => e.Id);

        var http = httpFactory.CreateClient("outbound-webhook");

        foreach (var delivery in due)
        {
            if (!byId.TryGetValue(delivery.EndpointId, out var endpoint) || !endpoint.Active)
            {
                delivery.RecordFailure("endpoint removed", null, clock.UtcNow, MaxAttempts);
                continue;
            }

            if (!await urlGuard.IsSafeAsync(endpoint.Url, ct))
            {
                delivery.RecordFailure("url failed SSRF safety check", null, clock.UtcNow, MaxAttempts);
                continue;
            }

            try
            {
                var secret = protector.Unprotect(endpoint.SecretEncrypted);
                var bytes = Encoding.UTF8.GetBytes(delivery.PayloadJson);
                var signature = Convert.ToHexString(
                    new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(bytes)).ToLowerInvariant();

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
                {
                    Content = new ByteArrayContent(bytes),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.TryAddWithoutValidation("x-xental-signature", signature);
                request.Headers.TryAddWithoutValidation("x-xental-event", delivery.EventType);
                request.Headers.TryAddWithoutValidation("x-xental-delivery-id", delivery.Id.ToString());
                request.Headers.TryAddWithoutValidation("x-xental-event-id", delivery.EventId);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await http.SendAsync(request, cts.Token);

                if (response.IsSuccessStatusCode)
                    delivery.MarkDelivered(clock.UtcNow, (int)response.StatusCode);
                else
                    delivery.RecordFailure($"HTTP {(int)response.StatusCode}", (int)response.StatusCode, clock.UtcNow, MaxAttempts);
            }
            catch (Exception ex)
            {
                delivery.RecordFailure(ex.Message, null, clock.UtcNow, MaxAttempts);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
