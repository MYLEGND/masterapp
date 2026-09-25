using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MetaSignalBrowserDispatchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"browserDispatchStatus\":\"invoked\"}")]
    public void MissingOrConflictingEvidenceRemainsUnverified(string? metadata)
        => Assert.Equal("unverified", MetaSignalBrowserDispatch.Resolve(false, metadata));

    [Fact]
    public void DerivedLandingDoesNotInventBrowserAttempt()
    {
        var status = MetaSignalBrowserDispatch.Resolve(false,
            "{\"bridgeSource\":\"analytics_events\",\"sourceAnalyticsEventType\":\"quote_landing_view\"}");
        Assert.Equal("derived_analytics", status);
        Assert.False(MetaSignalBrowserDispatch.IsFailure(status));
    }

    [Theory]
    [InlineData("disabled", false)]
    [InlineData("not_required", false)]
    [InlineData("human_gate", false)]
    [InlineData("pixel_unavailable", true)]
    [InlineData("invocation_failed", true)]
    public void BridgePreservesExplicitBrowserOutcome(string status, bool failure)
    {
        var metadata = "{\"analyticsMetadata\":{\"Metadata\":{\"BrowserMetadata\":{\"browserDispatchStatus\":\"" + status + "\"}}}}";
        Assert.Equal(status, MetaSignalBrowserDispatch.Resolve(false, metadata));
        Assert.Equal(failure, MetaSignalBrowserDispatch.IsFailure(status));
    }

    [Fact]
    public void RecordedBrowserInvocationDoesNotBecomeDeliveryAcknowledgement()
        => Assert.Equal("invoked", MetaSignalBrowserDispatch.Resolve(true, null));
}
