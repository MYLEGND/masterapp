using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Domain.Entities;
using System.Threading.Tasks;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Messaging;
using Infrastructure.Moderation;
using Infrastructure.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
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
    public async Task FeedVisibility_ReusesTheActiveClientMessagingAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = Client("client-one", "First", "Client");
        var second = Client("client-two", "Second", "Client");
        db.ClientProfiles.AddRange(first, second);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var firstActor = ClientActor(first);
        var secondActor = ClientActor(second);

        var post = await service.CreatePostAsync(
            new CreateSocialPostCommand(secondActor, SocialPostContentTypes.Post, "Visible to active clients"));
        Assert.True(post.Succeeded);

        var initialFeed = await service.GetFeedAsync(firstActor);
        Assert.True(initialFeed.Succeeded);
        Assert.Contains(initialFeed.Value!.Posts, item => item.Id == post.Value!.Id);

        var follow = await service.ToggleFollowAsync(new SocialFollowCommand(
            firstActor,
            second.ClientUserId,
            MessagingParticipantTypes.Client));
        Assert.True(follow.Succeeded);

        var reaction = await service.ToggleReactionAsync(new SocialPostMutationCommand(firstActor, post.Value!.Id));
        Assert.True(reaction.Succeeded);
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
    public async Task CurrentProfilePosts_UseTheExactTypedIdentity_AndExcludeExpiredStories()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "shared-profile-user",
            AgentUpn = "shared-profile-agent@example.test",
            FullName = "Shared Profile Agent",
            IsActive = true
        };
        var client = Client("shared-profile-user", "Shared", "Profile");
        db.AgentProfiles.Add(agent);
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var agentActor = new SocialFeedActor(
            new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent),
            agent.Id,
            agent.FullName!);
        var clientActor = ClientActor(client);

        var agentPost = await service.CreatePostAsync(new CreateSocialPostCommand(
            agentActor,
            SocialPostContentTypes.Post,
            "Agent-only profile content"));
        var clientPost = await service.CreatePostAsync(new CreateSocialPostCommand(
            clientActor,
            SocialPostContentTypes.Post,
            "Client profile content"));
        db.SocialPosts.Add(new SocialPost
        {
            Id = Guid.NewGuid(),
            AuthorUserId = client.ClientUserId,
            AuthorParticipantType = MessagingParticipantTypes.Client,
            AuthorProfileId = client.Id,
            ContentType = SocialPostContentTypes.Story,
            Audience = SocialPostAudiences.AuthorizedNetwork,
            Body = "Expired client story",
            PostedUtc = DateTime.UtcNow.AddDays(-2),
            ExpiresUtc = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var profile = await service.GetCurrentProfilePostsAsync(clientActor);
        var metrics = await service.GetProfileMetricsAsync(clientActor);

        Assert.True(agentPost.Succeeded);
        Assert.True(clientPost.Succeeded);
        Assert.True(profile.Succeeded);
        Assert.True(metrics.Succeeded);
        var visiblePost = Assert.Single(profile.Value!);
        Assert.Equal(clientPost.Value!.Id, visiblePost.Id);
        Assert.Equal(MessagingParticipantTypes.Client, visiblePost.Author.ParticipantType);
        Assert.Equal(client.Id, visiblePost.Author.ProfileId);
        Assert.Equal(1, metrics.Value!.PostCount);
        Assert.Equal(0, metrics.Value.StoryCount);
    }

    [Fact]
    public async Task CurrentProfileFollowLists_ReturnEveryRelationshipAndMatchProfileCounts()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = Client("profile-owner", "Profile", "Owner");
        var firstFollow = Client("first-follow", "First", "Follow");
        var secondFollow = Client("second-follow", "Second", "Follow");
        var firstFollower = Client("first-follower", "First", "Follower");
        var secondFollower = Client("second-follower", "Second", "Follower");
        db.ClientProfiles.AddRange(owner, firstFollow, secondFollow, firstFollower, secondFollower);
        db.SocialFollows.AddRange(
            new SocialFollow
            {
                Id = Guid.NewGuid(),
                FollowerUserId = owner.ClientUserId,
                FollowerParticipantType = MessagingParticipantTypes.Client,
                FollowedUserId = firstFollow.ClientUserId,
                FollowedParticipantType = MessagingParticipantTypes.Client,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
            },
            new SocialFollow
            {
                Id = Guid.NewGuid(),
                FollowerUserId = owner.ClientUserId,
                FollowerParticipantType = MessagingParticipantTypes.Client,
                FollowedUserId = secondFollow.ClientUserId,
                FollowedParticipantType = MessagingParticipantTypes.Client,
                CreatedUtc = DateTime.UtcNow
            },
            new SocialFollow
            {
                Id = Guid.NewGuid(),
                FollowerUserId = firstFollower.ClientUserId,
                FollowerParticipantType = MessagingParticipantTypes.Client,
                FollowedUserId = owner.ClientUserId,
                FollowedParticipantType = MessagingParticipantTypes.Client,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-2)
            },
            new SocialFollow
            {
                Id = Guid.NewGuid(),
                FollowerUserId = secondFollower.ClientUserId,
                FollowerParticipantType = MessagingParticipantTypes.Client,
                FollowedUserId = owner.ClientUserId,
                FollowedParticipantType = MessagingParticipantTypes.Client,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-3)
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var actor = ClientActor(owner);

        var follows = await service.GetCurrentProfileFollowListAsync(
            actor,
            SocialFollowListKinds.Follows);
        var followers = await service.GetCurrentProfileFollowListAsync(
            actor,
            SocialFollowListKinds.Followers);
        var metrics = await service.GetProfileMetricsAsync(actor);

        Assert.True(follows.Succeeded);
        Assert.True(followers.Succeeded);
        Assert.True(metrics.Succeeded);
        var followEntries = follows.Value ?? throw new InvalidOperationException("Follows list was missing.");
        var followerEntries = followers.Value ?? throw new InvalidOperationException("Followers list was missing.");
        var profileMetrics = metrics.Value ?? throw new InvalidOperationException("Profile metrics were missing.");
        Assert.Equal(new[] { secondFollow.ClientUserId, firstFollow.ClientUserId },
            followEntries.Select(entry => entry.Profile.UserId));
        Assert.All(followEntries, entry => Assert.True(entry.FollowedByCurrentActor));
        Assert.Equal(new[] { firstFollower.ClientUserId, secondFollower.ClientUserId },
            followerEntries.Select(entry => entry.Profile.UserId));
        Assert.All(followerEntries, entry => Assert.False(entry.FollowedByCurrentActor));
        Assert.Equal(followEntries.Count, profileMetrics.FollowingCount);
        Assert.Equal(followerEntries.Count, profileMetrics.FollowerCount);
    }

    [Fact]
    public async Task ProfileMetrics_UsesMobileOnlyDetails_AndOnlyReturnsAnEmailWhenItsOwnerEnabledIt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("mobile-social-profile", "Mobile", "Profile");
        db.ClientProfiles.Add(client);
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            Id = Guid.NewGuid(),
            ProfileId = client.Id,
            ParticipantType = MessagingParticipantTypes.Client,
            Username = "mobile.profile",
            NormalizedUsername = "mobile.profile",
            Bio = "A mobile-only Legend bio.",
            Website = "https://legend.example/profile",
            Location = "Phoenix, AZ",
            PublicEmail = "shareable@example.test",
            IsEmailVisible = false,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var hidden = await service.GetProfileMetricsAsync(ClientActor(client));

        Assert.True(hidden.Succeeded);
        Assert.Equal("mobile.profile", hidden.Value!.Profile.Username);
        Assert.Equal("A mobile-only Legend bio.", hidden.Value.Profile.Bio);
        Assert.Equal("https://legend.example/profile", hidden.Value.Profile.Website);
        Assert.Equal("Phoenix, AZ", hidden.Value.Profile.Location);
        Assert.Null(hidden.Value.Profile.PublicEmail);

        var settings = await db.MobileProfileSettings.SingleAsync();
        settings.IsEmailVisible = true;
        settings.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var visible = await service.GetProfileMetricsAsync(ClientActor(client));

        Assert.True(visible.Succeeded);
        Assert.Equal("shareable@example.test", visible.Value!.Profile.PublicEmail);
        Assert.Equal(client.Email, (await db.ClientProfiles.SingleAsync()).Email);
    }

    [Fact]
    public async Task EditAndDelete_RequireTheExactOwnerAndRemoveThePostFromTheProfile()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("post-owner", "Post", "Owner");
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "post-agent",
            AgentUpn = "post-agent@example.test",
            FullName = "Post Agent",
            IsActive = true
        };
        db.ClientProfiles.Add(client);
        db.AgentProfiles.Add(agent);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agent.AgentUserId,
            AgentUpn = agent.AgentUpn,
            ClientUserId = client.ClientUserId
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var owner = ClientActor(client);
        var otherActor = new SocialFeedActor(
            new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent),
            agent.Id,
            agent.FullName!);
        var created = await service.CreatePostAsync(new CreateSocialPostCommand(
            owner,
            SocialPostContentTypes.Post,
            "Original creator update"));
        Assert.True(created.Succeeded);

        var rejectedEdit = await service.UpdatePostAsync(new UpdateSocialPostCommand(
            otherActor,
            created.Value!.Id,
            "An unauthorized edit"));
        var updated = await service.UpdatePostAsync(new UpdateSocialPostCommand(
            owner,
            created.Value.Id,
            "Updated creator update"));
        var rejectedDelete = await service.DeletePostAsync(new SocialPostMutationCommand(
            otherActor,
            created.Value.Id));
        var deleted = await service.DeletePostAsync(new SocialPostMutationCommand(
            owner,
            created.Value.Id));
        var profileAfterDelete = await service.GetCurrentProfilePostsAsync(owner);
        var stored = await db.SocialPosts.SingleAsync(post => post.Id == created.Value.Id);

        Assert.False(rejectedEdit.Succeeded);
        Assert.Equal("social_post_not_owned", rejectedEdit.ErrorCode);
        Assert.True(updated.Succeeded);
        Assert.Equal("Updated creator update", updated.Value!.Body);
        Assert.False(rejectedDelete.Succeeded);
        Assert.Equal("social_post_not_owned", rejectedDelete.ErrorCode);
        Assert.True(deleted.Succeeded);
        Assert.NotNull(stored.DeletedUtc);
        Assert.True(profileAfterDelete.Succeeded);
        Assert.Empty(profileAfterDelete.Value!);
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

        var profile = await service.GetCurrentProfilePostsAsync(ClientActor(client));
        var feed = await service.GetFeedAsync(ClientActor(client));

        Assert.True(profile.Succeeded);
        Assert.True(feed.Succeeded);
        Assert.Equal(media.Id, Assert.Single(profile.Value!).Media.Single().Id);
        Assert.Equal(media.Id, Assert.Single(feed.Value!.Posts).Media.Single().Id);

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
    public async Task ActiveStory_IsReturnedThroughTheHomeFeedWithReadableMedia()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("story-media-client", "Story", "Media");
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var storage = new InMemoryTestSocialMediaStorage();
        var service = CreateService(db, storage);
        var content = new byte[] { 10, 20, 30, 40 };
        await using var uploadStream = new MemoryStream(content);

        var created = await service.CreateMediaPostAsync(
            new CreateSocialMediaPostCommand(
                ClientActor(client),
                SocialPostContentTypes.Story,
                "A current Story",
                [new SocialMediaUpload("story.jpg", content.Length, uploadStream, "Legend Story") ]));

        Assert.True(created.Succeeded);

        var feed = await service.GetFeedAsync(ClientActor(client));

        Assert.True(feed.Succeeded);
        var story = Assert.Single(feed.Value!.Stories);
        var media = Assert.Single(story.Media);
        Assert.Equal(created.Value!.Id, story.Id);
        Assert.Equal("Image", media.MediaKind);

        var retrieved = await service.GetMediaAsync(ClientActor(client), media.Id);

        Assert.True(retrieved.Succeeded);
        await using var received = retrieved.Value!.Content;
        using var buffer = new MemoryStream();
        await received.CopyToAsync(buffer);
        Assert.Equal(content, buffer.ToArray());
    }

    [Fact]
    public async Task MediaPost_IsNotPublishedUntilSecureStorageCanReadTheStoredObject()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("unreadable-media-client", "Unreadable", "Media");
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var storage = new WriteOnlyTestSocialMediaStorage();
        var service = CreateService(db, storage);
        await using var uploadStream = new MemoryStream([1, 2, 3, 4]);

        var created = await service.CreateMediaPostAsync(
            new CreateSocialMediaPostCommand(
                ClientActor(client),
                SocialPostContentTypes.Post,
                "A media update that cannot be read back",
                [new SocialMediaUpload("unavailable.jpg", 4, uploadStream, null)]));

        Assert.False(created.Succeeded);
        Assert.Equal("SOCIAL_MEDIA_STORAGE_UNAVAILABLE", created.ErrorCode);
        Assert.Empty(db.SocialPosts);
        Assert.True(storage.DeleteWasCalled);
    }

    [Fact]
    public async Task MediaRead_ReturnsRetryableFailure_WhenAuthorizedStorageIsUnavailable()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("storage-outage-client", "Storage", "Outage");
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var service = CreateService(db, new WriteOnlyTestSocialMediaStorage());
        var post = await service.CreatePostAsync(new CreateSocialPostCommand(
            ClientActor(client),
            SocialPostContentTypes.Post,
            "A post whose protected object is temporarily unavailable"));
        Assert.True(post.Succeeded);

        var asset = new SocialPostMediaAsset
        {
            Id = Guid.NewGuid(),
            SocialPostId = post.Value!.Id,
            DisplayOrder = 0,
            MediaKind = "Image",
            StorageKey = "test/unavailable.jpg",
            MimeType = "image/jpeg",
            FileSizeBytes = 4,
            ProcessingState = "Ready"
        };
        db.SocialPostMediaAssets.Add(asset);
        await db.SaveChangesAsync();

        var retrieved = await service.GetMediaAsync(ClientActor(client), asset.Id);

        Assert.False(retrieved.Succeeded);
        Assert.Equal("social_media_storage_unavailable", retrieved.ErrorCode);
        Assert.Equal(
            "Legend media is temporarily unavailable. Please try again shortly.",
            retrieved.ErrorMessage);
    }

    [Fact]
    public async Task VideoHac_IsStoredAndReadThroughTheAuthorizedMediaPath()
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
                "A secure video Hac",
                [new SocialMediaUpload(
                    "legend-hac.mp4",
                    content.Length,
                    uploadStream,
                    "A Legend video Hac")]));

        Assert.True(created.Succeeded);
        Assert.Equal(SocialPostContentTypes.Reel, created.Value!.ContentType);
        var media = Assert.Single(created.Value.Media);
        Assert.Equal("Video", media.MediaKind);
        Assert.Equal("video/mp4", media.MimeType);
        Assert.Equal("A Legend video Hac", media.AccessibilityText);

        var retrieved = await service.GetMediaAsync(ClientActor(client), media.Id);

        Assert.True(retrieved.Succeeded);
        await using var received = retrieved.Value!.Content;
        using var buffer = new MemoryStream();
        await received.CopyToAsync(buffer);
        Assert.Equal(content, buffer.ToArray());
        Assert.Equal("video/mp4", retrieved.Value.MimeType);
    }

    [Fact]
    public async Task MediaPost_EnforcesStoryAndHacMediaRules_AndCleansRejectedUploads()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("media-rules-client", "Media", "Rules");
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var storage = new InMemoryTestSocialMediaStorage();
        var service = CreateService(db, storage);

        await using var firstStoryImage = new MemoryStream([1]);
        await using var secondStoryImage = new MemoryStream([2]);
        var invalidStory = await service.CreateMediaPostAsync(
            new CreateSocialMediaPostCommand(
                ClientActor(client),
                SocialPostContentTypes.Story,
                "An invalid multi-item story",
                [
                    new SocialMediaUpload("first.jpg", 1, firstStoryImage, null),
                    new SocialMediaUpload("second.jpg", 1, secondStoryImage, null)
                ]));

        await using var hacImage = new MemoryStream([3]);
        var invalidHac = await service.CreateMediaPostAsync(
            new CreateSocialMediaPostCommand(
                ClientActor(client),
                SocialPostContentTypes.Reel,
                "An invalid image Hac",
                [new SocialMediaUpload("hac.jpg", 1, hacImage, null)]));

        Assert.False(invalidStory.Succeeded);
        Assert.Equal("social_media_post_invalid", invalidStory.ErrorCode);
        Assert.Equal(
            "Stories require exactly one supported image or video.",
            invalidStory.ErrorMessage);
        Assert.False(invalidHac.Succeeded);
        Assert.Equal("social_media_post_invalid", invalidHac.ErrorCode);
        Assert.Equal(
            "Hacs require exactly one supported video.",
            invalidHac.ErrorMessage);
        Assert.Empty(db.SocialPosts);
        Assert.Equal(0, storage.StoredMediaCount);
    }

    [Fact]
    public async Task MediaRead_AllowsAnActiveClientWhoCanSeeThePost()
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

        Assert.True(retrieved.Succeeded);
        Assert.NotNull(retrieved.Value);
    }

    [Fact]
    public async Task EngagementMetrics_AreIdempotent_Authorized_AndVisibleOnlyToTheCreator()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var author = Client("analytics-author", "Analytics", "Author");
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "analytics-agent",
            AgentUpn = "analytics-agent@example.test",
            FullName = "Analytics Agent",
            IsActive = true
        };
        db.ClientProfiles.Add(author);
        db.AgentProfiles.Add(agent);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agent.AgentUserId,
            AgentUpn = agent.AgentUpn,
            ClientUserId = author.ClientUserId
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var authorActor = ClientActor(author);
        var agentActor = new SocialFeedActor(
            new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent),
            agent.Id,
            agent.FullName!);
        var post = await service.CreatePostAsync(new CreateSocialPostCommand(
            authorActor,
            SocialPostContentTypes.Reel,
            "A measured Legend Hac"));
        Assert.True(post.Succeeded);

        var firstView = await service.RecordViewAsync(new RecordSocialPostViewCommand(
            agentActor, post.Value!.Id, 42, 50, null));
        var secondView = await service.RecordViewAsync(new RecordSocialPostViewCommand(
            agentActor, post.Value.Id, 30, 100, null));
        Assert.True(firstView.Succeeded);
        Assert.True(secondView.Succeeded);
        Assert.Equal(1, secondView.Value!.ViewCount);
        Assert.Equal(1, secondView.Value.UniqueViewerCount);
        Assert.Equal(42, secondView.Value.AverageWatchDurationSeconds);
        Assert.Equal(100, secondView.Value.AverageWatchCompletionPercentage);

        Assert.True((await service.ToggleReactionAsync(new SocialPostMutationCommand(agentActor, post.Value.Id))).Succeeded);
        var comment = await service.AddCommentAsync(new CreateSocialCommentCommand(agentActor, post.Value.Id, "Excellent work."));
        Assert.True(comment.Succeeded);
        var reply = await service.AddCommentAsync(new CreateSocialCommentCommand(authorActor, post.Value.Id, "Thank you.", comment.Value!.Id));
        Assert.True(reply.Succeeded);
        Assert.Equal(comment.Value.Id, reply.Value!.ParentCommentId);

        Assert.True((await service.ToggleSaveAsync(new SocialPostMutationCommand(agentActor, post.Value.Id))).Value);
        Assert.True((await service.ToggleRepostAsync(new SocialPostMutationCommand(agentActor, post.Value.Id))).Value);
        Assert.True((await service.RecordShareAsync(new SocialPostMutationCommand(agentActor, post.Value.Id))).Value);
        Assert.True((await service.RecordShareAsync(new SocialPostMutationCommand(agentActor, post.Value.Id))).Value);
        Assert.True((await service.RecordProfileVisitAsync(new SocialProfileVisitCommand(
            agentActor,
            author.ClientUserId,
            MessagingParticipantTypes.Client,
            post.Value.Id))).Succeeded);
        Assert.True((await service.ToggleFollowAsync(new SocialFollowCommand(
            agentActor,
            author.ClientUserId,
            MessagingParticipantTypes.Client,
            post.Value.Id))).Value);

        var ownerInsight = await service.GetPostInsightsAsync(authorActor, post.Value.Id);
        Assert.True(ownerInsight.Succeeded);
        Assert.Equal(1, ownerInsight.Value!.Metrics.ViewCount);
        Assert.Equal(1, ownerInsight.Value.Metrics.ReactionCount);
        Assert.Equal(2, ownerInsight.Value.Metrics.CommentCount);
        Assert.Equal(1, ownerInsight.Value.Metrics.ReplyCount);
        Assert.Equal(1, ownerInsight.Value.Metrics.SaveCount);
        Assert.Equal(1, ownerInsight.Value.Metrics.RepostCount);
        Assert.Equal(1, ownerInsight.Value.Metrics.ShareCount);
        Assert.Equal(1, ownerInsight.Value.Metrics.ProfileVisitCount);
        Assert.Equal(1, ownerInsight.Value.Metrics.FollowsGenerated);

        var rejectedInsight = await service.GetPostInsightsAsync(agentActor, post.Value.Id);
        Assert.False(rejectedInsight.Succeeded);
        Assert.Equal("social_insights_forbidden", rejectedInsight.ErrorCode);

        var creator = await service.GetCreatorInsightsAsync(authorActor);
        Assert.True(creator.Succeeded);
        Assert.Equal(1, creator.Value!.TotalViews);
        Assert.Equal(1, creator.Value.TotalReach);
        Assert.Equal(1, creator.Value.ProfileVisits);
        Assert.Equal(1, creator.Value.FollowersGained);
    }

    [Fact]
    public async Task MusicAttachment_UsesProviderVerifiedMetadata_AndRejectsUnknownTracks()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("music-client", "Music", "Client");
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var catalog = new TestMusicCatalog();
        var storage = new InMemoryTestSocialMediaStorage();
        var service = CreateService(db, storage, catalog);
        await using var upload = new MemoryStream([1, 2, 3]);
        var created = await service.CreateMediaPostAsync(new CreateSocialMediaPostCommand(
            ClientActor(client),
            SocialPostContentTypes.Reel,
            "A licensed clip",
            [new SocialMediaUpload("clip.mp4", 3, upload, null)],
            new SocialMusicSelection("test-catalog", "track-1", 10, 25, 0.8m, 0.2m)));

        Assert.True(created.Succeeded);
        Assert.NotNull(created.Value!.Music);
        Assert.Equal("Verified Track", created.Value.Music!.TrackTitle);
        Assert.Equal(10, created.Value.Music.TrimStartSeconds);
        Assert.Equal(25, created.Value.Music.TrimEndSeconds);

        await using var rejectedUpload = new MemoryStream([4, 5, 6]);
        var rejected = await service.CreateMediaPostAsync(new CreateSocialMediaPostCommand(
            ClientActor(client),
            SocialPostContentTypes.Reel,
            "Unknown track",
            [new SocialMediaUpload("rejected.mp4", 3, rejectedUpload, null)],
            new SocialMusicSelection("test-catalog", "not-a-track", 0, 10, 1, 1)));
        Assert.False(rejected.Succeeded);
        Assert.Equal("social_music_unknown", rejected.ErrorCode);
    }

    [Fact]
    public async Task BothStoredIdentityForms_ForOneClient_CollapseToASingleStoryAuthor()
    {
        // A client whose Entra object ID differs from the legacy ClientUserId. Content
        // authored under either form belongs to the same person and must render as one
        // story owner, not two.
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("legacy-client-id", "Dual", "Identity");
        client.ExternalIdentityObjectId = "entra-object-id";
        db.ClientProfiles.Add(client);

        var now = DateTime.UtcNow;
        foreach (var (authorUserId, body) in new[]
                 {
                     (client.ClientUserId, "Story authored under the legacy identity"),
                     (client.ExternalIdentityObjectId!, "Story authored under the Entra identity")
                 })
        {
            db.SocialPosts.Add(new SocialPost
            {
                Id = Guid.NewGuid(),
                AuthorUserId = authorUserId,
                AuthorParticipantType = MessagingParticipantTypes.Client,
                AuthorProfileId = client.Id,
                ContentType = SocialPostContentTypes.Story,
                Audience = SocialPostAudiences.AuthorizedNetwork,
                Body = body,
                PostedUtc = now,
                ExpiresUtc = now.AddHours(24)
            });
        }

        await db.SaveChangesAsync();

        var service = CreateService(db);
        // The mobile actor resolver always presents the Entra object ID.
        var actor = new SocialFeedActor(
            new MessagingActor(client.ExternalIdentityObjectId!, MessagingParticipantTypes.Client),
            client.Id,
            "Dual Identity");

        var feed = await service.GetFeedAsync(actor);

        Assert.True(feed.Succeeded);
        Assert.Equal(2, feed.Value!.Stories.Count);

        // One logical owner: a single distinct author identity across both stories, and
        // never the "Client" placeholder that the unresolved fallback used to mint.
        var distinctAuthors = feed.Value.Stories
            .Select(story => (story.Author.UserId, story.Author.ParticipantType))
            .Distinct()
            .ToArray();
        Assert.Single(distinctAuthors);
        Assert.All(feed.Value.Stories, story => Assert.Equal("Dual Identity", story.Author.DisplayName));
        Assert.All(feed.Value.Stories, story => Assert.Equal(client.Id, story.Author.ProfileId));
    }

    [Fact]
    public async Task StoryRail_SurvivesABurstOfFeedPostsThatWouldFillACombinedPage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("burst-author", "Busy", "Poster");
        db.ClientProfiles.Add(client);

        var storyPostedUtc = DateTime.UtcNow.AddHours(-1);
        db.SocialPosts.Add(new SocialPost
        {
            Id = Guid.NewGuid(),
            AuthorUserId = client.ClientUserId,
            AuthorParticipantType = MessagingParticipantTypes.Client,
            AuthorProfileId = client.Id,
            ContentType = SocialPostContentTypes.Story,
            Audience = SocialPostAudiences.AuthorizedNetwork,
            Body = "The only story",
            PostedUtc = storyPostedUtc,
            ExpiresUtc = DateTime.UtcNow.AddHours(23)
        });

        // More recent feed items than a single combined story+feed page can hold.
        for (var index = 0; index < 120; index++)
        {
            db.SocialPosts.Add(new SocialPost
            {
                Id = Guid.NewGuid(),
                AuthorUserId = client.ClientUserId,
                AuthorParticipantType = MessagingParticipantTypes.Client,
                AuthorProfileId = client.Id,
                ContentType = SocialPostContentTypes.Post,
                Audience = SocialPostAudiences.AuthorizedNetwork,
                Body = $"Feed update {index}",
                PostedUtc = storyPostedUtc.AddMinutes(index + 1)
            });
        }

        await db.SaveChangesAsync();

        var feed = await CreateService(db).GetFeedAsync(ClientActor(client));

        Assert.True(feed.Succeeded);
        var story = Assert.Single(feed.Value!.Stories);
        Assert.Equal("The only story", story.Body);
        Assert.NotEmpty(feed.Value.Posts);
    }

    [Fact]
    public async Task FollowersAudience_HidesThePostUntilTheViewerFollowsTheAuthor()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "audience-agent",
            AgentUpn = "audience-agent@example.test",
            FullName = "Audience Agent",
            IsActive = true
        };
        var client = Client("audience-client", "Audience", "Client");
        db.AgentProfiles.Add(agent);
        db.ClientProfiles.Add(client);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agent.AgentUserId,
            AgentUpn = agent.AgentUpn,
            ClientUserId = client.ClientUserId
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var agentActor = new SocialFeedActor(
            new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent),
            agent.Id,
            agent.FullName!);
        var clientActor = ClientActor(client);

        var restricted = await service.CreatePostAsync(new CreateSocialPostCommand(
            agentActor,
            SocialPostContentTypes.Post,
            "Followers only",
            new SocialPostDetails(Audience: SocialPostAudiences.Followers)));
        Assert.True(restricted.Succeeded);
        Assert.Equal(SocialPostAudiences.Followers, restricted.Value!.Audience);

        // The client is authorized to reach the agent but does not follow them yet.
        var beforeFollow = await service.GetFeedAsync(clientActor);
        Assert.True(beforeFollow.Succeeded);
        Assert.DoesNotContain(beforeFollow.Value!.Posts, post => post.Id == restricted.Value.Id);

        var follow = await service.ToggleFollowAsync(new SocialFollowCommand(
            clientActor,
            agent.AgentUserId,
            MessagingParticipantTypes.Agent));
        Assert.True(follow.Succeeded);

        var afterFollow = await service.GetFeedAsync(clientActor);
        Assert.True(afterFollow.Succeeded);
        Assert.Contains(afterFollow.Value!.Posts, post => post.Id == restricted.Value.Id);

        // The author always sees their own restricted post.
        var authorFeed = await service.GetFeedAsync(agentActor);
        Assert.True(authorFeed.Succeeded);
        Assert.Contains(authorFeed.Value!.Posts, post => post.Id == restricted.Value.Id);
    }

    [Fact]
    public async Task PostDetails_RoundTripAndDisabledCommentsAreRejected()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("details-client", "Details", "Client");
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var actor = ClientActor(client);

        var created = await service.CreatePostAsync(new CreateSocialPostCommand(
            actor,
            SocialPostContentTypes.Post,
            "An update with details",
            new SocialPostDetails(
                Audience: SocialPostAudiences.AuthorizedNetwork,
                Location: "Nashville, TN",
                CommentsEnabled: false)));

        Assert.True(created.Succeeded);
        Assert.Equal("Nashville, TN", created.Value!.Location);
        Assert.False(created.Value.CommentsEnabled);

        var comment = await service.AddCommentAsync(new CreateSocialCommentCommand(
            actor,
            created.Value.Id,
            "This should be refused"));

        Assert.False(comment.Succeeded);
        Assert.Equal("social_comments_disabled", comment.ErrorCode);

        // The stored detail survives a feed round trip.
        var feed = await service.GetFeedAsync(actor);
        Assert.True(feed.Succeeded);
        var projected = Assert.Single(feed.Value!.Posts, post => post.Id == created.Value.Id);
        Assert.Equal("Nashville, TN", projected.Location);
        Assert.False(projected.CommentsEnabled);
    }

    [Fact]
    public async Task UnsupportedAudience_IsRejectedRatherThanSilentlyWidened()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client("audience-guard-client", "Guard", "Client");
        db.ClientProfiles.Add(client);
        await db.SaveChangesAsync();

        var created = await CreateService(db).CreatePostAsync(new CreateSocialPostCommand(
            ClientActor(client),
            SocialPostContentTypes.Post,
            "Unsupported audience",
            new SocialPostDetails(Audience: "CloseFriends")));

        Assert.False(created.Succeeded);
        Assert.Equal("social_post_invalid", created.ErrorCode);
    }

    private static SocialFeedService CreateService(
        Infrastructure.Data.MasterAppDbContext db,
        ISocialMediaStorage? mediaStorage = null,
        ISocialMusicCatalog? musicCatalog = null)
    {
        var moderation = new CommunityTextModerationService(new ConfigurationBuilder().Build());
        var images = new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance);
        var messaging = new MessagingService(db, NullLogger<MessagingService>.Instance, moderation, images);
        return new SocialFeedService(
            db,
            messaging,
            mediaStorage ?? new UnavailableTestSocialMediaStorage(),
            musicCatalog ?? new UnavailableSocialMusicCatalog(),
            new SocialDiscoveryService(db));
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

        public Task<SocialMediaReadResult> OpenReadAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SocialMediaReadResult.Missing());

        public Task DeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryTestSocialMediaStorage
        : ISocialMediaStorage
    {
        private readonly Dictionary<string, byte[]> _content = [];

        public int StoredMediaCount => _content.Count;

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

        public Task<SocialMediaReadResult> OpenReadAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _content.TryGetValue(storageKey, out var bytes)
                    ? SocialMediaReadResult.Available(new MemoryStream(bytes, writable: false))
                    : SocialMediaReadResult.Missing());

        public Task DeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            _content.Remove(storageKey);
            return Task.CompletedTask;
        }
    }

    private sealed class WriteOnlyTestSocialMediaStorage
        : ISocialMediaStorage
    {
        public bool DeleteWasCalled { get; private set; }

        public async Task<SocialMediaStorageResult> StoreAsync(
            Guid mediaAssetId,
            string originalFileName,
            long declaredSizeBytes,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            await content.CopyToAsync(Stream.Null, cancellationToken);
            return SocialMediaStorageResult.Success(
                new SocialStoredMedia(
                    originalFileName,
                    $"{mediaAssetId:N}.jpg",
                    "Image",
                    "image/jpeg",
                    declaredSizeBytes,
                    $"test/{mediaAssetId:N}.jpg"));
        }

        public Task<SocialMediaReadResult> OpenReadAsync(
            string storageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SocialMediaReadResult.Unavailable());

        public Task DeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            DeleteWasCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TestMusicCatalog : ISocialMusicCatalog
    {
        private static readonly SocialMusicTrack Track = new(
            "test-catalog",
            "track-1",
            "Verified Track",
            "Legend Artist",
            180,
            "https://music.example.test/preview/track-1");

        public Task<SocialOperationResult<IReadOnlyList<SocialMusicTrack>>> SearchAsync(
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SocialOperationResult<IReadOnlyList<SocialMusicTrack>>.Success([Track]));

        public Task<SocialOperationResult<SocialMusicTrack>> ResolveAsync(
            string providerId,
            string providerTrackId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                providerId == Track.ProviderId && providerTrackId == Track.ProviderTrackId
                    ? SocialOperationResult<SocialMusicTrack>.Success(Track)
                    : SocialOperationResult<SocialMusicTrack>.Failure("social_music_unknown", "The requested catalog track is unavailable."));
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
