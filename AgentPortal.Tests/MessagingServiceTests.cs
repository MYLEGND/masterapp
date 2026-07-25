using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.JourneyCircles;
using Domain.Messaging;
using Infrastructure.Moderation;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MessagingServiceTests
{
    [Fact]
    public async Task ParticipantModel_UsesTheFullLogicalIdentityInItsUniqueIndex()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var entityType = db.Model.FindEntityType(typeof(MessageConversationParticipant));
        var index = Assert.Single(entityType!.GetIndexes(), candidate =>
            candidate.IsUnique &&
            candidate.Properties.Select(property => property.Name)
                .SequenceEqual(["ConversationId", "UserId", "ParticipantType"]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void RealtimeGroups_AreQualifiedByParticipantType()
    {
        var agentGroup = MessagingHub.GroupName("shared-user", MessagingParticipantTypes.Agent);
        var clientGroup = MessagingHub.GroupName("shared-user", MessagingParticipantTypes.Client);

        Assert.Equal("messaging:agent:shared-user", agentGroup);
        Assert.Equal("messaging:client:shared-user", clientGroup);
        Assert.NotEqual(agentGroup, clientGroup);
    }

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
    public async Task AgentAndClient_CompleteConversationFlow_TracksBothUnreadStates()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);

        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent,
            client.UserId,
            MessagingParticipantTypes.Client,
            Subject: "Protection review",
            InitialMessageBody: "Your protection review is ready.",
            ClientMessageId: "agent-opens-client-flow"));
        var conversationId = Assert.IsType<MessagingConversationDetail>(opened.Conversation).Id;

        var clientInbox = await service.ListConversationsAsync(client, new MessagingConversationListQuery());
        Assert.Equal(1, Assert.Single(clientInbox.Conversations).UnreadCount);
        Assert.True((await service.MarkConversationReadAsync(new MessagingConversationActionCommand(client, conversationId))).Succeeded);

        var reply = await service.SendMessageAsync(new SendMessagingMessageCommand(
            client,
            conversationId,
            "Thank you. I will review it today.",
            "client-replies-client-flow"));
        Assert.True(reply.Succeeded);

        var agentInbox = await service.ListConversationsAsync(agent, new MessagingConversationListQuery());
        Assert.Equal(1, Assert.Single(agentInbox.Conversations).UnreadCount);
        Assert.True((await service.MarkConversationReadAsync(new MessagingConversationActionCommand(agent, conversationId))).Succeeded);
        Assert.Equal(0, Assert.Single((await service.ListConversationsAsync(agent, new MessagingConversationListQuery())).Conversations).UnreadCount);
    }

    [Fact]
    public async Task AgentAndAgent_CompleteConversationFlow_TracksBothUnreadStates()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.AddRange(
            new AgentProfile { AgentUserId = "agent-one", AgentUpn = "agent.one@example.test", FullName = "Agent One", IsActive = true },
            new AgentProfile { AgentUserId = "agent-two", AgentUpn = "agent.two@example.test", FullName = "Agent Two", IsActive = true });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var firstAgent = new MessagingActor("agent-one", MessagingParticipantTypes.Agent);
        var secondAgent = new MessagingActor("agent-two", MessagingParticipantTypes.Agent);

        Assert.Contains((await service.ListRecipientsAsync(firstAgent)).Recipients,
            recipient => recipient.UserId == secondAgent.UserId && recipient.ParticipantType == MessagingParticipantTypes.Agent);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            firstAgent,
            secondAgent.UserId,
            MessagingParticipantTypes.Agent,
            InitialMessageBody: "Could you review this case?",
            ClientMessageId: "agent-opens-agent-flow"));
        var conversation = Assert.IsType<MessagingConversationDetail>(opened.Conversation);
        Assert.Equal(MessagingConversationTypes.AgentDirect, conversation.ConversationType);
        Assert.Equal(1, Assert.Single((await service.ListConversationsAsync(secondAgent, new MessagingConversationListQuery())).Conversations).UnreadCount);

        Assert.True((await service.MarkConversationReadAsync(new MessagingConversationActionCommand(secondAgent, conversation.Id))).Succeeded);
        Assert.True((await service.SendMessageAsync(new SendMessagingMessageCommand(
            secondAgent,
            conversation.Id,
            "Reviewed. The case is ready for the next step.",
            "agent-replies-agent-flow"))).Succeeded);
        Assert.Equal(1, Assert.Single((await service.ListConversationsAsync(firstAgent, new MessagingConversationListQuery())).Conversations).UnreadCount);
    }

    [Fact]
    public async Task ClientAndAcceptedJourneyCirclePeer_CompleteConversationFlow_TracksBothUnreadStates()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "journey-client-one", FirstName = "Journey", LastName = "One", Email = "one@example.test" };
        var second = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "journey-client-two", FirstName = "Journey", LastName = "Two", Email = "two@example.test" };
        db.ClientProfiles.AddRange(first, second);
        db.JourneyCircleProfiles.AddRange(
            new JourneyCircleProfile { Id = Guid.NewGuid(), ClientProfileId = first.Id, ConsentAffirmedUtc = DateTime.UtcNow, IsOptedIn = true, IsDiscoverable = true, AllowSuggestions = true, AllowConnectionRequests = true, DisplayName = "Journey One", CommunityAccessState = "Active" },
            new JourneyCircleProfile { Id = Guid.NewGuid(), ClientProfileId = second.Id, ConsentAffirmedUtc = DateTime.UtcNow, IsOptedIn = true, IsDiscoverable = true, AllowSuggestions = true, AllowConnectionRequests = true, DisplayName = "Journey Two", CommunityAccessState = "Active" });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var journey = new JourneyCirclesService(db, new CommunityTextModerationService(new ConfigurationBuilder().Build()), NullLogger<JourneyCirclesService>.Instance);
        var firstClient = new MessagingActor(first.ClientUserId, MessagingParticipantTypes.Client);
        var secondClient = new MessagingActor(second.ClientUserId, MessagingParticipantTypes.Client);

        var beforeAcceptance = await service.StartConversationAsync(new StartMessagingConversationCommand(
            firstClient,
            secondClient.UserId,
            MessagingParticipantTypes.Client,
            InitialMessageBody: "This must wait for accepted connection."));
        Assert.False(beforeAcceptance.Succeeded);
        Assert.Equal("MESSAGING_RECIPIENT_FORBIDDEN", beforeAcceptance.ErrorCode);

        Assert.True((await journey.RequestConnectionAsync(first.ClientUserId, second.Id, "Shared goals", "Hello there")).Succeeded);
        var request = Assert.Single(db.JourneyCircleConnections);
        Assert.True((await journey.RespondToConnectionAsync(second.ClientUserId, request.Id, true)).Succeeded);

        Assert.Contains((await service.ListRecipientsAsync(firstClient)).Recipients,
            recipient => recipient.UserId == secondClient.UserId && recipient.RelationshipLabel == "Journey Connection");
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            firstClient,
            secondClient.UserId,
            MessagingParticipantTypes.Client,
            InitialMessageBody: "Glad we connected.",
            ClientMessageId: "client-opens-journey-flow"));
        var conversation = Assert.IsType<MessagingConversationDetail>(opened.Conversation);
        Assert.Equal(MessagingConversationTypes.ClientJourney, conversation.ConversationType);
        Assert.Equal(1, Assert.Single((await service.ListConversationsAsync(secondClient, new MessagingConversationListQuery())).Conversations).UnreadCount);

        Assert.True((await service.MarkConversationReadAsync(new MessagingConversationActionCommand(secondClient, conversation.Id))).Succeeded);
        Assert.True((await service.SendMessageAsync(new SendMessagingMessageCommand(
            secondClient,
            conversation.Id,
            "Glad to connect with you too.",
            "client-replies-journey-flow"))).Succeeded);
        Assert.Equal(1, Assert.Single((await service.ListConversationsAsync(firstClient, new MessagingConversationListQuery())).Conversations).UnreadCount);
    }

    [Fact]
    public async Task JourneyConversationWithDualRoleCounterparty_UsesClientProfileIdentityForBothParticipants()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var first = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "dual-journey-one",
            ExternalIdentityObjectId = "dual-journey-one",
            FirstName = "Journey",
            LastName = "Client One",
            Email = "journey.one@example.test"
        };
        var second = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "dual-journey-two",
            ExternalIdentityObjectId = "dual-journey-two",
            FirstName = "Journey",
            LastName = "Client Two",
            Email = "journey.two@example.test"
        };

        db.ClientProfiles.AddRange(first, second);
        db.AgentProfiles.AddRange(
            new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = first.ClientUserId,
                AgentUpn = first.Email,
                NormalizedEmail = first.Email,
                FullName = "Incorrect Agent One",
                IsActive = true
            },
            new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = second.ClientUserId,
                AgentUpn = second.Email,
                NormalizedEmail = second.Email,
                FullName = "Incorrect Agent Two",
                IsActive = true
            });
        db.JourneyCircleProfiles.AddRange(
            new JourneyCircleProfile
            {
                Id = Guid.NewGuid(),
                ClientProfileId = first.Id,
                ConsentAffirmedUtc = DateTime.UtcNow,
                IsOptedIn = true,
                IsDiscoverable = true,
                AllowSuggestions = true,
                AllowConnectionRequests = true,
                DisplayName = "Journey Client One",
                CommunityAccessState = "Active"
            },
            new JourneyCircleProfile
            {
                Id = Guid.NewGuid(),
                ClientProfileId = second.Id,
                ConsentAffirmedUtc = DateTime.UtcNow,
                IsOptedIn = true,
                IsDiscoverable = true,
                AllowSuggestions = true,
                AllowConnectionRequests = true,
                DisplayName = "Journey Client Two",
                CommunityAccessState = "Active"
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var journey = new JourneyCirclesService(
            db,
            new CommunityTextModerationService(new ConfigurationBuilder().Build()),
            NullLogger<JourneyCirclesService>.Instance);

        Assert.True(
            (await journey.RequestConnectionAsync(
                first.ClientUserId,
                second.Id,
                "Shared goals",
                "Hello there")).Succeeded);

        var request = Assert.Single(db.JourneyCircleConnections);

        Assert.True(
            (await journey.RespondToConnectionAsync(
                second.ClientUserId,
                request.Id,
                true)).Succeeded);

        var firstActor = new MessagingActor(
            first.ClientUserId,
            MessagingParticipantTypes.Client);
        var secondActor = new MessagingActor(
            second.ClientUserId,
            MessagingParticipantTypes.Client);

        var opened = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                firstActor,
                secondActor.UserId,
                MessagingParticipantTypes.Client,
                InitialMessageBody: "Glad we connected.",
                ClientMessageId: "dual-role-journey-open"));

        Assert.True(opened.Succeeded);
        var conversation = Assert.IsType<MessagingConversationDetail>(
            opened.Conversation);

        var firstView = Assert.Single(
            (await service.ListConversationsAsync(
                firstActor,
                new MessagingConversationListQuery())).Conversations);
        var secondView = Assert.Single(
            (await service.ListConversationsAsync(
                secondActor,
                new MessagingConversationListQuery())).Conversations);

        Assert.Equal(MessagingConversationTypes.ClientJourney, conversation.ConversationType);

        Assert.Equal(second.ClientUserId, firstView.Counterparty.UserId);
        Assert.Equal(MessagingParticipantTypes.Client, firstView.Counterparty.ParticipantType);
        Assert.Equal("Journey Client Two", firstView.Counterparty.DisplayName);
        Assert.NotEqual("Incorrect Agent Two", firstView.Counterparty.DisplayName);
        Assert.NotEqual(MessagingParticipantTypes.Client, firstView.Counterparty.DisplayName);

        Assert.Equal(first.ClientUserId, secondView.Counterparty.UserId);
        Assert.Equal(MessagingParticipantTypes.Client, secondView.Counterparty.ParticipantType);
        Assert.Equal("Journey Client One", secondView.Counterparty.DisplayName);
        Assert.NotEqual("Incorrect Agent One", secondView.Counterparty.DisplayName);
        Assert.NotEqual(MessagingParticipantTypes.Client, secondView.Counterparty.DisplayName);
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
        Assert.Equal("ClientAgent|5:Agent7:agent-1|6:Client8:client-1", stored.DirectConversationKey);
        Assert.Equal(2, await db.InternalMessages.CountAsync());
    }

    [Fact]
    public async Task SameCanonicalUserIdWithDifferentParticipantTypes_UsesDistinctParticipantsAndUnreadState()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "agent-1",
            ExternalIdentityObjectId = "agent-1",
            FirstName = "Agent",
            LastName = "Client",
            Email = "agent.client@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-1",
            AgentUpn = "agent.one@mylegnd.com",
            ClientUserId = "agent-1"
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("agent-1", MessagingParticipantTypes.Client);

        var recipients = await service.ListRecipientsAsync(agent);
        Assert.Contains(recipients.Recipients, recipient =>
            recipient.UserId == client.UserId &&
            recipient.ParticipantType == MessagingParticipantTypes.Client);

        var opened = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                client.UserId,
                MessagingParticipantTypes.Client,
                InitialMessageBody: "This is the agent identity.",
                ClientMessageId: "same-user-agent-message"));

        Assert.True(opened.Succeeded);
        var conversation = Assert.IsType<MessagingConversationDetail>(opened.Conversation);
        Assert.Equal("ClientAgent|5:Agent7:agent-1|6:Client7:agent-1", (await db.MessageConversations.SingleAsync()).DirectConversationKey);
        Assert.Equal(2, await db.MessageConversationParticipants
            .Where(participant => participant.ConversationId == conversation.Id)
            .CountAsync());
        Assert.Contains(conversation.Participants, participant =>
            participant.UserId == agent.UserId && participant.ParticipantType == MessagingParticipantTypes.Agent);
        Assert.Contains(conversation.Participants, participant =>
            participant.UserId == client.UserId && participant.ParticipantType == MessagingParticipantTypes.Client);

        var clientInbox = await service.ListConversationsAsync(client, new MessagingConversationListQuery());
        var clientView = Assert.Single(clientInbox.Conversations);
        Assert.Equal(MessagingParticipantTypes.Agent, clientView.Counterparty.ParticipantType);
        Assert.Equal(1, clientView.UnreadCount);

        Assert.True((await service.MarkConversationReadAsync(
            new MessagingConversationActionCommand(client, conversation.Id))).Succeeded);
        Assert.True((await service.SendMessageAsync(new SendMessagingMessageCommand(
            client,
            conversation.Id,
            "This is the client identity.",
            "same-user-client-message"))).Succeeded);

        var agentInbox = await service.ListConversationsAsync(agent, new MessagingConversationListQuery());
        var agentView = Assert.Single(agentInbox.Conversations);
        Assert.Equal(MessagingParticipantTypes.Client, agentView.Counterparty.ParticipantType);
        Assert.Equal(1, agentView.UnreadCount);

        var reused = await service.StartConversationAsync(new StartMessagingConversationCommand(
            client,
            agent.UserId,
            MessagingParticipantTypes.Agent,
            InitialMessageBody: "Reuse the same direct identity pair.",
            ClientMessageId: "same-user-client-reuse"));
        Assert.True(reused.Succeeded);
        Assert.Equal(conversation.Id, reused.Conversation!.Id);

        var crossRoleDuplicate = await service.SendMessageAsync(new SendMessagingMessageCommand(
            client,
            conversation.Id,
            "This must not be mistaken for the agent retry.",
            "same-user-agent-message"));

        Assert.False(crossRoleDuplicate.Succeeded);
        Assert.Equal("MESSAGING_CLIENT_MESSAGE_CONFLICT", crossRoleDuplicate.ErrorCode);
    }

    [Fact]
    public async Task RecipientLookup_MergesAgentClientAndMessagingGrantIntoOneRecipientWithExistingConversation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: true);
        var service = CreateService(db);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);

        var started = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                client,
                "agent-1",
                MessagingParticipantTypes.Agent,
                InitialMessageBody: "Please review my coverage."));
        var recipients = await service.ListRecipientsAsync(client);

        var recipient = Assert.Single(recipients.Recipients);
        Assert.Equal("agent-1", recipient.UserId);
        Assert.Equal("Your Servicing Agent", recipient.RelationshipLabel);
        Assert.Equal(started.Conversation!.Id, recipient.ExistingConversationId);
    }

    [Fact]
    public async Task RecipientLookup_ExcludesOnlyTheExactActorIdentity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "client-1",
            AgentUpn = "client.one@example.test",
            FullName = "Incorrectly Mapped Self",
            IsActive = true
        });
        db.ClientAgentMessagingGrants.Add(new ClientAgentMessagingGrant
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-1",
            AgentUserId = "client-1",
            GrantedByAgentUserId = "agent-1",
            IsActive = true,
            GrantedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var recipients = await service.ListRecipientsAsync(new MessagingActor("client-1", MessagingParticipantTypes.Client));
        var self = await service.GetAuthorizedParticipantAsync(
            new MessagingActor("client-1", MessagingParticipantTypes.Client),
            "client-1",
            MessagingParticipantTypes.Agent);

        Assert.Contains(recipients.Recipients, recipient =>
            recipient.UserId == "client-1" && recipient.ParticipantType == MessagingParticipantTypes.Agent);
        Assert.True(self.Succeeded);
        Assert.Equal(MessagingParticipantTypes.Agent, self.Recipient!.ParticipantType);
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
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "client-2",
            ExternalIdentityObjectId = "client-2",
            FirstName = "Client",
            LastName = "Two",
            Email = "client.two@example.test"
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-2",
            AgentUpn = "agent.two@mylegnd.com",
            ClientUserId = "client-2"
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
        Assert.DoesNotContain(agentRecipients.Recipients, x => x.UserId == "client-2");
    }

    [Fact]
    public async Task AgentRecipientLookup_ReturnsOnlyConvertedClientAndBusinessClientRecords()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        db.ClientProfiles.AddRange(
            new ClientProfile
            {
                ClientUserId = "business-client",
                ExternalIdentityObjectId = "business-client",
                FirstName = "Business",
                LastName = "Client",
                Email = "business@example.test",
                CrmNotes = "{\"recordType\":\"BusinessClient\",\"pipelineStage\":\"BusinessClient\"}"
            },
            new ClientProfile
            {
                ClientUserId = "lead-client",
                ExternalIdentityObjectId = "lead-client",
                FirstName = "Excluded",
                LastName = "Lead",
                Email = "lead@example.test",
                CrmNotes = "{\"recordType\":\"Lead\",\"pipelineStage\":\"NewLead\"}"
            });
        db.AgentClients.AddRange(
            new AgentClient { AgentUserId = "agent-1", AgentUpn = "agent.one@mylegnd.com", ClientUserId = "business-client" },
            new AgentClient { AgentUserId = "agent-1", AgentUpn = "agent.one@mylegnd.com", ClientUserId = "lead-client" });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);

        var recipients = await service.ListRecipientsAsync(agent);
        var excludedStart = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                "lead-client",
                MessagingParticipantTypes.Client,
                InitialMessageBody: "This lead must not be messageable from the client recipient search."));

        Assert.Contains(recipients.Recipients, recipient => recipient.UserId == "client-1");
        Assert.Contains(recipients.Recipients, recipient => recipient.UserId == "business-client");
        Assert.DoesNotContain(recipients.Recipients, recipient => recipient.UserId == "lead-client");
        Assert.False(excludedStart.Succeeded);
        Assert.Equal("MESSAGING_RECIPIENT_FORBIDDEN", excludedStart.ErrorCode);
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
    public async Task LegacyAgentUpnLink_UsesTheSameAuthorizedRecipientScope()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: false, grantClientToAgent: false);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "legacy-agent-key",
            AgentUpn = "agent.one@mylegnd.com",
            ClientUserId = "client-1"
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var agentRecipients = await service.ListRecipientsAsync(new MessagingActor("agent-1", MessagingParticipantTypes.Agent));
        var clientRecipients = await service.ListRecipientsAsync(new MessagingActor("client-1", MessagingParticipantTypes.Client));

        Assert.Contains(agentRecipients.Recipients, x => x.UserId == "client-1");
        Assert.Contains(clientRecipients.Recipients, x => x.UserId == "agent-1");
        Assert.True((await service.StartConversationAsync(new StartMessagingConversationCommand(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent),
            "client-1",
            MessagingParticipantTypes.Client,
            InitialMessageBody: "Authorized through the established client relationship."))).Succeeded);
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
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);
        services.AddSingleton<IWebHostEnvironment>(environment.Object);
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
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);
        var images = new MessagingProfileImageResolver(
            db,
            environment.Object,
            NullLogger<MessagingProfileImageResolver>.Instance);
        return new MessagingService(db, NullLogger<MessagingService>.Instance, moderation, journeys, images);
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
            Email = "client.one@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
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
