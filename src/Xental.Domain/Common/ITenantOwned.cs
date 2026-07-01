namespace Xental.Domain.Common;

/// <summary>
/// Marker for entities scoped to a single tenant. The persistence layer applies
/// a global query filter and write-time enforcement on <see cref="TenantId"/> so
/// cross-tenant access is impossible.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
