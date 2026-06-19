using System;
using ProtectWebsite.Services.Tracking;
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
}
