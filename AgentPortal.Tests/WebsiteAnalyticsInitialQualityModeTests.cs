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
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MarketingOwnerScope = Shared.Analytics.MarketingOwnerScope;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[Collection("Profile website Founder environment")]
public class WebsiteAnalyticsInitialQualityModeTests
{
    [Theory]
    [InlineData("legend", "https://legend.example.test/")]
    [InlineData("protect", "https://example.com/")]
    public async Task Index_ProjectsConfiguredFounderSiteLinks_WithoutChangingProductRouteAuthority(string siteKey, string expected)
    {
        var previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderOid = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderOid);
        try
        {
            using var db = ControllerTestHelpers.BuildDb();
            var profile = SeedTrackingProfile(db);
            profile.AgentUserId = founderOid;
            profile.AgentUpn = "founder@example.com";
            await db.SaveChangesAsync();
            var controller = BuildController(db, profile);
            controller.Request.QueryString = new QueryString("?siteKey=" + siteKey);

            var view = Assert.IsType<ViewResult>(await controller.Index());

            Assert.Equal(expected, view.ViewData["PersonalLink"]);
            Assert.Equal("https://example.com", view.ViewData["LandingRoutesBaseUrl"]);
            using var links = JsonDocument.Parse(Assert.IsType<string>(view.ViewData["FounderSiteLinksJson"]));
            Assert.Equal("https://legend.example.test/", links.RootElement.GetProperty("legend").GetString());
            Assert.Equal("https://example.com/", links.RootElement.GetProperty("protect").GetString());
            using var options = JsonDocument.Parse(Assert.IsType<string>(view.ViewData["AgentOptionsJson"]));
            Assert.Equal(expected, options.RootElement[0].GetProperty("primaryUrl").GetString());
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", previous); }
    }

    [Fact]
    public async Task Index_PreservesAgentPersonalLink_WithoutFounderSiteProjection()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedTrackingProfile(db);
        await db.SaveChangesAsync();
        var controller = BuildController(db, profile);
        controller.Request.QueryString = new QueryString("?siteKey=legend");

        var view = Assert.IsType<ViewResult>(await controller.Index());

