using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Security;

/// <summary>
/// Shared platform HTTP security policy. Provides one place for the baseline
/// browser security headers and the reverse-proxy forwarded-headers config, so
/// applications that currently lack them can adopt the same policy as AgentPortal
/// without duplicating logic.
///
/// <para>Notes on scope:
///  * Content-Security-Policy is intentionally NOT set here — CSP is
///    application-specific and a wrong policy breaks pages, so each app defines
///    its own (AgentPortal already enforces one).
///  * Header writes are additive/non-destructive: an app that already sets a
///    header keeps its value, so this can never weaken an existing policy.</para>
/// </summary>
public static class PlatformSecurityHeaders
{
    /// <summary>
    /// Configures forwarded-header processing for Azure/reverse-proxy hosting,
    /// matching AgentPortal's proven configuration (X-Forwarded-For / -Proto).
    /// </summary>
    public static IServiceCollection AddPlatformForwardedHeaders(this IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        });
        return services;
    }

    /// <summary>
    /// Applies the platform baseline browser security headers (no CSP). Existing
    /// header values set elsewhere are preserved.
    /// </summary>
    public static IApplicationBuilder UsePlatformSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                if (!headers.ContainsKey("X-Content-Type-Options"))
                    headers["X-Content-Type-Options"] = "nosniff";
                if (!headers.ContainsKey("X-Frame-Options"))
                    headers["X-Frame-Options"] = "SAMEORIGIN";
                if (!headers.ContainsKey("Referrer-Policy"))
                    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                if (!headers.ContainsKey("Permissions-Policy"))
                    headers["Permissions-Policy"] =
                        "geolocation=(), camera=(), microphone=(), accelerometer=(), gyroscope=()";

                return Task.CompletedTask;
            });

            await next();
        });
    }
}
