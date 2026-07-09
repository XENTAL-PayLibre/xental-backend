using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xental.Application.ApiKeys;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Application.Onboarding;
using Xental.Domain.Onboarding;
using Xental.Domain.Tenancy;
using Xental.Infrastructure.Security;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class OnboardingApplicationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public void New_application_starts_sandbox_with_both_tracks_not_started()
    {
        var app = new OnboardingApplication(Guid.NewGuid());
        app.Tier.Should().Be(KycTier.Sandbox);
        app.DeveloperKycStatus.Should().Be(TrackStatus.NotStarted);
        app.BusinessKybStatus.Should().Be(TrackStatus.NotStarted);
        app.IsLive.Should().BeFalse();
    }

    [Fact]
    public void Submitting_a_track_moves_it_to_under_review()
    {
        var app = new OnboardingApplication(Guid.NewGuid());
        app.SubmitTrack(OnboardingTrack.DeveloperKyc, Now);
        app.DeveloperKycStatus.Should().Be(TrackStatus.UnderReview);
        app.SubmittedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Tier_becomes_live_only_when_BOTH_tracks_are_approved()
    {
        var app = new OnboardingApplication(Guid.NewGuid());
        var admin = Guid.NewGuid();

        app.SubmitTrack(OnboardingTrack.DeveloperKyc, Now);
        app.ApproveTrack(OnboardingTrack.DeveloperKyc, admin, Now);
        app.Tier.Should().Be(KycTier.Sandbox, "only one track approved");

        app.SubmitTrack(OnboardingTrack.BusinessKyb, Now);
        app.ApproveTrack(OnboardingTrack.BusinessKyb, admin, Now);
        app.Tier.Should().Be(KycTier.Live);
        app.IsLive.Should().BeTrue();
    }

    [Fact]
    public void Reject_and_request_more_info_require_a_reason_and_a_reviewable_status()
    {
        var app = new OnboardingApplication(Guid.NewGuid());
        var admin = Guid.NewGuid();

        // Cannot review a track that hasn't been submitted.
        var reviewNotStarted = () => app.ApproveTrack(OnboardingTrack.DeveloperKyc, admin, Now);
        reviewNotStarted.Should().Throw<Xental.Domain.Common.DomainException>();

        app.SubmitTrack(OnboardingTrack.DeveloperKyc, Now);
        app.RejectTrack(OnboardingTrack.DeveloperKyc, admin, "id mismatch", Now);
        app.DeveloperKycStatus.Should().Be(TrackStatus.Rejected);
        app.DecisionReason.Should().Be("id mismatch");
        app.ReviewedByAdminId.Should().Be(admin);
    }

    [Fact]
    public void Cannot_resubmit_an_already_approved_track()
    {
        var app = new OnboardingApplication(Guid.NewGuid());
        var admin = Guid.NewGuid();
        app.SubmitTrack(OnboardingTrack.DeveloperKyc, Now);
        app.ApproveTrack(OnboardingTrack.DeveloperKyc, admin, Now);

        var act = () => app.SubmitTrack(OnboardingTrack.DeveloperKyc, Now);
        act.Should().Throw<Xental.Domain.Common.DomainException>();
    }
}

public class OnboardingServiceTests
{
    [Fact]
    public async Task GetOrCreate_creates_a_sandbox_application_and_GetTier_defaults_to_sandbox()
    {
        using var db = new TestDatabase();
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        db.Tenant.TenantId = t.Id;
        var svc = new OnboardingService(ctx, db.Tenant);

        (await svc.GetTierAsync()).Should().Be(KycTier.Sandbox, "no application yet");
        var app = await svc.GetOrCreateAsync();
        app.Tier.Should().Be(KycTier.Sandbox);
        (await ctx.OnboardingApplications.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }
}

public class DeveloperKycServiceTests
{
    private static readonly DateOnly Dob = new(1990, 1, 1);

