using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Assistant;

namespace Xental.Api.Controllers;

/// <summary>
/// In-dashboard Copilot (agent plane, differentiator): natural-language questions about the
/// merchant's own account, answered from live data (insights, aging, forecast, customer scores,
/// flows). Grounded and tenant-scoped — it never invents figures. Dashboard plane.
/// </summary>
[ApiController]
[Route("api/v1/copilot")]
[Authorize(Policy = AuthPolicies.Dashboard)]
public sealed class CopilotController(CopilotService copilot) : ControllerBase
{
    /// <summary>Ask the Copilot a question about your account.</summary>
    [HttpPost("ask")]
    [ProducesResponseType(typeof(CopilotAnswer), StatusCodes.Status200OK)]
    public async Task<ActionResult<CopilotAnswer>> Ask(CopilotAskRequest request, CancellationToken ct) =>
        Ok(await copilot.AskAsync(request.Prompt, ct));
}
