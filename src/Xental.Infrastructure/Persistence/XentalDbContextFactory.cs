using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Time;

namespace Xental.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by `dotnet ef` so migrations can be created without
/// booting the full application host. Not used at runtime.
/// </summary>
public sealed class XentalDbContextFactory : IDesignTimeDbContextFactory<XentalDbContext>
{
    public XentalDbContext CreateDbContext(string[] args)
    {
        // Honor the runtime connection string when present (e.g. local Postgres),
        // otherwise fall back to a sensible local default.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=xental;Username=xental_app;Password=changeme";

        var options = new DbContextOptionsBuilder<XentalDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new XentalDbContext(options, new NullTenantContext(), new SystemClock());
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public Guid RequireTenantId() => throw new InvalidOperationException();
    }
}
