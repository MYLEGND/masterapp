using System.Collections.Frozen;
using Domain.Entities;
using Domain.Messaging;
using Domain.Moderation;
using Domain.Social;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Shared.Auth;

namespace Infrastructure.Social;

/// <summary>
/// Server-authoritative community feed. Active members can discover public
/// profiles; private-profile content is visible only to its owner and approved
/// followers.
/// </summary>
public sealed class SocialFeedService : ISocialFeedService
{
    private const int MaximumPostLength = 2_000;
    private const int MaximumCommentLength = 800;
    private const int MaximumFeedPosts = 80;
    private const int MaximumHacPosts = 80;
    private const int FeedCandidateWindowDays = 7;
    private const int MaximumAffinitySignalsPerType = 250;
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
    private const string RespectfulCommunityMessage = "This content cannot be shared because it violates Legend Legacy Protection's respectful-communication policy. Please revise it and try again.";
    private static readonly TimeSpan AudienceGraphCacheDuration = TimeSpan.FromMinutes(3);
    private const string AudienceGraphCachePrefix = "legend.social.audience.v1";

    private readonly MasterAppDbContext _db;
    private readonly ISocialMediaStorage _mediaStorage;
    private readonly ISocialMediaProcessingQueue? _mediaProcessingQueue;
    private readonly ISocialMusicCatalog _musicCatalog;
    private readonly ICommunityTextModerationService _moderation;
    private readonly IMemoryCache _memoryCache;
    private readonly string? _configuredFounderOid;
    private readonly ICommunitySafetyService? _communitySafety;

    public SocialFeedService(
        MasterAppDbContext db,
        ISocialMediaStorage mediaStorage,
        ISocialMusicCatalog musicCatalog,
        IMemoryCache memoryCache,
        ICommunityTextModerationService moderation,
        ISocialMediaProcessingQueue? mediaProcessingQueue = null,
        string? configuredFounderOid = null,
        ICommunitySafetyService? communitySafety = null)
    {
        _db = db;
        _mediaStorage = mediaStorage;
        _mediaProcessingQueue = mediaProcessingQueue;
        _musicCatalog = musicCatalog;
        _memoryCache = memoryCache;
        _moderation = moderation;
        _configuredFounderOid = configuredFounderOid ??
            Environment.GetEnvironmentVariable("FOUNDER_OID") ??
            Environment.GetEnvironmentVariable("FounderOid");
        _communitySafety = communitySafety;
    }

    public async Task<SocialOperationResult<SocialFeedSnapshot>> GetFeedAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
            return SocialOperationResult<SocialFeedSnapshot>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var activeAuthors = await GetActiveAuthorsAsync(actor, cancellationToken);
        var now = DateTime.UtcNow;
        var visibleAgentUserIds = activeAuthors
            .Where(key => key.ParticipantType == MessagingParticipantTypes.Agent)
            .Select(key => key.UserId)
            .ToArray();
        var visibleClientUserIds = activeAuthors
            .Where(key => key.ParticipantType == MessagingParticipantTypes.Client)
            .Select(key => key.UserId)
            .ToArray();
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var audience = await LoadAudienceGraphAsync(actor, cancellationToken);

        // Each surface has its own source collection. Home carries regular posts,
        // Stories remain ephemeral, and Hacs are video-only candidates for the FYP.
        // Keeping those contracts separate prevents a photo post from reaching the
        // vertical-video surface.
        var visiblePosts = _db.SocialPosts
            .AsNoTracking()
            .Where(post => post.PublicationState == SocialPostPublicationStates.Published &&
                           post.DeletedUtc == null &&
                           (post.ExpiresUtc == null || post.ExpiresUtc > now))
            // Source bytes are never publicly visible while the one persisted
            // video lifecycle is still pending or has failed.
            .Where(post => !post.MediaAssets.Any(media =>
                media.ProcessingState != SocialMediaProcessingStates.Ready))
            .Where(post =>
                (post.AuthorParticipantType == MessagingParticipantTypes.Agent &&
                 visibleAgentUserIds.Contains(post.AuthorUserId.ToLower())) ||
                (post.AuthorParticipantType == MessagingParticipantTypes.Client &&
                 visibleClientUserIds.Contains(post.AuthorUserId.ToLower())));

        var storyPosts = await visiblePosts
            .Where(post => post.ContentType == SocialPostContentTypes.Story)
            .OrderByDescending(post => post.PostedUtc)
            .Take(MaximumStoryPosts)
            .ToArrayAsync(cancellationToken);
        var candidateSince = now.AddDays(-FeedCandidateWindowDays);
        var feedPosts = await LoadRankableCandidatesAsync(
            visiblePosts
            .Where(post =>
                post.ContentType == SocialPostContentTypes.Post &&
                post.PostedUtc >= candidateSince),
            actor,
            audience,
            MaximumFeedPosts,
            cancellationToken);
        var hacPosts = await LoadRankableCandidatesAsync(
            visiblePosts
            .Where(post =>
                post.ContentType == SocialPostContentTypes.Reel &&
                post.MediaAssets.Count == 1 &&
                post.MediaAssets.Any(media => media.MediaKind == "Video") &&
                post.PostedUtc >= candidateSince),
            actor,
            audience,
            MaximumHacPosts,
            cancellationToken);

        var stories = await FilterPostsForViewerAsync(storyPosts, actorKey, audience, cancellationToken);
        var feed = await FilterPostsForViewerAsync(feedPosts, actorKey, audience, cancellationToken);
        var hacs = await FilterPostsForViewerAsync(hacPosts, actorKey, audience, cancellationToken);
        var rankedFeed = await RankFeedCandidatesForViewerAsync(
            feed,
            actor,
            audience,
            now,
            cancellationToken);
        var rankedHacs = await RankFeedCandidatesForViewerAsync(
            hacs,
            actor,
            audience,
            now,
            cancellationToken);
        var activity = await GetActivityAsync(actor, cancellationToken);
        var promotedGroups = await GetPromotedGroupsAsync(actor, activeAuthors, cancellationToken);

        var profileMetrics = await GetProfileMetricsAsync(actor, cancellationToken: cancellationToken);
        var creatorInsights = await GetCreatorInsightsAsync(actor, cancellationToken);
        if (!profileMetrics.Succeeded || profileMetrics.Value is null ||
            !creatorInsights.Succeeded || creatorInsights.Value is null)
        {
            return SocialOperationResult<SocialFeedSnapshot>.Failure(
                "social_metrics_unavailable",
                "Legend social metrics could not be loaded.");
        }

        var selectedFeed = rankedFeed.Take(MaximumFeedPosts).ToArray();
        var selectedHacs = rankedHacs.Take(MaximumHacPosts).ToArray();

        // Stories, Home posts, and Hacs share one SocialPostView projection.
        // Hydrate their final selected post set once so comments, reactions,
        // media, metrics, music, follows, saves, reposts, and authors are not
        // queried again for each surface.
        var hydratedPosts = await BuildPostViewsAsync(
            stories
                .Concat(selectedFeed)
                .Concat(selectedHacs)
                .GroupBy(post => post.Id)
                .Select(group => group.First()),
            actor,
            cancellationToken);

        var hydratedById = hydratedPosts.ToDictionary(post => post.Id);

        static IReadOnlyList<SocialPostView> SelectHydrated(
            IEnumerable<SocialPost> source,
            IReadOnlyDictionary<Guid, SocialPostView> hydratedById) =>
            source
                .Select(post => hydratedById[post.Id])
                .ToArray();

