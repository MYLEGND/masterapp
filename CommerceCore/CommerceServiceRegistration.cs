using Infrastructure.Commerce.Payments;
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
    public static IServiceCollection AddLegendCommerceSessions(this IServiceCollection services)
    {
        services.AddAuthentication(CommerceManagementController.StoreSessionScheme)
            .AddCookie(CommerceManagementController.StoreSessionScheme, options =>
            {
                options.Cookie.Name = "__Host-LEGEND.StoreSession";
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
                options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(1);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            });
        services.AddAuthorization();
        return services;
    }

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
    /// Serves the exact shared commerce internal controller and its existing
    /// modal/view sources. A valid signed scoped website ticket and the canonical
    /// business member capabilities are required for every action. No storefront
    /// payment, public cart, or independent automation worker is registered.
    /// </summary>
    public static IMvcBuilder AddLegendCommerceManagementMvc(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddLegendCommerceMvc();
        builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
            options.Conventions.Add(new CommerceManagementControllerConvention()));
        return builder;
    }

    private sealed class CommerceManagementControllerConvention :
        Microsoft.AspNetCore.Mvc.ApplicationModels.IApplicationModelConvention
    {
        public void Apply(Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel application)
        {
            ApplyInternalRoutes(application);
            var assembly = typeof(StoreController).Assembly;
            foreach (var controller in application.Controllers.Where(c =>
                c.ControllerType.Assembly == assembly &&
                c.ControllerType.AsType() != typeof(CommerceManagementController)).ToArray())
                application.Controllers.Remove(controller);
        }
    }

    /// <summary>
    /// Registers read-only public preview plus the same signed-ticket internal
    /// management controller. Never enables checkout or public cart writes.
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
            ApplyInternalRoutes(application);
            var assembly = typeof(StoreController).Assembly;
            foreach (var controller in application.Controllers
                .Where(c => c.ControllerType.Assembly == assembly &&
                    c.ControllerType.AsType() != typeof(StoreController) &&
                    c.ControllerType.AsType() != typeof(ParfaitPublicPreviewController) &&
                    c.ControllerType.AsType() != typeof(CommerceManagementController)).ToArray())
                application.Controllers.Remove(controller);
        }
    }

    /// <summary>Full public storefront and checkout plus scoped merchant management,
    /// without independently started automation workers.</summary>
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
            ApplyInternalRoutes(application);
            var allowed = new[] {
                typeof(StoreController), typeof(StoreCartController),
                typeof(StoreCheckoutController), typeof(TrainingSubscriptionsController), typeof(MerchantPaymentWebhookController), typeof(ParfaitPublicPreviewController),
                typeof(CommerceManagementController)
            };
            var assembly = typeof(StoreController).Assembly;
            foreach (var controller in application.Controllers.Where(c =>
                c.ControllerType.Assembly == assembly &&
                !allowed.Contains(c.ControllerType.AsType())).ToArray())
                application.Controllers.Remove(controller);
        }
    }

    private static void ApplyInternalRoutes(Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel application)
    {
        var controller = application.Controllers.SingleOrDefault(c => c.ControllerType.AsType() == typeof(CommerceManagementController));
        if (controller is null) return;
        foreach (var action in controller.Actions)
        foreach (var selector in action.Selectors.ToArray())
        {
            var template = selector.AttributeRouteModel?.Template;
            if (template is null || template.StartsWith("/internal/", StringComparison.Ordinal)) continue;
            var methods = selector.ActionConstraints.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>()
                .SelectMany(x => x.HttpMethods).ToArray();
            foreach (var route in CommerceManagementCapabilityPolicy.InternalRoutes.Concat(CommerceManagementCapabilityPolicy.AuthenticationRoutes).Where(x =>
                x.ActionPath == "/commerce/manage/" + template && methods.Contains(x.Method)))
            {
                if (action.Selectors.Any(x => x.AttributeRouteModel?.Template == route.InternalPath)) continue;
                var alias = new Microsoft.AspNetCore.Mvc.ApplicationModels.SelectorModel(selector)
                {
                    AttributeRouteModel = new Microsoft.AspNetCore.Mvc.ApplicationModels.AttributeRouteModel(
                        new Microsoft.AspNetCore.Mvc.RouteAttribute(route.InternalPath)) { Order = 1 }
                };
                action.Selectors.Add(alias);
            }
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
        services.AddHostedService<MerchantPaymentReconciliationWorker>();
        return services;
    }

    public static IServiceCollection AddLegendCommerceCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLegendCommerceCatalogReadOnly();
        services.AddMerchantPayments();
        services.AddScoped<TrainingEnrollmentService>();
        services.AddScoped<MerchantCheckoutService>();
        services.AddScoped<MerchantPaymentCompletionService>();
        services.AddScoped<ParfaitOrderService>();
        services.AddScoped<ParfaitCustomerAutomationService>();
        services.AddScoped<CommerceSignalService>();
        services.AddSingleton<ParfaitInternalAnalyticsCacheStamp>();
        services.AddScoped<ParfaitInternalAnalyticsService>();
        services.AddScoped<ParfaitInternalWorkspaceService>();
        return services;
    }
}
