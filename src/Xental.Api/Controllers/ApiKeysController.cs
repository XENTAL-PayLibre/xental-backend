using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.ApiKeys;
using Xental.Domain.Tenancy;

namespace Xental.Api.Controllers;

/// <summary>
/// API key management (the dashboard plane). Requires a <c>dashboard</c>-scoped token
/// (from <c>/developers/login</c>). Keys come in <c>test</c> and <c>live</c> modes; the
/// client secret is returned once at creation and never again.
/// </summary>
[ApiController]
[Route("api/v1/api-keys")]
[Authorize(Policy = AuthPolicies.ManageKeys)]
public sealed class ApiKeysController(ApiKeyService apiKeys) : ControllerBase
{
    /// <summary>Create a new API key. The client secret is shown only in this response.</summary>
    /// <response code="201">Key created; body carries the one-time client secret.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ApiKeyResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiKeyResponse>> Create(CreateApiKeyRequest request, CancellationToken ct)
    {
        var mode = ParseMode(request.Mode);
        var created = await apiKeys.CreateAsync(request.Label, mode, ct);
        var response = new ApiKeyResponse(
            created.Id, created.ClientId, created.ClientSecret,
            created.Mode, created.Label, ApiKeyStatus.Active.ToString(), null, created.CreatedAtUtc);
        return Created($"/api/v1/api-keys/{created.Id}", response);
    }

    /// <summary>List the current account's API keys (secrets are never returned here).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ApiKeyResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ApiKeyResponse>>> List(CancellationToken ct)
    {
        var keys = await apiKeys.ListAsync(ct);
        return Ok(keys.Select(ToResponse));
    }

    /// <summary>Revoke the existing key and issue a fresh one with the same label + mode.</summary>
    /// <response code="200">Rotated; body carries the new one-time client secret.</response>
    /// <response code="404">Key not found for this account.</response>
    [HttpPost("{id:guid}/rotate")]
    [ProducesResponseType(typeof(ApiKeyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiKeyResponse>> Rotate(Guid id, CancellationToken ct)
    {
        var created = await apiKeys.RotateAsync(id, ct);
        var response = new ApiKeyResponse(
            created.Id, created.ClientId, created.ClientSecret,
            created.Mode, created.Label, ApiKeyStatus.Active.ToString(), null, created.CreatedAtUtc);
        return Ok(response);
    }

    /// <summary>Revoke an API key. Tokens minted from it stop working once they expire.</summary>
    /// <response code="204">Revoked.</response>
    /// <response code="404">Key not found for this account.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await apiKeys.RevokeAsync(id, ct);
        return NoContent();
    }

    private static ApiKeyResponse ToResponse(ApiKey k) =>
        new(k.Id, k.ClientId, null, k.Mode.ToString(), k.Label, k.Status.ToString(), k.LastUsedAtUtc, k.CreatedAtUtc);

    private static ApiKeyMode ParseMode(string mode) =>
        mode.Equals("live", StringComparison.OrdinalIgnoreCase) ? ApiKeyMode.Live : ApiKeyMode.Test;
}
