using Infrastructure.Commerce;
using ParfaitApp.Controllers;
using Microsoft.Extensions.DependencyInjection;
using ParfaitApp.Services;

namespace Legend.Commerce;

/// <summary>
/// One registration contract for the shared, CommerceBusinessId-scoped
/// catalog/order/checkout/automation and dashboard engines. Hosts retain
/// their own identity, payment-provider, media-root and mail integrations.
/// </summary>
public static class CommerceServiceRegistration
{
    /// <summary>Registers exactly the same shared controllers with any MVC host.</summary>
    public static IMvcBuilder AddLegendCommerceMvc(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddApplicationPart(typeof(StoreController).Assembly);
    }

    public static IServiceCollection AddLegendCommerceCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ParfaitStoragePaths>();
        services.AddScoped<ParfaitProductService>();
        services.AddScoped<ParfaitOrderService>();
        services.AddScoped<ParfaitCustomerAutomationService>();
        services.AddScoped<CommerceSignalService>();
        services.AddSingleton<ParfaitInternalAnalyticsCacheStamp>();
        services.AddScoped<ParfaitInternalAnalyticsService>();
        services.AddScoped<ParfaitInternalWorkspaceService>();
        return services;
    }
}
