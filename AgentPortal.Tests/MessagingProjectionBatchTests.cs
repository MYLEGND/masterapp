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
        Assert.True(page.Succeeded);
        Assert.Equal(6, page.Conversation!.Messages.Count);
        Assert.Equal(1, translator.Calls);
        Assert.All(page.Conversation.Messages.Where(message => message.Reply is not null),
            message => Assert.Equal(providerFails ? "Hello." : "Bonjou.", message.Reply!.Body));
        await service.GetConversationPageAsync(recipient, conversationId, new MessagingConversationMessagePageQuery());
        Assert.Equal(providerFails ? 2 : 1, translator.Calls);
    }
}
