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
using Infrastructure.Notifications;
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

        // First messages use the same durable ledger as subsequent sends. The
        // app icon total is the database projection, never an inbox sum.
        var notification = await db.MobileActivityNotifications.SingleAsync(
            entry => entry.RecipientUserId == client.UserId &&
                     entry.RecipientParticipantType == client.ParticipantType &&
                     entry.ConversationId == conversation.Id);
        Assert.False(notification.IsRead);
        Assert.Equal(1, (await db.UserGlobalBadges.SingleAsync(
            badge => badge.UserId == client.UserId &&
                     badge.ParticipantType == client.ParticipantType)).UnreadCount);

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
        Assert.True((await db.MobileActivityNotifications.SingleAsync(
            entry => entry.Id == notification.Id)).IsRead);
        Assert.Equal(0, (await db.UserGlobalBadges.SingleAsync(
            badge => badge.UserId == client.UserId &&
                     badge.ParticipantType == client.ParticipantType)).UnreadCount);
        Assert.Equal(3, await db.MessagingAuditEntries.CountAsync());
    }

    [Fact]
    public async Task ConversationPage_ReturnsABoundedNewestWindowAndPreservesOlderHistory()
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
            InitialMessageBody: "Message one."));
        var conversationId = Assert.IsType<MessagingConversationDetail>(opened.Conversation).Id;

        foreach (var body in new[] { "Message two.", "Message three.", "Message four." })
        {
            Assert.True((await service.SendMessageAsync(new SendMessagingMessageCommand(
                client,
                conversationId,
                body))).Succeeded);
        }

        var newestPage = await service.GetConversationPageAsync(
            agent,
            conversationId,
            new MessagingConversationMessagePageQuery(Take: 2));
        var newest = Assert.IsType<MessagingConversationDetail>(newestPage.Conversation);
        Assert.Equal(["Message three.", "Message four."], newest.Messages.Select(message => message.Body));
        Assert.True(newest.HasOlderMessages);

        var olderPage = await service.GetConversationPageAsync(
            agent,
            conversationId,
            new MessagingConversationMessagePageQuery(newest.Messages[0].SentUtc, Take: 2));
        var older = Assert.IsType<MessagingConversationDetail>(olderPage.Conversation);
        Assert.Equal(["Message one.", "Message two."], older.Messages.Select(message => message.Body));
        Assert.False(older.HasOlderMessages);

        var fullConversation = await service.GetConversationAsync(agent, conversationId);
        Assert.Equal(4, Assert.IsType<MessagingConversationDetail>(fullConversation.Conversation).Messages.Count);
    }

    [Fact]
    public async Task FirstPersistedMessage_MakesTheCanonicalConversationVisibleToBothParticipants()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var service = CreateService(db);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var client = new MessagingActor("client-1", MessagingParticipantTypes.Client);

        var opened = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                client.UserId,
                client.ParticipantType));

        Assert.True(opened.Succeeded);
        var conversation = Assert.IsType<MessagingConversationDetail>(opened.Conversation);
        Assert.Empty(conversation.Messages);
        Assert.Empty((await service.ListConversationsAsync(
            agent,
            new MessagingConversationListQuery())).Conversations);
        Assert.Empty((await service.ListConversationsAsync(
            client,
            new MessagingConversationListQuery())).Conversations);

        var sent = await service.SendMessageAsync(
            new SendMessagingMessageCommand(
                agent,
                conversation.Id,
                "This first message establishes the inbox thread.",
                "first-message-establishes-inbox"));

        Assert.True(sent.Succeeded);
        var agentInbox = await service.ListConversationsAsync(
            agent,
            new MessagingConversationListQuery());
        var clientInbox = await service.ListConversationsAsync(
            client,
            new MessagingConversationListQuery());

        Assert.Equal(conversation.Id, Assert.Single(agentInbox.Conversations).Id);
        Assert.Equal(conversation.Id, Assert.Single(clientInbox.Conversations).Id);
        Assert.Equal(
            "This first message establishes the inbox thread.",
            Assert.Single(agentInbox.Conversations).LastMessagePreview);
        Assert.Equal(
            "This first message establishes the inbox thread.",
            Assert.Single(clientInbox.Conversations).LastMessagePreview);
        Assert.Equal(2, await db.MessageConversationParticipants.CountAsync(
            participant => participant.ConversationId == conversation.Id && participant.IsActive));
        Assert.Single(await db.InternalMessages.Where(
            message => message.ConversationId == conversation.Id).ToListAsync());
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

        var storedAgentParticipant = await db.MessageConversationParticipants
            .SingleAsync(participant =>
                participant.ConversationId == conversationId &&
                participant.ParticipantType == MessagingParticipantTypes.Agent);
        storedAgentParticipant.UserId = storedAgentParticipant.UserId.ToUpperInvariant();
        await db.SaveChangesAsync();

        var options = await service.GetConversationCallOptionsAsync(client, conversationId);

        Assert.True(options.Succeeded);
        Assert.Equal("Agent One", options.Options!.DisplayName);
        Assert.Equal("+14805550100", options.Options.PhoneNumber);
        Assert.Equal("agent.one@mylegnd.com", options.Options.FaceTimeAddress);
    }

    [Fact]
    public async Task GroupConversation_UsesAuthorizedRecipientsAndRejectsNonManagers()
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
            GroupImage: new MessagingGroupImage([1, 2, 3], "image/png"),
            Meeting: new MessagingGroupMeetingSetup(
                new MessagingParticipantReference("client-1", MessagingParticipantTypes.Client),
                "Wednesday Zoom",
                "https://zoom.us/j/123456789",
                new MessagingGroupMeetingSchedule(
                    MessagingGroupMeetingFrequencies.Biweekly,
                    ["Wednesday"],
                    "18:30",
                    "America/Phoenix"))));

        var conversation = Assert.IsType<MessagingConversationDetail>(created.Conversation);
        Assert.True(created.Succeeded);
        Assert.Equal(MessagingConversationTypes.Group, conversation.ConversationType);
        Assert.True(conversation.CanManageMembers);
        Assert.Equal(3, conversation.Participants.Count);
        Assert.Equal("image/png", conversation.GroupImage?.ContentType);
        Assert.Equal("Client One", conversation.Meeting?.Host.DisplayName);
        Assert.Equal("Wednesday Zoom", conversation.Meeting?.LinkLabel);
        Assert.Equal(MessagingGroupMeetingFrequencies.Biweekly, conversation.Meeting?.Schedule?.Frequency);
        Assert.Equal("Wednesday", Assert.Single(conversation.Meeting?.Schedule?.Weekdays ?? []));

        var updateGroup = await service.UpdateGroupProfileAsync(
            new UpdateMessagingGroupProfileCommand(
                owner,
                conversation.Id,
                "Protection review leaders",
                new MessagingGroupImage([4, 5, 6], "image/jpeg"),
                new MessagingGroupMeetingSetup(
                    new MessagingParticipantReference("client-2", MessagingParticipantTypes.Client),
                    "Weekly Teams room",
                    "https://teams.microsoft.com/l/meetup-join/example",
                    new MessagingGroupMeetingSchedule(
                        MessagingGroupMeetingFrequencies.Weekly,
                        ["Tuesday"],
                        "09:00",
                        "America/Phoenix"))));
        Assert.True(updateGroup.Succeeded);
        var updatedGroup = Assert.IsType<MessagingConversationDetail>(
            (await service.GetConversationAsync(owner, conversation.Id)).Conversation);
        Assert.Equal("Protection review leaders", updatedGroup.Subject);
        Assert.Equal("image/jpeg", updatedGroup.GroupImage?.ContentType);
        Assert.Equal("Client Two", updatedGroup.Meeting?.Host.DisplayName);
        Assert.Equal("Weekly Teams room", updatedGroup.Meeting?.LinkLabel);
        Assert.Equal("Tuesday", Assert.Single(updatedGroup.Meeting?.Schedule?.Weekdays ?? []));

        var addMember = await service.AddGroupParticipantAsync(
            new AddMessagingGroupParticipantCommand(
                owner,
                conversation.Id,
                "client-3",
                MessagingParticipantTypes.Client));
        Assert.True(addMember.Succeeded);
        Assert.Equal(4, Assert.IsType<MessagingConversationDetail>(
            (await service.GetConversationAsync(owner, conversation.Id)).Conversation).Participants.Count);

        var makeCollaborator = await service.SetGroupManagerAsync(
            new SetMessagingGroupManagerCommand(
                owner,
                conversation.Id,
                "client-1",
                MessagingParticipantTypes.Client,
                true));
        Assert.True(makeCollaborator.Succeeded);
        var collaboratorMeetingUpdate = await service.UpdateGroupProfileAsync(
            new UpdateMessagingGroupProfileCommand(
                new MessagingActor("client-1", MessagingParticipantTypes.Client),
                conversation.Id,
                "Protection review leaders",
                null,
                new MessagingGroupMeetingSetup(
                    new MessagingParticipantReference("client-1", MessagingParticipantTypes.Client),
                    "Unauthorized meeting",
                    "https://meet.google.com/example")));
        Assert.False(collaboratorMeetingUpdate.Succeeded);
        Assert.Equal("MESSAGING_GROUP_OWNER_REQUIRED", collaboratorMeetingUpdate.ErrorCode);

        var nonOwner = await service.AddGroupParticipantAsync(
            new AddMessagingGroupParticipantCommand(
                new MessagingActor("client-2", MessagingParticipantTypes.Client),
                conversation.Id,
                "client-3",
                MessagingParticipantTypes.Client));
        Assert.False(nonOwner.Succeeded);
        Assert.Equal("MESSAGING_GROUP_MANAGER_REQUIRED", nonOwner.ErrorCode);

        var nonOwnerUpdate = await service.UpdateGroupProfileAsync(
            new UpdateMessagingGroupProfileCommand(
                new MessagingActor("client-2", MessagingParticipantTypes.Client),
                conversation.Id,
                "Not allowed",
                null));
        Assert.False(nonOwnerUpdate.Succeeded);
        Assert.Equal("MESSAGING_GROUP_MANAGER_REQUIRED", nonOwnerUpdate.ErrorCode);
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
                Approve: true,
                ResolutionNote: "Your profile is confirmed. Welcome to verified Legend."));
        Assert.True(resolved.Succeeded);
        Assert.True((await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == "client-1")).IsVerified);
        Assert.Equal(VerificationReviewStatuses.Approved,
            (await db.VerificationReviewRequests.SingleAsync()).Status);

        // Decisions remain in the one staff review queue. The requester gets a
        // recipient-scoped Activity event instead of a new direct/group chat.
        Assert.Single(await db.MessageConversations.ToListAsync());
        Assert.Equal(2, await db.InternalMessages.CountAsync());
        var activity = await service.ListActivityNotificationsAsync(requester);
        var notification = Assert.Single(activity.Notifications);
        Assert.Equal("ControlledResourceApproved", notification.Kind);
        Assert.Equal("Legend verification approved", notification.Title);
        Assert.Equal("Your profile is confirmed. Welcome to verified Legend.", notification.Detail);
        Assert.Equal(request.Id, notification.ControlledResourceRequestId);
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

        var languages = await service.ListCommunicationLanguagesAsync(requester);
        Assert.True(languages.Succeeded);
        Assert.Equal(
            new[] { "en", "ht", "es", "fr", "pt", "de", "ja", "ko", "zh-Hans", "ar" },
            languages.Languages.Select(language => language.Code));
        Assert.Equal("English", languages.Languages[0].DisplayName);
        Assert.Equal("Haitian Creole", languages.Languages[1].DisplayName);
    }

    [Fact]
    public async Task LanguageTranslationAccess_AgentAndClientUseSameFounderControlledResource()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(
            db,
            linkClientToAgent: true,
            grantClientToAgent: false);

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

        var founder = new MessagingActor(
            "zac-founder-oid",
            MessagingParticipantTypes.Agent);

        var agent = new MessagingActor(
            "agent-1",
            MessagingParticipantTypes.Agent);

        var client = new MessagingActor(
            "client-1",
            MessagingParticipantTypes.Client);

        var agentRequest =
            await service.StartControlledResourceRequestAsync(
                new StartControlledResourceRequestCommand(
                    agent,
                    ControlledResourceTypes.LanguageTranslation));

        var clientRequest =
            await service.StartControlledResourceRequestAsync(
                new StartControlledResourceRequestCommand(
                    client,
                    ControlledResourceTypes.LanguageTranslation));

        Assert.True(agentRequest.Succeeded);
        Assert.True(clientRequest.Succeeded);

        Assert.Equal(
            MessagingParticipantTypes.Agent,
            agentRequest.Request!.RequesterParticipantType);

        Assert.Equal(
            MessagingParticipantTypes.Client,
            clientRequest.Request!.RequesterParticipantType);

        Assert.Equal(
            ControlledResourceTypes.LanguageTranslation,
            agentRequest.Request.ResourceType);

        Assert.Equal(
            ControlledResourceTypes.LanguageTranslation,
            clientRequest.Request.ResourceType);

        // Same Founder authority grants both roles.
        var agentGrant =
            await service.SetControlledResourceGrantAsync(
                new SetControlledResourceGrantCommand(
                    founder,
                    ControlledResourceTypes.LanguageTranslation,
                    agent.UserId,
                    agent.ParticipantType,
                    IsGranted: true));

        var clientGrant =
            await service.SetControlledResourceGrantAsync(
                new SetControlledResourceGrantCommand(
                    founder,
                    ControlledResourceTypes.LanguageTranslation,
                    client.UserId,
                    client.ParticipantType,
                    IsGranted: true));

        Assert.True(agentGrant.Succeeded);
        Assert.True(clientGrant.Succeeded);

        var access = new ControlledResourceAccessService(db);

        Assert.Equal(
            ControlledResourceAccessStates.Granted,
            (await access.GetAccessAsync(
                agent,
                ControlledResourceTypes.LanguageTranslation)).State);

        Assert.Equal(
            ControlledResourceAccessStates.Granted,
            (await access.GetAccessAsync(
                client,
                ControlledResourceTypes.LanguageTranslation)).State);

        // Preferred languages are stored through the same MobileProfileSettings
        // authority, discriminated only by participant type.
        var agentProfile =
            await db.AgentProfiles.SingleAsync(
                profile => profile.AgentUserId == agent.UserId);

        var clientProfile =
            await db.ClientProfiles.SingleAsync(
                profile => profile.ClientUserId == client.UserId);

        db.MobileProfileSettings.AddRange(
            new MobileProfileSettings
            {
                ProfileId = agentProfile.Id,
                ParticipantType = MessagingParticipantTypes.Agent,
                PreferredCommunicationLanguage = "ht"
            },
            new MobileProfileSettings
            {
                ProfileId = clientProfile.Id,
                ParticipantType = MessagingParticipantTypes.Client,
                PreferredCommunicationLanguage = "en"
            });

        await db.SaveChangesAsync();

        Assert.Equal(
            "ht",
            await access.GetPreferredLanguageAsync(agent));

        Assert.Equal(
            "en",
            await access.GetPreferredLanguageAsync(client));

        // Normal Agents may request/use translation but cannot manage grants.
        var unauthorizedDecision =
            await service.SetControlledResourceGrantAsync(
                new SetControlledResourceGrantCommand(
                    agent,
                    ControlledResourceTypes.LanguageTranslation,
                    client.UserId,
                    client.ParticipantType,
                    IsGranted: false));

        Assert.False(unauthorizedDecision.Succeeded);

        // Founder remains the centralized management authority.
        var founderDecision =
            await service.SetControlledResourceGrantAsync(
                new SetControlledResourceGrantCommand(
                    founder,
                    ControlledResourceTypes.LanguageTranslation,
                    agent.UserId,
                    agent.ParticipantType,
                    IsGranted: false));

        Assert.True(founderDecision.Succeeded);
    }

    [Fact]
    public async Task ActivityNotifications_ResolveBothClientIdentityForms()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: false, grantClientToAgent: false);
        var client = await db.ClientProfiles.SingleAsync();
        client.ExternalIdentityObjectId = "client-azure-object-id";
        db.MobileActivityNotifications.Add(new MobileActivityNotification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = client.ClientUserId,
            RecipientParticipantType = MessagingParticipantTypes.Client,
            Kind = "ControlledResourceApproved",
            Title = "Legend verification approved",
            Detail = "Your Legend verification was approved.",
            OccurredUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await CreateService(db).ListActivityNotificationsAsync(
            new MessagingActor(client.ExternalIdentityObjectId, MessagingParticipantTypes.Client));

        Assert.True(result.Succeeded);
        var notification = Assert.Single(result.Notifications);
        Assert.Equal("ControlledResourceApproved", notification.Kind);
    }

    [Fact]
    public async Task ClientAzureIdentity_ReceivesLegacyConversationWithoutCreatingANewThread()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var client = await db.ClientProfiles.SingleAsync();
        client.ExternalIdentityObjectId = "client-azure-object-id";
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var started = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                new MessagingActor("agent-1", MessagingParticipantTypes.Agent),
                client.ClientUserId,
                MessagingParticipantTypes.Client,
                InitialMessageBody: "Your Legend update is ready."));

        var azureClient = new MessagingActor(
            client.ExternalIdentityObjectId,
            MessagingParticipantTypes.Client);
        var inbox = await service.ListConversationsAsync(
            azureClient,
            new MessagingConversationListQuery());
        var detail = await service.GetConversationAsync(
            azureClient,
            started.Conversation!.Id);

        Assert.True(started.Succeeded);
        Assert.True(inbox.Succeeded);
        Assert.Single(inbox.Conversations);
        Assert.True(detail.Succeeded);
        Assert.Single(detail.Conversation!.Messages);
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
    public async Task LanguageTranslationRequest_AgentCanUseSharedControlledResourceRequest()
    {
        await using var db = ControllerTestHelpers.BuildDb();

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
            },
            new AgentProfile
            {
                AgentUserId = "requesting-agent",
                AgentUpn = "requesting.agent@example.test",
                NormalizedEmail = "requesting.agent@example.test",
                FullName = "Requesting Agent",
                IsActive = true
            });

        await db.SaveChangesAsync();

        var service = CreateService(db);

        var result = await service.StartControlledResourceRequestAsync(
            new StartControlledResourceRequestCommand(
                new MessagingActor(
                    "requesting-agent",
                    MessagingParticipantTypes.Agent),
                ControlledResourceTypes.LanguageTranslation));

        Assert.True(result.Succeeded);

        var request = Assert.IsType<MessagingVerificationReview>(result.Request);

        Assert.Equal(
            ControlledResourceTypes.LanguageTranslation,
            request.ResourceType);

        var persisted = Assert.Single(
            await db.VerificationReviewRequests
                .Where(x =>
                    x.RequesterUserId == "requesting-agent" &&
                    x.RequesterParticipantType == MessagingParticipantTypes.Agent &&
                    x.ResourceType == ControlledResourceTypes.LanguageTranslation)
                .ToListAsync());

        Assert.Equal(
            VerificationReviewStatuses.Pending,
            persisted.Status);

        Assert.Empty(
            await db.ControlledResourceGrants
                .Where(x =>
                    x.UserId == "requesting-agent" &&
                    x.ParticipantType == MessagingParticipantTypes.Agent &&
                    x.ResourceType == ControlledResourceTypes.LanguageTranslation)
                .ToListAsync());
    }

    [Fact]
    public async Task MessageTranslation_DoesNotTreatSenderPreferredLanguageAsMessageLanguage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(
            db,
            linkClientToAgent: true,
            grantClientToAgent: false);

        var agentProfile = await db.AgentProfiles.SingleAsync(
            profile => profile.AgentUserId == "agent-1");
        var clientProfile = await db.ClientProfiles.SingleAsync(
            profile => profile.ClientUserId == "client-1");

        // Both participants prefer Haitian Creole. This preference describes
        // how each participant wants to RECEIVE communication; it must never
        // be treated as proof that an individual message was written in ht.
        db.ControlledResourceGrants.AddRange(
            new ControlledResourceGrant
            {
                UserId = "agent-1",
                ParticipantType = MessagingParticipantTypes.Agent,
                ResourceType = ControlledResourceTypes.LanguageTranslation,
                IsActive = true,
                GrantedUtc = DateTime.UtcNow,
                GrantedByUserId = "zac-founder-oid"
            },
            new ControlledResourceGrant
            {
                UserId = "client-1",
                ParticipantType = MessagingParticipantTypes.Client,
                ResourceType = ControlledResourceTypes.LanguageTranslation,
                IsActive = true,
                GrantedUtc = DateTime.UtcNow,
                GrantedByUserId = "zac-founder-oid"
            });

        db.MobileProfileSettings.AddRange(
            new MobileProfileSettings
            {
                ProfileId = agentProfile.Id,
                ParticipantType = MessagingParticipantTypes.Agent,
                PreferredCommunicationLanguage = "ht"
            },
            new MobileProfileSettings
            {
                ProfileId = clientProfile.Id,
                ParticipantType = MessagingParticipantTypes.Client,
                PreferredCommunicationLanguage = "ht"
            });

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var agent = new MessagingActor(
            "agent-1",
            MessagingParticipantTypes.Agent);
        var client = new MessagingActor(
            "client-1",
            MessagingParticipantTypes.Client);

        // The sender PREFERS ht but actually writes this message in English.
        // TestTranslationService detects English ("en").
        var opened = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                client.UserId,
                client.ParticipantType,
                InitialMessageBody: "Welcome to Legend"));

        Assert.True(opened.Succeeded);

        var conversationId =
            Assert.IsType<MessagingConversationDetail>(
                opened.Conversation).Id;

        var clientView = await service.GetConversationAsync(
            client,
            conversationId);

        Assert.True(clientView.Succeeded);

        var presentedMessage = Assert.Single(
            Assert.IsType<MessagingConversationDetail>(
                clientView.Conversation).Messages);

        // Critical regression:
        //
        // OLD behavior:
        // sender prefers ht + recipient targets ht
        // => translation incorrectly skipped.
        //
        // CORRECT behavior:
        // actual message language is en + recipient targets ht
        // => existing translation pipeline runs.
        Assert.Equal(
            "Welcome to Legend (ht)",
            presentedMessage.Body);

        Assert.Equal(
            "Welcome to Legend",
            presentedMessage.OriginalBody);

        Assert.NotNull(presentedMessage.Translation);

        Assert.Equal(
            "en",
            presentedMessage.Translation!.OriginalLanguage);

        Assert.Equal(
            "ht",
            presentedMessage.Translation.TargetLanguage);

        // Original authoritative message must remain untouched.
        var original = await db.InternalMessages.SingleAsync();

        Assert.Equal(
            "Welcome to Legend",
            original.Body);

        // Existing translation cache remains the one persistence authority.
        var cachedTranslation =
            Assert.Single(await db.MessageTranslations.ToListAsync());

        Assert.Equal(
            original.Id,
            cachedTranslation.InternalMessageId);

        // Original language belongs to the authoritative InternalMessage,
        // not to the cached MessageTranslation derivative.
        Assert.Equal(
            "en",
            original.OriginalLanguage);

        Assert.Equal(
            "ht",
            cachedTranslation.TargetLanguage);

        Assert.Equal(
            "Welcome to Legend (ht)",
            cachedTranslation.TranslatedText);
    }

    [Fact]
    public async Task MessageTranslation_ClientToClient_IsBidirectionalByRecipientLanguage()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        db.ClientProfiles.AddRange(
            new ClientProfile
            {
                ClientUserId = "client-en",
                ExternalIdentityObjectId = "client-en",
                FirstName = "English",
                LastName = "Client",
                Email = "english.client@example.test",
                CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
            },
            new ClientProfile
            {
                ClientUserId = "client-ht",
                ExternalIdentityObjectId = "client-ht",
                FirstName = "Creole",
                LastName = "Client",
                Email = "creole.client@example.test",
                CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
            });

        await db.SaveChangesAsync();

        var englishProfile = await db.ClientProfiles.SingleAsync(
            x => x.ClientUserId == "client-en");
        var creoleProfile = await db.ClientProfiles.SingleAsync(
            x => x.ClientUserId == "client-ht");

        db.ControlledResourceGrants.AddRange(
            new ControlledResourceGrant
            {
                UserId = "client-en",
                ParticipantType = MessagingParticipantTypes.Client,
                ResourceType = ControlledResourceTypes.LanguageTranslation,
                IsActive = true,
                GrantedUtc = DateTime.UtcNow,
                GrantedByUserId = "zac-founder-oid"
            },
            new ControlledResourceGrant
            {
                UserId = "client-ht",
                ParticipantType = MessagingParticipantTypes.Client,
                ResourceType = ControlledResourceTypes.LanguageTranslation,
                IsActive = true,
                GrantedUtc = DateTime.UtcNow,
                GrantedByUserId = "zac-founder-oid"
            });

        db.MobileProfileSettings.AddRange(
            new MobileProfileSettings
            {
                ProfileId = englishProfile.Id,
                ParticipantType = MessagingParticipantTypes.Client,
                PreferredCommunicationLanguage = "en"
            },
            new MobileProfileSettings
            {
                ProfileId = creoleProfile.Id,
                ParticipantType = MessagingParticipantTypes.Client,
                PreferredCommunicationLanguage = "ht"
            });

        await db.SaveChangesAsync();

        var translator = new TestTranslationService();
        var service = CreateService(db, translator);

        var englishClient =
            new MessagingActor("client-en", MessagingParticipantTypes.Client);
        var creoleClient =
            new MessagingActor("client-ht", MessagingParticipantTypes.Client);

        const string englishBody = "I hope you are having a great day.";
        const string creoleBody = "Mwen espere ou pase yon bon jounen.";

        var opened = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                englishClient,
                creoleClient.UserId,
                creoleClient.ParticipantType,
                InitialMessageBody: englishBody,
                ClientMessageId: "translation-client-client-en"));

        Assert.True(opened.Succeeded);

        var conversation =
            Assert.IsType<MessagingConversationDetail>(opened.Conversation);

        var senderEnglishView =
            await service.GetConversationAsync(englishClient, conversation.Id);

        var senderEnglishMessage = Assert.Single(
            Assert.IsType<MessagingConversationDetail>(
                senderEnglishView.Conversation).Messages);

        Assert.Equal(englishBody, senderEnglishMessage.Body);
        Assert.Null(senderEnglishMessage.OriginalBody);
        Assert.Null(senderEnglishMessage.Translation);

        var creoleView =
            await service.GetConversationAsync(creoleClient, conversation.Id);

        var translatedEnglish = Assert.Single(
            Assert.IsType<MessagingConversationDetail>(
                creoleView.Conversation).Messages);

        Assert.Equal($"{englishBody} (ht)", translatedEnglish.Body);
        Assert.Equal(englishBody, translatedEnglish.OriginalBody);
        Assert.NotNull(translatedEnglish.Translation);
        Assert.Equal("en", translatedEnglish.Translation!.OriginalLanguage);
        Assert.Equal("ht", translatedEnglish.Translation.TargetLanguage);

        var originalEnglish = await db.InternalMessages.SingleAsync();
        Assert.Equal(englishBody, originalEnglish.Body);
        Assert.Equal("en", originalEnglish.OriginalLanguage);

        var englishToHtCache = Assert.Single(
            await db.MessageTranslations
                .Where(x => x.InternalMessageId == originalEnglish.Id)
                .ToListAsync());

        Assert.Equal("ht", englishToHtCache.TargetLanguage);
        Assert.Equal($"{englishBody} (ht)", englishToHtCache.TranslatedText);

        var callsAfterFirstTranslation = translator.TranslationCallCount;

        var creoleReread =
            await service.GetConversationAsync(creoleClient, conversation.Id);

        Assert.Equal(
            $"{englishBody} (ht)",
            Assert.Single(
                Assert.IsType<MessagingConversationDetail>(
                    creoleReread.Conversation).Messages).Body);

        Assert.Equal(
            callsAfterFirstTranslation,
            translator.TranslationCallCount);

        var sentBack = await service.SendMessageAsync(
            new SendMessagingMessageCommand(
                creoleClient,
                conversation.Id,
                creoleBody,
                ClientMessageId: "translation-client-client-ht"));

        Assert.True(sentBack.Succeeded);

        var creoleSenderView =
            await service.GetConversationAsync(creoleClient, conversation.Id);

        var creoleSenderMessages =
            Assert.IsType<MessagingConversationDetail>(
                creoleSenderView.Conversation).Messages;

        var creoleSenderMessage = Assert.Single(
            creoleSenderMessages.Where(x => x.Body == creoleBody));

        Assert.Equal(creoleBody, creoleSenderMessage.Body);
        Assert.Null(creoleSenderMessage.OriginalBody);
        Assert.Null(creoleSenderMessage.Translation);

        var englishRecipientView =
            await service.GetConversationAsync(englishClient, conversation.Id);

        var englishRecipientMessages =
            Assert.IsType<MessagingConversationDetail>(
                englishRecipientView.Conversation).Messages;

        var translatedCreole = Assert.Single(
            englishRecipientMessages.Where(
                x => x.OriginalBody == creoleBody));

        Assert.Equal($"{creoleBody} (en)", translatedCreole.Body);
        Assert.Equal(creoleBody, translatedCreole.OriginalBody);
        Assert.NotNull(translatedCreole.Translation);
        Assert.Equal("ht", translatedCreole.Translation!.OriginalLanguage);
        Assert.Equal("en", translatedCreole.Translation.TargetLanguage);

        var originalCreole = await db.InternalMessages.SingleAsync(
            x => x.Body == creoleBody);

        Assert.Equal(creoleBody, originalCreole.Body);
        Assert.Equal("ht", originalCreole.OriginalLanguage);

        var creoleToEnCache = Assert.Single(
            await db.MessageTranslations
                .Where(x => x.InternalMessageId == originalCreole.Id)
                .ToListAsync());

        Assert.Equal("en", creoleToEnCache.TargetLanguage);
        Assert.Equal($"{creoleBody} (en)", creoleToEnCache.TranslatedText);

        var callsBeforeEnglishReread = translator.TranslationCallCount;

        await service.GetConversationAsync(englishClient, conversation.Id);

        Assert.Equal(
            callsBeforeEnglishReread,
            translator.TranslationCallCount);

        Assert.Equal(
            2,
            await db.MessageTranslations.CountAsync());

        Assert.Equal(
            2,
            await db.InternalMessages.CountAsync());
    }

    [Fact]
    public async Task MessageTranslation_ClientAndAgent_IsBidirectionalByRecipientLanguage()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-ht",
            AgentUpn = "agent.ht@mylegnd.com",
            FullName = "Creole Agent",
            IsActive = true
        });

        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "client-en",
            ExternalIdentityObjectId = "client-en",
            FirstName = "English",
            LastName = "Client",
            Email = "english.client@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        });

        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-ht",
            AgentUpn = "agent.ht@mylegnd.com",
            ClientUserId = "client-en"
        });

        await db.SaveChangesAsync();

        var agentProfile = await db.AgentProfiles.SingleAsync(
            x => x.AgentUserId == "agent-ht");
        var clientProfile = await db.ClientProfiles.SingleAsync(
            x => x.ClientUserId == "client-en");

        db.ControlledResourceGrants.AddRange(
            new ControlledResourceGrant
            {
                UserId = "agent-ht",
                ParticipantType = MessagingParticipantTypes.Agent,
                ResourceType = ControlledResourceTypes.LanguageTranslation,
                IsActive = true,
                GrantedUtc = DateTime.UtcNow,
                GrantedByUserId = "zac-founder-oid"
            },
            new ControlledResourceGrant
            {
                UserId = "client-en",
                ParticipantType = MessagingParticipantTypes.Client,
                ResourceType = ControlledResourceTypes.LanguageTranslation,
                IsActive = true,
                GrantedUtc = DateTime.UtcNow,
                GrantedByUserId = "zac-founder-oid"
            });

        db.MobileProfileSettings.AddRange(
            new MobileProfileSettings
            {
                ProfileId = agentProfile.Id,
                ParticipantType = MessagingParticipantTypes.Agent,
                PreferredCommunicationLanguage = "ht"
            },
            new MobileProfileSettings
            {
                ProfileId = clientProfile.Id,
                ParticipantType = MessagingParticipantTypes.Client,
                PreferredCommunicationLanguage = "en"
            });

        await db.SaveChangesAsync();

        var translator = new TestTranslationService();
        var service = CreateService(db, translator);

        var client =
            new MessagingActor("client-en", MessagingParticipantTypes.Client);
        var agent =
            new MessagingActor("agent-ht", MessagingParticipantTypes.Agent);

        const string englishBody =
            "Your appointment is confirmed for tomorrow.";
        const string creoleBody =
            "Mwen konfime randevou ou pou demen.";

        var opened = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                client,
                agent.UserId,
                agent.ParticipantType,
                InitialMessageBody: englishBody,
                ClientMessageId: "translation-client-agent-en"));

        Assert.True(opened.Succeeded);

        var conversation =
            Assert.IsType<MessagingConversationDetail>(opened.Conversation);

        var clientSenderView =
            await service.GetConversationAsync(client, conversation.Id);

        var clientOriginal = Assert.Single(
            Assert.IsType<MessagingConversationDetail>(
                clientSenderView.Conversation).Messages);

        Assert.Equal(englishBody, clientOriginal.Body);
        Assert.Null(clientOriginal.OriginalBody);
        Assert.Null(clientOriginal.Translation);

        var agentRecipientView =
            await service.GetConversationAsync(agent, conversation.Id);

        var agentTranslated = Assert.Single(
            Assert.IsType<MessagingConversationDetail>(
                agentRecipientView.Conversation).Messages);

        Assert.Equal($"{englishBody} (ht)", agentTranslated.Body);
        Assert.Equal(englishBody, agentTranslated.OriginalBody);
        Assert.NotNull(agentTranslated.Translation);
        Assert.Equal("en", agentTranslated.Translation!.OriginalLanguage);
        Assert.Equal("ht", agentTranslated.Translation.TargetLanguage);

        var originalEnglish = await db.InternalMessages.SingleAsync();

        Assert.Equal(englishBody, originalEnglish.Body);
        Assert.Equal("en", originalEnglish.OriginalLanguage);

        var englishToHt = Assert.Single(
            await db.MessageTranslations
                .Where(x => x.InternalMessageId == originalEnglish.Id)
                .ToListAsync());

        Assert.Equal("ht", englishToHt.TargetLanguage);

        var callsAfterAgentTranslation = translator.TranslationCallCount;

        await service.GetConversationAsync(agent, conversation.Id);

        Assert.Equal(
            callsAfterAgentTranslation,
            translator.TranslationCallCount);

        var response = await service.SendMessageAsync(
            new SendMessagingMessageCommand(
                agent,
                conversation.Id,
                creoleBody,
                ClientMessageId: "translation-agent-client-ht"));

        Assert.True(response.Succeeded);

        var agentSenderView =
            await service.GetConversationAsync(agent, conversation.Id);

        var agentSenderMessages =
            Assert.IsType<MessagingConversationDetail>(
                agentSenderView.Conversation).Messages;

        var agentOriginal = Assert.Single(
            agentSenderMessages.Where(x => x.Body == creoleBody));

        Assert.Null(agentOriginal.OriginalBody);
        Assert.Null(agentOriginal.Translation);

        var clientRecipientView =
            await service.GetConversationAsync(client, conversation.Id);

        var clientMessages =
            Assert.IsType<MessagingConversationDetail>(
                clientRecipientView.Conversation).Messages;

        var clientTranslated = Assert.Single(
            clientMessages.Where(x => x.OriginalBody == creoleBody));

        Assert.Equal($"{creoleBody} (en)", clientTranslated.Body);
        Assert.Equal(creoleBody, clientTranslated.OriginalBody);
        Assert.NotNull(clientTranslated.Translation);
        Assert.Equal("ht", clientTranslated.Translation!.OriginalLanguage);
        Assert.Equal("en", clientTranslated.Translation.TargetLanguage);

        var originalCreole = await db.InternalMessages.SingleAsync(
            x => x.Body == creoleBody);

        Assert.Equal(creoleBody, originalCreole.Body);
        Assert.Equal("ht", originalCreole.OriginalLanguage);

        var creoleToEnglish = Assert.Single(
            await db.MessageTranslations
                .Where(x => x.InternalMessageId == originalCreole.Id)
                .ToListAsync());

        Assert.Equal("en", creoleToEnglish.TargetLanguage);

        var callsBeforeClientReread = translator.TranslationCallCount;

        await service.GetConversationAsync(client, conversation.Id);

        Assert.Equal(
            callsBeforeClientReread,
            translator.TranslationCallCount);

        Assert.Equal(
            2,
            await db.MessageTranslations.CountAsync());

        Assert.Equal(
            2,
            await db.InternalMessages.CountAsync());
    }

    [Fact]
    public async Task MessageTranslation_AgentInboxPreviewUsesRecipientPresentationAndSharedCache()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        db.AgentProfiles.AddRange(
            new AgentProfile
            {
                AgentUserId = "agent-en",
                AgentUpn = "agent.en@example.test",
                FullName = "English Agent",
                IsActive = true
            },
            new AgentProfile
            {
                AgentUserId = "agent-ht",
                AgentUpn = "agent.ht@example.test",
                FullName = "Creole Agent",
                IsActive = true
            });
        await db.SaveChangesAsync();

        var englishProfile = await db.AgentProfiles.SingleAsync(
            profile => profile.AgentUserId == "agent-en");
        var creoleProfile = await db.AgentProfiles.SingleAsync(
            profile => profile.AgentUserId == "agent-ht");

        db.ControlledResourceGrants.AddRange(
            new ControlledResourceGrant
            {
                UserId = "agent-en",
                ParticipantType = MessagingParticipantTypes.Agent,
                ResourceType = ControlledResourceTypes.LanguageTranslation,
                IsActive = true,
                GrantedUtc = DateTime.UtcNow,
                GrantedByUserId = "zac-founder-oid"
            },
            new ControlledResourceGrant
            {
                UserId = "agent-ht",
                ParticipantType = MessagingParticipantTypes.Agent,
                ResourceType = ControlledResourceTypes.LanguageTranslation,
                IsActive = true,
                GrantedUtc = DateTime.UtcNow,
                GrantedByUserId = "zac-founder-oid"
            });
        db.MobileProfileSettings.AddRange(
            new MobileProfileSettings
            {
                ProfileId = englishProfile.Id,
                ParticipantType = MessagingParticipantTypes.Agent,
                PreferredCommunicationLanguage = "en"
            },
            new MobileProfileSettings
            {
                ProfileId = creoleProfile.Id,
                ParticipantType = MessagingParticipantTypes.Agent,
                PreferredCommunicationLanguage = "ht"
            });
        await db.SaveChangesAsync();

        var translator = new TestTranslationService();
        var service = CreateService(db, translator);
        var englishAgent = new MessagingActor("agent-en", MessagingParticipantTypes.Agent);
        var creoleAgent = new MessagingActor("agent-ht", MessagingParticipantTypes.Agent);
        const string englishBody = "Your appointment is confirmed for tomorrow.";
        const string creoleBody = "Mwen konfime randevou ou pou demen.";

        var opened = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                englishAgent,
                creoleAgent.UserId,
                creoleAgent.ParticipantType,
                InitialMessageBody: englishBody));

        Assert.True(opened.Succeeded);
        var conversation = Assert.IsType<MessagingConversationDetail>(opened.Conversation);

        var englishInboxBeforeTranslation = await service.ListConversationsAsync(
            englishAgent,
            new MessagingConversationListQuery());

        Assert.True(englishInboxBeforeTranslation.Succeeded);
        Assert.Equal(
            englishBody,
            Assert.Single(englishInboxBeforeTranslation.Conversations).LastMessagePreview);

        var creoleInbox = await service.ListConversationsAsync(
            creoleAgent,
            new MessagingConversationListQuery());

        Assert.True(creoleInbox.Succeeded);
        Assert.Equal($"{englishBody} (ht)", Assert.Single(creoleInbox.Conversations).LastMessagePreview);

        var englishSource = await db.InternalMessages.SingleAsync();
        Assert.Equal(englishBody, englishSource.Body);
        Assert.Equal("en", englishSource.OriginalLanguage);
        var englishTranslation = Assert.Single(await db.MessageTranslations.ToListAsync());
        Assert.Equal(englishSource.Id, englishTranslation.InternalMessageId);
        Assert.Equal("ht", englishTranslation.TargetLanguage);

        var callsAfterCreoleInbox = translator.TranslationCallCount;
        var creoleConversation = await service.GetConversationAsync(creoleAgent, conversation.Id);
        Assert.Equal(
            $"{englishBody} (ht)",
            Assert.Single(creoleConversation.Conversation!.Messages).Body);
        Assert.Equal(callsAfterCreoleInbox, translator.TranslationCallCount);

        var reply = await service.SendMessageAsync(
            new SendMessagingMessageCommand(
                creoleAgent,
                conversation.Id,
                creoleBody));
        Assert.True(reply.Succeeded);

        var creoleInboxAfterReply = await service.ListConversationsAsync(
            creoleAgent,
            new MessagingConversationListQuery());

        Assert.True(creoleInboxAfterReply.Succeeded);
        Assert.Equal(
            creoleBody,
            Assert.Single(creoleInboxAfterReply.Conversations).LastMessagePreview);

        var englishInbox = await service.ListConversationsAsync(
            englishAgent,
            new MessagingConversationListQuery());

        Assert.True(englishInbox.Succeeded);
        Assert.Equal($"{creoleBody} (en)", Assert.Single(englishInbox.Conversations).LastMessagePreview);

        var creoleSource = await db.InternalMessages.SingleAsync(message => message.Body == creoleBody);
        Assert.Equal(creoleBody, creoleSource.Body);
        Assert.Equal("ht", creoleSource.OriginalLanguage);
        Assert.Contains(
            await db.MessageTranslations.ToListAsync(),
            translation =>
                translation.InternalMessageId == creoleSource.Id &&
                translation.TargetLanguage == "en");

        var callsAfterEnglishInbox = translator.TranslationCallCount;
        var englishConversation = await service.GetConversationAsync(englishAgent, conversation.Id);
        var translatedReply = Assert.Single(
            englishConversation.Conversation!.Messages.Where(
                message => message.OriginalBody == creoleBody));
        Assert.Equal($"{creoleBody} (en)", translatedReply.Body);
        Assert.Equal(callsAfterEnglishInbox, translator.TranslationCallCount);
    }

    [Fact]
    public async Task MessageTranslation_UsesTheSameGrantForAzureAndLegacyClientIdentityForms()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var clientProfile = await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == "client-1");
        clientProfile.ExternalIdentityObjectId = "client-azure-object-id";
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = clientProfile.ClientUserId,
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
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent),
            clientProfile.ClientUserId,
            MessagingParticipantTypes.Client,
            InitialMessageBody: "Welcome to Legend"));
        var azureClient = new MessagingActor(
            clientProfile.ExternalIdentityObjectId,
            MessagingParticipantTypes.Client);

        var translatedView = await service.GetConversationAsync(
            azureClient,
            opened.Conversation!.Id);

        Assert.True(translatedView.Succeeded);
        var message = Assert.Single(translatedView.Conversation!.Messages);
        Assert.Equal("Welcome to Legend (ht)", message.Body);
        Assert.Equal("Welcome to Legend", message.OriginalBody);
        Assert.Equal("ht", message.Translation!.TargetLanguage);
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
    public async Task FounderPromotion_ReusesTheCanonicalGroupAndIdempotentlyJoinsMembers()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: false, grantClientToAgent: false);
        var founderObjectId = Guid.NewGuid().ToString();
        var founder = new MessagingActor(founderObjectId, MessagingParticipantTypes.Agent);
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = founderObjectId,
            AgentUpn = "founder@example.test",
            FullName = "Founder",
            IsActive = true
        });
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-2",
            ExternalIdentityObjectId = "client-2",
            FirstName = "Client",
            LastName = "Two",
            Email = "client.two@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        });
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-3",
            ExternalIdentityObjectId = "client-3",
            FirstName = "Client",
            LastName = "Three",
            Email = "client.three@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, configuredFounderOid: founderObjectId);

        var created = await service.CreateGroupAsync(new CreateMessagingGroupCommand(
            founder,
            [
                new MessagingParticipantReference("client-1", MessagingParticipantTypes.Client),
                new MessagingParticipantReference("client-2", MessagingParticipantTypes.Client)
            ],
            "Founder office hours"));
        Assert.True(created.Succeeded);
        var conversationId = created.Conversation!.Id;

        var denied = await service.SetGroupPromotionAsync(new SetMessagingGroupPromotionCommand(
            new MessagingActor("agent-1", MessagingParticipantTypes.Agent), conversationId, true));
        Assert.False(denied.Succeeded);
        Assert.Equal("MESSAGING_GROUP_PROMOTION_FORBIDDEN", denied.ErrorCode);

        var promoted = await service.SetGroupPromotionAsync(new SetMessagingGroupPromotionCommand(
            founder, conversationId, true));
        Assert.True(promoted.Succeeded);
        Assert.True(promoted.Conversation!.IsPromoted);
        Assert.True(promoted.Conversation.CanManagePromotion);
        Assert.NotNull(promoted.Conversation.PromotionStartedUtc);
        Assert.Equal(conversationId, promoted.Conversation.Id);

        var joiner = new MessagingActor("client-3", MessagingParticipantTypes.Client);
        Assert.True((await service.JoinPromotedGroupAsync(
            new JoinPromotedMessagingGroupCommand(joiner, conversationId))).Succeeded);
        Assert.True((await service.JoinPromotedGroupAsync(
            new JoinPromotedMessagingGroupCommand(joiner, conversationId))).Succeeded);
        Assert.Single(await db.MessageConversationParticipants.Where(participant =>
            participant.ConversationId == conversationId &&
            participant.UserId == joiner.UserId &&
            participant.ParticipantType == joiner.ParticipantType).ToListAsync());

        var stopped = await service.SetGroupPromotionAsync(new SetMessagingGroupPromotionCommand(
            founder, conversationId, false));
        Assert.True(stopped.Succeeded);
        Assert.False(stopped.Conversation!.IsPromoted);
        Assert.NotNull(stopped.Conversation.PromotionEndedUtc);
        var unavailable = await service.JoinPromotedGroupAsync(
            new JoinPromotedMessagingGroupCommand(new MessagingActor("client-1", MessagingParticipantTypes.Client), conversationId));
        Assert.False(unavailable.Succeeded);
        Assert.Equal("MESSAGING_PROMOTED_GROUP_UNAVAILABLE", unavailable.ErrorCode);
    }

    [Fact]
    public async Task OrdinaryAgent_CanMessageTheFounderClientUsingItsCanonicalObjectId()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: false, grantClientToAgent: false);
        var founderObjectId = Guid.NewGuid().ToString();
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "founder-client-legacy-id",
            ExternalIdentityObjectId = founderObjectId,
            FirstName = "Founder",
            LastName = "Client",
            Email = "founder.client@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, configuredFounderOid: founderObjectId);
        var agent = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);

        var recipient = await service.GetAuthorizedParticipantAsync(
            agent,
            founderObjectId,
            MessagingParticipantTypes.Client);
        var conversation = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                agent,
                founderObjectId,
                MessagingParticipantTypes.Client,
                InitialMessageBody: "Welcome to the team."));

        Assert.True(recipient.Succeeded);
        Assert.Equal("founder-client-legacy-id", recipient.Recipient!.UserId);
        Assert.True(conversation.Succeeded, $"{conversation.ErrorCode}: {conversation.ErrorMessage}");
        Assert.Equal("founder-client-legacy-id", conversation.Conversation!.Participants
            .Single(participant => participant.ParticipantType == MessagingParticipantTypes.Client).UserId);
    }

    [Fact]
    public void AddMasterAppMessaging_RegistersTheSingleMessagingService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
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

    private static MessagingService CreateService(
        Infrastructure.Data.MasterAppDbContext db,
        TestTranslationService? translation = null,
        string? configuredFounderOid = null)
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
            translation ?? new TestTranslationService(),
            new NotificationEngine(
                db,
                images,
                new NoopNotificationRealtimePublisher(),
                NullLogger<NotificationEngine>.Instance),
            configuredFounderOid);
    }

    private sealed class NoopNotificationRealtimePublisher : INotificationRealtimePublisher
    {
        public Task PublishAsync(
            MessagingActor recipient,
            NotificationRealtimeEvent notification,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestTranslationService : ITranslationService
    {
        public int DetectionCallCount { get; private set; }

        public int TranslationCallCount { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            DetectionCallCount++;

            var language = text switch
            {
                "Mwen espere ou pase yon bon jounen." => "ht",
                "Mwen konfime randevou ou pou demen." => "ht",
                _ => "en"
            };

            return Task.FromResult(
                new TranslationDetectionResult(true, language));
        }

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            TranslationCallCount++;

            var detectedSource = sourceLanguage ?? text switch
            {
                "Mwen espere ou pase yon bon jounen." => "ht",
                "Mwen konfime randevou ou pou demen." => "ht",
                _ => "en"
            };

            return Task.FromResult(
                new TranslationProviderResult(
                    true,
                    $"{text} ({targetLanguage})",
                    detectedSource,
                    "TestTranslator"));
        }
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
