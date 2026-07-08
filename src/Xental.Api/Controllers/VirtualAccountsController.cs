using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Common.Interfaces;
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
[Authorize(Policy = AuthPolicies.ApiOrDashboard)] // reads usable from either plane
public sealed class VirtualAccountsController(VirtualAccountService accounts, IModeContext mode) : ControllerBase
{
    /// <summary>List the tenant's virtual accounts (optionally scoped to a sub-merchant).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<VirtualAccountResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<VirtualAccountResponse>>> List(
        [FromQuery] string? subMerchantRef, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok((await accounts.ListAsync(subMerchantRef, take, ct)).Select(ToResponse));

    /// <summary>Provision a persistent NUBAN for a customer.
    /// <para>From an <b>API key</b> the mode follows the key's <c>key_mode</c> — a test key mints a
    /// sandbox NUBAN, a live key a real one. From the <b>dashboard</b> provisioning is always live and
    /// requires approved onboarding; test customers and virtual accounts are created via a test API key.</para></summary>
    /// <response code="201">Provisioned; body carries the NUBAN + reconciliation state.</response>
    /// <response code="403">Provisioning from the dashboard without approved onboarding.</response>
    /// <response code="409">A virtual account already exists for this accountRef.</response>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.Provision)]
    [ProducesResponseType(typeof(VirtualAccountResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<VirtualAccountResponse>> Create(CreateVirtualAccountRequest request, CancellationToken ct)
    {
        // Test only from a test API key; the dashboard always provisions live (KYC-gated in IModeContext).
        var testMode = !await mode.IsLiveAsync(ct);
        var va = await accounts.CreateAsync(
            request.AccountRef, request.Name, request.Email, request.Phone,
            request.ExpectedAmountKobo, request.ExpiryDateUtc, request.SubMerchantRef, testMode, ct);
        return Created($"/api/v1/virtual-accounts/{va.Reference}",
            ToResponse(va, request.Name, request.Email, request.Phone));
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

    /// <summary>Delete a customer's virtual account (and the customer if it has no other accounts).
    /// Only accounts with no payment activity can be deleted.</summary>
    /// <response code="204">Deleted.</response>
    /// <response code="404">No virtual account for this accountRef.</response>
    /// <response code="409">The account has payment activity and cannot be deleted.</response>
    [HttpDelete("{accountRef}")]
    [Authorize(Policy = AuthPolicies.Provision)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(string accountRef, CancellationToken ct)
    {
        await accounts.DeleteAsync(accountRef, ct);
        return NoContent();
    }

    private static VirtualAccountResponse ToResponse(VirtualAccountView vv) =>
        Map(vv.Account, vv.CustomerName, vv.CustomerEmail, vv.CustomerPhone);

    private static VirtualAccountResponse ToResponse(VirtualAccount v, string? customerName, string? customerEmail, string? customerPhone) =>
        Map(v, customerName, customerEmail, customerPhone);

    private static VirtualAccountResponse Map(VirtualAccount v, string? customerName, string? customerEmail, string? customerPhone) => new(
        v.Id, v.Reference, v.AccountNumber, v.BankName, v.AccountName,
        customerName, customerEmail, customerPhone,
        v.ExpectedAmountKobo, v.AmountPaidKobo, v.Deficit.Kobo, v.OverpaymentCredit.Kobo,
        v.Status.ToString(), v.PaymentState.ToString(), v.SubMerchantId, v.ExpiryDateUtc, v.CreatedAtUtc);
}
