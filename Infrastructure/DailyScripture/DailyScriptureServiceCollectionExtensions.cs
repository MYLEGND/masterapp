using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Infrastructure.Messaging;

namespace Infrastructure.DailyScripture;

public static class DailyScriptureServiceCollectionExtensions
{
    public static IServiceCollection AddDailyScripture(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(DailyScriptureOptions.FromConfiguration(configuration));
        services.AddScoped<IDailyScriptureService, DailyScriptureService>();
        services.TryAddScoped<IControlledResourceAccessService, ControlledResourceAccessService>();
        services.AddScoped<IDailyScriptureManagementService, DailyScriptureManagementService>();
        return services;
    }
}

public sealed class DailyScriptureOptions
{
    public string BusinessTimeZoneId { get; init; } = "America/Phoenix";

    public static DailyScriptureOptions FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration["DailyScripture:BusinessTimeZoneId"]?.Trim();
        return new DailyScriptureOptions
        {
            BusinessTimeZoneId = string.IsNullOrWhiteSpace(configured)
                ? "America/Phoenix"
                : configured
        };
    }
}
