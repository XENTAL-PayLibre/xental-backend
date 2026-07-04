using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Merchants;
using Xental.Domain.Merchants;

namespace Xental.Api.Controllers;

/// <summary>
/// Sub-merchants (the API plane). Requires an <c>api</c>-scoped token (from
/// <c>/auth/token</c>). Sub-merchants are internal Xental records used to segment a
/// developer's own customers/branches — they are not created on any external provider.
/// </summary>
[ApiController]
[Route("api/v1/sub-merchants")]
[Authorize(Policy = AuthPolicies.ApiOrDashboard)] // manage sub-merchants from an API key or the dashboard
public sealed class SubMerchantsController(SubMerchantService subMerchants) : ControllerBase
{
    /// <summary>Create a sub-merchant under the current account.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SubMerchantResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SubMerchantResponse>> Create(CreateSubMerchantRequest request, CancellationToken ct)
    {
        var sub = await subMerchants.CreateAsync(request.Name, request.Reference, ct);
        return Created($"/api/v1/sub-merchants/{sub.Id}", ToResponse(sub));
    }

    /// <summary>List the current tenant's sub-merchants.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<SubMerchantResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SubMerchantResponse>>> List(CancellationToken ct)
    {
        var subs = await subMerchants.ListAsync(ct);
        return Ok(subs.Select(ToResponse));
    }

    /// <summary>Set the sub-merchant's payout account + platform fee. The account is bank-verified (NUBAN
    /// name-match) and the settlement worker routes this sub-merchant's collections here.</summary>
    /// <response code="400">Payout account could not be verified.</response>
    [HttpPut("{id:guid}/payout")]
    [ProducesResponseType(typeof(SubMerchantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubMerchantResponse>> SetPayout(Guid id, SetSubMerchantPayoutRequest request, CancellationToken ct)
    {
        var sub = await subMerchants.SetPayoutAsync(id, request.BankName, request.BankCode, request.AccountNumber, request.PlatformFeeBps, ct);
        return Ok(ToResponse(sub));
    }

    /// <summary>Collected / settled / pending balance for a sub-merchant (net kobo).</summary>
    [HttpGet("{id:guid}/balance")]
    [ProducesResponseType(typeof(SubMerchantBalanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubMerchantBalanceResponse>> Balance(Guid id, CancellationToken ct)
    {
        var b = await subMerchants.GetBalanceAsync(id, ct);
        return Ok(new SubMerchantBalanceResponse(b.SubMerchantId, b.Reference, b.CollectedKobo, b.SettledKobo, b.PendingKobo, b.VirtualAccounts));
    }

    private static SubMerchantResponse ToResponse(SubMerchant s) =>
        new(s.Id, s.Name, s.Reference, s.Status.ToString(), s.HasPayoutAccount,
            s.SettlementBankName, s.SettlementBankCode, s.SettlementAccountNumber, s.SettlementAccountName,
            s.PlatformFeeBps, s.CreatedAtUtc);
}
