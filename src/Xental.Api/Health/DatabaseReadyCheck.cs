using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xental.Infrastructure.Persistence;

namespace Xental.Api.Health;

/// <summary>
/// Readiness probe: verifies the app can reach its database. Distinct from liveness
/// (<c>/health</c>), which only proves the process is up. Load balancers should gate traffic
/// on <c>/ready</c> so a booting instance with no DB connection isn't sent requests.
/// </summary>
public sealed class DatabaseReadyCheck(XentalDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database unreachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database check failed.", ex);
        }
    }
}
