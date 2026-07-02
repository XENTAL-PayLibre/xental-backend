using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Webhooks;
using Xental.Domain.Webhooks;

namespace Xental.Api.Controllers;

/// <summary>
/// Manage outbound webhook endpoints (dashboard plane). Xental delivers enriched, HMAC-signed,
/// pre-reconciled events to these URLs with retries + dead-letter. URLs are SSRF-guarded; the
/// per-endpoint signing secret is shown once and stored encrypted.
/// </summary>
[ApiController]
[Route("api/v1/webhook-endpoints")]
[Authorize(Policy = AuthPolicies.Dashboard)]
public sealed class WebhookEndpointsController(WebhookEndpointService endpoints) : ControllerBase
{
    /// <summary>Register a callback URL. Returns the signing secret once.</summary>
    /// <response code="201">Registered; body carries the one-time signing secret.</response>
    /// <response code="400">URL is not a public HTTPS endpoint.</response>
    [HttpPost]
    [ProducesResponseType(typeof(WebhookEndpointCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WebhookEndpointCreatedResponse>> Create(CreateWebhookEndpointRequest request, CancellationToken ct)
    {
        var e = await endpoints.RegisterAsync(request.Url, ct);
        return Created($"/api/v1/webhook-endpoints/{e.Id}", new WebhookEndpointCreatedResponse(e.Id, e.Url, e.SigningSecret));
    }

    /// <summary>List the account's active webhook endpoints (secrets not returned).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<WebhookEndpointResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<WebhookEndpointResponse>>> List(CancellationToken ct)
    {
        var list = await endpoints.ListAsync(ct);
        return Ok(list.Select(e => new WebhookEndpointResponse(e.Id, e.Url, e.Active, e.CreatedAtUtc)));
    }

    /// <summary>Remove (deactivate) an endpoint.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await endpoints.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Recent delivery attempts (observability), optionally filtered by status.</summary>
    [HttpGet("deliveries")]
    [ProducesResponseType(typeof(IEnumerable<WebhookDeliveryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<WebhookDeliveryResponse>>> Deliveries(
        [FromQuery] WebhookDeliveryStatus? status, CancellationToken ct)
    {
        var list = await endpoints.ListDeliveriesAsync(status, ct);
        return Ok(list.Select(d => new WebhookDeliveryResponse(
            d.Id, d.EndpointId, d.EventType, d.Status.ToString(), d.Attempts,
            d.NextAttemptAtUtc, d.DeliveredAtUtc, d.LastStatusCode, d.LastError, d.CreatedAtUtc)));
    }

    /// <summary>Requeue a failed/dead-lettered delivery for immediate retry.</summary>
    [HttpPost("deliveries/{id:guid}/replay")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Replay(Guid id, CancellationToken ct)
    {
        await endpoints.ReplayAsync(id, ct);
        return Accepted();
    }
}
