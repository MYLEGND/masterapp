using System;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Security;

/// <summary>
/// The one shared rate-limiting authority. Owns policy construction, limits,
/// windows, rejection behavior, and partition-key logic (anonymous → trusted
/// client IP; authenticated → canonical identity). Limits mirror AgentPortal's
/// production-proven values. Policies are OPT-IN per endpoint via
/// [EnableRateLimiting]; there is deliberately NO global limiter here, so no
/// route, webhook, OAuth callback, mobile-bearer, internal, or background path is
/// throttled unless an endpoint explicitly enables a policy.
///
/// <para>Applications only call <see cref="AddPlatformRateLimiting"/> in their
/// composition root and reference the shared policy names — they must not rebuild
/// window/partition logic.</para>
/// </summary>
public static class PlatformRateLimiting
{
    /// <summary>High-volume machine ingest (analytics/tracking): 300/min per partition.</summary>
    public const string PublicIngestPolicy = "public-ingest";

    /// <summary>Public form/lead submission: 30/min per partition.</summary>
    public const string PublicFormPolicy = "public-form";

    /// <summary>
    /// Configures the platform rate-limiting policies on an application's
    /// <see cref="RateLimiterOptions"/>. Apps register via
    /// <c>services.AddRateLimiter(PlatformRateLimiting.ConfigurePolicies)</c> in
    /// their composition root; all policy/limit/partition logic lives here.
    /// </summary>
    public static void ConfigurePolicies(RateLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        AddFixedWindowPolicy(options, PublicIngestPolicy, permitLimit: 300, window: TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, PublicFormPolicy, permitLimit: 30, window: TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The one shared fixed-window policy builder. Applications register their own
    /// named policies and limits (as data) through this method rather than
    /// rebuilding window/partition logic. Named policies partition by client IP,
    /// matching every existing public/anonymous limiter on the platform.
    /// </summary>
    public static void AddFixedWindowPolicy(
        RateLimiterOptions options,
        string policyName,
        int permitLimit,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(policyName, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: IpPartitionKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));
    }

    /// <summary>
    /// Anonymous partition key: client IP. IP comes from
    /// <see cref="HttpContext.Connection"/>, which reflects forwarded headers only
    /// after the Phase 4 forwarded-header / trusted-proxy middleware has run —
    /// arbitrary X-Forwarded-For values are not trusted directly.
    /// </summary>
    public static string IpPartitionKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "anon";

    /// <summary>
    /// Authenticated traffic partitions by stable identity; anonymous by client IP.
    /// Used by app-specific global limiters (e.g. AgentPortal) that mix both.
    /// </summary>
    public static string ResolvePartitionKey(HttpContext context)
        => context.User?.Identity?.IsAuthenticated == true
            ? (context.User.Identity?.Name ?? "auth-unknown")
            : IpPartitionKey(context);
}
