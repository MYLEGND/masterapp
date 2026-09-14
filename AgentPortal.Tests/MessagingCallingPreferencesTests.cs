using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Shared.Calling;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Fact]
    public async Task DirectCalls_PreferencesFollowTypedProfileAcrossDevicesAndIdentityAliases()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var client = await db.ClientProfiles.SingleAsync(p => p.ClientUserId == "client-1");
        client.ExternalIdentityObjectId = "agent-1";
        db.MobileProfileSettings.Add(new() { ProfileId = client.Id, ParticipantType = "Client", PreferredCommunicationLanguage = "ht", Bio = "Keep my profile" });
        await db.SaveChangesAsync();
        var firstDevice = CreateService(db);
        var saved = await firstDevice.ExecuteAsync("client-1", "Client", new("preferences", Guid.NewGuid(), Preferences: new("soft", "profile")), default);
        Assert.True(saved.Succeeded, saved.Error);
        Assert.Contains(saved.Ringtones!, option => option.Id == "soft" && option.Resource == "legend_ringback");
        Assert.Contains(saved.Wallpapers!, option => option.Id == "profile");
        db.ChangeTracker.Clear();
        var anotherDevice = CreateService(db);
        var restored = await anotherDevice.ExecuteAsync("agent-1", "Client", new("preferences", Guid.NewGuid()), default);
        Assert.True(restored.Succeeded, restored.Error);
        Assert.Equal(new LegendCallPreferences("soft", "profile"), restored.Preferences);
        var otherRole = await anotherDevice.ExecuteAsync("agent-1", "Agent", new("preferences", Guid.NewGuid()), default);
        Assert.Equal(new LegendCallPreferences(), otherRole.Preferences);
        var row = await db.MobileProfileSettings.SingleAsync();
        Assert.Equal("ht", row.PreferredCommunicationLanguage);
        Assert.Equal("Keep my profile", row.Bio);
        Assert.Equal(client.Id, row.ProfileId);
    }

    [Theory]
    [InlineData("https://attacker.invalid/tone.wav", "profile")]
    [InlineData("signature", "https://attacker.invalid/photo.png")]
    public async Task DirectCalls_InvalidPreferencesNeverPersist(string ringtone, string wallpaper)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var result = await CreateService(db).ExecuteAsync("agent-1", "Agent", new("preferences", Guid.NewGuid(), Preferences: new(ringtone, wallpaper)), default);
        Assert.False(result.Succeeded);
        Assert.Empty(await db.MobileProfileSettings.ToArrayAsync());
    }

    [Fact]
    public async Task DirectCalls_PreferencesDenyUnknownAccountAndMissingDevice()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        Assert.False((await service.ExecuteAsync("missing", "Client", new("preferences", Guid.NewGuid(), Preferences: new("soft", "profile")), default)).Succeeded);
        Assert.False((await service.ExecuteAsync("agent-1", "Agent", new("preferences", Guid.Empty, Preferences: new("soft", "profile")), default)).Succeeded);
        Assert.Empty(await db.MobileProfileSettings.ToArrayAsync());
    }

    [Fact]
    public async Task DirectCalls_SnapshotUsesEachPeersWallpaperAndRecipientsRingtone()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        Assert.True((await service.ExecuteAsync("agent-1", "Agent", new("preferences", Guid.NewGuid(), Preferences: new("signature", "profile")), default)).Succeeded);
        Assert.True((await service.ExecuteAsync("client-1", "Client", new("preferences", Guid.NewGuid(), Preferences: new("soft", "legend")), default)).Succeeded);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var caller = Guid.NewGuid(); var id = Guid.NewGuid();
        var invite = await service.ExecuteAsync("agent-1", "Agent", new("invite", caller, id, conversation.Id), default);
        Assert.True(invite.Succeeded, invite.Error);
        Assert.Equal("profile", invite.Call!.CallerWallpaperMode);
        Assert.Equal("legend", invite.Call.CalleeWallpaperMode);
        Assert.Equal("legend_ringback", invite.Call.IncomingRingtoneResource);
        Assert.Null(invite.Call.ReceivedUtc);
        var recipient = await service.ExecuteAsync("client-1", "Client", new("get", Guid.NewGuid(), id), default);
        Assert.Equal(invite.Call.CallerWallpaperMode, recipient.Call!.CallerWallpaperMode);
        Assert.Equal(invite.Call.CalleeWallpaperMode, recipient.Call.CalleeWallpaperMode);
        Assert.Equal(invite.Call.IncomingRingtoneResource, recipient.Call.IncomingRingtoneResource);
    }
}
