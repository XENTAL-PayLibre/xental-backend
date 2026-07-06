using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Payments;
using Xental.Domain.Payments;

namespace Xental.Api.Controllers;

/// <summary>
/// Split-settlement plan (dashboard plane, Feature 1). When legs are configured, the settlement
/// worker fans a fully-paid account's net out across them instead of a single sweep. An empty list
/// clears the plan (back to single sweep). Pure configuration — nothing here moves money.
/// </summary>
[ApiController]
[Route("api/v1/settings/splits")]
[Authorize(Policy = AuthPolicies.ManageSettings)]
public sealed class SettlementSplitsController(SplitSettlementService splits) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<SplitLegResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SplitLegResponse>>> Get(CancellationToken ct) =>
        Ok((await splits.GetSplitsAsync(ct)).Select(ToResponse));

    [HttpPut]
    [ProducesResponseType(typeof(IEnumerable<SplitLegResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SplitLegResponse>>> Set(SetSplitsRequest request, CancellationToken ct)
    {
        var specs = request.Splits.Select(s =>
            new SplitSpec(s.BeneficiaryName, s.AccountNumber, s.BankCode, s.Basis, s.ShareBps, s.FlatKobo, s.Priority));
        var result = await splits.SetSplitsAsync(specs, ct);
        return Ok(result.Select(ToResponse));
    }

    private static SplitLegResponse ToResponse(SettlementSplit s) => new(
        s.Id, s.BeneficiaryName, s.BeneficiaryAccountNumber, s.BeneficiaryBankCode,
        s.Basis.ToString(), s.ShareBps, s.FlatKobo, s.Priority, s.Enabled);
}

/// <summary>
/// Escrow control over a virtual account's settlement (API plane, Feature 1). Holding parks the
/// funds so the worker will not sweep/split them; releasing lets the next sweep proceed.
/// </summary>
[ApiController]
[Route("api/v1/settlements")]
[Authorize(Policy = AuthPolicies.MovePayouts)] // escrow control — API key or dashboard Owner/Admin
public sealed class SettlementsController(SplitSettlementService splits) : ControllerBase
{
    /// <summary>Place an escrow hold on an account so it is not settled until released.</summary>
    [HttpPost("{accountRef}/hold")]
    [ProducesResponseType(typeof(EscrowHoldResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EscrowHoldResponse>> Hold(string accountRef, EscrowHoldRequest request, CancellationToken ct)
    {
        var hold = await splits.HoldAsync(accountRef, request?.ReleaseCondition, ct);
        return Ok(new EscrowHoldResponse(hold.Id, accountRef, hold.AmountKobo, hold.State.ToString(), hold.ReleaseCondition, hold.CreatedAtUtc));
    }

    /// <summary>Release the active escrow hold so the account can settle on the next sweep.</summary>
    [HttpPost("{accountRef}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Release(string accountRef, CancellationToken ct)
    {
        await splits.ReleaseAsync(accountRef, ct);
        return NoContent();
    }
}
