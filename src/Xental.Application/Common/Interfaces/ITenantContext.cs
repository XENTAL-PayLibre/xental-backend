namespace Xental.Application.Common.Interfaces;

/// <summary>The tenant of the current request (resolved from the JWT).</summary>
public interface ITenantContext
{
    /// <summary>Current tenant id, or null when the request is unauthenticated.</summary>
    Guid? TenantId { get; }

    /// <summary>Returns the current tenant id or throws if there is none.</summary>
    Guid RequireTenantId();
}
