namespace Xental.Application.Common.Interfaces;

/// <summary>
/// Resolves whether the current request operates in <b>live</b> (real money) or <b>test</b> mode —
/// the piece a dashboard-plane request otherwise lacks. API tokens carry a <c>key_mode</c> claim;
/// dashboard tokens declare intent via the <c>X-Xental-Mode</c> header (default test), and live is
/// only granted to tenants with an approved live onboarding. Lets provisioning / money-movement work
/// identically from both planes without a controller reading raw claims.
/// </summary>
public interface IModeContext
{
    /// <summary>True if the request is authorised to act in live mode. Dashboard requests that ask for
    /// live without approved KYC throw rather than silently downgrading.</summary>
    Task<bool> IsLiveAsync(CancellationToken ct = default);
}
