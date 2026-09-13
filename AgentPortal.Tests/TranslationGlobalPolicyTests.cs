using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TranslationGlobalPolicyTests
{
    private static string Founder => Environment.GetEnvironmentVariable("FOUNDER_OID")
        ?? Environment.GetEnvironmentVariable("FounderOid") ?? "b5b95fe7-66a0-4e91-8596-38faee82a6cd";

    [Fact]
    public async Task GlobalPolicy_IsSharedAcrossScopes_AndRetainsConsumedUsageWhenReduced()
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new MasterAppDbContext(options);
        await using var otherScope = new MasterAppDbContext(options);
        var actor = await AddAgentAsync(db);
        var authority = Authority(db);
        var global = await authority.SetGlobalLimitAsync(Founder, 100, null);
        var reserved = await authority.TryReserveAsync(new(actor, new string('a', 64), "en", "fr", "AzureTranslator", 40));
        Assert.True(reserved.Succeeded);
        await authority.CompleteAsync(reserved.Reservation!, true, true, null);
        await Authority(otherScope).SetGlobalLimitAsync(Founder, 20, global.Version);
        var snapshot = await Authority(db).GetSnapshotAsync(actor);
        Assert.Equal(20, snapshot.CharacterAllowance);
        Assert.Equal(40, snapshot.ConsumedCharacters);
        Assert.Equal(0, snapshot.RemainingCharacters);
        var denied = await authority.TryReserveAsync(new(actor, new string('b', 64), "en", "fr", "AzureTranslator", 1));
        Assert.Equal("translation_quota_exhausted", denied.ErrorCode);
    }

    [Fact]
    public async Task IndividualAllowance_RemainsIndependent_AndCanReturnToGlobalPolicy()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var actor = await AddAgentAsync(db);
        var authority = Authority(db);
        var global = await authority.SetGlobalLimitAsync(Founder, 100, null);
        await authority.SetEntitlementAsync(Founder, new(actor, 300, false, "FounderCustom", true));
        global = await authority.SetGlobalLimitAsync(Founder, 150, global.Version);
        Assert.Equal(300, (await authority.GetSnapshotAsync(actor)).CharacterAllowance);
        await authority.SetEntitlementAsync(Founder, new(actor, 150, false, "GlobalPolicy", false));
        await authority.SetGlobalLimitAsync(Founder, 200, global.Version);
        Assert.Equal(200, (await Authority(db).GetSnapshotAsync(actor)).CharacterAllowance);
        Assert.Single(await db.LegendTranslationEntitlements.ToListAsync());
    }

    [Fact]
    public async Task GlobalPolicy_RejectsNonFounderAndStaleEdits()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var authority = Authority(db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.SetGlobalLimitAsync("ordinary", 100, null));
        Assert.Empty(await db.Set<LegendTranslationGlobalPolicy>().ToListAsync());
        var first = await authority.SetGlobalLimitAsync(Founder, 100, null);
        await authority.SetGlobalLimitAsync(Founder, 200, first.Version);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => authority.SetGlobalLimitAsync(Founder, 900, first.Version));
        Assert.Equal(200, (await authority.GetGlobalLimitAsync()).CharacterAllowance);
        await Assert.ThrowsAsync<ArgumentException>(() => authority.SetGlobalLimitAsync(Founder, -1, null));
    }

    [Fact]
    public async Task FounderSearch_IncludesActiveAgents_AndExcludesClosedAgents()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var actor = await AddAgentAsync(db);
        var authority = Authority(db);
        Assert.Single((await authority.SearchFounderAccountsAsync("Example", 8)).Accounts);
        var profile = await db.AgentProfiles.SingleAsync();
        db.AccountLifecycleRecords.Add(new AccountLifecycleRecord
        {
            Id = Guid.NewGuid(), ProfileId = profile.Id, UserId = actor.UserId,
            ParticipantType = actor.ParticipantType, State = Domain.Accounts.AccountLifecycleStates.Closed,
            ClosedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        Assert.Empty((await authority.SearchFounderAccountsAsync("Example", 8)).Accounts);
        Assert.False(await authority.IsFounderEntitlementEligibleAsync(actor));
    }

    private static TranslationEntitlementAuthority Authority(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Founder:Oid"] = Founder,
            ["LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance"] = "0"
        }).Build();
        return new(db, new ControlledResourceAccessService(db, configuration), configuration,
            NullLogger<TranslationEntitlementAuthority>.Instance);
    }

    private static async Task<MessagingActor> AddAgentAsync(MasterAppDbContext db)
    {
        var actor = new MessagingActor("ordinary-agent", MessagingParticipantTypes.Agent);
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(), AgentUserId = actor.UserId, AgentUpn = "example@example.test",
            FullName = "Example Agent", IsActive = true
        });
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = actor.UserId, ParticipantType = actor.ParticipantType,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = Founder
        });
        await db.SaveChangesAsync();
        return actor;
    }
}
