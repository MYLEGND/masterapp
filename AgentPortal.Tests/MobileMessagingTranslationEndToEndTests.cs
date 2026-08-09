using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Infrastructure.Moderation;
using Infrastructure.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileMessagingTranslationEndToEndTests
{
    [Fact]
    public async Task RecipientMobileEndpoint_ReturnsTheServerLocalizedMessageAndPreservesTheOriginal()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "translation-agent",
            AgentUpn = "agent@example.test",
            FullName = "English Agent",
            IsActive = true
        };
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "translation-client",
            ExternalIdentityObjectId = "translation-client",
            FirstName = "Creole",
            LastName = "Client",
            Email = "client@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        };
        db.AddRange(agent, client);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agent.AgentUserId,
            AgentUpn = agent.AgentUpn,
            ClientUserId = client.ClientUserId
        });
        db.ClientEntitlements.Add(new ClientEntitlement
        {
            Id = Guid.NewGuid(),
            ClientProfileId = client.Id,
            EntitlementKey = BillingEntitlementKeys.ClientAppFullAccess,
            Status = ClientEntitlementStatus.Active,
            SourceType = ClientEntitlementSourceType.Subscription,
            SourceId = "translation-test-subscription",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = client.ClientUserId,
            ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation,
            IsActive = true,
            GrantedUtc = DateTime.UtcNow,
            GrantedByUserId = "founder"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = client.Id,
            ParticipantType = MessagingParticipantTypes.Client,
            PreferredCommunicationLanguage = "ht"
        });
        await db.SaveChangesAsync();

        var translator = new Mock<ITranslationService>(MockBehavior.Strict);
        translator.Setup(value => value.DetectLanguageAsync(
                "Hello, how are you?",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationDetectionResult(true, "en"));
        translator.Setup(value => value.TranslateAsync(
                "Hello, how are you?",
                "ht",
                "en",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationProviderResult(
                true,
                "Bonjou, kijan ou ye?",
                "en",
                "TestTranslator"));

        var images = new MessagingProfileImageResolver(
            db,
            NullLogger<MessagingProfileImageResolver>.Instance);
        var service = new MessagingService(
            db,
            NullLogger<MessagingService>.Instance,
            new CommunityTextModerationService(new ConfigurationBuilder().Build()),
            images,
            new ControlledResourceAccessService(db),
            translator.Object,
            new NotificationEngine(
                db,
                images,
                new NoopNotificationRealtimePublisher(),
                new ApplePushDeliverySignal(),
                NullLogger<NotificationEngine>.Instance));
        var conversation = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent),
                client.ClientUserId,
                MessagingParticipantTypes.Client,
                InitialMessageBody: "Hello, how are you?"));

        Assert.True(conversation.Succeeded, $"{conversation.ErrorCode}: {conversation.ErrorMessage}");
        var controller = new MobileMessagingController(
            new MobileActorResolver(db, NullLogger<MobileActorResolver>.Instance),
            service,
            Mock.Of<IMessageAttachmentStorage>(),
            Mock.Of<IMessagingRealtimePublisher>(),
            images,
            new ControlledResourceAccessService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = ControllerTestHelpers.BuildUser(client.ClientUserId)
                }
            }
        };

        var result = await controller.Messages(
            conversation.Conversation!.Id,
            null,
            null,
            CancellationToken.None);

        var messages = Assert.IsAssignableFrom<IReadOnlyList<MobileMessageDto>>(
            Assert.IsType<OkObjectResult>(result).Value);
        var message = Assert.Single(messages);
        Assert.Equal("Bonjou, kijan ou ye?", message.Body);
        Assert.Equal("Hello, how are you?", message.OriginalBody);
        Assert.NotNull(message.Translation);
        Assert.Equal("en", message.Translation!.OriginalLanguage);
        Assert.Equal("ht", message.Translation.TargetLanguage);
        translator.VerifyAll();
    }

    private sealed class NoopNotificationRealtimePublisher : INotificationRealtimePublisher
    {
        public Task PublishAsync(
            MessagingActor recipient,
            NotificationRealtimeEvent notification,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
