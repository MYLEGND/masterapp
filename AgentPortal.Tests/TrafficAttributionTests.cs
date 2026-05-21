using AgentPortal.Services.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class TrafficAttributionTests
{
    [Fact]
    public void Classify_NoSignals_DoesNotSilentlyBecomeDirect()
    {
        var classified = TrafficAttribution.Classify(null, null, null, null);

        Assert.Equal(TrafficType.Unknown, classified);
    }

    [Fact]
    public void Classify_InternalFlag_WinsOverPaidIdentifiers()
    {
        var classified = TrafficAttribution.Classify(
            utmSource: "facebook",
            utmMedium: "cpc",
            utmCampaign: "life-leads",
            fbclid: "fbclid-123",
            isInternal: true,
            environment: "production",
            host: "protect.mylegnd.com");

        Assert.Equal(TrafficType.Internal, classified);
        Assert.False(TrafficAttribution.IsMetaAttributedPaid(
            "facebook",
            "cpc",
            "life-leads",
            "fbclid-123",
            isInternal: true,
            environment: "production",
            host: "protect.mylegnd.com"));
    }

    [Fact]
    public void Classify_NonProductionHost_IsTestTraffic()
    {
        var classified = TrafficAttribution.Classify(
            utmSource: "facebook",
            utmMedium: "cpc",
            utmCampaign: "life-leads",
            fbclid: "fbclid-123",
            environment: "development",
            host: "localhost:6205");

        Assert.Equal(TrafficType.Test, classified);
        Assert.False(TrafficAttribution.IsMetaAttributedPaid(
            "facebook",
            "cpc",
            "life-leads",
            "fbclid-123",
            environment: "development",
            host: "localhost:6205"));
    }

    [Fact]
    public void Classify_BotSignals_AreExcludedFromGrowthBuckets()
    {
        var classified = TrafficAttribution.Classify(
            utmSource: "monitor",
            utmMedium: "healthcheck",
            utmCampaign: "uptime-bot",
            fbclid: null);

        Assert.Equal(TrafficType.BotSuspicious, classified);
        Assert.False(TrafficAttribution.MatchesFilter(classified, TrafficType.NonPaid));
    }

    [Fact]
    public void MatchesFilter_NonPaid_ExcludesUnknown()
    {
        Assert.False(TrafficAttribution.MatchesFilter(TrafficType.Unknown, TrafficType.NonPaid));
        Assert.True(TrafficAttribution.MatchesFilter(TrafficType.Organic, TrafficType.NonPaid));
        Assert.True(TrafficAttribution.MatchesFilter(TrafficType.Direct, TrafficType.NonPaid));
        Assert.True(TrafficAttribution.MatchesFilter(TrafficType.Referral, TrafficType.NonPaid));
    }
}