        Assert.Equal("https://example.com/a/agent-1", view.ViewData["PersonalLink"]);
        Assert.Null(view.ViewData["FounderSiteLinksJson"]);
    }

    [Fact]
    public async Task Index_DefaultsToRealHumanTraffic_WhenOnlyInternalRowsExist()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedTrackingProfile(db);
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.Add(BuildEvent(now.AddMinutes(-5), profile.Id, isInternal: true, sessionId: "internal-s1", visitorId: "internal-v1"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, profile);

        // Use an explicit UTC window. "today" is correctly viewer-time-zone
        // based, so a test that seeds DateTime.UtcNow just after the viewer's
        // local midnight can otherwise place its own fixture yesterday.
        var result = await controller.Index(
            preset: "custom",
            fromUtc: now.AddHours(-1),
            toUtc: now.AddMinutes(1));

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("real_human_traffic", Assert.IsType<string>(view.ViewData["InitialQualityMode"]));

        using var doc = JsonDocument.Parse(Assert.IsType<string>(view.ViewData["InitialSummaryJson"]));
        Assert.True(doc.RootElement.GetProperty("isAvailable").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("sessionConversionRate", out _));
        Assert.Equal(0, doc.RootElement.GetProperty("pageViews").GetInt32());
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

        // This test specifically verifies session-level real-human membership,
        // not a viewer-local date boundary. Keep the fixture in-range at every
        // UTC hour so that it cannot fail around Phoenix midnight.
        var result = await controller.Index(
            preset: "custom",
            fromUtc: now.AddHours(-1),
            toUtc: now.AddMinutes(1));

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("real_human_traffic", Assert.IsType<string>(view.ViewData["InitialQualityMode"]));

        using var doc = JsonDocument.Parse(Assert.IsType<string>(view.ViewData["InitialSummaryJson"]));
        Assert.True(doc.RootElement.GetProperty("isAvailable").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("sessionConversionRate", out _));
        Assert.Equal(1, doc.RootElement.GetProperty("pageViews").GetInt32());
    }

    [Fact]
    public async Task FounderPersonalCanResolveItsOwnMarketingDestination()
    {
        var previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderOid = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderOid);
        try
        {
            using var db = ControllerTestHelpers.BuildDb();
            var profile = SeedTrackingProfile(db);
            profile.AgentUserId = founderOid;
            await db.SaveChangesAsync();
            var controller = BuildController(db, profile);
            var result = Assert.IsType<JsonResult>(await controller.MetaConnectionStatus(profile.Id));
            var status = Assert.IsType<MetaAdsConnectionStatusDto>(result.Value);
            Assert.False(status.RequiresAgentScope);
            Assert.Equal(profile.Id, status.AgentTrackingProfileId);

            var method = typeof(WebsiteAnalyticsController).GetMethod("ResolveMarketingSetupTrackingAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var resolved = await (Task<AgentTrackingProfile?>)method.Invoke(controller, new object?[] { profile.Id, CancellationToken.None })!;
            Assert.Equal(profile.Id, resolved?.Id);
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", previous); }
    }

    [Fact]
    public async Task FounderMetaStatusSettingsAndDisconnectUseOnlyFounderOwner()
    {
        var previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var oid = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", oid);
        try
        {
            using var db = ControllerTestHelpers.BuildDb();
            var profile = SeedTrackingProfile(db);
            profile.AgentUserId = oid;
            profile.AgentUpn = "founder@example.com";
            await db.SaveChangesAsync();
            var controller = BuildController(db, profile);
            var store = controller.HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
            await store.SaveAdsAsync(MarketingOwnerScope.Founder, new Shared.Analytics.MetaAdsConnectionRecord { AccessToken = "founder-token", AccountId = "founder-account" });
            await store.SaveAdsAsync(MarketingOwnerScope.Agent(profile.Id), new Shared.Analytics.MetaAdsConnectionRecord { AgentTrackingProfileId = profile.Id, AccessToken = "agent-token", AccountId = "legacy-agent-account" });
            var status = Assert.IsType<MetaAdsConnectionStatusDto>(Assert.IsType<JsonResult>(await controller.MetaConnectionStatus(profile.Id)).Value);
            Assert.Equal("founder-account", status.AccountId);
            var method = typeof(WebsiteAnalyticsController).GetMethod("GetMarketingSettingsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var settings = await (Task<MarketingConnection>)method.Invoke(controller, new object[] { profile, MarketingOwnerScope.Founder, CancellationToken.None })!;
            Assert.Equal(MarketingOwnerScope.Founder.Key, settings.OwnerKey);
            await controller.MetaDisconnect(profile.Id);
            Assert.Null(await store.GetAdsAsync(MarketingOwnerScope.Founder));
            Assert.Equal("legacy-agent-account", (await store.GetAdsAsync(MarketingOwnerScope.Agent(profile.Id)))!.AccountId);
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", previous); }
    }

    [Fact]
    public async Task MetaCallbackRejectsForeignOwnerBeforeExchangeOrSave()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedTrackingProfile(db);
        await db.SaveChangesAsync();
        var oauth = new Mock<IMetaAdsOAuthService>();
        oauth.Setup(x => x.InspectState("state")).Returns(new Infrastructure.Analytics.MarketingMetaOAuthState(
            MarketingOwnerScope.Agent(Guid.NewGuid()), "/WebsiteAnalytics/Index", "https://example.com/callback"));
        var controller = BuildController(db, profile, oauth.Object);
        Assert.IsType<ForbidResult>(await controller.MetaCallback("code", "state"));
        oauth.Verify(x => x.CompleteCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(db.MarketingConnections);
    }

    [Fact]
    public async Task GlobalCredentialRequestsFailClosedBeforeCallingProviders()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedTrackingProfile(db);
        await db.SaveChangesAsync();
        var controller = BuildController(db, profile);
        controller.Request.QueryString = new QueryString("?team=true");
        Assert.IsType<ForbidResult>(await controller.MarketingSetup(profile.Id));
        Assert.IsType<ForbidResult>(await controller.ConnectOpenAi(new(profile.Id, "unused", null)));
        Assert.IsType<ForbidResult>(await controller.RefreshOpenAi(new(profile.Id, Guid.NewGuid())));
        Assert.IsType<ForbidResult>(await controller.DisconnectOpenAi(new(profile.Id, Guid.NewGuid())));
        Assert.IsType<ForbidResult>(await controller.SaveMarketingSetup(new(profile.Id, Guid.NewGuid(), null, null, false, null, null, null, null)));
        Assert.Empty(db.MarketingConnections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAiConnectUsesCanonicalProfileOwnerBeforeProviderCall(bool founderProfile)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedTrackingProfile(db);
        if (founderProfile) profile.AgentUpn = "founder@example.com";
        await db.SaveChangesAsync();
        var expected = founderProfile ? MarketingOwnerScope.Founder : MarketingOwnerScope.Agent(profile.Id);
        var connector = new Mock<Infrastructure.Analytics.IOpenAiAdsDirectConnectionService>();
        connector.Setup(service => service.ConnectAsync(expected, "test-key", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Stop after verified owner selection."));
        var controller = BuildController(db, profile);
        controller.HttpContext.RequestServices = new ServiceCollection().AddSingleton(connector.Object).BuildServiceProvider();
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectOpenAi(new(profile.Id, "test-key", null)));
        connector.Verify(service => service.ConnectAsync(expected, "test-key", null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(db.MarketingConnections);
    }

    [Fact]
    public async Task MarketingSetupDoesNotTreatAnAdsConnectionAsConfiguredCapiOrAcceptedEvents()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = SeedTrackingProfile(db);
        profile.AgentUpn = "founder@example.com";
        await db.SaveChangesAsync();
        var owner = MarketingOwnerScope.Founder;
        var controller = BuildController(db, profile);
        var store = controller.HttpContext.RequestServices.GetRequiredService<Infrastructure.Analytics.MarketingConnectionStore>();
        await store.SaveAdsAsync(owner, new Shared.Analytics.MetaAdsConnectionRecord { AccessToken = "protected-test-token", AccountId = "account" });
        var pixels = new Mock<Infrastructure.Analytics.IMetaPixelResolutionService>();
        pixels.Setup(service => service.ResolveForOwnerAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Infrastructure.Analytics.ResolvedMetaPixelContext());
        var openAi = new Mock<Infrastructure.Analytics.IOpenAiAdsAccountConnectionAuthority>();
        openAi.Setup(service => service.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(
            new Shared.Analytics.OpenAiAdsConnectionSnapshot(owner, false, false, Guid.Empty, null, null, null, null, null, null, null, [], null, null, false, false, null, null, null));
        var health = new Mock<Infrastructure.Analytics.IOpenAiMeasurementHealthService>();
        health.Setup(service => service.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(
            new Shared.Analytics.OpenAiMeasurementHealthSnapshot(owner, false, false, false, null, 0, 0, 0, 0, null, false, 0, "not_connected"));
        var calendar = new Mock<Infrastructure.Bookings.IMicrosoftCalendarConnectionAuthority>();
        calendar.Setup(service => service.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(
            new Infrastructure.Bookings.MicrosoftCalendarConnectionSnapshot(
                owner, false, false, Guid.Empty, null, null, null, null, [], null, null, null, null));
        controller.HttpContext.RequestServices = new ServiceCollection().AddSingleton(store).AddSingleton(pixels.Object)
            .AddSingleton(openAi.Object).AddSingleton(health.Object).AddSingleton(calendar.Object)
            .AddSingleton<Infrastructure.Analytics.MarketingProviderSetupProjection>()
            .AddSingleton(Mock.Of<Infrastructure.Analytics.IOpenAiAdsDirectConnectionService>())
            .AddSingleton(new Infrastructure.Analytics.MarketingMeasurementEvidenceService(db, new ConfigurationBuilder().Build(), store, openAi.Object)).BuildServiceProvider();
        var result = Assert.IsType<JsonResult>(await controller.MarketingSetup(profile.Id));
        var json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(json.GetProperty("marketing").GetProperty("metaAdsConnected").GetBoolean());
        Assert.False(json.GetProperty("marketing").GetProperty("metaCapiConfiguredSecurely").GetBoolean());
        Assert.False(json.GetProperty("evidence").GetProperty("receivingEvents").GetBoolean());
        Assert.Equal(0, json.GetProperty("evidence").GetProperty("meta").GetProperty("accepted").GetInt32());
        pixels.Verify(service => service.ResolveForOwnerAsync(owner, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static WebsiteAnalyticsController BuildController(MasterAppDbContext db, AgentTrackingProfile profile, IMetaAdsOAuthService? oauth = null)
    {
        var analyticsConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Founder:Upn"] = "founder@example.com",
                ["Commerce:LegendPublicBaseUrl"] = "https://legend.example.test/",
                ["Analytics:EnvironmentFilter"] = "production",
                ["Analytics:ExcludeLocalHosts"] = "false"
            })
            .Build();

        var tracking = new Mock<IAgentTrackingService>();
        tracking.Setup(x => x.GetByUserIdAsync(profile.AgentUserId!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        tracking.Setup(x => x.GetByUpnAsync("agent@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        tracking.Setup(x => x.GetPersonalUrlsAsync(profile, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentUrlInfo("https://example.com/a/agent-1"));
        tracking.Setup(x => x.GetAllProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentTrackingProfile> { profile });

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
            NullLogger<WebsiteAnalyticsAiDataBuilder>.Instance, db, analyticsConfig,
            Mock.Of<IUnifiedMarketingPerformanceService>(), Mock.Of<Infrastructure.WebsiteEditing.IPromotionOrchestrationService>());

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", profile.AgentUserId!),
            new Claim("preferred_username", "agent@example.com")
        }, "TestAuth"));

        var services = new ServiceCollection();
        services.AddSingleton(new Infrastructure.Analytics.MarketingConnectionStore(db,
            new Infrastructure.Analytics.MarketingCredentialProtector(DataProtectionProvider.Create("AgentPortal.Tests"))));
        var http = new DefaultHttpContext { User = user, RequestServices = services.BuildServiceProvider() };
        var accessor = new HttpContextAccessor { HttpContext = http };
        var effective = new EffectiveAgentContext(accessor, tracking.Object, NullLogger<EffectiveAgentContext>.Instance);
        var protector = new Infrastructure.Analytics.MetaCapiCredentialProtector(DataProtectionProvider.Create("AgentPortal.Tests"));

        return new WebsiteAnalyticsController(
            analytics,
            metaAds,
            oauth ?? Mock.Of<IMetaAdsOAuthService>(),
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
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>())
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
            PageKey = "quote_life",
            UserAgent = "Mozilla/5.0",
            Browser = "Chrome",
            OperatingSystem = "macOS",
            WebDriver = false,
            IsHeadless = false,
            EngagedMilliseconds = eventType == "page_engaged_15s" ? 15000 : 0,
            DwellMilliseconds = eventType == "page_engaged_15s" ? 20000 : 0,
            ScrollPercent = eventType == "page_engaged_15s" ? 80 : 0,
            HumanInteractionCount = eventType == "page_engaged_15s" ? 4 : 0,
            MouseMoveCount = eventType == "page_engaged_15s" ? 12 : 0,
            IsBounceCandidate = eventType == "page_engaged_15s" ? false : null,
            IsExitPage = false
        };
    }
}
