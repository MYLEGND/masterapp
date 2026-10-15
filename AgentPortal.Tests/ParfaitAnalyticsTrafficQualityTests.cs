using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Domain.Entities;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class ParfaitAnalyticsTrafficQualityTests
{
    [Fact]
    public async Task CanonicalCommerceIngest_UsesVerifiedOriginAndBusiness()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db, "https://shopparfait.com");
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Ingest(BuildRequest("page_engaged_15s", "https://shopparfait.com/store", "https://google.com"), default));
        var row = Assert.Single(db.AnalyticsEvents);
        Assert.Equal("shopparfait.com", row.Host);
        Assert.NotNull(row.CommerceBusinessId);
        Assert.False(row.IsInternal);
    }

    [Fact]
    public async Task CanonicalCommerceIngest_PreservesHumanEvidence()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db, "https://shopparfait.com");
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Ingest(BuildRequest("page_engaged_15s", "https://shopparfait.com/store", "https://instagram.com"), default));
        var row = Assert.Single(db.AnalyticsEvents);
        Assert.Equal(15000, row.EngagedMilliseconds);
        Assert.True(row.HumanInteractionCount > 0);
        Assert.False(row.WebDriver);
    }

    [Fact]
    public async Task CanonicalCommerceIngest_RejectsLocalOriginDespitePublicPayloadUrl()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db, "http://localhost:2121");
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await controller.Ingest(BuildRequest("page_engaged_15s", "https://shopparfait.com/store", ""), default));
        Assert.Empty(db.AnalyticsEvents);
    }

    [Fact]
    public void LegacyAppNamedEnvironment_PublicHost_IsNotAutoClassifiedAsInternalQa()
    {
        var analyticsEvent = new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "page_engaged_15s",
            SessionId = "pfs_legacy_session",
            VisitorId = "pfv_legacy_visitor",
            Environment = "ParfaitApp",
            Host = "shopparfait.com",
            UserAgent = "Mozilla/5.0",
            EngagedMilliseconds = 15000,
            DwellMilliseconds = 20000,
            ScrollPercent = 80,
            HumanInteractionCount = 4,
            MouseMoveCount = 12,
            IsBounceCandidate = false,
            IsExitPage = false,
            IsInternal = false,
            WebDriver = false,
            IsHeadless = false
        };

        var allEvents = new List<AnalyticsEvent> { analyticsEvent };

        Assert.Empty(TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(allEvents, TrafficQualityMode.InternalQa));
        Assert.Single(TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(allEvents, TrafficQualityMode.RealHumanTraffic));
    }

    [Fact]
    public void SessionWithStrongHumanFollowUp_IsClassifiedAsRealHuman_NotSuspicious()
    {
        var sessionId = "pfs_prod_like_session";
        var visitorId = "pfv_prod_like_visitor";

        var allEvents = new List<AnalyticsEvent>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "page_view",
                SessionId = sessionId,
                VisitorId = visitorId,
                Environment = "production",
                Host = "shopparfait.com",
                UserAgent = "Mozilla/5.0",
                EngagedMilliseconds = 0,
                DwellMilliseconds = 12,
                ScrollPercent = 0,
                HumanInteractionCount = 0,
                MouseMoveCount = 0,
                IsBounceCandidate = true,
                IsExitPage = false,
                IsInternal = false,
                WebDriver = false,
                IsHeadless = false
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "AddToCart",
                SessionId = sessionId,
                VisitorId = visitorId,
                Environment = "production",
                Host = "shopparfait.com",
                UserAgent = "Mozilla/5.0",
                EngagedMilliseconds = 6581,
                DwellMilliseconds = 48345,
                ScrollPercent = 100,
                HumanInteractionCount = 115,
                MouseMoveCount = 149,
                IsBounceCandidate = false,
                IsExitPage = false,
                IsInternal = false,
                WebDriver = false,
                IsHeadless = false
            }
        };

        var realHuman = TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(allEvents, TrafficQualityMode.RealHumanTraffic);
        var suspicious = TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(allEvents, TrafficQualityMode.SuspiciousActivity);

        Assert.Equal(2, realHuman.Count);
        Assert.Empty(suspicious);
    }

    private static ProtectWebsite.Controllers.TrackingProxyController BuildController(MasterAppDbContext db, string origin)
    {
        var controller = WebsiteTrackingIngestTests.BuildController(db);
        var configuration = new ConfigurationBuilder().Build();
        var stores = new CommerceStoreContextService(db, new Infrastructure.Businesses.CommerceBusinessScopeResolver(db),
            new ParfaitBusinessScopeService(db), new Infrastructure.WebsiteEditing.WebsiteDomainService(db, Moq.Mock.Of<System.Net.Http.IHttpClientFactory>(), configuration), configuration);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, db);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, stores);
        controller.HttpContext.RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        controller.Request.Headers.Origin = origin;
        return controller;
    }

    private static DefaultHttpContext BuildHttpContext(string host, int? port = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = port.HasValue
            ? new HostString(host, port.Value)
            : new HostString(host);
        httpContext.Request.Path = "/parfait-analytics/track";
        httpContext.Request.Headers.UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.10");
        return httpContext;
    }

    private static WebsiteTrackingProxyAuthority.AnalyticsEventRequest BuildRequest(string eventName, string url, string referrer)
    {
        return new WebsiteTrackingProxyAuthority.AnalyticsEventRequest
        {
            EventType = eventName, SiteKey = "commerce", Path = new Uri(url).AbsolutePath,
            ClientEventId = Guid.NewGuid(),
            VisitorId = "pfv_test_visitor",
            SessionId = "pfs_test_session",
            Url = url,
            Referrer = referrer,
            DeviceType = "desktop",
            Browser = "Chrome",
            OperatingSystem = "macOS",
            TimeZone = "America/Santo_Domingo",
            Language = "en-US",
            ScreenWidth = 1728,
            ScreenHeight = 1117,
            ViewportWidth = 1440,
            ViewportHeight = 900,
            ScrollPercent = 80,
            DwellMilliseconds = 20000,
            EngagedMilliseconds = 15000,
            IsBounceCandidate = false,
            IsExitPage = false,
            WebDriver = false,
            IsHeadless = false,
            MouseMoveCount = 12,
            HumanInteractionCount = 4,
            VisibilityChangeCount = 1,
            TrackingVersion = "parfait-commerce-tracking-test"
        };
    }
}
