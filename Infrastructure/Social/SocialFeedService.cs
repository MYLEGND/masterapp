using Domain.Entities;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Social;

/// <summary>
/// Server-authoritative community feed. Visibility is derived exclusively from
/// typed profiles and existing servicing/Journey Circles relationships; a
/// follow is only a feed preference and never grants access by itself.
/// </summary>
public sealed class SocialFeedService : ISocialFeedService
{
    private const int MaximumPostLength = 2_000;
    private const int MaximumCommentLength = 800;
    private const int MaximumFeedPosts = 80;
    private const int MaximumStoryPosts = 30;
    private const int MaximumCommentsPerPost = 4;
    private const int MaximumActivityItems = 30;

    private readonly MasterAppDbContext _db;
    private readonly IMessagingService _messaging;

    public SocialFeedService(MasterAppDbContext db, IMessagingService messaging)
    {
        _db = db;
        _messaging = messaging;
    }

    public async Task<SocialOperationResult<SocialFeedSnapshot>> GetFeedAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
            return SocialOperationResult<SocialFeedSnapshot>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var visibleAuthors = await GetVisibleAuthorsAsync(actor, cancellationToken);
        var now = DateTime.UtcNow;
        var posts = (await _db.SocialPosts
                .AsNoTracking()
                .Where(post => post.DeletedUtc == null && (post.ExpiresUtc == null || post.ExpiresUtc > now))
                .OrderByDescending(post => post.PostedUtc)
                .Take(MaximumFeedPosts + MaximumStoryPosts)
                .ToListAsync(cancellationToken))
            .Where(post => visibleAuthors.Contains(AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType)))
            .ToArray();

        var stories = await BuildPostViewsAsync(
            posts.Where(post => string.Equals(post.ContentType, SocialPostContentTypes.Story, StringComparison.Ordinal)).Take(MaximumStoryPosts),
            actor,
            cancellationToken);
        var feed = await BuildPostViewsAsync(
            posts.Where(post => !string.Equals(post.ContentType, SocialPostContentTypes.Story, StringComparison.Ordinal)).Take(MaximumFeedPosts),
            actor,
            cancellationToken);
        var activity = await GetActivityAsync(actor, cancellationToken);

        return SocialOperationResult<SocialFeedSnapshot>.Success(
            new SocialFeedSnapshot(stories, feed, activity, activity.Count));
    }

    public async Task<SocialOperationResult<SocialPostView>> CreatePostAsync(
        CreateSocialPostCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<SocialPostView>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var contentType = NormalizePostType(command.ContentType);
        var body = NormalizeBody(command.Body, MaximumPostLength);
        if (contentType is null || string.IsNullOrWhiteSpace(body))
            return SocialOperationResult<SocialPostView>.Failure("social_post_invalid", "Choose a post type and add a concise update.");

        var post = new SocialPost
        {
            Id = Guid.NewGuid(),
            AuthorUserId = Normalize(command.Actor.Identity.UserId),
            AuthorParticipantType = command.Actor.Identity.ParticipantType,
            AuthorProfileId = command.Actor.ProfileId,
            ContentType = contentType,
            Audience = SocialPostAudiences.AuthorizedNetwork,
            Body = body,
            PostedUtc = DateTime.UtcNow,
            ExpiresUtc = contentType == SocialPostContentTypes.Story ? DateTime.UtcNow.AddHours(24) : null
        };

        _db.SocialPosts.Add(post);
        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<SocialPostView>.Success(await BuildPostViewAsync(post, command.Actor, cancellationToken));
    }

    public async Task<SocialOperationResult<SocialPostView>> ToggleReactionAsync(
        SocialPostMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<SocialPostView>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var post = await GetVisiblePostAsync(command.Actor, command.PostId, cancellationToken);
        if (post is null)
            return SocialOperationResult<SocialPostView>.Failure("social_post_unavailable", "This post is not available to your mobile identity.");

        var actorUserId = Normalize(command.Actor.Identity.UserId);
        var existing = await _db.SocialPostReactions.SingleOrDefaultAsync(
            reaction => reaction.SocialPostId == post.Id &&
                        reaction.ActorUserId == actorUserId &&
                        reaction.ActorParticipantType == command.Actor.Identity.ParticipantType,
            cancellationToken);
        if (existing is null)
        {
            _db.SocialPostReactions.Add(new SocialPostReaction
            {
                Id = Guid.NewGuid(),
                SocialPostId = post.Id,
                ActorUserId = actorUserId,
                ActorParticipantType = command.Actor.Identity.ParticipantType,
                ReactionType = SocialReactionTypes.Appreciate,
                CreatedUtc = DateTime.UtcNow
            });
        }
        else
        {
            _db.SocialPostReactions.Remove(existing);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<SocialPostView>.Success(await BuildPostViewAsync(post, command.Actor, cancellationToken));
    }

    public async Task<SocialOperationResult<SocialCommentView>> AddCommentAsync(
        CreateSocialCommentCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<SocialCommentView>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var post = await GetVisiblePostAsync(command.Actor, command.PostId, cancellationToken);
        var body = NormalizeBody(command.Body, MaximumCommentLength);
        if (post is null)
            return SocialOperationResult<SocialCommentView>.Failure("social_post_unavailable", "This post is not available to your mobile identity.");
        if (string.IsNullOrWhiteSpace(body))
            return SocialOperationResult<SocialCommentView>.Failure("social_comment_invalid", "Add a concise comment before sending it.");

        var comment = new SocialPostComment
        {
            Id = Guid.NewGuid(),
            SocialPostId = post.Id,
            AuthorUserId = Normalize(command.Actor.Identity.UserId),
            AuthorParticipantType = command.Actor.Identity.ParticipantType,
            AuthorProfileId = command.Actor.ProfileId,
            Body = body,
            CreatedUtc = DateTime.UtcNow
        };
        _db.SocialPostComments.Add(comment);
        await _db.SaveChangesAsync(cancellationToken);

        return SocialOperationResult<SocialCommentView>.Success(new SocialCommentView(
            comment.Id,
            ToAuthor(command.Actor),
            comment.Body,
            comment.CreatedUtc));
    }

    public async Task<SocialOperationResult<bool>> ToggleFollowAsync(
        SocialFollowCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<bool>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var followedUserId = Normalize(command.FollowedUserId);
        var followedType = NormalizeParticipantType(command.FollowedParticipantType);
        if (string.IsNullOrWhiteSpace(followedUserId) || followedType is null ||
            (followedUserId == Normalize(command.Actor.Identity.UserId) && followedType == command.Actor.Identity.ParticipantType))
        {
            return SocialOperationResult<bool>.Failure("social_follow_invalid", "Choose another authorized Legend profile to follow.");
        }

        var visibleAuthors = await GetVisibleAuthorsAsync(command.Actor, cancellationToken);
        if (!visibleAuthors.Contains(AuthorKey.From(followedUserId, followedType)))
            return SocialOperationResult<bool>.Failure("social_follow_forbidden", "You can follow only profiles already authorized for your Legend network.");

        var followerUserId = Normalize(command.Actor.Identity.UserId);
        var existing = await _db.SocialFollows.SingleOrDefaultAsync(
            follow => follow.FollowerUserId == followerUserId &&
                      follow.FollowerParticipantType == command.Actor.Identity.ParticipantType &&
                      follow.FollowedUserId == followedUserId &&
                      follow.FollowedParticipantType == followedType,
            cancellationToken);
        if (existing is null)
        {
            _db.SocialFollows.Add(new SocialFollow
            {
                Id = Guid.NewGuid(),
                FollowerUserId = followerUserId,
                FollowerParticipantType = command.Actor.Identity.ParticipantType,
                FollowedUserId = followedUserId,
                FollowedParticipantType = followedType,
                CreatedUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
            return SocialOperationResult<bool>.Success(true);
        }

        _db.SocialFollows.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<bool>.Success(false);
    }

    private async Task<SocialPost?> GetVisiblePostAsync(
        SocialFeedActor actor,
        Guid postId,
        CancellationToken cancellationToken)
    {
        if (postId == Guid.Empty)
            return null;

        var post = await _db.SocialPosts.SingleOrDefaultAsync(
            item => item.Id == postId && item.DeletedUtc == null && (item.ExpiresUtc == null || item.ExpiresUtc > DateTime.UtcNow),
            cancellationToken);
        if (post is null)
            return null;

        var visibleAuthors = await GetVisibleAuthorsAsync(actor, cancellationToken);
        return visibleAuthors.Contains(AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType)) ? post : null;
    }

    private Task<bool> IsValidActorAsync(SocialFeedActor actor, CancellationToken cancellationToken)
    {
        var identity = actor.Identity;
        var userId = Normalize(identity.UserId);
        if (actor.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(userId))
            return Task.FromResult(false);

        return identity.ParticipantType switch
        {
            MessagingParticipantTypes.Agent => _db.AgentProfiles.AsNoTracking().AnyAsync(
                profile => profile.IsActive &&
                           profile.Id == actor.ProfileId &&
                           profile.AgentUserId.ToLower() == userId,
                cancellationToken),
            MessagingParticipantTypes.Client => _db.ClientProfiles.AsNoTracking().AnyAsync(
                profile => profile.Id == actor.ProfileId &&
                           (profile.ClientUserId.ToLower() == userId ||
                            (profile.ExternalIdentityObjectId != null && profile.ExternalIdentityObjectId.ToLower() == userId)),
                cancellationToken),
            _ => Task.FromResult(false)
        };
    }

    private async Task<IReadOnlyList<SocialPostView>> BuildPostViewsAsync(
        IEnumerable<SocialPost> posts,
        SocialFeedActor actor,
        CancellationToken cancellationToken)
    {
        var materialized = posts.ToArray();
        if (materialized.Length == 0)
            return Array.Empty<SocialPostView>();

        var postIds = materialized.Select(post => post.Id).ToArray();
        var comments = await _db.SocialPostComments
            .AsNoTracking()
            .Where(comment => postIds.Contains(comment.SocialPostId) && comment.DeletedUtc == null)
            .OrderByDescending(comment => comment.CreatedUtc)
            .ToListAsync(cancellationToken);
        var reactions = await _db.SocialPostReactions
            .AsNoTracking()
            .Where(reaction => postIds.Contains(reaction.SocialPostId))
            .ToListAsync(cancellationToken);
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var follows = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow =>
                follow.FollowerUserId == actorKey.UserId &&
                follow.FollowerParticipantType == actorKey.ParticipantType)
            .ToListAsync(cancellationToken);

        var authors = await ResolveAuthorsAsync(
            materialized.Select(post => new AuthorReference(post.AuthorUserId, post.AuthorParticipantType, post.AuthorProfileId))
                .Concat(comments.Select(comment => new AuthorReference(comment.AuthorUserId, comment.AuthorParticipantType, comment.AuthorProfileId))),
            cancellationToken);
        return materialized.Select(post =>
        {
            var postComments = comments
                .Where(comment => comment.SocialPostId == post.Id)
                .OrderBy(comment => comment.CreatedUtc)
                .TakeLast(MaximumCommentsPerPost)
                .Select(comment => new SocialCommentView(
                    comment.Id,
                    authors.GetValueOrDefault(AuthorKey.From(comment.AuthorUserId, comment.AuthorParticipantType)) ?? ToUnknownAuthor(comment.AuthorUserId, comment.AuthorParticipantType, comment.AuthorProfileId),
                    comment.Body,
                    comment.CreatedUtc))
                .ToArray();
            var postReactions = reactions.Where(reaction => reaction.SocialPostId == post.Id).ToArray();
            return new SocialPostView(
                post.Id,
                authors.GetValueOrDefault(AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType)) ?? ToUnknownAuthor(post.AuthorUserId, post.AuthorParticipantType, post.AuthorProfileId),
                post.ContentType,
                post.Body,
                post.PostedUtc,
                post.ExpiresUtc,
                postReactions.Length,
                comments.Count(comment => comment.SocialPostId == post.Id),
                postReactions.Any(reaction => AuthorKey.From(reaction.ActorUserId, reaction.ActorParticipantType) == actorKey),
                follows.Any(follow =>
                    AuthorKey.From(follow.FollowedUserId, follow.FollowedParticipantType) ==
                    AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType)),
                postComments);
        }).ToArray();
    }

    private async Task<SocialPostView> BuildPostViewAsync(SocialPost post, SocialFeedActor actor, CancellationToken cancellationToken) =>
        (await BuildPostViewsAsync([post], actor, cancellationToken)).Single();

    private async Task<IReadOnlyList<SocialActivityView>> GetActivityAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken)
    {
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var ownPostIds = await _db.SocialPosts
            .AsNoTracking()
            .Where(post => post.AuthorUserId == actorKey.UserId && post.AuthorParticipantType == actorKey.ParticipantType && post.DeletedUtc == null)
            .Select(post => post.Id)
            .ToListAsync(cancellationToken);
        if (ownPostIds.Count == 0)
            return Array.Empty<SocialActivityView>();

        var reactions = await _db.SocialPostReactions
            .AsNoTracking()
            .Where(reaction => ownPostIds.Contains(reaction.SocialPostId) &&
                               (reaction.ActorUserId != actorKey.UserId || reaction.ActorParticipantType != actorKey.ParticipantType))
            .OrderByDescending(reaction => reaction.CreatedUtc)
            .Take(MaximumActivityItems)
            .ToListAsync(cancellationToken);
        var comments = await _db.SocialPostComments
            .AsNoTracking()
            .Where(comment => ownPostIds.Contains(comment.SocialPostId) && comment.DeletedUtc == null &&
                              (comment.AuthorUserId != actorKey.UserId || comment.AuthorParticipantType != actorKey.ParticipantType))
            .OrderByDescending(comment => comment.CreatedUtc)
            .Take(MaximumActivityItems)
            .ToListAsync(cancellationToken);
        var follows = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow => follow.FollowedUserId == actorKey.UserId && follow.FollowedParticipantType == actorKey.ParticipantType)
            .OrderByDescending(follow => follow.CreatedUtc)
            .Take(MaximumActivityItems)
            .ToListAsync(cancellationToken);

        var references = reactions.Select(reaction => new AuthorReference(reaction.ActorUserId, reaction.ActorParticipantType, Guid.Empty))
            .Concat(comments.Select(comment => new AuthorReference(comment.AuthorUserId, comment.AuthorParticipantType, comment.AuthorProfileId)))
            .Concat(follows.Select(follow => new AuthorReference(follow.FollowerUserId, follow.FollowerParticipantType, Guid.Empty)));
        var authors = await ResolveAuthorsAsync(references, cancellationToken);

        return reactions.Select(reaction => new SocialActivityView(
                    reaction.Id,
                    "reaction",
                    authors.GetValueOrDefault(AuthorKey.From(reaction.ActorUserId, reaction.ActorParticipantType)) ?? ToUnknownAuthor(reaction.ActorUserId, reaction.ActorParticipantType, Guid.Empty),
                    reaction.SocialPostId,
                    reaction.CreatedUtc))
            .Concat(comments.Select(comment => new SocialActivityView(
                comment.Id,
                "comment",
                authors.GetValueOrDefault(AuthorKey.From(comment.AuthorUserId, comment.AuthorParticipantType)) ?? ToUnknownAuthor(comment.AuthorUserId, comment.AuthorParticipantType, comment.AuthorProfileId),
                comment.SocialPostId,
                comment.CreatedUtc)))
            .Concat(follows.Select(follow => new SocialActivityView(
                follow.Id,
                "follow",
                authors.GetValueOrDefault(AuthorKey.From(follow.FollowerUserId, follow.FollowerParticipantType)) ?? ToUnknownAuthor(follow.FollowerUserId, follow.FollowerParticipantType, Guid.Empty),
                null,
                follow.CreatedUtc)))
            .OrderByDescending(item => item.OccurredUtc)
            .Take(MaximumActivityItems)
            .ToArray();
    }

    private async Task<HashSet<AuthorKey>> GetVisibleAuthorsAsync(SocialFeedActor actor, CancellationToken cancellationToken)
    {
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var visible = new HashSet<AuthorKey> { actorKey };

        var recipients = await _messaging.ListRecipientsAsync(actor.Identity, cancellationToken: cancellationToken);
        if (!recipients.Succeeded)
            return visible;

        foreach (var recipient in recipients.Recipients)
            visible.Add(AuthorKey.From(recipient.UserId, recipient.ParticipantType));

        return visible;
    }

    private async Task<Dictionary<AuthorKey, SocialAuthor>> ResolveAuthorsAsync(
        IEnumerable<AuthorReference> references,
        CancellationToken cancellationToken)
    {
        var distinct = references
            .Select(reference => new AuthorReference(Normalize(reference.UserId), reference.ParticipantType, reference.ProfileId))
            .Where(reference => !string.IsNullOrWhiteSpace(reference.UserId))
            .Distinct()
            .ToArray();
        var result = new Dictionary<AuthorKey, SocialAuthor>();

        var agentReferences = distinct.Where(reference => reference.ParticipantType == MessagingParticipantTypes.Agent).ToArray();
        if (agentReferences.Length > 0)
        {
            var ids = agentReferences.Select(reference => reference.UserId).Distinct().ToArray();
            var agents = await _db.AgentProfiles
                .AsNoTracking()
                .Where(profile => profile.IsActive && ids.Contains(profile.AgentUserId.ToLower()))
                .Select(profile => new { profile.Id, profile.AgentUserId, profile.FullName, profile.AgentUpn })
                .ToListAsync(cancellationToken);
            foreach (var profile in agents)
            {
                var name = FirstNonEmpty(profile.FullName, profile.AgentUpn, "Agent");
                result[AuthorKey.From(profile.AgentUserId, MessagingParticipantTypes.Agent)] = new SocialAuthor(
                    Normalize(profile.AgentUserId), MessagingParticipantTypes.Agent, profile.Id, name);
            }
        }

        var clientReferences = distinct.Where(reference => reference.ParticipantType == MessagingParticipantTypes.Client).ToArray();
        if (clientReferences.Length > 0)
        {
            var ids = clientReferences.Select(reference => reference.UserId).Distinct().ToArray();
            var clients = await _db.ClientProfiles
                .AsNoTracking()
                .Where(profile => ids.Contains(profile.ClientUserId.ToLower()) ||
                                  (profile.ExternalIdentityObjectId != null && ids.Contains(profile.ExternalIdentityObjectId.ToLower())))
                .Select(profile => new { profile.Id, profile.ClientUserId, profile.ExternalIdentityObjectId, profile.FirstName, profile.LastName, profile.Email })
                .ToListAsync(cancellationToken);
            foreach (var profile in clients)
            {
                var userId = ids.Contains(Normalize(profile.ClientUserId))
                    ? Normalize(profile.ClientUserId)
                    : Normalize(profile.ExternalIdentityObjectId);
                var name = FirstNonEmpty($"{profile.FirstName} {profile.LastName}".Trim(), profile.Email, "Client");
                result[AuthorKey.From(userId, MessagingParticipantTypes.Client)] = new SocialAuthor(
                    userId, MessagingParticipantTypes.Client, profile.Id, name);
            }
        }

        return result;
    }

    private static SocialAuthor ToAuthor(SocialFeedActor actor) => new(
        Normalize(actor.Identity.UserId),
        actor.Identity.ParticipantType,
        actor.ProfileId,
        actor.DisplayName);

    private static SocialAuthor ToUnknownAuthor(string userId, string participantType, Guid profileId) => new(
        Normalize(userId), participantType, profileId, participantType == MessagingParticipantTypes.Agent ? "Agent" : "Client");

    private static string? NormalizePostType(string? contentType) => contentType?.Trim() switch
    {
        SocialPostContentTypes.Post => SocialPostContentTypes.Post,
        SocialPostContentTypes.Story => SocialPostContentTypes.Story,
        SocialPostContentTypes.Reel => SocialPostContentTypes.Reel,
        _ => null
    };

    private static string? NormalizeParticipantType(string? participantType) => participantType?.Trim() switch
    {
        MessagingParticipantTypes.Agent => MessagingParticipantTypes.Agent,
        MessagingParticipantTypes.Client => MessagingParticipantTypes.Client,
        _ => null
    };

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string NormalizeBody(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length > maximumLength ? string.Empty : normalized;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private readonly record struct AuthorKey(string UserId, string ParticipantType)
    {
        public static AuthorKey From(string? userId, string? participantType) =>
            new(Normalize(userId), participantType?.Trim() ?? string.Empty);
    }

    private readonly record struct AuthorReference(string UserId, string ParticipantType, Guid ProfileId);
}
