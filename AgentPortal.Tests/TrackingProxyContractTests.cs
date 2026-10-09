using System.Linq;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.DoesNotContain(feature.Controllers, type => typeof(WebsiteAnalyticsIngestAuthority).IsAssignableFrom(type.AsType()));
        Assert.Contains(feature.Controllers, type => type.AsType() == typeof(TrackingProxyController));
    }

    [Fact]
    public void PublicAnalyticsHasExactlyOneConcreteIngestRouteAcrossHosts()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddMvcCore()
            .AddApplicationPart(typeof(WebsiteTrackingProxyAuthority).Assembly)
            .AddApplicationPart(typeof(TrackingProxyController).Assembly)
            .AddApplicationPart(typeof(ParfaitApp.Controllers.StoreCartController).Assembly)
            .AddApplicationPart(typeof(AgentPortal.Controllers.WebsiteAnalyticsController).Assembly);
        using var provider = services.BuildServiceProvider();
        var actions = provider.GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>().ToArray();
        var ingest = actions.Where(a => a.AttributeRouteInfo?.Template?.EndsWith("/ingest", System.StringComparison.OrdinalIgnoreCase) == true).ToArray();
        var endpoint = Assert.Single(ingest);
        Assert.Equal("api/tracking/ingest", endpoint.AttributeRouteInfo!.Template);
        Assert.Equal(typeof(TrackingProxyController), endpoint.ControllerTypeInfo.AsType());
        Assert.DoesNotContain(actions, a => a.AttributeRouteInfo?.Template is "analytics/business-page" or "analytics/meta-signal");
        Assert.DoesNotContain(actions, a => a.ControllerName is "AnalyticsIngest" or "ParfaitAnalytics");
        Assert.DoesNotContain(actions, a => a.AttributeRouteInfo?.Template?.Contains("parfait-analytics", System.StringComparison.OrdinalIgnoreCase) == true);
    }
}
