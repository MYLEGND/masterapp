using System.Linq;
using AgentPortal.Controllers.Api;
using ProtectWebsite.Controllers;
using Xunit;

namespace AgentPortal.Tests;

public class TrackingProxyContractTests
{
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
        // before forwarding; the AgentPortal ingest contract has no SiteKey.
        Assert.Equal(typeof(string), Assert.Single(proxyProperties.Where(x => x.Name == "SiteKey")).PropertyType);
        proxyProperties.RemoveAll(x => x.Name == "SiteKey");

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
