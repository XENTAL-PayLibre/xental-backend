using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Application.Payments;

namespace Xental.Api.Controllers;

/// <summary>
/// Sandbox tools (agent layer). Lets a developer or their AI agent verify an integration
/// end-to-end with zero money: simulate a bank transfer into a virtual account and watch it
/// reconcile through the real engine (splits + money rules included). Test mode only — callable
/// from a test API key or the dashboard (without the live X-Xental-Mode header).
/// </summary>
[ApiController]
[Route("api/v1/sandbox")]
[Authorize(Policy = AuthPolicies.Provision)]
public sealed class SandboxController(SandboxSimulationService sim, IModeContext mode) : ControllerBase
{
    /// <summary>Simulate a deposit into one of your virtual accounts (test mode only).</summary>
    [HttpPost("simulate/deposit")]
    [ProducesResponseType(typeof(SimulatedDepositResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SimulatedDepositResponse>> SimulateDeposit(SimulateDepositRequest request, CancellationToken ct)
    {
        // Strictly zero real money: the simulator is only for test mode (never a live key/session).
        if (await mode.IsLiveAsync(ct))
            throw new ForbiddenException("The sandbox simulator is only available in test mode.");

        var result = await sim.SimulateDepositAsync(request.AccountRef, request.AmountKobo, request.SenderName, request.Reversal ?? false, ct);
        return Ok(new SimulatedDepositResponse(
            result.Status.ToString(), result.Reference, result.Reconciliation?.ToString(), result.PaymentState?.ToString(), result.Reason?.ToString()));
    }
}
