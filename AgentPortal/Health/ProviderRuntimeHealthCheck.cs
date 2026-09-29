using Infrastructure.Analytics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.Analytics;

namespace AgentPortal.Health;

/// <summary>
/// Safe aggregate canary for the shared external-provider runtime.
///
/// Only providers that are already stored as connected can fail this check.
/// Unconfigured providers are neutral. No account IDs, credentials, provider
/// payloads, or user data are emitted by the health endpoint.
/// </summary>
public sealed class ProviderRuntimeHealthCheck(
    IPlatformConnectionHealthAuthority authority,
    IMemoryCache cache,
    ILogger<ProviderRuntimeHealthCheck> logger) : IHealthCheck
{
    private const string CacheKey = "legend:provider-runtime-health:founder";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out HealthCheckResult cached))
            return cached;

        try
        {
            var snapshot = await authority.ReadAsync(MarketingOwnerScope.Founder, cancellationToken);
            var failures = new List<string>();

            if (snapshot.Meta.StoredConnected && !snapshot.Meta.ProviderVerified)
                failures.Add($"meta:{snapshot.Meta.Status}");

            if (snapshot.OpenAi.Connection.Connected && !snapshot.OpenAi.ProviderVerified)
                failures.Add($"openai:{snapshot.OpenAi.Status}");

            if (snapshot.Calendar.Connection.Connected && !snapshot.Calendar.ProviderVerified)
                failures.Add($"calendar:{snapshot.Calendar.Status}");

            var result = failures.Count == 0
                ? HealthCheckResult.Healthy("Configured provider connections verified.")
                : HealthCheckResult.Unhealthy(
                    "One or more configured provider connections failed live verification.",
                    data: new Dictionary<string, object>
                    {
                        ["providers"] = string.Join(",", failures)
                    });

            cache.Set(CacheKey, result, TimeSpan.FromSeconds(45));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Canonical provider health canary failed.");
            var result = HealthCheckResult.Unhealthy("Provider health authority unavailable.");
            cache.Set(CacheKey, result, TimeSpan.FromSeconds(15));
            return result;
        }
    }
}