    private static async Task<Guid> SeedTenant(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private static DeveloperKycInput Input(string fullName = "Ada Obi", string id = "22222222222") => new(
        fullName, Dob, "Nigeria", "1 Marina, Lagos", GovIdType.Bvn, id,
        "EMK Bank", "011", fullName, "0123456789", "https://github.com/ada", "Payments app");

    private static (DeveloperKycService svc, FakeIdentityVerifier idv, FakeNombaClient nomba, AesSecretProtector prot)
        Build(TestDatabase db, Xental.Infrastructure.Persistence.XentalDbContext ctx)
    {
        var idv = new FakeIdentityVerifier();
        var nomba = new FakeNombaClient();
        var prot = TestProtector.Create();
        return (new DeveloperKycService(ctx, db.Tenant, prot, idv, nomba, db.Clock, new FakeEmailSender()), idv, nomba, prot);
    }

    [Fact]
    public async Task All_checks_pass_records_verified_evidence_and_moves_track_under_review()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await using var ctx = db.CreateContext();
        var (svc, idv, nomba, prot) = Build(db, ctx);
        idv.IdentityResult = new IdentityResult(true, "Ada", "Obi", Dob);   // govt name matches
        nomba.LookupAccountName = "Ada Obi";                                 // bank name matches

        await svc.SubmitAsync(Input());

        await using var check = db.CreateContext();
        var app = await check.OnboardingApplications.IgnoreQueryFilters().FirstAsync();
        app.DeveloperKycStatus.Should().Be(TrackStatus.UnderReview, "a human always signs off Live");
        app.Tier.Should().Be(KycTier.Sandbox);

        var checks = await check.VerificationChecks.IgnoreQueryFilters().ToListAsync();
        checks.Should().HaveCount(2);
        checks.Should().OnlyContain(c => c.Outcome == VerificationOutcome.Verified);

        // Id number stored encrypted (never raw) and round-trips.
        var kyc = await check.DeveloperKycs.IgnoreQueryFilters().FirstAsync();
        kyc.IdNumberEncrypted.Should().NotContain("22222222222");
        prot.Unprotect(kyc.IdNumberEncrypted).Should().Be("22222222222");
        kyc.IdNumberHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Name_mismatch_flags_the_check_but_still_needs_human_review()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await using var ctx = db.CreateContext();
        var (svc, idv, nomba, _) = Build(db, ctx);
        idv.IdentityResult = new IdentityResult(true, "Someone", "Else", Dob); // govt name does NOT match

        await svc.SubmitAsync(Input(fullName: "Ada Obi"));

        await using var check = db.CreateContext();
        var bvn = await check.VerificationChecks.IgnoreQueryFilters().FirstAsync(c => c.Kind == VerificationKind.Bvn);
        bvn.Outcome.Should().Be(VerificationOutcome.Mismatch);
        (await check.OnboardingApplications.IgnoreQueryFilters().FirstAsync())
            .DeveloperKycStatus.Should().Be(TrackStatus.UnderReview);
    }

    [Fact]
    public async Task Nuban_lookup_error_is_recorded_as_error_not_a_crash()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await using var ctx = db.CreateContext();
        var (svc, _, nomba, _) = Build(db, ctx);
        nomba.LookupThrows = true;

        await svc.SubmitAsync(Input());

        await using var check = db.CreateContext();
        (await check.VerificationChecks.IgnoreQueryFilters().FirstAsync(c => c.Kind == VerificationKind.Nuban))
            .Outcome.Should().Be(VerificationOutcome.Error);
    }

    [Fact]
    public async Task Invalid_bvn_length_is_rejected()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await using var ctx = db.CreateContext();
        var (svc, _, _, _) = Build(db, ctx);

        var act = () => svc.SubmitAsync(Input(id: "123"));
        await act.Should().ThrowAsync<Xental.Application.Common.Exceptions.ValidationException>();
    }
}

public class BusinessKybServiceTests
{
    private static async Task<Guid> SeedTenant(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private static BusinessKybInput Input(string legal = "Acme Ltd") => new(
        legal, "RC123456", "LLC", "Finance", "Nigeria", "1 Marina, Lagos",
        "+234", "7035678999", "https://acme.example",
        "EMK Bank", "011", legal, "0123456789");

    private static (BusinessKybService svc, FakeIdentityVerifier idv, FakeNombaClient nomba, FakeDocumentStorage store)
        Build(TestDatabase db, Xental.Infrastructure.Persistence.XentalDbContext ctx)
    {
        var idv = new FakeIdentityVerifier();
        var nomba = new FakeNombaClient();
        var store = new FakeDocumentStorage();
        return (new BusinessKybService(ctx, db.Tenant, idv, nomba, store, db.Clock, new FakeEmailSender()), idv, nomba, store);
    }

    private static Stream Pdf(int bytes = 1024) => new MemoryStream(new byte[bytes]);

    [Fact]
    public async Task Save_business_runs_cac_and_nuban_checks_and_marks_track_in_progress()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await using var ctx = db.CreateContext();
        var (svc, idv, nomba, _) = Build(db, ctx);
        idv.CompanyResult = new CompanyResult(true, "Acme Ltd", "RC123456");
        nomba.LookupAccountName = "Acme Ltd";

        await svc.SaveBusinessAsync(Input());

        await using var check = db.CreateContext();
        var checks = await check.VerificationChecks.IgnoreQueryFilters().ToListAsync();
        checks.Select(c => c.Kind).Should().Contain(new[] { VerificationKind.Cac, VerificationKind.Nuban });
        checks.Should().OnlyContain(c => c.Outcome == VerificationOutcome.Verified);
        (await check.OnboardingApplications.IgnoreQueryFilters().FirstAsync())
            .BusinessKybStatus.Should().Be(TrackStatus.InProgress);
    }

