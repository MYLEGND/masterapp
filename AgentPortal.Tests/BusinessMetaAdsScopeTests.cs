using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessMetaAdsScopeTests
{
    [Fact]
    public async Task MissingBusinessAdsConnectionDoesNotUseGlobalConfiguredAccount()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "business" };
        db.Add(business); await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["MetaAds:Enabled"] = "true", ["MetaAds:AccessToken"] = "global-token", ["MetaAds:DefaultAccountId"] = "123"
        }).Build();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var agentConnections = new Mock<IMetaAdsConnectionStore>(MockBehavior.Strict);
        var service = new MetaAdsService(config, db, factory.Object, agentConnections.Object,
            Mock.Of<IAnalyticsQueryService>(), NullLogger<MetaAdsService>.Instance, new(db, protector));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCampaignsAsync(
            TimeRangeRequest.FromPreset("7d", null, null, TimeZoneInfo.Utc), ScopeContext.ForBusiness(business.Id)));
        factory.VerifyNoOtherCalls();
        agentConnections.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FounderProfileNeverUsesAgentLegacyCredentialsAndGlobalNeverUsesProcessCredentials(bool connected)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var founder = new AgentTrackingProfile { Id = Guid.NewGuid(), AgentUserId = "founder", AgentUpn = "founder@example.com", Slug = "founder", Status = "Active" };
        db.Add(founder); await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Founder:Upn"] = founder.AgentUpn, ["MetaAds:Enabled"] = "true",
            ["MetaAds:AccessToken"] = "global-token", ["MetaAds:DefaultAccountId"] = "999"
        }).Build();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var connections = new MarketingConnectionStore(db, protector);
        if (connected) await connections.SaveAdsAsync(MarketingOwnerScope.Founder, new() { AccessToken = "founder-token", AccountId = "act_123" });
        await connections.SaveAdsAsync(MarketingOwnerScope.Agent(founder.Id), new() { AgentTrackingProfileId = founder.Id, AccessToken = "wrong-agent-token", AccountId = "act_456" });
        var legacy = new Mock<IMetaAdsConnectionStore>(MockBehavior.Strict);
        var service = new MetaAdsService(config, db, Mock.Of<IHttpClientFactory>(), legacy.Object,
            Mock.Of<IAnalyticsQueryService>(), NullLogger<MetaAdsService>.Instance, connections);
        var method = typeof(MetaAdsService).GetMethod("ResolveCredentialsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task<(string Token,string AccountId)> Resolve(ScopeContext scope) =>
            await (Task<(string Token,string AccountId)>)method.Invoke(service, new object[] { scope, System.Threading.CancellationToken.None })!;
        var expected = connected ? ("founder-token", "123") : (string.Empty, string.Empty);
        Assert.Equal(expected, await Resolve(ScopeContext.ForAgent(founder.Id)));
        Assert.Equal(expected, await Resolve(ScopeContext.ForFounder(founder.Id)));
        Assert.Equal((string.Empty,string.Empty), await Resolve(ScopeContext.Global));
        Assert.Equal((string.Empty,string.Empty), await Resolve(ScopeContext.ForSite("legendwebsite")));
        legacy.VerifyNoOtherCalls();
    }

    [Fact]
    public void CampaignLeadAttributionRejectsOtherBusinessesAgentLeadsAndAmbiguousScope()
    {
        var own = Guid.NewGuid();
        var method = typeof(MetaAdsService).GetMethod("LeadScopePredicate", BindingFlags.NonPublic | BindingFlags.Static)!;
        Func<WebsiteLead,bool> Filter(ScopeContext scope) => ((Expression<Func<WebsiteLead,bool>>)method.Invoke(null, new object?[] { scope, null })!).Compile();
        var filter = Filter(ScopeContext.ForBusiness(own));
        Assert.True(filter(new WebsiteLead { CommerceBusinessId = own }));
        Assert.False(filter(new WebsiteLead { CommerceBusinessId = Guid.NewGuid() }));
        Assert.False(filter(new WebsiteLead { AgentTrackingProfileId = Guid.NewGuid() }));
        Assert.False(filter(new WebsiteLead { CommerceBusinessId = own, AgentTrackingProfileId = Guid.NewGuid() }));
        Assert.False(Filter(new ScopeContext { ScopeType = ScopeType.Business })(new WebsiteLead()));
    }
}
