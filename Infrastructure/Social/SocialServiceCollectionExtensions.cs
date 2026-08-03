using Domain.Social;
using Infrastructure.Social.OpenMusic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Social;

public static class SocialServiceCollectionExtensions
{
    public static IServiceCollection AddMasterAppSocial(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<ISocialFeedService, SocialFeedService>();
        services.AddScoped<ISocialDiscoveryService, SocialDiscoveryService>();
        services.AddSingleton<ISocialMediaStorage, SocialMediaStorage>();
        services.AddSingleton<ISocialMediaVideoProcessor>(serviceProvider =>
            (SocialMediaStorage)serviceProvider.GetRequiredService<ISocialMediaStorage>());
        services.AddSingleton<SocialMediaProcessingWorker>();
        services.AddSingleton<ISocialMediaProcessingQueue>(serviceProvider =>
            serviceProvider.GetRequiredService<SocialMediaProcessingWorker>());
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<SocialMediaProcessingWorker>());
        services.AddSingleton<ISocialMusicCatalog, CuratedOpenMusicCatalog>();

        return services;
    }
}
