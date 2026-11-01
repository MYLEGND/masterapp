using Domain.Billing;
using Infrastructure.Billing.Square;
using Infrastructure.Data;
using Infrastructure.Households;
using Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.TryAddScoped<IClientEntraLifecycleService, ClientEntraLifecycleService>();
        services.TryAddScoped<IHouseholdMembershipService, HouseholdMembershipService>();
        // Training entitlements do not use household provisioning. Resolve this scoped
        // dependency only when the household entitlement branch actually needs it.
        services.AddScoped<IBillingEntitlementService>(provider => new BillingEntitlementService(
            provider.GetRequiredService<MasterAppDbContext>(),
            new Lazy<IHouseholdMembershipService>(() => provider.GetRequiredService<IHouseholdMembershipService>())));
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
