using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;

namespace Xental.Api.Auth;

/// <summary>Resolves the current tenant from the authenticated JWT's `tenant_id` claim.</summary>
public sealed class TenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid? TenantId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirst("tenant_id")?.Value;
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public Guid RequireTenantId() =>
        TenantId ?? throw new AuthenticationException("No authenticated tenant on the request.");
}
