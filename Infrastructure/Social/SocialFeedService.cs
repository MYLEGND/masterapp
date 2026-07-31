using Domain.Entities;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Social;

/// <summary>
/// Server-authoritative community feed. Visibility is derived exclusively from
/// typed profiles and the shared messaging recipient authority; a follow is
/// only a feed preference and never grants access by itself.
/// </summary>
public sealed class SocialFeedService : ISocialFeedService
{
    private const int MaximumPostLength = 2_000;
    private const int MaximumCommentLength = 800;
    private const int MaximumFeedPosts = 80;
    private const int MaximumStoryPosts = 30;
    private const int MaximumProfilePosts = 120;
    private const int MaximumCommentsPerPost = 4;
    private const int MaximumActivityItems = 30;
    private const int MaximumMediaItemsPerPost = 10;
    private const int MaximumAccessibilityTextLength = 500;
    private const int MaximumLocationLength = 200;
    private const int MaximumMusicQueryLength = 120;
    private const int MaximumStoryInteractionCount = 20;
    private const int MaximumWatchDurationSeconds = 86_400;
    private const int TopInsightItemCount = 5;

    private readonly MasterAppDbContext _db;
    private readonly IMessagingService _messaging;
    private readonly ISocialMediaStorage _mediaStorage;
    private readonly ISocialMusicCatalog _musicCatalog;
    private readonly ISocialDiscoveryService _discovery;

    public SocialFeedService(
        MasterAppDbContext db,
        IMessagingService messaging,
        ISocialMediaStorage mediaStorage,
        ISocialMusicCatalog musicCatalog,
        ISocialDiscoveryService discovery)
    {
        _db = db;
        _messaging = messaging;
        _mediaStorage = mediaStorage;
        _musicCatalog = musicCatalog;
        _discovery = discovery;
    }

    public async Task<SocialOperationResult<SocialFeedSnapshot>> GetFeedAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
            return SocialOperationResult<SocialFeedSnapshot>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var visibleAuthors = await GetVisibleAuthorsAsync(actor, cancellationToken);
        var now = DateTime.UtcNow;
        var visibleAgentUserIds = visibleAuthors
            .Where(key => key.ParticipantType == MessagingParticipantTypes.Agent)
            .Select(key => key.UserId)
            .ToArray();
        var visibleClientUserIds = visibleAuthors
            .Where(key => key.ParticipantType == MessagingParticipantTypes.Client)
            .Select(key => key.UserId)
            .ToArray();
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var audience = await LoadAudienceGraphAsync(actorKey, cancellationToken);

        // Stories and feed items are paged independently. A single combined page let a
        // burst of one content type consume the whole window and starve the other, so an
        // active poster could empty the story rail even while unexpired stories existed.
        var visiblePosts = _db.SocialPosts
            .AsNoTracking()
            .Where(post => post.DeletedUtc == null && (post.ExpiresUtc == null || post.ExpiresUtc > now))
            .Where(post =>
                (post.AuthorParticipantType == MessagingParticipantTypes.Agent &&
                 visibleAgentUserIds.Contains(post.AuthorUserId.ToLower())) ||
                (post.AuthorParticipantType == MessagingParticipantTypes.Client &&
                 visibleClientUserIds.Contains(post.AuthorUserId.ToLower())))
            // Audience narrows inside the authorized network. Authors always see their own
            // posts regardless of the audience they chose.
            .Where(post =>
                post.Audience == SocialPostAudiences.AuthorizedNetwork ||
                (post.AuthorUserId == actorKey.UserId &&
                 post.AuthorParticipantType == actorKey.ParticipantType) ||
                (post.Audience == SocialPostAudiences.Followers &&
                 ((post.AuthorParticipantType == MessagingParticipantTypes.Agent &&
                   audience.FollowedAgentIds.Contains(post.AuthorUserId)) ||
                  (post.AuthorParticipantType == MessagingParticipantTypes.Client &&
                   audience.FollowedClientIds.Contains(post.AuthorUserId)))) ||
                (post.Audience == SocialPostAudiences.MutualConnections &&
                 ((post.AuthorParticipantType == MessagingParticipantTypes.Agent &&
                   audience.FollowedAgentIds.Contains(post.AuthorUserId) &&
                   audience.FollowerAgentIds.Contains(post.AuthorUserId)) ||
                  (post.AuthorParticipantType == MessagingParticipantTypes.Client &&
                   audience.FollowedClientIds.Contains(post.AuthorUserId) &&
                   audience.FollowerClientIds.Contains(post.AuthorUserId)))));

        var storyPosts = await visiblePosts
            .Where(post => post.ContentType == SocialPostContentTypes.Story)
            .OrderByDescending(post => post.PostedUtc)
            .Take(MaximumStoryPosts)
            .ToArrayAsync(cancellationToken);
        var feedPosts = await visiblePosts
            .Where(post => post.ContentType != SocialPostContentTypes.Story)
            .OrderByDescending(post => post.PostedUtc)
            .Take(MaximumFeedPosts)
            .ToArrayAsync(cancellationToken);

        var stories = await BuildPostViewsAsync(storyPosts, actor, cancellationToken);
        var feed = await BuildPostViewsAsync(feedPosts, actor, cancellationToken);
        var activity = await GetActivityAsync(actor, cancellationToken);

        var profileMetrics = await GetProfileMetricsAsync(actor, cancellationToken: cancellationToken);
        var creatorInsights = await GetCreatorInsightsAsync(actor, cancellationToken);
        if (!profileMetrics.Succeeded || profileMetrics.Value is null ||
            !creatorInsights.Succeeded || creatorInsights.Value is null)
        {
            return SocialOperationResult<SocialFeedSnapshot>.Failure(
                "social_metrics_unavailable",
                "Legend social metrics could not be loaded.");
        }

