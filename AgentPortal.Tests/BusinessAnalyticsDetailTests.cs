using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Infrastructure.WebsiteEditing;
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
    public async Task BusinessAnalyticsEventMapReadsAutomaticActionsFromTheCanonicalWebsiteContract()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Id = Guid.NewGuid(), Key = "business", DisplayName = "Business" };
        var settings = new CommerceBusinessStorefrontSettings
        {
            CommerceBusinessId = business.Id,
            PublicFactsJson = System.Text.Json.JsonSerializer.Serialize(new WebsiteBusinessFacts
            {
                ContactEmail = "hello@example.org",
                Phone = "(602) 555-0199"
            })
        };
        var document = new WebsiteContentDocument
        {
            Pages = new()
            {
                ["/"] = new WebsitePageContent
                {
                    Extras =
                    [
                        new WebsiteExtraComponent
                        {
                            Id = "call",
                            SectionId = "home.section",
                            Type = "button",
                            Text = "Talk to us",
                            ActionKey = "business_call",
                            Href = "tel:6025550199"
                        }
                    ]
                }
            }
        };
        var state = new WebsiteContentState
        {
            OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id),
            SiteKey = WebsiteEditorSiteKeys.Business,
            DraftJson = System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            Revision = 4
        };
        var version = new WebsiteContentVersion
        {
            StateId = state.Id,
            Revision = 3,
            DocumentJson = state.DraftJson
        };
        state.PublishedVersionId = version.Id;
        db.AddRange(business, settings, state, version);
        await db.SaveChangesAsync();

        var analytics = new Mock<IAnalyticsQueryService>(MockBehavior.Strict);
        analytics.Setup(x => x.GetSummaryAsync(It.IsAny<TimeRangeRequest>(),
            It.Is<ScopeContext>(scope => scope.ScopeType == ScopeType.Business && scope.CommerceBusinessId == business.Id),
            TrafficType.All)).ReturnsAsync(new SummaryKpiDto());
        analytics.Setup(x => x.GetMarketingHealthAsync(It.IsAny<TimeRangeRequest>(),
            It.Is<ScopeContext>(scope => scope.ScopeType == ScopeType.Business && scope.CommerceBusinessId == business.Id)))
            .ReturnsAsync(new MarketingHealthDto());

        var service = new BusinessWorkspaceService(db, analytics.Object, new(db, new ConfigurationBuilder().Build()));
        var model = await service.AnalyticsAsync(business, 30, CancellationToken.None);

        Assert.Contains(model.EventMap, row => row.Element == "page" && row.Event == "ViewContent" && row.Mode == "automatic");
        Assert.Contains(model.EventMap, row => row.Element == "page" && row.Event == "MeaningfulScroll" && row.Mode == "automatic");
        Assert.Contains(model.EventMap, row => row.Element == "extra:call" && row.Event == "cta_click" && row.Mode == "automatic");
        Assert.Contains(model.EventMap, row => row.Element == "extra:call" && row.Event == "ContactStepReached" && row.Mode == "automatic_meta");
        analytics.VerifyAll();
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
