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
    [Fact]
    public async Task ConversationDetail_BoundsOpenAndResumeWithoutDiscardingOlderHistory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 95);
        var service = CreateService(db);
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var opened = (await service.GetConversationAsync(sender, id)).Conversation!;
        Assert.Equal(60, opened.Messages.Count);
        Assert.Equal("Message 35", opened.Messages[0].Body);
        Assert.Equal("Message 94", opened.Messages[^1].Body);
        Assert.True(opened.HasOlderMessages);
        var resumed = await service.StartConversationAsync(new StartMessagingConversationCommand(
            sender, "client-1", MessagingParticipantTypes.Client));
        Assert.Equal(id, resumed.Conversation!.Id);
        Assert.Equal(60, resumed.Conversation.Messages.Count);
        var older = (await service.GetConversationPageAsync(sender, id,
            new MessagingConversationMessagePageQuery(opened.Messages[0].SentUtc))).Conversation!;
        Assert.Equal(35, older.Messages.Count);
        Assert.False(older.HasOlderMessages);
        Assert.Equal(95, opened.Messages.Concat(older.Messages).Select(message => message.Id).Distinct().Count());
        var forbidden = await service.GetConversationAsync(new MessagingActor("other-agent", MessagingParticipantTypes.Agent), id);
        Assert.False(forbidden.Succeeded);
    }

    [Fact]
    public async Task ConversationPage_DoesNotTranslateLookaheadAndStillReflectsNewMessages()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = await SeedProjectionHistoryAsync(db, 4);
        var profile = await db.ClientProfiles.SingleAsync(row => row.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1", ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = profile.Id, ParticipantType = MessagingParticipantTypes.Client,
            PreferredCommunicationLanguage = "ht"
        });
        await db.SaveChangesAsync();
        var translator = new DeferredTranslationProbe();
        var service = CreateService(db, translator);
        var recipient = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var page = (await service.GetConversationPageAsync(recipient, id,
            new MessagingConversationMessagePageQuery(Take: 2))).Conversation!;
        Assert.Equal(2, page.Messages.Count);
        Assert.True(page.HasOlderMessages);
        Assert.Equal(2, translator.Calls);
        Assert.Equal(2, await db.MessageTranslations.CountAsync());
        var sent = await service.SendMessageAsync(new SendMessagingMessageCommand(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent), id, "Newest message"));
        Assert.True(sent.Succeeded);
        var refreshed = (await service.GetConversationPageAsync(recipient, id,
            new MessagingConversationMessagePageQuery(Take: 2))).Conversation!;
        Assert.Contains(refreshed.Messages, message => message.Id == sent.Message!.Id);
        Assert.Equal(3, translator.Calls);
    }

    private static async Task<Guid> SeedProjectionHistoryAsync(Infrastructure.Data.MasterAppDbContext db, int count)
    {
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var started = await CreateService(db).StartConversationAsync(new StartMessagingConversationCommand(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent), "client-1", MessagingParticipantTypes.Client));
        var id = started.Conversation!.Id;
        var start = DateTime.UtcNow.AddDays(-1);
        db.InternalMessages.AddRange(Enumerable.Range(0, count).Select(index => new InternalMessage
        {
            Id = Guid.NewGuid(), ConversationId = id,
            SenderUserId = "agent-1", SenderType = MessagingParticipantTypes.Agent,
            Body = "Message " + index, OriginalLanguage = "en", SenderPreferredLanguage = "en",
            SentUtc = start.AddSeconds(index)
        }));
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Direct_chat_title_uses_the_counterpartys_current_profile_for_both_roles()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var started = await service.StartConversationAsync(new StartMessagingConversationCommand(agent, client.UserId, client.ParticipantType));
        var id = started.Conversation!.Id;
        Assert.True((await service.SendMessageAsync(new SendMessagingMessageCommand(agent, id, "Hello."))).Succeeded);
        var row = await db.MessageConversations.SingleAsync(c => c.Id == id);
        row.Subject = "Conversation";
        await db.SaveChangesAsync();
        Assert.Equal("Client One", (await service.GetConversationAsync(agent, id)).Conversation!.DisplayTitle);
        Assert.Equal("Agent One", (await service.GetConversationAsync(client, id)).Conversation!.DisplayTitle);
        Assert.Equal("Client One", (await service.ListConversationsAsync(agent, new MessagingConversationListQuery())).Conversations.Single().DisplayTitle);
        row.ConversationType = MessagingConversationTypes.Group;
        row.Subject = "Team discussion";
        await db.SaveChangesAsync();
        Assert.Equal("Team discussion", (await service.GetConversationAsync(agent, id)).Conversation!.DisplayTitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationProjection_ResolvesRepeatedQuotedOriginalOnlyOncePerPage(bool providerFails)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var profile = await db.ClientProfiles.SingleAsync(row => row.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1", ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = profile.Id, ParticipantType = MessagingParticipantTypes.Client,
            PreferredCommunicationLanguage = "ht"
        });
        await db.SaveChangesAsync();
        var translator = new DeferredTranslationProbe { Fail = providerFails };
        var service = CreateService(db, translator);
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var recipient = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var started = await service.StartConversationAsync(new StartMessagingConversationCommand(sender, recipient.UserId, recipient.ParticipantType));
        var conversationId = started.Conversation!.Id;
        var original = await service.SendMessageAsync(new SendMessagingMessageCommand(sender, conversationId, "Hello."));
        for (var i = 0; i < 5; i++)
            Assert.True((await service.SendMessageAsync(new SendMessagingMessageCommand(recipient,
                conversationId, "Reply " + i, ReplyToMessageId: original.Message!.Id))).Succeeded);
        Assert.Equal(0, translator.Calls);
        var page = await service.GetConversationPageAsync(recipient, conversationId, new MessagingConversationMessagePageQuery());
        Assert.Equal(1, translator.Calls);
        if (providerFails)
        {
            Assert.False(page.Succeeded);
            Assert.Equal(MessagingTranslationPresentation.UnavailableCode, page.ErrorCode);
            Assert.Null(page.Conversation);
            Assert.Empty(await db.MessageTranslations.ToListAsync());
            Assert.Equal("Hello.", (await db.InternalMessages.SingleAsync(row => row.Id == original.Message!.Id)).Body);
            var participant = await db.MessageConversationParticipants.SingleAsync(row =>
                row.ConversationId == conversationId && row.UserId == recipient.UserId);
            Assert.Null(participant.LastReadMessageId);

            // Repeated quotations share one attempt even on a failed page. Neither
            // failure is cached or advances the reader past the withheld original.
            var failedRetry = await service.GetConversationPageAsync(recipient, conversationId, new MessagingConversationMessagePageQuery());
            Assert.False(failedRetry.Succeeded);
            Assert.Equal(MessagingTranslationPresentation.UnavailableCode, failedRetry.ErrorCode);
            Assert.Null(failedRetry.Conversation);
            Assert.Equal(2, translator.Calls);
            Assert.Empty(await db.MessageTranslations.ToListAsync());
            translator.Fail = false;
            page = await service.GetConversationPageAsync(recipient, conversationId, new MessagingConversationMessagePageQuery());
            Assert.Equal(3, translator.Calls);
            Assert.Null(participant.LastReadMessageId);
        }
        Assert.True(page.Succeeded);
        Assert.Equal(6, page.Conversation!.Messages.Count);
        Assert.Equal(6, await db.InternalMessages.CountAsync());
        var quotes = page.Conversation.Messages.Where(message => message.Reply is not null).ToList();
        Assert.Equal(5, quotes.Count);
        Assert.All(quotes, message => Assert.Equal("Bonjou.", message.Reply!.Body));
        var presentedOriginal = Assert.Single(page.Conversation.Messages.Where(message => message.Id == original.Message!.Id));
        Assert.Equal("Bonjou.", presentedOriginal.Body);
        Assert.Equal("Hello.", presentedOriginal.OriginalBody);
        var cache = Assert.Single(await db.MessageTranslations.ToListAsync());
        Assert.Equal(original.Message!.Id, cache.InternalMessageId);
        Assert.Equal("Hello.", (await db.InternalMessages.SingleAsync(row => row.Id == original.Message.Id)).Body);
        var cachedPage = await service.GetConversationPageAsync(recipient, conversationId, new MessagingConversationMessagePageQuery());
        Assert.True(cachedPage.Succeeded);
        Assert.Equal(page.Conversation.Messages.Select(message => message.Id), cachedPage.Conversation!.Messages.Select(message => message.Id));
        Assert.Equal(providerFails ? 3 : 1, translator.Calls);
    }
}
