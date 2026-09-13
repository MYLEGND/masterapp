using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Domain.Entities;
using Domain.Billing;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TranslationEntitlementIdentityTests
{
    private static readonly MessagingActor Canonical = new("quota-member-record", MessagingParticipantTypes.Client);
    private static readonly MessagingActor Alias = new("quota-member-oid", MessagingParticipantTypes.Client);
    private static DateOnly Period => new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

    [Theory]
    [InlineData("quota-member-oid")]
    [InlineData(" QUOTA-MEMBER-OID ")]
    public async Task ExternalIdentityUsesTheExistingGrantedCanonicalEntitlementWithZeroDefault(string externalUserId)
    {
        var externalActor = new MessagingActor(externalUserId, MessagingParticipantTypes.Client);
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAsync(db);
        var access = new ControlledResourceAccessService(db);
        Assert.Equal(ControlledResourceAccessStates.Granted,
            (await access.GetAccessAsync(externalActor, ControlledResourceTypes.LanguageTranslation)).State);
        var authority = Authority(db);
        var snapshot = await authority.GetSnapshotAsync(externalActor);
        Assert.Equal(10, snapshot.CharacterAllowance);
        Assert.Equal("FounderManaged", snapshot.EntitlementSource);
        var reserved = await authority.TryReserveAsync(Request(externalActor, "alias-first", 6));
        Assert.True(reserved.Succeeded, reserved.ErrorCode);
        Assert.Equal(Canonical, reserved.Reservation!.Account);
        await authority.CompleteAsync(reserved.Reservation, true, true, null);
        var denied = await authority.TryReserveAsync(Request(Canonical, "canonical-next", 5));
        Assert.False(denied.Succeeded);
        Assert.Equal("translation_quota_exhausted", denied.ErrorCode);
        var usage = await db.LegendTranslationUsagePeriods.SingleAsync();
        Assert.Equal(Canonical.UserId, usage.UserId);
        Assert.Equal(6, usage.ConsumedCharacters);
        Assert.Equal(0, usage.ReservedCharacters);
        Assert.All(await db.LegendTranslationUsageLedgers.ToListAsync(), ledger => Assert.Equal(Canonical.UserId, ledger.UserId));
    }

    [Fact]
    public async Task AlternatingIdentityFormsCannotReserveTwoDefaultAllowances()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAsync(db, entitlement: false);
        var authority = Authority(db, 10);
        Assert.True((await authority.TryReserveAsync(Request(Alias, "default-alias", 6))).Succeeded);
        var second = await authority.TryReserveAsync(Request(Canonical, "default-canonical", 5));
        Assert.False(second.Succeeded);
        Assert.Equal("translation_quota_exhausted", second.ErrorCode);
        Assert.Equal(6, (await db.LegendTranslationUsagePeriods.SingleAsync()).ReservedCharacters);
    }

    [Fact]
    public async Task AvoidedUsageAndSnapshotShareTheCanonicalMemberAccount()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAsync(db);
        var authority = Authority(db);
        await authority.RecordAvoidedAsync(Alias, TranslationAvoidedPath.SameLanguage, 7);
        await authority.RecordAvoidedAsync(Canonical, TranslationAvoidedPath.SameLanguage, 8);
        var usage = await db.LegendTranslationUsagePeriods.SingleAsync();
        Assert.Equal(Canonical.UserId, usage.UserId);
        Assert.Equal(15, usage.SameLanguageCharactersAvoided);
        Assert.Equal(await authority.GetSnapshotAsync(Canonical), await authority.GetSnapshotAsync(Alias));
    }

    [Fact]
    public async Task AgentWithTheSameIdentifierKeepsItsOwnTypedQuota()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAsync(db);
        var agent = new MessagingActor(Alias.UserId, MessagingParticipantTypes.Agent);
        db.ControlledResourceGrants.Add(Grant(agent));
        db.LegendTranslationEntitlements.Add(new LegendTranslationEntitlement
        {
            UserId = agent.UserId, ParticipantType = agent.ParticipantType, MonthlyCharacterAllowance = 2
        });
        await db.SaveChangesAsync();
        var authority = Authority(db);
        Assert.Equal(2, (await authority.GetSnapshotAsync(agent)).CharacterAllowance);
        var denied = await authority.TryReserveAsync(Request(agent, "agent-own-quota", 3));
        Assert.Equal("translation_quota_exhausted", denied.ErrorCode);
        Assert.True((await authority.TryReserveAsync(Request(Alias, "member-own-quota", 3))).Succeeded);
        Assert.Equal(2, await db.LegendTranslationUsagePeriods.CountAsync());
    }

    [Fact]
    public async Task AliasResolutionCannotCreatePermission()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAsync(db, grant: false);
        var result = await Authority(db).TryReserveAsync(Request(Alias, "ungranted-alias", 1));
        Assert.False(result.Succeeded);
        Assert.Equal("translation_access_revoked", result.ErrorCode);
        Assert.Empty(await db.LegendTranslationUsageLedgers.ToListAsync());
        Assert.Empty(await db.LegendTranslationUsagePeriods.ToListAsync());
    }

    [Fact]
    public async Task HistoricalAliasReservationRemainsVisibleAndSettlesItsPersistedOwner()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAsync(db);
        var canonicalUsage = Usage(Canonical.UserId, consumed: 2);
        var aliasUsage = Usage(Alias.UserId, reserved: 3);
        var ledger = new LegendTranslationUsageLedger
        {
            UserId = Alias.UserId, ParticipantType = Alias.ParticipantType, PeriodStart = Period,
            RequestReference = Reference("historical-reservation"), BillableCharacters = 3,
            State = "Reserved", ReservationExpiresUtc = DateTime.UtcNow.AddMinutes(1)
        };
        db.AddRange(canonicalUsage, aliasUsage, ledger);
        await db.SaveChangesAsync();
        var authority = Authority(db);
        foreach (var actor in new[] { Canonical, Alias })
        {
            var snapshot = await authority.GetSnapshotAsync(actor);
            Assert.Equal(2, snapshot.ConsumedCharacters);
            Assert.Equal(3, snapshot.ReservedCharacters);
            Assert.Equal(5, snapshot.RemainingCharacters);
            var blocked = await authority.TryReserveAsync(Request(actor, "blocked-" + actor.UserId, 1));
            Assert.False(blocked.Succeeded);
            Assert.Equal("translation_accounting_unavailable", blocked.ErrorCode);
        }
        // Even if a caller holds the now-canonical actor, durable ledger identity
        // owns settlement of the historical reservation. No row is re-keyed.
        await authority.CompleteAsync(new TranslationQuotaReservation(
            ledger.Id, Canonical, Period, 3, ledger.RequestReference), false, false, "cancelled");
        Assert.Equal(0, aliasUsage.ReservedCharacters);
        Assert.Equal(Alias.UserId, ledger.UserId);
        Assert.Equal("Released", ledger.State);
        Assert.True((await authority.TryReserveAsync(Request(Alias, "after-settlement", 3))).Succeeded);
        Assert.Equal(3, canonicalUsage.ReservedCharacters);
        Assert.Equal(2, canonicalUsage.ConsumedCharacters);
    }

    [Fact]
    public async Task HistoricalAliasConsumptionCannotBeHiddenByCanonicalLookup()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAsync(db);
        db.AddRange(Usage(Canonical.UserId, consumed: 2), Usage(Alias.UserId, consumed: 4));
        await db.SaveChangesAsync();
        var authority = Authority(db);
        var snapshot = await authority.GetSnapshotAsync(Alias);
        Assert.Equal(6, snapshot.ConsumedCharacters);
        Assert.Equal(4, snapshot.RemainingCharacters);
        var result = await authority.TryReserveAsync(Request(Canonical, "no-historical-reset", 1));
        Assert.False(result.Succeeded);
        Assert.Equal("translation_accounting_unavailable", result.ErrorCode);
        Assert.Equal(6, await db.LegendTranslationUsagePeriods.SumAsync(item => item.ConsumedCharacters));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FounderDirectoryRetainsAllUsageMetricsAcrossIdentityForms(bool includeAlias)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAsync(db);
        var profile = await db.ClientProfiles.SingleAsync();
        profile.CrmStatus = "Active";
        db.ClientSubscriptions.Add(new ClientSubscription
        {
            Id = Guid.NewGuid(), ClientProfileId = profile.Id, AcceptedOfferId = Guid.NewGuid(),
            OwnerAgentUserId = "test-founder", Status = ClientSubscriptionStatus.Active,
            PaymentStanding = ClientSubscriptionPaymentStanding.Current, MonthlyAmountCents = 1, Currency = "USD"
        });
        foreach (var userId in includeAlias ? new[] { Canonical.UserId, Alias.UserId } : new[] { Canonical.UserId })
        {
            var row = Usage(userId, consumed: 2, reserved: 1);
            row.SameLanguageCharactersAvoided = 3;
            row.TranslationMemoryCharactersAvoided = 4;
            row.StructuralCompositionCharactersAvoided = 5;
            row.ContextualCharactersAvoided = 6;
            row.PromotedTranslationModelCharactersAvoided = 7;
            row.ProviderObservationCharactersAvoided = 8;
            row.ProviderOperationCount = 9;
            row.ProviderBillableCharacters = 10;
            row.QuotaDeniedRequestCount = 11;
            row.ProviderFailureCount = 12;
            row.GroupUniqueTargetReuseCount = 13;
            db.Add(row);
        }
        await db.SaveChangesAsync();
        var account = Assert.Single((await Authority(db).SearchFounderAccountsAsync(null, 8)).Accounts);
        var multiplier = includeAlias ? 2 : 1;
        Assert.Equal(2 * multiplier, account.Entitlement.ConsumedCharacters);
        Assert.Equal(multiplier, account.Entitlement.ReservedCharacters);
        Assert.Equal(3 * multiplier, account.Usage.SameLanguageCharactersAvoided);
        Assert.Equal(4 * multiplier, account.Usage.TranslationMemoryCharactersAvoided);
        Assert.Equal(5 * multiplier, account.Usage.StructuralCompositionCharactersAvoided);
        Assert.Equal(6 * multiplier, account.Usage.ContextualCharactersAvoided);
        Assert.Equal(7 * multiplier, account.Usage.PromotedTranslationModelCharactersAvoided);
        Assert.Equal(8 * multiplier, account.Usage.ProviderObservationCharactersAvoided);
        Assert.Equal(9 * multiplier, account.Usage.ProviderOperationCount);
        Assert.Equal(10 * multiplier, account.Usage.ProviderBillableCharacters);
        Assert.Equal(11 * multiplier, account.Usage.QuotaDeniedRequestCount);
        Assert.Equal(12 * multiplier, account.Usage.ProviderFailureCount);
        Assert.Equal(13 * multiplier, account.Usage.GroupUniqueTargetReuseCount);
    }

    private static LegendTranslationUsagePeriod Usage(string userId, long consumed = 0, long reserved = 0) => new()
    {
        UserId = userId, ParticipantType = MessagingParticipantTypes.Client, PeriodStart = Period,
        ConsumedCharacters = consumed, ReservedCharacters = reserved
    };

    private static TranslationQuotaReservationRequest Request(MessagingActor actor, string reference, int amount) =>
        new(actor, Reference(reference), "en", "ht", "AzureTranslator", amount);

    private static string Reference(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ControlledResourceGrant Grant(MessagingActor actor) => new()
    {
        UserId = actor.UserId, ParticipantType = actor.ParticipantType,
        ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
        GrantedUtc = DateTime.UtcNow, GrantedByUserId = "test-founder"
    };

    private static async Task SeedAsync(MasterAppDbContext db, bool entitlement = true, bool grant = true)
    {
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = Guid.NewGuid(), ClientUserId = Canonical.UserId, ExternalIdentityObjectId = Alias.UserId,
            FirstName = "Independent", LastName = "Member", Email = "quota@example.test"
        });
        if (grant) db.ControlledResourceGrants.Add(Grant(Canonical));
        if (entitlement) db.LegendTranslationEntitlements.Add(new LegendTranslationEntitlement
        {
            UserId = Canonical.UserId, ParticipantType = Canonical.ParticipantType, MonthlyCharacterAllowance = 10
        });
        await db.SaveChangesAsync();
    }

    private static TranslationEntitlementAuthority Authority(MasterAppDbContext db, int defaultAllowance = 0) => new(
        db, new ControlledResourceAccessService(db),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance"] = defaultAllowance.ToString()
        }).Build(), NullLogger<TranslationEntitlementAuthority>.Instance);
}
