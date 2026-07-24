using Domain.Messaging;
using Domain.JourneyCircles;
using Domain.Moderation;
using Infrastructure.JourneyCircles;
using Infrastructure.Moderation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddMasterAppMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<IMessagingService, MessagingService>();
        services.AddSingleton<ICommunityTextModerationService>(_ => new CommunityTextModerationService(configuration));
        services.AddScoped<IJourneyCirclesService, JourneyCirclesService>();
        services.AddScoped<IMessagingProfileImageResolver, MessagingProfileImageResolver>();
        services.AddSingleton<IMessagingContactKeyProtector, MessagingContactKeyProtector>();
        services.AddSingleton<IMessageAttachmentStorage, MessagingAttachmentStorage>();
        services.AddSingleton<IMessagingRealtimePublisher, MessagingRealtimePublisher>();
        services.AddHostedService<MessagingRealtimeNotificationHostedService>();

        return services;
    }
}
