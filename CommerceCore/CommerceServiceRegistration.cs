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
    /// <summary>
    /// Registers GET-only storefront MVC actions. Never include checkout,
    /// cart writes, payment capture, or merchant management in preview.
    /// </summary>
    public static IMvcBuilder AddLegendCommercePreviewMvc(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddLegendCommerceMvc();
        builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
            options.Conventions.Add(new CommercePreviewControllerConvention()));
        return builder;
    }

    private sealed class CommercePreviewControllerConvention :
        Microsoft.AspNetCore.Mvc.ApplicationModels.IApplicationModelConvention
    {
        public void Apply(Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel application)
        {
            var assembly = typeof(StoreController).Assembly;
            foreach (var controller in application.Controllers
                .Where(c => c.ControllerType.Assembly == assembly &&
                    c.ControllerType.AsType() != typeof(StoreController) &&
                    c.ControllerType.AsType() != typeof(ParfaitPublicPreviewController)).ToArray())
                application.Controllers.Remove(controller);
        }
    }

    /// <summary>Full public storefront and checkout, but no merchant management
    /// endpoints or independently started automation workers.</summary>
    public static IMvcBuilder AddLegendCommerceCutoverMvc(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddLegendCommerceMvc();
        builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
            options.Conventions.Add(new CommerceCutoverControllerConvention()));
        return builder;
    }

    private sealed class CommerceCutoverControllerConvention :
        Microsoft.AspNetCore.Mvc.ApplicationModels.IApplicationModelConvention
    {
        public void Apply(Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel application)
        {
            var allowed = new[] {
                typeof(StoreController), typeof(StoreCartController),
                typeof(StoreCheckoutController), typeof(ParfaitPublicPreviewController)
            };
            var assembly = typeof(StoreController).Assembly;
            foreach (var controller in application.Controllers.Where(c =>
                c.ControllerType.Assembly == assembly &&
                !allowed.Contains(c.ControllerType.AsType())).ToArray())
                application.Controllers.Remove(controller);
        }
    }

    public static IMvcBuilder ExcludeLegendCommerceMvc(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var assembly = typeof(StoreController).Assembly;
        foreach (var part in builder.PartManager.ApplicationParts.OfType<AssemblyPart>()
            .Where(part => part.Assembly == assembly).ToArray())
            builder.PartManager.ApplicationParts.Remove(part);
        return builder;
    }

    /// <summary>
    /// Explicitly grants the current commerce origin one SQL-arbitrated email
    /// dispatcher. Read-only website hosts intentionally do not register it.
    /// </summary>
    public static IServiceCollection AddLegendCommerceAutomationWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ICommerceAutomationDispatchLease, SqlCommerceAutomationDispatchLease>();
        services.AddHostedService<ParfaitCustomerAutomationHostedService>();
        return services;
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
