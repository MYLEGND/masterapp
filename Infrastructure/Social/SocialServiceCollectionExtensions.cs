using Domain.Social;
using Infrastructure.Social.OpenMusic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Social;

public static class SocialServiceCollectionExtensions
{
    public static IServiceCollection AddMasterAppMediaStorage(this IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<ISocialMediaStorage, SocialMediaStorage>(services);
        return services;
    }

    public static IServiceCollection AddMasterAppSocial(
        this IServiceCollection services,
        IConfiguration configuration,
        bool enableMediaProcessing = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddMemoryCache();
        services.AddScoped<ISocialFeedService, SocialFeedService>();
        services.AddScoped<ISocialDiscoveryService, SocialDiscoveryService>();
        services.AddMasterAppMediaStorage();
        if (enableMediaProcessing)
        {
            services.AddSingleton<ISocialMediaVideoProcessor>(serviceProvider =>
                (SocialMediaStorage)serviceProvider.GetRequiredService<ISocialMediaStorage>());
            services.AddSingleton<SocialMediaProcessingWorker>();
            services.AddSingleton<ISocialMediaProcessingQueue>(serviceProvider =>
                serviceProvider.GetRequiredService<SocialMediaProcessingWorker>());
            services.AddHostedService(serviceProvider =>
                serviceProvider.GetRequiredService<SocialMediaProcessingWorker>());
        }
        services.AddSingleton<ISocialMusicCatalog, CuratedOpenMusicCatalog>();

        return services;
    }
}