        return SocialOperationResult<SocialFeedSnapshot>.Success(
            new SocialFeedSnapshot(
                stories,
                feed,
                activity,
                activity.Count,
                profileMetrics.Value,
                creatorInsights.Value));
    }

    public async Task<SocialOperationResult<IReadOnlyList<SocialPostView>>> GetCurrentProfilePostsAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
        {
            return SocialOperationResult<IReadOnlyList<SocialPostView>>.Failure(
                "social_actor_invalid",
                "Your mobile identity is not available for Legend updates.");
        }

        var author = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var authorUserIds = await AuthorUserIdFormsAsync(author, cancellationToken);
        var now = DateTime.UtcNow;
        var posts = await _db.SocialPosts
            .AsNoTracking()
            .Where(post => authorUserIds.Contains(post.AuthorUserId) &&
                           post.AuthorParticipantType == author.ParticipantType &&
                           post.DeletedUtc == null &&
                           (post.ExpiresUtc == null || post.ExpiresUtc > now))
            .OrderByDescending(post => post.PostedUtc)
            .Take(MaximumProfilePosts)
            .ToArrayAsync(cancellationToken);

        return SocialOperationResult<IReadOnlyList<SocialPostView>>.Success(
            await BuildPostViewsAsync(posts, actor, cancellationToken));
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

        var details = command.Details ?? new SocialPostDetails();
        var audience = SocialPostAudiences.Normalize(details.Audience);
        if (audience is null)
            return SocialOperationResult<SocialPostView>.Failure("social_post_invalid", "Choose a supported audience for this update.");

        var post = new SocialPost
        {
            Id = Guid.NewGuid(),
            AuthorUserId = Normalize(command.Actor.Identity.UserId),
            AuthorParticipantType = command.Actor.Identity.ParticipantType,
            AuthorProfileId = command.Actor.ProfileId,
            ContentType = contentType,
            Audience = audience,
            Location = NormalizeOptionalText(details.Location, MaximumLocationLength),
            CommentsEnabled = details.CommentsEnabled,
            Body = body,
            PostedUtc = DateTime.UtcNow,
            ExpiresUtc = contentType == SocialPostContentTypes.Story ? DateTime.UtcNow.AddHours(24) : null
        };

        _db.SocialPosts.Add(post);
        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<SocialPostView>.Success(await BuildPostViewAsync(post, command.Actor, cancellationToken));
    }

    public async Task<SocialOperationResult<SocialPostView>> CreateMediaPostAsync(
        CreateSocialMediaPostCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_actor_invalid",
                "Your mobile identity is not available for Legend updates.");
        }

        var contentType = NormalizePostType(command.ContentType);
        var body = NormalizeBody(command.Body, MaximumPostLength);
        var uploads = command.Media?.ToArray() ?? Array.Empty<SocialMediaUpload>();

        if (contentType is null ||
            uploads.Length == 0 ||
            uploads.Length > MaximumMediaItemsPerPost ||
            ((command.Body ?? string.Empty).Trim().Length > 0 &&
             string.IsNullOrWhiteSpace(body)))
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_post_invalid",
                $"Choose a post type and attach between 1 and {MaximumMediaItemsPerPost} supported media files.");
        }

        var details = command.Details ?? new SocialPostDetails();
        var audience = SocialPostAudiences.Normalize(details.Audience);
        if (audience is null)
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_post_invalid",
                "Choose a supported audience for this update.");
        }

        var music = await ResolveMusicAsync(command.Music, cancellationToken);
        if (!music.Succeeded)
        {
            return SocialOperationResult<SocialPostView>.Failure(
                music.ErrorCode ?? "social_music_invalid",
                music.ErrorMessage ?? "The selected music could not be verified.");
        }

        var postId = Guid.NewGuid();
        var storedKeys = new List<string>(uploads.Length);
        var mediaAssets = new List<SocialPostMediaAsset>(uploads.Length);

        try
        {
            for (var displayOrder = 0; displayOrder < uploads.Length; displayOrder++)
            {
                var upload = uploads[displayOrder];
                var mediaAssetId = Guid.NewGuid();

                var storageResult = await _mediaStorage.StoreAsync(
                    mediaAssetId,
                    upload.OriginalFileName,
                    upload.DeclaredSizeBytes,
                    upload.Content,
                    cancellationToken);

                if (!storageResult.Succeeded || storageResult.Media is null)
                {
                    await DeleteStoredMediaAsync(storedKeys, CancellationToken.None);

                    return SocialOperationResult<SocialPostView>.Failure(
                        storageResult.ErrorCode ?? "social_media_storage_failed",
                        storageResult.ErrorMessage ?? "The social media file could not be stored.");
                }

                var stored = storageResult.Media;
                storedKeys.Add(stored.StorageKey);

                if (!await CanReadStoredMediaAsync(stored, cancellationToken))
                {
                    await DeleteStoredMediaAsync(storedKeys, CancellationToken.None);

                    return SocialOperationResult<SocialPostView>.Failure(
                        "SOCIAL_MEDIA_STORAGE_UNAVAILABLE",
                        "Legend could not verify the uploaded media in secure storage.");
                }

                mediaAssets.Add(new SocialPostMediaAsset
                {
                    Id = mediaAssetId,
                    SocialPostId = postId,
                    DisplayOrder = displayOrder,
                    MediaKind = stored.MediaKind,
                    StorageKey = stored.StorageKey,
                    MimeType = stored.MimeType,
                    FileSizeBytes = stored.FileSizeBytes,
                    ProcessingState = "Ready",
                    AccessibilityText = NormalizeOptionalText(
                        upload.AccessibilityText,
                        MaximumAccessibilityTextLength),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
            }

            if (!HasValidMediaForContentType(contentType, mediaAssets))
            {
                await DeleteStoredMediaAsync(storedKeys, CancellationToken.None);

                return SocialOperationResult<SocialPostView>.Failure(
                    "social_media_post_invalid",
                    MediaValidationMessage(contentType));
            }

            var now = DateTime.UtcNow;
            var post = new SocialPost
            {
                Id = postId,
                AuthorUserId = Normalize(command.Actor.Identity.UserId),
                AuthorParticipantType = command.Actor.Identity.ParticipantType,
                AuthorProfileId = command.Actor.ProfileId,
                ContentType = contentType,
                Audience = audience,
                Location = NormalizeOptionalText(details.Location, MaximumLocationLength),
                CommentsEnabled = details.CommentsEnabled,
                Body = body,
                PostedUtc = now,
                ExpiresUtc = contentType == SocialPostContentTypes.Story
                    ? now.AddHours(24)
                    : null,
                MediaAssets = mediaAssets,
                MusicAttachment = music.Value
            };

            _db.SocialPosts.Add(post);
            await _db.SaveChangesAsync(cancellationToken);

            return SocialOperationResult<SocialPostView>.Success(
                await BuildPostViewAsync(
                    post,
                    command.Actor,
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            await DeleteStoredMediaAsync(storedKeys, CancellationToken.None);
            throw;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            await DeleteStoredMediaAsync(storedKeys, CancellationToken.None);

            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_persistence_failed",
                "The media was uploaded, but the social post could not be saved.");
        }
    }

    public async Task<SocialOperationResult<SocialPostView>> UpdatePostAsync(
        UpdateSocialPostCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_actor_invalid",
                "Your mobile identity is not available for Legend updates.");
        }

        var author = AuthorKey.From(command.Actor.Identity.UserId, command.Actor.Identity.ParticipantType);
        var post = await _db.SocialPosts.SingleOrDefaultAsync(
            item => item.Id == command.PostId &&
                    item.DeletedUtc == null &&
                    item.AuthorUserId == author.UserId &&
                    item.AuthorParticipantType == author.ParticipantType,
            cancellationToken);
        if (post is null)
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_post_not_owned",
                "Only the creator can edit this update.");
        }

        var body = NormalizeBody(command.Body, MaximumPostLength);
        var hasMedia = await _db.SocialPostMediaAssets
            .AsNoTracking()
            .AnyAsync(item => item.SocialPostId == post.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(body) && !hasMedia)
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_post_edit_invalid",
                "Add a concise update before saving.");
        }

        post.Body = body;
        await _db.SaveChangesAsync(cancellationToken);

        return SocialOperationResult<SocialPostView>.Success(
            await BuildPostViewAsync(post, command.Actor, cancellationToken));
    }

    public async Task<SocialOperationResult<bool>> DeletePostAsync(
        SocialPostMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
        {
            return SocialOperationResult<bool>.Failure(
                "social_actor_invalid",
                "Your mobile identity is not available for Legend updates.");
        }

        var author = AuthorKey.From(command.Actor.Identity.UserId, command.Actor.Identity.ParticipantType);
        var post = await _db.SocialPosts.SingleOrDefaultAsync(
            item => item.Id == command.PostId &&
                    item.DeletedUtc == null &&
                    item.AuthorUserId == author.UserId &&
                    item.AuthorParticipantType == author.ParticipantType,
            cancellationToken);
        if (post is null)
        {
            return SocialOperationResult<bool>.Failure(
                "social_post_not_owned",
                "Only the creator can delete this update.");
        }

        post.DeletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<bool>.Success(true);
    }

    public async Task<SocialOperationResult<SocialMediaStream>> GetMediaAsync(
        SocialFeedActor actor,
        Guid mediaAssetId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
        {
            return SocialOperationResult<SocialMediaStream>.Failure(
                "social_actor_invalid",
                "Your mobile identity is not available for Legend updates.");
        }

        if (mediaAssetId == Guid.Empty)
        {
            return SocialOperationResult<SocialMediaStream>.Failure(
                "social_media_unavailable",
                "This media is not available to your mobile identity.");
        }

        var media = await _db.SocialPostMediaAssets
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == mediaAssetId,
                cancellationToken);

        if (media is null)
        {
            return SocialOperationResult<SocialMediaStream>.Failure(
                "social_media_unavailable",
                "This media is not available to your mobile identity.");
        }

        var visiblePost = await GetVisiblePostAsync(
            actor,
            media.SocialPostId,
            cancellationToken);

        if (visiblePost is null ||
            !string.Equals(
                media.ProcessingState,
                "Ready",
                StringComparison.OrdinalIgnoreCase))
        {
            return SocialOperationResult<SocialMediaStream>.Failure(
                "social_media_unavailable",
                "This media is not available to your mobile identity.");
        }

        var storedMedia = await _mediaStorage.OpenReadAsync(
            media.StorageKey,
            cancellationToken);

        if (storedMedia.Status == SocialMediaReadStatus.Unavailable)
        {
            return SocialOperationResult<SocialMediaStream>.Failure(
                "social_media_storage_unavailable",
                "Legend media is temporarily unavailable. Please try again shortly.");
        }

        if (storedMedia.Status != SocialMediaReadStatus.Available ||
            storedMedia.Content is null)
        {
            return SocialOperationResult<SocialMediaStream>.Failure(
                "social_media_unavailable",
                "This media is not available to your mobile identity.");
        }

        return SocialOperationResult<SocialMediaStream>.Success(
            new SocialMediaStream(
                storedMedia.Content,
                media.MimeType));
    }

    private async Task<bool> CanReadStoredMediaAsync(
        SocialStoredMedia stored,
        CancellationToken cancellationToken)
    {
        var storedMedia = await _mediaStorage.OpenReadAsync(
            stored.StorageKey,
            cancellationToken);

        if (storedMedia.Status != SocialMediaReadStatus.Available ||
            storedMedia.Content is null)
            return false;

        await using (storedMedia.Content)
        {
            var firstByte = new byte[1];
            return await storedMedia.Content.ReadAsync(firstByte.AsMemory(), cancellationToken) == 1;
        }
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
        if (!post.CommentsEnabled)
        {
            return SocialOperationResult<SocialCommentView>.Failure(
                "social_comments_disabled",
                "The author turned off comments for this update.");
        }

        if (command.ParentCommentId is { } parentCommentId)
        {
            var parent = await _db.SocialPostComments
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    comment => comment.Id == parentCommentId &&
                               comment.SocialPostId == post.Id &&
                               comment.DeletedUtc == null,
                    cancellationToken);
            if (parent is null)
            {
                return SocialOperationResult<SocialCommentView>.Failure(
                    "social_comment_parent_unavailable",
                    "Reply only to a visible comment on this update.");
            }
        }

        var comment = new SocialPostComment
        {
            Id = Guid.NewGuid(),
            SocialPostId = post.Id,
            ParentCommentId = command.ParentCommentId,
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
            comment.ParentCommentId,
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
            return SocialOperationResult<bool>.Failure("social_follow_invalid", "Choose another profile in your Legend network to follow.");
        }

        // A follow target must be reachable either through the messaging recipient
        // authority or through the caller's Discover scope. Without the second path a
        // member found in Discover could be opened but never followed.
        var visibleAuthors = await GetVisibleAuthorsAsync(command.Actor, cancellationToken);
        if (!visibleAuthors.Contains(AuthorKey.From(followedUserId, followedType)) &&
            !await _discovery.IsDiscoverableByAsync(command.Actor, followedUserId, followedType, cancellationToken))
        {
            return SocialOperationResult<bool>.Failure("social_follow_forbidden", "You can follow only profiles available in your Legend network.");
        }

        var followerUserId = Normalize(command.Actor.Identity.UserId);
        var sourcePostId = command.SourcePostId;
        if (sourcePostId is { } candidateSourcePostId)
        {
            var sourcePost = await GetVisiblePostAsync(command.Actor, candidateSourcePostId, cancellationToken);
            if (sourcePost is null ||
                AuthorKey.From(sourcePost.AuthorUserId, sourcePost.AuthorParticipantType) !=
                AuthorKey.From(followedUserId, followedType))
            {
                return SocialOperationResult<bool>.Failure(
                    "social_follow_source_invalid",
                    "This follow must be attributed to a visible Legend post by that profile.");
            }
        }
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
                SourceSocialPostId = sourcePostId,
                CreatedUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
            return SocialOperationResult<bool>.Success(true);
        }

        _db.SocialFollows.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<bool>.Success(false);
    }

    public async Task<SocialOperationResult<IReadOnlyList<SocialFollowListEntry>>> GetCurrentProfileFollowListAsync(
        SocialFeedActor actor,
        string listKind,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
        {
            return SocialOperationResult<IReadOnlyList<SocialFollowListEntry>>.Failure(
                "social_actor_invalid",
                "Your mobile identity is not available for Legend updates.");
        }

        var normalizedKind = SocialFollowListKinds.Normalize(listKind);
        if (normalizedKind is null)
        {
            return SocialOperationResult<IReadOnlyList<SocialFollowListEntry>>.Failure(
                "social_follow_list_invalid",
                "Choose either the Follows or Followers list.");
        }

        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var entries = await GetFollowListAsync(actorKey, actorKey, normalizedKind, cancellationToken);
        return SocialOperationResult<IReadOnlyList<SocialFollowListEntry>>.Success(entries);
    }

    public async Task<SocialOperationResult<bool>> ToggleSaveAsync(
        SocialPostMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<bool>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var post = await GetVisiblePostAsync(command.Actor, command.PostId, cancellationToken);
        if (post is null)
            return SocialOperationResult<bool>.Failure("social_post_unavailable", "This post is not available to your mobile identity.");

        var actorKey = AuthorKey.From(command.Actor.Identity.UserId, command.Actor.Identity.ParticipantType);
        var existing = await _db.SocialPostSaves.SingleOrDefaultAsync(save =>
            save.SocialPostId == post.Id &&
            save.ActorUserId == actorKey.UserId &&
            save.ActorParticipantType == actorKey.ParticipantType,
            cancellationToken);
        if (existing is null)
        {
            _db.SocialPostSaves.Add(new SocialPostSave
            {
                Id = Guid.NewGuid(),
                SocialPostId = post.Id,
                ActorUserId = actorKey.UserId,
                ActorParticipantType = actorKey.ParticipantType,
                CreatedUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
            return SocialOperationResult<bool>.Success(true);
        }

        _db.SocialPostSaves.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<bool>.Success(false);
    }

    public async Task<SocialOperationResult<bool>> ToggleRepostAsync(
        SocialPostMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<bool>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var post = await GetVisiblePostAsync(command.Actor, command.PostId, cancellationToken);
        if (post is null)
            return SocialOperationResult<bool>.Failure("social_post_unavailable", "This post is not available to your mobile identity.");

        var actorKey = AuthorKey.From(command.Actor.Identity.UserId, command.Actor.Identity.ParticipantType);
        var existing = await _db.SocialPostReposts.SingleOrDefaultAsync(repost =>
            repost.SocialPostId == post.Id &&
            repost.ActorUserId == actorKey.UserId &&
            repost.ActorParticipantType == actorKey.ParticipantType,
            cancellationToken);
        if (existing is null)
        {
            _db.SocialPostReposts.Add(new SocialPostRepost
            {
                Id = Guid.NewGuid(),
                SocialPostId = post.Id,
                ActorUserId = actorKey.UserId,
                ActorParticipantType = actorKey.ParticipantType,
                CreatedUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
            return SocialOperationResult<bool>.Success(true);
        }

        _db.SocialPostReposts.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<bool>.Success(false);
    }

    public async Task<SocialOperationResult<bool>> RecordShareAsync(
        SocialPostMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<bool>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var post = await GetVisiblePostAsync(command.Actor, command.PostId, cancellationToken);
        if (post is null)
            return SocialOperationResult<bool>.Failure("social_post_unavailable", "This post is not available to your mobile identity.");

        var actorKey = AuthorKey.From(command.Actor.Identity.UserId, command.Actor.Identity.ParticipantType);
        var existing = await _db.SocialPostShares.SingleOrDefaultAsync(share =>
            share.SocialPostId == post.Id &&
            share.ActorUserId == actorKey.UserId &&
            share.ActorParticipantType == actorKey.ParticipantType,
            cancellationToken);
        if (existing is not null)
            return SocialOperationResult<bool>.Success(true);

        _db.SocialPostShares.Add(new SocialPostShare
        {
            Id = Guid.NewGuid(),
            SocialPostId = post.Id,
            ActorUserId = actorKey.UserId,
            ActorParticipantType = actorKey.ParticipantType,
            CreatedUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<bool>.Success(true);
    }

    public async Task<SocialOperationResult<SocialPostMetrics>> RecordViewAsync(
        RecordSocialPostViewCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<SocialPostMetrics>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var post = await GetVisiblePostAsync(command.Actor, command.PostId, cancellationToken);
        if (post is null)
            return SocialOperationResult<SocialPostMetrics>.Failure("social_post_unavailable", "This post is not available to your mobile identity.");

        if (!IsValidWatchMeasurement(command.WatchDurationSeconds, command.WatchCompletionPercentage) ||
            !IsValidStoryInteraction(post.ContentType, command.StoryInteractionType))
        {
            return SocialOperationResult<SocialPostMetrics>.Failure(
                "social_view_invalid",
                "The engagement measurement is not valid for this content.");
        }

        var actorKey = AuthorKey.From(command.Actor.Identity.UserId, command.Actor.Identity.ParticipantType);
        var view = await _db.SocialPostViews.SingleOrDefaultAsync(item =>
            item.SocialPostId == post.Id &&
            item.ViewerUserId == actorKey.UserId &&
            item.ViewerParticipantType == actorKey.ParticipantType,
            cancellationToken);
        var now = DateTime.UtcNow;
        if (view is null)
        {
            view = new SocialPostViewer
            {
                Id = Guid.NewGuid(),
                SocialPostId = post.Id,
                ViewerUserId = actorKey.UserId,
                ViewerParticipantType = actorKey.ParticipantType,
                FirstViewedUtc = now,
                LastViewedUtc = now,
                MaximumWatchDurationSeconds = command.WatchDurationSeconds,
                MaximumWatchCompletionPercentage = command.WatchCompletionPercentage
            };
            ApplyStoryInteraction(view, command.StoryInteractionType);
            _db.SocialPostViews.Add(view);
        }
        else
        {
            view.LastViewedUtc = now;
            view.MaximumWatchDurationSeconds = Maximum(view.MaximumWatchDurationSeconds, command.WatchDurationSeconds);
            view.MaximumWatchCompletionPercentage = Maximum(view.MaximumWatchCompletionPercentage, command.WatchCompletionPercentage);
            ApplyStoryInteraction(view, command.StoryInteractionType);
        }

        await _db.SaveChangesAsync(cancellationToken);
        var metrics = await LoadPostMetricsAsync([post.Id], cancellationToken);
        return SocialOperationResult<SocialPostMetrics>.Success(metrics[post.Id]);
    }

    public async Task<SocialOperationResult<SocialProfileMetrics>> GetProfileMetricsAsync(
        SocialFeedActor actor,
        SocialAuthor? profile = null,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
            return SocialOperationResult<SocialProfileMetrics>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var requested = profile ?? ToAuthor(actor);
        var targetKey = AuthorKey.From(requested.UserId, requested.ParticipantType);
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        if (targetKey != actorKey)
        {
            var visible = await GetVisibleAuthorsAsync(actor, cancellationToken);
            if (!visible.Contains(targetKey) &&
                !await HasDirectFollowRelationshipAsync(actorKey, targetKey, cancellationToken))
            {
                return SocialOperationResult<SocialProfileMetrics>.Failure(
                    "social_profile_forbidden",
                    "This Legend profile is not available to your mobile identity.");
            }
        }

        var resolved = await ResolveAuthorsAsync([new AuthorReference(targetKey.UserId, targetKey.ParticipantType, requested.ProfileId)], cancellationToken);
        var author = resolved.GetValueOrDefault(targetKey);
        if (author is null)
        {
            return SocialOperationResult<SocialProfileMetrics>.Failure(
                "social_profile_unavailable",
                "This Legend profile is not available.");
        }

        var targetUserIds = await AuthorUserIdFormsAsync(targetKey, cancellationToken);
        var now = DateTime.UtcNow;
        var posts = await _db.SocialPosts.AsNoTracking()
            .Where(post => targetUserIds.Contains(post.AuthorUserId) &&
                           post.AuthorParticipantType == targetKey.ParticipantType &&
                           post.DeletedUtc == null &&
                           (post.ExpiresUtc == null || post.ExpiresUtc > now))
            .Select(post => new { post.Id, post.ContentType })
            .ToArrayAsync(cancellationToken);
        var metrics = await LoadPostMetricsAsync(posts.Select(post => post.Id).ToArray(), cancellationToken);
        // Count the exact lists we return to the native profile. This keeps Hacs,
        // Follows, and Followers coherent even when a client has legacy and Entra
        // identity forms stored in historical relationship rows.
        var followers = await GetFollowListAsync(targetKey, actorKey, SocialFollowListKinds.Followers, cancellationToken);
        var follows = await GetFollowListAsync(targetKey, actorKey, SocialFollowListKinds.Follows, cancellationToken);
        var followerCount = followers.Count;
        var followingCount = follows.Count;
        int? privateVisits = targetKey == actorKey
            ? await _db.SocialProfileVisits.AsNoTracking().CountAsync(visit =>
                visit.TargetUserId == targetKey.UserId &&
                visit.TargetParticipantType == targetKey.ParticipantType,
                cancellationToken)
            : null;

        return SocialOperationResult<SocialProfileMetrics>.Success(new SocialProfileMetrics(
            author,
            posts.Count(post => post.ContentType == SocialPostContentTypes.Post),
            posts.Count(post => post.ContentType == SocialPostContentTypes.Reel),
            posts.Count(post => post.ContentType == SocialPostContentTypes.Story),
            followerCount,
            followingCount,
            metrics.Values.Sum(metric => metric.ReactionCount),
            metrics.Values.Sum(metric => metric.ViewCount),
            metrics.Values.Sum(metric => metric.UniqueViewerCount),
            privateVisits));
    }

    public async Task<SocialOperationResult<SocialCreatorInsights>> GetCreatorInsightsAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
            return SocialOperationResult<SocialCreatorInsights>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var actorUserIds = await AuthorUserIdFormsAsync(actorKey, cancellationToken);
        var posts = await _db.SocialPosts.AsNoTracking()
            .Where(post => actorUserIds.Contains(post.AuthorUserId) &&
                           post.AuthorParticipantType == actorKey.ParticipantType &&
                           post.DeletedUtc == null)
            .OrderByDescending(post => post.PostedUtc)
            .Select(post => new { post.Id, post.ContentType, post.PostedUtc })
            .ToArrayAsync(cancellationToken);
        var metrics = await LoadPostMetricsAsync(posts.Select(post => post.Id).ToArray(), cancellationToken);
        var followers = await _db.SocialFollows.AsNoTracking().Where(follow =>
            follow.FollowedUserId == actorKey.UserId &&
            follow.FollowedParticipantType == actorKey.ParticipantType).ToArrayAsync(cancellationToken);
        var followingCount = await _db.SocialFollows.AsNoTracking().CountAsync(follow =>
            follow.FollowerUserId == actorKey.UserId &&
            follow.FollowerParticipantType == actorKey.ParticipantType,
            cancellationToken);
        var profileVisits = await _db.SocialProfileVisits.AsNoTracking().CountAsync(visit =>
            visit.TargetUserId == actorKey.UserId &&
            visit.TargetParticipantType == actorKey.ParticipantType,
            cancellationToken);
        var weeklyStart = DateTime.UtcNow.AddDays(-7);
        var rows = posts.Select(post => ToInsight(post.Id, post.ContentType, post.PostedUtc, metrics[post.Id])).ToArray();
        var totalViews = rows.Sum(row => row.Metrics.ViewCount);
        var totalReach = rows.Sum(row => row.Metrics.UniqueViewerCount);
        var totalInteractions = rows.Sum(row => row.Metrics.ReactionCount + row.Metrics.CommentCount + row.Metrics.RepostCount + row.Metrics.ShareCount + row.Metrics.SaveCount);

        return SocialOperationResult<SocialCreatorInsights>.Success(new SocialCreatorInsights(
            DateTime.UtcNow,
            totalViews,
            totalReach,
            followers.Length,
            followingCount,
            followers.Count(follow => follow.CreatedUtc >= weeklyStart),
            profileVisits,
            rows.Sum(row => row.Metrics.ReactionCount),
            rows.Sum(row => row.Metrics.CommentCount),
            rows.Sum(row => row.Metrics.ReplyCount),
            rows.Sum(row => row.Metrics.ShareCount),
            rows.Sum(row => row.Metrics.RepostCount),
            rows.Sum(row => row.Metrics.SaveCount),
            Percentage(totalInteractions, totalReach),
            rows.Where(row => row.ContentType == SocialPostContentTypes.Post).OrderByDescending(InsightScore).Take(TopInsightItemCount).ToArray(),
            rows.Where(row => row.ContentType == SocialPostContentTypes.Reel).OrderByDescending(InsightScore).Take(TopInsightItemCount).ToArray(),
            rows.Where(row => row.ContentType == SocialPostContentTypes.Story).OrderByDescending(InsightScore).Take(TopInsightItemCount).ToArray()));
    }

    public async Task<SocialOperationResult<SocialPostInsight>> GetPostInsightsAsync(
        SocialFeedActor actor,
        Guid postId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
            return SocialOperationResult<SocialPostInsight>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var post = await _db.SocialPosts.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == postId && item.DeletedUtc == null &&
            item.AuthorUserId == actorKey.UserId &&
            item.AuthorParticipantType == actorKey.ParticipantType,
            cancellationToken);
        if (post is null)
            return SocialOperationResult<SocialPostInsight>.Failure("social_insights_forbidden", "Only the creator can view this post's insights.");

        var metrics = await LoadPostMetricsAsync([post.Id], cancellationToken);
        return SocialOperationResult<SocialPostInsight>.Success(ToInsight(post.Id, post.ContentType, post.PostedUtc, metrics[post.Id]));
    }

    public async Task<SocialOperationResult<bool>> RecordProfileVisitAsync(
        SocialProfileVisitCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<bool>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var targetKey = AuthorKey.From(command.TargetUserId, command.TargetParticipantType);
        var actorKey = AuthorKey.From(command.Actor.Identity.UserId, command.Actor.Identity.ParticipantType);
        if (targetKey == actorKey)
            return SocialOperationResult<bool>.Failure("social_profile_visit_invalid", "Profile visits must target another profile in your Legend network.");

        var visible = await GetVisibleAuthorsAsync(command.Actor, cancellationToken);
        if (!visible.Contains(targetKey) &&
            !await HasDirectFollowRelationshipAsync(actorKey, targetKey, cancellationToken))
            return SocialOperationResult<bool>.Failure("social_profile_forbidden", "This Legend profile is not available to your mobile identity.");

        var sourcePostId = command.SourcePostId.GetValueOrDefault();
        if (sourcePostId != Guid.Empty)
        {
            var sourcePost = await GetVisiblePostAsync(command.Actor, sourcePostId, cancellationToken);
            if (sourcePost is null || AuthorKey.From(sourcePost.AuthorUserId, sourcePost.AuthorParticipantType) != targetKey)
                return SocialOperationResult<bool>.Failure("social_profile_visit_source_invalid", "The profile visit source is not available.");
        }

        var visit = await _db.SocialProfileVisits.SingleOrDefaultAsync(item =>
            item.TargetUserId == targetKey.UserId &&
            item.TargetParticipantType == targetKey.ParticipantType &&
            item.VisitorUserId == actorKey.UserId &&
            item.VisitorParticipantType == actorKey.ParticipantType &&
            item.SourceSocialPostId == sourcePostId,
            cancellationToken);
        if (visit is null)
        {
            _db.SocialProfileVisits.Add(new SocialProfileVisit
            {
                Id = Guid.NewGuid(),
                TargetUserId = targetKey.UserId,
                TargetParticipantType = targetKey.ParticipantType,
                VisitorUserId = actorKey.UserId,
                VisitorParticipantType = actorKey.ParticipantType,
                SourceSocialPostId = sourcePostId,
                FirstVisitedUtc = DateTime.UtcNow,
                LastVisitedUtc = DateTime.UtcNow
            });
        }
        else
        {
            visit.LastVisitedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<bool>.Success(true);
    }

    public async Task<SocialOperationResult<IReadOnlyList<SocialMusicTrack>>> SearchMusicAsync(
        SocialFeedActor actor,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
            return SocialOperationResult<IReadOnlyList<SocialMusicTrack>>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var normalized = query?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaximumMusicQueryLength)
        {
            return SocialOperationResult<IReadOnlyList<SocialMusicTrack>>.Failure(
                "social_music_query_invalid",
                "Search with between 1 and 120 characters.");
        }

        return await _musicCatalog.SearchAsync(normalized, cancellationToken);
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
        if (!visibleAuthors.Contains(AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType)))
            return null;

        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var audience = await LoadAudienceGraphAsync(actorKey, cancellationToken);
        return IsAudiencePermitted(post, actorKey, audience) ? post : null;
    }

    private async Task<Dictionary<Guid, SocialPostMetrics>> LoadPostMetricsAsync(
        IReadOnlyCollection<Guid> postIds,
        CancellationToken cancellationToken)
    {
        if (postIds.Count == 0)
            return new Dictionary<Guid, SocialPostMetrics>();

        var ids = postIds.Distinct().ToArray();
        var views = await _db.SocialPostViews.AsNoTracking()
            .Where(view => ids.Contains(view.SocialPostId))
            .ToArrayAsync(cancellationToken);
        var reactions = await _db.SocialPostReactions.AsNoTracking()
            .Where(reaction => ids.Contains(reaction.SocialPostId))
            .ToArrayAsync(cancellationToken);
        var comments = await _db.SocialPostComments.AsNoTracking()
            .Where(comment => ids.Contains(comment.SocialPostId) && comment.DeletedUtc == null)
            .ToArrayAsync(cancellationToken);
        var reposts = await _db.SocialPostReposts.AsNoTracking()
            .Where(repost => ids.Contains(repost.SocialPostId))
            .ToArrayAsync(cancellationToken);
        var saves = await _db.SocialPostSaves.AsNoTracking()
            .Where(save => ids.Contains(save.SocialPostId))
            .ToArrayAsync(cancellationToken);
        var shares = await _db.SocialPostShares.AsNoTracking()
            .Where(share => ids.Contains(share.SocialPostId))
            .ToArrayAsync(cancellationToken);
        var profileVisits = await _db.SocialProfileVisits.AsNoTracking()
            .Where(visit => ids.Contains(visit.SourceSocialPostId))
            .ToArrayAsync(cancellationToken);
        var follows = await _db.SocialFollows.AsNoTracking()
            .Where(follow => follow.SourceSocialPostId != null && ids.Contains(follow.SourceSocialPostId.Value))
            .ToArrayAsync(cancellationToken);

        return ids.ToDictionary(postId => postId, postId =>
        {
            var postViews = views.Where(view => view.SocialPostId == postId).ToArray();
            var watchedDuration = postViews
                .Where(view => view.MaximumWatchDurationSeconds.HasValue)
                .Select(view => view.MaximumWatchDurationSeconds!.Value)
                .ToArray();
            var watchedCompletion = postViews
                .Where(view => view.MaximumWatchCompletionPercentage.HasValue)
                .Select(view => view.MaximumWatchCompletionPercentage!.Value)
                .ToArray();

            return new SocialPostMetrics(
                postViews.Length,
                postViews.Length,
                reactions.Count(reaction => reaction.SocialPostId == postId),
                comments.Count(comment => comment.SocialPostId == postId),
                comments.Count(comment => comment.SocialPostId == postId && comment.ParentCommentId != null),
                reposts.Count(repost => repost.SocialPostId == postId),
                saves.Count(save => save.SocialPostId == postId),
                shares.Count(share => share.SocialPostId == postId),
                profileVisits.Count(visit => visit.SourceSocialPostId == postId),
                follows.Count(follow => follow.SourceSocialPostId == postId),
                watchedDuration.Length == 0 ? null : decimal.Round(watchedDuration.Average(), 3),
                watchedCompletion.Length == 0 ? null : decimal.Round(watchedCompletion.Average(), 2),
                postViews.Sum(view => view.StoryExitCount),
                postViews.Sum(view => view.StoryTapForwardCount),
                postViews.Sum(view => view.StoryTapBackwardCount));
        });
    }

    private async Task<SocialOperationResult<SocialPostMusicAttachment?>> ResolveMusicAsync(
        SocialMusicSelection? selection,
        CancellationToken cancellationToken)
    {
        if (selection is null)
            return SocialOperationResult<SocialPostMusicAttachment?>.Success(null);

        var providerId = selection.ProviderId?.Trim() ?? string.Empty;
        var trackId = selection.ProviderTrackId?.Trim() ?? string.Empty;
        if (providerId.Length is 0 or > 80 || trackId.Length is 0 or > 256 ||
            selection.TrimStartSeconds < 0 || selection.TrimEndSeconds <= selection.TrimStartSeconds ||
            selection.MusicVolume is < 0 or > 1 || selection.OriginalAudioVolume is < 0 or > 1)
        {
            return SocialOperationResult<SocialPostMusicAttachment?>.Failure(
                "social_music_invalid",
                "The music selection contains an invalid provider, clip, or volume setting.");
        }

        var resolved = await _musicCatalog.ResolveAsync(providerId, trackId, cancellationToken);
        if (!resolved.Succeeded || resolved.Value is null ||
            !string.Equals(resolved.Value.ProviderId, providerId, StringComparison.Ordinal) ||
            !string.Equals(resolved.Value.ProviderTrackId, trackId, StringComparison.Ordinal) ||
            resolved.Value.TrackDurationSeconds <= 0 ||
            selection.TrimEndSeconds > resolved.Value.TrackDurationSeconds)
        {
            return SocialOperationResult<SocialPostMusicAttachment?>.Failure(
                resolved.ErrorCode ?? "social_music_invalid",
                resolved.ErrorMessage ?? "The music selection could not be verified.");
        }

        return SocialOperationResult<SocialPostMusicAttachment?>.Success(new SocialPostMusicAttachment
        {
            Id = Guid.NewGuid(),
            ProviderId = resolved.Value.ProviderId,
            ProviderTrackId = resolved.Value.ProviderTrackId,
            TrackTitle = resolved.Value.TrackTitle,
            ArtistName = resolved.Value.ArtistName,
            TrackDurationSeconds = resolved.Value.TrackDurationSeconds,
            PreviewUrl = resolved.Value.PreviewUrl,
            TrimStartSeconds = selection.TrimStartSeconds,
            TrimEndSeconds = selection.TrimEndSeconds,
            MusicVolume = selection.MusicVolume,
            OriginalAudioVolume = selection.OriginalAudioVolume,
            CreatedUtc = DateTime.UtcNow
        });
    }

    private static SocialPostMusicView ToMusicView(SocialPostMusicAttachment music) => new(
        music.ProviderId,
        music.ProviderTrackId,
        music.TrackTitle,
        music.ArtistName,
        music.TrackDurationSeconds,
        music.PreviewUrl,
        music.TrimStartSeconds,
        music.TrimEndSeconds,
        music.MusicVolume,
        music.OriginalAudioVolume);

    private static SocialPostInsight ToInsight(
        Guid postId,
        string contentType,
        DateTime postedUtc,
        SocialPostMetrics metrics) => new(
            postId,
            contentType,
            postedUtc,
            metrics,
            Percentage(InsightScore(metrics), metrics.UniqueViewerCount));

    private static int InsightScore(SocialPostInsight insight) => InsightScore(insight.Metrics);

    private static int InsightScore(SocialPostMetrics metrics) =>
        metrics.ReactionCount + metrics.CommentCount + metrics.RepostCount + metrics.ShareCount + metrics.SaveCount;

    private static decimal Percentage(int numerator, int denominator) =>
        denominator <= 0 ? 0 : decimal.Round(numerator * 100m / denominator, 2);

    private static bool IsValidWatchMeasurement(decimal? duration, decimal? completion) =>
        (!duration.HasValue || duration.Value is >= 0 and <= MaximumWatchDurationSeconds) &&
        (!completion.HasValue || completion.Value is >= 0 and <= 100);

    private static bool IsValidStoryInteraction(string contentType, string? interactionType) =>
        string.IsNullOrWhiteSpace(interactionType) ||
        string.Equals(contentType, SocialPostContentTypes.Story, StringComparison.Ordinal) &&
        interactionType.Trim() is SocialStoryInteractionTypes.Exit or SocialStoryInteractionTypes.TapForward or SocialStoryInteractionTypes.TapBackward;

    private static void ApplyStoryInteraction(SocialPostViewer view, string? interactionType)
    {
        switch (interactionType?.Trim())
        {
            case SocialStoryInteractionTypes.Exit when view.StoryExitCount < MaximumStoryInteractionCount:
                view.StoryExitCount++;
                break;
            case SocialStoryInteractionTypes.TapForward when view.StoryTapForwardCount < MaximumStoryInteractionCount:
                view.StoryTapForwardCount++;
                break;
            case SocialStoryInteractionTypes.TapBackward when view.StoryTapBackwardCount < MaximumStoryInteractionCount:
                view.StoryTapBackwardCount++;
                break;
        }
    }

    private static decimal? Maximum(decimal? existing, decimal? candidate) =>
        !existing.HasValue ? candidate : !candidate.HasValue ? existing : Math.Max(existing.Value, candidate.Value);

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
        var mediaAssets = await _db.SocialPostMediaAssets
            .AsNoTracking()
            .Where(media => postIds.Contains(media.SocialPostId))
            .OrderBy(media => media.DisplayOrder)
            .ToListAsync(cancellationToken);
        var metricsByPost = await LoadPostMetricsAsync(postIds, cancellationToken);
        var musicByPost = await _db.SocialPostMusicAttachments
            .AsNoTracking()
            .Where(music => postIds.Contains(music.SocialPostId))
            .ToDictionaryAsync(music => music.SocialPostId, cancellationToken);
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var follows = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow =>
                follow.FollowerUserId == actorKey.UserId &&
                follow.FollowerParticipantType == actorKey.ParticipantType)
            .ToListAsync(cancellationToken);
        var savedPostIds = await _db.SocialPostSaves
            .AsNoTracking()
            .Where(save => postIds.Contains(save.SocialPostId) &&
                           save.ActorUserId == actorKey.UserId &&
                           save.ActorParticipantType == actorKey.ParticipantType)
            .Select(save => save.SocialPostId)
            .ToHashSetAsync(cancellationToken);
        var repostedPostIds = await _db.SocialPostReposts
            .AsNoTracking()
            .Where(repost => postIds.Contains(repost.SocialPostId) &&
                              repost.ActorUserId == actorKey.UserId &&
                              repost.ActorParticipantType == actorKey.ParticipantType)
            .Select(repost => repost.SocialPostId)
            .ToHashSetAsync(cancellationToken);

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
                    comment.ParentCommentId,
                    comment.Body,
                    comment.CreatedUtc))
                .ToArray();
            var postReactions = reactions.Where(reaction => reaction.SocialPostId == post.Id).ToArray();
            return new SocialPostView(
                post.Id,
                authors.GetValueOrDefault(AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType)) ?? ToUnknownAuthor(post.AuthorUserId, post.AuthorParticipantType, post.AuthorProfileId),
                post.ContentType,
                post.Body,
                post.Audience,
                post.Location,
                post.CommentsEnabled,
                post.PostedUtc,
                post.ExpiresUtc,
                postReactions.Length,
                comments.Count(comment => comment.SocialPostId == post.Id),
                postReactions.Any(reaction => AuthorKey.From(reaction.ActorUserId, reaction.ActorParticipantType) == actorKey),
                follows.Any(follow =>
                    AuthorKey.From(follow.FollowedUserId, follow.FollowedParticipantType) ==
                    AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType)),
                savedPostIds.Contains(post.Id),
                repostedPostIds.Contains(post.Id),
                metricsByPost[post.Id],
                musicByPost.TryGetValue(post.Id, out var music) ? ToMusicView(music) : null,
                mediaAssets
                    .Where(media => media.SocialPostId == post.Id)
                    .OrderBy(media => media.DisplayOrder)
                    .Select(media => new SocialMediaAssetView(
                        media.Id,
                        media.DisplayOrder,
                        media.MediaKind,
                        media.MimeType,
                        media.FileSizeBytes,
                        media.Width,
                        media.Height,
                        media.AspectRatio,
                        media.DurationSeconds,
                        media.ProcessingState,
                        media.AccessibilityText))
                    .ToArray(),
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

    /// <summary>
    /// The follow edges needed to evaluate narrowed post audiences for one viewer.
    /// </summary>
    private readonly record struct AudienceGraph(
        string[] FollowedAgentIds,
        string[] FollowedClientIds,
        string[] FollowerAgentIds,
        string[] FollowerClientIds);

    private async Task<AudienceGraph> LoadAudienceGraphAsync(
        AuthorKey actorKey,
        CancellationToken cancellationToken)
    {
        var followedByActor = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow => follow.FollowerUserId == actorKey.UserId &&
                             follow.FollowerParticipantType == actorKey.ParticipantType)
            .Select(follow => new { follow.FollowedUserId, follow.FollowedParticipantType })
            .ToArrayAsync(cancellationToken);

        var followersOfActor = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow => follow.FollowedUserId == actorKey.UserId &&
                             follow.FollowedParticipantType == actorKey.ParticipantType)
            .Select(follow => new { follow.FollowerUserId, follow.FollowerParticipantType })
            .ToArrayAsync(cancellationToken);

        return new AudienceGraph(
            followedByActor
                .Where(follow => follow.FollowedParticipantType == MessagingParticipantTypes.Agent)
                .Select(follow => follow.FollowedUserId).Distinct().ToArray(),
            followedByActor
                .Where(follow => follow.FollowedParticipantType == MessagingParticipantTypes.Client)
                .Select(follow => follow.FollowedUserId).Distinct().ToArray(),
            followersOfActor
                .Where(follow => follow.FollowerParticipantType == MessagingParticipantTypes.Agent)
                .Select(follow => follow.FollowerUserId).Distinct().ToArray(),
            followersOfActor
                .Where(follow => follow.FollowerParticipantType == MessagingParticipantTypes.Client)
                .Select(follow => follow.FollowerUserId).Distinct().ToArray());
    }

    /// <summary>
    /// Whether one post's chosen audience admits this viewer. The author always passes.
    /// </summary>
    private static bool IsAudiencePermitted(SocialPost post, AuthorKey actorKey, AudienceGraph audience)
    {
        if (post.Audience == SocialPostAudiences.AuthorizedNetwork)
            return true;
        if (AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType) == actorKey)
            return true;

        var isAgentAuthor = post.AuthorParticipantType == MessagingParticipantTypes.Agent;
        var viewerFollowsAuthor = isAgentAuthor
            ? audience.FollowedAgentIds.Contains(post.AuthorUserId)
            : audience.FollowedClientIds.Contains(post.AuthorUserId);

        if (post.Audience == SocialPostAudiences.Followers)
            return viewerFollowsAuthor;

        if (post.Audience != SocialPostAudiences.MutualConnections)
            return false;

        var authorFollowsViewer = isAgentAuthor
            ? audience.FollowerAgentIds.Contains(post.AuthorUserId)
            : audience.FollowerClientIds.Contains(post.AuthorUserId);
        return viewerFollowsAuthor && authorFollowsViewer;
    }

    /// <summary>
    /// All normalized author user IDs that belong to one logical participant. Clients can
    /// have authored content under either stored identity form, so a single-form match
    /// silently hides their older posts from their own profile and insights.
    /// </summary>
    private async Task<string[]> AuthorUserIdFormsAsync(
        AuthorKey key,
        CancellationToken cancellationToken)
    {
        if (key.ParticipantType != MessagingParticipantTypes.Client ||
            string.IsNullOrWhiteSpace(key.UserId))
        {
            return [key.UserId];
        }

        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .Where(candidate => candidate.ClientUserId.ToLower() == key.UserId ||
                                (candidate.ExternalIdentityObjectId != null &&
                                 candidate.ExternalIdentityObjectId.ToLower() == key.UserId))
            .Select(candidate => new { candidate.ClientUserId, candidate.ExternalIdentityObjectId })
            .FirstOrDefaultAsync(cancellationToken);

        return profile is null
            ? [key.UserId]
            : ClientIdentityForms(profile.ClientUserId, profile.ExternalIdentityObjectId).ToArray();
    }

    /// <summary>
    /// Builds an unpaged relationship list from the same follow edges used by the
    /// profile totals. Returning every edge is deliberate: the native client owns
    /// scrolling, while the server remains the single source of truth for who is
    /// present in Follows and Followers.
    /// </summary>
    private async Task<IReadOnlyList<SocialFollowListEntry>> GetFollowListAsync(
        AuthorKey profileKey,
        AuthorKey viewerKey,
        string listKind,
        CancellationToken cancellationToken)
    {
        var profileUserIds = await AuthorUserIdFormsAsync(profileKey, cancellationToken);
        var rawEntries = listKind == SocialFollowListKinds.Follows
            ? await _db.SocialFollows
                .AsNoTracking()
                .Where(follow => profileUserIds.Contains(follow.FollowerUserId) &&
                                 follow.FollowerParticipantType == profileKey.ParticipantType)
                .OrderByDescending(follow => follow.CreatedUtc)
                .Select(follow => new FollowListReference(
                    follow.FollowedUserId,
                    follow.FollowedParticipantType,
                    follow.CreatedUtc))
                .ToArrayAsync(cancellationToken)
            : await _db.SocialFollows
                .AsNoTracking()
                .Where(follow => profileUserIds.Contains(follow.FollowedUserId) &&
                                 follow.FollowedParticipantType == profileKey.ParticipantType)
                .OrderByDescending(follow => follow.CreatedUtc)
                .Select(follow => new FollowListReference(
                    follow.FollowerUserId,
                    follow.FollowerParticipantType,
                    follow.CreatedUtc))
                .ToArrayAsync(cancellationToken);

        if (rawEntries.Length == 0)
            return Array.Empty<SocialFollowListEntry>();

        var viewerUserIds = await AuthorUserIdFormsAsync(viewerKey, cancellationToken);
        var viewerFollowing = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow => viewerUserIds.Contains(follow.FollowerUserId) &&
                             follow.FollowerParticipantType == viewerKey.ParticipantType)
            .Select(follow => new AuthorReference(
                follow.FollowedUserId,
                follow.FollowedParticipantType,
                Guid.Empty))
            .ToArrayAsync(cancellationToken);
        var authors = await ResolveAuthorsAsync(
            rawEntries
                .Select(entry => new AuthorReference(entry.UserId, entry.ParticipantType, Guid.Empty))
                .Concat(viewerFollowing),
            cancellationToken);
        var followedByViewer = viewerFollowing
            .Select(reference => CanonicalAuthorKey(reference, authors))
            .ToHashSet();

        return rawEntries.Select(entry =>
        {
            var reference = new AuthorReference(entry.UserId, entry.ParticipantType, Guid.Empty);
            var author = authors.GetValueOrDefault(AuthorKey.From(entry.UserId, entry.ParticipantType))
                         ?? ToUnknownAuthor(entry.UserId, entry.ParticipantType, Guid.Empty);
            return new SocialFollowListEntry(
                author,
                followedByViewer.Contains(CanonicalAuthorKey(reference, authors)));
        }).ToArray();
    }

    private async Task<bool> HasDirectFollowRelationshipAsync(
        AuthorKey first,
        AuthorKey second,
        CancellationToken cancellationToken)
    {
        var firstUserIds = await AuthorUserIdFormsAsync(first, cancellationToken);
        var secondUserIds = await AuthorUserIdFormsAsync(second, cancellationToken);
        return await _db.SocialFollows.AsNoTracking().AnyAsync(follow =>
            (firstUserIds.Contains(follow.FollowerUserId) &&
             follow.FollowerParticipantType == first.ParticipantType &&
             secondUserIds.Contains(follow.FollowedUserId) &&
             follow.FollowedParticipantType == second.ParticipantType) ||
            (secondUserIds.Contains(follow.FollowerUserId) &&
             follow.FollowerParticipantType == second.ParticipantType &&
             firstUserIds.Contains(follow.FollowedUserId) &&
             follow.FollowedParticipantType == first.ParticipantType),
            cancellationToken);
    }

    private static AuthorKey CanonicalAuthorKey(
        AuthorReference reference,
        IReadOnlyDictionary<AuthorKey, SocialAuthor> authors)
    {
        var raw = AuthorKey.From(reference.UserId, reference.ParticipantType);
        return authors.TryGetValue(raw, out var author)
            ? AuthorKey.From(author.UserId, author.ParticipantType)
            : raw;
    }

    private async Task<HashSet<AuthorKey>> GetVisibleAuthorsAsync(SocialFeedActor actor, CancellationToken cancellationToken)
    {
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var visible = new HashSet<AuthorKey> { actorKey };

        var recipients = await _messaging.ListRecipientsAsync(actor.Identity, cancellationToken: cancellationToken);
        if (!recipients.Succeeded)
            return await ExpandClientIdentityFormsAsync(visible, cancellationToken);

        foreach (var recipient in recipients.Recipients)
            visible.Add(AuthorKey.From(recipient.UserId, recipient.ParticipantType));

        return await ExpandClientIdentityFormsAsync(visible, cancellationToken);
    }

    /// <summary>
    /// Adds the sibling stored identity form for every already-authorized client so
    /// content authored under the alternate form stays reachable. This widens spelling,
    /// never authority: no participant who was not already authorized is added.
    /// </summary>
    private async Task<HashSet<AuthorKey>> ExpandClientIdentityFormsAsync(
        HashSet<AuthorKey> visible,
        CancellationToken cancellationToken)
    {
        var clientUserIds = visible
            .Where(key => key.ParticipantType == MessagingParticipantTypes.Client)
            .Select(key => key.UserId)
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (clientUserIds.Length == 0)
            return visible;

        var profiles = await _db.ClientProfiles
            .AsNoTracking()
            .Where(profile => clientUserIds.Contains(profile.ClientUserId.ToLower()) ||
                              (profile.ExternalIdentityObjectId != null &&
                               clientUserIds.Contains(profile.ExternalIdentityObjectId.ToLower())))
            .Select(profile => new { profile.ClientUserId, profile.ExternalIdentityObjectId })
            .ToListAsync(cancellationToken);

        foreach (var profile in profiles)
        {
            foreach (var identityForm in ClientIdentityForms(profile.ClientUserId, profile.ExternalIdentityObjectId))
                visible.Add(AuthorKey.From(identityForm, MessagingParticipantTypes.Client));
        }

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
                // One client profile can be referenced by two stored identity forms: the
                // Entra object ID and the legacy ClientUserId. Historical posts exist under
                // both. Registering only the matched form left the other form unresolved,
                // which minted a second "Client" author for the same person and produced a
                // duplicate story profile in the rail. Register both forms against one
                // canonical author so every reference collapses to a single identity.
                var canonicalUserId = Normalize(
                    FirstNonEmpty(profile.ExternalIdentityObjectId, profile.ClientUserId));
                if (string.IsNullOrWhiteSpace(canonicalUserId))
                    continue;

                var name = FirstNonEmpty($"{profile.FirstName} {profile.LastName}".Trim(), profile.Email, "Client");
                var author = new SocialAuthor(
                    canonicalUserId, MessagingParticipantTypes.Client, profile.Id, name);

                foreach (var identityForm in ClientIdentityForms(profile.ClientUserId, profile.ExternalIdentityObjectId))
                    result[AuthorKey.From(identityForm, MessagingParticipantTypes.Client)] = author;
            }
        }

        return result;
    }

    /// <summary>
    /// Every normalized identity form a client profile can legitimately be stored under.
    /// </summary>
    private static IEnumerable<string> ClientIdentityForms(string? clientUserId, string? externalIdentityObjectId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in new[] { clientUserId, externalIdentityObjectId })
        {
            var normalized = Normalize(candidate);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                yield return normalized;
        }
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

    private static bool HasValidMediaForContentType(
        string contentType,
        IReadOnlyCollection<SocialPostMediaAsset> mediaAssets) =>
        contentType switch
        {
            SocialPostContentTypes.Post =>
                mediaAssets.Count is > 0 and <= MaximumMediaItemsPerPost,
            SocialPostContentTypes.Story => mediaAssets.Count == 1,
            SocialPostContentTypes.Reel =>
                mediaAssets.Count == 1 &&
                string.Equals(
                    mediaAssets.Single().MediaKind,
                    "Video",
                    StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static string MediaValidationMessage(string contentType) =>
        contentType switch
        {
            SocialPostContentTypes.Story =>
                "Stories require exactly one supported image or video.",
            SocialPostContentTypes.Reel =>
                "Hacs require exactly one supported video.",
            _ =>
                $"Posts require between 1 and {MaximumMediaItemsPerPost} supported media files."
        };

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private async Task DeleteStoredMediaAsync(
        IEnumerable<string> storageKeys,
        CancellationToken cancellationToken)
    {
        foreach (var storageKey in storageKeys.Distinct(StringComparer.Ordinal))
        {
            await _mediaStorage.DeleteAsync(storageKey, cancellationToken);
        }
    }

    private static string NormalizeBody(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length > maximumLength ? string.Empty : normalized;
    }

    private static string? NormalizeOptionalText(
        string? value,
        int maximumLength)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private readonly record struct AuthorKey(string UserId, string ParticipantType)
    {
        public static AuthorKey From(string? userId, string? participantType) =>
            new(Normalize(userId), participantType?.Trim() ?? string.Empty);
    }

    private readonly record struct AuthorReference(string UserId, string ParticipantType, Guid ProfileId);

    private readonly record struct FollowListReference(
        string UserId,
        string ParticipantType,
        DateTime CreatedUtc);
}