    [Fact]
    public async Task Upload_rejects_non_pdf_image_and_oversize()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await using var ctx = db.CreateContext();
        var (svc, _, _, _) = Build(db, ctx);

        var badType = () => svc.UploadDocumentAsync(KycDocumentType.ProofOfAddress, Pdf(), "text/plain");
        await badType.Should().ThrowAsync<Xental.Application.Common.Exceptions.ValidationException>();

        var tooBig = () => svc.UploadDocumentAsync(KycDocumentType.ProofOfAddress, Pdf(11 * 1024 * 1024), "application/pdf");
        await tooBig.Should().ThrowAsync<Xental.Application.Common.Exceptions.ValidationException>();
    }

    [Fact]
    public async Task Submit_requires_both_documents_and_then_moves_track_under_review()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await using var ctx = db.CreateContext();
        var (svc, _, _, store) = Build(db, ctx);

        await svc.SaveBusinessAsync(Input());

        // Missing documents -> cannot submit.
        var premature = () => svc.SubmitAsync(attestationAccepted: true, ip: "1.2.3.4");
        await premature.Should().ThrowAsync<Xental.Application.Common.Exceptions.ValidationException>();

        await svc.UploadDocumentAsync(KycDocumentType.CertificateOfIncorporation, Pdf(), "application/pdf");
        await svc.UploadDocumentAsync(KycDocumentType.ProofOfAddress, Pdf(), "image/png");

        // Attestation is mandatory.
        var noAttest = () => svc.SubmitAsync(attestationAccepted: false, ip: "1.2.3.4");
        await noAttest.Should().ThrowAsync<Xental.Application.Common.Exceptions.ValidationException>();

        await svc.SubmitAsync(attestationAccepted: true, ip: "1.2.3.4");

        await using var check = db.CreateContext();
        (await check.OnboardingApplications.IgnoreQueryFilters().FirstAsync())
            .BusinessKybStatus.Should().Be(TrackStatus.UnderReview);
        (await check.BusinessKybs.IgnoreQueryFilters().FirstAsync()).AttestationAccepted.Should().BeTrue();
        store.Objects.Should().HaveCount(2, "both documents stored");
    }
}

public class NameMatcherTests
{
    [Theory]
    [InlineData("Ada Obi", "Ada Obi", true)]
    [InlineData("ADA  OBI", "ada obi", true)]
    [InlineData("Ada Obi", "Obi Ada", true)]        // order-independent
    [InlineData("Ada", "Ada Chidinma Obi", true)]   // subset
    [InlineData("Ada Obi", "Chidi Okonkwo", false)]
    [InlineData("Ada Obi", "", false)]
    public void Matches_names_fuzzily(string a, string b, bool expected) =>
        Xental.Domain.Common.NameMatcher.IsMatch(a, b).Should().Be(expected);
}

public class LiveKeyGateTests
{
    private static async Task<Guid> SeedTenant(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var t = new Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h"); ctx.Tenants.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private static ApiKeyService Service(TestDatabase db, Xental.Infrastructure.Persistence.XentalDbContext ctx) =>
        new(ctx, db.Tenant, new Pbkdf2SecretHasher(), new FakeTokenGenerator(), db.Clock);

    [Fact]
    public async Task Sandbox_tenant_can_create_TEST_keys()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await using var ctx = db.CreateContext();
        var created = await Service(db, ctx).CreateAsync("test key", ApiKeyMode.Test);
        created.ClientId.Should().StartWith("xnt_test");
    }

    [Fact]
    public async Task Sandbox_tenant_is_BLOCKED_from_creating_LIVE_keys()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await using var ctx = db.CreateContext();

        var act = () => Service(db, ctx).CreateAsync("live key", ApiKeyMode.Live);
        await act.Should().ThrowAsync<OnboardingNotApprovedException>();

        (await ctx.ApiKeys.IgnoreQueryFilters().CountAsync()).Should().Be(0, "no key issued when gated");
    }

    [Fact]
    public async Task Live_tier_tenant_can_create_LIVE_keys()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedTenant(db);
        await OnboardingSeed.ApprovedLiveAsync(db, db.Tenant.TenantId!.Value);

        await using var ctx = db.CreateContext();
        var created = await Service(db, ctx).CreateAsync("live key", ApiKeyMode.Live);
        created.ClientId.Should().StartWith("xnt_live");
    }
}
