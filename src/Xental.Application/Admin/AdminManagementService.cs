using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Admin;

namespace Xental.Application.Admin;

/// <summary>
/// SuperAdmin-only admin management + self-service MFA enrollment. Creating admins is gated by the
/// SuperAdmin policy at the endpoint; every action is audited.
/// </summary>
public sealed class AdminManagementService(
    IApplicationDbContext db,
    IPasswordHasher passwords,
    ITotpService totp,
    ISecretProtector protector,
    IAdminContext admin,
    IClock clock)
{
    public async Task<Guid> CreateAdminAsync(string email, string password, AdminRole role, CancellationToken ct = default)
    {
        var normalized = AdminUser.NormalizeEmail(email);
        if (password is null || password.Length < 12)
            throw new ValidationException("Admin password must be at least 12 characters.");
        if (await db.AdminUsers.AnyAsync(a => a.Email == normalized, ct))
            throw new ConflictException("An admin with that email already exists.");

        var user = new AdminUser(normalized, passwords.Hash(password), role);
        db.AdminUsers.Add(user);
        db.AdminAuditLogs.Add(new AdminAuditLog(admin.RequireAdminId(), "create_admin", null, $"{normalized} ({role})", clock.UtcNow));
        await db.SaveChangesAsync(ct);
        return user.Id;
    }

    public async Task SetStatusAsync(Guid adminId, bool active, CancellationToken ct = default)
    {
        var self = admin.RequireAdminId();
        if (adminId == self && !active) throw new ValidationException("You cannot disable your own account.");
        var user = await db.AdminUsers.FirstOrDefaultAsync(a => a.Id == adminId, ct)
            ?? throw new NotFoundException("Admin not found.");
        if (active) user.Reactivate(); else user.Disable();
        db.AdminAuditLogs.Add(new AdminAuditLog(self, active ? "enable_admin" : "disable_admin", null, adminId.ToString(), clock.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Enroll MFA for the current admin. Returns the otpauth:// URI for the QR code.</summary>
    public async Task<string> EnrollMfaAsync(CancellationToken ct = default)
    {
        var adminId = admin.RequireAdminId();
        var user = await db.AdminUsers.FirstOrDefaultAsync(a => a.Id == adminId, ct)
            ?? throw new NotFoundException("Admin not found.");
        var secret = totp.GenerateSecret();
        user.EnrollMfa(protector.Protect(secret));
        await db.SaveChangesAsync(ct);
        return totp.BuildOtpAuthUri(secret, user.Email, "Xental Admin");
    }
}
