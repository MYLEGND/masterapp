using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Shared.Analytics;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Protect_Website.Controllers;
using System.Xml.Linq;
using Xunit;

namespace AgentPortal.Tests;

public class LaunchAuditProjectionTests
{
    [Theory]
    [InlineData("Critical")]
    [InlineData("Watch")]
    public async Task Shared_health_preserves_meta_failure_for_every_consumer(string status)
    {
        var range = new TimeRangeRequest();
        var scope = new ScopeContext();
        var query = new Mock<IAnalyticsQueryService>();
        var meta = new Mock<IMetaSignalAnalyticsService>();
        query.Setup(x => x.GetMarketingHealthAsync(range, scope, TrafficType.All))
            .ReturnsAsync(new MarketingHealthDto());
        meta.Setup(x => x.GetHealthDashboardAsync(range, scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetaSignalHealthDashboardDto { FailureDetection = new()
            { new() { Status = status, Count = 2, Label = "Dispatch", Detail = "No acceptance evidence" } } });
        var result = await MarketingHealthProjection.LoadAsync(query.Object, meta.Object,
            range, scope, TrafficType.All, NullLogger.Instance);
        Assert.Equal(status, result.MetaHealthStatus);
        Assert.Contains(result.Warnings, x => x.Contains("No acceptance evidence"));
    }

    [Fact]
    public async Task Missing_meta_diagnostics_cannot_be_healthy()
    {
        var query = new Mock<IAnalyticsQueryService>();
        var meta = new Mock<IMetaSignalAnalyticsService>();
        query.Setup(x => x.GetMarketingHealthAsync(It.IsAny<TimeRangeRequest>(), It.IsAny<ScopeContext>(), TrafficType.All))
            .ReturnsAsync(new MarketingHealthDto());
        meta.Setup(x => x.GetHealthDashboardAsync(It.IsAny<TimeRangeRequest>(), It.IsAny<ScopeContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unavailable"));
        var result = await MarketingHealthProjection.LoadAsync(query.Object, meta.Object,
            new TimeRangeRequest(), new ScopeContext(), TrafficType.All, NullLogger.Instance);
        Assert.Equal("Unavailable", result.MetaHealthStatus);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Sitemap_and_paid_registry_share_canonical_controls_without_private_routes()
    {
        var options = new Mock<IOptionsMonitor<LandingRoutesOptions>>();
        options.SetupGet(x => x.CurrentValue).Returns(new LandingRoutesOptions());
        var discovery = new LandingRouteDiscoveryService(options.Object, new ConfigurationBuilder().Build());
        var routes = discovery.GetActiveRoutes();
        Assert.Equal(8, routes.Count);
        var response = Assert.IsType<ContentResult>(new SitemapController().Index());
        var xml = XDocument.Parse(response.Content!);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var urls = xml.Descendants(ns + "loc").Select(x => x.Value).ToList();
        Assert.Equal(urls.Count, urls.Distinct().Count());
        foreach (var route in routes)
        {
            Assert.Contains(route.ControlUrl, urls);
            Assert.Equal(route.ControlPath + "/landing", route.BasePath);
        }
        Assert.Contains(ProtectRouteCatalog.CanonicalOrigin + "/RiskAssessment", urls);
        Assert.Contains(ProtectRouteCatalog.CanonicalOrigin + "/Quote/Commercial", urls);
        Assert.All(urls, url => Assert.StartsWith(ProtectRouteCatalog.CanonicalOrigin + "/", url));
        Assert.DoesNotContain(urls, url => url.Contains("founder") || url.Contains("ThankYou") || url.Contains("/landing"));
    }
}
