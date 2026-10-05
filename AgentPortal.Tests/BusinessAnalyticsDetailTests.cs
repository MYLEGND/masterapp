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
        var service = new BusinessWorkspaceService(db, analytics.Object);
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
        var business = new CommerceBusiness { Id = Guid.NewGuid(), Key = "business", DisplayName = "Business", IsActive = true, Status = "Active" };
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
                ["/"] = new WebsitePageDocument
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "call",
                            Type = "cta",
                            Tag = "a",
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
            CommerceBusinessId = business.Id,
            DraftJson = System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            Revision = 4
        };
        var version = new WebsiteContentVersion
        {
            StateId = state.Id,
            Revision = 3,
            DocumentJson = state.DraftJson
        };
        // Unpublished edits must not appear as live behavior or rewrite publication history.
        document.Pages["/"].Composition[0].Text = "Unpublished call label";
        document.Pages["/"].Composition.Add(new WebsiteCompositionNode
        {
            Id = "draft-only",
            Type = "cta",
            Tag = "a",
            ActionKey = "business_email",
            Text = "Draft email"
        });
        state.DraftJson = System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
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

        var service = new BusinessWorkspaceService(db, analytics.Object);
        var model = await service.AnalyticsAsync(business,
            TimeRangeRequest.FromPreset("30d", viewerTz: TimeZoneInfo.Utc), CancellationToken.None);

        Assert.Contains(model.EventMap, row => row.Element == "automatic:page_view" && row.Event == "page_view" && row.Mode == "automatic");
        Assert.Contains(model.EventMap, row => row.Element == "automatic:meaningful_scroll" && row.Event == "scroll_depth_50" && row.Mode == "automatic");
        var call = Assert.Single(model.EventMap.Where(row => row.Element == "call"));
        Assert.Equal("business_call", call.ActionKey);
        Assert.Equal("cta_click", call.Event);
        Assert.Equal("Talk to us", call.VisibleLabel);
        Assert.DoesNotContain(model.EventMap, row => row.Element == "draft-only" || row.VisibleLabel == "Unpublished call label");
        Assert.Equal("browser", call.Authority);
        Assert.Equal(version.Id, call.PublishedVersion);
        Assert.Equal(3, call.Revision);
        Assert.DoesNotContain(model.EventMap, row => row.Mode == "automatic_meta" || !row.Published);
        Assert.Contains(model.EventMap, row => row.BehaviorKey == "appointment_booked" && row.Locked && row.Authority == "verified_server");
        var canonical = await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build()).ReadAsync(ScopeContext.ForBusiness(business.Id));
        Assert.Equal(canonical.Select(row => (row.Element, row.CanonicalEvent, row.MetaMapping, row.OpenAiMapping)),
            model.EventMap.Select(row => (row.Element, row.Event, row.MetaMapping, row.OpenAiMapping)));
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
        var service = new BusinessWorkspaceService(db, analytics.Object);
        Assert.NotNull(await service.AnalyticsDataAsync(business, "visitor-timeline", new(), TrafficType.All,
            visitorId: "visitor", sessionId: "session"));
        analytics.VerifyAll();
    }
}
