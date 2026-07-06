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

    /// <summary>Either plane — read/manage endpoints usable by both an API key and a dashboard user.</summary>
    public const string ApiOrDashboard = "api-or-dashboard";

    /// <summary>Admin plane. <see cref="Admin"/> = any admin; <see cref="SuperAdmin"/> requires role SuperAdmin.</summary>
    public const string Admin = "admin";
    public const string SuperAdmin = "super-admin";

    // Role-gated dashboard policies (the session's team role: Owner / Admin / Developer / Employee).
    public const string TeamManage = "team-manage";          // Owner, Admin
    public const string ManageKeys = "manage-keys";          // Owner, Admin, Developer
    public const string ManageSettings = "manage-settings";  // Owner, Admin
    /// <summary>Mutate billing schedules: an API key (integrator), or a dashboard Owner/Admin — not an Employee.</summary>
    public const string ManageBilling = "manage-billing";

    /// <summary>Provision / operate resources from either plane: an API key, or a dashboard
    /// Owner/Admin/Developer. Used for DVA provisioning, checkout sessions, and the sandbox.</summary>
    public const string Provision = "provision";

    /// <summary>Move money from either plane: an API key, or a dashboard Owner/Admin (never Employee).
    /// Used for transfers, refunds, and escrow hold/release.</summary>
    public const string MovePayouts = "move-payouts";

    public const string ScopeClaim = "scope";
    public const string AdminRoleClaim = "admin_role";
    public const string RoleClaim = "team_role";
}
