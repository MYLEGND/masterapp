using System;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.Tracking;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessMetaPixelResolutionTests
{
    [Fact]
    public async Task BusinessUsesOnlyItsConnectionAndNeverFallsBackToFounderOrOtherBusiness()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var protection = new EphemeralDataProtectionProvider();
        using var credentials = new MarketingCredentialProtector(protection);
        var connections = new MarketingConnectionStore(db, credentials);
        var business = new CommerceBusiness { Key = "business" };
        var other = new CommerceBusiness { Key = "other" };
        db.AddRange(business, other);
        await db.SaveChangesAsync();
        await connections.ImportProfileAsync(MarketingOwnerScope.Founder, "111", "founder-token", null);
        await connections.ImportProfileAsync(MarketingOwnerScope.Business(other.Id), "222", "other-token", null);
        var resolver = new MetaPixelResolutionService(new ConfigurationBuilder().Build(), db,
            new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance),
            new AgentMarketingProfileService(db, connections, protection), connections,
            NullLogger<MetaPixelResolutionService>.Instance);
        Assert.False((await resolver.ResolveForBusinessAsync(business.Id)).HasBrowserPixel);
        Assert.False((await resolver.ResolveForBusinessAsync(Guid.Empty)).HasBrowserPixel);
        await connections.ImportProfileAsync(MarketingOwnerScope.Business(business.Id), "333", "own-token", "test");
        var resolved = await resolver.ResolveForBusinessAsync(business.Id);
        Assert.Equal("333", resolved.PixelId);
        Assert.Equal("own-token", resolved.AccessToken);
        Assert.Equal(MetaPixelOwnerTypes.Business, resolved.PixelOwnerType);
        Assert.Null(resolved.AgentTrackingProfileId);
        var row = await db.MarketingConnections.FindAsync((await connections.GetStatusAsync(MarketingOwnerScope.Business(business.Id)))!.Id);
        row!.DisconnectedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.False((await resolver.ResolveForBusinessAsync(business.Id)).HasServerCapiCredentials);
        row.DisconnectedUtc = null;
        business.IsActive = false;
        await db.SaveChangesAsync();
        Assert.False((await resolver.ResolveForBusinessAsync(business.Id)).HasBrowserPixel);
    }
    [Fact]
    public async Task AgentWithoutOwnConnectionNeverInheritsFounderPixel()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var protection = new EphemeralDataProtectionProvider();
        using var credentials = new MarketingCredentialProtector(protection);
        var connections = new MarketingConnectionStore(db, credentials);
        var tracking = new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-1",
            AgentUpn = "agent@example.test",
            Slug = "agent-one",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.AgentTrackingProfiles.Add(tracking);
        await db.SaveChangesAsync();

        await connections.ImportProfileAsync(MarketingOwnerScope.Founder, "founder-pixel", "founder-token", "founder-test");

        var resolver = new MetaPixelResolutionService(new ConfigurationBuilder().Build(), db,
            new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance),
            new AgentMarketingProfileService(db, connections, protection), connections,
            NullLogger<MetaPixelResolutionService>.Instance);

        var resolved = await resolver.ResolveForLeadAsync(tracking.Id, tracking.Slug, isFounderPath: false);

        Assert.False(resolved.HasBrowserPixel);
        Assert.False(resolved.HasServerCapiCredentials);
        Assert.Equal(MetaPixelOwnerTypes.None, resolved.PixelOwnerType);
        Assert.Equal(tracking.Id, resolved.AgentTrackingProfileId);
        Assert.Equal(tracking.Slug, resolved.AgentSlug);
    }

    [Fact]
    public async Task AgentWithOwnConnectionUsesOnlyItsOwnPixelAndToken()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var protection = new EphemeralDataProtectionProvider();
        using var credentials = new MarketingCredentialProtector(protection);
        var connections = new MarketingConnectionStore(db, credentials);
        var tracking = new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-2",
            AgentUpn = "agent2@example.test",
            Slug = "agent-two",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.AgentTrackingProfiles.Add(tracking);
        await db.SaveChangesAsync();

        await connections.ImportProfileAsync(MarketingOwnerScope.Founder, "founder-pixel", "founder-token", "founder-test");
        await connections.ImportProfileAsync(MarketingOwnerScope.Agent(tracking.Id), "agent-pixel", "agent-token", "agent-test");

        var resolver = new MetaPixelResolutionService(new ConfigurationBuilder().Build(), db,
            new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance),
            new AgentMarketingProfileService(db, connections, protection), connections,
            NullLogger<MetaPixelResolutionService>.Instance);

        var resolved = await resolver.ResolveForLeadAsync(tracking.Id, tracking.Slug, isFounderPath: false);

        Assert.Equal("agent-pixel", resolved.PixelId);
        Assert.Equal("agent-token", resolved.AccessToken);
        Assert.Equal("agent-test", resolved.TestEventCode);
        Assert.Equal(MetaPixelOwnerTypes.Agent, resolved.PixelOwnerType);
        Assert.Equal(tracking.Id, resolved.AgentTrackingProfileId);
    }

}
