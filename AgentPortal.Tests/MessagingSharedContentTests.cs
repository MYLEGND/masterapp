using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Moderation;
using Infrastructure.Social;
using Infrastructure.Social.OpenMusic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPost_PersistsReferenceAndRechecksRecipientVisibility(bool firstMessage)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var author = await db.AgentProfiles.SingleAsync(profile => profile.AgentUserId == "agent-1");
        var client = await db.ClientProfiles.SingleAsync(profile => profile.ClientUserId == "client-1");
        var social = new SocialFeedService(db, Mock.Of<ISocialMediaStorage>(), new CuratedOpenMusicCatalog(),
            new MemoryCache(new MemoryCacheOptions()),
            new CommunityTextModerationService(new ConfigurationBuilder().Build()));
        var source = new SocialPost
        {
            Id = Guid.NewGuid(), AuthorUserId = author.AgentUserId, AuthorParticipantType = MessagingParticipantTypes.Agent,
            AuthorProfileId = author.Id, ContentType = SocialPostContentTypes.Post, Body = string.Empty,
            PublicationState = SocialPostPublicationStates.Published, PostedUtc = DateTime.UtcNow
        };
        db.SocialPosts.Add(source);
        var media = new SocialPostMediaAsset
        {
            Id = Guid.NewGuid(), SocialPostId = source.Id, DisplayOrder = 0,
            MediaKind = "Image", MimeType = "image/jpeg", ProcessingState = SocialMediaProcessingStates.Ready,
            StorageKey = "private-test-image", FileSizeBytes = 12
        };
        db.SocialPostMediaAssets.Add(media);
        await db.SaveChangesAsync();
        var messaging = CreateService(db, social: social);
        var sender = new MessagingActor(author.AgentUserId, MessagingParticipantTypes.Agent);
        var recipient = new MessagingActor(client.ClientUserId, MessagingParticipantTypes.Client);
        var started = await messaging.StartConversationAsync(new StartMessagingConversationCommand(sender,
            recipient.UserId, recipient.ParticipantType, SharedPostId: firstMessage ? source.Id : null));
        Assert.True(started.Succeeded);
        if (!firstMessage)
        {
            var sent = await messaging.SendMessageAsync(new SendMessagingMessageCommand(sender,
                started.Conversation!.Id, string.Empty, "share-key", SharedPostId: source.Id));
            Assert.True(sent.Succeeded);
            Assert.Equal(source.Id, sent.Message!.SharedContent!.SourcePostId);
            var retried = await messaging.SendMessageAsync(new SendMessagingMessageCommand(sender,
                started.Conversation.Id, string.Empty, "share-key", SharedPostId: source.Id));
            Assert.Equal(sent.Message.Id, retried.Message!.Id);
        }
        Assert.Equal(source.Id, Assert.Single(await db.InternalMessages.ToListAsync()).SharedSocialPostId);
        var read = await messaging.GetConversationAsync(recipient, started.Conversation!.Id);
        var card = Assert.Single(read.Conversation!.Messages).SharedContent!;
        Assert.Equal("available", card.Status);
        Assert.Equal(media.Id, Assert.Single(card.Media).Id);
        Assert.Equal(string.Empty, card.Body);
        Assert.Equal($"/Social/Posts/{source.Id:D}", card.Url);
        var notification = await db.MobileActivityNotifications.SingleAsync(item => item.RecipientUserId == recipient.UserId);
        Assert.Equal("Shared content", await messaging.PrepareNotificationPresentationAsync(recipient, notification.Id));
        var privacy = new MobileProfileSettings
        {
            ProfileId = author.Id, ParticipantType = MessagingParticipantTypes.Agent, IsPrivate = true
        };
        db.MobileProfileSettings.Add(privacy);
        await db.SaveChangesAsync();
        var restricted = await messaging.GetConversationAsync(recipient, started.Conversation.Id);
        Assert.Equal("unavailable", Assert.Single(restricted.Conversation!.Messages).SharedContent!.Status);
        Assert.Equal("Shared content is unavailable.",
            await messaging.PrepareNotificationPresentationAsync(recipient, notification.Id));
        Assert.DoesNotContain(author.FullName!, notification.Detail);
        Assert.Equal("available", Assert.Single((await messaging.GetConversationAsync(sender,
            started.Conversation.Id)).Conversation!.Messages).SharedContent!.Status);
        privacy.IsPrivate = false;
        await db.SaveChangesAsync();
        // Revocation is checked on the same source at each projection; the
        // persisted message does not turn source visibility into a new grant.
        source.DeletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var revoked = await messaging.GetConversationAsync(recipient, started.Conversation.Id);
        var unavailable = Assert.Single(revoked.Conversation!.Messages).SharedContent!;
        Assert.Equal("unavailable", unavailable.Status);
        Assert.Null(unavailable.Body);
        Assert.Null(unavailable.AuthorDisplayName);
        Assert.Empty(unavailable.Media);
        var invalid = await messaging.SendMessageAsync(new SendMessagingMessageCommand(sender,
            started.Conversation.Id, string.Empty, SharedPostId: source.Id));
        Assert.False(invalid.Succeeded);
        Assert.Single(await db.InternalMessages.ToListAsync());
    }
}
