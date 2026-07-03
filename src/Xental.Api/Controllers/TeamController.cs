using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xental.Api.Authorization;
using Xental.Api.Contracts;
using Xental.Application.Team;
using Xental.Domain.Tenancy;

namespace Xental.Api.Controllers;

/// <summary>
/// Team directory for a developer account (dashboard plane). Manage the people on your team and
/// their roles (Admin / Employee / Developer). Records only — not separate logins.
/// </summary>
[ApiController]
[Route("api/v1/team")]
[Authorize(Policy = AuthPolicies.TeamManage)]
public sealed class TeamController(TeamService team) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TeamMemberResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TeamMemberResponse>>> List(CancellationToken ct) =>
        Ok((await team.ListAsync(ct)).Select(ToResponse));

    /// <summary>Invite a team member by email. They receive an accept link to set a password.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(TeamMemberResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TeamMemberResponse>> Invite(AddTeamMemberRequest request, CancellationToken ct)
    {
        var member = await team.InviteAsync(new TeamMemberSpec(request.Name, request.Email, request.Role), ct);
        return Created($"/api/v1/team/{member.Id}", ToResponse(member));
    }

    /// <summary>Accept a team invitation by setting a password (anonymous). Then sign in normally.</summary>
    [HttpPost("accept")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Accept(AcceptInviteRequest request, CancellationToken ct)
    {
        await team.AcceptAsync(request.Token, request.Password, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(TeamMemberResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TeamMemberResponse>> Update(Guid id, UpdateTeamMemberRequest request, CancellationToken ct)
    {
        var member = await team.UpdateAsync(id, new TeamMemberSpec(request.Name, request.Email, request.Role), ct);
        return Ok(ToResponse(member));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        await team.RemoveAsync(id, ct);
        return NoContent();
    }

    private static TeamMemberResponse ToResponse(TeamMember m) =>
        new(m.Id, m.Name, m.Email, m.Role.ToString(), m.Status.ToString(), m.CreatedAtUtc);
}
