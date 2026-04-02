using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentPortal.Services;

/// <summary>
/// Readiness check: verifies the distributed cache (Redis) is reachable.
/// Only registered when a Redis connection string is configured.
/// </summary>
public sealed class RedisReadinessCheck : IHealthCheck
{
    private readonly IDistributedCache _cache;

    public RedisReadinessCheck(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            const string probeKey = "healthz:redis:probe";
            await _cache.SetStringAsync(probeKey, "1", new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10)
            }, cancellationToken);

            var val = await _cache.GetStringAsync(probeKey, cancellationToken);
            return val == "1"
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("Redis probe returned unexpected value.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis connection check threw an exception.", ex);
        }
    }
}
