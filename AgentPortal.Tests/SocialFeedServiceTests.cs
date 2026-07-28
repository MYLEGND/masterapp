using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Domain.Entities;
using System.Threading.Tasks;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.JourneyCircles;
using Infrastructure.Messaging;
using Infrastructure.Moderation;
using Infrastructure.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class SocialFeedServiceTests
{
    [Fact]
    public async Task TypedIdentities_WithTheSameUserId_RemainDistinctAcrossPostsReactionsAndActivity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agentProfile = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "shared-user",
            AgentUpn = "agent@example.test",
            FullName = "Agent Identity",
            IsActive = true
        };
        var clientProfile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "shared-user",
            ExternalIdentityObjectId = "shared-user",
            FirstName = "Client",
            LastName = "Identity",
            Email = "client@example.test",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
        };
        db.AgentProfiles.Add(agentProfile);
        db.ClientProfiles.Add(clientProfile);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agentProfile.AgentUserId,
            AgentUpn = agentProfile.AgentUpn,
            ClientUserId = clientProfile.ClientUserId
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var agent = new SocialFeedActor(
            new MessagingActor("shared-user", MessagingParticipantTypes.Agent),
            agentProfile.Id,
            agentProfile.FullName!);
        var client = new SocialFeedActor(
            new MessagingActor("shared-user", MessagingParticipantTypes.Client),
            clientProfile.Id,
            "Client Identity");

        var agentPost = await service.CreatePostAsync(new CreateSocialPostCommand(agent, SocialPostContentTypes.Post, "Agent update"));
        var clientPost = await service.CreatePostAsync(new CreateSocialPostCommand(client, SocialPostContentTypes.Post, "Client update"));

        Assert.True(agentPost.Succeeded);
        Assert.True(clientPost.Succeeded);
        Assert.Equal(MessagingParticipantTypes.Agent, agentPost.Value!.Author.ParticipantType);
        Assert.Equal(agentProfile.Id, agentPost.Value.Author.ProfileId);
        Assert.Equal(MessagingParticipantTypes.Client, clientPost.Value!.Author.ParticipantType);
        Assert.Equal(clientProfile.Id, clientPost.Value.Author.ProfileId);

        var reaction = await service.ToggleReactionAsync(new SocialPostMutationCommand(client, agentPost.Value.Id));
        Assert.True(reaction.Succeeded);

        var agentFeed = await service.GetFeedAsync(agent);
        var clientFeed = await service.GetFeedAsync(client);

        Assert.True(agentFeed.Succeeded);
        Assert.True(clientFeed.Succeeded);
        Assert.Contains(agentFeed.Value!.Posts, post =>
            post.Author.ParticipantType == MessagingParticipantTypes.Client &&
            post.Author.ProfileId == clientProfile.Id &&
            post.Body == "Client update");
        Assert.Contains(clientFeed.Value!.Posts, post =>
            post.Author.ParticipantType == MessagingParticipantTypes.Agent &&
            post.Author.ProfileId == agentProfile.Id &&
            post.Body == "Agent update");
        Assert.Equal(1, agentFeed.Value.ActivityCount);
        Assert.False(agentFeed.Value.Posts.Single(post => post.Id == agentPost.Value.Id).ReactedByCurrentActor);
        Assert.True(clientFeed.Value.Posts.Single(post => post.Id == agentPost.Value.Id).ReactedByCurrentActor);
    }

    [Fact]
    public async Task FeedVisibility_ReusesMessagingAuthorization_AndFollowCannotExpandIt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = Client("client-one", "First", "Client");
        var second = Client("client-two", "Second", "Client");
        db.ClientProfiles.AddRange(first, second);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var firstActor = ClientActor(first);
        var secondActor = ClientActor(second);

        var privatePost = await service.CreatePostAsync(
            new CreateSocialPostCommand(secondActor, SocialPostContentTypes.Post, "Not authorized for the first client"));
        Assert.True(privatePost.Succeeded);

        var initialFeed = await service.GetFeedAsync(firstActor);
        Assert.True(initialFeed.Succeeded);
        Assert.DoesNotContain(initialFeed.Value!.Posts, post => post.Id == privatePost.Value!.Id);

        var rejectedFollow = await service.ToggleFollowAsync(new SocialFollowCommand(
            firstActor,
            second.ClientUserId,
            MessagingParticipantTypes.Client));
        Assert.False(rejectedFollow.Succeeded);
        Assert.Equal("social_follow_forbidden", rejectedFollow.ErrorCode);

        var rejectedReaction = await service.ToggleReactionAsync(new SocialPostMutationCommand(firstActor, privatePost.Value!.Id));
        Assert.False(rejectedReaction.Succeeded);
        Assert.Equal("social_post_unavailable", rejectedReaction.ErrorCode);
    }

    [Fact]
    public async Task ExpiredStories_AreNotReturnedEvenToTheirAuthor()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("story-client", "Story", "Client");
        db.ClientProfiles.Add(client);
        db.SocialPosts.Add(new SocialPost
        {
            Id = Guid.NewGuid(),
            AuthorUserId = client.ClientUserId,
            AuthorParticipantType = MessagingParticipantTypes.Client,
            AuthorProfileId = client.Id,
            ContentType = SocialPostContentTypes.Story,
            Audience = SocialPostAudiences.AuthorizedNetwork,
            Body = "Expired",
            PostedUtc = DateTime.UtcNow.AddDays(-2),
            ExpiresUtc = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetFeedAsync(ClientActor(client));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Stories);
    }

    [Fact]
    public async Task ImagePost_IsStoredAndReadThroughTheAuthorizedMediaPath()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("media-client", "Media", "Client");
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var storage = new InMemoryTestSocialMediaStorage();
        var service = CreateService(db, storage);
        var content = new byte[] { 1, 2, 3, 4 };

        await using var uploadStream = new MemoryStream(content);
        var created = await service.CreateMediaPostAsync(
            new CreateSocialMediaPostCommand(
                ClientActor(client),
                SocialPostContentTypes.Post,
                "A secure image update",
                [new SocialMediaUpload(
                    "legend-photo.jpg",
                    content.Length,
                    uploadStream,
                    "A Legend client profile image")]));

        Assert.True(created.Succeeded);
        var media = Assert.Single(created.Value!.Media);
        Assert.Equal("Image", media.MediaKind);
        Assert.Equal("image/jpeg", media.MimeType);
        Assert.Equal("A Legend client profile image", media.AccessibilityText);

        var retrieved = await service.GetMediaAsync(
            ClientActor(client),
            media.Id);

        Assert.True(retrieved.Succeeded);
        await using var received = retrieved.Value!.Content;
        using var buffer = new MemoryStream();
        await received.CopyToAsync(buffer);
        Assert.Equal(content, buffer.ToArray());
        Assert.Equal("image/jpeg", retrieved.Value.MimeType);
    }

    [Fact]
    public async Task VideoReel_IsStoredAndReadThroughTheAuthorizedMediaPath()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("video-client", "Video", "Client");
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var storage = new InMemoryTestSocialMediaStorage();
        var service = CreateService(db, storage);
        var content = new byte[] { 9, 8, 7, 6 };

        await using var uploadStream = new MemoryStream(content);
        var created = await service.CreateMediaPostAsync(
            new CreateSocialMediaPostCommand(
                ClientActor(client),
                SocialPostContentTypes.Reel,
                "A secure video reel",
                [new SocialMediaUpload(
                    "legend-reel.mp4",
                    content.Length,
                    uploadStream,
                    "A Legend video reel")]));

        Assert.True(created.Succeeded);
        Assert.Equal(SocialPostContentTypes.Reel, created.Value!.ContentType);
        var media = Assert.Single(created.Value.Media);
        Assert.Equal("Video", media.MediaKind);
        Assert.Equal("video/mp4", media.MimeType);
        Assert.Equal("A Legend video reel", media.AccessibilityText);

        var retrieved = await service.GetMediaAsync(ClientActor(client), media.Id);

        Assert.True(retrieved.Succeeded);
        await using var received = retrieved.Value!.Content;
        using var buffer = new MemoryStream();
        await received.CopyToAsync(buffer);
        Assert.Equal(content, buffer.ToArray());
        Assert.Equal("video/mp4", retrieved.Value.MimeType);
    }

    [Fact]
    public async Task MediaRead_RejectsAClientWhoCannotSeeThePost()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var author = Client("media-author", "Media", "Author");
        var unrelatedClient = Client("media-unrelated", "Unrelated", "Client");
        db.ClientProfiles.AddRange(author, unrelatedClient);
        await db.SaveChangesAsync();

        var storage = new InMemoryTestSocialMediaStorage();
        var service = CreateService(db, storage);
        await using var uploadStream = new MemoryStream([5, 6, 7, 8]);
        var created = await service.CreateMediaPostAsync(
            new CreateSocialMediaPostCommand(
                ClientActor(author),
                SocialPostContentTypes.Post,
                "A private image update",
                [new SocialMediaUpload("private.jpg", 4, uploadStream, null)]));

        Assert.True(created.Succeeded);
        var mediaId = Assert.Single(created.Value!.Media).Id;

        var retrieved = await service.GetMediaAsync(
            ClientActor(unrelatedClient),
            mediaId);

        Assert.False(retrieved.Succeeded);
        Assert.Equal("social_media_unavailable", retrieved.ErrorCode);
    }

    private static SocialFeedService CreateService(
        Infrastructure.Data.MasterAppDbContext db,
        ISocialMediaStorage? mediaStorage = null)
    {
        var moderation = new CommunityTextModerationService(new ConfigurationBuilder().Build());
        var journeys = new JourneyCirclesService(db, moderation, NullLogger<JourneyCirclesService>.Instance);
        var images = new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance);
        var messaging = new MessagingService(db, NullLogger<MessagingService>.Instance, moderation, journeys, images);
        return new SocialFeedService(
            db,
            messaging,
            mediaStorage ?? new UnavailableTestSocialMediaStorage(),
            new UnavailableSocialMusicCatalog());
    }

    private sealed class UnavailableTestSocialMediaStorage
        : ISocialMediaStorage
    {
        public Task<SocialMediaStorageResult> StoreAsync(
            Guid mediaAssetId,
            string originalFileName,
            long declaredSizeBytes,
            Stream content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SocialMediaStorageResult.Failure(
                "test_media_storage_unused",
                "This text-only test fixture does not store media."));

        public Task<Stream?> OpenReadAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public Task DeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryTestSocialMediaStorage
        : ISocialMediaStorage
    {
        private readonly Dictionary<string, byte[]> _content = [];

        public async Task<SocialMediaStorageResult> StoreAsync(
            Guid mediaAssetId,
            string originalFileName,
            long declaredSizeBytes,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
            var mediaKind = extension == ".mp4" ? "Video" : "Image";
            var mimeType = extension == ".mp4" ? "video/mp4" : "image/jpeg";
            var storageKey = $"test/{mediaAssetId:N}{extension}";
            _content[storageKey] = bytes;

            return SocialMediaStorageResult.Success(
                new SocialStoredMedia(
                    originalFileName,
                    $"{mediaAssetId:N}{extension}",
                    mediaKind,
                    mimeType,
                    bytes.Length,
                    storageKey));
        }

        public Task<Stream?> OpenReadAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(
                _content.TryGetValue(storageKey, out var bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null);

        public Task DeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            _content.Remove(storageKey);
            return Task.CompletedTask;
        }
    }

    private static ClientProfile Client(string userId, string firstName, string lastName) => new()
    {
        Id = Guid.NewGuid(),
        ClientUserId = userId,
        ExternalIdentityObjectId = userId,
        FirstName = firstName,
        LastName = lastName,
        Email = $"{userId}@example.test",
        CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
    };

    private static SocialFeedActor ClientActor(ClientProfile profile) => new(
        new MessagingActor(profile.ClientUserId, MessagingParticipantTypes.Client),
        profile.Id,
        $"{profile.FirstName} {profile.LastName}");
}
