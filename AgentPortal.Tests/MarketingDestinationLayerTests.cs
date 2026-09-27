using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MarketingDestinationLayerTests
{
    [Fact]
    public void RegistryExposesOneCanonicalProviderCatalog()
    {
        var registry = new MarketingDestinationRegistry(new IMarketingDestination[]
        {
            new OpenAiMarketingDestination(new FakeOpenAiAuthority()),
            new FakeDestination(MarketingDestinationKeys.Meta)
        });

        Assert.Equal(new[] { MarketingDestinationKeys.Meta, MarketingDestinationKeys.OpenAi }, registry.Keys);
        Assert.Equal(MarketingDestinationKeys.Meta, registry.GetRequired(" META ").Key);
        Assert.Equal(MarketingDestinationKeys.OpenAi, registry.GetRequired("openai").Key);
    }

    [Fact]
    public void DuplicateProviderRegistrationFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => new MarketingDestinationRegistry(new IMarketingDestination[]
        {
            new FakeDestination("meta"),
            new FakeDestination("META")
        }));
    }

    [Fact]
    public async Task OpenAiDestinationIsRegisteredButCannotSendBeforeItsImplementationStep()
    {
        var decision = await new OpenAiMarketingDestination(new FakeOpenAiAuthority()).EvaluateAsync(
            MarketingOwnerScope.Business(Guid.NewGuid()),
            new MarketingOutcome("Lead", "event-1", IsServerAuthority: true));

        Assert.Equal(MarketingDestinationKeys.OpenAi, decision.DestinationKey);
        Assert.False(decision.Supported);
        Assert.False(decision.Configured);
        Assert.False(decision.Eligible);
        Assert.Equal("destination_not_configured", decision.Reason);
    }

    [Fact]
    public async Task MetaAdapterReusesExistingConnectionAndSingleTruthEligibility()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());

        await store.ImportAsync(owner, null, pixelId: "123456", capiToken: "secret");
        var adapter = new MetaMarketingDestination(store);

        var eligibleMetadata = MetaSignalSingleTruthPolicy.BuildMetadataJson(
            "Lead",
            null,
            null,
            new { },
            isBrowserSignal: false,
            isServerAuthority: true,
            metaServerAuthorityEligible: true,
            metaSingleTruthDispatchEligible: true,
            metaPipelineOrigin: "test");

        var eligible = await adapter.EvaluateAsync(
            owner,
            new MarketingOutcome("Lead", "event-1", IsServerAuthority: true, eligibleMetadata));

        Assert.True(eligible.Supported);
        Assert.True(eligible.Configured);
        Assert.True(eligible.Eligible);
        Assert.Equal("eligible", eligible.Reason);

        var forged = await adapter.EvaluateAsync(
            owner,
            new MarketingOutcome("Lead", "event-2", IsServerAuthority: true, "{}"));

        Assert.True(forged.Supported);
        Assert.True(forged.Configured);
        Assert.False(forged.Eligible);
        Assert.Equal("canonical_outcome_not_dispatch_eligible", forged.Reason);
    }

    [Fact]
    public async Task ProviderSpecificLookupCannotBleedIntoExistingMetaAuthority()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());

        db.MarketingConnections.Add(new MarketingConnection
        {
            OwnerKey = owner.Key,
            OwnerType = owner.OwnerType,
            CommerceBusinessId = owner.CommerceBusinessId,
            Provider = MarketingDestinationKeys.OpenAi
        });
        await db.SaveChangesAsync();

        Assert.Null(await store.GetStatusAsync(owner));
        Assert.NotNull(await store.GetStatusAsync(owner, MarketingDestinationKeys.OpenAi));
    }

    [Fact]
    public void SharedRegistrationMakesRegistryAvailableToEveryHostUsingMarketingConnections()
    {
        var services = new ServiceCollection();
        MarketingServiceRegistration.AddMarketingConnections(services);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IMarketingDestination) &&
            descriptor.ImplementationType == typeof(MetaMarketingDestination));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IMarketingDestination) &&
            descriptor.ImplementationType == typeof(OpenAiMarketingDestination));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IMarketingDestinationRegistry) &&
            descriptor.ImplementationType == typeof(MarketingDestinationRegistry));
    }

    private sealed class FakeOpenAiAuthority : IOpenAiAdsAccountConnectionAuthority
    {
        public Task<OpenAiAdsConnectionSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenAiAdsConnectionSnapshot(owner, false, false, Guid.Empty, null, null, null, null, null, null, null, [], null, null, false, false, null, null, null));

        public Task<OpenAiAdsConnectionSecrets> GetSecretsAsync(MarketingOwnerScope owner, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenAiAdsConnectionSecrets());

        public Task<OpenAiAdsConnectionSnapshot> BindVerifiedAsync(MarketingOwnerScope owner, VerifiedOpenAiAdsAccount verifiedAccount, OpenAiAdsConnectionSecrets secrets, Guid? expectedRevision = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OpenAiAdsConnectionSnapshot> DisconnectAsync(MarketingOwnerScope owner, Guid expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDestination(string key) : IMarketingDestination
    {
        public string Key { get; } = key;

        public ValueTask<MarketingDestinationDecision> EvaluateAsync(
            MarketingOwnerScope owner,
            MarketingOutcome outcome,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MarketingDestinationDecision(Key, true, true, true, "eligible"));
    }
}
