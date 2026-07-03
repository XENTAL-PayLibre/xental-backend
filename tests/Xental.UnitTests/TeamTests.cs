using FluentAssertions;
using Xental.Application.Common.Exceptions;
using Xental.Application.Team;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Security;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class TeamServiceTests
{
    private static (TeamService svc, FakeEmailSender email) Build(TestDatabase db, Infrastructure.Persistence.XentalDbContext ctx)
    {
        var email = new FakeEmailSender();
        var svc = new TeamService(ctx, db.Tenant, TestSecurity.PasswordHasher(),
            new FakeTokenGenerator(), new Sha256TokenHasher(), new FakeLinkBuilder(), email, db.Clock);
        return (svc, email);
    }

    private static async Task<Guid> SeedTenantAsync(TestDatabase db, Infrastructure.Persistence.XentalDbContext ctx)
    {
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;
        return t.Id;
    }

    [Fact]
    public async Task Invite_creates_a_pending_member_and_emails_a_link()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedTenantAsync(db, ctx);
        var (svc, email) = Build(db, ctx);

        var m = await svc.InviteAsync(new TeamMemberSpec("Tunde Adebayo", "Tunde@Gmail.com", "Admin"));
        m.Status.Should().Be(TeamMemberStatus.Invited);
        m.Email.Should().Be("tunde@gmail.com");
        m.CanSignIn.Should().BeFalse("no password until they accept");
        email.LastInviteLink.Should().NotBeNullOrEmpty();

        (await svc.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Accept_sets_password_and_activates()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedTenantAsync(db, ctx);
        var (svc, email) = Build(db, ctx);

        await svc.InviteAsync(new TeamMemberSpec("Ada", "ada@x.com", "Developer"));
        await svc.AcceptAsync(email.LastInviteLink!, TestSecurity.StrongPassword);

        var member = (await svc.ListAsync()).Single();
        member.Status.Should().Be(TeamMemberStatus.Active);
        member.CanSignIn.Should().BeTrue();
    }

    [Fact]
    public async Task Accept_with_a_bad_token_fails()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedTenantAsync(db, ctx);
        var (svc, _) = Build(db, ctx);
        var act = async () => await svc.AcceptAsync("not-a-real-token", TestSecurity.StrongPassword);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Accept_rejects_a_weak_password()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedTenantAsync(db, ctx);
        var (svc, email) = Build(db, ctx);
        await svc.InviteAsync(new TeamMemberSpec("Ada", "ada@x.com", "Developer"));
        var act = async () => await svc.AcceptAsync(email.LastInviteLink!, "weak");
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedTenantAsync(db, ctx);
        var (svc, _) = Build(db, ctx);
        await svc.InviteAsync(new TeamMemberSpec("A", "dev@x.com", "Developer"));
        var act = async () => await svc.InviteAsync(new TeamMemberSpec("B", "DEV@x.com", "Employee"));
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Update_changes_name_email_role()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedTenantAsync(db, ctx);
        var (svc, _) = Build(db, ctx);
        var m = await svc.InviteAsync(new TeamMemberSpec("Old", "old@x.com", "Employee"));

        var updated = await svc.UpdateAsync(m.Id, new TeamMemberSpec("New Name", "new@x.com", "Developer"));
        updated.Name.Should().Be("New Name");
        updated.Email.Should().Be("new@x.com");
        updated.Role.Should().Be(TeamRole.Developer);
    }

    [Fact]
    public async Task Remove_drops_from_list_and_frees_the_email()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedTenantAsync(db, ctx);
        var (svc, _) = Build(db, ctx);
        var m = await svc.InviteAsync(new TeamMemberSpec("Gone", "gone@x.com", "Admin"));

        await svc.RemoveAsync(m.Id);
        (await svc.ListAsync()).Should().BeEmpty();
        var act = async () => await svc.InviteAsync(new TeamMemberSpec("Again", "gone@x.com", "Admin"));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Unknown_role_is_a_validation_error()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        await SeedTenantAsync(db, ctx);
        var (svc, _) = Build(db, ctx);
        var act = async () => await svc.InviteAsync(new TeamMemberSpec("X", "x@x.com", "Owner"));
        await act.Should().ThrowAsync<ValidationException>();
    }
}
