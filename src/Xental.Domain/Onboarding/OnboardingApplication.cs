using Xental.Domain.Common;

namespace Xental.Domain.Onboarding;

/// <summary>Access level unlocked by onboarding. Signup grants Sandbox; approved KYC+KYB grants Live.</summary>
public enum KycTier
{
    Sandbox = 1, // test keys only, zero real money
    Live = 2,    // live keys, real money
}

/// <summary>Status of one onboarding track (developer KYC or business KYB).</summary>
public enum TrackStatus
{
    NotStarted = 1,
    InProgress = 2,      // saved but not submitted
    Submitted = 3,       // applicant submitted; auto-checks running
    UnderReview = 4,     // needs a human (docs, or an auto-check that couldn't verify)
    MoreInfoNeeded = 5,  // admin bounced it back to the applicant
    Approved = 6,
    Rejected = 7,
}

/// <summary>Which onboarding track a review action targets.</summary>
public enum OnboardingTrack
{
    DeveloperKyc = 1,
    BusinessKyb = 2,
}

/// <summary>
/// Coordinates a tenant's move from Sandbox to Live. One per tenant. Holds the tier and the
/// status of each track (developer KYC, business KYB); the actual field data lives in
/// <see cref="DeveloperKyc"/> / <see cref="BusinessKyb"/>. Real-money access is granted only when
/// <b>both</b> tracks are Approved by an admin — never automatically.
/// </summary>
public sealed class OnboardingApplication : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public KycTier Tier { get; private set; }
    public TrackStatus DeveloperKycStatus { get; private set; }
    public TrackStatus BusinessKybStatus { get; private set; }

    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public Guid? ReviewedByAdminId { get; private set; }
    public string? DecisionReason { get; private set; }

    private OnboardingApplication() { } // EF

    public OnboardingApplication(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        Tier = KycTier.Sandbox;
        DeveloperKycStatus = TrackStatus.NotStarted;
        BusinessKybStatus = TrackStatus.NotStarted;
    }

    public bool IsLive => Tier == KycTier.Live;
    public bool BothTracksApproved => DeveloperKycStatus == TrackStatus.Approved && BusinessKybStatus == TrackStatus.Approved;

    // ---- Applicant-driven transitions (submission) -------------------------

    /// <summary>
    /// Applicant submits a track for review. It always lands in <c>UnderReview</c> — automation
    /// pre-verifies what it can, but a human always signs off before Live is granted. Blocked once
    /// a track is already Approved (an admin must reset it first).
    /// </summary>
    public void SubmitTrack(OnboardingTrack track, DateTimeOffset at)
    {
        if (Get(track) == TrackStatus.Approved)
            throw new DomainException($"{track} is already approved.");
        SetTrack(track, TrackStatus.UnderReview);
        SubmittedAtUtc = at;
    }

    public void MarkTrackInProgress(OnboardingTrack track)
    {
        if (Get(track) == TrackStatus.Approved)
            throw new DomainException($"{track} is already approved.");
        SetTrack(track, TrackStatus.InProgress);
    }

    // ---- Admin-driven transitions (review decisions) -----------------------

    public void ApproveTrack(OnboardingTrack track, Guid adminId, DateTimeOffset at)
    {
        RequireReviewable(track);
        SetTrack(track, TrackStatus.Approved);
        Stamp(adminId, at, reason: null);
        PromoteIfEligible();
    }

    public void RejectTrack(OnboardingTrack track, Guid adminId, string reason, DateTimeOffset at)
    {
        RequireReviewable(track);
        SetTrack(track, TrackStatus.Rejected);
        Stamp(adminId, at, DomainException.Require(reason, nameof(reason)));
    }

    public void RequestMoreInfo(OnboardingTrack track, Guid adminId, string reason, DateTimeOffset at)
    {
        RequireReviewable(track);
        SetTrack(track, TrackStatus.MoreInfoNeeded);
        Stamp(adminId, at, DomainException.Require(reason, nameof(reason)));
    }

    // ---- helpers -----------------------------------------------------------

    private void PromoteIfEligible()
    {
        if (BothTracksApproved)
            Tier = KycTier.Live;
    }

    private void RequireReviewable(OnboardingTrack track)
    {
        var status = Get(track);
        if (status is not (TrackStatus.Submitted or TrackStatus.UnderReview))
            throw new DomainException($"{track} is not awaiting review (status: {status}).");
    }

    private void Stamp(Guid adminId, DateTimeOffset at, string? reason)
    {
        if (adminId == Guid.Empty) throw new DomainException("Reviewer admin id is required.");
        ReviewedByAdminId = adminId;
        DecidedAtUtc = at;
        DecisionReason = reason;
    }

    public TrackStatus Get(OnboardingTrack track) =>
        track == OnboardingTrack.DeveloperKyc ? DeveloperKycStatus : BusinessKybStatus;

    private void SetTrack(OnboardingTrack track, TrackStatus status)
    {
        if (track == OnboardingTrack.DeveloperKyc) DeveloperKycStatus = status;
        else BusinessKybStatus = status;
    }
}