        return SocialOperationResult<SocialFeedSnapshot>.Success(
            new SocialFeedSnapshot(
                SelectHydrated(stories, hydratedById),
                SelectHydrated(selectedFeed, hydratedById),
                SelectHydrated(selectedHacs, hydratedById),
                activity,
                activity.Count,
                profileMetrics.Value,
                creatorInsights.Value)
            {
                PromotedGroups = promotedGroups
            });
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
                           post.PublicationState == SocialPostPublicationStates.Published &&
                           post.DeletedUtc == null &&
                           (post.ExpiresUtc == null || post.ExpiresUtc > now))
            .OrderByDescending(post => post.PostedUtc)
            .Take(MaximumProfilePosts)
            .ToArrayAsync(cancellationToken);

        return SocialOperationResult<IReadOnlyList<SocialPostView>>.Success(
            await BuildPostViewsAsync(posts, actor, cancellationToken));
    }

    /// <summary>
    /// Returns a member's actual published profile content. The same network and
    /// audience checks that guard the feed are applied before a post is projected.
    /// </summary>
    public async Task<SocialOperationResult<IReadOnlyList<SocialPostView>>> GetPublicProfilePostsAsync(
        SocialFeedActor actor,
        SocialAuthor profile,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveNetworkProfileAsync(actor, profile, cancellationToken);
        if (!resolved.Succeeded || resolved.Value is null)
        {
            return SocialOperationResult<IReadOnlyList<SocialPostView>>.Failure(
                resolved.ErrorCode ?? "social_profile_unavailable",
                resolved.ErrorMessage ?? "This Legend profile is not available.");
        }

        var targetUserIds = await AuthorUserIdFormsAsync(resolved.Value.TargetKey, cancellationToken);
        var now = DateTime.UtcNow;
        var candidates = await _db.SocialPosts
            .AsNoTracking()
            .Where(post => targetUserIds.Contains(post.AuthorUserId)
                           && post.AuthorParticipantType == resolved.Value.TargetKey.ParticipantType
                           && post.ContentType != SocialPostContentTypes.Story
                           && post.PublicationState == SocialPostPublicationStates.Published
                           && post.DeletedUtc == null
                           && (post.ExpiresUtc == null || post.ExpiresUtc > now)
                           && !post.MediaAssets.Any(media =>
                               media.ProcessingState != SocialMediaProcessingStates.Ready))
            .OrderByDescending(post => post.PostedUtc)
            .ToArrayAsync(cancellationToken);
        var audience = await LoadAudienceGraphAsync(actor, cancellationToken);
        var posts = (await FilterPostsForViewerAsync(candidates, resolved.Value.ActorKey, audience, cancellationToken))
            .Take(MaximumProfilePosts)
            .ToArray();

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
        if (!IsCommunityTextAllowed(body, "SocialPost"))
            return ContentBlocked<SocialPostView>();

        var details = command.Details ?? new SocialPostDetails();

        var post = new SocialPost
        {
            Id = Guid.NewGuid(),
            AuthorUserId = Normalize(command.Actor.Identity.UserId),
            AuthorParticipantType = command.Actor.Identity.ParticipantType,
            AuthorProfileId = command.Actor.ProfileId,
            ContentType = contentType,
            PublicationState = SocialPostPublicationStates.Published,
            Audience = SocialPostAudiences.AuthorizedNetwork,
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
        if (!IsCommunityTextAllowed(body, "SocialMediaPost"))
            return ContentBlocked<SocialPostView>();

        // Hacs have one portable delivery contract. The iOS creation path
        // produces H.264/AAC MP4 before this point; rejecting unnormalized
        // containers here avoids publishing a black or unsupported player.
        if (contentType == SocialPostContentTypes.Reel &&
            uploads.Any(upload => !SocialMediaUploadLimits
                .IsPortableHacVideoFileName(upload.OriginalFileName)))
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_post_invalid",
                "Legend Hacs require a prepared MP4 video. Choose the video again and try publishing.");
        }

        var previewImage = command.PreviewImage;
        if (!IsValidHacPreview(contentType, previewImage))
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_preview_invalid",
                "A Hac preview must be a JPEG image no larger than 1 MB.");
        }

        var details = command.Details ?? new SocialPostDetails();
        SocialPostMusicAttachment? musicAttachment = null;
        if (command.PublishImmediately)
        {
            var music = await ResolveMusicAsync(command.Music, cancellationToken);
            if (!music.Succeeded)
            {
                return SocialOperationResult<SocialPostView>.Failure(
                    music.ErrorCode ?? "social_music_invalid",
                    music.ErrorMessage ?? "The selected music could not be verified.");
            }

            musicAttachment = music.Value;
        }

        var postId = Guid.NewGuid();
        var storedKeys = new List<string>(uploads.Length + (previewImage is null ? 0 : 1));
        var mediaAssets = new List<SocialPostMediaAsset>(uploads.Length);
        var pendingVideoAssetIds = new List<Guid>();

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
                    ProcessingState = stored.RequiresBackgroundProcessing
                        ? SocialMediaProcessingStates.PendingProcessing
                        : SocialMediaProcessingStates.Ready,
                    AccessibilityText = NormalizeOptionalText(
                        upload.AccessibilityText,
                        MaximumAccessibilityTextLength),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });

                if (stored.RequiresBackgroundProcessing)
                    pendingVideoAssetIds.Add(mediaAssetId);
            }

            if (!HasValidMediaForContentType(contentType, mediaAssets))
            {
                await DeleteStoredMediaAsync(storedKeys, CancellationToken.None);

                return SocialOperationResult<SocialPostView>.Failure(
                    "social_media_post_invalid",
                    MediaValidationMessage(contentType));
            }

            if (previewImage is not null)
            {
                var previewResult = await StoreHacPreviewAsync(previewImage, cancellationToken);
                if (!previewResult.Succeeded || previewResult.Value is null)
                {
                    await DeleteStoredMediaAsync(storedKeys, CancellationToken.None);
                    return SocialOperationResult<SocialPostView>.Failure(
                        previewResult.ErrorCode ?? "social_media_preview_invalid",
                        previewResult.ErrorMessage ?? "Legend could not store the selected Hac preview.");
                }

                storedKeys.Add(previewResult.Value.StorageKey);
                mediaAssets.Single().ThumbnailStorageKey = previewResult.Value.StorageKey;
            }

            var now = DateTime.UtcNow;
            var post = new SocialPost
            {
                Id = postId,
                AuthorUserId = Normalize(command.Actor.Identity.UserId),
                AuthorParticipantType = command.Actor.Identity.ParticipantType,
                AuthorProfileId = command.Actor.ProfileId,
                ContentType = contentType,
                PublicationState = command.PublishImmediately
                    ? SocialPostPublicationStates.Published
                    : SocialPostPublicationStates.Draft,
                Audience = SocialPostAudiences.AuthorizedNetwork,
                Location = NormalizeOptionalText(details.Location, MaximumLocationLength),
                CommentsEnabled = details.CommentsEnabled,
                Body = body,
                PostedUtc = now,
                ExpiresUtc = command.PublishImmediately && contentType == SocialPostContentTypes.Story
                    ? now.AddHours(24)
                    : null,
                MediaAssets = mediaAssets,
                MusicAttachment = musicAttachment
            };

            _db.SocialPosts.Add(post);
            await _db.SaveChangesAsync(cancellationToken);

            // This signal is deliberately non-blocking. The persisted media
            // state remains the recovery authority if an App Service recycle
            // occurs before the worker reads it.
            foreach (var pendingVideoAssetId in pendingVideoAssetIds)
                _mediaProcessingQueue?.Enqueue(pendingVideoAssetId);

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

    public async Task<SocialOperationResult<SocialPostView>> PublishStagedMediaPostAsync(
        PublishStagedSocialMediaPostCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_actor_invalid",
                "Your mobile identity is not available for Legend updates.");
        }

        var author = AuthorKey.From(command.Actor.Identity.UserId, command.Actor.Identity.ParticipantType);
        var post = await _db.SocialPosts
            .Include(item => item.MediaAssets)
            .Include(item => item.MusicAttachment)
            .SingleOrDefaultAsync(
                item => item.Id == command.PostId &&
                        item.AuthorUserId == author.UserId &&
                        item.AuthorParticipantType == author.ParticipantType &&
                        item.DeletedUtc == null,
                cancellationToken);
        if (post is null || post.PublicationState != SocialPostPublicationStates.Draft)
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_draft_unavailable",
                "This media draft is no longer available. Choose the media again and try publishing.");
        }

        if (!HasValidMediaForContentType(post.ContentType, post.MediaAssets.ToArray()))
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_post_invalid",
                MediaValidationMessage(post.ContentType));
        }

        var body = NormalizeBody(command.Body, MaximumPostLength);
        if ((command.Body ?? string.Empty).Trim().Length > 0 && string.IsNullOrWhiteSpace(body))
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_post_invalid",
                "The caption is too long. Keep it within the Legend update limit.");
        }
        if (!IsCommunityTextAllowed(body, "SocialMediaPostPublish"))
            return ContentBlocked<SocialPostView>();

        if (!IsValidHacPreview(post.ContentType, command.PreviewImage))
        {
            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_preview_invalid",
                "A Hac preview must be a JPEG image no larger than 1 MB.");
        }

        var music = await ResolveMusicAsync(command.Music, cancellationToken);
        if (!music.Succeeded)
        {
            return SocialOperationResult<SocialPostView>.Failure(
                music.ErrorCode ?? "social_music_invalid",
                music.ErrorMessage ?? "The selected music could not be verified.");
        }

        string? newlyStoredPreviewKey = null;
        string? replacedPreviewKey = null;
        try
        {
            if (command.PreviewImage is not null)
            {
                var preview = await StoreHacPreviewAsync(command.PreviewImage, cancellationToken);
                if (!preview.Succeeded || preview.Value is null)
                {
                    return SocialOperationResult<SocialPostView>.Failure(
                        preview.ErrorCode ?? "social_media_preview_invalid",
                        preview.ErrorMessage ?? "Legend could not store the selected Hac preview.");
                }

                var video = post.MediaAssets.Single();
                newlyStoredPreviewKey = preview.Value.StorageKey;
                replacedPreviewKey = video.ThumbnailStorageKey;
                video.ThumbnailStorageKey = newlyStoredPreviewKey;
            }

            var details = command.Details ?? new SocialPostDetails();
            var now = DateTime.UtcNow;
            post.Body = body;
            post.Audience = SocialPostAudiences.AuthorizedNetwork;
            post.Location = NormalizeOptionalText(details.Location, MaximumLocationLength);
            post.CommentsEnabled = details.CommentsEnabled;
            post.PostedUtc = now;
            post.ExpiresUtc = post.ContentType == SocialPostContentTypes.Story
                ? now.AddHours(24)
                : null;
            post.PublicationState = SocialPostPublicationStates.Published;

            if (post.MusicAttachment is not null)
                _db.SocialPostMusicAttachments.Remove(post.MusicAttachment);
            post.MusicAttachment = music.Value;

            await _db.SaveChangesAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(replacedPreviewKey) &&
                !string.Equals(replacedPreviewKey, newlyStoredPreviewKey, StringComparison.Ordinal))
            {
                await DeleteStoredMediaAsync([replacedPreviewKey], CancellationToken.None);
            }

            return SocialOperationResult<SocialPostView>.Success(
                await BuildPostViewAsync(post, command.Actor, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(newlyStoredPreviewKey))
                await DeleteStoredMediaAsync([newlyStoredPreviewKey], CancellationToken.None);
            throw;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            if (!string.IsNullOrWhiteSpace(newlyStoredPreviewKey))
                await DeleteStoredMediaAsync([newlyStoredPreviewKey], CancellationToken.None);

            return SocialOperationResult<SocialPostView>.Failure(
                "social_media_persistence_failed",
                "Legend could not publish this media draft. Please try again.");
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
                    item.PublicationState == SocialPostPublicationStates.Published &&
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
        if (!IsCommunityTextAllowed(body, "SocialPostUpdate"))
            return ContentBlocked<SocialPostView>();

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
        var post = await _db.SocialPosts
            .Include(item => item.MediaAssets)
            .SingleOrDefaultAsync(
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

        // Object storage is separate from the relational transaction. Delete those
        // idempotent keys first so a returned success never leaves the member's
        // video or image behind; a transient storage failure leaves the database
        // untouched and the user can safely retry.
        var mediaStorageKeys = post.MediaAssets
            .SelectMany(media => new[]
            {
                media.StorageKey,
                media.ThumbnailStorageKey
            })
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        try
        {
            await DeleteStoredMediaAsync(mediaStorageKeys, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested == false)
        {
            return SocialOperationResult<bool>.Failure(
                "social_media_delete_failed",
                "Legend could not remove this post's media. Nothing was deleted; please try again.");
        }

        // These tables intentionally preserve a relationship or an attribution
        // without requiring a database FK to the post. A physical post deletion
        // must explicitly clean or detach those source references.
        var profileVisits = await _db.SocialProfileVisits
            .Where(visit => visit.SourceSocialPostId == post.Id)
            .ToArrayAsync(cancellationToken);
        var sourceFollows = await _db.SocialFollows
            .Where(follow => follow.SourceSocialPostId == post.Id)
            .ToArrayAsync(cancellationToken);
        var dependentReposts = await _db.SocialPosts
            .Where(item => item.RepostOfSocialPostId == post.Id)
            .ToArrayAsync(cancellationToken);

        _db.SocialProfileVisits.RemoveRange(profileVisits);
        foreach (var follow in sourceFollows)
            follow.SourceSocialPostId = null;
        foreach (var repost in dependentReposts)
            repost.RepostOfSocialPostId = null;

        // The model configures cascade removal for media metadata, comments,
        // reactions, views, saves, shares, reposts, and music. This is deliberately
        // a physical removal: deleted social content no longer occupies a row in the
        // production database.
        _db.SocialPosts.Remove(post);
        await _db.SaveChangesAsync(cancellationToken);
        return SocialOperationResult<bool>.Success(true);
    }

    public async Task<SocialOperationResult<bool>> RemoveReportedPostAsync(
        Guid postId,
        CancellationToken cancellationToken = default)
    {
        if (postId == Guid.Empty)
            return SocialOperationResult<bool>.Failure("social_report_target_invalid", "The reported content is unavailable.");

        var post = await _db.SocialPosts.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == postId && item.DeletedUtc == null,
            cancellationToken);
        if (post is null)
            return SocialOperationResult<bool>.Success(true);

        return await DeletePostAsync(
            new SocialPostMutationCommand(
                new SocialFeedActor(
                    new MessagingActor(post.AuthorUserId, post.AuthorParticipantType),
                    post.AuthorProfileId,
                    "Community moderation"),
                post.Id),
            cancellationToken);
    }

    /// <summary>
    /// Removes a departing member's social footprint through the existing
    /// social and storage authority. Posts are physically deleted through the
    /// same path a member uses. A comment with replies from other members is
    /// minimally redacted instead, preserving those replies' parent relation
    /// without retaining the departing member's content or identity.
    /// </summary>
    public async Task<SocialOperationResult<SocialAccountClosureDisposition>> RemoveAccountContentForClosureAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
        {
            return SocialOperationResult<SocialAccountClosureDisposition>.Failure(
                "social_actor_invalid",
                "The account is not available for social-content removal.");
        }

        var author = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var authorUserIds = await AuthorUserIdFormsAsync(author, cancellationToken);
        var postIds = await _db.SocialPosts
            .AsNoTracking()
            .Where(post => authorUserIds.Contains(post.AuthorUserId) &&
                           post.AuthorParticipantType == author.ParticipantType &&
                           post.DeletedUtc == null)
            .Select(post => post.Id)
            .ToArrayAsync(cancellationToken);

        foreach (var postId in postIds)
        {
            var deleted = await DeletePostAsync(new SocialPostMutationCommand(actor, postId), cancellationToken);
            if (!deleted.Succeeded)
            {
                return SocialOperationResult<SocialAccountClosureDisposition>.Failure(
                    deleted.ErrorCode ?? "social_closure_delete_failed",
                    deleted.ErrorMessage ?? "Legend could not remove the account's social content.");
            }
        }

        var authoredComments = await _db.SocialPostComments
            .Where(comment => authorUserIds.Contains(comment.AuthorUserId) &&
                              comment.AuthorParticipantType == author.ParticipantType)
            .ToArrayAsync(cancellationToken);
        var authoredCommentIds = authoredComments.Select(comment => comment.Id).ToArray();
        var commentsWithReplies = authoredCommentIds.Length == 0
            ? new HashSet<Guid>()
            : (await _db.SocialPostComments
                .AsNoTracking()
                .Where(comment => comment.ParentCommentId != null &&
                                  authoredCommentIds.Contains(comment.ParentCommentId.Value))
                .Select(comment => comment.ParentCommentId!.Value)
                .Distinct()
                .ToArrayAsync(cancellationToken))
                .ToHashSet();

        var removableComments = authoredComments
            .Where(comment => !commentsWithReplies.Contains(comment.Id))
            .ToArray();
        var redactedComments = authoredComments
            .Where(comment => commentsWithReplies.Contains(comment.Id))
            .ToArray();
        var now = DateTime.UtcNow;
        foreach (var comment in redactedComments)
        {
            comment.AuthorUserId = $"closed:{comment.Id:N}";
            comment.AuthorParticipantType = "Closed";
            comment.AuthorProfileId = Guid.Empty;
            comment.Body = string.Empty;
            comment.DeletedUtc = now;
        }

        var reactions = await _db.SocialPostReactions
            .Where(reaction => authorUserIds.Contains(reaction.ActorUserId) &&
                               reaction.ActorParticipantType == author.ParticipantType)
            .ToArrayAsync(cancellationToken);
        var views = await _db.SocialPostViews
            .Where(view => authorUserIds.Contains(view.ViewerUserId) &&
                           view.ViewerParticipantType == author.ParticipantType)
            .ToArrayAsync(cancellationToken);
        var saves = await _db.SocialPostSaves
            .Where(save => authorUserIds.Contains(save.ActorUserId) &&
                           save.ActorParticipantType == author.ParticipantType)
            .ToArrayAsync(cancellationToken);
        var shares = await _db.SocialPostShares
            .Where(share => authorUserIds.Contains(share.ActorUserId) &&
                            share.ActorParticipantType == author.ParticipantType)
            .ToArrayAsync(cancellationToken);
        var reposts = await _db.SocialPostReposts
            .Where(repost => authorUserIds.Contains(repost.ActorUserId) &&
                             repost.ActorParticipantType == author.ParticipantType)
            .ToArrayAsync(cancellationToken);
        var follows = await _db.SocialFollows
            .Where(follow =>
                (authorUserIds.Contains(follow.FollowerUserId) &&
                 follow.FollowerParticipantType == author.ParticipantType) ||
                (authorUserIds.Contains(follow.FollowedUserId) &&
                 follow.FollowedParticipantType == author.ParticipantType))
            .ToArrayAsync(cancellationToken);
        var visits = await _db.SocialProfileVisits
            .Where(visit =>
                (authorUserIds.Contains(visit.VisitorUserId) &&
                 visit.VisitorParticipantType == author.ParticipantType) ||
                (authorUserIds.Contains(visit.TargetUserId) &&
                 visit.TargetParticipantType == author.ParticipantType))
            .ToArrayAsync(cancellationToken);

        _db.SocialPostComments.RemoveRange(removableComments);
        _db.SocialPostReactions.RemoveRange(reactions);
        _db.SocialPostViews.RemoveRange(views);
        _db.SocialPostSaves.RemoveRange(saves);
        _db.SocialPostShares.RemoveRange(shares);
        _db.SocialPostReposts.RemoveRange(reposts);
        _db.SocialFollows.RemoveRange(follows);
        _db.SocialProfileVisits.RemoveRange(visits);
        await _db.SaveChangesAsync(cancellationToken);

        return SocialOperationResult<SocialAccountClosureDisposition>.Success(
            new SocialAccountClosureDisposition(postIds.Length, removableComments.Length, redactedComments.Length));
    }

    public async Task<SocialOperationResult<SocialMediaStream>> GetMediaAsync(
        SocialFeedActor actor,
        Guid mediaAssetId,
        CancellationToken cancellationToken = default)
        => await GetMediaStreamAsync(actor, mediaAssetId, includePreview: false, cancellationToken);

    public async Task<SocialOperationResult<SocialMediaStream>> GetMediaPreviewAsync(
        SocialFeedActor actor,
        Guid mediaAssetId,
        CancellationToken cancellationToken = default)
        => await GetMediaStreamAsync(actor, mediaAssetId, includePreview: true, cancellationToken);

    private async Task<SocialOperationResult<SocialMediaStream>> GetMediaStreamAsync(
        SocialFeedActor actor,
        Guid mediaAssetId,
        bool includePreview,
        CancellationToken cancellationToken)
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
            !SocialMediaProcessingStates.IsReady(media.ProcessingState))
        {
            return SocialOperationResult<SocialMediaStream>.Failure(
                "social_media_unavailable",
                "This media is not available to your mobile identity.");
        }

        var storageKey = includePreview
            ? media.ThumbnailStorageKey
            : media.StorageKey;
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return SocialOperationResult<SocialMediaStream>.Failure(
                "social_media_unavailable",
                "This media preview is not available to your mobile identity.");
        }

        var storedMedia = await _mediaStorage.OpenReadAsync(
            storageKey,
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
                includePreview ? "image/jpeg" : media.MimeType));
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
            var header = new byte[32];
            var read = 0;
            while (read < header.Length)
            {
                var bytesRead = await storedMedia.Content.ReadAsync(
                    header.AsMemory(read, header.Length - read),
                    cancellationToken);
                if (bytesRead == 0)
                    break;
                read += bytesRead;
            }

            if (read == 0)
                return false;

            return !string.Equals(
                       stored.MediaKind,
                       "Video",
                       StringComparison.OrdinalIgnoreCase) ||
                   HasIsoBaseMediaHeader(header.AsSpan(0, read));
        }
    }

    /// MP4 files begin with an ISO base media `ftyp` box. This lightweight
    /// validation deliberately happens before a video is marked Ready: storage
    /// reachability alone cannot prove that a supposed video is playable media.
    private static bool HasIsoBaseMediaHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 12 &&
        header[4] == (byte)'f' &&
        header[5] == (byte)'t' &&
        header[6] == (byte)'y' &&
        header[7] == (byte)'p';

    /// <summary>
    /// Stores the one optional Hac poster through the same media authority as
    /// primary uploads. Both draft creation and publication reuse this method;
    /// no second preview-storage route exists.
    /// </summary>
    private async Task<SocialOperationResult<SocialStoredMedia>> StoreHacPreviewAsync(
        SocialMediaUpload preview,
        CancellationToken cancellationToken)
    {
        var result = await _mediaStorage.StoreAsync(
            Guid.NewGuid(),
            preview.OriginalFileName ?? string.Empty,
            preview.DeclaredSizeBytes,
            preview.Content,
            cancellationToken);
        if (!result.Succeeded || result.Media is null ||
            !string.Equals(result.Media.MediaKind, "Image", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.Media.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            if (result.Media is not null)
                await DeleteStoredMediaAsync([result.Media.StorageKey], CancellationToken.None);

            return SocialOperationResult<SocialStoredMedia>.Failure(
                result.ErrorCode ?? "social_media_preview_invalid",
                result.ErrorMessage ?? "Legend could not store the selected Hac preview.");
        }

        if (!await CanReadStoredMediaAsync(result.Media, cancellationToken))
        {
            await DeleteStoredMediaAsync([result.Media.StorageKey], CancellationToken.None);
            return SocialOperationResult<SocialStoredMedia>.Failure(
                "social_media_storage_unavailable",
                "Legend could not verify the selected Hac preview in secure storage.");
        }

        return SocialOperationResult<SocialStoredMedia>.Success(result.Media);
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
        if (!IsCommunityTextAllowed(body, "SocialComment"))
            return ContentBlocked<SocialCommentView>();
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

    public async Task<SocialOperationResult<SocialFollowResult>> ToggleFollowAsync(
        SocialFollowCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<SocialFollowResult>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");

        var followedUserId = Normalize(command.FollowedUserId);
        var followedType = NormalizeParticipantType(command.FollowedParticipantType);
        if (string.IsNullOrWhiteSpace(followedUserId) || followedType is null ||
            (followedUserId == Normalize(command.Actor.Identity.UserId) && followedType == command.Actor.Identity.ParticipantType))
        {
            return SocialOperationResult<SocialFollowResult>.Failure("social_follow_invalid", "Choose another active Legend profile to follow.");
        }

        var activeAuthors = await GetActiveAuthorsAsync(command.Actor, cancellationToken);
        if (!activeAuthors.Contains(AuthorKey.From(followedUserId, followedType)))
        {
            return SocialOperationResult<SocialFollowResult>.Failure("social_follow_forbidden", "You can follow only active Legend profiles.");
        }

        var targetAuthors = await ResolveAuthorsAsync(
            [new AuthorReference(followedUserId, followedType, Guid.Empty)],
            cancellationToken);
        var target = targetAuthors.GetValueOrDefault(AuthorKey.From(followedUserId, followedType));
        if (target is null)
            return SocialOperationResult<SocialFollowResult>.Failure("social_follow_forbidden", "This Legend profile is not available.");

        var followerUserId = Normalize(command.Actor.Identity.UserId);
        followedUserId = target.UserId;
        var sourcePostId = command.SourcePostId;
        if (sourcePostId is { } candidateSourcePostId)
        {
            var sourcePost = await GetVisiblePostAsync(command.Actor, candidateSourcePostId, cancellationToken);
            if (sourcePost is null ||
                AuthorKey.From(sourcePost.AuthorUserId, sourcePost.AuthorParticipantType) !=
                AuthorKey.From(followedUserId, followedType))
            {
                return SocialOperationResult<SocialFollowResult>.Failure(
                    "social_follow_source_invalid",
                    "This follow must be attributed to a visible Legend post by that profile.");
            }
        }
        var targetUserIds = await AuthorUserIdFormsAsync(AuthorKey.From(followedUserId, followedType), cancellationToken);
        var existing = await _db.SocialFollows.SingleOrDefaultAsync(
            follow => follow.FollowerUserId == followerUserId &&
                      follow.FollowerParticipantType == command.Actor.Identity.ParticipantType &&
                      targetUserIds.Contains(follow.FollowedUserId) &&
                      follow.FollowedParticipantType == followedType,
            cancellationToken);
        if (existing is null)
        {
            var status = target.IsPrivate
                ? SocialFollowStatuses.Pending
                : SocialFollowStatuses.Accepted;
            _db.SocialFollows.Add(new SocialFollow
            {
                Id = Guid.NewGuid(),
                FollowerUserId = followerUserId,
                FollowerParticipantType = command.Actor.Identity.ParticipantType,
                FollowedUserId = followedUserId,
                FollowedParticipantType = followedType,
                SourceSocialPostId = sourcePostId,
                Status = status,
                CreatedUtc = DateTime.UtcNow,
                RespondedUtc = status == SocialFollowStatuses.Accepted ? DateTime.UtcNow : null
            });
            await _db.SaveChangesAsync(cancellationToken);
            InvalidateAudienceGraphCache(command.Actor.ProfileId, target.ProfileId);
            return SocialOperationResult<SocialFollowResult>.Success(
                new SocialFollowResult(status == SocialFollowStatuses.Accepted, status == SocialFollowStatuses.Pending));
        }

        _db.SocialFollows.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
        InvalidateAudienceGraphCache(command.Actor.ProfileId, target.ProfileId);
        return SocialOperationResult<SocialFollowResult>.Success(new SocialFollowResult(false, false));
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

    public async Task<SocialOperationResult<IReadOnlyList<SocialFollowRequestView>>> GetIncomingFollowRequestsAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
        {
            return SocialOperationResult<IReadOnlyList<SocialFollowRequestView>>.Failure(
                "social_actor_invalid", "Your mobile identity is not available for Legend updates.");
        }

        var targetKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var targetUserIds = await AuthorUserIdFormsAsync(targetKey, cancellationToken);
        var requests = await _db.SocialFollows.AsNoTracking()
            .Where(follow => targetUserIds.Contains(follow.FollowedUserId) &&
                             follow.FollowedParticipantType == targetKey.ParticipantType &&
                             follow.Status == SocialFollowStatuses.Pending)
            .OrderByDescending(follow => follow.CreatedUtc)
            .ToArrayAsync(cancellationToken);
        if (requests.Length == 0)
            return SocialOperationResult<IReadOnlyList<SocialFollowRequestView>>.Success(Array.Empty<SocialFollowRequestView>());

        var authors = await ResolveAuthorsAsync(
            requests.Select(request => new AuthorReference(request.FollowerUserId, request.FollowerParticipantType, Guid.Empty)),
            cancellationToken);
        return SocialOperationResult<IReadOnlyList<SocialFollowRequestView>>.Success(
            requests.Select(request => new SocialFollowRequestView(
                request.Id,
                authors.GetValueOrDefault(AuthorKey.From(request.FollowerUserId, request.FollowerParticipantType))
                    ?? ToUnknownAuthor(request.FollowerUserId, request.FollowerParticipantType, Guid.Empty),
                request.CreatedUtc)).ToArray());
    }

    public async Task<SocialOperationResult<SocialFollowResult>> DecideFollowRequestAsync(
        SocialFollowRequestDecisionCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(command.Actor, cancellationToken))
            return SocialOperationResult<SocialFollowResult>.Failure("social_actor_invalid", "Your mobile identity is not available for Legend updates.");
        if (command.FollowRequestId == Guid.Empty)
            return SocialOperationResult<SocialFollowResult>.Failure("social_follow_request_invalid", "Choose a valid follow request.");

        var targetKey = AuthorKey.From(command.Actor.Identity.UserId, command.Actor.Identity.ParticipantType);
        var targetUserIds = await AuthorUserIdFormsAsync(targetKey, cancellationToken);
        var request = await _db.SocialFollows.SingleOrDefaultAsync(follow =>
            follow.Id == command.FollowRequestId &&
            targetUserIds.Contains(follow.FollowedUserId) &&
            follow.FollowedParticipantType == targetKey.ParticipantType &&
            follow.Status == SocialFollowStatuses.Pending,
            cancellationToken);
        if (request is null)
            return SocialOperationResult<SocialFollowResult>.Failure("social_follow_request_unavailable", "This follow request is no longer available.");

        if (command.Approve)
        {
            request.Status = SocialFollowStatuses.Accepted;
            request.RespondedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var requester = await ResolveAuthorsAsync(
                [new AuthorReference(request.FollowerUserId, request.FollowerParticipantType, Guid.Empty)],
                cancellationToken);
            InvalidateAudienceGraphCache(
                command.Actor.ProfileId,
                requester.GetValueOrDefault(AuthorKey.From(request.FollowerUserId, request.FollowerParticipantType))?.ProfileId ?? Guid.Empty);
            return SocialOperationResult<SocialFollowResult>.Success(new SocialFollowResult(true, false));
        }

        _db.SocialFollows.Remove(request);
        await _db.SaveChangesAsync(cancellationToken);
        var declinedRequester = await ResolveAuthorsAsync(
            [new AuthorReference(request.FollowerUserId, request.FollowerParticipantType, Guid.Empty)],
            cancellationToken);
        InvalidateAudienceGraphCache(
            command.Actor.ProfileId,
            declinedRequester.GetValueOrDefault(AuthorKey.From(request.FollowerUserId, request.FollowerParticipantType))?.ProfileId ?? Guid.Empty);
        return SocialOperationResult<SocialFollowResult>.Success(new SocialFollowResult(false, false));
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
        var resolved = await ResolveNetworkProfileAsync(actor, profile, cancellationToken);
        if (!resolved.Succeeded || resolved.Value is null)
        {
            return SocialOperationResult<SocialProfileMetrics>.Failure(
                resolved.ErrorCode ?? "social_profile_unavailable",
                resolved.ErrorMessage ?? "This Legend profile is not available.");
        }

        var targetKey = resolved.Value.TargetKey;
        var actorKey = resolved.Value.ActorKey;
        var author = resolved.Value.Author;

        var targetUserIds = await AuthorUserIdFormsAsync(targetKey, cancellationToken);
        var now = DateTime.UtcNow;
        var posts = await _db.SocialPosts.AsNoTracking()
            .Where(post => targetUserIds.Contains(post.AuthorUserId) &&
                           post.AuthorParticipantType == targetKey.ParticipantType &&
                           post.PublicationState == SocialPostPublicationStates.Published &&
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

    private async Task<SocialOperationResult<NetworkProfileResolution>> ResolveNetworkProfileAsync(
        SocialFeedActor actor,
        SocialAuthor? profile,
        CancellationToken cancellationToken)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
        {
            return SocialOperationResult<NetworkProfileResolution>.Failure(
                "social_actor_invalid",
                "Your mobile identity is not available for Legend updates.");
        }

        var requested = profile ?? ToAuthor(actor);
        var targetKey = AuthorKey.From(requested.UserId, requested.ParticipantType);
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        if (targetKey != actorKey)
        {
            var active = await GetActiveAuthorsAsync(actor, cancellationToken);
            if (!active.Contains(targetKey))
            {
                return SocialOperationResult<NetworkProfileResolution>.Failure(
                    "social_profile_forbidden",
                    "This Legend profile is not available to your mobile identity.");
            }
        }

        var authors = await ResolveAuthorsAsync(
            [new AuthorReference(targetKey.UserId, targetKey.ParticipantType, requested.ProfileId)],
            cancellationToken);
        var author = authors.GetValueOrDefault(targetKey);
        if (author is null)
        {
            return SocialOperationResult<NetworkProfileResolution>.Failure(
                "social_profile_unavailable",
                "This Legend profile is not available.");
        }

        author = await ApplyMobileProfileDetailsAsync(author, cancellationToken);
        return SocialOperationResult<NetworkProfileResolution>.Success(
            new NetworkProfileResolution(targetKey, actorKey, author));
    }

    private async Task<SocialAuthor> ApplyMobileProfileDetailsAsync(
        SocialAuthor author,
        CancellationToken cancellationToken)
    {
        var mobileProfile = await _db.MobileProfileSettings.AsNoTracking()
            .SingleOrDefaultAsync(setting =>
                setting.ProfileId == author.ProfileId
                && setting.ParticipantType == author.ParticipantType,
                cancellationToken);
        if (mobileProfile is null)
            return author;

        return ApplyMobileProfileDetails(author, mobileProfile);
    }

    private sealed record NetworkProfileResolution(
        AuthorKey TargetKey,
        AuthorKey ActorKey,
        SocialAuthor Author);

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
                           post.PublicationState == SocialPostPublicationStates.Published &&
                           post.DeletedUtc == null)
            .OrderByDescending(post => post.PostedUtc)
            .Select(post => new { post.Id, post.ContentType, post.PostedUtc })
            .ToArrayAsync(cancellationToken);
        var metrics = await LoadPostMetricsAsync(posts.Select(post => post.Id).ToArray(), cancellationToken);
        var followers = await _db.SocialFollows.AsNoTracking().Where(follow =>
            follow.FollowedUserId == actorKey.UserId &&
            follow.FollowedParticipantType == actorKey.ParticipantType &&
            follow.Status == SocialFollowStatuses.Accepted).ToArrayAsync(cancellationToken);
        var followingCount = await _db.SocialFollows.AsNoTracking().CountAsync(follow =>
            follow.FollowerUserId == actorKey.UserId &&
            follow.FollowerParticipantType == actorKey.ParticipantType &&
            follow.Status == SocialFollowStatuses.Accepted,
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
            item.PublicationState == SocialPostPublicationStates.Published &&
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

        var active = await GetActiveAuthorsAsync(command.Actor, cancellationToken);
        if (!active.Contains(targetKey))
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

    /// <summary>
    /// Bounds a seven-day candidate pool without allowing public volume to push
    /// followed members out before the authoritative ranker sees their content.
    /// The direct-relationship slice is deliberately independent of the public
    /// discovery slice; output ordering is still decided only by the ranker.
    /// </summary>
    private async Task<SocialPost[]> LoadRankableCandidatesAsync(
        IQueryable<SocialPost> candidates,
        SocialFeedActor actor,
        AudienceGraph audience,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        var prioritizedProfileIds = audience.FollowedProfileIds
            .Append(actor.ProfileId)
            .Distinct()
            .ToArray();
        var relationshipCandidates = await candidates
            .Where(post => prioritizedProfileIds.Contains(post.AuthorProfileId))
            .OrderByDescending(post => post.PostedUtc)
            .Take(outputLimit)
            .ToArrayAsync(cancellationToken);
        var publicCandidates = await candidates
            .Where(post => !prioritizedProfileIds.Contains(post.AuthorProfileId))
            .OrderByDescending(post => post.PostedUtc)
            .Take(outputLimit * 4)
            .ToArrayAsync(cancellationToken);

        return relationshipCandidates
            .Concat(publicCandidates)
            .DistinctBy(post => post.Id)
            .ToArray();
    }

    /// <summary>
    /// The single ranking authority for home posts and the Hac FYP. It applies
    /// relationship tiers before engagement ranking: mutual follows, then accounts
    /// the member follows, always precede public discovery candidates. Within a tier,
    /// durable watch, interaction, profile-visit, community-engagement, and recency
    /// signals decide the order. The iOS app only renders this server authority.
    /// </summary>
    private async Task<IReadOnlyList<SocialPost>> RankFeedCandidatesForViewerAsync(
        IReadOnlyList<SocialPost> posts,
        SocialFeedActor actor,
        AudienceGraph audience,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (posts.Count == 0)
            return Array.Empty<SocialPost>();

        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var actorUserIds = await AuthorUserIdFormsAsync(actorKey, cancellationToken);
        var socialPosts = _db.SocialPosts
            .AsNoTracking()
            .Where(post => post.PublicationState == SocialPostPublicationStates.Published &&
                           post.DeletedUtc == null);
        var authorAffinity = new Dictionary<Guid, decimal>();

        void AddAffinity(Guid authorProfileId, decimal value)
        {
            if (authorProfileId == Guid.Empty || authorProfileId == actor.ProfileId)
                return;

            authorAffinity[authorProfileId] = authorAffinity.GetValueOrDefault(authorProfileId) + value;
        }

        var watchedPosts = await (
            from view in _db.SocialPostViews.AsNoTracking()
            join post in socialPosts on view.SocialPostId equals post.Id
            where actorUserIds.Contains(view.ViewerUserId) &&
                  view.ViewerParticipantType == actorKey.ParticipantType
            select new
            {
                post.AuthorProfileId,
                view.MaximumWatchDurationSeconds,
                view.MaximumWatchCompletionPercentage,
                view.LastViewedUtc
            }).OrderByDescending(view => view.LastViewedUtc)
            .Take(MaximumAffinitySignalsPerType)
            .ToArrayAsync(cancellationToken);
        foreach (var view in watchedPosts)
        {
            var completion = Math.Min(1m, (view.MaximumWatchCompletionPercentage ?? 0m) / 100m);
            var duration = Math.Min(2m, (view.MaximumWatchDurationSeconds ?? 0m) / 30m);
            var watchAffinity = completion < 0.20m
                ? -3m
                : 1m + completion * 8m + duration * 2m;
            AddAffinity(view.AuthorProfileId, watchAffinity);
        }

        var reactions = await (
            from reaction in _db.SocialPostReactions.AsNoTracking()
            join post in socialPosts on reaction.SocialPostId equals post.Id
            where actorUserIds.Contains(reaction.ActorUserId) &&
                  reaction.ActorParticipantType == actorKey.ParticipantType
            select new { post.AuthorProfileId, reaction.CreatedUtc })
            .OrderByDescending(reaction => reaction.CreatedUtc)
            .Take(MaximumAffinitySignalsPerType)
            .ToArrayAsync(cancellationToken);
        foreach (var reaction in reactions)
            AddAffinity(reaction.AuthorProfileId, 5m);

        var comments = await (
            from comment in _db.SocialPostComments.AsNoTracking()
            join post in socialPosts on comment.SocialPostId equals post.Id
            where actorUserIds.Contains(comment.AuthorUserId) &&
                  comment.AuthorParticipantType == actorKey.ParticipantType &&
                  comment.DeletedUtc == null
            select new { post.AuthorProfileId, comment.CreatedUtc })
            .OrderByDescending(comment => comment.CreatedUtc)
            .Take(MaximumAffinitySignalsPerType)
            .ToArrayAsync(cancellationToken);
        foreach (var comment in comments)
            AddAffinity(comment.AuthorProfileId, 8m);

        var saves = await (
            from save in _db.SocialPostSaves.AsNoTracking()
            join post in socialPosts on save.SocialPostId equals post.Id
            where actorUserIds.Contains(save.ActorUserId) &&
                  save.ActorParticipantType == actorKey.ParticipantType
            select new { post.AuthorProfileId, save.CreatedUtc })
            .OrderByDescending(save => save.CreatedUtc)
            .Take(MaximumAffinitySignalsPerType)
            .ToArrayAsync(cancellationToken);
        foreach (var save in saves)
            AddAffinity(save.AuthorProfileId, 10m);

        var reposts = await (
            from repost in _db.SocialPostReposts.AsNoTracking()
            join post in socialPosts on repost.SocialPostId equals post.Id
            where actorUserIds.Contains(repost.ActorUserId) &&
                  repost.ActorParticipantType == actorKey.ParticipantType
            select new { post.AuthorProfileId, repost.CreatedUtc })
            .OrderByDescending(repost => repost.CreatedUtc)
            .Take(MaximumAffinitySignalsPerType)
            .ToArrayAsync(cancellationToken);
        foreach (var repost in reposts)
            AddAffinity(repost.AuthorProfileId, 12m);

        var shares = await (
            from share in _db.SocialPostShares.AsNoTracking()
            join post in socialPosts on share.SocialPostId equals post.Id
            where actorUserIds.Contains(share.ActorUserId) &&
                  share.ActorParticipantType == actorKey.ParticipantType
            select new { post.AuthorProfileId, share.CreatedUtc })
            .OrderByDescending(share => share.CreatedUtc)
            .Take(MaximumAffinitySignalsPerType)
            .ToArrayAsync(cancellationToken);
        foreach (var share in shares)
            AddAffinity(share.AuthorProfileId, 8m);

        var profileVisits = await _db.SocialProfileVisits
            .AsNoTracking()
            .Where(visit => actorUserIds.Contains(visit.VisitorUserId) &&
                            visit.VisitorParticipantType == actorKey.ParticipantType)
            .OrderByDescending(visit => visit.LastVisitedUtc)
            .Take(MaximumAffinitySignalsPerType)
            .Select(visit => new AuthorReference(
                visit.TargetUserId,
                visit.TargetParticipantType,
                Guid.Empty))
            .ToArrayAsync(cancellationToken);
        if (profileVisits.Length > 0)
        {
            var visitedAuthors = await ResolveAuthorsAsync(profileVisits, cancellationToken);
            foreach (var visit in profileVisits)
            {
                var author = visitedAuthors.GetValueOrDefault(
                    AuthorKey.From(visit.UserId, visit.ParticipantType));
                if (author is not null)
                    AddAffinity(author.ProfileId, 4m);
            }
        }

        var metricsByPost = await LoadPostMetricsAsync(
            posts.Select(post => post.Id).ToArray(),
            cancellationToken);

        decimal Score(SocialPost post)
        {
            var metrics = metricsByPost[post.Id];
            var affinity = authorAffinity.GetValueOrDefault(post.AuthorProfileId);
            var engagement = Math.Min(
                40m,
                metrics.ReactionCount * 2m +
                metrics.CommentCount * 3m +
                metrics.SaveCount * 4m +
                metrics.RepostCount * 5m +
                metrics.ShareCount * 3m);
            var watchCompletion = Math.Min(
                4m,
                (metrics.AverageWatchCompletionPercentage ?? 0m) / 25m);
            var ageHours = Math.Max(0d, (now - post.PostedUtc).TotalHours);
            var timeDecay = (decimal)(1d / Math.Pow(ageHours + 2d, 1.5d));
            var rawScore = 1m + affinity * 10m + engagement + watchCompletion;

            return rawScore * timeDecay;
        }

        return posts
            .OrderByDescending(post => RelationshipTier(post, actor, audience))
            .ThenByDescending(Score)
            .ThenByDescending(post => post.PostedUtc)
            .ToArray();
    }

    private static int RelationshipTier(
        SocialPost post,
        SocialFeedActor actor,
        AudienceGraph audience) =>
        post.AuthorProfileId == actor.ProfileId
            ? 3
            : audience.MutualFollowedProfileIds.Contains(post.AuthorProfileId)
                ? 2
                : audience.FollowedProfileIds.Contains(post.AuthorProfileId)
                    ? 1
                    : 0;

    private async Task<SocialPost?> GetVisiblePostAsync(
        SocialFeedActor actor,
        Guid postId,
        CancellationToken cancellationToken)
    {
        if (postId == Guid.Empty)
            return null;

        var post = await _db.SocialPosts.SingleOrDefaultAsync(
            item => item.Id == postId && item.DeletedUtc == null &&
                    item.PublicationState == SocialPostPublicationStates.Published &&
                    (item.ExpiresUtc == null || item.ExpiresUtc > DateTime.UtcNow) &&
                    !item.MediaAssets.Any(media =>
                        media.ProcessingState != SocialMediaProcessingStates.Ready),
            cancellationToken);
        if (post is null)
            return null;

        var activeAuthors = await GetActiveAuthorsAsync(actor, cancellationToken);
        if (!activeAuthors.Contains(AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType)))
            return null;

        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var audience = await LoadAudienceGraphAsync(actor, cancellationToken);
        return (await FilterPostsForViewerAsync([post], actorKey, audience, cancellationToken)).SingleOrDefault();
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
            .Where(follow => follow.SourceSocialPostId != null &&
                             follow.Status == SocialFollowStatuses.Accepted &&
                             ids.Contains(follow.SourceSocialPostId.Value))
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
            PreviewUrl = resolved.Value.AudioUrl,
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

    private async Task<IReadOnlyList<SocialPromotedGroupView>> GetPromotedGroupsAsync(
        SocialFeedActor actor,
        IReadOnlySet<AuthorKey> activeAuthors,
        CancellationToken cancellationToken)
    {
        var founderObjectId = FounderAuthority.GetConfiguredObjectId(
            _configuredFounderOid);
        if (founderObjectId is null)
            return Array.Empty<SocialPromotedGroupView>();

        var rows = await _db.MessageConversations
            .AsNoTracking()
            .Where(conversation =>
                conversation.ConversationType == MessagingConversationTypes.Group &&
                conversation.Purpose == null &&
                !conversation.IsClosed &&
                conversation.IsPromoted &&
                conversation.PromotionStartedUtc != null &&
                (conversation.OwnerParticipantType == MessagingParticipantTypes.Agent ||
                 conversation.OwnerParticipantType == MessagingParticipantTypes.Client) &&
                conversation.OwnerUserId != null &&
                conversation.OwnerUserId.ToLower() == founderObjectId)
            .OrderByDescending(conversation => conversation.PromotionStartedUtc)
            .ThenBy(conversation => conversation.Id)
            .Select(conversation => new PromotedGroupRow(
                conversation.Id,
                conversation.Subject,
                conversation.OwnerUserId!,
                conversation.OwnerParticipantType!,
                conversation.GroupImageContent,
                conversation.GroupImageContentType,
                conversation.Participants.Count(participant => participant.IsActive),
                conversation.PromotionStartedUtc!.Value))
            .ToArrayAsync(cancellationToken);
        if (rows.Length == 0)
            return Array.Empty<SocialPromotedGroupView>();

        var visibleRows = rows
            .Where(row => activeAuthors.Contains(AuthorKey.From(row.OwnerUserId, row.OwnerParticipantType)))
            .ToArray();
        if (visibleRows.Length == 0)
            return Array.Empty<SocialPromotedGroupView>();

        var owners = await ResolveAuthorsAsync(
            visibleRows.Select(row => new AuthorReference(
                row.OwnerUserId,
                row.OwnerParticipantType,
                Guid.Empty)),
            cancellationToken);
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var actorUserIds = await AuthorUserIdFormsAsync(actorKey, cancellationToken);
        var visibleConversationIds = visibleRows.Select(row => row.ConversationId).ToArray();
        var joinedConversationIds = await _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(participant =>
                participant.IsActive &&
                participant.ParticipantType == actorKey.ParticipantType &&
                actorUserIds.Contains(participant.UserId.ToLower()) &&
                visibleConversationIds.Contains(participant.ConversationId))
            .Select(participant => participant.ConversationId)
            .ToHashSetAsync(cancellationToken);

        return visibleRows
            .Select(row =>
            {
                var ownerKey = AuthorKey.From(row.OwnerUserId, row.OwnerParticipantType);
                return owners.TryGetValue(ownerKey, out var owner)
                    ? new SocialPromotedGroupView(
                        row.ConversationId,
                        FirstNonEmpty(row.Subject, "Legend group"),
                        owner,
                        ToPromotedGroupImage(row.GroupImageContent, row.GroupImageContentType),
                        row.ActiveMemberCount,
                        joinedConversationIds.Contains(row.ConversationId),
                        row.PromotionStartedUtc)
                    : null;
            })
            .OfType<SocialPromotedGroupView>()
            .ToArray();
    }

    private static MessagingGroupImage? ToPromotedGroupImage(byte[]? content, string? contentType) =>
        content is { Length: > 0 } && !string.IsNullOrWhiteSpace(contentType)
            ? new MessagingGroupImage(content, contentType)
            : null;

    private bool IsCommunityTextAllowed(string? content, string surface) =>
        _moderation.Evaluate(content, surface).IsAllowed;

    private static SocialOperationResult<T> ContentBlocked<T>() =>
        SocialOperationResult<T>.Failure("social_content_blocked", RespectfulCommunityMessage);

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
                           (profile.CrmStatus == null || profile.CrmStatus == "" || profile.CrmStatus == "Active") &&
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
        var materialized = posts
            .GroupBy(post => post.Id)
            .Select(group => group.First())
            .ToArray();

        if (materialized.Length == 0)
            return Array.Empty<SocialPostView>();

        var postIds = materialized
            .Select(post => post.Id)
            .ToArray();

        var comments = await _db.SocialPostComments
            .AsNoTracking()
            .Where(comment =>
                postIds.Contains(comment.SocialPostId) &&
                comment.DeletedUtc == null)
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

        var metricsByPost = await LoadPostMetricsAsync(
            postIds,
            cancellationToken);

        var musicByPost = await _db.SocialPostMusicAttachments
            .AsNoTracking()
            .Where(music => postIds.Contains(music.SocialPostId))
            .ToDictionaryAsync(
                music => music.SocialPostId,
                cancellationToken);

        var actorKey = AuthorKey.From(
            actor.Identity.UserId,
            actor.Identity.ParticipantType);

        var follows = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow =>
                follow.FollowerUserId == actorKey.UserId &&
                follow.FollowerParticipantType == actorKey.ParticipantType)
            .ToListAsync(cancellationToken);

        var savedPostIds = await _db.SocialPostSaves
            .AsNoTracking()
            .Where(save =>
                postIds.Contains(save.SocialPostId) &&
                save.ActorUserId == actorKey.UserId &&
                save.ActorParticipantType == actorKey.ParticipantType)
            .Select(save => save.SocialPostId)
            .ToHashSetAsync(cancellationToken);

        var repostedPostIds = await _db.SocialPostReposts
            .AsNoTracking()
            .Where(repost =>
                postIds.Contains(repost.SocialPostId) &&
                repost.ActorUserId == actorKey.UserId &&
                repost.ActorParticipantType == actorKey.ParticipantType)
            .Select(repost => repost.SocialPostId)
            .ToHashSetAsync(cancellationToken);

        var authors = await ResolveAuthorsAsync(
            materialized
                .Select(post => new AuthorReference(
                    post.AuthorUserId,
                    post.AuthorParticipantType,
                    post.AuthorProfileId))
                .Concat(comments.Select(comment => new AuthorReference(
                    comment.AuthorUserId,
                    comment.AuthorParticipantType,
                    comment.AuthorProfileId))),
            cancellationToken);

        // Index the authoritative query results once. The former implementation
        // repeatedly scanned the complete comment/reaction/media/follow arrays
        // for every post being projected.
        var commentsByPost = comments
            .GroupBy(comment => comment.SocialPostId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var reactionsByPost = reactions
            .GroupBy(reaction => reaction.SocialPostId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var mediaByPost = mediaAssets
            .GroupBy(media => media.SocialPostId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(media => media.DisplayOrder)
                    .ToArray());

        var acceptedFollowAuthors = follows
            .Where(follow =>
                follow.Status == SocialFollowStatuses.Accepted)
            .Select(follow => AuthorKey.From(
                follow.FollowedUserId,
                follow.FollowedParticipantType))
            .ToHashSet();

        var pendingFollowAuthors = follows
            .Where(follow =>
                follow.Status == SocialFollowStatuses.Pending)
            .Select(follow => AuthorKey.From(
                follow.FollowedUserId,
                follow.FollowedParticipantType))
            .ToHashSet();

        return materialized
            .Select(post =>
            {
                var postComments =
                    commentsByPost.GetValueOrDefault(post.Id) ?? [];

                var postReactions =
                    reactionsByPost.GetValueOrDefault(post.Id) ?? [];

                var postMedia =
                    mediaByPost.GetValueOrDefault(post.Id) ?? [];

                var postAuthorKey = AuthorKey.From(
                    post.AuthorUserId,
                    post.AuthorParticipantType);

                var visibleComments = postComments
                    .OrderBy(comment => comment.CreatedUtc)
                    .TakeLast(MaximumCommentsPerPost)
                    .Select(comment => new SocialCommentView(
                        comment.Id,
                        authors.GetValueOrDefault(
                            AuthorKey.From(
                                comment.AuthorUserId,
                                comment.AuthorParticipantType))
                            ?? ToUnknownAuthor(
                                comment.AuthorUserId,
                                comment.AuthorParticipantType,
                                comment.AuthorProfileId),
                        comment.ParentCommentId,
                        comment.Body,
                        comment.CreatedUtc))
                    .ToArray();

                return new SocialPostView(
                    post.Id,
                    authors.GetValueOrDefault(postAuthorKey)
                        ?? ToUnknownAuthor(
                            post.AuthorUserId,
                            post.AuthorParticipantType,
                            post.AuthorProfileId),
                    post.ContentType,
                    post.Body,
                    post.Audience,
                    post.Location,
                    post.CommentsEnabled,
                    post.PostedUtc,
                    post.ExpiresUtc,
                    postReactions.Length,
                    postComments.Length,
                    postReactions.Any(reaction =>
                        AuthorKey.From(
                            reaction.ActorUserId,
                            reaction.ActorParticipantType) == actorKey),
                    acceptedFollowAuthors.Contains(postAuthorKey),
                    pendingFollowAuthors.Contains(postAuthorKey),
                    savedPostIds.Contains(post.Id),
                    repostedPostIds.Contains(post.Id),
                    metricsByPost[post.Id],
                    musicByPost.TryGetValue(
                        post.Id,
                        out var music)
                        ? ToMusicView(music)
                        : null,
                    postMedia
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
                            media.AccessibilityText,
                            !string.IsNullOrWhiteSpace(
                                media.ThumbnailStorageKey)))
                        .ToArray(),
                    visibleComments);
            })
            .ToArray();
    }

    private async Task<SocialPostView> BuildPostViewAsync(SocialPost post, SocialFeedActor actor, CancellationToken cancellationToken) =>
        (await BuildPostViewsAsync([post], actor, cancellationToken)).Single();

    private async Task<IReadOnlyList<SocialActivityView>> GetActivityAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken)
    {
        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var actorUserIds = await AuthorUserIdFormsAsync(actorKey, cancellationToken);
        var ownPostIds = await _db.SocialPosts
            .AsNoTracking()
            .Where(post => actorUserIds.Contains(post.AuthorUserId) &&
                           post.AuthorParticipantType == actorKey.ParticipantType &&
                           post.PublicationState == SocialPostPublicationStates.Published &&
                           post.DeletedUtc == null)
            .Select(post => post.Id)
            .ToListAsync(cancellationToken);
        if (ownPostIds.Count == 0)
            return Array.Empty<SocialActivityView>();

        var reactions = await _db.SocialPostReactions
            .AsNoTracking()
            .Where(reaction => ownPostIds.Contains(reaction.SocialPostId) &&
                               (!actorUserIds.Contains(reaction.ActorUserId) ||
                                reaction.ActorParticipantType != actorKey.ParticipantType))
            .OrderByDescending(reaction => reaction.CreatedUtc)
            .Take(MaximumActivityItems)
            .ToListAsync(cancellationToken);
        var comments = await _db.SocialPostComments
            .AsNoTracking()
            .Where(comment => ownPostIds.Contains(comment.SocialPostId) && comment.DeletedUtc == null &&
                              (!actorUserIds.Contains(comment.AuthorUserId) ||
                               comment.AuthorParticipantType != actorKey.ParticipantType))
            .OrderByDescending(comment => comment.CreatedUtc)
            .Take(MaximumActivityItems)
            .ToListAsync(cancellationToken);
        var follows = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow => actorUserIds.Contains(follow.FollowedUserId) &&
                             follow.FollowedParticipantType == actorKey.ParticipantType &&
                             follow.Status == SocialFollowStatuses.Accepted)
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
    /// The authoritative follow graph for visibility and ranking. Profile IDs
    /// deliberately back the tiers so legacy and Entra identity forms resolve to
    /// the same relationship rather than producing a second ranking path.
    /// </summary>
    private sealed record AudienceGraph(
        FrozenSet<Guid> FollowedProfileIds,
        FrozenSet<Guid> MutualFollowedProfileIds);

    private async Task<AudienceGraph> LoadAudienceGraphAsync(
        SocialFeedActor actor,
        CancellationToken cancellationToken)
    {
        var cacheKey = AudienceGraphCacheKey(actor.ProfileId);
        if (_memoryCache.TryGetValue(cacheKey, out AudienceGraph? cached) && cached is not null)
            return cached;

        var actorKey = AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType);
        var audience = await BuildAudienceGraphAsync(actorKey, cancellationToken);
        _memoryCache.Set(cacheKey, audience, AudienceGraphCacheDuration);
        return audience;
    }

    private async Task<AudienceGraph> BuildAudienceGraphAsync(
        AuthorKey actorKey,
        CancellationToken cancellationToken)
    {
        var actorUserIds = await AuthorUserIdFormsAsync(actorKey, cancellationToken);
        var followedByActor = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow => actorUserIds.Contains(follow.FollowerUserId) &&
                             follow.FollowerParticipantType == actorKey.ParticipantType &&
                             follow.Status == SocialFollowStatuses.Accepted)
            .Select(follow => new { follow.FollowedUserId, follow.FollowedParticipantType })
            .ToArrayAsync(cancellationToken);

        var followersOfActor = await _db.SocialFollows
            .AsNoTracking()
            .Where(follow => actorUserIds.Contains(follow.FollowedUserId) &&
                             follow.FollowedParticipantType == actorKey.ParticipantType &&
                             follow.Status == SocialFollowStatuses.Accepted)
            .Select(follow => new { follow.FollowerUserId, follow.FollowerParticipantType })
            .ToArrayAsync(cancellationToken);
        var relationReferences = followedByActor
            .Select(follow => new AuthorReference(
                follow.FollowedUserId,
                follow.FollowedParticipantType,
                Guid.Empty))
            .Concat(followersOfActor.Select(follow => new AuthorReference(
                follow.FollowerUserId,
                follow.FollowerParticipantType,
                Guid.Empty)))
            .ToArray();
        var authors = await ResolveAuthorsAsync(relationReferences, cancellationToken);
        var followedProfileIds = followedByActor
            .Select(follow => authors.GetValueOrDefault(
                AuthorKey.From(follow.FollowedUserId, follow.FollowedParticipantType))?.ProfileId ?? Guid.Empty)
            .Where(profileId => profileId != Guid.Empty)
            .ToHashSet();
        var followerProfileIds = followersOfActor
            .Select(follow => authors.GetValueOrDefault(
                AuthorKey.From(follow.FollowerUserId, follow.FollowerParticipantType))?.ProfileId ?? Guid.Empty)
            .Where(profileId => profileId != Guid.Empty)
            .ToHashSet();

        return new AudienceGraph(
            followedProfileIds.ToFrozenSet(),
            followedProfileIds.Intersect(followerProfileIds).ToFrozenSet());
    }

    private static string AudienceGraphCacheKey(Guid profileId) =>
        $"{AudienceGraphCachePrefix}:{profileId:N}";

    private void InvalidateAudienceGraphCache(params Guid[] profileIds)
    {
        foreach (var profileId in profileIds.Where(profileId => profileId != Guid.Empty).Distinct())
            _memoryCache.Remove(AudienceGraphCacheKey(profileId));
    }

    private async Task<SocialPost[]> FilterPostsForViewerAsync(
        IEnumerable<SocialPost> posts,
        AuthorKey actorKey,
        AudienceGraph audience,
        CancellationToken cancellationToken)
    {
        var materialized = posts.ToArray();
        if (materialized.Length == 0)
            return Array.Empty<SocialPost>();

        var privateProfileIds = await _db.MobileProfileSettings.AsNoTracking()
            .Where(setting => setting.IsPrivate &&
                              materialized.Select(post => post.AuthorProfileId).Contains(setting.ProfileId))
            .Select(setting => setting.ProfileId)
            .ToHashSetAsync(cancellationToken);

        return materialized.Where(post =>
        {
            if (AuthorKey.From(post.AuthorUserId, post.AuthorParticipantType) == actorKey ||
                !privateProfileIds.Contains(post.AuthorProfileId))
                return true;

            return audience.FollowedProfileIds.Contains(post.AuthorProfileId);
        }).ToArray();
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
            : LogicalParticipantIdentity.ClientUserIdForms(
                profile.ClientUserId,
                profile.ExternalIdentityObjectId);
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
                                 follow.FollowerParticipantType == profileKey.ParticipantType &&
                                 follow.Status == SocialFollowStatuses.Accepted)
                .OrderByDescending(follow => follow.CreatedUtc)
                .Select(follow => new FollowListReference(
                    follow.FollowedUserId,
                    follow.FollowedParticipantType,
                    follow.CreatedUtc))
                .ToArrayAsync(cancellationToken)
            : await _db.SocialFollows
                .AsNoTracking()
                .Where(follow => profileUserIds.Contains(follow.FollowedUserId) &&
                                 follow.FollowedParticipantType == profileKey.ParticipantType &&
                                 follow.Status == SocialFollowStatuses.Accepted)
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
                             follow.FollowerParticipantType == viewerKey.ParticipantType &&
                             follow.Status == SocialFollowStatuses.Accepted)
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

    private static AuthorKey CanonicalAuthorKey(
        AuthorReference reference,
        IReadOnlyDictionary<AuthorKey, SocialAuthor> authors)
    {
        var raw = AuthorKey.From(reference.UserId, reference.ParticipantType);
        return authors.TryGetValue(raw, out var author)
            ? AuthorKey.From(author.UserId, author.ParticipantType)
            : raw;
    }

    private async Task<HashSet<AuthorKey>> GetActiveAuthorsAsync(SocialFeedActor actor, CancellationToken cancellationToken)
    {
        var active = new HashSet<AuthorKey>
        {
            AuthorKey.From(actor.Identity.UserId, actor.Identity.ParticipantType)
        };

        var agents = await _db.AgentProfiles.AsNoTracking()
            .Where(profile => profile.IsActive)
            .Select(profile => profile.AgentUserId)
            .ToArrayAsync(cancellationToken);
        foreach (var userId in agents)
            active.Add(AuthorKey.From(userId, MessagingParticipantTypes.Agent));

        var clients = await _db.ClientProfiles.AsNoTracking()
            .Where(profile => profile.CrmStatus == null || profile.CrmStatus == "" || profile.CrmStatus == "Active")
            .Select(profile => new { profile.ClientUserId, profile.ExternalIdentityObjectId })
            .ToArrayAsync(cancellationToken);
        foreach (var client in clients)
        {
            foreach (var userId in LogicalParticipantIdentity.ClientUserIdForms(
                         client.ClientUserId,
                         client.ExternalIdentityObjectId))
                active.Add(AuthorKey.From(userId, MessagingParticipantTypes.Client));
        }

        if (_communitySafety is not null)
        {
            var blocked = await _communitySafety.GetBlockedParticipantsAsync(actor.Identity, cancellationToken);
            foreach (var participant in blocked)
            {
                foreach (var userId in participant.UserIdForms)
                    active.Remove(AuthorKey.From(userId, participant.ParticipantType));
            }
        }

        return active;
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
                .Select(profile => new { profile.Id, profile.AgentUserId, profile.FullName, profile.AgentUpn, profile.Title, profile.Phone })
                .ToListAsync(cancellationToken);
            foreach (var profile in agents)
            {
                var name = FirstNonEmpty(profile.FullName, profile.AgentUpn, "Agent");
                result[AuthorKey.From(profile.AgentUserId, MessagingParticipantTypes.Agent)] = new SocialAuthor(
                    Normalize(profile.AgentUserId),
                    MessagingParticipantTypes.Agent,
                    profile.Id,
                    name,
                    RoleLabel: AgentProfileIdentity.LegendRoleLabel(profile.Title),
                    Phone: profile.Phone);
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
                .Select(profile => new { profile.Id, profile.ClientUserId, profile.ExternalIdentityObjectId, profile.FirstName, profile.LastName, profile.Email, profile.Phone })
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
                    canonicalUserId, MessagingParticipantTypes.Client, profile.Id, name,
                    Phone: profile.Phone);

                foreach (var identityForm in LogicalParticipantIdentity.ClientUserIdForms(
                             profile.ClientUserId,
                             profile.ExternalIdentityObjectId))
                    result[AuthorKey.From(identityForm, MessagingParticipantTypes.Client)] = author;
            }
        }

        var profileIds = result.Values
            .Select(author => author.ProfileId)
            .Where(profileId => profileId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (profileIds.Length == 0)
            return result;

        var settings = await _db.MobileProfileSettings.AsNoTracking()
            .Where(setting => profileIds.Contains(setting.ProfileId))
            .ToDictionaryAsync(setting => setting.ProfileId, cancellationToken);
        foreach (var key in result.Keys.ToArray())
        {
            if (settings.TryGetValue(result[key].ProfileId, out var setting))
                result[key] = ApplyMobileProfileDetails(result[key], setting);
        }

        return result;
    }

    private static SocialAuthor ApplyMobileProfileDetails(
        SocialAuthor author,
        MobileProfileSettings mobileProfile) => author with
    {
        Username = mobileProfile.Username,
        Bio = mobileProfile.Bio,
        Website = mobileProfile.Website,
        Location = mobileProfile.Location,
        PublicEmail = mobileProfile.IsEmailVisible ? mobileProfile.PublicEmail : null,
        PublicPhone = mobileProfile.IsPhoneVisible ? author.Phone : null,
        IsPrivate = mobileProfile.IsPrivate
    };

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

    private static bool IsValidHacPreview(
        string contentType,
        SocialMediaUpload? previewImage) =>
        previewImage is null ||
        (contentType == SocialPostContentTypes.Reel &&
         previewImage.DeclaredSizeBytes > 0 &&
         previewImage.DeclaredSizeBytes <= SocialMediaUploadLimits.MaximumPreviewImageBytes &&
         string.Equals(
             Path.GetExtension(previewImage.OriginalFileName?.Trim() ?? string.Empty),
             ".jpg",
             StringComparison.OrdinalIgnoreCase));

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

    private sealed record PromotedGroupRow(
        Guid ConversationId,
        string? Subject,
        string OwnerUserId,
        string OwnerParticipantType,
        byte[]? GroupImageContent,
        string? GroupImageContentType,
        int ActiveMemberCount,
        DateTime PromotionStartedUtc);
}
