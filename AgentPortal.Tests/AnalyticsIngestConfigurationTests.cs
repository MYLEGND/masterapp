using System.Collections.Generic;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AnalyticsIngestConfigurationTests
{
    [Fact]
    public void CanonicalReceiverSecretWinsOverStaleSenderAlias()
    {
        var values = new Dictionary<string, string?>
        {
            ["Analytics:SharedSecret"] = "canonical-value",
            ["Tracking:SharedSecret"] = "stale-value"
        };
        Assert.Equal("canonical-value", AnalyticsIngestConfiguration.ResolveSecret(k => values.GetValueOrDefault(k), _ => null));
    }

    [Theory]
    [InlineData("LeadIngest:SharedSecret")]
    [InlineData("Tracking:SharedSecret")]
    [InlineData("TRACKING_SHARED_SECRET")]
    public void LegacyKeysRemainCompatibleWithoutChangingSecretBytes(string key)
    {
        Assert.Equal(" legacy-value ", AnalyticsIngestConfiguration.ResolveSecret(k => k == key ? " legacy-value " : "", _ => null));
    }

    [Fact]
    public void CanonicalDeploymentSettingWinsOverLaterVaultProvider()
        => Assert.Equal("receiver-value", AnalyticsIngestConfiguration.ResolveSecret(_ => "stale-vault", _ => "receiver-value"));

    [Fact]
    public void MissingSecretRemainsUnavailable()
        => Assert.Null(AnalyticsIngestConfiguration.ResolveSecret(_ => " ", _ => null));
}
