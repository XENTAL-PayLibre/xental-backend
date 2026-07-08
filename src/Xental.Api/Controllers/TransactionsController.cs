using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Payments;
using Xental.Domain.Payments;

namespace Xental.Api.Controllers;

/// <summary>
/// The tenant's deposit ledger / statement (API plane). Filterable audit view of reconciled
/// inflows, each with its reconciliation status, internal reason flag, fees, and risk score.
/// </summary>
[ApiController]
[Route("api/v1/transactions")]
[Authorize(Policy = AuthPolicies.ApiOrDashboard)] // read-only ledger — usable from the dashboard too
[EnableRateLimiting("api-key")]
public sealed class TransactionsController(TransactionQueryService transactions, RefundService refunds) : ControllerBase
{
    /// <summary>List transactions, filtered by date range, status, reconciliation, or accountRef.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TransactionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TransactionResponse>>> List(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] TransactionStatus? status, [FromQuery] ReconciliationStatus? reconciliation,
        [FromQuery] string? accountRef, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var list = await transactions.ListAsync(new TransactionFilter(from, to, status, reconciliation, accountRef), take, ct);
        return Ok(list.Select(ToResponse));
    }

    /// <summary>Pay-ins summary (total / successful / failed) for the dashboard cards, optionally scoped
    /// to a date range. Usable from the dashboard or an API key.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(TransactionSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TransactionSummaryResponse>> Summary(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct = default)
    {
        var s = await transactions.SummaryAsync(from, to, ct);
        return Ok(new TransactionSummaryResponse(
            s.Total, s.TotalPayinsKobo, s.Successful, s.Failed, s.PendingReview, s.SuccessfulKobo, s.NetCreditedKobo));
    }

    /// <summary>Fetch a single transaction by its provider reference.</summary>
    [HttpGet("{reference}")]
    [ProducesResponseType(typeof(TransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TransactionResponse>> Get(string reference, CancellationToken ct)
    {
        var t = await transactions.GetByReferenceAsync(reference, ct);
        return Ok(ToResponse(t));
    }

    /// <summary>Refund an overpayment surplus back to the payer (dashboard Owner/Admin). Sends only the
    /// amount still held for the account; releases any overpayment hold on success. Idempotent per deposit.</summary>
    /// <response code="200">Refund sent (or already sent).</response>
    /// <response code="400">No refundable surplus, nothing available (already settled), or missing destination.</response>
    /// <response code="409">A refund for this deposit is already in progress.</response>
    [HttpPost("{reference}/refund")]
    [Authorize(Policy = AuthPolicies.MovePayouts)] // money-out — API key or dashboard Owner/Admin
    [ProducesResponseType(typeof(RefundResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RefundResponse>> Refund(string reference, RefundOverpaymentRequest? request, CancellationToken ct)
    {
        var dest = request is null ? null : new RefundDestination(request.AccountNumber, request.BankCode, request.AccountName);
        var r = await refunds.RefundOverpaymentAsync(reference, dest, ct);
        return Ok(new RefundResponse(r.Status, r.TransferRef, r.AmountKobo, r.DestinationAccountNumber, r.DestinationBankCode, r.ProviderReference));
    }

    private static TransactionResponse ToResponse(Transaction t) => new(
        t.Id, t.NombaReference, t.VirtualAccountId, t.AmountKobo, t.FeeKobo, t.NetCreditKobo,
        t.Status.ToString(), t.Reconciliation.ToString(), t.Reason?.ToString(), t.RiskScore,
        t.TransferName, t.SenderAccountNumber, t.SenderBankCode, t.OccurredAtUtc, t.ReconciledAtUtc);
}
