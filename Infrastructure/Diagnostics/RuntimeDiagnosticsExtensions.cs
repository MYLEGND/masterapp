using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shared.Diagnostics;

namespace Infrastructure.Diagnostics;

public static class RuntimeDiagnosticsExtensions
{
    public static IServiceCollection AddRuntimeDiagnostics(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(environment);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<RuntimeDiagnosticStore>();
        services.TryAddSingleton<IRuntimeDiagnosticSink>(provider => provider.GetRequiredService<RuntimeDiagnosticStore>());
        services.AddControllersWithViews().AddApplicationPart(typeof(RuntimeDiagnosticsController).Assembly);
        return services;
    }
}
