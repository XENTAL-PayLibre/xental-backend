using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Xental.Infrastructure;

/// <summary>
/// Composition root for the Infrastructure layer. Concrete implementations of
/// Application abstractions (persistence, external services, messaging) are
/// wired up here as modules are added.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Database context, repositories and external integrations are
        // registered here per module, reading settings from configuration.
        return services;
    }
}
