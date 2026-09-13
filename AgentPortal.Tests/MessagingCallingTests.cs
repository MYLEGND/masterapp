using Domain.Entities;
using System;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Infrastructure.Data;
using System.Threading.Tasks;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Shared.Calling;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Fact]
    public async Task DirectCalls_RepeatedInviteRechecksRevokedCallerMembership()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var device = Guid.NewGuid(); var id = Guid.NewGuid();
        Assert.True((await service.ExecuteAsync("agent-1", "Agent", new("invite", device, id, conversation.Id), default)).Succeeded);
        var membership = await db.MessageConversationParticipants.SingleAsync(p => p.ConversationId == conversation.Id && p.UserId == "agent-1");
        membership.IsActive = false;
        await db.SaveChangesAsync();
        var result = await service.ExecuteAsync("agent-1", "Agent", new("invite", device, id, conversation.Id), default);
        Assert.False(result.Succeeded);
        Assert.Null(result.Policy);
    }

    [Fact]
    public async Task DirectCalls_SameOwnerProfilesKeepRecipientReceiptSeparateFromCaller()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var client = await db.ClientProfiles.SingleAsync(p => p.ClientUserId == "client-1");
        client.ExternalIdentityObjectId = "agent-1";
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(
            new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var id = Guid.NewGuid();
        var invite = await service.ExecuteAsync("agent-1", "Agent", new("invite", Guid.NewGuid(), id, conversation.Id), default);
        Assert.True(invite.Succeeded, invite.Error);
        Assert.Contains("agent-1", invite.Call!.CalleeUserIds!);
        var rejected = await service.ExecuteAsync("agent-1", "Agent", new("received", Guid.NewGuid(), id), default);
        Assert.False(rejected.Succeeded);
        Assert.Equal("Only the recipient can confirm call delivery.", rejected.Error);
        var received = await service.ExecuteAsync("agent-1", "Client", new("received", Guid.NewGuid(), id), default);
        Assert.True(received.Succeeded, received.Error);
        Assert.NotNull(received.Call!.ReceivedUtc);
        Assert.Null(received.Call.CalleeDeviceId);
        Assert.True((await service.ExecuteAsync("agent-1", "Client", new("accept", Guid.NewGuid(), id), default)).Succeeded);
    }

    [Fact]
    public async Task DirectCalls_ReloadedTimestampsRemainUtcInStatusAndPushSnapshots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(
            new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var caller = Guid.NewGuid();
        var id = Guid.NewGuid();
        var invite = await service.ExecuteAsync("agent-1", "Agent", new("invite", caller, id, conversation.Id), default);
        Assert.True(invite.Succeeded, invite.Error);
        db.ChangeTracker.Clear();
        var persisted = await db.LegendCallSessions.SingleAsync(call => call.Id == id);
        Assert.Equal(DateTimeKind.Unspecified, persisted.ExpiresUtc.Kind);
        var current = await service.ExecuteAsync("agent-1", "Agent", new("get", caller, id), default);
        Assert.True(current.Succeeded, current.Error);
        Assert.Equal(invite.Call!.ExpiresUtc.Ticks, current.Call!.ExpiresUtc.Ticks);
        AssertUtc(current.Call);
        AssertUtc(Infrastructure.Messaging.MessagingService.CallSnapshot(persisted));
        Assert.True((await service.ExecuteAsync("client-1", "Client", new("received", Guid.NewGuid(), id), default)).Succeeded);
        db.ChangeTracker.Clear();
        var received = await service.ExecuteAsync("agent-1", "Agent", new("get", caller, id), default);
        Assert.NotNull(received.Call!.ReceivedUtc);
        AssertUtc(received.Call);

        static void AssertUtc(LegendCallSnapshot snapshot)
        {
            var wire = JsonSerializer.SerializeToElement(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.EndsWith("Z", wire.GetProperty("createdUtc").GetString());
            Assert.EndsWith("Z", wire.GetProperty("expiresUtc").GetString());
            if (snapshot.ReceivedUtc != null)
                Assert.EndsWith("Z", wire.GetProperty("receivedUtc").GetString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectCalls_CancellationCannotBeOvertakenByALateInvite(bool cancelFirst)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var device = Guid.NewGuid(); var id = Guid.NewGuid();
        Task<LegendCallResult> Run(string action) => service.ExecuteAsync("agent-1", "Agent", new(action, device, id, conversation.Id), default);
        if (!cancelFirst) Assert.True((await Run("invite")).Succeeded);
        Assert.True((await Run("cancel")).Succeeded);
        var retry = await Run("invite");
        Assert.True(retry.Succeeded, retry.Error);
        Assert.Equal("ended", retry.Call!.Status);
        Assert.True((await Run("cancel")).Succeeded);
        Assert.Single(await db.LegendCallSessions.ToArrayAsync());
        Assert.Empty((await service.ExecuteAsync("client-1", "Client", new("sync", Guid.NewGuid()), default)).ActiveCalls!);
        Assert.False((await service.ExecuteAsync("client-1", "Client", new("cancel", Guid.NewGuid(), id, conversation.Id), default)).Succeeded);
        Assert.False((await service.ExecuteAsync("agent-1", "Agent", new("cancel", Guid.NewGuid(), id, conversation.Id), default)).Succeeded);
    }

    [Fact]
    public async Task DirectCalls_OnlyRecipientCanConfirmDelivery_WithoutClaimingAnswerOrExtendingLease()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var caller = Guid.NewGuid(); var receiver = Guid.NewGuid(); var id = Guid.NewGuid();
        var invite = await service.ExecuteAsync("agent-1", "Agent", new("invite", caller, id, conversation.Id), default);
        Assert.True(invite.Succeeded);
        Assert.Null(invite.Call!.ReceivedUtc);
        Assert.False((await service.ExecuteAsync("agent-1", "Agent", new("received", caller, id), default)).Succeeded);
        Assert.False((await service.ExecuteAsync("stranger", "Client", new("received", receiver, id), default)).Succeeded);
        var receipt = await service.ExecuteAsync("client-1", "Client", new("received", receiver, id), default);
        Assert.True(receipt.Succeeded, receipt.Error);
        Assert.NotNull(receipt.Call!.ReceivedUtc);
        Assert.Null(receipt.Call.CalleeDeviceId);
        Assert.Equal(invite.Call.ExpiresUtc, receipt.Call.ExpiresUtc);
        var count = await db.LegendCallSignals.CountAsync();
        var repeat = await service.ExecuteAsync("client-1", "Client", new("received", Guid.NewGuid(), id), default);
        Assert.Equal(receipt.Call.ReceivedUtc, repeat.Call!.ReceivedUtc);
        Assert.Equal(count, await db.LegendCallSignals.CountAsync());
        Assert.Contains(await db.LegendCallSignals.ToArrayAsync(), row => row.RecipientGroup == "messaging:agent:agent-1" && row.Payload.Contains("ReceivedUtc"));
        // A different device on the receiving account may still answer exactly once.
        Assert.True((await service.ExecuteAsync("client-1", "Client", new("accept", Guid.NewGuid(), id), default)).Succeeded);
        Assert.False((await service.ExecuteAsync("client-1", "Client", new("accept", receiver, id), default)).Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectCalls_ExpiryDistinguishesUndeliveredFromUnanswered(bool delivered)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var caller = Guid.NewGuid(); var id = Guid.NewGuid();
        await service.ExecuteAsync("agent-1", "Agent", new("invite", caller, id, conversation.Id), default);
        if (delivered) await service.ExecuteAsync("client-1", "Client", new("received", Guid.NewGuid(), id), default);
        var row = await db.LegendCallSessions.SingleAsync();
        row.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        var expired = await service.ExecuteAsync("agent-1", "Agent", new("get", caller, id), default);
        Assert.Equal("missed", expired.Call!.Status);
        Assert.Equal(delivered ? "The call was not answered." : "The recipient could not be reached. Their device did not confirm receiving the call.", expired.Call.FailureMessage);
        Assert.False((await service.ExecuteAsync("client-1", "Client", new("received", Guid.NewGuid(), id), default)).Succeeded);
    }

    [Fact]
    public async Task DirectCalls_BindAnsweringDeviceAndRejectUnauthorizedOrStaleSignals()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", "Agent");
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(agent, "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var caller = Guid.NewGuid(); var callee = Guid.NewGuid(); var id = Guid.NewGuid();
        Task<LegendCallResult> Execute(string user, LegendCallCommand command) => service.ExecuteAsync(user, user == "agent-1" ? "Agent" : "Client", command, default);
        var result = await Execute("agent-1", new("invite", caller, id, conversation.Id, Video: true));
        Assert.True(result.Succeeded, result.Error);
        Assert.All(result.Policy!.StunUrls, url => Assert.StartsWith("stun:", url));
        Assert.True((await Execute("agent-1", new("invite", caller, id, conversation.Id))).Succeeded);
        Assert.Single(await db.LegendCallSessions.ToArrayAsync());
        Assert.False((await Execute("client-1", new("invite", callee, Guid.NewGuid(), conversation.Id))).Succeeded);
        Assert.False((await Execute("stranger", new("get", Guid.NewGuid(), id))).Succeeded);
        Assert.False((await Execute("agent-1", new("signal", caller, id, SignalKind: "offer", SignalData: "sdp", Epoch: 1))).Succeeded);
        Assert.True((await Execute("client-1", new("accept", callee, id))).Succeeded);
        Assert.False((await Execute("client-1", new("accept", Guid.NewGuid(), id))).Succeeded);
        Assert.False((await Execute("agent-1", new("signal", Guid.NewGuid(), id, SignalKind: "offer", SignalData: "sdp", Epoch: 1))).Succeeded);
        Assert.False((await Execute("agent-1", new("signal", caller, id, SignalKind: "offer", SignalData: " ", Epoch: 1))).Succeeded);
        Assert.Equal(0, (await db.LegendCallSessions.SingleAsync()).Epoch);
        Assert.True((await Execute("agent-1", new("signal", caller, id, SignalKind: "offer", SignalData: "sdp", Epoch: 1))).Succeeded);
        Assert.False((await Execute("client-1", new("signal", callee, id, SignalKind: "candidate", SignalData: "candidate", Epoch: 0))).Succeeded);
        Assert.True((await Execute("client-1", new("signal", callee, id, SignalKind: "answer", SignalData: "sdp", Epoch: 1))).Succeeded);
        Assert.True((await Execute("agent-1", new("connected", caller, id))).Succeeded);
        Assert.Equal("active", (await Execute("client-1", new("get", callee, id))).Call!.Status);
        Assert.True((await Execute("client-1", new("end", callee, id))).Succeeded);
        Assert.Empty((await Execute("agent-1", new("sync", caller))).ActiveCalls!);
        var signals = await db.LegendCallSignals.ToArrayAsync();
        Assert.Contains(signals, s => s.RecipientGroup == "messaging:agent:agent-1");
        Assert.Contains(signals, s => s.RecipientGroup == "messaging:client:client-1");
        Assert.All(signals, s => Assert.Equal(TimeSpan.FromSeconds(60), s.ExpiresUtc - s.CreatedUtc));
        Assert.False((await Execute("agent-1", new("signal", caller, id, SignalKind: "offer", SignalData: "sdp", Epoch: 2))).Succeeded);
    }

    [Fact]
    public async Task DirectCalls_ExpireAndReuseTheCanonicalPushRegistration()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var device = Guid.NewGuid(); var id = Guid.NewGuid();
        Assert.True((await service.ExecuteAsync("agent-1", "Agent", new("invite", device, id, conversation.Id), default)).Succeeded);
        var row = await db.LegendCallSessions.SingleAsync(); row.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        Assert.Equal("missed", (await service.ExecuteAsync("agent-1", "Agent", new("get", device, id), default)).Call!.Status);
        var token = new string('a', 64);
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.True((await service.ExecuteAsync("agent-1", "Agent", new("register-voip", device, PushToken: token, PushEnvironment: "sandbox"), default)).Succeeded);
        var registration = Assert.Single(await db.MobilePushDevices.ToArrayAsync());
        Assert.Equal(MobilePushProviders.ApnsVoip, registration.Provider);
        Assert.Equal("agent-1", registration.UserId);
        Assert.True((await service.ExecuteAsync("agent-1", "Agent", new("unregister-voip", device, PushToken: token, PushEnvironment: "sandbox"), default)).Succeeded);
        Assert.False(registration.IsActive);
        Assert.False((await service.ExecuteAsync("agent-1", "Agent", new("register-voip", device, PushToken: "invalid-token", PushEnvironment: "sandbox"), default)).Succeeded);
    }
    [Fact]
    public async Task DirectCalls_StopAuthorizingSignalsWhenMembershipIsRevoked()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var caller = Guid.NewGuid(); var callee = Guid.NewGuid(); var id = Guid.NewGuid();
        Assert.True((await service.ExecuteAsync("agent-1", "Agent", new("invite", caller, id, conversation.Id), default)).Succeeded);
        Assert.True((await service.ExecuteAsync("client-1", "Client", new("accept", callee, id), default)).Succeeded);
        var membership = await db.MessageConversationParticipants.SingleAsync(p => p.ConversationId == conversation.Id && p.UserId == "client-1");
        membership.IsActive = false;
        await db.SaveChangesAsync();
        Assert.False((await service.ExecuteAsync("client-1", "Client", new("heartbeat", callee, id), default)).Succeeded);
        Assert.False((await service.ExecuteAsync("client-1", "Client", new("signal", callee, id, SignalKind: "candidate", SignalData: "private", Epoch: 0), default)).Succeeded);
        Assert.Empty((await service.ExecuteAsync("client-1", "Client", new("sync", callee), default)).ActiveCalls!);
    }

    [Fact]
    public async Task DirectCalls_RelationalConcurrencyPreventsTwoDevicesClaimingOneCall()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var first = new MasterAppDbContext(options);
        await first.Database.EnsureCreatedAsync();
        await SeedAgentAndClientAsync(first, true, false);
        var service = CreateService(first);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var id = Guid.NewGuid();
        Assert.True((await service.ExecuteAsync("agent-1", "Agent", new("invite", Guid.NewGuid(), id, conversation.Id), default)).Succeeded);
        await using var second = new MasterAppDbContext(options);
        // Both requests have observed the same row version before either accepts.
        await first.LegendCallSessions.SingleAsync(c => c.Id == id);
        await second.LegendCallSessions.SingleAsync(c => c.Id == id);
        var winner = Guid.NewGuid();
        Assert.True((await service.ExecuteAsync("client-1", "Client", new("accept", winner, id), default)).Succeeded);
        var lost = await CreateService(second).ExecuteAsync("client-1", "Client", new("accept", Guid.NewGuid(), id), default);
        Assert.False(lost.Succeeded);
        second.ChangeTracker.Clear();
        Assert.Equal(winner, (await second.LegendCallSessions.SingleAsync(c => c.Id == id)).CalleeDeviceId);
        Assert.Single(await second.LegendCallSignals.Where(s => s.RecipientGroup == "messaging:agent:agent-1" && s.Payload.Contains("connecting")).ToArrayAsync());
    }

    [Fact]
    public async Task DirectCalls_ResolveLegacyClientIdsForBothDevicesAndSignalingRoutes()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var profile = await db.ClientProfiles.SingleAsync(p => p.ClientUserId == "client-1");
        profile.ExternalIdentityObjectId = "client-external-oid";
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var id = Guid.NewGuid();
        var invite = await service.ExecuteAsync("agent-1", "Agent", new("invite", Guid.NewGuid(), id, conversation.Id), default);
        Assert.True(invite.Succeeded, invite.Error);
        Assert.Contains("client-external-oid", invite.Call!.CalleeUserIds!);
        Assert.Contains("client-1", invite.Call.CalleeUserIds!);
        Assert.Contains(await db.LegendCallSignals.ToArrayAsync(), s => s.RecipientGroup == "messaging:client:client-external-oid");
        Assert.True((await service.ExecuteAsync("client-external-oid", "Client", new("accept", Guid.NewGuid(), id), default)).Succeeded);
    }

}
