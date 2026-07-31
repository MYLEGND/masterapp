using Domain.Social;
using Infrastructure.Social.Spotify;
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
        services.Configure<SpotifySocialMusicOptions>(
            configuration.GetSection(SpotifySocialMusicOptions.SectionName));
        services.AddHttpClient<SpotifySocialMusicCatalog>();
        services.AddSingleton<UnavailableSocialMusicCatalog>();
        services.AddTransient<ISocialMusicCatalog>(serviceProvider =>
        {
            var spotify = configuration
                .GetSection(SpotifySocialMusicOptions.SectionName)
                .Get<SpotifySocialMusicOptions>();

            return spotify?.IsConfigured == true
                ? serviceProvider.GetRequiredService<SpotifySocialMusicCatalog>()
                : serviceProvider.GetRequiredService<UnavailableSocialMusicCatalog>();
        });

        return services;
    }
}
