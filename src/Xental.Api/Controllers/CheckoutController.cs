using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Common.Interfaces;
using Xental.Application.Payments;

namespace Xental.Api.Controllers;

/// <summary>
/// Live Checkout (differentiator). An integrator (API plane) mints a session token scoped to one
/// virtual account, then hands the payer the snapshot/stream URLs. The snapshot + SSE stream are
/// <b>anonymous</b> — a payer watches "Payment received ✓" land without an account — and expose
/// only payment state, never PII. Strictly read-only against the money path.
/// </summary>
[ApiController]
public sealed class CheckoutController(
    CheckoutService checkout,
    IReconciliationNotifier notifier) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Create a checkout session for one of your virtual accounts (API plane).</summary>
    [HttpPost("/api/v1/checkout/sessions")]
    [Authorize(Policy = AuthPolicies.Api)]
    [ProducesResponseType(typeof(CheckoutSessionResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<CheckoutSessionResponse>> Create(CreateCheckoutSessionRequest request, CancellationToken ct)
    {
        var ttl = request.TtlSeconds is int s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        var (session, account) = await checkout.CreateSessionAsync(request.AccountRef, ttl, ct);
        var response = new CheckoutSessionResponse(
            session.Token,
            $"/api/v1/checkout/{session.Token}",
            $"/api/v1/checkout/{session.Token}/stream",
            session.ExpiresAtUtc,
            ToSnapshot(CheckoutService.Snapshot(account)));
        return Created(response.SnapshotUrl, response);
    }

    /// <summary>Current payment state for a checkout token (anonymous). 404 if unknown/expired.</summary>
    [HttpGet("/api/v1/checkout/{token}")]
    [HttpGet("/checkout/{token}")] // convenience alias for payer-facing links
    [AllowAnonymous]
    [ProducesResponseType(typeof(CheckoutSnapshotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckoutSnapshotResponse>> Snapshot(string token, CancellationToken ct)
    {
        var resolved = await checkout.ResolveAsync(token, ct);
        return resolved is null ? NotFound() : Ok(ToSnapshot(CheckoutService.Snapshot(resolved.Value.Account)));
    }

    /// <summary>
    /// Server-Sent Events stream of reconciliation status for a checkout token (anonymous).
    /// Emits the current snapshot immediately, then a <c>data:</c> event on every status change.
    /// </summary>
    [HttpGet("/api/v1/checkout/{token}/stream")]
    [AllowAnonymous]
    public async Task Stream(string token, CancellationToken ct)
    {
        var resolved = await checkout.ResolveAsync(token, ct);
        if (resolved is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var (_, account) = resolved.Value;

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // don't let a reverse proxy buffer the stream

        // Initial snapshot so a late subscriber sees current state immediately.
        await WriteSseAsync(ToSnapshot(CheckoutService.Snapshot(account)), ct);

        try
        {
            await foreach (var evt in notifier.SubscribeAsync(account.Id, ct))
                await WriteSseAsync(new CheckoutSnapshotResponse(
                    evt.AccountRef, account.AccountNumber, account.BankName, account.AccountName,
                    evt.PaymentState, evt.AmountPaidKobo, evt.ExpectedAmountKobo), ct);
        }
        catch (OperationCanceledException) { /* client disconnected */ }
    }

    private async Task WriteSseAsync(CheckoutSnapshotResponse snapshot, CancellationToken ct)
    {
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(snapshot, Json)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    private static CheckoutSnapshotResponse ToSnapshot(CheckoutSnapshot s) => new(
        s.AccountRef, s.AccountNumber, s.BankName, s.AccountName,
        s.PaymentState, s.AmountPaidKobo, s.ExpectedAmountKobo);
}
