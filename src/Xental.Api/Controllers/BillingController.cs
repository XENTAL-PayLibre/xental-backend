using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Billing;
using Xental.Application.Common.Exceptions;
using Xental.Domain.Billing;

namespace Xental.Api.Controllers;

/// <summary>
/// Recurring billing (push model). A schedule binds a customer's reusable virtual account to a
/// cadence and a per-cycle expected amount (variable — settable each cycle). Xental opens a period
/// each cycle, attributes the customer's DVA deposits to it, reminds on due/overdue, and emits
/// <c>billing.period.due|paid|overdue</c> webhooks. It never pulls funds. Usable from an API key or
/// the dashboard.
/// </summary>
[ApiController]
[Route("api/v1/billing")]
[Authorize(Policy = AuthPolicies.ApiOrDashboard)]
public sealed class BillingController(BillingService billing) : ControllerBase
{
    /// <summary>Create a schedule on one of your reusable virtual accounts and open its first period.</summary>
    /// <response code="400">Invalid interval/amount, or the account is closed.</response>
    /// <response code="404">No virtual account for the given accountRef.</response>
    /// <response code="409">The account already has an active schedule (or ref clash).</response>
    [HttpPost("schedules")]
    [Authorize(Policy = AuthPolicies.ManageBilling)]
    [ProducesResponseType(typeof(BillingScheduleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BillingScheduleResponse>> Create(CreateBillingScheduleRequest request, CancellationToken ct)
    {
        if (!TryParseInterval(request.Interval, out var interval))
            throw new ValidationException("interval must be one of: Weekly, Monthly, Quarterly, Yearly.");

        var view = await billing.CreateAsync(
            request.AccountRef, interval, request.AmountKobo, request.DueOffsetDays, request.Description, request.Reference, ct);
        return Created($"/api/v1/billing/schedules/{view.Schedule.Id}", ToResponse(view));
    }

    /// <summary>List the tenant's billing schedules (most recent first).</summary>
    [HttpGet("schedules")]
    [ProducesResponseType(typeof(IEnumerable<BillingScheduleResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<BillingScheduleResponse>>> List([FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok((await billing.ListAsync(take, ct)).Select(ToResponse));

    /// <summary>Fetch a schedule by id.</summary>
    [HttpGet("schedules/{id:guid}")]
    [ProducesResponseType(typeof(BillingScheduleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BillingScheduleResponse>> Get(Guid id, CancellationToken ct) =>
        Ok(ToResponse(await billing.GetAsync(id, ct)));

    /// <summary>Set the expected amount for the next cycle (variable billing).</summary>
    [HttpPut("schedules/{id:guid}/next-amount")]
    [Authorize(Policy = AuthPolicies.ManageBilling)]
    [ProducesResponseType(typeof(BillingScheduleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BillingScheduleResponse>> SetNextAmount(Guid id, SetNextAmountRequest request, CancellationToken ct) =>
        Ok(ToResponse(await billing.SetNextAmountAsync(id, request.AmountKobo, ct)));

    /// <summary>Pause a schedule (stops opening new periods).</summary>
    [HttpPost("schedules/{id:guid}/pause")]
    [Authorize(Policy = AuthPolicies.ManageBilling)]
    [ProducesResponseType(typeof(BillingScheduleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BillingScheduleResponse>> Pause(Guid id, CancellationToken ct) =>
        Ok(ToResponse(await billing.PauseAsync(id, ct)));

    /// <summary>Resume a paused schedule.</summary>
    [HttpPost("schedules/{id:guid}/resume")]
    [Authorize(Policy = AuthPolicies.ManageBilling)]
    [ProducesResponseType(typeof(BillingScheduleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BillingScheduleResponse>> Resume(Guid id, CancellationToken ct) =>
        Ok(ToResponse(await billing.ResumeAsync(id, ct)));

    /// <summary>Cancel a schedule permanently.</summary>
    [HttpPost("schedules/{id:guid}/cancel")]
    [Authorize(Policy = AuthPolicies.ManageBilling)]
    [ProducesResponseType(typeof(BillingScheduleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BillingScheduleResponse>> Cancel(Guid id, CancellationToken ct) =>
        Ok(ToResponse(await billing.CancelAsync(id, ct)));

    /// <summary>List a schedule's billing periods (most recent first).</summary>
    [HttpGet("schedules/{id:guid}/periods")]
    [ProducesResponseType(typeof(IEnumerable<BillingPeriodResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<BillingPeriodResponse>>> Periods(Guid id, [FromQuery] int take = 100, CancellationToken ct = default) =>
        Ok((await billing.ListPeriodsAsync(id, take, ct)).Select(ToResponse));

    private static bool TryParseInterval(string value, out BillingInterval interval) =>
        Enum.TryParse(value, ignoreCase: true, out interval) && Enum.IsDefined(interval);

    private static BillingScheduleResponse ToResponse(BillingScheduleView v) => new(
        v.Schedule.Id, v.Schedule.Reference, v.AccountRef, v.Schedule.Interval.ToString(), v.Schedule.Status.ToString(),
        v.Schedule.NextAmountKobo, v.Schedule.DueOffsetDays, v.Schedule.PeriodsGenerated, v.Schedule.CarryCreditKobo,
        v.Schedule.CurrentPeriodEndUtc, v.Schedule.Description, v.Schedule.CreatedAtUtc);

    private static BillingPeriodResponse ToResponse(BillingPeriod p) => new(
        p.Id, p.Sequence, p.Status.ToString(), p.ExpectedAmountKobo, p.AmountAttributedKobo, p.OutstandingKobo,
        p.PeriodStartUtc, p.PeriodEndUtc, p.DueDateUtc, p.PaidAtUtc);
}
