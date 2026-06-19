using System;
using ProtectWebsite.Services.Tracking;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class UnifiedEventMapperTests
{
    [Fact]
    public void ToAnalytics_CarriesBehaviorFieldsThroughUnifiedPipeline()
    {
        var eventUtc = new DateTime(2026, 6, 18, 16, 30, 0, DateTimeKind.Utc);
        var context = new UnifiedEventContext
        {
            EventName = "page_exit",
            Referrer = "https://facebook.com/campaign",
            ReferrerHost = "facebook.com",
            SessionId = "session-1",
            VisitorId = "visitor-1",
            MouseMoveCount = 12,
            HumanInteractionCount = 8,
            VisibilityChangeCount = 3,
            ScrollPercent = 82,
            DwellMilliseconds = 45_678,
            EngagedMilliseconds = 32_100,
            IsBounceCandidate = true,
            IsExitPage = true,
            Language = "en-US",
            TimeZone = "America/Phoenix",
            EventUtc = eventUtc,
            IsInternal = true
        };

        var analyticsEvent = UnifiedEventMapper.ToAnalytics(context);

        Assert.Equal("facebook.com", analyticsEvent.ReferrerHost);
        Assert.Equal(12, analyticsEvent.MouseMoveCount);
        Assert.Equal(8, analyticsEvent.HumanInteractionCount);
        Assert.Equal(3, analyticsEvent.VisibilityChangeCount);
        Assert.Equal(82, analyticsEvent.ScrollPercent);
        Assert.Equal(45_678, analyticsEvent.DwellMilliseconds);
        Assert.Equal(32_100, analyticsEvent.EngagedMilliseconds);
        Assert.True(analyticsEvent.IsBounceCandidate);
        Assert.True(analyticsEvent.IsExitPage);
        Assert.Equal("en-US", analyticsEvent.Language);
        Assert.Equal("America/Phoenix", analyticsEvent.TimeZone);
        Assert.Equal(eventUtc, analyticsEvent.EventUtc);
        Assert.True(analyticsEvent.IsInternal);
    }

    [Fact]
    public void ToAnalytics_StampsSingleTruthEligibilityForServerOnlyLeadEvents()
    {
        var leadId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var context = UnifiedEventContextBuilder.Build(
            httpContext: null,
            eventName: "website_lead_submitted",
            sessionId: "session-1",
            metadata: new
            {
                LeadId = leadId
            });

        var analyticsEvent = UnifiedEventMapper.ToAnalytics(context);

        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(analyticsEvent.MetadataJson, "isBrowserSignal"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(analyticsEvent.MetadataJson, "isServerAuthority"));
        Assert.True(MetaSignalSingleTruthPolicy.ReadBoolean(analyticsEvent.MetadataJson, "metaServerAuthorityEligible"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(analyticsEvent.MetadataJson, "metaSingleTruthDispatchEligible"));
        Assert.Equal(
            $"website_lead_submitted:{leadId:N}:session-1",
            MetaSignalSingleTruthPolicy.ReadString(analyticsEvent.MetadataJson, "eventKey"));
    }
}
