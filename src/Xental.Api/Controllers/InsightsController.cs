using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Payments;

namespace Xental.Api.Controllers;

/// <summary>Reconciliation + collections analytics for the account dashboard (dashboard plane).</summary>
[ApiController]
[Route("api/v1/insights")]
[Authorize(Policy = AuthPolicies.Dashboard)]
public sealed class InsightsController(InsightsService insights) : ControllerBase
{
    /// <summary>Headline metrics: collection rate, outstanding deficit, reconciliation breakdown, risk.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(InsightsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<InsightsResponse>> Get(CancellationToken ct)
    {
        var s = await insights.GetAsync(ct);
        return Ok(new InsightsResponse(
            s.VirtualAccounts, s.Deposits, s.TotalCollectedKobo, s.ExpectedKobo, s.OutstandingDeficitKobo,
            s.CollectionRatePct, s.Reconciled, s.Underpaid, s.Overpaid, s.PendingReview, s.HighRisk,
            s.FullyPaidAccounts, s.PartiallyPaidAccounts));
    }
}
