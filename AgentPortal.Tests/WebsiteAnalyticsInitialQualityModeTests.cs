using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models.Analytics;
using AgentPortal.Services;
using AgentPortal.Services.Analytics;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteAnalyticsInitialQualityModeTests
{
    [Fact]
    public async Task Index_DefaultsToRealHumanTraffic_WhenOnlyInternalRowsExist()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedTrackingProfile(db);
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.Add(BuildEvent(now.AddMinutes(-5), profile.Id, isInternal: true, sessionId: "internal-s1", visitorId: "internal-v1"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, profile);

        var result = await controller.Index(preset: "today");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("real_human_traffic", Assert.IsType<string>(view.ViewData["InitialQualityMode"]));

        using var doc = JsonDocument.Parse(Assert.IsType<string>(view.ViewData["InitialSummaryJson"]));
        Assert.Equal(0, doc.RootElement.GetProperty("PageViews").GetInt32());
    }

    [Fact]
    public async Task Index_StaysOnRealHuman_WhenHumanRowsExist()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedTrackingProfile(db);
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.Add(BuildEvent(now.AddMinutes(-6), profile.Id, isInternal: false, sessionId: "human-s1", visitorId: "human-v1"));
        db.AnalyticsEvents.Add(BuildEvent(now.AddMinutes(-5.5), profile.Id, isInternal: false, sessionId: "human-s1", visitorId: "human-v1", eventType: "page_engaged_15s"));
        db.AnalyticsEvents.Add(BuildEvent(now.AddMinutes(-5), profile.Id, isInternal: true, sessionId: "internal-s1", visitorId: "internal-v1"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, profile);

        var result = await controller.Index(preset: "today");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("real_human_traffic", Assert.IsType<string>(view.ViewData["InitialQualityMode"]));

        using var doc = JsonDocument.Parse(Assert.IsType<string>(view.ViewData["InitialSummaryJson"]));
        Assert.Equal(1, doc.RootElement.GetProperty("PageViews").GetInt32());
    }

    private static WebsiteAnalyticsController BuildController(MasterAppDbContext db, AgentTrackingProfile profile)
    {
        var analyticsConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Founder:Upn"] = "founder@example.com",
                ["Analytics:EnvironmentFilter"] = "development",
                ["Analytics:ExcludeLocalHosts"] = "false"
            })
            .Build();

        var tracking = new Mock<IAgentTrackingService>();
        tracking.Setup(x => x.GetByUserIdAsync("agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        tracking.Setup(x => x.GetByUpnAsync("agent@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        tracking.Setup(x => x.GetPersonalUrlsAsync(profile, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentUrlInfo("https://example.com/a/agent-1"));

        var landingRoutes = new Mock<ILandingRouteDiscoveryService>();
        landingRoutes.Setup(x => x.GetBaseUrl()).Returns("https://example.com");
        landingRoutes.Setup(x => x.GetAllRoutes()).Returns(Array.Empty<LandingRouteDefinition>());

        var analytics = new AnalyticsQueryService(db, analyticsConfig);
        var metaAds = Mock.Of<IMetaAdsService>();
        var metaSignalAnalytics = Mock.Of<IMetaSignalAnalyticsService>();
        var aiDataBuilder = new WebsiteAnalyticsAiDataBuilder(
            analytics,
            metaAds,
            metaSignalAnalytics,
            NullLogger<WebsiteAnalyticsAiDataBuilder>.Instance);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", "agent-1"),
            new Claim("preferred_username", "agent@example.com")
        }, "TestAuth"));

        var http = new DefaultHttpContext { User = user };
        var accessor = new HttpContextAccessor { HttpContext = http };
        var effective = new EffectiveAgentContext(accessor, tracking.Object, NullLogger<EffectiveAgentContext>.Instance);
        var protector = new MetaCapiCredentialProtector(DataProtectionProvider.Create("AgentPortal.Tests"));

        return new WebsiteAnalyticsController(
            analytics,
            metaAds,
            Mock.Of<IMetaAdsOAuthService>(),
            Mock.Of<IMetaAdsConnectionStore>(),
            tracking.Object,
            metaSignalAnalytics,
            landingRoutes.Object,
            aiDataBuilder,
            Mock.Of<IVisitorConcentrationService>(),
            Mock.Of<IKpiDetailBreakdownService>(),
            Mock.Of<IVisitorTrustScoringService>(),
            Mock.Of<IAnalyticsIncidentQueryService>(),
            NullLogger<WebsiteAnalyticsController>.Instance,
            db,
            analyticsConfig,
            effective,
            protector)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static AgentTrackingProfile SeedTrackingProfile(MasterAppDbContext db)
    {
        var profile = new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-1",
            AgentUpn = "agent@example.com",
            DisplayName = "Agent One",
            Slug = "agent-1",
            Status = "Active",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.AgentTrackingProfiles.Add(profile);
        return profile;
    }

    private static AnalyticsEvent BuildEvent(
        DateTime eventUtc,
        Guid agentTrackingProfileId,
        bool isInternal,
        string sessionId,
        string visitorId,
        string eventType = "page_view")
    {
        return new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            ClientEventId = Guid.NewGuid(),
            EventType = eventType,
            EventUtc = eventUtc,
            ReceivedUtc = eventUtc,
            SessionId = sessionId,
            VisitorId = visitorId,
            AgentTrackingProfileId = agentTrackingProfileId,
            Environment = "production",
            Host = "portal.mylegnd.com",
            IsInternal = isInternal,
            PageKey = "quote_life"
        };
    }
}
