using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Merchants;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

/// <summary>A virtual account enriched with its customer's contact details for the dashboard.</summary>
public sealed record VirtualAccountView(VirtualAccount Account, string? CustomerName, string? CustomerEmail, string? CustomerPhone);

/// <summary>
/// Provisions and reads persistent NUBANs. Creating a virtual account finds-or-creates the
/// customer (by developer-supplied ref), asks Nomba for a NUBAN, and persists the mapping.
/// All operations are tenant-scoped by the DbContext query filter.
/// </summary>
public sealed class VirtualAccountService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    INombaClient nomba,
    IEmailSender mailer)
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

        // Best-effort: tell the customer where to pay + how much, sent under the merchant's brand.
        if (!string.IsNullOrWhiteSpace(customer.Email))
            await mailer.SendCustomerAccountDetailsAsync(
                customer.Email!, tenant.DisplayBrand,
                provisioned.AccountNumber, provisioned.BankName, provisioned.AccountName,
                expectedAmountKobo, ct);

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

    public async Task<VirtualAccountView> GetByReferenceAsync(string accountRef, CancellationToken ct = default)
    {
        var reference = (accountRef ?? string.Empty).Trim();
        var view = await (
            from v in db.VirtualAccounts.AsNoTracking().Where(v => v.Reference == reference)
            join c in db.Customers.AsNoTracking() on v.CustomerId equals c.Id into cj
            from c in cj.DefaultIfEmpty()
            select new VirtualAccountView(v, c!.Name, c.Email, c.Phone)).FirstOrDefaultAsync(ct);
        return view ?? throw new NotFoundException($"No virtual account for accountRef '{reference}'.");
    }

    /// <summary>The tenant's virtual accounts, most recent first (optionally scoped to a sub-merchant),
    /// each with its customer's name/email/phone for the dashboard.</summary>
    public async Task<IReadOnlyList<VirtualAccountView>> ListAsync(string? subMerchantRef = null, int take = 50, CancellationToken ct = default)
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
        return await (
            from v in q
            join c in db.Customers.AsNoTracking() on v.CustomerId equals c.Id into cj
            from c in cj.DefaultIfEmpty()
            orderby v.CreatedAtUtc descending
            select new VirtualAccountView(v, c!.Name, c.Email, c.Phone))
            .Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
    }

    /// <summary>Delete a virtual account (and its customer, if this was the customer's only account).
    /// Refuses accounts that have any payment activity — those must be kept for the ledger.</summary>
    public async Task DeleteAsync(string accountRef, CancellationToken ct = default)
    {
        tenantContext.RequireTenantId();
        var reference = (accountRef ?? string.Empty).Trim();
        var va = await db.VirtualAccounts.FirstOrDefaultAsync(v => v.Reference == reference, ct)
            ?? throw new NotFoundException($"No virtual account for accountRef '{reference}'.");

        var hasActivity = va.AmountPaidKobo > 0 || await db.Transactions.AnyAsync(t => t.VirtualAccountId == va.Id, ct);
        if (hasActivity)
            throw new ConflictException("This customer has payment activity and cannot be deleted.");

        var customerId = va.CustomerId;
        db.VirtualAccounts.Remove(va);

        // Drop the customer too when it has no other virtual accounts.
        var customerHasOtherAccounts = await db.VirtualAccounts.AnyAsync(v => v.CustomerId == customerId && v.Id != va.Id, ct);
        if (!customerHasOtherAccounts)
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
            if (customer is not null) db.Customers.Remove(customer);
        }

        await db.SaveChangesAsync(ct);
    }
}
