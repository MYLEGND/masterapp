using AgentPortal.Services;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendAnalyticsCoverageTests
{
    [Fact]
    public void MissingProjectionIsNullWhileObservedZeroRemainsZero()
    {
        var payload = new AiSafeAnalyticsPayload();
        Assert.Equal(0, WebsiteAnalyticsLegendReadProjection.Observe(payload, "Summary", 0));
        payload.Warnings.Add("Summary unavailable; fallback aggregates must not be interpreted as observed zero.");
        Assert.Null(WebsiteAnalyticsLegendReadProjection.Observe(payload, "Summary", 0));
        Assert.Equal(3, WebsiteAnalyticsLegendReadProjection.Observe(payload, "QuoteFunnel", 3));
    }
}
