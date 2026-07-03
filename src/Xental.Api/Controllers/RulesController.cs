using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Payments;
using Xental.Domain.Payments;

namespace Xental.Api.Controllers;

/// <summary>
/// Money Rules (dashboard plane, Feature 3): declarative if-this-then-that reactions to reconciled
/// deposits (overpaid → hold, high-risk → hold, any → notify). Evaluated post-commit by the
/// reconciliation engine; they never change the verdict. Configuration only — nothing here moves money.
/// </summary>
[ApiController]
[Route("api/v1/rules")]
[Authorize(Policy = AuthPolicies.Dashboard)]
public sealed class RulesController(MoneyRuleService rules) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<RuleResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RuleResponse>>> List(CancellationToken ct) =>
        Ok((await rules.ListAsync(ct)).Select(ToResponse));

    [HttpPost]
    [ProducesResponseType(typeof(RuleResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<RuleResponse>> Create(CreateRuleRequest request, CancellationToken ct)
    {
        var rule = await rules.CreateAsync(
            new RuleSpec(request.Trigger, request.Action, request.ThresholdKobo, request.MinRiskScore, request.Priority), ct);
        return Created($"/api/v1/rules/{rule.Id}", ToResponse(rule));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await rules.DeleteAsync(id, ct);
        return NoContent();
    }

    private static RuleResponse ToResponse(MoneyRule r) => new(
        r.Id, r.Trigger.ToString(), r.Action.ToString(), r.ThresholdKobo, r.MinRiskScore, r.Enabled, r.Priority);
}
