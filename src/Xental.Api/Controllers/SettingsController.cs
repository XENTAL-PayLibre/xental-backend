using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Payments;
using Xental.Domain.Payments;

namespace Xental.Api.Controllers;

/// <summary>
/// Tenant settlement preferences (dashboard plane). When auto-settle is on and a bank account is
/// configured, fully-paid virtual accounts are swept to that account by the settlement worker.
/// </summary>
[ApiController]
[Route("api/v1/settings/settlement")]
[Authorize(Policy = AuthPolicies.Dashboard)]
public sealed class SettingsController(SettlementConfigService settlement) : ControllerBase
{
    /// <summary>Current settlement configuration for the account.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(SettlementConfigResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SettlementConfigResponse>> Get(CancellationToken ct)
        => Ok(ToResponse(await settlement.GetAsync(ct)));

    /// <summary>Set the settlement bank account and auto-settle threshold.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(SettlementConfigResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SettlementConfigResponse>> Update(UpdateSettlementRequest request, CancellationToken ct)
    {
        var config = await settlement.UpdateAsync(
            request.SettlementAccountNumber, request.SettlementBankCode, request.SettlementAccountName,
            request.AutoSettle, request.MinPayoutKobo, ct);
        return Ok(ToResponse(config));
    }

    private static SettlementConfigResponse ToResponse(SettlementConfig c) => new(
        c.SettlementAccountNumber, c.SettlementBankCode, c.SettlementAccountName,
        c.AutoSettle, c.MinPayoutKobo, c.CanAutoSettle);
}
