using System.Net;
using Infrastructure.Leads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Infrastructure.Reception;

public static class ReceptionistServiceRegistration
{
    /// <summary>Management may run on workspace hosts without starting the voice worker.</summary>
    public static IServiceCollection AddReceptionistManagement(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddScoped<ReceptionistOperatorService>();
        return services;
    }

    /// <summary>Register only in the existing control-plane host. Business setup remains in the shared marketing authority.</summary>
    public static IServiceCollection AddReceptionistRuntime(this IServiceCollection services)
    {
        services.AddConfirmedInquiryPersistence();
        services.AddReceptionistManagement();
        services.TryAddScoped<Infrastructure.WebsiteEditing.WebsiteDomainService>();
        services.TryAddScoped<Infrastructure.WebsiteEditing.PublicWebsiteRuntimeScopeResolver>();
        services.AddCors(options => options.AddPolicy(ReceptionistBrowserController.CorsPolicy, policy => policy
            .WithOrigins("https://mylegnd.com", "https://www.mylegnd.com", "https://protect.mylegnd.com", "https://portal.mylegnd.com")
            .AllowAnyHeader().WithMethods("POST")));
        services.AddScoped<Microsoft.AspNetCore.Cors.Infrastructure.ICorsPolicyProvider, Infrastructure.WebsiteEditing.WebsiteCorsPolicyProvider>();
        services.AddScoped<ReceptionistRuntimeAuthority>();
        services.AddScoped<IOpenAiReceptionistClient, OpenAiReceptionistClient>();
        services.AddScoped<ReceptionistApplicationServices>();
        services.AddHttpClient(OpenAiReceptionistClient.ClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 64
            });
        // No resilience/retry handler may wrap a provider control attempt. Its SQL intent owns uncertainty.
        services.AddHostedService<ReceptionistRuntimeWorker>();
        return services;
    }
}
