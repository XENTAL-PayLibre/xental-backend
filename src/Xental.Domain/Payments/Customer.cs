using Xental.Domain.Common;

namespace Xental.Domain.Payments;

/// <summary>
/// A payer (e.g. a student/tenant/subscriber) owned by a developer account. Identified
/// by a stable, developer-supplied <see cref="Reference"/> (accountRef), unique per tenant.
/// A customer is issued a persistent virtual account (NUBAN).
/// </summary>
public sealed class Customer : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string Reference { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string? Email { get; private set; }
    public string? Phone { get; private set; }

    private Customer() { } // EF

    public Customer(Guid tenantId, string reference, string name, string? email = null, string? phone = null)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required.");
        TenantId = tenantId;
        Reference = DomainException.Require(reference, nameof(reference));
        Name = DomainException.Require(name, nameof(name));
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
    }
}
