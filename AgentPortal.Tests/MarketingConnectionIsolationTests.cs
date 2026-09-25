using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MarketingConnectionIsolationTests
{
    [Fact]
    public void SharedMarketingAuthorityDoesNotReplaceHostCookieConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
        {
            ["DataProtection:BlobUri"] = "host-cookie-ring",
            ["DataProtection:KeyVaultKeyId"] = "host-cookie-vault",
            ["MarketingDataProtection:BlobUri"] = "https://example.blob.core.windows.net/keys/shared.xml",
            ["MarketingDataProtection:KeyVaultKeyId"] = "https://example.vault.azure.net/keys/shared"
        }).Build();
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment
        {
            ContentRootPath = System.IO.Path.GetTempPath(),
            EnvironmentName = "Production"
        };
        using var protector = MarketingCredentialProtector.CreateShared(config, environment);
        Assert.Equal("host-cookie-ring", config["DataProtection:BlobUri"]);
        Assert.Equal("host-cookie-vault", config["DataProtection:KeyVaultKeyId"]);
        config["MarketingDataProtection:KeyVaultKeyId"] = null;
        Assert.Throws<InvalidOperationException>(() => MarketingCredentialProtector.CreateShared(config, environment));
    }

    [Fact]
    public async Task SameAccountAcrossOwnersHasIndependentCredentialsAndDisconnect()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var a = MarketingOwnerScope.Business(Guid.NewGuid());
        var b = MarketingOwnerScope.Business(Guid.NewGuid());
        await store.SaveAdsAsync(a, new() { AccessToken = "business-a-secret", AccountId = "shared-ad-account" });
        await store.SaveAdsAsync(b, new() { AccessToken = "business-b-secret", AccountId = "shared-ad-account" });
        Assert.Equal("business-a-secret", (await store.GetAdsAsync(a))!.AccessToken);
        Assert.Equal("business-b-secret", (await store.GetAdsAsync(b))!.AccessToken);
        Assert.DoesNotContain("business-a-secret", (await store.GetStatusAsync(a))!.AdsAccessTokenCiphertext!);
        await store.DisconnectAsync(a);
        await store.ImportAsync(a, new() { AccessToken = "stale-cache-secret" });
        Assert.Null(await store.GetAdsAsync(a));
        Assert.Equal("business-b-secret", (await store.GetAdsAsync(b))!.AccessToken);
    }

    [Fact]
    public async Task SwappedCiphertextCannotDecryptForAnotherOwner()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var a = MarketingOwnerScope.Business(Guid.NewGuid());
        var b = MarketingOwnerScope.Business(Guid.NewGuid());
        await store.SaveAdsAsync(a, new() { AccessToken = "secret-a" });
        await store.SaveAdsAsync(b, new() { AccessToken = "secret-b" });
        var source = await db.MarketingConnections.SingleAsync(x => x.OwnerKey == a.Key);
        var target = await db.MarketingConnections.SingleAsync(x => x.OwnerKey == b.Key);
        target.AdsAccessTokenCiphertext = source.AdsAccessTokenCiphertext;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<CryptographicException>(() => store.GetAdsAsync(b));
    }

    [Fact]
    public async Task StaleSettingsRevisionCannotOverwriteAnotherEdit()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Founder;
        await store.ImportAsync(owner, null);
        var revision = (await store.GetStatusAsync(owner))!.Revision;
        await store.SaveSettingsAsync(owner, "123456", null, null, revision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => store.SaveSettingsAsync(owner, "999", null, null, revision));
        Assert.Equal("123456", (await store.GetStatusAsync(owner))!.PixelId);
    }

    [Fact]
    public async Task BusinessAnalyticsCannotUseJsonOrSharedSessionToReadForeignRows()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var now = DateTime.UtcNow;
        foreach (var business in new Guid?[] { owner, foreign, null })
        {
            db.AnalyticsEvents.Add(new()
            {
                EventId = Guid.NewGuid(), CommerceBusinessId = business, EventType = "page_view",
                SessionId = "same-session", VisitorId = "same-visitor", EventUtc = now, ReceivedUtc = now,
                Host = "shop.example.com", Environment = "production", MetadataJson = "{\"reportingOwner\":\"business\"}"
            });
            db.MetaSignalEvents.Add(new()
            {
                CommerceBusinessId = business, EventId = "same-event", EventName = "Lead",
                SessionId = "same-session", CreatedUtc = now
            });
        }
        await db.SaveChangesAsync();
        var query = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var scope = ScopeContext.ForBusiness(owner);
        var range = new TimeRangeRequest { FromUtc = now.AddHours(-1), ToUtc = now.AddHours(1), QualityMode = TrafficQualityMode.AllTraffic };
        var events = await query.LoadAttributedEventsAsync(range, scope);
        Assert.Single(events);
        Assert.Equal(owner, events[0].CommerceBusinessId);
        var signals = await query.LoadScopedMetaEventsAsync(range, scope, events);
        Assert.Single(signals);
        Assert.Equal(owner, signals[0].CommerceBusinessId);
        Assert.Empty(await query.LoadAttributedEventsAsync(range, new ScopeContext { ScopeType = ScopeType.Business }));
        Assert.Empty(await query.LoadAttributedEventsAsync(range, new ScopeContext { ScopeType = ScopeType.Agent }));
    }
}
