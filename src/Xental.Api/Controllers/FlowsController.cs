using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Payments;
using Xental.Domain.Payments;

namespace Xental.Api.Controllers;

/// <summary>
/// Programmable Payment Flows (dashboard plane, differentiator): multi-step automation on reconciled
/// deposits — trigger + conditions → an ordered list of actions (hold, release, notify, review-flag).
/// The <see cref="FlowEngine"/> runs them post-commit, so a flow can never change the payment verdict.
/// Every run is recorded (see <c>/flows/runs</c>). Configuration only — nothing here moves money.
/// </summary>
[ApiController]
[Route("api/v1/flows")]
[Authorize(Policy = AuthPolicies.ManageSettings)]
public sealed class FlowsController(FlowService flows) : ControllerBase
{
    /// <summary>List the tenant's flows (with their ordered actions).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<FlowResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<FlowResponse>>> List(CancellationToken ct) =>
        Ok((await flows.ListAsync(ct)).Select(ToResponse));

    /// <summary>The most recent flow-run audit entries.</summary>
    [HttpGet("runs")]
    [ProducesResponseType(typeof(IEnumerable<FlowRunResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<FlowRunResponse>>> Runs([FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok((await flows.RunsAsync(take, ct)).Select(ToRunResponse));

    /// <summary>Get a single flow by id.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(FlowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FlowResponse>> Get(Guid id, CancellationToken ct) =>
        Ok(ToResponse(await flows.GetAsync(id, ct)));

    /// <summary>Create a flow.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(FlowResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FlowResponse>> Create(CreateFlowRequest request, CancellationToken ct)
    {
        var flow = await flows.CreateAsync(
            new FlowSpec(request.Name, request.Trigger, request.Actions, request.MinAmountKobo, request.MinRiskScore, request.Priority), ct);
        return Created($"/api/v1/flows/{flow.Id}", ToResponse(flow));
    }

    /// <summary>Replace a flow's configuration.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(FlowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FlowResponse>> Update(Guid id, CreateFlowRequest request, CancellationToken ct)
    {
        var flow = await flows.UpdateAsync(id,
            new FlowSpec(request.Name, request.Trigger, request.Actions, request.MinAmountKobo, request.MinRiskScore, request.Priority), ct);
        return Ok(ToResponse(flow));
    }

    /// <summary>Enable or disable a flow.</summary>
    [HttpPost("{id:guid}/enabled")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetEnabled(Guid id, SetFlowEnabledRequest request, CancellationToken ct)
    {
        await flows.SetEnabledAsync(id, request.Enabled, ct);
        return NoContent();
    }

    /// <summary>Delete a flow.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await flows.DeleteAsync(id, ct);
        return NoContent();
    }

    private static FlowResponse ToResponse(Flow f) => new(
        f.Id, f.Name, f.Trigger.ToString(),
        f.Actions.OrderBy(a => a.Order).Select(a => a.Type.ToString()).ToList(),
        f.MinAmountKobo, f.MinRiskScore, f.Enabled, f.Priority, f.CreatedAtUtc);

    private static FlowRunResponse ToRunResponse(FlowRun r) => new(
        r.Id, r.FlowId, r.FlowName, r.Trigger, r.AccountRef, r.TransactionRef, r.Outcome, r.CreatedAtUtc);
}
