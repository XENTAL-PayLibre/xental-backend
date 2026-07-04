using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.Team;

public sealed record TeamMemberSpec(string Name, string Email, string Role);

/// <summary>
/// Manages the current account's team (dashboard plane): invite by email, edit, remove; plus the
/// anonymous accept-invite flow where an invitee sets a password and becomes able to sign in.
/// </summary>
public sealed class TeamService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    IPasswordHasher passwords,
    ITokenGenerator tokens,
    ITokenHasher tokenHasher,
    ILinkBuilder links,
    IEmailSender email,
    IClock clock)
{
    public async Task<IReadOnlyList<TeamMember>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        return await db.TeamMembers.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.Status != TeamMemberStatus.Removed)
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync(ct);
    }

    /// <summary>Invite a new team member: create them (Invited) and email an accept link.</summary>
    public async Task<TeamMember> InviteAsync(TeamMemberSpec spec, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var role = ParseRole(spec.Role);
        var normalizedEmail = TeamMember.NormalizeEmail(spec.Email);

        var owner = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (owner is not null && owner.Email == normalizedEmail)
            throw new ConflictException("That email already owns this account.");

        var exists = await db.TeamMembers
            .AnyAsync(m => m.TenantId == tenantId && m.Email == normalizedEmail && m.Status != TeamMemberStatus.Removed, ct);
        if (exists)
            throw new ConflictException($"A team member with email '{normalizedEmail}' already exists.");

        var raw = tokens.Generate("inv", 32);
        var member = new TeamMember(tenantId, spec.Name, spec.Email, role, tokenHasher.Hash(raw), clock.UtcNow.Add(links.TeamInviteTtl));
        db.TeamMembers.Add(member);
        await db.SaveChangesAsync(ct);

        await email.SendTeamInviteAsync(member.Email, links.TeamInviteLink(raw), owner?.Name ?? "your team", ct);
        return member;
    }

    public async Task<TeamMember> UpdateAsync(Guid id, TeamMemberSpec spec, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var member = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId && m.Status != TeamMemberStatus.Removed, ct)
            ?? throw new NotFoundException($"Team member '{id}' not found.");

        var role = ParseRole(spec.Role);
        var normalizedEmail = TeamMember.NormalizeEmail(spec.Email);
        if (normalizedEmail != member.Email)
        {
            var clash = await db.TeamMembers
                .AnyAsync(m => m.TenantId == tenantId && m.Email == normalizedEmail && m.Status != TeamMemberStatus.Removed && m.Id != id, ct);
            if (clash)
                throw new ConflictException($"A team member with email '{normalizedEmail}' already exists.");
        }

        member.Update(spec.Name, spec.Email, role);
        await db.SaveChangesAsync(ct);
        return member;
    }

    /// <summary>Re-send a pending invitation: rotate the token (invalidating the old link) and email a fresh one.</summary>
    public async Task<TeamMember> ResendAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var member = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId && m.Status == TeamMemberStatus.Invited, ct)
            ?? throw new NotFoundException($"No pending invitation for team member '{id}'.");

        var raw = tokens.Generate("inv", 32);
        member.ReInvite(tokenHasher.Hash(raw), clock.UtcNow.Add(links.TeamInviteTtl));
        await db.SaveChangesAsync(ct);

        var owner = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        await email.SendTeamInviteAsync(member.Email, links.TeamInviteLink(raw), owner?.Name ?? "your team", ct);
        return member;
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var member = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId && m.Status != TeamMemberStatus.Removed, ct)
            ?? throw new NotFoundException($"Team member '{id}' not found.");
        member.Remove();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Anonymous: accept an invitation by setting a password. The member can then sign in.</summary>
    public async Task<TeamMember> AcceptAsync(string rawToken, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            throw new ValidationException("This invitation is invalid or has expired.");
        Common.PasswordPolicy.Validate(password);

        var hash = tokenHasher.Hash(rawToken);
        var member = await db.TeamMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.InviteTokenHash == hash, ct);
        if (member is null || !member.InviteUsable(clock.UtcNow))
            throw new ValidationException("This invitation is invalid or has expired.");

        member.Accept(passwords.Hash(password), clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return member;
    }

    private static TeamRole ParseRole(string role) =>
        Enum.TryParse<TeamRole>(role, ignoreCase: true, out var r)
            ? r
            : throw new ValidationException($"Unknown role '{role}'. Use Admin, Employee, or Developer.");
}
