using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Xental.Api.Contracts;
using Xental.Application.Authentication;

namespace Xental.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController(AuthenticationService authentication) : ControllerBase
{
    /// <summary>Exchange client credentials for a short-lived JWT access token.</summary>
    [AllowAnonymous]
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Token(TokenRequest request, CancellationToken ct)
    {
        var token = await authentication.IssueTokenAsync(request.ClientId, request.ClientSecret, ct);
        return Ok(new TokenResponse(token.Token, "Bearer", token.ExpiresInSeconds));
    }
}
