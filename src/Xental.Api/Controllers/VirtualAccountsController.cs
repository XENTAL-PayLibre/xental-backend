using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Payments;
using Xental.Domain.Payments;

namespace Xental.Api.Controllers;

/// <summary>
/// Persistent virtual accounts (NUBANs) — the API plane. Requires an <c>api</c>-scoped token.
/// Provisioning maps a developer-supplied <c>accountRef</c> to a Nomba NUBAN and (optionally)
/// an expected amount used to reconcile inflows.
/// </summary>
[ApiController]
[Route("api/v1/virtual-accounts")]
[Authorize(Policy = AuthPolicies.ApiOrDashboard)] // reads usable from the dashboard; provisioning stays API-only
public sealed class VirtualAccountsController(VirtualAccountService accounts) : ControllerBase
{
    /// <summary>List the tenant's virtual accounts (optionally scoped to a sub-merchant).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<VirtualAccountResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<VirtualAccountResponse>>> List(
        [FromQuery] string? subMerchantRef, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok((await accounts.ListAsync(subMerchantRef, take, ct)).Select(ToResponse));

    /// <summary>Provision a persistent NUBAN for a customer. Provisioning stays API-key only (key mode
    /// decides test/live).</summary>
    /// <response code="201">Provisioned; body carries the NUBAN + reconciliation state.</response>
    /// <response code="409">A virtual account already exists for this accountRef.</response>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.Api)]
    [ProducesResponseType(typeof(VirtualAccountResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<VirtualAccountResponse>> Create(CreateVirtualAccountRequest request, CancellationToken ct)
    {
        // Anything that isn't an explicit live key provisions a simulated sandbox NUBAN.
        var testMode = !string.Equals(User.FindFirst("key_mode")?.Value, "live", StringComparison.OrdinalIgnoreCase);
        var va = await accounts.CreateAsync(
            request.AccountRef, request.Name, request.Email, request.Phone,
            request.ExpectedAmountKobo, request.ExpiryDateUtc, request.SubMerchantRef, testMode, ct);
        return Created($"/api/v1/virtual-accounts/{va.Reference}", ToResponse(va));
    }

    /// <summary>Fetch a virtual account by its accountRef (details + balance/state).</summary>
    /// <response code="404">No virtual account for this accountRef.</response>
    [HttpGet("{accountRef}")]
    [ProducesResponseType(typeof(VirtualAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VirtualAccountResponse>> Get(string accountRef, CancellationToken ct)
    {
        var va = await accounts.GetByReferenceAsync(accountRef, ct);
        return Ok(ToResponse(va));
    }

    private static VirtualAccountResponse ToResponse(VirtualAccount v) => new(
        v.Id, v.Reference, v.AccountNumber, v.BankName, v.AccountName,
        v.ExpectedAmountKobo, v.AmountPaidKobo, v.Deficit.Kobo, v.OverpaymentCredit.Kobo,
        v.Status.ToString(), v.PaymentState.ToString(), v.SubMerchantId, v.ExpiryDateUtc, v.CreatedAtUtc);
}
