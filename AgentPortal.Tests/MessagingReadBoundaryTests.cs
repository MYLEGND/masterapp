using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderedReadBoundary_LeavesLaterArrivalUnreadAndRespectsReceiptPrivacy(bool hideReceipts)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var reader = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            sender, reader.UserId, reader.ParticipantType, InitialMessageBody: "Already rendered"));
        Assert.True(opened.Succeeded);
        var id = opened.Conversation!.Id;
        var first = await db.InternalMessages.SingleAsync(x => x.ConversationId == id);
        first.SentUtc = new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);
        var firstNotification = await db.MobileActivityNotifications.SingleAsync(x => x.SourceMessageId == first.Id);
        firstNotification.OccurredUtc = first.SentUtc;
        await db.SaveChangesAsync();
        if (hideReceipts)
            Assert.True((await service.SetReadReceiptsAsync(reader, id, false, false)).Succeeded);

        var rendered = await service.GetConversationAsync(reader, id);
        var renderedId = Assert.Single(rendered.Conversation!.Messages).Id;
        var later = await service.SendMessageAsync(new SendMessagingMessageCommand(sender, id, "Arrived after rendering"));
        Assert.True(later.Succeeded);
        var second = await db.InternalMessages.SingleAsync(x => x.Id == later.Message!.Id);
        second.SentUtc = first.SentUtc.AddSeconds(1);
        var secondNotification = await db.MobileActivityNotifications.SingleAsync(x => x.SourceMessageId == second.Id);
        secondNotification.OccurredUtc = second.SentUtc;
        await db.SaveChangesAsync();

        var result = await service.MarkConversationReadAsync(new MessagingConversationActionCommand(reader, id, renderedId));
        Assert.True(result.Succeeded);
        var participant = await db.MessageConversationParticipants.SingleAsync(x => x.ConversationId == id && x.UserId == reader.UserId);
        Assert.Equal(first.Id, participant.LastReadMessageId);
        Assert.Equal(first.SentUtc, participant.LastReadUtc);
        Assert.True(firstNotification.IsRead);
        Assert.False(secondNotification.IsRead);
        Assert.Equal(1, Assert.Single((await service.ListConversationsAsync(reader, new MessagingConversationListQuery())).Conversations).UnreadCount);
        Assert.Equal(1, (await db.UserGlobalBadges.SingleAsync(x => x.UserId == reader.UserId && x.ParticipantType == reader.ParticipantType)).UnreadCount);
        var receipts = (await service.GetConversationAsync(sender, id)).Conversation!.ReadReceipts!.Readers;
        if (hideReceipts)
        {
            Assert.DoesNotContain(receipts, x => x.UserId == reader.UserId);
            Assert.Null(participant.SharedReadThroughUtc);
        }
        else
            Assert.Equal(first.SentUtc, Assert.Single(receipts.Where(x => x.UserId == reader.UserId)).ReadThroughUtc);

        // A later visible page can advance; replaying the older page cannot regress it.
        Assert.True((await service.MarkConversationReadAsync(new MessagingConversationActionCommand(reader, id, second.Id))).Succeeded);
        Assert.True((await service.MarkConversationReadAsync(new MessagingConversationActionCommand(reader, id, first.Id))).Succeeded);
        Assert.Equal(second.Id, participant.LastReadMessageId);
        Assert.Equal(second.SentUtc, participant.LastReadUtc);
        Assert.True(secondNotification.IsRead);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleted")]
    [InlineData("foreign")]
    public async Task RenderedReadBoundary_RejectsInvalidTargetWithoutMutatingReadState(string kind)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var reader = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            sender, reader.UserId, reader.ParticipantType, InitialMessageBody: "Visible message"));
        Assert.True(opened.Succeeded);
        var id = opened.Conversation!.Id;
        var target = Guid.NewGuid();
        if (kind == "deleted")
        {
            var message = await db.InternalMessages.SingleAsync(x => x.ConversationId == id);
            message.IsDeleted = true;
            target = message.Id;
        }
        if (kind == "foreign")
        {
            var other = new MessageConversation { Id = Guid.NewGuid(), ConversationType = MessagingConversationTypes.ClientAgent,
                CreatedByUserId = sender.UserId, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
            db.MessageConversations.Add(other);
            db.InternalMessages.Add(new InternalMessage { Id = target, ConversationId = other.Id,
                SenderUserId = sender.UserId, SenderType = sender.ParticipantType, Body = "Foreign conversation", SentUtc = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();
        var participant = await db.MessageConversationParticipants.SingleAsync(x => x.ConversationId == id && x.UserId == reader.UserId);
        var before = (participant.LastReadUtc, participant.LastReadMessageId, participant.SharedReadThroughUtc);
        var result = await service.MarkConversationReadAsync(new MessagingConversationActionCommand(reader, id, target));
        Assert.False(result.Succeeded);
        Assert.Equal("MESSAGING_READ_BOUNDARY_INVALID", result.ErrorCode);
        Assert.Equal(before, (participant.LastReadUtc, participant.LastReadMessageId, participant.SharedReadThroughUtc));
        Assert.False((await db.MobileActivityNotifications.SingleAsync(x => x.ConversationId == id)).IsRead);
    }
}
