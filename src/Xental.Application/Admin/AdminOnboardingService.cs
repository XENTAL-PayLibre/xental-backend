using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Admin;
using Xental.Domain.Onboarding;

namespace Xental.Application.Admin;

public sealed record AdminOnboardingSummary(
    Guid TenantId, string TenantEmail, string Tier,
    string DeveloperKycStatus, string BusinessKybStatus, DateTimeOffset? SubmittedAtUtc);

public sealed record AdminCheckView(string Kind, string Outcome, string Provider, string? Detail, DateTimeOffset CheckedAtUtc);
public sealed record AdminDocumentView(string Type, string ReviewStatus, Uri DownloadUrl);
public sealed record AdminOnboardingDetail(
    AdminOnboardingSummary Summary,
    IReadOnlyList<AdminCheckView> Checks,
    IReadOnlyList<AdminDocumentView> Documents);

/// <summary>
/// Admin review of onboarding — cross-tenant (the admin plane isn't tenant-scoped, so it bypasses
/// the row-level filter) and fully audited. Approvals flow through the domain, which promotes the
/// tenant to Live only when <b>both</b> tracks are approved.
/// </summary>
public sealed class AdminOnboardingService(
    IApplicationDbContext db,
    IAdminContext admin,
    IDocumentStorage storage,
    IClock clock)
{
    public async Task<IReadOnlyList<AdminOnboardingSummary>> ListAsync(TrackStatus? awaiting, CancellationToken ct = default)
    {
        var query = db.OnboardingApplications.IgnoreQueryFilters().AsNoTracking();
        if (awaiting is TrackStatus s)
            query = query.Where(a => a.DeveloperKycStatus == s || a.BusinessKybStatus == s);
        var apps = await query.OrderByDescending(a => a.SubmittedAtUtc).Take(500).ToListAsync(ct);

        var tenantIds = apps.Select(a => a.TenantId).ToList();
        var emails = await db.Tenants.IgnoreQueryFilters().Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Email, ct);

        return apps.Select(a => Summary(a, emails.GetValueOrDefault(a.TenantId, ""))).ToList();
    }

    public async Task<AdminOnboardingDetail> GetDetailAsync(Guid tenantId, CancellationToken ct = default)
    {
        var app = await LoadAsync(tenantId, ct);
        var email = (await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tenantId, ct))?.Email ?? "";

        var checks = await db.VerificationChecks.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId).OrderByDescending(c => c.CheckedAtUtc).ToListAsync(ct);

        var docs = await db.KycDocuments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId).ToListAsync(ct);
        var docViews = new List<AdminDocumentView>();
        foreach (var d in docs)
            docViews.Add(new AdminDocumentView(d.Type.ToString(), d.ReviewStatus.ToString(),
                await storage.CreateDownloadUrlAsync(d.ObjectKey, TimeSpan.FromMinutes(10), ct)));

        return new AdminOnboardingDetail(
            Summary(app, email),
            checks.Select(c => new AdminCheckView(c.Kind.ToString(), c.Outcome.ToString(), c.Provider, c.Detail, c.CheckedAtUtc)).ToList(),
            docViews);
    }

    public async Task ApproveAsync(Guid tenantId, OnboardingTrack track, CancellationToken ct = default)
    {
        var app = await LoadAsync(tenantId, ct);
        app.ApproveTrack(track, admin.RequireAdminId(), clock.UtcNow);
        Audit("approve", tenantId, track.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task RejectAsync(Guid tenantId, OnboardingTrack track, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("A reason is required.");
        var app = await LoadAsync(tenantId, ct);
        app.RejectTrack(track, admin.RequireAdminId(), reason, clock.UtcNow);
        Audit("reject", tenantId, $"{track}: {reason}");
        await db.SaveChangesAsync(ct);
    }

    public async Task RequestMoreInfoAsync(Guid tenantId, OnboardingTrack track, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("A reason is required.");
        var app = await LoadAsync(tenantId, ct);
        app.RequestMoreInfo(track, admin.RequireAdminId(), reason, clock.UtcNow);
        Audit("request_info", tenantId, $"{track}: {reason}");
        await db.SaveChangesAsync(ct);
    }

    private async Task<OnboardingApplication> LoadAsync(Guid tenantId, CancellationToken ct) =>
        await db.OnboardingApplications.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Onboarding application not found.");

    private void Audit(string action, Guid tenantId, string? detail) =>
        db.AdminAuditLogs.Add(new AdminAuditLog(admin.RequireAdminId(), action, tenantId.ToString(), detail, clock.UtcNow));

    private static AdminOnboardingSummary Summary(OnboardingApplication a, string email) => new(
        a.TenantId, email, a.Tier.ToString(), a.DeveloperKycStatus.ToString(), a.BusinessKybStatus.ToString(), a.SubmittedAtUtc);
}
