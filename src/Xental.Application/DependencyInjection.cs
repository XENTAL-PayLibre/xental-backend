using Microsoft.Extensions.DependencyInjection;
using Xental.Application.ApiKeys;
using Xental.Application.Authentication;
using Xental.Application.Merchants;
using Xental.Application.Tenancy;

namespace Xental.Application;

/// <summary>
/// Composition root for the Application layer. Use-case services are registered here.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<SessionIssuer>();
        services.AddScoped<DeveloperRegistrationService>();
        services.AddScoped<DeveloperAuthService>();
        services.AddScoped<DeveloperProfileService>();
        services.AddScoped<EmailVerificationService>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<OAuthLoginService>();
        services.AddScoped<AuthenticationService>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<SubMerchantService>();
        services.AddScoped<Payments.VirtualAccountService>();
        services.AddScoped<Payments.NombaWebhookService>();
        services.AddScoped<Payments.RiskEvaluator>();
        services.AddScoped<Payments.TransactionQueryService>();
        services.AddScoped<Payments.TransferService>();
        services.AddScoped<Payments.InsightsService>();
        services.AddScoped<Payments.SettlementConfigService>();
        services.AddScoped<Webhooks.OutboundEventPublisher>();
        services.AddScoped<Webhooks.WebhookEndpointService>();
        return services;
    }
}
