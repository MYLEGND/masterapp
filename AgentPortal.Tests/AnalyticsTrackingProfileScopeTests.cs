using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Threading;
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

public sealed class AnalyticsTrackingProfileScopeTests
{
    [Theory]
    [InlineData(ScopeType.Agent)]
    [InlineData(ScopeType.Founder)]
    public async Task ExpandsOnlyProfilesForTheSelectedOwner(ScopeType scopeType)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var selected = Profile("owner@example.test", "current");
        var alias = Profile("owner@example.test", "historical");
        var foreign = Profile("foreign@example.test", "foreign");
        db.AgentTrackingProfiles.AddRange(selected, alias, foreign);
        await db.SaveChangesAsync();

        var ids = await AnalyticsTrackingProfileScope.ResolveAsync(db, new ScopeContext
        {
            ScopeType = scopeType,
            AgentTrackingProfileId = selected.Id
        });

        Assert.NotNull(ids);
        Assert.Equal(2, ids.Length);
        Assert.Contains(selected.Id, ids);
        Assert.Contains(alias.Id, ids);
        Assert.DoesNotContain(foreign.Id, ids);
    }

    [Fact]
    public async Task UnknownProfileCannotExpandToBlankIdentityProfiles()
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.AgentTrackingProfiles.Add(Profile("", "unassigned"));
        await db.SaveChangesAsync();
        var missingId = Guid.NewGuid();

        var ids = await AnalyticsTrackingProfileScope.ResolveAsync(db, ScopeContext.ForAgent(missingId));

        Assert.Equal(new[] { missingId }, ids);
    }

    [Theory]
    [InlineData(ScopeType.Agent)]
    [InlineData(ScopeType.Founder)]
    public async Task InvalidOrMixedOwnerFailsClosed(ScopeType scopeType)
    {
        using var db = ControllerTestHelpers.BuildDb();
        Assert.Empty((await AnalyticsTrackingProfileScope.ResolveAsync(db,
            new ScopeContext { ScopeType = scopeType }))!);
        Assert.Empty((await AnalyticsTrackingProfileScope.ResolveAsync(db,
            new ScopeContext { ScopeType = scopeType, AgentTrackingProfileId = Guid.Empty }))!);
        Assert.Empty((await AnalyticsTrackingProfileScope.ResolveAsync(db,
            new ScopeContext
            {
                ScopeType = scopeType,
                AgentTrackingProfileId = Guid.NewGuid(),
                CommerceBusinessId = Guid.NewGuid()
            }))!);
    }

    [Fact]
    public async Task NonProfileScopesDoNotAcquireAgentAliases()
    {
        using var db = ControllerTestHelpers.BuildDb();
        Assert.Null(await AnalyticsTrackingProfileScope.ResolveAsync(db, ScopeContext.Global));
        Assert.Null(await AnalyticsTrackingProfileScope.ResolveAsync(db, ScopeContext.ForBusiness(Guid.NewGuid())));
    }

    [Fact]
    public async Task FounderMetaCredentialsComeOnlyFromCanonicalFounderConnection()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var connections = new MarketingConnectionStore(db, protector);
        var founder = Profile("founder@example.test", "founder");
        db.AgentTrackingProfiles.Add(founder);
        await db.SaveChangesAsync();
        var founderId = founder.Id;
        await connections.SaveAdsAsync(MarketingOwnerScope.Agent(founderId), new MetaAdsConnectionRecord
        {
            AgentTrackingProfileId = founderId, AccessToken = "agent-token", AccountId = "agent-account"
        });
        await connections.SaveAdsAsync(MarketingOwnerScope.Founder, new MetaAdsConnectionRecord
        {
            AccessToken = "founder-token", AccountId = "act_founder-account"
        });
        var service = MetaService(db, connections);

        var result = await Credentials(service, ScopeContext.ForFounder(founderId));

        Assert.Equal(("founder-token", "founder-account"), result);
    }

    [Fact]
    public async Task MissingFounderMetaConnectionCannotInheritAgentOrConfiguredCredentials()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var connections = new MarketingConnectionStore(db, protector);
        var founder = Profile("founder@example.test", "founder");
        db.AgentTrackingProfiles.Add(founder);
        await db.SaveChangesAsync();
        var founderId = founder.Id;
        await connections.SaveAdsAsync(MarketingOwnerScope.Agent(founderId), new MetaAdsConnectionRecord
        {
            AgentTrackingProfileId = founderId, AccessToken = "agent-token", AccountId = "agent-account"
        });

        var result = await Credentials(MetaService(db, connections), ScopeContext.ForFounder(founderId));

        Assert.Equal((string.Empty, string.Empty), result);
    }

    [Theory]
    [InlineData(ScopeType.Agent)]
    [InlineData(ScopeType.Founder)]
    [InlineData((ScopeType)999)]
    public async Task InvalidOwnerCannotAcquireGlobalMetaCredentials(ScopeType scopeType)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var service = MetaService(db, new MarketingConnectionStore(db, protector));
        Assert.Equal((string.Empty, string.Empty), await Credentials(service,
            new ScopeContext { ScopeType = scopeType, AgentTrackingProfileId = Guid.Empty }));
        Assert.Equal((string.Empty, string.Empty), await Credentials(service,
            new ScopeContext { ScopeType = scopeType, AgentTrackingProfileId = Guid.NewGuid(), CommerceBusinessId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task FounderCampaignReportsIncludeOwnerAliasesButRejectForeignAndMixedOwners()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var founder = Profile("founder@example.test", "founder");
        var alias = Profile("founder@example.test", "alias");
        var foreign = Profile("other@example.test", "other");
        db.AgentTrackingProfiles.AddRange(founder, alias, foreign);
        var now = DateTime.UtcNow;
        db.AnalyticsEvents.AddRange(
            new AnalyticsEvent { AgentTrackingProfileId = alias.Id, EventId = Guid.NewGuid(), EventType = "Lead", EventUtc = now },
            new AnalyticsEvent { AgentTrackingProfileId = foreign.Id, EventId = Guid.NewGuid(), EventType = "Lead", EventUtc = now },
            new AnalyticsEvent { AgentTrackingProfileId = alias.Id, CommerceBusinessId = Guid.NewGuid(), EventId = Guid.NewGuid(), EventType = "Lead", EventUtc = now });
        await db.SaveChangesAsync();
        var scope = ScopeContext.ForFounder(founder.Id);
        var ids = await AnalyticsTrackingProfileScope.ResolveAsync(db, scope);
        var leadMethod = typeof(MetaAdsService).GetMethod("LeadScopePredicate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var predicate = ((Expression<Func<WebsiteLead, bool>>)leadMethod.Invoke(null, new object?[] { scope, ids })!).Compile();
        Assert.True(predicate(new WebsiteLead { AgentTrackingProfileId = alias.Id }));
        Assert.False(predicate(new WebsiteLead { AgentTrackingProfileId = foreign.Id }));
        Assert.False(predicate(new WebsiteLead { AgentTrackingProfileId = alias.Id, CommerceBusinessId = Guid.NewGuid() }));
        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest { FromUtc = now.AddMinutes(-1), ToUtc = now.AddMinutes(1), QualityMode = TrafficQualityMode.AllTraffic };
        var rows = await analytics.LoadAttributedEventsAsync(range, scope);
        Assert.Equal(alias.Id, Assert.Single(rows).AgentTrackingProfileId);
    }

    private static MetaAdsService MetaService(Infrastructure.Data.MasterAppDbContext db, MarketingConnectionStore connections) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Founder:Upn"] = "founder@example.test", ["MetaAds:AccessToken"] = "global-token", ["MetaAds:DefaultAccountId"] = "global-account" }).Build(),
            db, Mock.Of<IHttpClientFactory>(), new Mock<IMetaAdsConnectionStore>(MockBehavior.Strict).Object,
            Mock.Of<IAnalyticsQueryService>(), NullLogger<MetaAdsService>.Instance, connections);

    private static Task<(string Token, string AccountId)> Credentials(MetaAdsService service, ScopeContext scope) =>
        (Task<(string Token, string AccountId)>)typeof(MetaAdsService)
            .GetMethod("ResolveCredentialsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object[] { scope, CancellationToken.None })!;

    private static AgentTrackingProfile Profile(string upn, string slug) => new()
    {
        Id = Guid.NewGuid(), AgentUpn = upn, AgentUserId = Guid.NewGuid().ToString(), Slug = slug
    };
}
