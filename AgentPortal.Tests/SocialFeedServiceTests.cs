using System;
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

    private static SocialFeedService CreateService(Infrastructure.Data.MasterAppDbContext db)
    {
        var moderation = new CommunityTextModerationService(new ConfigurationBuilder().Build());
        var journeys = new JourneyCirclesService(db, moderation, NullLogger<JourneyCirclesService>.Instance);
        var images = new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance);
        var messaging = new MessagingService(db, NullLogger<MessagingService>.Instance, moderation, journeys, images);
        return new SocialFeedService(
            db,
            messaging,
            new UnavailableTestSocialMediaStorage());
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
