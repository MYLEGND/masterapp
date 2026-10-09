using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Infrastructure.Leads;

/// <summary>
/// Canonical registration boundary for website notification transport and
/// durable notification recovery. Public website hosts may register only the
/// transport; the platform control-plane host owns the background workers.
/// </summary>
public static class WebsiteLeadServiceRegistration
{
    public static IServiceCollection AddWebsiteLeadNotificationTransport(this IServiceCollection services)
    {
        services.TryAddScoped<IWebsiteInquiryEmailSender, GraphWebsiteInquiryEmailSender>();
        return services;
    }

    public static IServiceCollection AddWebsiteLeadBackgroundWorkers(this IServiceCollection services)
    {
        services.AddWebsiteLeadNotificationTransport();
        services.TryAddScoped<BusinessInquiryNotificationService>();
        services.TryAddScoped<WebsiteLeadAppNotificationService>();
        services.AddHostedService<BusinessInquiryNotificationWorker>();
        services.AddHostedService<WebsiteLeadNotificationRecoveryWorker>();
        services.AddHostedService<WebsiteLeadAppNotificationWorker>();
        return services;
    }
}
