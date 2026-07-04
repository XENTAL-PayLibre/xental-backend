using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Application.Payments;
using Xental.Infrastructure.Nomba;

namespace Xental.Api.Controllers;

/// <summary>
/// Inbound provider webhooks. Nomba posts payment events here; the request is HMAC-verified,
/// deduped by request id, matched to a virtual account, and reconciled. Anonymous (verified
/// by signature, not a token) and exempt from rate limiting.
/// </summary>
[ApiController]
[Route("webhooks")]              // standard, unversioned path: /webhooks/nomba
[Route("api/v1/webhooks")]       // kept for backward compatibility
[AllowAnonymous]
public sealed class WebhooksController(
    NombaWebhookService webhooks,
    IOptions<NombaOptions> nomba,
    IErrorAlerter alerter,
    ILogger<WebhooksController> logger) : ControllerBase
{
    /// <summary>Nomba webhook receiver (HMAC-SHA256 verified).</summary>
    /// <response code="200">Accepted (processed, duplicate, ignored, or unmatched).</response>
    /// <response code="401">Invalid or missing signature.</response>
    [HttpPost("nomba")]
    [DisableRateLimiting]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Nomba(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var rawBody = ms.ToArray();

        var signature = Request.Headers[nomba.Value.WebhookSignatureHeader].ToString();
        var timestamp = Request.Headers["nomba-timestamp"].ToString();

        WebhookResult result;
        try
        {
            result = await webhooks.ProcessAsync(rawBody, signature, timestamp, ct);
        }
        catch (AuthenticationException)
        {
            // A signature failure is either a misconfigured secret (our side) or a forged/malicious
            // post. Either way an operator should know — alert (throttled so a flood collapses to one
            // email), then surface the 401 as before.
            logger.LogWarning("Rejected Nomba webhook with an invalid signature (body {Bytes} bytes).", rawBody.Length);
            await alerter.NotifyOperationalAsync(
                "Webhook signature verification failed",
                "A Nomba webhook was rejected for an invalid HMAC signature. If this repeats, check that NOMBA__WEBHOOKSIGNINGSECRET matches the value configured in the Nomba dashboard; otherwise it may be a forged request.",
                "webhook-signature-failure", ct);
            throw;
        }

        // Always 200 for accepted signatures so Nomba doesn't retry indefinitely; the body
        // reports what happened for observability.
        return Ok(new
        {
            status = result.Status.ToString().ToLowerInvariant(),
            reference = result.Reference,
            reconciliation = result.Reconciliation?.ToString(),
            paymentState = result.PaymentState?.ToString(),
            reason = result.Reason?.ToString(),
        });
    }
}
