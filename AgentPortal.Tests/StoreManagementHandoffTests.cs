using System;
using System.Net.Http;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class StoreManagementHandoffTests
{
    [Fact]
    public async Task Handoff_is_bound_to_browser_tenant_actor_capability_and_consumed_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var business = new CommerceBusiness { Key = "synthetic-handoff", IsActive = true, Status = "Active" };
        var profile = new ClientProfile { ClientUserId = "synthetic-actor" };
        var member = new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id, RoleKey = "staff", CanManageAnalytics = true, CanManageStorefront = false };
        var state = new WebsiteContentState { SiteKey = "business", OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id),
            CommerceBusinessId = business.Id, ProvisionedStoreBusinessId = business.Id };
        db.AddRange(business, profile, member, state, new WebsiteDomainBinding { CommerceBusinessId = business.Id,
            Hostname = "store.example.invalid", Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().Build();
        var stores = new CommerceStoreContextService(db, new CommerceBusinessScopeResolver(db), new ParfaitBusinessScopeService(db),
            new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config), config);
        using var tickets = new WebsiteEditorTicketProtector(new EphemeralDataProtectionProvider());
        const string nonce = "synthetic-browser-nonce";
        var request = new StoreManagementLoginRequest(business.Id, "business", state.OwnerKey, "https://store.example.invalid",
            "/internal/analytics", WebsiteEditorHandoffToken.Hash(nonce), DateTime.UtcNow.AddMinutes(4));
        var scope = (await stores.ResolveManagementScopeAsync(request))!;
        var token = tickets.ProtectStoreLoginRequest(request);
        Assert.Null(await WebsiteEditorHandoffService.CreateStoreAsync(db, stores, tickets, token, "outsider", "analytics"));
        Assert.Null(await WebsiteEditorHandoffService.CreateStoreAsync(db, stores, tickets, token, profile.ClientUserId, "settings"));
        var issued = (await WebsiteEditorHandoffService.CreateStoreAsync(db, stores, tickets, token, profile.ClientUserId, "analytics"))!.Value;
        string? Capability(string path) => path == "/internal/analytics" ? "analytics" : null;
        Assert.Null(await WebsiteEditorHandoffService.RedeemStoreAsync(db, stores, scope, issued.OpaqueState, "foreign-browser", Capability));
        Assert.Null(await WebsiteEditorHandoffService.RedeemStoreAsync(db, stores, scope with { CommerceBusinessId = Guid.NewGuid() }, issued.OpaqueState, nonce, Capability));
        Assert.Null(await WebsiteEditorHandoffService.RedeemStoreAsync(db, stores, scope, issued.OpaqueState, nonce, _ => "settings"));
        Assert.Null((await db.ClientIdentityContinuations.SingleAsync()).ConsumedUtc);
        var redeemed = await WebsiteEditorHandoffService.RedeemStoreAsync(db, stores, scope, issued.OpaqueState, nonce, Capability);
        Assert.NotNull(redeemed);
        Assert.Equal(profile.Id, redeemed.Value.Actor.ClientProfileId);
        Assert.Equal("/internal/analytics", redeemed.Value.ReturnPath);
        Assert.Null(await WebsiteEditorHandoffService.RedeemStoreAsync(db, stores, scope, issued.OpaqueState, nonce, Capability));
        var expiring = (await WebsiteEditorHandoffService.CreateStoreAsync(db, stores, tickets, token, profile.ClientUserId, "analytics"))!.Value;
        var clock = new TestClock();
        Assert.Null(await WebsiteEditorHandoffService.RedeemStoreAsync(db, stores, scope, expiring.OpaqueState, nonce,
            path => { clock.Now = clock.Now.AddMinutes(3); return Capability(path); }, timeProvider: clock));
        var staleDomain = (await WebsiteEditorHandoffService.CreateStoreAsync(db, stores, tickets, token, profile.ClientUserId, "analytics"))!.Value;
        var domain = await db.Set<WebsiteDomainBinding>().SingleAsync();
        domain.CertificateStatus = "pending";
        await db.SaveChangesAsync();
        Assert.Null(await WebsiteEditorHandoffService.RedeemStoreAsync(db, stores, scope, staleDomain.OpaqueState, nonce, Capability));
        Assert.Null((await db.ClientIdentityContinuations.AsNoTracking().SingleAsync(r => r.TokenHash == WebsiteEditorHandoffToken.Hash(staleDomain.OpaqueState))).ConsumedUtc);
        domain.CertificateStatus = "active";
        await db.SaveChangesAsync();
        var second = (await WebsiteEditorHandoffService.CreateStoreAsync(db, stores, tickets, token, profile.ClientUserId, "analytics"))!.Value;
        member.Status = "Revoked";
        await db.SaveChangesAsync();
        Assert.Null(await WebsiteEditorHandoffService.RedeemStoreAsync(db, stores, scope, second.OpaqueState, nonce, Capability));
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

}
