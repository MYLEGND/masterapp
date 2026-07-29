using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.DailyScripture;

public static class DailyScriptureServiceCollectionExtensions
{
    public static IServiceCollection AddDailyScripture(this IServiceCollection services)
    {
        services.AddSingleton<IDailyScriptureService, DailyScriptureService>();
        return services;
    }
}
