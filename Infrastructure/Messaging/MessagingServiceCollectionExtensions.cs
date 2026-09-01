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
        services.AddSingleton<IAzureTranslatorSubscriptionCapacitySource, AzureTranslatorSubscriptionCapacitySource>();
        services.AddScoped<ITranslationCapacityAuthority, TranslationCapacityAuthority>();
        services.AddScoped<ILegendConnectRuntimePolicyAuthority, LegendConnectRuntimePolicyAuthority>();
        services.AddScoped<LegendConnectHistoricalReevaluationWorkAuthority>();
        services.AddScoped<ITranslationEntitlementAuthority, TranslationEntitlementAuthority>();
        services.AddScoped<ITranslationDemandRecorder, TranslationDemandRecorder>();
        services.AddScoped<ITranslationSystemUsageRecorder, TranslationSystemUsageRecorder>();
        services.AddScoped<ILegendConnectOperationalEventWriter, LegendConnectOperationalEventWriter>();
        services.AddScoped<ILegendConnectTranslationIntelligence, LegendConnectTranslationIntelligence>();
        services.AddScoped<ITranslationProvider, AzureTranslatorService>();
        services.AddScoped<ITranslationService, LegendConnectTranslationRouter>();
        services.AddScoped<ITranslationLearningPublisher, LegendTranslationLearningPublisher>();
        services.AddScoped<LegendConnectCorpusService>();
        services.AddScoped<LegendConnectCurriculumService>();
        services.AddScoped<LegendConnectCurriculumManifestProcessor>();
        services.AddScoped<LegendConnectFounderTrainingIngestionAuthority>();
        services.AddScoped<ILegendConnectStructuralCompositionGate>(provider =>
            provider.GetRequiredService<LegendConnectCurriculumService>());
        services.AddScoped<LegendConnectAutonomousGapPlanner>();
        services.AddScoped<LegendConnectAutonomousLearningService>();
        services.AddScoped<LegendConnectTrainingDatasetCompiler>(provider =>
            new LegendConnectTrainingDatasetCompiler(
                provider.GetRequiredService<Infrastructure.Data.MasterAppDbContext>()));
        services.AddScoped<LegendConnectModelTrainingService>(provider =>
            new LegendConnectModelTrainingService(
                provider.GetRequiredService<Infrastructure.Data.MasterAppDbContext>(),
                provider.GetRequiredService<LegendConnectTrainingDatasetCompiler>(),
                provider.GetRequiredService<ILegendConnectModelTrainingBackend>(),
                provider.GetRequiredService<IConfiguration>()));
        services.AddScoped<ILegendConnectModelTrainingBackend, OpenAiLegendConnectModelTrainingBackend>();
        services.AddScoped<ILegendConnectModelInferenceTransport, OpenAiLegendConnectModelInferenceTransport>();
        services.AddScoped<LegendConnectModelEvaluationService>(provider =>
            new LegendConnectModelEvaluationService(
                provider.GetRequiredService<Infrastructure.Data.MasterAppDbContext>(),
                provider.GetRequiredService<LegendConnectTrainingDatasetCompiler>(),
                provider.GetRequiredService<ILegendConnectModelEvaluationBackend>(),
                provider.GetRequiredService<ILegendConnectActiveModelInference>(),
                provider.GetRequiredService<IConfiguration>()));
        services.AddScoped<ILegendConnectModelEvaluationBackend, OpenAiLegendConnectModelEvaluationBackend>();
        services.AddScoped<ILegendConnectActiveModelInference, LegendConnectActiveModelInference>();
        services.AddScoped<ILegendConnectInternetResearchTransport, OpenAiLegendConnectInternetResearchTransport>();
        services.AddScoped<LegendConnectModelPromotionService>(provider =>
            new LegendConnectModelPromotionService(
                provider.GetRequiredService<Infrastructure.Data.MasterAppDbContext>(),
                provider.GetRequiredService<LegendConnectTrainingDatasetCompiler>(),
                provider.GetRequiredService<IConfiguration>()));
        services.AddScoped<ILegendConnectLanguageTeacher, OpenAiLegendConnectLanguageTeacher>();
        services.AddScoped<ILegendConnectOperations, LegendConnectOperations>();
        services.AddHttpClient("LegendModelTraining", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("LegendModelEvaluation", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("LegendLanguageTeacher", client =>
        {
            // Teacher/critic work is non-authoritative and isolated from
            // operational translation. Larger controlled families need a
            // bounded reasoning window without changing Azure/message latency.
            var teacherTimeoutSeconds =
                Math.Clamp(
                    configuration.GetValue<int?>(
                        "LegendConnect:LanguageTeacher:TimeoutSeconds") ??
                        90,
                    30,
                    180);

            client.Timeout =
                TimeSpan.FromSeconds(
                    teacherTimeoutSeconds);
        });
        services.AddHttpClient("LegendInternetResearch", client =>
        {
            // The outer Founder request budget remains authoritative. This
            // client adds a narrower transport ceiling and performs no writes.
            client.Timeout = TimeSpan.FromSeconds(45);
        });
        services.AddHttpClient("AzureTranslator", client =>
        {
            // Provider failures must never hold up message delivery.
            client.Timeout = TimeSpan.FromSeconds(6);
        });
        services.AddHttpClient("AzureResourceManager", client =>
        {
            // Capacity sync is operational metadata only. It must fail closed
            // for Azure work without delaying an authoritative message write.
            client.BaseAddress = new Uri("https://management.azure.com/");
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

        return services;
    }

    /// <summary>
    /// Starts the single canonical LEGEND background-worker set in the one
    /// application composition root that owns production learning. Messaging
    /// consumers deliberately do not start these workers merely by registering
    /// messaging services. AddHostedService is idempotent per implementation,
    /// so repeated calls cannot manufacture a competing in-process executor.
    /// </summary>
    public static IServiceCollection AddLegendConnectHostedWorkers(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<LegendConnectCurriculumManifestHostedService>();
        services.AddHostedService<LegendConnectLearningHostedService>();
        services.AddHostedService<LegendConnectCorpusAcquisitionHostedService>();

        return services;
    }
}
