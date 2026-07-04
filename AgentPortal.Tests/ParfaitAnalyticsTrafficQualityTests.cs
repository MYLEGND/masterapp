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
    public async Task TrackAsync_StoresCanonicalPublicStorefrontAsProductionTraffic()
    {
        var originalEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        try
        {
            using var db = ControllerTestHelpers.BuildDb();
            var service = BuildService(db);
            var httpContext = BuildHttpContext("localhost", 2121);

            await service.TrackAsync(
                BuildRequest(
                    eventName: "page_engaged_15s",
                    url: "https://shopparfait.com/store",
                    referrer: "https://www.google.com/search?q=parfait"),
                httpContext);

            var row = Assert.Single(db.AnalyticsEvents);
            Assert.Equal("shopparfait.com", row.Host);
            Assert.Equal("production", row.Environment);
            Assert.False(row.IsInternal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", originalEnvironment);
        }
    }

    [Fact]
    public async Task TrackAsync_PublicStorefrontEngagement_QualifiesForRealHumanInsteadOfInternalQa()
    {
        var originalEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        try
        {
            using var db = ControllerTestHelpers.BuildDb();
            var service = BuildService(db);
            var httpContext = BuildHttpContext("localhost", 2121);

            await service.TrackAsync(
                BuildRequest(
                    eventName: "page_engaged_15s",
                    url: "https://shopparfait.com/store/product/sculpt-jacket",
                    referrer: "https://www.instagram.com/"),
                httpContext);

            var allEvents = db.AnalyticsEvents.ToList();
            Assert.Empty(TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(allEvents, TrafficQualityMode.InternalQa));
            Assert.Single(TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(allEvents, TrafficQualityMode.RealHumanTraffic));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", originalEnvironment);
        }
    }

    [Fact]
    public async Task TrackAsync_LocalStorefrontTraffic_RemainsInternalQa()
    {
        var originalEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        try
        {
            using var db = ControllerTestHelpers.BuildDb();
            var service = BuildService(db);
            var httpContext = BuildHttpContext("localhost", 2121);

            await service.TrackAsync(
                BuildRequest(
                    eventName: "page_engaged_15s",
                    url: "http://localhost:2121/store",
                    referrer: "http://localhost:2121/"),
                httpContext);

            var row = Assert.Single(db.AnalyticsEvents);
            Assert.Equal("development", row.Environment);
            Assert.True(row.IsInternal);

            var allEvents = db.AnalyticsEvents.ToList();
            Assert.Single(TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(allEvents, TrafficQualityMode.InternalQa));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", originalEnvironment);
        }
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

    private static ParfaitAnalyticsService BuildService(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:PublicBaseUrl"] = "https://shopparfait.com"
            })
            .Build();

        return new ParfaitAnalyticsService(db, configuration);
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

    private static ParfaitAnalyticsEventRequest BuildRequest(string eventName, string url, string referrer)
    {
        return new ParfaitAnalyticsEventRequest
        {
            EventName = eventName,
            EventId = Guid.NewGuid().ToString("N"),
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
