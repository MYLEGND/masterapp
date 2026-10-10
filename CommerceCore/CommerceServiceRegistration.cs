using Infrastructure.Commerce;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using ParfaitApp.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        var assembly = typeof(StoreController).Assembly;
        return builder.PartManager.ApplicationParts.OfType<AssemblyPart>()
            .Any(part => part.Assembly == assembly)
            ? builder
            : builder.AddApplicationPart(assembly);
    }

    /// <summary>
    /// Registers catalog reads without payment, automation, admin endpoints,
    /// background jobs, or any public storefront MVC route.
    /// </summary>
    public static IServiceCollection AddLegendCommerceCatalogReadOnly(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ParfaitStoragePaths>();
        services.TryAddScoped<ParfaitProductService>();
        services.TryAddScoped<ICommerceCatalogReader>(
            provider => provider.GetRequiredService<ParfaitProductService>());
        return services;
    }

    /// <summary>
    /// Excludes commerce endpoints from hosts consuming catalog reads only.
    /// Routes are registered later through explicit AddLegendCommerceMvc.
    /// </summary>
    public static IMvcBuilder ExcludeLegendCommerceMvc(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var assembly = typeof(StoreController).Assembly;
        foreach (var part in builder.PartManager.ApplicationParts.OfType<AssemblyPart>()
            .Where(part => part.Assembly == assembly).ToArray())
            builder.PartManager.ApplicationParts.Remove(part);
        return builder;
    }

    public static IServiceCollection AddLegendCommerceCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLegendCommerceCatalogReadOnly();
        services.AddScoped<ParfaitOrderService>();
        services.AddScoped<ParfaitCustomerAutomationService>();
        services.AddScoped<CommerceSignalService>();
        services.AddSingleton<ParfaitInternalAnalyticsCacheStamp>();
        services.AddScoped<ParfaitInternalAnalyticsService>();
        services.AddScoped<ParfaitInternalWorkspaceService>();
        return services;
    }
}
