using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Billing;
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
    public async Task ConversationInboxControls_AreScopedToTheActorAndRestoreOnANewMessage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);

        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent,
            client.UserId,
            client.ParticipantType,
            InitialMessageBody: "Your review is ready."));
        var conversationId = Assert.IsType<MessagingConversationDetail>(opened.Conversation).Id;

        Assert.True((await service.SetConversationPinnedAsync(
            new SetMessagingConversationPinnedCommand(client, conversationId, true))).Succeeded);
        Assert.True(Assert.Single((await service.ListConversationsAsync(
            client,
            new MessagingConversationListQuery())).Conversations).IsPinned);
        Assert.False(Assert.Single((await service.ListConversationsAsync(
            agent,
            new MessagingConversationListQuery())).Conversations).IsPinned);

        Assert.True((await service.RemoveConversationForActorAsync(
            new RemoveMessagingConversationCommand(client, conversationId))).Succeeded);
        Assert.Empty((await service.ListConversationsAsync(client, new MessagingConversationListQuery())).Conversations);
        Assert.Single((await service.ListConversationsAsync(agent, new MessagingConversationListQuery())).Conversations);

        Assert.True((await service.SendMessageAsync(new SendMessagingMessageCommand(
            agent,
            conversationId,
            "A new detail is available."))).Succeeded);

        var restored = Assert.Single((await service.ListConversationsAsync(
            client,
            new MessagingConversationListQuery())).Conversations);
        Assert.False(restored.IsPinned);
        Assert.Null((await db.MessageConversationParticipants.SingleAsync(participant =>
            participant.ConversationId == conversationId &&
            participant.UserId == client.UserId &&
            participant.ParticipantType == client.ParticipantType)).HiddenUtc);
    }

    [Fact]
    public async Task ConversationPins_EnforceTheSixConversationLimit()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent,
            "client-1",
            MessagingParticipantTypes.Client,
            InitialMessageBody: "Pinned limit test."));
        var conversationId = Assert.IsType<MessagingConversationDetail>(opened.Conversation).Id;

        db.MessageConversationParticipants.AddRange(Enumerable.Range(0, 6).Select(index =>
            new MessageConversationParticipant
            {
                Id = Guid.NewGuid(),
                ConversationId = Guid.NewGuid(),
                UserId = agent.UserId,
                ParticipantType = agent.ParticipantType,
                IsActive = true,
                JoinedUtc = DateTime.UtcNow,
                PinnedUtc = DateTime.UtcNow.AddMinutes(-index)
            }));
        await db.SaveChangesAsync();

        var result = await service.SetConversationPinnedAsync(
            new SetMessagingConversationPinnedCommand(agent, conversationId, true));

        Assert.False(result.Succeeded);
        Assert.Equal("MESSAGING_PIN_LIMIT_REACHED", result.ErrorCode);
    }

    [Fact]
    public async Task MessageUnsend_IsRestrictedToTheAuthorAndRedactsTheMessageBody()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent,
            client.UserId,
            client.ParticipantType,
            InitialMessageBody: "Private message."));
        var conversation = Assert.IsType<MessagingConversationDetail>(opened.Conversation);
        var messageId = Assert.Single(conversation.Messages).Id;

        var denied = await service.DeleteMessageAsync(
            new DeleteMessagingMessageCommand(client, conversation.Id, messageId));
        Assert.False(denied.Succeeded);
        Assert.Equal("MESSAGING_MESSAGE_FORBIDDEN", denied.ErrorCode);

        Assert.True((await service.DeleteMessageAsync(
            new DeleteMessagingMessageCommand(agent, conversation.Id, messageId))).Succeeded);
        var refreshed = Assert.IsType<MessagingConversationDetail>(
            (await service.GetConversationAsync(client, conversation.Id)).Conversation);
        var message = Assert.Single(refreshed.Messages);
        Assert.True(message.IsDeleted);
        Assert.Equal("Message unsent", message.Body);
    }

    [Fact]
    public async Task DirectConversationCallOptions_UseOnlyTheAuthorizedCounterparty()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var agentProfile = await db.AgentProfiles.SingleAsync();
        agentProfile.Phone = "+1 (480) 555-0100";
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent,
            client.UserId,
            client.ParticipantType,
            InitialMessageBody: "Call options test."));
        var conversationId = Assert.IsType<MessagingConversationDetail>(opened.Conversation).Id;

        var options = await service.GetConversationCallOptionsAsync(client, conversationId);

        Assert.True(options.Succeeded);
        Assert.Equal("Agent One", options.Options!.DisplayName);
        Assert.Equal("+14805550100", options.Options.PhoneNumber);
        Assert.Equal("agent.one@mylegnd.com", options.Options.FaceTimeAddress);
    }

    [Fact]
    public async Task GroupConversation_UsesAuthorizedRecipientsAndOnlyOwnerCanAddMembers()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        db.ClientProfiles.AddRange(
            new ClientProfile
            {
                ClientUserId = "client-2",
                ExternalIdentityObjectId = "client-2",
                FirstName = "Client",
                LastName = "Two",
                Email = "client.two@example.test"
            },
            new ClientProfile
            {
                ClientUserId = "client-3",
                ExternalIdentityObjectId = "client-3",
                FirstName = "Client",
                LastName = "Three",
                Email = "client.three@example.test"
            });
        db.AgentClients.AddRange(
            new AgentClient { AgentUserId = "agent-1", AgentUpn = "agent.one@mylegnd.com", ClientUserId = "client-2" },
            new AgentClient { AgentUserId = "agent-1", AgentUpn = "agent.one@mylegnd.com", ClientUserId = "client-3" });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var owner = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var created = await service.CreateGroupAsync(new CreateMessagingGroupCommand(
            owner,
            [
                new MessagingParticipantReference("client-1", MessagingParticipantTypes.Client),
                new MessagingParticipantReference("client-2", MessagingParticipantTypes.Client)
            ],
            "Protection review team",
            "Welcome to the review.",
            GroupImage: new MessagingGroupImage([1, 2, 3], "image/png")));

        var conversation = Assert.IsType<MessagingConversationDetail>(created.Conversation);
        Assert.True(created.Succeeded);
        Assert.Equal(MessagingConversationTypes.Group, conversation.ConversationType);
        Assert.True(conversation.CanManageMembers);
        Assert.Equal(3, conversation.Participants.Count);
        Assert.Equal("image/png", conversation.GroupImage?.ContentType);

        var updateGroup = await service.UpdateGroupProfileAsync(
            new UpdateMessagingGroupProfileCommand(
                owner,
                conversation.Id,
                "Protection review leaders",
                new MessagingGroupImage([4, 5, 6], "image/jpeg")));
        Assert.True(updateGroup.Succeeded);
        var updatedGroup = Assert.IsType<MessagingConversationDetail>(
            (await service.GetConversationAsync(owner, conversation.Id)).Conversation);
        Assert.Equal("Protection review leaders", updatedGroup.Subject);
        Assert.Equal("image/jpeg", updatedGroup.GroupImage?.ContentType);

        var addMember = await service.AddGroupParticipantAsync(
            new AddMessagingGroupParticipantCommand(
                owner,
                conversation.Id,
                "client-3",
                MessagingParticipantTypes.Client));
        Assert.True(addMember.Succeeded);
        Assert.Equal(4, Assert.IsType<MessagingConversationDetail>(
            (await service.GetConversationAsync(owner, conversation.Id)).Conversation).Participants.Count);

        var nonOwner = await service.AddGroupParticipantAsync(
            new AddMessagingGroupParticipantCommand(
                new MessagingActor("client-1", MessagingParticipantTypes.Client),
                conversation.Id,
                "client-3",
                MessagingParticipantTypes.Client));
        Assert.False(nonOwner.Succeeded);
        Assert.Equal("MESSAGING_GROUP_OWNER_REQUIRED", nonOwner.ErrorCode);

        var nonOwnerUpdate = await service.UpdateGroupProfileAsync(
            new UpdateMessagingGroupProfileCommand(
                new MessagingActor("client-1", MessagingParticipantTypes.Client),
                conversation.Id,
                "Not allowed",
                null));
        Assert.False(nonOwnerUpdate.Succeeded);
        Assert.Equal("MESSAGING_GROUP_OWNER_REQUIRED", nonOwnerUpdate.ErrorCode);
    }

    [Fact]
    public async Task VerificationRequest_UsesOnePrivateFounderReviewQueueAndResolvesTheProfile()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: false, grantClientToAgent: false);
        db.AgentProfiles.AddRange(
            new AgentProfile
            {
                AgentUserId = "zac-founder-oid",
                AgentUpn = LegendVerifiedIdentity.FounderEmail,
                NormalizedEmail = LegendVerifiedIdentity.FounderEmail,
                FullName = "Zac Owen",
                IsActive = true
            },
            new AgentProfile
            {
                AgentUserId = "legend-oid",
                AgentUpn = LegendVerifiedIdentity.LegendEmail,
                NormalizedEmail = LegendVerifiedIdentity.LegendEmail,
                FullName = "Legend™",
                IsActive = true
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var requester = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartVerificationRequestAsync(requester);
        var request = Assert.IsType<MessagingVerificationReview>(opened.Request);

        Assert.True(opened.Succeeded);
        Assert.Equal(VerificationReviewStatuses.Pending, request.Status);

        var persisted = await db.MessageConversations.SingleAsync();
        Assert.Equal(MessagingConversationTypes.Group, persisted.ConversationType);
        Assert.Equal(MessagingConversationPurposes.VerificationReview, persisted.Purpose);
        Assert.Equal("zac-founder-oid", persisted.OwnerUserId);
        Assert.Equal(MessagingParticipantTypes.Agent, persisted.OwnerParticipantType);
        Assert.Equal(2, await db.MessageConversationParticipants.CountAsync());
        Assert.Equal(1, await db.VerificationReviewRequests.CountAsync());

        var repeated = await service.StartVerificationRequestAsync(requester);
        Assert.Equal(request.Id, Assert.IsType<MessagingVerificationReview>(repeated.Request).Id);

        var requesterDetail = await service.GetConversationAsync(requester, persisted.Id);
        Assert.False(requesterDetail.Succeeded);

        var founderDetail = await service.GetConversationAsync(
            new MessagingActor("zac-founder-oid", MessagingParticipantTypes.Agent),
            persisted.Id);
        var reviewQueue = Assert.IsType<MessagingConversationDetail>(founderDetail.Conversation);
        Assert.True(reviewQueue.CanManageMembers);
        Assert.Single(reviewQueue.Messages);
        Assert.Equal("client-1", reviewQueue.Messages.Single().SenderUserId);
        var review = Assert.IsType<MessagingVerificationReview>(reviewQueue.Messages.Single().VerificationReview);
        Assert.True(review.CanResolve);

        var resolved = await service.ResolveVerificationReviewRequestAsync(
            new ResolveVerificationReviewRequestCommand(
                new MessagingActor("zac-founder-oid", MessagingParticipantTypes.Agent),
                request.Id,
                Approve: true));
        Assert.True(resolved.Succeeded);
        Assert.True((await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == "client-1")).IsVerified);
        Assert.Equal(VerificationReviewStatuses.Approved,
            (await db.VerificationReviewRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task LanguageTranslationAccess_UsesTheExistingFounderReviewQueueAndDirectGrant()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: false, grantClientToAgent: false);
        db.AgentProfiles.AddRange(
            new AgentProfile
            {
                AgentUserId = "zac-founder-oid",
                AgentUpn = LegendVerifiedIdentity.FounderEmail,
                NormalizedEmail = LegendVerifiedIdentity.FounderEmail,
                FullName = "Zac Owen",
                IsActive = true
            },
            new AgentProfile
            {
                AgentUserId = "legend-oid",
                AgentUpn = LegendVerifiedIdentity.LegendEmail,
                NormalizedEmail = LegendVerifiedIdentity.LegendEmail,
                FullName = "Legend™",
                IsActive = true
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var requester = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var request = await service.StartControlledResourceRequestAsync(
            new StartControlledResourceRequestCommand(requester, ControlledResourceTypes.LanguageTranslation));

        Assert.True(request.Succeeded);
        Assert.Equal(ControlledResourceTypes.LanguageTranslation, request.Request!.ResourceType);
        Assert.Equal(MessagingConversationPurposes.VerificationReview,
            (await db.MessageConversations.SingleAsync()).Purpose);

        var grant = await service.SetControlledResourceGrantAsync(
            new SetControlledResourceGrantCommand(
                new MessagingActor("zac-founder-oid", MessagingParticipantTypes.Agent),
                ControlledResourceTypes.LanguageTranslation,
                requester.UserId,
                requester.ParticipantType,
                IsGranted: true));

        Assert.True(grant.Succeeded);
        Assert.Equal(VerificationReviewStatuses.Approved,
            (await db.VerificationReviewRequests.SingleAsync()).Status);
        Assert.True(await db.ControlledResourceGrants.AnyAsync(access =>
            access.UserId == requester.UserId &&
            access.ParticipantType == requester.ParticipantType &&
            access.ResourceType == ControlledResourceTypes.LanguageTranslation &&
            access.IsActive));
        Assert.Equal(ControlledResourceAccessStates.Granted,
            (await new ControlledResourceAccessService(db).GetAccessAsync(
                requester,
                ControlledResourceTypes.LanguageTranslation)).State);
    }

    [Fact]
    public async Task MessageTranslation_CachesHaitianCreoleWithoutReplacingTheOriginalMessage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var clientProfile = await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1",
            ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation,
            IsActive = true,
            GrantedUtc = DateTime.UtcNow,
            GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = clientProfile.Id,
            ParticipantType = MessagingParticipantTypes.Client,
            PreferredCommunicationLanguage = "ht"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            agent,
            client.UserId,
            client.ParticipantType,
            InitialMessageBody: "Welcome to Legend"));
        var conversationId = Assert.IsType<MessagingConversationDetail>(opened.Conversation).Id;

        var firstView = await service.GetConversationAsync(client, conversationId);
        var message = Assert.Single(Assert.IsType<MessagingConversationDetail>(firstView.Conversation).Messages);
        Assert.Equal("Welcome to Legend (ht)", message.Body);
        Assert.Equal("Welcome to Legend", message.OriginalBody);
        Assert.Equal("en", message.Translation!.OriginalLanguage);
        Assert.Equal("ht", message.Translation.TargetLanguage);
        Assert.Equal("Welcome to Legend", (await db.InternalMessages.SingleAsync()).Body);
        Assert.Single(await db.MessageTranslations.ToListAsync());

        var secondView = await service.GetConversationAsync(client, conversationId);
        Assert.Equal("Welcome to Legend (ht)",
            Assert.Single(Assert.IsType<MessagingConversationDetail>(secondView.Conversation).Messages).Body);
        Assert.Single(await db.MessageTranslations.ToListAsync());
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

        Assert.Contains((await service.ListRecipientsAsync(
            firstAgent,
            recipientScope: MessagingRecipientScopes.Agents)).Recipients,
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
    public async Task AgentRecipients_ExposeOneCardForDuplicateAgentEmailAliases()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: false, grantClientToAgent: false);
        db.AgentProfiles.AddRange(
            new AgentProfile
            {
                AgentUserId = "legacy-actor-oid",
                AgentUpn = "agent.one@mylegnd.com",
                FullName = "Agent One",
                IsActive = true
            },
            new AgentProfile
            {
                AgentUserId = "legacy-peer-oid",
                AgentUpn = "peer@example.test",
                FullName = "Peer Agent",
                IsActive = true,
                CreatedUtc = DateTime.UtcNow.AddDays(-2),
                UpdatedUtc = DateTime.UtcNow.AddDays(-2)
            },
            new AgentProfile
            {
                AgentUserId = "current-peer-oid",
                AgentUpn = "peer@example.test",
                NormalizedEmail = "peer@example.test",
                FullName = "Peer Agent",
                Title = "Legend Agent",
                IsActive = true,
                CreatedUtc = DateTime.UtcNow.AddDays(-1),
                UpdatedUtc = DateTime.UtcNow.AddDays(-1)
            });
        await db.SaveChangesAsync();

        var recipients = await CreateService(db).ListRecipientsAsync(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent),
            recipientScope: MessagingRecipientScopes.Agents);

        Assert.True(recipients.Succeeded);
        var recipient = Assert.Single(recipients.Recipients);
        Assert.Equal("current-peer-oid", recipient.UserId);
        Assert.Equal("Peer Agent", recipient.DisplayName);
    }

    [Fact]
    public async Task ActiveClients_CompleteConversationFlowWithoutRequiringJourneyAcceptance()
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
        var firstClient = new MessagingActor(first.ClientUserId, MessagingParticipantTypes.Client);
        var secondClient = new MessagingActor(second.ClientUserId, MessagingParticipantTypes.Client);

        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            firstClient,
            secondClient.UserId,
            MessagingParticipantTypes.Client,
            InitialMessageBody: "Active clients can begin a private conversation."));

        Assert.True(opened.Succeeded);
        Assert.Contains((await service.ListRecipientsAsync(firstClient)).Recipients,
            recipient => recipient.UserId == secondClient.UserId && recipient.ParticipantType == MessagingParticipantTypes.Client);
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
    public async Task LegacyDirectConversationWithoutKey_IsClaimedInsteadOfDuplicated()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);

        var legacyConversation = new MessageConversation
        {
            Id = Guid.NewGuid(),
            ConversationType = MessagingConversationTypes.ClientAgent,
            DirectConversationKey = null,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedUtc = DateTime.UtcNow.AddDays(-1),
            CreatedByUserId = "agent-1"
        };
        db.MessageConversations.Add(legacyConversation);
        db.MessageConversationParticipants.AddRange(
            new MessageConversationParticipant
            {
                Id = Guid.NewGuid(),
                ConversationId = legacyConversation.Id,
                UserId = "agent-1",
                ParticipantType = MessagingParticipantTypes.Agent,
                IsActive = true,
                JoinedUtc = legacyConversation.CreatedUtc
            },
            new MessageConversationParticipant
            {
                Id = Guid.NewGuid(),
                ConversationId = legacyConversation.Id,
                UserId = "client-1",
                ParticipantType = MessagingParticipantTypes.Client,
                IsActive = true,
                JoinedUtc = legacyConversation.CreatedUtc
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                new MessagingActor("agent-1", MessagingParticipantTypes.Agent),
                "client-1",
                MessagingParticipantTypes.Client,
                InitialMessageBody: "Continue the existing conversation.",
                ClientMessageId: "legacy-direct-reuse"));

        Assert.True(result.Succeeded);
        Assert.Equal(legacyConversation.Id, result.Conversation!.Id);
        Assert.Single(await db.MessageConversations.ToListAsync());

        var stored = await db.MessageConversations.SingleAsync();
        Assert.Equal(
            "ClientAgent|5:Agent7:agent-1|6:Client8:client-1",
            stored.DirectConversationKey);
        Assert.Single(await db.InternalMessages.ToListAsync());
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

        var recipients = await service.ListRecipientsAsync(
            agent,
            recipientScope: MessagingRecipientScopes.Clients);
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
        Assert.Equal("Agent", recipient.RelationshipLabel);
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
    public async Task ActiveClientCanMessageAnyActiveAgentWithoutAServicingRelationship()
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

        Assert.True(result.Succeeded);
        Assert.Single(await db.MessageConversations.ToListAsync());
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

        Assert.Contains(clientRecipients.Recipients, x => x.UserId == "agent-1" && x.ParticipantType == MessagingParticipantTypes.Agent);
        Assert.Contains(clientRecipients.Recipients, x => x.UserId == "agent-2" && x.ParticipantType == MessagingParticipantTypes.Agent);
        Assert.Contains(clientRecipients.Recipients, x => x.UserId == "client-2" && x.ParticipantType == MessagingParticipantTypes.Client);
        Assert.Contains(agentRecipients.Recipients, x => x.UserId == "client-1");
        Assert.Contains(agentRecipients.Recipients, x => x.UserId == "agent-2");
        Assert.DoesNotContain(agentRecipients.Recipients, x => x.UserId == "client-2");
    }

    [Fact]
    public async Task AgentRecipientLookup_SeparatesAssignedClientsAndLeads()
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
                CrmStatus = "Lead",
                CrmNotes = "{\"recordType\":\"Lead\",\"pipelineStage\":\"NewLead\"}"
            },
            new ClientProfile
            {
                ClientUserId = "global-lead",
                ExternalIdentityObjectId = "global-lead",
                FirstName = "Global",
                LastName = "Lead",
                Email = "global.lead@example.test",
                CrmStatus = "Lead",
                CrmNotes = "{\"recordType\":\"Lead\",\"pipelineStage\":\"NewLead\"}"
            });
        db.AgentClients.AddRange(
            new AgentClient { AgentUserId = "agent-1", AgentUpn = "agent.one@mylegnd.com", ClientUserId = "business-client" },
            new AgentClient { AgentUserId = "agent-1", AgentUpn = "agent.one@mylegnd.com", ClientUserId = "lead-client" },
            new AgentClient { AgentUserId = "agent-2", AgentUpn = "agent.two@mylegnd.com", ClientUserId = "global-lead" });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);

        var recipients = await service.ListRecipientsAsync(agent);
        var clientRecipients = await service.ListRecipientsAsync(agent, recipientScope: MessagingRecipientScopes.Clients);
        var leadRecipients = await service.ListRecipientsAsync(agent, recipientScope: MessagingRecipientScopes.Leads);
        var leadStart = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                "lead-client",
                MessagingParticipantTypes.Client,
                InitialMessageBody: "This active lead can be messaged from the lead recipient search."));
        var globalLeadStart = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                "global-lead",
                MessagingParticipantTypes.Client,
                InitialMessageBody: "This active lead remains available outside assigned client ownership."));

        Assert.Contains(recipients.Recipients, recipient => recipient.UserId == "client-1");
        Assert.Contains(recipients.Recipients, recipient => recipient.UserId == "business-client");
        Assert.Contains(recipients.Recipients, recipient => recipient.UserId == "lead-client" && recipient.RelationshipLabel == "Lead");
        Assert.Contains(recipients.Recipients, recipient => recipient.UserId == "global-lead" && recipient.RelationshipLabel == "Lead");
        Assert.DoesNotContain(clientRecipients.Recipients, recipient => recipient.UserId == "lead-client");
        Assert.Contains(leadRecipients.Recipients, recipient => recipient.UserId == "lead-client" && recipient.RelationshipLabel == "Lead");
        Assert.Contains(leadRecipients.Recipients, recipient => recipient.UserId == "global-lead" && recipient.RelationshipLabel == "Lead");
        Assert.True(leadStart.Succeeded);
        Assert.True(globalLeadStart.Succeeded, globalLeadStart.ErrorMessage);
    }

    [Fact]
    public async Task AgentRecipientScopes_KeepGlobalAgentsSeparateFromAuthorizedClients()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        db.AgentProfiles.AddRange(
            new AgentProfile
            {
                AgentUserId = "agent-2",
                AgentUpn = "agent.two@example.test",
                FullName = "Active Agent",
                IsActive = true
            },
            new AgentProfile
            {
                AgentUserId = "inactive-agent",
                AgentUpn = "inactive.agent@example.test",
                FullName = "Inactive Agent",
                IsActive = false
            });
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "business-client",
            ExternalIdentityObjectId = "business-client",
            FirstName = "Business",
            LastName = "Client",
            Email = "business@example.test",
            CrmNotes = "{\"recordType\":\"BusinessClient\",\"pipelineStage\":\"BusinessClient\"}"
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-1",
            AgentUpn = "agent.one@mylegnd.com",
            ClientUserId = "business-client"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);

        var agentScope = await service.ListRecipientsAsync(
            agent,
            recipientScope: MessagingRecipientScopes.Agents);
        var clientScope = await service.ListRecipientsAsync(
            agent,
            recipientScope: MessagingRecipientScopes.Clients);

        Assert.True(agentScope.Succeeded);
        Assert.Contains(agentScope.Recipients, recipient =>
            recipient.UserId == "agent-2" && recipient.ParticipantType == MessagingParticipantTypes.Agent);
        Assert.DoesNotContain(agentScope.Recipients, recipient =>
            recipient.UserId == "agent-1" && recipient.ParticipantType == MessagingParticipantTypes.Agent);
        Assert.DoesNotContain(agentScope.Recipients, recipient => recipient.ParticipantType == MessagingParticipantTypes.Client);
        Assert.DoesNotContain(agentScope.Recipients, recipient => recipient.UserId == "inactive-agent");

        Assert.True(clientScope.Succeeded);
        Assert.Contains(clientScope.Recipients, recipient =>
            recipient.UserId == "client-1" && recipient.ParticipantType == MessagingParticipantTypes.Client);
        Assert.Contains(clientScope.Recipients, recipient =>
            recipient.UserId == "business-client" && recipient.ParticipantType == MessagingParticipantTypes.Client);
        Assert.DoesNotContain(clientScope.Recipients, recipient => recipient.ParticipantType == MessagingParticipantTypes.Agent);
    }

    [Fact]
    public async Task ClientRecipientScopes_ExposeActiveClientsAndAgentsButNeverLeads()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);

        var agentResult = await service.ListRecipientsAsync(
            new MessagingActor("client-1", MessagingParticipantTypes.Client),
            recipientScope: MessagingRecipientScopes.Agents);
        var clientResult = await service.ListRecipientsAsync(
            new MessagingActor("client-1", MessagingParticipantTypes.Client),
            recipientScope: MessagingRecipientScopes.Clients);
        var leadResult = await service.ListRecipientsAsync(
            new MessagingActor("client-1", MessagingParticipantTypes.Client),
            recipientScope: MessagingRecipientScopes.Leads);

        Assert.True(agentResult.Succeeded);
        Assert.All(agentResult.Recipients, recipient => Assert.Equal(MessagingParticipantTypes.Agent, recipient.ParticipantType));
        Assert.True(clientResult.Succeeded);
        Assert.All(clientResult.Recipients, recipient => Assert.Equal(MessagingParticipantTypes.Client, recipient.ParticipantType));
        Assert.False(leadResult.Succeeded);
        Assert.Equal("MESSAGING_RECIPIENT_SCOPE_INVALID", leadResult.ErrorCode);
    }

    [Fact]
    public async Task ClientRecipientLookup_ExcludesInactiveBlockedAndSuspendedProfiles()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);

        var activeClient = new ClientProfile
        {
            ClientUserId = "active-client",
            FirstName = "Active",
            LastName = "Client",
            Email = "active.client@example.test"
        };
        var inactiveClient = new ClientProfile
        {
            ClientUserId = "inactive-client",
            FirstName = "Inactive",
            LastName = "Client",
            Email = "inactive.client@example.test",
            CrmStatus = "Inactive"
        };
        var blockedClient = new ClientProfile
        {
            ClientUserId = "blocked-client",
            FirstName = "Blocked",
            LastName = "Client",
            Email = "blocked.client@example.test",
            CrmStatus = "Blocked"
        };
        var suspendedClient = new ClientProfile
        {
            ClientUserId = "suspended-client",
            FirstName = "Suspended",
            LastName = "Client",
            Email = "suspended.client@example.test"
        };
        var peerBlockedByActor = new ClientProfile
        {
            ClientUserId = "peer-blocked-client",
            FirstName = "Peer",
            LastName = "Blocked",
            Email = "peer.blocked@example.test"
        };
        db.ClientProfiles.AddRange(activeClient, inactiveClient, blockedClient, suspendedClient, peerBlockedByActor);
        await db.SaveChangesAsync();
        db.ClientSubscriptions.Add(new ClientSubscription
        {
            ClientProfileId = suspendedClient.Id,
            OwnerAgentUserId = "agent-1",
            Status = ClientSubscriptionStatus.Suspended
        });
        db.JourneyCircleBlocks.Add(new JourneyCircleBlock
        {
            Id = Guid.NewGuid(),
            BlockerClientProfileId = (await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == "client-1")).Id,
            BlockedClientProfileId = peerBlockedByActor.Id,
            CreatedUtc = DateTime.UtcNow
        });
        db.AgentProfiles.AddRange(
            new AgentProfile
            {
                AgentUserId = "active-agent",
                AgentUpn = "active.agent@example.test",
                FullName = "Active Agent",
                IsActive = true
            },
            new AgentProfile
            {
                AgentUserId = "inactive-agent",
                AgentUpn = "inactive.agent@example.test",
                FullName = "Inactive Agent",
                IsActive = false
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var actor = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var clients = await service.ListRecipientsAsync(actor, recipientScope: MessagingRecipientScopes.Clients);
        var agents = await service.ListRecipientsAsync(actor, recipientScope: MessagingRecipientScopes.Agents);

        Assert.True(clients.Succeeded);
        Assert.Contains(clients.Recipients, recipient => recipient.UserId == activeClient.ClientUserId);
        Assert.DoesNotContain(clients.Recipients, recipient => recipient.UserId == inactiveClient.ClientUserId);
        Assert.DoesNotContain(clients.Recipients, recipient => recipient.UserId == blockedClient.ClientUserId);
        Assert.DoesNotContain(clients.Recipients, recipient => recipient.UserId == suspendedClient.ClientUserId);
        Assert.DoesNotContain(clients.Recipients, recipient => recipient.UserId == peerBlockedByActor.ClientUserId);

        Assert.True(agents.Succeeded);
        Assert.Contains(agents.Recipients, recipient => recipient.UserId == "active-agent");
        Assert.DoesNotContain(agents.Recipients, recipient => recipient.UserId == "inactive-agent");

        var blockedDirectStart = await service.StartConversationAsync(new StartMessagingConversationCommand(
            actor,
            blockedClient.ClientUserId,
            MessagingParticipantTypes.Client,
            InitialMessageBody: "This must be rejected."));
        Assert.False(blockedDirectStart.Succeeded);
        Assert.Equal("MESSAGING_RECIPIENT_FORBIDDEN", blockedDirectStart.ErrorCode);
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
        var globalAgent = await service.GetAuthorizedParticipantAsync(
            client,
            "agent-2",
            MessagingParticipantTypes.Agent);

        var recipient = Assert.Single(search.Recipients);
        Assert.Equal("agent-1", recipient.UserId);
        Assert.True(participant.Succeeded);
        Assert.Equal("Agent One", participant.Recipient!.DisplayName);
        Assert.True(globalAgent.Succeeded);
        Assert.Equal("Other Agent", globalAgent.Recipient!.DisplayName);
    }

    [Fact]
    public async Task RecipientSearch_FindsAnAuthorizedProfileByItsUsername()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var agent = await db.AgentProfiles.SingleAsync(profile => profile.AgentUserId == "agent-1");
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            Id = Guid.NewGuid(),
            ProfileId = agent.Id,
            ParticipantType = MessagingParticipantTypes.Agent,
            Username = "agent.one",
            NormalizedUsername = "agent.one",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var recipients = await CreateService(db).ListRecipientsAsync(
            new MessagingActor("client-1", MessagingParticipantTypes.Client),
            "@agent.one");

        var recipient = Assert.Single(recipients.Recipients);
        Assert.Equal("agent-1", recipient.UserId);
        Assert.Equal("agent.one", recipient.Username);
    }

    [Fact]
    public async Task AgentUpnDoesNotAuthorizeASeparateAgentIdentity()
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
        var start = await service.StartConversationAsync(new StartMessagingConversationCommand(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent),
            "client-1",
            MessagingParticipantTypes.Client,
            InitialMessageBody: "An email alias must not authorize a different agent identity."));

        Assert.Empty(agentRecipients.Recipients.Where(x => x.ParticipantType == MessagingParticipantTypes.Client));
        Assert.Contains(clientRecipients.Recipients, x => x.UserId == "agent-1" && x.ParticipantType == MessagingParticipantTypes.Agent);
        Assert.False(start.Succeeded);
        Assert.Equal("MESSAGING_RECIPIENT_FORBIDDEN", start.ErrorCode);
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
        var images = new MessagingProfileImageResolver(
            db,
            NullLogger<MessagingProfileImageResolver>.Instance);
        return new MessagingService(
            db,
            NullLogger<MessagingService>.Instance,
            moderation,
            images,
            new ControlledResourceAccessService(db),
            new TestTranslationService());
    }

    private sealed class TestTranslationService : ITranslationService
    {
        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationProviderResult(
                true,
                $"{text} ({targetLanguage})",
                sourceLanguage ?? "en",
                "TestTranslator"));
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
