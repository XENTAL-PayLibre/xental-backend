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
using Xental.Infrastructure.Webhooks;

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

        // Operational alerting: email operators on unhandled 5xx (throttled).
        services.AddOptions<AlertOptions>().Bind(configuration.GetSection(AlertOptions.SectionName));
        services.AddSingleton<IErrorAlerter, EmailErrorAlerter>();

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
        services.AddOptions<Payments.SettlementOptions>().Bind(configuration.GetSection(Payments.SettlementOptions.SectionName));
        services.AddHttpClient("nomba", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<NombaOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });
        services.AddSingleton<INombaTokenProvider, NombaTokenProvider>();
        services.AddScoped<INombaClient, NombaClient>();
        services.AddSingleton<INombaSignatureVerifier, NombaSignatureVerifier>();

        // Dojah identity verification (BVN/NIN/CAC data checks) for KYC automation.
        services.AddOptions<Identity.DojahOptions>().Bind(configuration.GetSection(Identity.DojahOptions.SectionName));
        services.AddHttpClient("dojah", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<Identity.DojahOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddScoped<IIdentityVerifier, Identity.DojahIdentityVerifier>();

        // KYC/KYB document storage (MinIO now, S3 later — same S3 API, config-only switch).
        services.AddOptions<Storage.StorageOptions>().Bind(configuration.GetSection(Storage.StorageOptions.SectionName));
        services.AddSingleton<IDocumentStorage, Storage.S3DocumentStorage>();

        // Admin MFA (TOTP).
        services.AddSingleton<ITotpService, Security.Totp>();

        // Per-tenant tier limits (daily payout cap).
        services.AddOptions<Xental.Application.Common.TierLimitOptions>()
            .Bind(configuration.GetSection(Xental.Application.Common.TierLimitOptions.SectionName));

        // Outbound developer webhooks: at-rest secret encryption, SSRF guard, delivery worker.
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddSingleton<IOutboundUrlGuard, OutboundUrlGuard>();
        services.AddHttpClient("outbound-webhook");
        services.AddHostedService<WebhookDeliveryWorker>();

        // Auto-settlement: sweep fully-paid accounts to the tenant's bank when they opt in.
        services.AddHostedService<Payments.SettlementWorker>();

        // Live Checkout: in-process reconciliation pub/sub feeding the SSE streams.
        services.AddSingleton<IReconciliationNotifier, Payments.InMemoryReconciliationNotifier>();

        return services;
    }
}
