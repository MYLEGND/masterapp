using System;
using System.Collections.Generic;
using System.Linq;
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
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task RecipientMobileEndpoint_UsesFounderApprovedMemoryBeforeQuotaOrAzureTranslation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "memory-agent",
            AgentUpn = "memory-agent@example.test",
            FullName = "English Agent",
            IsActive = true
        };
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "memory-client",
            ExternalIdentityObjectId = "memory-client",
            FirstName = "Creole",
            LastName = "Client",
            Email = "memory-client@example.test",
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
            SourceId = "memory-translation-test",
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

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance"] = "0",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "1000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "100"
        }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var retained = await corpus.SubmitApprovedKnowledgeAsync(new LegendConnectKnowledgeSubmission(
            "en", "Hello, how are you?", "ht", "Bonjou, kijan ou ye?",
            "Greeting", "Everyday", null, "FounderApproved"));
        Assert.True(retained.Succeeded);

        var access = new ControlledResourceAccessService(db, configuration, registry);
        var entitlements = new TranslationEntitlementAuthority(
            db,
            access,
            configuration,
            NullLogger<TranslationEntitlementAuthority>.Instance);
        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance),
            intelligence: new LegendConnectTranslationIntelligence(db, configuration),
            entitlements: entitlements);
        var images = new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance);
        var service = new MessagingService(
            db,
            NullLogger<MessagingService>.Instance,
            new CommunityTextModerationService(configuration),
            images,
            access,
            router,
            new NotificationEngine(
                db,
                images,
                new NoopNotificationRealtimePublisher(),
                new ApplePushDeliverySignal(),
                NullLogger<NotificationEngine>.Instance),
            languages: registry,
            translationEntitlements: entitlements,
            translationSystemUsage: new TranslationSystemUsageRecorder(
                db,
                NullLogger<TranslationSystemUsageRecorder>.Instance));
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
            access)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = ControllerTestHelpers.BuildUser(client.ClientUserId)
                }
            }
        };

        var result = await controller.Messages(conversation.Conversation!.Id, null, null, CancellationToken.None);
        var message = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MobileMessageDto>>(
            Assert.IsType<OkObjectResult>(result).Value));
        Assert.Equal("Bonjou, kijan ou ye?", message.Body);
        Assert.Equal("Hello, how are you?", message.OriginalBody);
        Assert.Equal("LegendConnectTranslationMemory", message.Translation!.Provider);
        Assert.Equal(0, provider.TranslateCalls);
        Assert.Equal(1, provider.DetectionCalls);
        var usage = await db.LegendTranslationUsagePeriods.SingleAsync();
        Assert.Equal(0, usage.ConsumedCharacters);
        Assert.Equal(0, usage.ProviderOperationCount);
    }

    [Fact]
    public async Task ConsentedLiveTranslation_IsRetainedByTheExistingCorpus_AndLaterServedFromMemoryBeforeAzure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "consented-live-agent",
            AgentUpn = "consented-live-agent@example.test",
            FullName = "English Agent",
            IsActive = true
        };
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "consented-live-client",
            ExternalIdentityObjectId = "consented-live-client",
            FirstName = "Creole",
            LastName = "Client",
            Email = "consented-live-client@example.test",
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
            SourceId = "consented-live-translation-test",
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
        db.MobileProfileSettings.AddRange(
            new MobileProfileSettings
            {
                ProfileId = agent.Id,
                ParticipantType = MessagingParticipantTypes.Agent,
                AllowsConsentedTranslationLearning = true
            },
            new MobileProfileSettings
            {
                ProfileId = client.Id,
                ParticipantType = MessagingParticipantTypes.Client,
                PreferredCommunicationLanguage = "ht",
                AllowsConsentedTranslationLearning = true
            });
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance"] = "10000",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "1000"
        }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var access = new ControlledResourceAccessService(db, configuration, registry);
        var entitlements = new TranslationEntitlementAuthority(
            db,
            access,
            configuration,
            NullLogger<TranslationEntitlementAuthority>.Instance);
        var provider = new ConsentedLiveRecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance),
            intelligence: new LegendConnectTranslationIntelligence(db, configuration),
            entitlements: entitlements);
        var images = new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance);
        var publisher = new LegendTranslationLearningPublisher(
            db,
            registry,
            NullLogger<LegendTranslationLearningPublisher>.Instance);
        var service = new MessagingService(
            db,
            NullLogger<MessagingService>.Instance,
            new CommunityTextModerationService(configuration),
            images,
            access,
            router,
            new NotificationEngine(
                db,
                images,
                new NoopNotificationRealtimePublisher(),
                new ApplePushDeliverySignal(),
                NullLogger<NotificationEngine>.Instance),
            translationLearning: publisher,
            languages: registry,
            translationEntitlements: entitlements,
            translationSystemUsage: new TranslationSystemUsageRecorder(
                db,
                NullLogger<TranslationSystemUsageRecorder>.Instance));

        const string source = "Are you coming over tonight?";
        const string target = "Èske w ap vini aswè a?";
        var conversation = await service.StartConversationAsync(
            new StartMessagingConversationCommand(
                new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent),
                client.ClientUserId,
                MessagingParticipantTypes.Client,
                InitialMessageBody: source));
        Assert.True(conversation.Succeeded, $"{conversation.ErrorCode}: {conversation.ErrorMessage}");
        Assert.Equal(0, provider.TranslateCalls);

        var initialRead = await service.GetConversationAsync(
            new MessagingActor(client.ClientUserId, MessagingParticipantTypes.Client),
            conversation.Conversation!.Id);
        Assert.True(initialRead.Succeeded, initialRead.ErrorMessage);
        Assert.Equal(1, provider.TranslateCalls);

        var retainedEvent = await db.LegendTranslationLearningEvents.SingleAsync();
        Assert.Equal("Eligible", retainedEvent.EligibilityState);
        Assert.Equal("ConsentedLiveTranslation", retainedEvent.Provenance);
        Assert.Equal(source, retainedEvent.SourceText);
        Assert.Equal(target, retainedEvent.TargetText);

        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        await corpus.ProcessPendingAsync(10);
        Assert.Equal("Processed", (await db.LegendTranslationLearningEvents.SingleAsync()).ProcessingState);
        Assert.Single(await db.LegendTranslationAlignments.ToListAsync());
        Assert.Single(await db.LegendLanguageContextRelationships.ToListAsync());

        var later = await service.SendMessageAsync(new SendMessagingMessageCommand(
            new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent),
            conversation.Conversation!.Id,
            source));
        Assert.True(later.Succeeded, $"{later.ErrorCode}: {later.ErrorMessage}");
        Assert.Equal(1, provider.TranslateCalls);

        var laterRead = await service.GetConversationAsync(
            new MessagingActor(client.ClientUserId, MessagingParticipantTypes.Client),
            conversation.Conversation!.Id);
        Assert.True(laterRead.Succeeded, laterRead.ErrorMessage);
        Assert.Equal(1, provider.TranslateCalls);
        var laterTranslation = await db.MessageTranslations
            .OrderByDescending(item => item.CreatedUtc)
            .FirstAsync();
        Assert.Equal("LegendConnectTranslationMemory", laterTranslation.Provider);
        Assert.Equal(target, laterTranslation.TranslatedText);
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public int DetectionCalls { get; private set; }
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            DetectionCalls++;
            return Task.FromResult(new TranslationDetectionResult(true, "en-US"));
        }

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            return Task.FromResult(new TranslationProviderResult(
                true,
                "Unexpected provider result",
                sourceLanguage,
                ProviderName));
        }
    }

    private sealed class ConsentedLiveRecordingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en-US"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            return Task.FromResult(new TranslationProviderResult(
                true,
                "Èske w ap vini aswè a?",
                sourceLanguage,
                ProviderName));
        }
    }

    private sealed class NoopNotificationRealtimePublisher : INotificationRealtimePublisher
    {
        public Task PublishAsync(
            MessagingActor recipient,
            NotificationRealtimeEvent notification,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
