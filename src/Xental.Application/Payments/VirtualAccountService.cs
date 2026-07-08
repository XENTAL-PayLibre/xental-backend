using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Merchants;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>
/// Provisions and reads persistent NUBANs. Creating a virtual account finds-or-creates the
/// customer (by developer-supplied ref), asks Nomba for a NUBAN, and persists the mapping.
/// All operations are tenant-scoped by the DbContext query filter.
/// </summary>
public sealed class VirtualAccountService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    INombaClient nomba)
{
    public async Task<VirtualAccount> CreateAsync(
        string? accountRef, string name, string? email, string? phone,
        long? expectedAmountKobo, DateTimeOffset? expiryDateUtc, string? subMerchantRef, bool testMode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Customer name is required.");
        if (expectedAmountKobo is < 0)
            throw new ValidationException("expectedAmount cannot be negative.");

        var tenantId = tenantContext.RequireTenantId();

        // Only a verified account may create customers/NUBANs.
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new NotFoundException("Account not found.");
        if (!tenant.EmailVerified)
            throw new EmailNotVerifiedException("Verify your email before creating a customer.");

        // The customer reference is generated server-side when the caller doesn't supply one.
        var reference = string.IsNullOrWhiteSpace(accountRef) ? "cust_" + Guid.NewGuid().ToString("N")[..16] : accountRef.Trim();

        // Optionally attach the NUBAN to a sub-merchant, which routes its settlement to that
        // sub-merchant's payout account.
        Guid? subMerchantId = null;
        if (!string.IsNullOrWhiteSpace(subMerchantRef))
        {
            var subRef = subMerchantRef.Trim();
            var sub = await db.SubMerchants.FirstOrDefaultAsync(s => s.Reference == subRef && s.Status == SubMerchantStatus.Active, ct)
                ?? throw new NotFoundException($"No active sub-merchant with reference '{subRef}'.");
            subMerchantId = sub.Id;
        }

        if (await db.VirtualAccounts.AnyAsync(v => v.Reference == reference, ct))
            throw new ConflictException($"A virtual account already exists for accountRef '{reference}'.");

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Reference == reference, ct);
        if (customer is null)
        {
            customer = new Customer(tenantId, reference, name.Trim(), email, phone);
            db.Customers.Add(customer);
        }

        // Test-mode keys mint a simulated NUBAN locally — no real provider call. This keeps sandbox
        // provisioning unlimited (Nomba's sandbox caps virtual accounts per account holder) and free of
        // any upstream dependency; the sandbox deposit simulator credits these exactly like live ones.
        var provisioned = testMode
            ? await SimulateNubanAsync(reference, name.Trim(), ct)
            : await nomba.CreateVirtualAccountAsync(reference, name.Trim(), email, phone, ct);

        var account = new VirtualAccount(
            tenantId, customer.Id, reference,
            provisioned.AccountNumber, provisioned.BankName, provisioned.AccountName,
            provisioned.ProviderAccountId, expectedAmountKobo, expiryDateUtc, subMerchantId);
        db.VirtualAccounts.Add(account);

        await db.SaveChangesAsync(ct);
        return account;
    }

    /// <summary>Allocate a unique, sandbox-only NUBAN (99-prefixed) without calling the provider.</summary>
    private async Task<ProvisionedVirtualAccount> SimulateNubanAsync(string reference, string name, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var number = "99" + Random.Shared.NextInt64(0, 100_000_000).ToString("D8");
            if (!await db.VirtualAccounts.IgnoreQueryFilters().AnyAsync(v => v.AccountNumber == number, ct))
                return new ProvisionedVirtualAccount(number, "Xental Sandbox Bank", name, $"sandbox-{reference}");
        }
        throw new ValidationException("Could not allocate a sandbox account number. Please retry.");
    }

    public async Task<VirtualAccount> GetByReferenceAsync(string accountRef, CancellationToken ct = default)
    {
        var reference = (accountRef ?? string.Empty).Trim();
        return await db.VirtualAccounts.AsNoTracking().FirstOrDefaultAsync(v => v.Reference == reference, ct)
            ?? throw new NotFoundException($"No virtual account for accountRef '{reference}'.");
    }

    /// <summary>The tenant's virtual accounts, most recent first (optionally scoped to a sub-merchant).</summary>
    public async Task<IReadOnlyList<VirtualAccount>> ListAsync(string? subMerchantRef = null, int take = 50, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        var q = db.VirtualAccounts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(subMerchantRef))
        {
            var subRef = subMerchantRef.Trim();
            var sub = await db.SubMerchants.AsNoTracking().FirstOrDefaultAsync(s => s.Reference == subRef, ct)
                ?? throw new NotFoundException($"No sub-merchant with reference '{subRef}'.");
            q = q.Where(v => v.SubMerchantId == sub.Id);
        }
        return await q.OrderByDescending(v => v.CreatedAtUtc).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
    }
}
