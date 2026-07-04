using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Admin;

namespace Xental.Api.Auth;

/// <summary>Resolves the current admin from the authenticated admin JWT's `admin_id`/`admin_role` claims.</summary>
public sealed class AdminContext(IHttpContextAccessor accessor) : IAdminContext
{
    public Guid? AdminId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirst("admin_id")?.Value;
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public AdminRole? Role =>
        Enum.TryParse<AdminRole>(accessor.HttpContext?.User.FindFirst("admin_role")?.Value, out var r) ? r : null;

    public Guid RequireAdminId() =>
        AdminId ?? throw new AuthenticationException("No authenticated admin on the request.");
}
