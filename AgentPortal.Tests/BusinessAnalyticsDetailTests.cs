using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Moq;
using Shared.Analytics;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace AgentPortal.Tests;

public sealed class BusinessAnalyticsDetailTests
{
    [Fact]
    public async Task KpiCurrentAndPreviousWindowsUseTheSameBusinessAuthority()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = Guid.NewGuid();
        var analytics = new Mock<IAnalyticsQueryService>(MockBehavior.Strict);
        analytics.Setup(x => x.GetTrafficAsync(It.IsAny<TimeRangeRequest>(),
            It.Is<ScopeContext>(s => s.ScopeType == ScopeType.Business && s.CommerceBusinessId == business && s.AgentTrackingProfileId == null), TrafficType.All))
            .ReturnsAsync(new TrafficOverviewDto());
        analytics.Setup(x => x.GetSummaryAsync(It.IsAny<TimeRangeRequest>(),
            It.Is<ScopeContext>(s => s.ScopeType == ScopeType.Business && s.CommerceBusinessId == business), TrafficType.All))
            .ReturnsAsync(new SummaryKpiDto { Sessions = 7 });
        var service = new BusinessWorkspaceService(db, analytics.Object, new(db, new ConfigurationBuilder().Build()));
        var result = Assert.IsType<AgentPortal.Models.Analytics.KpiDetailDto>(await service.AnalyticsDataAsync(business,
            "kpi-detail", new TimeRangeRequest { FromUtc = DateTime.UtcNow.AddDays(-7), ToUtc = DateTime.UtcNow }, TrafficType.All, metric: "sessions"));
        Assert.Equal(7, result.Totals.Total);
        Assert.Equal(7, result.Totals.PreviousTotal);
        analytics.Verify(x => x.GetSummaryAsync(It.IsAny<TimeRangeRequest>(), It.IsAny<ScopeContext>(), TrafficType.All), Times.Exactly(2));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyticsDataAsync(business, "kpi-detail", new(), TrafficType.All, metric: "unknown"));
    }

    [Fact]
    public async Task VisitorTimelineRestrictsVisitorAndSessionBeforeLoadingMetaSignals()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = Guid.NewGuid();
        var selected = new AnalyticsEvent { VisitorId = "visitor", SessionId = "session", EventUtc = DateTime.UtcNow, EventType = "page_view" };
        var analytics = new Mock<IAnalyticsQueryService>(MockBehavior.Strict);
        analytics.Setup(x => x.LoadAttributedEventsAsync(It.IsAny<TimeRangeRequest>(),
            It.Is<ScopeContext>(s => s.ScopeType == ScopeType.Business && s.CommerceBusinessId == business), TrafficType.All, It.IsAny<CancellationToken>()))
            .ReturnsAsync([selected, new AnalyticsEvent { VisitorId = "other", SessionId = "session" }, new AnalyticsEvent { VisitorId = "visitor", SessionId = "other" }]);
        analytics.Setup(x => x.LoadScopedMetaEventsAsync(It.IsAny<TimeRangeRequest>(),
            It.Is<ScopeContext>(s => s.CommerceBusinessId == business),
            It.Is<IReadOnlyCollection<AnalyticsEvent>>(events => events.Count == 1 && events.Contains(selected)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MetaSignalEvent>());
        var service = new BusinessWorkspaceService(db, analytics.Object, new(db, new ConfigurationBuilder().Build()));
        Assert.NotNull(await service.AnalyticsDataAsync(business, "visitor-timeline", new(), TrafficType.All,
            visitorId: "visitor", sessionId: "session"));
        analytics.VerifyAll();
    }
}
