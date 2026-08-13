using AgentPortal.Models;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectMetricToneTests
{
    [Theory]
    [InlineData("Validated", LegendConnectMetricTone.Success)]
    [InlineData("Observation", LegendConnectMetricTone.Info)]
    [InlineData("Insufficient", LegendConnectMetricTone.Warning)]
    [InlineData("Blocked", LegendConnectMetricTone.Danger)]
    public void Quality_MapsAuthoritativeMaturityToOnePresentationTone(string state, string expected) =>
        Assert.Equal(expected, LegendConnectMetricTone.Quality(state));

    [Theory]
    [InlineData("FounderApproved", LegendConnectMetricTone.Authority)]
    [InlineData("ProviderDerived", LegendConnectMetricTone.Info)]
    [InlineData("Legacy", LegendConnectMetricTone.Warning)]
    public void Provenance_MapsEvidenceOriginToOnePresentationTone(string provenance, string expected) =>
        Assert.Equal(expected, LegendConnectMetricTone.Provenance(provenance));

    [Fact]
    public void ConfidenceAndLifecycle_DistinguishTrustedCautionAndFailure()
    {
        Assert.Equal(LegendConnectMetricTone.Success, LegendConnectMetricTone.Confidence(0.98m));
        Assert.Equal(LegendConnectMetricTone.Warning, LegendConnectMetricTone.Confidence(0.72m));
        Assert.Equal(LegendConnectMetricTone.Danger, LegendConnectMetricTone.Confidence(0.20m));
        Assert.Equal(LegendConnectMetricTone.Warning, LegendConnectMetricTone.Lifecycle("Pending"));
        Assert.Equal(LegendConnectMetricTone.Danger, LegendConnectMetricTone.Lifecycle("Failed"));
    }
}
