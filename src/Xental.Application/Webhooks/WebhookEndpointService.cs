using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Webhooks;

namespace Xental.Application.Webhooks;

public sealed record RegisteredWebhookEndpoint(Guid Id, string Url, string SigningSecret);

/// <summary>
/// Manages a developer's outbound webhook endpoints. URLs are SSRF-guarded; a signing secret
/// is generated and shown once, stored encrypted, and used to sign every delivery. All ops are
/// tenant-scoped by the DbContext query filter.
/// </summary>
public sealed class WebhookEndpointService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    IOutboundUrlGuard urlGuard,
    ISecretProtector protector,
    ITokenGenerator tokens,
    IClock clock)
{
    public async Task<RegisteredWebhookEndpoint> RegisterAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ValidationException("Webhook url is required.");
        await urlGuard.EnsureSafeAsync(url, ct); // throws ValidationException if unsafe

        var tenantId = tenantContext.RequireTenantId();
        var secret = tokens.Generate("whsec", 32);
        var endpoint = new WebhookEndpoint(tenantId, url.Trim(), protector.Protect(secret));
        db.WebhookEndpoints.Add(endpoint);
        await db.SaveChangesAsync(ct);
        return new RegisteredWebhookEndpoint(endpoint.Id, endpoint.Url, secret);
    }

    public async Task<IReadOnlyList<WebhookEndpoint>> ListAsync(CancellationToken ct = default) =>
        await db.WebhookEndpoints.AsNoTracking().Where(e => e.Active)
            .OrderByDescending(e => e.CreatedAtUtc).ToListAsync(ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new NotFoundException("Webhook endpoint not found.");
        endpoint.Deactivate();
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(WebhookDeliveryStatus? status, CancellationToken ct = default)
    {
        var q = db.WebhookDeliveries.AsNoTracking().AsQueryable();
        if (status is { } s) q = q.Where(d => d.Status == s);
        return await q.OrderByDescending(d => d.CreatedAtUtc).Take(200).ToListAsync(ct);
    }

    /// <summary>Requeue a failed/dead-lettered delivery for immediate retry.</summary>
    public async Task ReplayAsync(Guid deliveryId, CancellationToken ct = default)
    {
        var delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct)
            ?? throw new NotFoundException("Delivery not found.");
        delivery.Replay(clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }
}
