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
public sealed class TransactionsController(TransactionQueryService transactions) : ControllerBase
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

    /// <summary>Fetch a single transaction by its provider reference.</summary>
    [HttpGet("{reference}")]
    [ProducesResponseType(typeof(TransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TransactionResponse>> Get(string reference, CancellationToken ct)
    {
        var t = await transactions.GetByReferenceAsync(reference, ct);
        return Ok(ToResponse(t));
    }

    private static TransactionResponse ToResponse(Transaction t) => new(
        t.Id, t.NombaReference, t.VirtualAccountId, t.AmountKobo, t.FeeKobo, t.NetCreditKobo,
        t.Status.ToString(), t.Reconciliation.ToString(), t.Reason?.ToString(), t.RiskScore,
        t.TransferName, t.OccurredAtUtc, t.ReconciledAtUtc);
}
