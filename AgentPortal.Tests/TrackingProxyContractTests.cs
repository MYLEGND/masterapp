using System.Linq;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using AgentPortal.Controllers.Api;
using ProtectWebsite.Controllers;
using Xunit;

namespace AgentPortal.Tests;

public class TrackingProxyContractTests
{
    [Fact]
    public void SharedAuthoritiesAreNotDiscoveredAsDuplicatePublicControllers()
    {
        // Runtime diagnostics adds Infrastructure as an MVC application part.
        // Reproduce that real discovery path alongside the Protect host.
        var manager = new ApplicationPartManager();
        manager.ApplicationParts.Add(new AssemblyPart(typeof(WebsiteTrackingProxyAuthority).Assembly));
        manager.ApplicationParts.Add(new AssemblyPart(typeof(TrackingProxyController).Assembly));
        manager.FeatureProviders.Add(new ControllerFeatureProvider());
        var feature = new ControllerFeature();
        manager.PopulateFeature(feature);

        Assert.DoesNotContain(feature.Controllers, type => type.AsType() == typeof(WebsiteTrackingProxyAuthority));
        Assert.DoesNotContain(feature.Controllers, type => type.AsType() == typeof(WebsiteAnalyticsIngestAuthority));
        Assert.Single(feature.Controllers.Where(type => typeof(WebsiteTrackingProxyAuthority).IsAssignableFrom(type.AsType())));
        Assert.Single(feature.Controllers.Where(type => typeof(WebsiteAnalyticsIngestAuthority).IsAssignableFrom(type.AsType())));
        Assert.Contains(feature.Controllers, type => type.AsType() == typeof(TrackingProxyController));
    }

    [Fact]
    public void AnalyticsEventRequest_StaysInSyncBetweenProxyAndIngest()
    {
        var ingestProperties = typeof(AnalyticsIngestController.AnalyticsEventRequest)
            .GetProperties()
            .OrderBy(x => x.Name, System.StringComparer.Ordinal)
            .ToList();
        var proxyProperties = typeof(TrackingProxyController.AnalyticsEventRequest)
            .GetProperties()
            .OrderBy(x => x.Name, System.StringComparer.Ordinal)
            .ToList();

        // Public website scope is validated and persisted by the shared proxy
        // before forwarding; AgentPortal ingest has neither scope selector.
        foreach (var scopeField in new[] { "SiteKey", "WebsiteBindingId" })
        {
            Assert.Equal(typeof(string), Assert.Single(proxyProperties.Where(x => x.Name == scopeField)).PropertyType);
            proxyProperties.RemoveAll(x => x.Name == scopeField);
        }

        Assert.Equal(
            ingestProperties.Select(x => x.Name).ToArray(),
            proxyProperties.Select(x => x.Name).ToArray());

        foreach (var ingestProperty in ingestProperties)
        {
            var proxyProperty = Assert.Single(proxyProperties.Where(x => x.Name == ingestProperty.Name));
            Assert.Equal(ingestProperty.PropertyType, proxyProperty.PropertyType);
        }
    }
}
