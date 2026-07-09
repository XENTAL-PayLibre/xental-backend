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

    /// <summary>Outstanding receivables bucketed by how long they've been outstanding.</summary>
    [HttpGet("aging")]
    [ProducesResponseType(typeof(AgingReportResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AgingReportResponse>> Aging(CancellationToken ct)
    {
        var r = await insights.GetAgingAsync(ct);
        return Ok(new AgingReportResponse(
            r.TotalOutstandingKobo,
            r.Buckets.Select(b => new AgingBucketResponse(b.Label, b.Accounts, b.OutstandingKobo)).ToList()));
    }

    /// <summary>Cash-flow forecast: scheduled billing due (weekly) + a run-rate projection.</summary>
    [HttpGet("forecast")]
    [ProducesResponseType(typeof(CashFlowForecastResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CashFlowForecastResponse>> Forecast([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var f = await insights.GetForecastAsync(days, ct);
        return Ok(new CashFlowForecastResponse(
            f.Days, f.ScheduledDueKobo, f.DailyRunRateKobo, f.RunRateProjectedKobo, f.ProjectedTotalKobo,
            f.Weeks.Select(w => new ForecastWeekResponse(w.WeekStartUtc, w.ScheduledKobo)).ToList()));
    }

    /// <summary>Per-customer collection reliability, worst (most outstanding) first.</summary>
    [HttpGet("customers")]
    [ProducesResponseType(typeof(IEnumerable<CustomerScoreResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CustomerScoreResponse>>> Customers([FromQuery] int take = 100, CancellationToken ct = default)
    {
        var scores = await insights.GetCustomerScoresAsync(take, ct);
        return Ok(scores.Select(s => new CustomerScoreResponse(
            s.CustomerRef, s.CustomerName, s.ExpectedKobo, s.PaidKobo, s.OutstandingKobo,
            s.CollectionRatePct, s.Deposits, s.DuePeriods, s.LatePeriods, s.Score, s.Rating)));
    }
}
