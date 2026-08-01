using Domain.Messaging;
using Domain.JourneyCircles;
using Domain.Moderation;
using Infrastructure.JourneyCircles;
using Infrastructure.Moderation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddMasterAppMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IConfiguration>(configuration);
        services.AddScoped<IMessagingService, MessagingService>();
        services.AddScoped<IControlledResourceAccessService, ControlledResourceAccessService>();
        services.AddScoped<ITranslationService, AzureTranslatorService>();
        services.AddHttpClient("AzureTranslator", client =>
        {
            // Provider failures must never hold up message delivery.
            client.Timeout = TimeSpan.FromSeconds(6);
        });
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
