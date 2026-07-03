namespace Xental.Api.Authorization;

/// <summary>
/// Named authorization policies. Tokens carry a <c>scope</c> claim that separates the
/// two planes: <c>dashboard</c> tokens (email/password login) manage API keys, while
/// <c>api</c> tokens (client-credentials) call the payments API.
/// </summary>
public static class AuthPolicies
{
    public const string Dashboard = "dashboard";
    public const string Api = "api";

    /// <summary>Admin plane. <see cref="Admin"/> = any admin; <see cref="SuperAdmin"/> requires role SuperAdmin.</summary>
    public const string Admin = "admin";
    public const string SuperAdmin = "super-admin";

    public const string ScopeClaim = "scope";
    public const string AdminRoleClaim = "admin_role";
}
