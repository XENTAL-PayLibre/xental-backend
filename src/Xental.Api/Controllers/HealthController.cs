using Microsoft.AspNetCore.Mvc;

namespace Xental.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class HealthController : ControllerBase
{
    /// <summary>Lightweight liveness probe for the API.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(new
    {
        status = "Healthy",
        service = "Xental.Api",
        timestamp = DateTimeOffset.UtcNow
    });
}
