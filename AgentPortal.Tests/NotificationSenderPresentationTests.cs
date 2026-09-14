using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class NotificationSenderPresentationTests
{
    [Fact]
    public async Task Call_photo_capability_rechecks_call_scope_membership_and_expiry()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var actor = new MessagingActor("receiver", "Client");
        var call = new LegendCallSession { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(),
            CallerUserId = "sender", CallerType = "Agent", CalleeUserId = actor.UserId,
            CalleeType = actor.ParticipantType, ExpiresUtc = DateTime.UtcNow.AddMinutes(1) };
        db.LegendCallSessions.Add(call); await db.SaveChangesAsync();
        var identity = new MessagingParticipantIdentity("sender", "Agent", Guid.NewGuid(), "Sender", null, "S");
        var recipientIdentity = new MessagingParticipantIdentity("receiver", "Client", Guid.NewGuid(), "Receiver", null, "R");
        var recipientPhoto = new MessagingProfileImage(new byte[] { 4, 5, 6 }, "image/png");
        var photo = new MessagingProfileImage(new byte[] { 1, 2, 3 }, "image/png");
        var images = new Mock<IMessagingProfileImageResolver>();
        images.Setup(x => x.ResolveIdentitiesAsync(It.IsAny<IEnumerable<MessagingParticipantReference>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((System.Collections.Generic.IEnumerable<MessagingParticipantReference> refs, CancellationToken _) => refs.ToDictionary(r => (r.UserId, r.ParticipantType), r => r.UserId == "sender" ? identity : recipientIdentity));
        images.Setup(x => x.ResolveAsync(identity, It.IsAny<CancellationToken>())).ReturnsAsync(photo);
        images.Setup(x => x.ResolveAsync(recipientIdentity, It.IsAny<CancellationToken>())).ReturnsAsync(recipientPhoto);
        var messaging = new Mock<IMessagingService>();
        messaging.Setup(x => x.GetConversationRealtimeRecipientsAsync(actor, call.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new MessagingRealtimeRecipient(actor.UserId, actor.ParticipantType) });
        messaging.Setup(x => x.GetConversationRealtimeRecipientsAsync(new MessagingActor("sender", "Agent"), call.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new MessagingRealtimeRecipient("sender", "Agent") });
        var protection = new EphemeralDataProtectionProvider();
        using var services = new ServiceCollection().AddSingleton<IDataProtectionProvider>(protection)
            .AddSingleton(messaging.Object).BuildServiceProvider();
        var engine = new NotificationEngine(db, images.Object, Mock.Of<INotificationRealtimePublisher>(),
            new ApplePushDeliverySignal(), NullLogger<NotificationEngine>.Instance, services);
        var paths = await engine.GetCallParticipantImagePathsAsync(call.Id);
        var path = paths.Caller;
        Assert.NotNull(paths.Callee);
        var calleeToken = Uri.UnescapeDataString(paths.Callee.Split("?token=")[1]);
        Assert.Same(recipientPhoto, await engine.GetSenderImageAsync(call.Id, calleeToken));
        var legacyToken = protection.CreateProtector("Legend.NotificationSenderImage.v1").ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(new { NotificationId = call.Id, RecipientUserId = actor.UserId, RecipientType = actor.ParticipantType, IsCall = true }), TimeSpan.FromMinutes(1));
        Assert.Same(photo, await engine.GetSenderImageAsync(call.Id, legacyToken));
        Assert.NotNull(path);
        var token = Uri.UnescapeDataString(path.Split("?token=")[1]);
        Assert.Same(photo, await engine.GetSenderImageAsync(call.Id, token));
        Assert.Null(await engine.GetSenderImageAsync(Guid.NewGuid(), token));
        Assert.Null(await engine.GetSenderImageAsync(call.Id, token + "tampered"));
        call.CalleeType = "Agent"; await db.SaveChangesAsync();
        Assert.Null(await engine.GetSenderImageAsync(call.Id, token));
        call.CalleeType = "Client";
        call.CallerType = "Client"; await db.SaveChangesAsync();
        Assert.Null(await engine.GetSenderImageAsync(call.Id, calleeToken));
        call.CallerType = "Agent"; call.Status = "ended"; await db.SaveChangesAsync();
        Assert.Null(await engine.GetSenderImageAsync(call.Id, token));
        call.Status = "ringing"; call.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        Assert.Null(await engine.GetSenderImageAsync(call.Id, token));
        Assert.Null((await engine.GetCallParticipantImagePathsAsync(call.Id)).Caller);
        call.ExpiresUtc = DateTime.UtcNow.AddMinutes(1); await db.SaveChangesAsync();
        messaging.Setup(x => x.GetConversationRealtimeRecipientsAsync(actor, call.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MessagingRealtimeRecipient>());
        Assert.Null(await engine.GetSenderImageAsync(call.Id, token));
    }

    [Fact]
    public async Task Sender_photo_capability_is_scoped_expiring_and_revoked_with_membership()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var actor = new MessagingActor("receiver", "Client");
        var conversationId = Guid.NewGuid();
        var message = new InternalMessage { Id = Guid.NewGuid(), ConversationId = conversationId, SenderUserId = "sender", SenderType = "Agent" };
        var notice = new MobileActivityNotification { RecipientUserId = actor.UserId, RecipientParticipantType = actor.ParticipantType,
            SourceMessageId = message.Id, ConversationId = conversationId };
        var membership = new MessageConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversationId,
            UserId = actor.UserId, ParticipantType = actor.ParticipantType, IsActive = true };
        db.InternalMessages.Add(message); db.MobileActivityNotifications.Add(notice); db.MessageConversationParticipants.Add(membership);
        await db.SaveChangesAsync();
        var identity = new MessagingParticipantIdentity("sender", "Agent", Guid.NewGuid(), "Sender Name", null, "SN");
        var photo = new MessagingProfileImage(new byte[] { 1, 2, 3 }, "image/png");
        var images = new Mock<IMessagingProfileImageResolver>();
        images.Setup(x => x.ResolveIdentitiesAsync(It.IsAny<IEnumerable<MessagingParticipantReference>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<(string, string), MessagingParticipantIdentity> { [("sender", "Agent")] = identity });
        images.Setup(x => x.ResolveAsync(identity, It.IsAny<CancellationToken>())).ReturnsAsync(photo);
        var messaging = new Mock<IMessagingService>();
        messaging.Setup(x => x.GetConversationRealtimeRecipientsAsync(actor, conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new MessagingRealtimeRecipient(actor.UserId, actor.ParticipantType) });
        var protection = new EphemeralDataProtectionProvider();
        using var services = new ServiceCollection().AddSingleton<IDataProtectionProvider>(protection)
            .AddSingleton(messaging.Object).BuildServiceProvider();
        var engine = new NotificationEngine(db, images.Object, Mock.Of<INotificationRealtimePublisher>(),
            new ApplePushDeliverySignal(), NullLogger<NotificationEngine>.Instance, services);
        var presentation = await engine.GetSenderPresentationAsync(actor, notice.Id);
        Assert.NotNull(presentation); Assert.Equal(identity.DisplayName, presentation.Name);
        var token = Uri.UnescapeDataString(presentation.ImagePath.Split("?token=")[1]);
        Assert.Same(photo, await engine.GetSenderImageAsync(notice.Id, token));
        Assert.Null(await engine.GetSenderImageAsync(Guid.NewGuid(), token));
        Assert.Null(await engine.GetSenderImageAsync(notice.Id, token + "tampered"));
        Assert.Null(await engine.GetSenderPresentationAsync(new MessagingActor("other", "Client"), notice.Id));
        var expired = protection.CreateProtector("Legend.NotificationSenderImage.v1").ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(new { NotificationId = notice.Id, RecipientUserId = actor.UserId,
                RecipientType = actor.ParticipantType }), DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Null(await engine.GetSenderImageAsync(notice.Id, expired));
        message.IsDeleted = true; await db.SaveChangesAsync();
        Assert.Null(await engine.GetSenderImageAsync(notice.Id, token));
        message.IsDeleted = false; membership.IsActive = false; await db.SaveChangesAsync();
        Assert.Null(await engine.GetSenderImageAsync(notice.Id, token));
        membership.IsActive = true; await db.SaveChangesAsync();
        messaging.Setup(x => x.GetConversationRealtimeRecipientsAsync(actor, conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MessagingRealtimeRecipient>());
        Assert.Null(await engine.GetSenderImageAsync(notice.Id, token));
    }
}
