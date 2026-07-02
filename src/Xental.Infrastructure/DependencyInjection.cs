using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Authentication;
using Xental.Infrastructure.Configuration;
using Xental.Infrastructure.Messaging;
using Xental.Infrastructure.Nomba;
using Xental.Infrastructure.Persistence;
using Xental.Infrastructure.Security;
using Xental.Infrastructure.Time;

namespace Xental.Infrastructure;

/// <summary>Composition root for the Infrastructure layer.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Persistence
        services.AddDbContext<XentalDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Default")));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<XentalDbContext>());

        // Primitives
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretHasher, Pbkdf2SecretHasher>();
        services.AddSingleton<ITokenGenerator, SecureTokenGenerator>();

        // Password hashing (bcrypt) for developer accounts.
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName));
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();

        // Single-use magic-link tokens (verification + password reset).
        services.AddSingleton<ITokenHasher, Sha256TokenHasher>();

        // App URLs + email link building.
        services.AddOptions<AppOptions>().Bind(configuration.GetSection(AppOptions.SectionName));
        services.AddSingleton<ILinkBuilder, AppLinkBuilder>();

        // Transactional email (Resend).
        services.AddOptions<ResendOptions>().Bind(configuration.GetSection(ResendOptions.SectionName));
        services.AddHttpClient("resend");
        services.AddScoped<IEmailSender, ResendEmailSender>();

        // Social login providers (Google, GitHub). Options bound under Auth:*.
        services.AddHttpClient("oauth");
        services.AddScoped<IExternalIdentityProvider, GoogleIdentityProvider>();
        services.AddScoped<IExternalIdentityProvider, GitHubIdentityProvider>();

        // JWT
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<IJwtTokenService, JwtTokenService>();

        // Nomba auth primitive (token provider caches the 30-min token across requests).
        // The virtual-account client that uses it is added in Phase 2.
        services.AddOptions<NombaOptions>().Bind(configuration.GetSection(NombaOptions.SectionName));
        services.AddHttpClient("nomba", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<NombaOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });
        services.AddSingleton<INombaTokenProvider, NombaTokenProvider>();
        services.AddScoped<INombaClient, NombaClient>();
        services.AddSingleton<INombaSignatureVerifier, NombaSignatureVerifier>();

        return services;
    }
}
