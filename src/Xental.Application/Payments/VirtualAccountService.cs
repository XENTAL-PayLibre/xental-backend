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
        string accountRef, string name, string? email, string? phone,
        long? expectedAmountKobo, DateTimeOffset? expiryDateUtc, string? subMerchantRef, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountRef))
            throw new ValidationException("accountRef is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Customer name is required.");
        if (expectedAmountKobo is < 0)
            throw new ValidationException("expectedAmount cannot be negative.");

        var tenantId = tenantContext.RequireTenantId();
        var reference = accountRef.Trim();

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

        var provisioned = await nomba.CreateVirtualAccountAsync(reference, name.Trim(), email, phone, ct);

        var account = new VirtualAccount(
            tenantId, customer.Id, reference,
            provisioned.AccountNumber, provisioned.BankName, provisioned.AccountName,
            provisioned.ProviderAccountId, expectedAmountKobo, expiryDateUtc, subMerchantId);
        db.VirtualAccounts.Add(account);

        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<VirtualAccount> GetByReferenceAsync(string accountRef, CancellationToken ct = default)
    {
        var reference = (accountRef ?? string.Empty).Trim();
        return await db.VirtualAccounts.AsNoTracking().FirstOrDefaultAsync(v => v.Reference == reference, ct)
            ?? throw new NotFoundException($"No virtual account for accountRef '{reference}'.");
    }
}
