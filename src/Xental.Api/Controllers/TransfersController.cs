using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Xental.Api.Authorization;
using Xental.Api.Banking;
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
[Authorize(Policy = AuthPolicies.ApiOrDashboard)] // reads usable from the dashboard; the payout write stays API-only
[EnableRateLimiting("api-key")]
public sealed class TransfersController(TransferService transfers, IMemoryCache cache) : ControllerBase
{
    private const string BanksCacheKey = "transfers:banks";

    /// <summary>List the account's payouts, most recent first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TransferResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TransferResponse>>> List([FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok((await transfers.ListAsync(take, ct)).Select(ToResponse));

    /// <summary>List selectable banks (name + code) for payout/settlement UIs. Pulls the provider's live
    /// list (cached), falling back to a built-in Nigerian bank list if the provider is unavailable.</summary>
    [HttpGet("banks")]
    [ProducesResponseType(typeof(IEnumerable<BankResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<BankResponse>>> Banks(CancellationToken ct)
    {
        if (cache.TryGetValue(BanksCacheKey, out IReadOnlyList<BankResponse>? cached) && cached is not null)
            return Ok(cached);

        var live = await transfers.GetBanksAsync(ct);
        if (live.Count > 0)
        {
            var mapped = live
                .Select(b => new BankResponse(b.Name, b.Code))
                .OrderBy(b => b.Name)
                .ToList();
            cache.Set(BanksCacheKey, (IReadOnlyList<BankResponse>)mapped, TimeSpan.FromHours(12));
            return Ok(mapped);
        }

        // Provider unavailable — serve the built-in list without caching so the next call retries.
        return Ok(NigerianBanks.All);
    }

    /// <summary>Resolve the recipient account name before sending (name check).</summary>
    [HttpPost("bank/lookup")]
    [ProducesResponseType(typeof(BankLookupResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BankLookupResponse>> Lookup(BankLookupRequest request, CancellationToken ct)
    {
        var r = await transfers.LookupAsync(request.AccountNumber, request.BankCode, ct);
        return Ok(new BankLookupResponse(r.AccountName, r.AccountNumber, r.BankCode));
    }

    /// <summary>Initiate a bank transfer (idempotent on merchantTxRef). Callable from an API key or a
    /// dashboard Owner/Admin.</summary>
    /// <response code="201">Transfer created/initiated.</response>
    [HttpPost("bank")]
    [Authorize(Policy = AuthPolicies.MovePayouts)]
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
