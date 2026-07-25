using Domain.Billing;
using Infrastructure.Billing.Square;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Billing;

public static class BillingServiceCollectionExtensions
{
    public static IServiceCollection AddMasterAppBilling(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(SquareBillingOptions.FromConfiguration(configuration));
        services.AddSingleton(ClientSubscriptionActivationPolicyOptions.FromConfiguration(configuration));

        services.AddHttpClient("MasterAppBilling.Square", (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<SquareBillingOptions>();
            client.BaseAddress = new Uri(options.GetBaseUrl());
            client.Timeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds));
        });

        services.AddScoped<IBillingGateway, SquareBillingGateway>();
        services.AddScoped<IBillingOrchestrator, MasterAppBillingOrchestrator>();
        services.AddScoped<IBillingEntitlementService, BillingEntitlementService>();
        services.AddScoped<IClientPaymentMethodService, ClientPaymentMethodService>();
        services.AddScoped<IClientBillingNotificationService, ClientBillingNotificationService>();
        services.AddScoped<IClientSubscriptionActivationPolicyService, ClientSubscriptionActivationPolicyService>();
        services.AddScoped<IBillingProviderEventProcessor, BillingProviderEventProcessor>();
        services.AddScoped<IBillingReconciliationService, BillingReconciliationService>();
        services.AddScoped<IBillingWebhookSignatureValidator, SquareBillingWebhookSignatureValidator>();
        services.AddScoped<IBillingWebhookIngressService, BillingWebhookIngressService>();

        return services;
    }
}
