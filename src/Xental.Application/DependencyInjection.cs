using Microsoft.Extensions.DependencyInjection;

namespace Xental.Application;

/// <summary>
/// Composition root for the Application layer. Each module registers its
/// handlers, validators and services from here as the system grows.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Application-wide registrations (validators, pipeline behaviours,
        // mappers, use-case handlers) are added here per module.
        return services;
    }
}
