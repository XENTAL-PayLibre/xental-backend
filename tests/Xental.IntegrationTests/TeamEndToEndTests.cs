using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xental.IntegrationTests;

public class TeamEndToEndTests
{
    private const string Password = "Str0ng-Passw0rd!";
    private static int _seq;
    private static string NewEmail() => $"team{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}@example.com";

    private sealed record TeamMemberResponse(Guid Id, string Name, string Email, string Role, string Status, DateTimeOffset CreatedAtUtc);

    private static HttpClient NewClient(XentalApiFactory f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<HttpClient> DashboardClientAsync(XentalApiFactory f)
    {
        var client = NewClient(f);
        var email = NewEmail();
        (await client.PostAsJsonAsync("/api/v1/developers/register", new { name = "Owner", email, password = Password }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var token = FakeEmailSender.VerificationTokenFor(email);
        await client.GetAsync($"/api/v1/developers/verify-email?token={token}");
        (await client.PostAsJsonAsync("/api/v1/developers/login", new { email, password = Password }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    [Fact]
    public async Task Invite_lists_updates_and_removes_a_member()
    {
        using var f = new XentalApiFactory();
        var owner = await DashboardClientAsync(f);

        var add = await owner.PostAsJsonAsync("/api/v1/team", new { name = "Tunde Adebayo", email = "tunde@example.com", role = "Admin" });
        add.StatusCode.Should().Be(HttpStatusCode.Created);
        var member = (await add.Content.ReadFromJsonAsync<TeamMemberResponse>())!;
        member.Status.Should().Be("Invited");

        (await owner.GetFromJsonAsync<List<TeamMemberResponse>>("/api/v1/team")).Should().ContainSingle();

        var upd = await owner.PutAsJsonAsync($"/api/v1/team/{member.Id}", new { name = "Tunde A.", email = "tunde@example.com", role = "Developer" });
        (await upd.Content.ReadFromJsonAsync<TeamMemberResponse>())!.Role.Should().Be("Developer");

        (await owner.DeleteAsync($"/api/v1/team/{member.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<TeamMemberResponse>>("/api/v1/team")).Should().BeEmpty();
    }

    [Fact]
    public async Task Invite_accept_then_member_signs_in_with_their_role()
    {
        using var f = new XentalApiFactory();
        var owner = await DashboardClientAsync(f);
        var memberEmail = NewEmail();

        (await owner.PostAsJsonAsync("/api/v1/team", new { name = "Ada", email = memberEmail, role = "Developer" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var inviteToken = FakeEmailSender.InviteTokenFor(memberEmail);
        inviteToken.Should().NotBeNullOrEmpty();

        var accept = await NewClient(f).PostAsJsonAsync("/api/v1/team/accept", new { token = inviteToken, password = Password });
        accept.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The member can now sign in to the same account.
        var member = NewClient(f);
        (await member.PostAsJsonAsync("/api/v1/developers/login", new { email = memberEmail, password = Password }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // RBAC: a Developer can manage API keys but NOT the team (Admin/Owner only).
        (await member.GetAsync("/api/v1/api-keys")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.GetAsync("/api/v1/team")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Duplicate_email_conflicts()
    {
        using var f = new XentalApiFactory();
        var owner = await DashboardClientAsync(f);
        await owner.PostAsJsonAsync("/api/v1/team", new { name = "A", email = "dupe@example.com", role = "Employee" });
        (await owner.PostAsJsonAsync("/api/v1/team", new { name = "B", email = "dupe@example.com", role = "Admin" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Team_requires_a_dashboard_session()
    {
        using var f = new XentalApiFactory();
        (await NewClient(f).GetAsync("/api/v1/team")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
