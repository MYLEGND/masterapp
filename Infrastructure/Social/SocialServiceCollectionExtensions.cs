using Domain.Social;
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
        services.AddSingleton<ISocialMediaStorage, SocialMediaStorage>();

        return services;
    }
}
