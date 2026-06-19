using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class MetaSignalSingleTruthPolicyTests
{
    [Fact]
    public void IsAuthorizedCapiSource_RequiresDispatcherForServerTruthEvents()
    {
        Assert.False(MetaSignalSingleTruthPolicy.IsAuthorizedCapiSource("Lead", "Controllers"));
        Assert.True(MetaSignalSingleTruthPolicy.IsAuthorizedCapiSource("Lead", MetaSignalSingleTruthPolicy.DispatchOwner));
        Assert.True(MetaSignalSingleTruthPolicy.IsAuthorizedCapiSource("ViewContent", "Controllers"));
    }

    [Fact]
    public void CanDispatchServerAuthority_RequiresSingleTruthMetadata()
    {
        var metadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson(
            eventName: "PolicyPaid",
            leadId: null,
            sessionId: "session-1",
            payload: new { amount = 100 },
            isBrowserSignal: false,
            isServerAuthority: true,
            metaServerAuthorityEligible: true,
            metaSingleTruthDispatchEligible: true,
            metaPipelineOrigin: "unit_test");

        Assert.True(MetaSignalSingleTruthPolicy.CanDispatchServerAuthority("PolicyPaid", metadataJson));
        Assert.False(MetaSignalSingleTruthPolicy.CanDispatchServerAuthority("ViewContent", metadataJson));
    }
}
