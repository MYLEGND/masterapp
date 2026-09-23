using Shared.Analytics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Analytics;

/// <summary>One health projection for dashboard and AI consumers; no independent health state.</summary>
public static class MarketingHealthProjection
{
    public static async Task<MarketingHealthDto> LoadAsync(IAnalyticsQueryService analytics,
        IMetaSignalAnalyticsService metaSignals, TimeRangeRequest range, ScopeContext scope,
        TrafficType trafficType, ILogger logger, CancellationToken ct = default)
    {
        var result = await analytics.GetMarketingHealthAsync(range, scope, trafficType);
        try
        {
            var meta = await metaSignals.GetHealthDashboardAsync(range, scope, ct);
            var issues = meta.FailureDetection.Where(x => x.Count > 0).ToList();
            result.MetaHealthStatus = issues.Any(x => x.Status == "Critical") ? "Critical" :
                issues.Count > 0 ? "Watch" : "Healthy";
            foreach (var issue in issues)
                result.Warnings.Add($"Meta · {issue.Label}: {issue.Detail} (All traffic sources in this scope and range.)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Meta health unavailable while building marketing health.");
            result.MetaHealthStatus = "Unavailable";
            result.Warnings.Add("Meta health could not be checked. Overall marketing health remains unverified.");
        }
        return result;
    }
}
