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
        services.AddScoped<Team.TeamService>();
        services.AddScoped<Payments.VirtualAccountService>();
        services.AddScoped<Payments.NombaWebhookService>();
        services.AddScoped<Payments.RiskEvaluator>();
        services.AddScoped<Payments.TransactionQueryService>();
        services.AddScoped<Payments.TransferService>();
        services.AddScoped<Payments.RefundService>();
        services.AddScoped<Payments.InsightsService>();
        services.AddScoped<Payments.SettlementConfigService>();
        services.AddScoped<Payments.SplitSettlementService>();
        services.AddScoped<Payments.RuleEngine>();
        services.AddScoped<Payments.MoneyRuleService>();
        services.AddScoped<Payments.FlowEngine>();
        services.AddScoped<Payments.FlowService>();
        services.AddScoped<Assistant.CopilotService>();
        services.AddScoped<Payments.SandboxSimulationService>();
        services.AddScoped<Payments.CheckoutService>();
        services.AddScoped<Billing.BillingService>();
        services.AddScoped<Onboarding.OnboardingService>();
        services.AddScoped<Onboarding.DeveloperKycService>();
        services.AddScoped<Onboarding.BusinessKybService>();
        services.AddScoped<Admin.AdminAuthService>();
        services.AddScoped<Admin.AdminOnboardingService>();
        services.AddScoped<Admin.AdminManagementService>();
        services.AddScoped<Admin.AdminReconciliationService>();
        services.AddScoped<Webhooks.OutboundEventPublisher>();
        services.AddScoped<Webhooks.WebhookEndpointService>();
        return services;
    }
}
