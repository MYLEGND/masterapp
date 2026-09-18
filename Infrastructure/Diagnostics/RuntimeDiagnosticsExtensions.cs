using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shared.Diagnostics;

namespace Infrastructure.Diagnostics;

public sealed class RuntimeDiagnosticPublicWebsiteOptions
{
    public string[] AllowedOrigins { get; set; } = [];
}

public static class RuntimeDiagnosticsExtensions
{
    public const string PublicWebsiteCorsPolicy = "RuntimeDiagnosticPublicWebsite";

    // Protect supplies its existing public website origins once to both policies.
    // Credentials are enabled only on the diagnostics branch, never on editor routes.
    public static IServiceCollection AddRuntimeDiagnosticPublicWebsiteTransport(this IServiceCollection services,
        IEnumerable<string> allowedOrigins)
    {
        var origins = allowedOrigins.Distinct(StringComparer.Ordinal).ToArray();
        if (origins.Length is < 1 or > 16 || origins.Any(origin =>
                origin.Contains('*') || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.GetLeftPart(UriPartial.Authority) != origin || uri.UserInfo.Length != 0))
            throw new ArgumentException("Diagnostics requires exact HTTPS origins.", nameof(allowedOrigins));
        services.Configure<RuntimeDiagnosticPublicWebsiteOptions>(options => options.AllowedOrigins = origins);
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "RequestVerificationToken";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });
        services.AddCors(options => options.AddPolicy(PublicWebsiteCorsPolicy, policy => policy
            .WithOrigins(origins).WithMethods("GET", "POST")
            .WithHeaders("Content-Type", "RequestVerificationToken").AllowCredentials()));
        return services;
    }

    public static IServiceCollection AddRuntimeDiagnostics(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(environment);
        services.AddHttpContextAccessor();
        services.AddOptions<RuntimeDiagnosticPublicWebsiteOptions>();
        services.TryAddSingleton<RuntimeDiagnosticStore>();
        services.TryAddSingleton<IRuntimeDiagnosticSink>(provider => provider.GetRequiredService<RuntimeDiagnosticStore>());
        services.AddControllersWithViews().AddApplicationPart(typeof(RuntimeDiagnosticsController).Assembly);
        return services;
    }
}
