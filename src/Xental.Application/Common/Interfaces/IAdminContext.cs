using Xental.Domain.Admin;

namespace Xental.Application.Common.Interfaces;

/// <summary>The admin of the current request (resolved from the admin JWT).</summary>
public interface IAdminContext
{
    Guid? AdminId { get; }
    AdminRole? Role { get; }
    Guid RequireAdminId();
}
