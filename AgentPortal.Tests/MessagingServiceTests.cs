using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.JourneyCircles;
using Domain.Messaging;
using Infrastructure.Moderation;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MessagingServiceTests
{
    [Fact]
    public async Task AgentClientRelationship_AllowsMessaging_AndTracksUnreadAndReadState()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);

        var created = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                "client-1",
                MessagingParticipantTypes.Client,
                Subject: "Policy review",
                InitialMessageBody: "Your policy review is ready.",
                ClientMessageId: "first-agent-message"));

        Assert.True(created.Succeeded);
        var conversation = Assert.IsType<MessagingConversationDetail>(created.Conversation);
        Assert.Equal(MessagingConversationTypes.ClientAgent, conversation.ConversationType);
        Assert.Single(conversation.Messages);

        var clientList = await service.ListConversationsAsync(client, new MessagingConversationListQuery());
        Assert.True(clientList.Succeeded);
        var listedConversation = Assert.Single(clientList.Conversations);
        Assert.Equal(1, listedConversation.UnreadCount);
        Assert.Equal("Agent One", listedConversation.Counterparty.DisplayName);

        var read = await service.MarkConversationReadAsync(
            new MessagingConversationActionCommand(client, conversation.Id));

        Assert.True(read.Succeeded);
        var afterRead = await service.ListConversationsAsync(client, new MessagingConversationListQuery());
        Assert.Equal(0, Assert.Single(afterRead.Conversations).UnreadCount);
        Assert.Equal(3, await db.MessagingAuditEntries.CountAsync());
    }

    [Fact]
    public async Task ActiveMessagingGrant_AllowsMessaging_WhenAgentClientRelationshipDoesNotExist()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: false, grantClientToAgent: true);
        var service = CreateService(db);

        var result = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                new MessagingActor("client-1", MessagingParticipantTypes.Client),
                "agent-1",
                MessagingParticipantTypes.Agent,
                InitialMessageBody: "Can we review my coverage?"));

        Assert.True(result.Succeeded);
        Assert.Equal(MessagingConversationTypes.ClientAgent, result.Conversation!.ConversationType);
    }

    [Fact]
    public async Task RepeatedDirectConversationStarts_ReuseTheSameConversationKey()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);

        var first = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                "client-1",
                MessagingParticipantTypes.Client,
                InitialMessageBody: "First message.",
                ClientMessageId: "first-direct-start"));
        var repeated = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                "client-1",
                MessagingParticipantTypes.Client,
                InitialMessageBody: "Second message.",
                ClientMessageId: "repeated-direct-start"));

        Assert.True(first.Succeeded);
        Assert.True(repeated.Succeeded);
        Assert.Equal(first.Conversation!.Id, repeated.Conversation!.Id);
        var stored = Assert.Single(await db.MessageConversations.ToListAsync());
        Assert.Equal("ClientAgent|agent-1|client-1", stored.DirectConversationKey);
        Assert.Equal(2, await db.InternalMessages.CountAsync());
    }

    [Fact]
    public async Task NoAgentClientRelationshipOrGrant_DeniesClientMessaging()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: false, grantClientToAgent: false);
        var service = CreateService(db);

        var result = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                new MessagingActor("client-1", MessagingParticipantTypes.Client),
                "agent-1",
                MessagingParticipantTypes.Agent,
                InitialMessageBody: "Can we review my coverage?"));

        Assert.False(result.Succeeded);
        Assert.Equal("MESSAGING_RECIPIENT_FORBIDDEN", result.ErrorCode);
        Assert.Empty(await db.MessageConversations.ToListAsync());
    }

    [Fact]
    public async Task RecipientLookup_ReturnsOnlyServerAuthorizedRecipients()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-2",
            AgentUpn = "agent.two@mylegnd.com",
            FullName = "Agent Two",
            IsActive = true
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var clientRecipients = await service.ListRecipientsAsync(
            new MessagingActor("client-1", MessagingParticipantTypes.Client));
        var agentRecipients = await service.ListRecipientsAsync(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent));

        var clientRecipient = Assert.Single(clientRecipients.Recipients);
        Assert.Equal("agent-1", clientRecipient.UserId);
        Assert.Contains(agentRecipients.Recipients, x => x.UserId == "client-1");
        Assert.Contains(agentRecipients.Recipients, x => x.UserId == "agent-2");
    }

    [Fact]
    public async Task RecipientSearch_NormalizesWordsAndReturnsOnlyAuthorizedParticipant()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-2",
            AgentUpn = "other.agent@mylegnd.com",
            FullName = "Other Agent",
            IsActive = true
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);

        var search = await service.ListRecipientsAsync(client, "One, Agent");
        var participant = await service.GetAuthorizedParticipantAsync(
            client,
            "agent-1",
            MessagingParticipantTypes.Agent);
        var unauthorized = await service.GetAuthorizedParticipantAsync(
            client,
            "agent-2",
            MessagingParticipantTypes.Agent);

        var recipient = Assert.Single(search.Recipients);
        Assert.Equal("agent-1", recipient.UserId);
        Assert.True(participant.Succeeded);
        Assert.Equal("Agent One", participant.Recipient!.DisplayName);
        Assert.False(unauthorized.Succeeded);
        Assert.Equal("MESSAGING_RECIPIENT_NOT_FOUND", unauthorized.ErrorCode);
    }

    [Fact]
    public async Task AssistantActor_CannotAccessMessaging()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "assistant-1",
            AgentUpn = "assistant@example.test",
            FullName = "Restricted Assistant",
            IsActive = true
        });
        db.AgentAssistants.Add(new AgentAssistant
        {
            ParentAgentUserId = "agent-1",
            AssistantUserId = "assistant-1",
            FirstName = "Restricted",
            LastName = "Assistant",
            Email = "assistant@example.test",
            IsActive = true
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                new MessagingActor("assistant-1", MessagingParticipantTypes.Agent),
                "client-1",
                MessagingParticipantTypes.Client,
                InitialMessageBody: "This must not send."));

        Assert.False(result.Succeeded);
        Assert.Equal("MESSAGING_ACTOR_INVALID", result.ErrorCode);
    }

    [Fact]
    public async Task AttachmentWorkflow_BlocksDownloadsUntilTrustedScannerMarksClean()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var started = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                "client-1",
                MessagingParticipantTypes.Client,
                InitialMessageBody: "Please review the attachment."));
        var message = Assert.Single(started.Conversation!.Messages);
        var attachmentId = Guid.NewGuid();

        var attachment = await service.AddPendingAttachmentAsync(
            new AddPendingMessagingAttachmentCommand(
                agent,
                message.Id,
                attachmentId,
                "policy.pdf",
                "policy-123.pdf",
                "application/pdf",
                1024,
                "messaging/policy-123.pdf"));

        Assert.True(attachment.Succeeded);
        Assert.Equal(MessagingAttachmentScanStatuses.Pending, attachment.Attachment!.ScanStatus);

        var pendingDownload = await service.GetAttachmentForDownloadAsync(
            new MessagingAttachmentDownloadCommand(
                new MessagingActor("client-1", MessagingParticipantTypes.Client),
                attachmentId));
        Assert.False(pendingDownload.Succeeded);
        Assert.Equal("MESSAGING_ATTACHMENT_NOT_READY", pendingDownload.ErrorCode);

        var directClean = await service.UpdateAttachmentScanStatusAsync(
            new UpdateMessagingAttachmentScanStatusCommand("malware-provider", attachmentId, MessagingAttachmentScanStatuses.Clean));
        Assert.False(directClean.Succeeded);
        Assert.Equal("MESSAGING_ATTACHMENT_SCAN_TRANSITION_INVALID", directClean.ErrorCode);

        Assert.True((await service.UpdateAttachmentScanStatusAsync(
            new UpdateMessagingAttachmentScanStatusCommand("malware-provider", attachmentId, MessagingAttachmentScanStatuses.Scanning))).Succeeded);
        Assert.True((await service.UpdateAttachmentScanStatusAsync(
            new UpdateMessagingAttachmentScanStatusCommand("malware-provider", attachmentId, MessagingAttachmentScanStatuses.Clean))).Succeeded);

        var cleanDownload = await service.GetAttachmentForDownloadAsync(
            new MessagingAttachmentDownloadCommand(
                new MessagingActor("client-1", MessagingParticipantTypes.Client),
                attachmentId));
        Assert.True(cleanDownload.Succeeded);
        Assert.Equal("messaging/policy-123.pdf", cleanDownload.Attachment!.StoragePath);
    }

    [Fact]
    public void AddMasterAppMessaging_RegistersTheSingleMessagingService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<Infrastructure.Data.MasterAppDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddMasterAppMessaging(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsAssignableFrom<IMessagingService>(scope.ServiceProvider.GetRequiredService<IMessagingService>());
        Assert.Single(services.Where(descriptor => descriptor.ServiceType == typeof(IMessagingService)));
    }

    private static MessagingService CreateService(Infrastructure.Data.MasterAppDbContext db)
    {
        var moderation = new CommunityTextModerationService(new ConfigurationBuilder().Build());
        var journeys = new JourneyCirclesService(db, moderation, NullLogger<JourneyCirclesService>.Instance);
        return new MessagingService(db, NullLogger<MessagingService>.Instance, moderation, journeys);
    }

    private static async Task SeedAgentAndClientAsync(
        Infrastructure.Data.MasterAppDbContext db,
        bool linkClientToAgent,
        bool grantClientToAgent)
    {
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-1",
            AgentUpn = "agent.one@mylegnd.com",
            FullName = "Agent One",
            IsActive = true
        });
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "client-1",
            ExternalIdentityObjectId = "client-1",
            FirstName = "Client",
            LastName = "One",
            Email = "client.one@example.test"
        });
        if (linkClientToAgent)
        {
            db.AgentClients.Add(new AgentClient
            {
                AgentUserId = "agent-1",
                AgentUpn = "agent.one@mylegnd.com",
                ClientUserId = "client-1"
            });
        }

        if (grantClientToAgent)
        {
            db.ClientAgentMessagingGrants.Add(new ClientAgentMessagingGrant
            {
                Id = Guid.NewGuid(),
                ClientUserId = "client-1",
                AgentUserId = "agent-1",
                GrantedByAgentUserId = "agent-1",
                IsActive = true,
                GrantedUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }
}
