using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderTranslationEntitlementTests
{
    [Theory]
    [InlineData("Agent", false)]
    [InlineData("Client", false)]
    [InlineData("Client", true)]
    public async Task ConfiguredFounder_IsUnlimitedAndMeteredThroughEitherProfile(string participantType, bool useAlias)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var founderOid = Environment.GetEnvironmentVariable("FOUNDER_OID")
            ?? Environment.GetEnvironmentVariable("FounderOid")
            ?? "776180e2-f234-4f60-a8ae-5aa01d664cef";
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = Guid.NewGuid(), ClientUserId = "founder-member-alias",
            ExternalIdentityObjectId = founderOid, FirstName = "Founder", LastName = "Member"
        });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Founder:Oid"] = founderOid,
            ["LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance"] = "0"
        }).Build();
        var access = new ControlledResourceAccessService(db, configuration);
        var authority = new TranslationEntitlementAuthority(db, access, configuration,
            NullLogger<TranslationEntitlementAuthority>.Instance);
        var actor = new MessagingActor(useAlias ? "founder-member-alias" : founderOid, participantType);
        var reservation = await authority.TryReserveAsync(new TranslationQuotaReservationRequest(
            actor, new string('a', 64), "fr", "en", "AzureTranslator", 125));
        Assert.True(reservation.Succeeded, reservation.ErrorCode);
        await authority.CompleteAsync(reservation.Reservation!, true, true, null);
        var snapshot = await authority.GetSnapshotAsync(actor);
        Assert.True(snapshot.IsUnlimited);
        Assert.Null(snapshot.RemainingCharacters);
        Assert.Equal("FounderIdentity", snapshot.EntitlementSource);
        Assert.Equal(125, snapshot.ConsumedCharacters);
        Assert.Equal(1, (await db.LegendTranslationUsagePeriods.SingleAsync()).ProviderOperationCount);
        Assert.Empty(await db.LegendTranslationEntitlements.ToListAsync());
    }

    [Fact]
    public async Task OtherAccount_WithTranslationAccess_DoesNotAcquireFounderAllowance()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var actor = new MessagingActor("ordinary-member", MessagingParticipantTypes.Client);
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = actor.UserId, ParticipantType = actor.ParticipantType,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = "founder"
        });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Founder:Oid"] = "776180e2-f234-4f60-a8ae-5aa01d664cef",
            ["LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance"] = "0"
        }).Build();
        var authority = new TranslationEntitlementAuthority(db,
            new ControlledResourceAccessService(db, configuration), configuration,
            NullLogger<TranslationEntitlementAuthority>.Instance);
        var reservation = await authority.TryReserveAsync(new TranslationQuotaReservationRequest(
            actor, new string('b', 64), "en", "fr", "AzureTranslator", 125));
        Assert.False(reservation.Succeeded);
        Assert.Equal("translation_quota_exhausted", reservation.ErrorCode);
        Assert.False((await authority.GetSnapshotAsync(actor)).IsUnlimited);
    }
}
