using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task DeferredNotification_UsesRecipientAuthorityWithoutHoldingSend(bool failTranslation, bool fcm, bool exhaust)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var clientProfile = await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1", ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = clientProfile.Id, ParticipantType = MessagingParticipantTypes.Client,
            PreferredCommunicationLanguage = "ht"
        });
        await db.SaveChangesAsync();
        var translator = new DeferredTranslationProbe { Fail = failTranslation };
        var service = CreateService(db, translator);
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var recipient = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            sender, recipient.UserId, recipient.ParticipantType));
        var command = new SendMessagingMessageCommand(sender, opened.Conversation!.Id, "Hello.", "durable-send-1");
        var sent = await service.SendMessageAsync(command);
        Assert.True(sent.Succeeded);
        Assert.Equal(0, translator.Calls);
        Assert.Single(await db.InternalMessages.ToListAsync());
        var notification = Assert.Single(await db.MobileActivityNotifications.ToListAsync());
        Assert.Null(await service.PrepareNotificationPresentationAsync(sender, notification.Id));
        Assert.Equal(0, translator.Calls);
        var detail = await service.PrepareNotificationPresentationAsync(recipient, notification.Id);
        Assert.Equal(failTranslation ? null : "Bonjou.", detail);
        Assert.Equal(failTranslation ? "Hello." : "Bonjou.", notification.Detail);
        Assert.Equal(failTranslation ? 0 : 1, await db.MessageTranslations.CountAsync());
        // A failed translation never changes the durable send into a failed send.
        var duplicate = await service.SendMessageAsync(command);
        Assert.True(duplicate.Succeeded);
        Assert.Equal(sent.Message!.Id, duplicate.Message!.Id);
        Assert.Single(await db.InternalMessages.ToListAsync());
        using var provider = new ServiceCollection()
            .AddSingleton(db).AddSingleton<IMessagingService>(service)
            .AddSingleton<INotificationEngine>(services => new NotificationEngine(db,
                new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance),
                new NoopNotificationRealtimePublisher(), new ApplePushDeliverySignal(),
                NullLogger<NotificationEngine>.Instance, services))
            .BuildServiceProvider();
        var engine = provider.GetRequiredService<INotificationEngine>();
        // The activity path invokes the same authority without any device registration.
        var snapshot = await engine.GetSnapshotAsync(recipient, 10);
        Assert.Equal(failTranslation ? "Hello." : "Bonjou.", Assert.Single(snapshot.Notifications).Detail);
        Assert.Empty(await db.MobilePushDevices.ToListAsync());
        var device = new MobilePushDevice
        {
            UserId = recipient.UserId, ParticipantType = recipient.ParticipantType,
            Provider = fcm ? MobilePushProviders.Fcm : MobilePushProviders.Apns,
            DeviceToken = "synthetic-device", TokenHash = "synthetic-hash"
        };
        var delivery = new MobilePushDelivery { NotificationId = notification.Id, MobilePushDeviceId = device.Id };
        db.MobilePushDevices.Add(device);
        db.MobilePushDeliveries.Add(delivery);
        await db.SaveChangesAsync();
        var gateway = new DeferredPushProbe();
        using Microsoft.Extensions.Hosting.BackgroundService worker = fcm
            ? new FirebasePushDeliveryHostedService(provider.GetRequiredService<IServiceScopeFactory>(), gateway,
                NullLogger<FirebasePushDeliveryHostedService>.Instance)
            : new ApplePushDeliveryHostedService(provider.GetRequiredService<IServiceScopeFactory>(), gateway,
                new ApplePushDeliverySignal(), NullLogger<ApplePushDeliveryHostedService>.Instance);
        var pass = worker.GetType().GetMethod("DeliverDueNotificationsAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)pass.Invoke(worker, new object[] { CancellationToken.None })!;
        Assert.Equal(failTranslation ? 0 : 1, gateway.Bodies.Count);
        if (failTranslation)
        {
            Assert.Null(delivery.SentUtc);
            Assert.Null(delivery.AbandonedUtc);
            Assert.Equal(1, delivery.AttemptCount);
            Assert.Equal("notification_presentation_unavailable", delivery.LastError);
            if (exhaust)
            {
                for (var attempt = 1; attempt < 6; attempt++)
                {
                    delivery.NextAttemptUtc = DateTime.UtcNow.AddSeconds(-1);
                    await db.SaveChangesAsync();
                    await (Task)pass.Invoke(worker, new object[] { CancellationToken.None })!;
                }
                Assert.Equal(6, delivery.AttemptCount);
                Assert.NotNull(delivery.AbandonedUtc);
                Assert.Null(delivery.SentUtc);
                Assert.Equal("notification_presentation_exhausted", delivery.LastError);
                Assert.Empty(gateway.Bodies);
                translator.Fail = false;
                await (Task)pass.Invoke(worker, new object[] { CancellationToken.None })!;
                Assert.Empty(gateway.Bodies);
                return;
            }
            translator.Fail = false;
            delivery.NextAttemptUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
            await (Task)pass.Invoke(worker, new object[] { CancellationToken.None })!;
        }
        Assert.Equal("Bonjou.", Assert.Single(gateway.Bodies));
        Assert.NotNull(delivery.SentUtc);
        Assert.Single(await db.MessageTranslations.ToListAsync());
        Assert.Equal("Bonjou.", await service.PrepareNotificationPresentationAsync(recipient, notification.Id));
    }

    [Fact]
    public async Task DeferredNotification_DetectionFailureCannotUseMatchingProfilePreferencesAsBodyEvidence()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var agentProfile = await db.AgentProfiles.SingleAsync(profile => profile.AgentUserId == "agent-1");
        var clientProfile = await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1", ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.AddRange(
            new MobileProfileSettings
            {
                ProfileId = agentProfile.Id, ParticipantType = MessagingParticipantTypes.Agent,
                PreferredCommunicationLanguage = "en"
            },
            new MobileProfileSettings
            {
                ProfileId = clientProfile.Id, ParticipantType = MessagingParticipantTypes.Client,
                PreferredCommunicationLanguage = "en"
            });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FailingTranslationService());
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var recipient = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = await service.StartConversationAsync(new StartMessagingConversationCommand(
            sender, recipient.UserId, recipient.ParticipantType));
        const string body = "Mwen konfime randevou ou pou demen.";
        var sent = await service.SendMessageAsync(new SendMessagingMessageCommand(
            sender, opened.Conversation!.Id, body));
        Assert.True(sent.Succeeded);
        var source = Assert.Single(await db.InternalMessages.ToListAsync());
        Assert.Equal("en", source.SenderPreferredLanguage);
        Assert.Null(source.OriginalLanguage);
        var notification = Assert.Single(await db.MobileActivityNotifications.ToListAsync());
        // Before the fix this returned the raw Creole body as a ready English
        // presentation solely because sender and recipient preferences matched.
        Assert.Null(await service.PrepareNotificationPresentationAsync(recipient, notification.Id));
        Assert.Equal(body, notification.Detail);
        Assert.Null(source.OriginalLanguage);
        Assert.Empty(await db.MessageTranslations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstMessage_AcknowledgesOriginalBeforeRecipientTranslation(bool group)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, linkClientToAgent: true, grantClientToAgent: false);
        var client = await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == "client-1");
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = "client-1", ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
            GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
        });
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = client.Id, ParticipantType = MessagingParticipantTypes.Client,
            PreferredCommunicationLanguage = "ht"
        });
        if (group)
        {
            var secondClient = new ClientProfile
            {
                ClientUserId = "client-2", ExternalIdentityObjectId = "client-2",
                FirstName = "Client", LastName = "Two", Email = "two@example.test"
            };
            db.ClientProfiles.Add(secondClient);
            db.AgentClients.Add(new AgentClient
            {
                AgentUserId = "agent-1", AgentUpn = "agent.one@mylegnd.com", ClientUserId = "client-2"
            });
            await db.SaveChangesAsync();
            GrantClientAppAccess(db, secondClient);
        }
        await db.SaveChangesAsync();
        var translator = new DeferredTranslationProbe();
        var service = CreateService(db, translator);
        var sender = new MessagingActor("agent-1", MessagingParticipantTypes.Agent);
        var recipient = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var opened = group
            ? await service.CreateGroupAsync(new CreateMessagingGroupCommand(sender,
                [new MessagingParticipantReference("client-1", MessagingParticipantTypes.Client),
                 new MessagingParticipantReference("client-2", MessagingParticipantTypes.Client)],
                "Shared discussion", "Hello."))
            : await service.StartConversationAsync(new StartMessagingConversationCommand(sender,
                recipient.UserId, recipient.ParticipantType, InitialMessageBody: "Hello."));
        Assert.True(opened.Succeeded);
        Assert.Equal(group ? 3 : 2, opened.Conversation!.Participants.Count);
        Assert.Equal("Hello.", Assert.Single(opened.Conversation.Messages).Body);
        Assert.Equal(0, translator.Calls);
        Assert.Empty(await db.MessageTranslations.ToListAsync());
        var source = Assert.Single(await db.InternalMessages.ToListAsync());
        Assert.Equal("Hello.", source.Body);
        Assert.Null(source.OriginalLanguage);
        var notification = await db.MobileActivityNotifications.SingleAsync(item =>
            item.SourceMessageId == source.Id && item.RecipientUserId == recipient.UserId);
        Assert.Equal("Hello.", notification.Detail);
        Assert.Equal("Bonjou.", await service.PrepareNotificationPresentationAsync(recipient, notification.Id));
        Assert.Equal("Bonjou.", notification.Detail);
        Assert.Single(await db.MessageTranslations.ToListAsync());
        if (!group)
        {
            var senderProfile = await db.AgentProfiles.SingleAsync(profile => profile.AgentUserId == sender.UserId);
            db.ControlledResourceGrants.Add(new ControlledResourceGrant
            {
                UserId = sender.UserId, ParticipantType = sender.ParticipantType,
                ResourceType = ControlledResourceTypes.LanguageTranslation, IsActive = true,
                GrantedUtc = DateTime.UtcNow, GrantedByUserId = "zac-founder-oid"
            });
            db.MobileProfileSettings.Add(new MobileProfileSettings
            {
                ProfileId = senderProfile.Id, ParticipantType = sender.ParticipantType,
                PreferredCommunicationLanguage = "en"
            });
            var historical = new InternalMessage
            {
                Id = Guid.NewGuid(), ConversationId = source.ConversationId,
                SenderUserId = recipient.UserId, SenderType = recipient.ParticipantType,
                Body = "Mwen konfime randevou a.", OriginalLanguage = "ht",
                SentUtc = DateTime.UtcNow.AddMinutes(-2)
            };
            db.InternalMessages.Add(historical);
            db.MessageTranslations.Add(new MessageTranslation
            {
                Id = Guid.NewGuid(), InternalMessageId = historical.Id, TargetLanguage = "en",
                TranslatedText = "I confirm the appointment.", Provider = "test", CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            var before = await service.GetConversationAsync(sender, source.ConversationId);
            Assert.Equal("I confirm the appointment.",
                before.Conversation!.Messages.Single(item => item.Id == historical.Id).Body);
            var callsBeforeAck = translator.Calls;
            var resumed = await service.StartConversationAsync(new StartMessagingConversationCommand(sender,
                recipient.UserId, recipient.ParticipantType, InitialMessageBody: "Acknowledged."));
            Assert.True(resumed.Succeeded);
            Assert.Equal("Acknowledged.", Assert.Single(resumed.Conversation!.Messages).Body);
            Assert.True(resumed.Conversation.HasOlderMessages);
            Assert.Equal(callsBeforeAck, translator.Calls);
            var after = await service.GetConversationAsync(sender, source.ConversationId);
            Assert.Equal("I confirm the appointment.",
                after.Conversation!.Messages.Single(item => item.Id == historical.Id).Body);
        }
    }

    private sealed class DeferredPushProbe : IApplePushGateway, IFirebasePushGateway
    {
        public List<string> Bodies { get; } = new();
        public Task<ApplePushDeliveryResult> SendAsync(ApplePushDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Bodies.Add(request.Body);
            return Task.FromResult(new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Sent));
        }
        public Task<FirebasePushDeliveryResult> SendAsync(FirebasePushDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Bodies.Add(request.Body);
            return Task.FromResult(new FirebasePushDeliveryResult(FirebasePushDeliveryOutcome.Sent));
        }
    }

    private sealed class DeferredTranslationProbe : ITranslationService
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranslationDetectionResult(true, "en"));
        public Task<TranslationProviderResult> TranslateAsync(string text, string targetLanguage,
            string? sourceLanguage = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new TranslationProviderResult(!Fail, Fail ? null : "Bonjou.", "en", "test"));
        }
    }
}
