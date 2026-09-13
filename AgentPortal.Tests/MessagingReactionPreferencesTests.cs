using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Fact]
    public async Task ReactionPreferences_PersistAcrossServiceInstancesAndKeepOtherAccountSettings()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var actor = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var other = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var service = CreateService(db);
        Assert.Equal(0, (await service.GetReactionPreferencesAsync(actor))!.PreferredReactionSkinTone);
        Assert.Empty(await db.MobileProfileSettings.ToListAsync());
        Assert.True((await service.SetReactionPreferencesAsync(actor, 4)).Succeeded);
        var settings = Assert.Single(await db.MobileProfileSettings.ToListAsync());
        settings.SendReadReceipts = false;
        settings.Bio = "Existing member profile";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var anotherDevice = CreateService(db);
        Assert.Equal(4, (await anotherDevice.GetReactionPreferencesAsync(actor))!.PreferredReactionSkinTone);
        Assert.Equal(0, (await anotherDevice.GetReactionPreferencesAsync(other))!.PreferredReactionSkinTone);
        Assert.True((await anotherDevice.SetReactionPreferencesAsync(actor, 0)).Succeeded);
        db.ChangeTracker.Clear();
        settings = Assert.Single(await db.MobileProfileSettings.ToListAsync());
        Assert.Equal(0, settings.PreferredReactionSkinTone);
        Assert.False(settings.SendReadReceipts);
        Assert.Equal("Existing member profile", settings.Bio);
        Assert.Empty(await db.MessageReactions.ToListAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public async Task ReactionPreferences_RejectInvalidToneWithoutCreatingSettings(int tone)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var result = await CreateService(db).SetReactionPreferencesAsync(new("client-1", MessagingParticipantTypes.Client), tone);
        Assert.False(result.Succeeded);
        Assert.Equal("MESSAGING_REACTION_PREFERENCE_INVALID", result.ErrorCode);
        Assert.Empty(await db.MobileProfileSettings.ToListAsync());
    }

    [Fact]
    public async Task ReactionPreferences_RejectUnknownActorWithoutReadOrWrite()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var actor = new MessagingActor("unknown-account", MessagingParticipantTypes.Agent);
        Assert.Null(await service.GetReactionPreferencesAsync(actor));
        Assert.False((await service.SetReactionPreferencesAsync(actor, 3)).Succeeded);
        Assert.Empty(await db.MobileProfileSettings.ToListAsync());
    }
}
