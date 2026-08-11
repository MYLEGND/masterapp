using Domain.Messaging;
using Domain.JourneyCircles;
using Domain.Moderation;
using Infrastructure.JourneyCircles;
using Infrastructure.Moderation;
using Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;

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
        services.AddScoped<ILegendLanguageRegistry, LegendLanguageRegistry>();
        services.AddScoped<ITranslationCapacityAuthority, TranslationCapacityAuthority>();
        services.AddScoped<ITranslationDemandRecorder, TranslationDemandRecorder>();
        services.AddScoped<ITranslationSystemUsageRecorder, TranslationSystemUsageRecorder>();
        services.AddScoped<ILegendConnectOperationalEventWriter, LegendConnectOperationalEventWriter>();
        services.AddScoped<ILegendConnectTranslationIntelligence, LegendConnectTranslationIntelligence>();
        services.AddScoped<ITranslationProvider, AzureTranslatorService>();
        services.AddScoped<ITranslationService, LegendConnectTranslationRouter>();
        services.AddScoped<ITranslationLearningPublisher, LegendTranslationLearningPublisher>();
        services.AddScoped<LegendConnectCorpusService>();
        services.AddScoped<LegendConnectAutonomousGapPlanner>();
        services.AddScoped<LegendConnectAutonomousLearningService>();
        services.AddScoped<ILegendConnectOperations, LegendConnectOperations>();
        services.AddHttpClient("AzureTranslator", client =>
        {
            // Provider failures must never hold up message delivery.
            client.Timeout = TimeSpan.FromSeconds(6);
        });
        services.AddSingleton<ICommunityTextModerationService>(_ => new CommunityTextModerationService(configuration));
        services.AddScoped<ICommunitySafetyService, CommunitySafetyService>();
        services.AddScoped<IJourneyCirclesService, JourneyCirclesService>();
        services.AddScoped<MessagingProfileImageResolver>();
        services.AddScoped<IMessagingProfileImageResolver>(provider =>
            provider.GetRequiredService<MessagingProfileImageResolver>());
        services.AddScoped<IProfileImageWriter>(provider =>
            provider.GetRequiredService<MessagingProfileImageResolver>());
        services.AddSingleton<IApplePushDeliverySignal, ApplePushDeliverySignal>();
        services.AddScoped<INotificationEngine, NotificationEngine>();
        services.AddSingleton<INotificationRealtimePublisher, NotificationRealtimePublisher>();
        services.AddHttpClient("ApplePush", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
            UseProxy = false,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            UseCookies = false
        })
        .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddSingleton<IApplePushGateway, ApplePushGateway>();
        services.AddHostedService<ApplePushDeliveryHostedService>();
        services.AddHttpClient("FirebasePush", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IFirebaseAccessTokenProvider, FirebaseAccessTokenProvider>();
        services.AddSingleton<IFirebasePushGateway, FirebasePushGateway>();
        services.AddHostedService<FirebasePushDeliveryHostedService>();
        services.AddSingleton<IMessagingContactKeyProtector, MessagingContactKeyProtector>();
        services.AddSingleton<IMessageAttachmentStorage, MessagingAttachmentStorage>();
        services.AddSingleton<IMessagingRealtimePublisher, MessagingRealtimePublisher>();
        services.AddHostedService<MessagingRealtimeNotificationHostedService>();
        services.AddHostedService<LegendConnectLearningHostedService>();
        services.AddHostedService<LegendConnectCorpusAcquisitionHostedService>();

        return services;
    }
}
