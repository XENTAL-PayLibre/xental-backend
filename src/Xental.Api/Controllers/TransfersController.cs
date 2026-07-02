using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Payments;
using Xental.Domain.Payments;

namespace Xental.Api.Controllers;

/// <summary>
/// Outbound bank transfers / settlement payouts (API plane). Transfers are idempotent on
/// <c>merchantTxRef</c> — re-submitting the same ref returns the existing transfer.
/// </summary>
[ApiController]
[Route("api/v1/transfers")]
[Authorize(Policy = AuthPolicies.Api)]
public sealed class TransfersController(TransferService transfers) : ControllerBase
{
    /// <summary>Resolve the recipient account name before sending (name check).</summary>
    [HttpPost("bank/lookup")]
    [ProducesResponseType(typeof(BankLookupResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankLookupResponse>> Lookup(BankLookupRequest request, CancellationToken ct)
    {
        var r = await transfers.LookupAsync(request.AccountNumber, request.BankCode, ct);
        return Ok(new BankLookupResponse(r.AccountName, r.AccountNumber, r.BankCode));
    }

    /// <summary>Initiate a bank transfer (idempotent on merchantTxRef).</summary>
    /// <response code="201">Transfer created/initiated.</response>
    [HttpPost("bank")]
    [ProducesResponseType(typeof(TransferResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<TransferResponse>> Create(CreateTransferRequest request, CancellationToken ct)
    {
        var t = await transfers.InitiateAsync(
            request.MerchantTxRef, request.AmountKobo, request.AccountNumber, request.BankCode, request.Narration, ct);
        return Created($"/api/v1/transfers/{t.MerchantTxRef}", ToResponse(t));
    }

    /// <summary>Get a transfer by its merchantTxRef.</summary>
    [HttpGet("{merchantTxRef}")]
    [ProducesResponseType(typeof(TransferResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TransferResponse>> Get(string merchantTxRef, CancellationToken ct)
    {
        var t = await transfers.GetAsync(merchantTxRef, ct);
        return Ok(ToResponse(t));
    }

    private static TransferResponse ToResponse(Transfer t) => new(
        t.Id, t.MerchantTxRef, t.AmountKobo, t.RecipientAccountNumber, t.RecipientBankCode,
        t.Status.ToString(), t.ProviderReference, t.FailureReason, t.CreatedAtUtc, t.CompletedAtUtc);
}
