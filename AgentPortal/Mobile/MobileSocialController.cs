using Domain.Messaging;
using Domain.Social;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Mobile;

[ApiController]
[Route("api/v1/mobile/social")]
[Authorize(Policy = MobileApiAuthorization.PolicyName)]
[IgnoreAntiforgeryToken]
[TypeFilter(typeof(MobileApiExceptionFilter))]
public sealed class MobileSocialController : MobileApiControllerBase
{
    private readonly ISocialFeedService _social;
    private readonly IMessagingProfileImageResolver _profiles;
    private readonly MasterAppDbContext? _db;

    public MobileSocialController(
        IMobileActorResolver actorResolver,
        ISocialFeedService social,
        IMessagingProfileImageResolver profiles,
        MasterAppDbContext? db = null)
        : base(actorResolver)
    {
        _social = social;
        _profiles = profiles;
        _db = db;
    }

    [HttpGet("feed")]
    public async Task<IActionResult> Feed(CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.GetFeedAsync(resolved.Actor!, cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToSnapshotDtoAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("profile/posts")]
    public async Task<IActionResult> CurrentProfilePosts(CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.GetCurrentProfilePostsAsync(resolved.Actor!, cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToPostDtosAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("profiles/posts")]
    public async Task<IActionResult> PublicProfilePosts(
        [FromQuery] string? userId,
        [FromQuery] string? participantType,
        [FromQuery] Guid? profileId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(participantType))
        {
            return SocialFailure(
                "social_profile_invalid",
                "Choose a Legend profile to open.");
        }

        var result = await _social.GetPublicProfilePostsAsync(
            resolved.Actor!,
            new SocialAuthor(userId, participantType, profileId.GetValueOrDefault(), string.Empty),
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToPostDtosAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("profile/follows")]
    public async Task<IActionResult> CurrentProfileFollows(
        [FromQuery] string? list,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.GetCurrentProfileFollowListAsync(
            resolved.Actor!,
            list ?? string.Empty,
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToFollowListDtosAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("profile/follow-requests")]
    public async Task<IActionResult> IncomingFollowRequests(CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.GetIncomingFollowRequestsAsync(resolved.Actor!, cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToFollowRequestDtosAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("posts")]
    public async Task<IActionResult> CreatePost(
        [FromBody] MobileCreateSocialPostRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.CreatePostAsync(
            new CreateSocialPostCommand(
                resolved.Actor!,
                request?.ContentType ?? string.Empty,
                request?.Body ?? string.Empty,
                new SocialPostDetails(
                    request?.Audience,
                    request?.Location,
                    request?.CommentsEnabled ?? true)),
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToPostDtoAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPut("posts/{postId:guid}")]
    public async Task<IActionResult> UpdatePost(
        Guid postId,
        [FromBody] MobileUpdateSocialPostRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.UpdatePostAsync(
            new UpdateSocialPostCommand(resolved.Actor!, postId, request?.Body ?? string.Empty),
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToPostDtoAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpDelete("posts/{postId:guid}")]
    public async Task<IActionResult> DeletePost(Guid postId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.DeletePostAsync(
            new SocialPostMutationCommand(resolved.Actor!, postId),
            cancellationToken);
        return result.Succeeded
            ? NoContent()
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("posts/media")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(SocialMediaUploadLimits.MaximumMultipartRequestBytes)]
    [RequestFormLimits(
        MultipartBodyLengthLimit = SocialMediaUploadLimits.MaximumMultipartRequestBytes,
        ValueLengthLimit = SocialMediaUploadLimits.MaximumFormValueLength)]
    public async Task<IActionResult> CreateMediaPost(
        [FromForm] MobileCreateSocialMediaPostRequest? request,
        CancellationToken cancellationToken)
        => await CreateMediaPostCore(request, publishImmediately: true, cancellationToken);

    /// <summary>
    /// Accepts a durable, non-public media draft while the member is still
    /// editing. It intentionally shares the exact validation and persistence
    /// path used by direct publishing below.
    /// </summary>
    [HttpPost("posts/media/stage")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(SocialMediaUploadLimits.MaximumMultipartRequestBytes)]
    [RequestFormLimits(
        MultipartBodyLengthLimit = SocialMediaUploadLimits.MaximumMultipartRequestBytes,
        ValueLengthLimit = SocialMediaUploadLimits.MaximumFormValueLength)]
    public async Task<IActionResult> StageMediaPost(
        [FromForm] MobileCreateSocialMediaPostRequest? request,
        CancellationToken cancellationToken)
        => await CreateMediaPostCore(request, publishImmediately: false, cancellationToken);

    [HttpPost("posts/{postId:guid}/publish")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(SocialMediaUploadLimits.MaximumPreviewImageBytes + SocialMediaUploadLimits.MultipartEnvelopeBytes)]
    [RequestFormLimits(
        MultipartBodyLengthLimit = SocialMediaUploadLimits.MaximumPreviewImageBytes + SocialMediaUploadLimits.MultipartEnvelopeBytes,
        ValueLengthLimit = SocialMediaUploadLimits.MaximumFormValueLength)]
    public async Task<IActionResult> PublishStagedMediaPost(
        Guid postId,
        [FromForm] MobilePublishStagedSocialMediaPostRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var preview = request?.Preview;
        await using var previewStream = preview is { Length: > 0 }
            ? preview.OpenReadStream()
            : null;
        var previewUpload = previewStream is null
            ? null
            : new SocialMediaUpload(
                preview!.FileName,
                preview.Length,
                previewStream,
                null);

        var result = await _social.PublishStagedMediaPostAsync(
            new PublishStagedSocialMediaPostCommand(
                resolved.Actor!,
                postId,
                request?.Body ?? string.Empty,
                ToMusicSelection(request),
                new SocialPostDetails(
                    request?.Audience,
                    request?.Location,
                    request?.CommentsEnabled ?? true),
                previewUpload),
            cancellationToken);

        return result.Succeeded && result.Value is not null
            ? StatusCode(
                StatusCodes.Status202Accepted,
                await ToPostDtoAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    private async Task<IActionResult> CreateMediaPostCore(
        MobileCreateSocialMediaPostRequest? request,
        bool publishImmediately,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var files = request?.Files?
            .Where(file => file is not null && file.Length > 0)
            .ToArray() ?? Array.Empty<IFormFile>();

        if (files.Length == 0)
        {
            return SocialFailure(
                "social_media_post_invalid",
                "Attach at least one supported image or video.");
        }

        var preview = request?.Preview;
        if (preview is { Length: > SocialMediaUploadLimits.MaximumPreviewImageBytes })
        {
            return SocialFailure(
                "social_media_preview_invalid",
                "The selected Hac preview is too large. Choose another frame and try again.");
        }

        var openedStreams = new List<Stream>(files.Length + (preview is { Length: > 0 } ? 1 : 0));

        try
        {
            var uploads = new List<SocialMediaUpload>(files.Length);

            foreach (var file in files)
            {
                var stream = file.OpenReadStream();
                openedStreams.Add(stream);

                uploads.Add(new SocialMediaUpload(
                    file.FileName,
                    file.Length,
                    stream,
                    request?.AccessibilityText));
            }

            SocialMediaUpload? previewUpload = null;
            if (preview is { Length: > 0 })
            {
                var previewStream = preview.OpenReadStream();
                openedStreams.Add(previewStream);
                previewUpload = new SocialMediaUpload(
                    preview.FileName,
                    preview.Length,
                    previewStream,
                    null);
            }

            var result = await _social.CreateMediaPostAsync(
                new CreateSocialMediaPostCommand(
                    resolved.Actor!,
                    request?.ContentType ?? string.Empty,
                    request?.Body ?? string.Empty,
                    uploads,
                    ToMusicSelection(request),
                    new SocialPostDetails(
                        request?.Audience,
                        request?.Location,
                        request?.CommentsEnabled ?? true),
                    previewUpload,
                    publishImmediately),
                cancellationToken);

            // Multipart bytes are durable at this point. Video normalization
            // continues through the single hosted lifecycle, so do not retain
            // the iOS upload socket while FFmpeg runs.
            return result.Succeeded && result.Value is not null
                ? StatusCode(
                    StatusCodes.Status202Accepted,
                    await ToPostDtoAsync(result.Value, cancellationToken))
                : SocialFailure(result.ErrorCode, result.ErrorMessage);
        }
        finally
        {
            foreach (var stream in openedStreams)
                await stream.DisposeAsync();
        }
    }

    [HttpGet("media/{mediaAssetId:guid}")]
    public async Task<IActionResult> GetMedia(
        Guid mediaAssetId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.GetMediaAsync(
            resolved.Actor!,
            mediaAssetId,
            cancellationToken);

        if (!result.Succeeded || result.Value is null)
        {
            return result.ErrorCode is "social_actor_invalid" or "social_media_storage_unavailable"
                ? SocialFailure(result.ErrorCode, result.ErrorMessage)
                : NotFound();
        }

        return File(
            result.Value.Content,
            result.Value.MimeType,
            enableRangeProcessing: true);
    }

    [HttpGet("media/{mediaAssetId:guid}/preview")]
    public async Task<IActionResult> GetMediaPreview(
        Guid mediaAssetId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.GetMediaPreviewAsync(
            resolved.Actor!,
            mediaAssetId,
            cancellationToken);

        if (!result.Succeeded || result.Value is null)
        {
            return result.ErrorCode is "social_actor_invalid" or "social_media_storage_unavailable"
                ? SocialFailure(result.ErrorCode, result.ErrorMessage)
                : NotFound();
        }

        return File(
            result.Value.Content,
            result.Value.MimeType,
            enableRangeProcessing: true);
    }

    [HttpPost("posts/{postId:guid}/reaction")]
    public async Task<IActionResult> ToggleReaction(Guid postId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.ToggleReactionAsync(new SocialPostMutationCommand(resolved.Actor!, postId), cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToPostDtoAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("posts/{postId:guid}/comments")]
    public async Task<IActionResult> AddComment(
        Guid postId,
        [FromBody] MobileCreateSocialCommentRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.AddCommentAsync(
            new CreateSocialCommentCommand(resolved.Actor!, postId, request?.Body ?? string.Empty, request?.ParentCommentId),
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToCommentDtoAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("follows/toggle")]
    public async Task<IActionResult> ToggleFollow(
        [FromBody] MobileToggleSocialFollowRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.ToggleFollowAsync(
            new SocialFollowCommand(resolved.Actor!, request?.FollowedUserId ?? string.Empty, request?.FollowedParticipantType ?? string.Empty, request?.SourcePostId),
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(new MobileSocialFollowResultDto(result.Value.IsFollowing, result.Value.IsPending))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("profile/follow-requests/{requestId:guid}/decision")]
    public async Task<IActionResult> DecideFollowRequest(
        Guid requestId,
        [FromBody] MobileSocialFollowRequestDecisionRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;
        if (request is null)
            return SocialFailure("social_follow_request_invalid", "Choose whether to approve or decline this request.");

        var result = await _social.DecideFollowRequestAsync(
            new SocialFollowRequestDecisionCommand(resolved.Actor!, requestId, request.Approve),
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(new MobileSocialFollowResultDto(result.Value.IsFollowing, result.Value.IsPending))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("posts/{postId:guid}/save")]
    public async Task<IActionResult> ToggleSave(Guid postId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.ToggleSaveAsync(new SocialPostMutationCommand(resolved.Actor!, postId), cancellationToken);
        return result.Succeeded
            ? Ok(new MobileSocialStateResultDto(result.Value))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("posts/{postId:guid}/repost")]
    public async Task<IActionResult> ToggleRepost(Guid postId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.ToggleRepostAsync(new SocialPostMutationCommand(resolved.Actor!, postId), cancellationToken);
        return result.Succeeded
            ? Ok(new MobileSocialStateResultDto(result.Value))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("posts/{postId:guid}/share")]
    public async Task<IActionResult> RecordShare(Guid postId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.RecordShareAsync(new SocialPostMutationCommand(resolved.Actor!, postId), cancellationToken);
        return result.Succeeded
            ? Ok(new MobileSocialStateResultDto(result.Value))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("posts/{postId:guid}/view")]
    public async Task<IActionResult> RecordView(
        Guid postId,
        [FromBody] MobileRecordSocialViewRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.RecordViewAsync(new RecordSocialPostViewCommand(
            resolved.Actor!,
            postId,
            request?.WatchDurationSeconds,
            request?.WatchCompletionPercentage,
            request?.StoryInteractionType), cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(ToMetricsDto(result.Value))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("insights/creator")]
    public async Task<IActionResult> CreatorInsights(CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.GetCreatorInsightsAsync(resolved.Actor!, cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(ToCreatorInsightsDto(result.Value))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("posts/{postId:guid}/insights")]
    public async Task<IActionResult> PostInsights(Guid postId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.GetPostInsightsAsync(resolved.Actor!, postId, cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(ToPostInsightDto(result.Value))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("profiles/metrics")]
    public async Task<IActionResult> ProfileMetrics(
        [FromQuery] string? userId,
        [FromQuery] string? participantType,
        [FromQuery] Guid? profileId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        SocialAuthor? profile = string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(participantType)
            ? null
            : new SocialAuthor(userId, participantType, profileId.GetValueOrDefault(), string.Empty);
        var result = await _social.GetProfileMetricsAsync(resolved.Actor!, profile, cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToProfileMetricsDtoAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("profiles/visit")]
    public async Task<IActionResult> RecordProfileVisit(
        [FromBody] MobileRecordProfileVisitRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.RecordProfileVisitAsync(new SocialProfileVisitCommand(
            resolved.Actor!,
            request?.TargetUserId ?? string.Empty,
            request?.TargetParticipantType ?? string.Empty,
            request?.SourcePostId), cancellationToken);
        return result.Succeeded
            ? Ok(new MobileSocialStateResultDto(result.Value))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpGet("music/search")]
    public async Task<IActionResult> SearchMusic([FromQuery] string? query, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.SearchMusicAsync(resolved.Actor!, query ?? string.Empty, cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(result.Value.Select(ToMusicDto).ToArray())
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    private async Task<(SocialFeedActor? Actor, IActionResult? Error)> ResolveSocialActorAsync(CancellationToken cancellationToken)
    {
        var resolution = await ResolveActorAsync(cancellationToken);
        if (resolution.Error is not null || resolution.Actor is null)
            return (null, resolution.Error ?? Error(StatusCodes.Status403Forbidden, "mobile_social_unavailable", "Your mobile identity is not available."));

        return (new SocialFeedActor(
            resolution.Actor.Actor,
            resolution.Actor.ProfileId,
            resolution.Actor.DisplayName), null);
    }

    private async Task<MobileSocialSnapshotDto> ToSnapshotDtoAsync(SocialFeedSnapshot snapshot, CancellationToken cancellationToken) => new(
        await ToPostDtosAsync(snapshot.Stories, cancellationToken),
        await ToPostDtosAsync(snapshot.Posts, cancellationToken),
        await ToPostDtosAsync(snapshot.Hacs, cancellationToken),
        await ToActivityDtosAsync(snapshot.Activity, cancellationToken),
        snapshot.ActivityCount,
        await ToProfileMetricsDtoAsync(snapshot.CurrentProfileMetrics, cancellationToken),
        ToCreatorInsightsDto(snapshot.CreatorInsights),
        await ToPromotedGroupDtosAsync(snapshot.PromotedGroups, cancellationToken));

    private async Task<IReadOnlyList<MobileSocialPromotedGroupDto>> ToPromotedGroupDtosAsync(
        IEnumerable<SocialPromotedGroupView> groups,
        CancellationToken cancellationToken)
    {
        var result = new List<MobileSocialPromotedGroupDto>();
        foreach (var group in groups)
        {
            result.Add(new MobileSocialPromotedGroupDto(
                group.ConversationId,
                group.Subject,
                await ToAuthorDtoAsync(group.Owner, cancellationToken),
                MobileAvatarProjection.FromGroupImage(
                    group.ConversationId,
                    group.GroupImage),
                group.ActiveMemberCount,
                group.IsJoinedByCurrentActor,
                group.PromotionStartedUtc));
        }

        return result;
    }

    // Avatar resolution uses the request-scoped MasterAppDbContext. EF Core permits one
    // operation at a time per context, so all DTO projection stays sequential.
    private async Task<IReadOnlyList<MobileSocialPostDto>> ToPostDtosAsync(
        IEnumerable<SocialPostView> posts,
        CancellationToken cancellationToken)
    {
        var result = new List<MobileSocialPostDto>();
        foreach (var post in posts)
            result.Add(await ToPostDtoAsync(post, cancellationToken));

        return result;
    }

    private async Task<IReadOnlyList<MobileSocialActivityDto>> ToActivityDtosAsync(
        IEnumerable<SocialActivityView> activity,
        CancellationToken cancellationToken)
    {
        var result = new List<MobileSocialActivityDto>();
        foreach (var item in activity)
            result.Add(await ToActivityDtoAsync(item, cancellationToken));

        return result;
    }

    private async Task<IReadOnlyList<MobileSocialFollowListEntryDto>> ToFollowListDtosAsync(
        IEnumerable<SocialFollowListEntry> entries,
        CancellationToken cancellationToken)
    {
        var result = new List<MobileSocialFollowListEntryDto>();
        foreach (var entry in entries)
        {
            result.Add(new MobileSocialFollowListEntryDto(
                await ToAuthorDtoAsync(entry.Profile, cancellationToken),
                entry.FollowedByCurrentActor));
        }

        return result;
    }

    private async Task<IReadOnlyList<MobileSocialFollowRequestDto>> ToFollowRequestDtosAsync(
        IEnumerable<SocialFollowRequestView> requests,
        CancellationToken cancellationToken)
    {
        var result = new List<MobileSocialFollowRequestDto>();
        foreach (var request in requests)
            result.Add(new MobileSocialFollowRequestDto(
                request.Id,
                await ToAuthorDtoAsync(request.Profile, cancellationToken),
                request.RequestedUtc));
        return result;
    }

    private async Task<MobileSocialPostDto> ToPostDtoAsync(SocialPostView post, CancellationToken cancellationToken) => new(
        post.Id,
        await ToAuthorDtoAsync(post.Author, cancellationToken),
        post.ContentType,
        post.Body,
        post.Audience,
        post.Location,
        post.CommentsEnabled,
        post.PostedUtc,
        post.ExpiresUtc,
        post.ReactionCount,
        post.CommentCount,
        post.ReactedByCurrentActor,
        post.FollowedByCurrentActor,
        post.FollowRequestPending,
        post.SavedByCurrentActor,
        post.RepostedByCurrentActor,
        ToMetricsDto(post.Metrics),
        post.Music is null ? null : ToMusicDto(post.Music),
        post.Media.Select(media => new MobileSocialMediaDto(
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
            media.HasPreviewImage)).ToArray(),
        await ToCommentDtosAsync(post.Comments, cancellationToken));

    private async Task<IReadOnlyList<MobileSocialCommentDto>> ToCommentDtosAsync(
        IEnumerable<SocialCommentView> comments,
        CancellationToken cancellationToken)
    {
        var result = new List<MobileSocialCommentDto>();
        foreach (var comment in comments)
            result.Add(await ToCommentDtoAsync(comment, cancellationToken));

        return result;
    }

    private async Task<MobileSocialCommentDto> ToCommentDtoAsync(SocialCommentView comment, CancellationToken cancellationToken) => new(
        comment.Id,
        await ToAuthorDtoAsync(comment.Author, cancellationToken),
        comment.ParentCommentId,
        comment.Body,
        comment.CreatedUtc);

    private async Task<MobileSocialActivityDto> ToActivityDtoAsync(SocialActivityView activity, CancellationToken cancellationToken) => new(
        activity.Id,
        activity.Kind,
        await ToAuthorDtoAsync(activity.Actor, cancellationToken),
        activity.PostId,
        activity.OccurredUtc);

    private async Task<MobileSocialAuthorDto> ToAuthorDtoAsync(SocialAuthor author, CancellationToken cancellationToken)
    {
        var identity = new MessagingParticipantIdentity(
            author.UserId,
            author.ParticipantType,
            author.ProfileId,
            author.DisplayName,
            null,
            string.Empty);
        return new MobileSocialAuthorDto(
            new MobileLogicalIdentityDto(author.UserId, author.ParticipantType),
            author.ProfileId.ToString("D"),
            author.DisplayName,
            await MobileAvatarProjection.ResolveAsync(_profiles, identity, cancellationToken),
            author.Username,
            author.Bio,
            author.Website,
            author.Location,
            author.PublicEmail,
            author.IsPrivate,
            await IsVerifiedProfileAsync(
                author.ParticipantType,
                author.ProfileId,
                cancellationToken),
            author.RoleLabel,
            author.PublicPhone);
    }

    private async Task<bool> IsVerifiedProfileAsync(
        string participantType,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (_db is null || profileId == Guid.Empty)
        {
            return false;
        }

        if (string.Equals(participantType, MessagingParticipantTypes.Client, StringComparison.Ordinal))
        {
            return await _db.ClientProfiles
                .AsNoTracking()
                .Where(profile => profile.Id == profileId)
                .Select(profile => profile.IsVerified)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (!string.Equals(participantType, MessagingParticipantTypes.Agent, StringComparison.Ordinal))
            return false;

        var profile = await _db.AgentProfiles
            .AsNoTracking()
            .Where(candidate => candidate.Id == profileId && candidate.IsActive)
            .Select(candidate => new { candidate.IsVerified, Email = candidate.NormalizedEmail ?? candidate.AgentUpn })
            .SingleOrDefaultAsync(cancellationToken);
        return profile?.IsVerified == true || LegendVerifiedIdentity.IsVerifiedAgentEmail(profile?.Email);
    }

    private async Task<MobileSocialProfileMetricsDto> ToProfileMetricsDtoAsync(
        SocialProfileMetrics metrics,
        CancellationToken cancellationToken) => new(
            await ToAuthorDtoAsync(metrics.Profile, cancellationToken),
            metrics.PostCount,
            metrics.VideoCount,
            metrics.StoryCount,
            metrics.FollowerCount,
            metrics.FollowingCount,
            metrics.TotalReactionCount,
            metrics.TotalContentViewCount,
            metrics.TotalReachCount,
            metrics.PrivateProfileVisitCount);

    private static MobileSocialPostMetricsDto ToMetricsDto(SocialPostMetrics metrics) => new(
        metrics.ViewCount,
        metrics.UniqueViewerCount,
        metrics.ReactionCount,
        metrics.CommentCount,
        metrics.ReplyCount,
        metrics.RepostCount,
        metrics.SaveCount,
        metrics.ShareCount,
        metrics.ProfileVisitCount,
        metrics.FollowsGenerated,
        metrics.AverageWatchDurationSeconds,
        metrics.AverageWatchCompletionPercentage,
        metrics.StoryExitCount,
        metrics.StoryTapForwardCount,
        metrics.StoryTapBackwardCount);

    private static MobileSocialMusicDto ToMusicDto(SocialMusicTrack track) => new(
        track.ProviderId,
        track.ProviderTrackId,
        track.TrackTitle,
        track.ArtistName,
        track.TrackDurationSeconds,
        track.AudioUrl,
        null,
        null,
        null,
        null);

    private static MobileSocialMusicDto ToMusicDto(SocialPostMusicView music) => new(
        music.ProviderId,
        music.ProviderTrackId,
        music.TrackTitle,
        music.ArtistName,
        music.TrackDurationSeconds,
        music.AudioUrl,
        music.TrimStartSeconds,
        music.TrimEndSeconds,
        music.MusicVolume,
        music.OriginalAudioVolume);

    private static MobileSocialPostInsightDto ToPostInsightDto(SocialPostInsight insight) => new(
        insight.PostId,
        insight.ContentType,
        insight.PostedUtc,
        ToMetricsDto(insight.Metrics),
        insight.EngagementRatePercentage);

    private static MobileSocialCreatorInsightsDto ToCreatorInsightsDto(SocialCreatorInsights insights) => new(
        insights.GeneratedUtc,
        insights.TotalViews,
        insights.TotalReach,
        insights.FollowerCount,
        insights.FollowingCount,
        insights.FollowersGained,
        insights.ProfileVisits,
        insights.TotalReactions,
        insights.TotalComments,
        insights.TotalReplies,
        insights.TotalShares,
        insights.TotalReposts,
        insights.TotalSaves,
        insights.EngagementRatePercentage,
        insights.TopPosts.Select(ToPostInsightDto).ToArray(),
        insights.TopVideos.Select(ToPostInsightDto).ToArray(),
        insights.TopStories.Select(ToPostInsightDto).ToArray());

    private static SocialMusicSelection? ToMusicSelection(
        string? providerId,
        string? trackId,
        decimal? trimStartSeconds,
        decimal? trimEndSeconds,
        decimal? musicVolume,
        decimal? originalAudioVolume) =>
        string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(trackId)
            ? null
            : new SocialMusicSelection(
                providerId,
                trackId,
                trimStartSeconds ?? 0,
                trimEndSeconds ?? 0,
                musicVolume ?? 1,
                originalAudioVolume ?? 1);

    private static SocialMusicSelection? ToMusicSelection(MobileCreateSocialMediaPostRequest? request) =>
        ToMusicSelection(
            request?.MusicProviderId,
            request?.MusicTrackId,
            request?.MusicTrimStartSeconds,
            request?.MusicTrimEndSeconds,
            request?.MusicVolume,
            request?.OriginalAudioVolume);

    private static SocialMusicSelection? ToMusicSelection(MobilePublishStagedSocialMediaPostRequest? request) =>
        ToMusicSelection(
            request?.MusicProviderId,
            request?.MusicTrackId,
            request?.MusicTrimStartSeconds,
            request?.MusicTrimEndSeconds,
            request?.MusicVolume,
            request?.OriginalAudioVolume);

    private IActionResult SocialFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode is
            "social_post_invalid" or
            "social_post_edit_invalid" or
            "social_media_post_invalid" or
            "social_media_preview_invalid" or
            "social_media_draft_unavailable" or
            "social_comment_invalid" or
            "social_comment_parent_unavailable" or
            "social_comments_disabled" or
            "social_follow_invalid" or
            "social_follow_source_invalid" or
            "social_follow_list_invalid" or
            "social_follow_request_invalid" or
            "social_view_invalid" or
            "social_music_invalid" or
            "social_music_query_invalid" or
            "social_profile_visit_invalid" or
            "social_profile_visit_source_invalid" or
            "SOCIAL_MEDIA_ID_INVALID" or
            "SOCIAL_MEDIA_CONTENT_INVALID" or
            "SOCIAL_MEDIA_SIZE_INVALID" or
            "SOCIAL_MEDIA_SIZE_MISMATCH" or
            "SOCIAL_VIDEO_DURATION_EXCEEDED" or
            "SOCIAL_VIDEO_DURATION_INVALID" or
            "SOCIAL_MEDIA_NAME_INVALID" or
            "SOCIAL_MEDIA_TYPE_INVALID"
                ? StatusCodes.Status400BadRequest
                : errorCode is
                    "SOCIAL_MEDIA_STORAGE_FAILED" or
                    "SOCIAL_MEDIA_STORAGE_UNAVAILABLE" or
                    "social_media_storage_unavailable" or
                    "social_media_delete_failed" or
                    "social_media_persistence_failed"
                        ? StatusCodes.Status503ServiceUnavailable
                        : errorCode is "social_music_provider_unavailable"
                            ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status403Forbidden;
        return Error(status, errorCode ?? "mobile_social_rejected", errorMessage ?? "This social action is not available.");
    }
}

public sealed record MobileCreateSocialPostRequest(
    string? ContentType,
    string? Body,
    string? Audience = null,
    string? Location = null,
    bool? CommentsEnabled = null);
public sealed record MobileUpdateSocialPostRequest(string? Body);

public sealed class MobileCreateSocialMediaPostRequest
{
    public string? ContentType { get; init; }
    public string? Body { get; init; }
    public string? AccessibilityText { get; init; }
    public string? Audience { get; init; }
    public string? Location { get; init; }
    public bool? CommentsEnabled { get; init; }
    public IFormFile? Preview { get; init; }
    public string? MusicProviderId { get; init; }
    public string? MusicTrackId { get; init; }
    public decimal? MusicTrimStartSeconds { get; init; }
    public decimal? MusicTrimEndSeconds { get; init; }
    public decimal? MusicVolume { get; init; }
    public decimal? OriginalAudioVolume { get; init; }
    public List<IFormFile> Files { get; init; } = [];
}

public sealed class MobilePublishStagedSocialMediaPostRequest
{
    public string? Body { get; init; }
    public string? Audience { get; init; }
    public string? Location { get; init; }
    public bool? CommentsEnabled { get; init; }
    public IFormFile? Preview { get; init; }
    public string? MusicProviderId { get; init; }
    public string? MusicTrackId { get; init; }
    public decimal? MusicTrimStartSeconds { get; init; }
    public decimal? MusicTrimEndSeconds { get; init; }
    public decimal? MusicVolume { get; init; }
    public decimal? OriginalAudioVolume { get; init; }
}

public sealed record MobileCreateSocialCommentRequest(string? Body, Guid? ParentCommentId);
public sealed record MobileToggleSocialFollowRequest(string? FollowedUserId, string? FollowedParticipantType, Guid? SourcePostId);
public sealed record MobileSocialFollowRequestDecisionRequest(bool Approve);
public sealed record MobileRecordSocialViewRequest(decimal? WatchDurationSeconds, decimal? WatchCompletionPercentage, string? StoryInteractionType);
public sealed record MobileRecordProfileVisitRequest(string? TargetUserId, string? TargetParticipantType, Guid? SourcePostId);
public sealed record MobileSocialFollowResultDto(bool IsFollowing, bool IsPending);
public sealed record MobileSocialStateResultDto(bool IsActive);
public sealed record MobileSocialAuthorDto(
    MobileLogicalIdentityDto Identity,
    string ProfileId,
    string DisplayName,
    MobileAvatarDto? Avatar,
    string? Username = null,
    string? Bio = null,
    string? Website = null,
    string? Location = null,
    string? PublicEmail = null,
    bool IsPrivate = false,
    bool IsVerified = false,
    string? RoleLabel = null,
    string? PublicPhone = null);
public sealed record MobileSocialFollowListEntryDto(MobileSocialAuthorDto Profile, bool FollowedByCurrentActor);
public sealed record MobileSocialFollowRequestDto(Guid Id, MobileSocialAuthorDto Profile, DateTime RequestedUtc);
public sealed record MobileSocialCommentDto(Guid Id, MobileSocialAuthorDto Author, Guid? ParentCommentId, string Body, DateTime CreatedUtc);

public sealed record MobileSocialMediaDto(
    Guid Id,
    int DisplayOrder,
    string MediaKind,
    string MimeType,
    long FileSizeBytes,
    int? Width,
    int? Height,
    decimal? AspectRatio,
    decimal? DurationSeconds,
    string ProcessingState,
    string? AccessibilityText,
    bool HasPreviewImage);

public sealed record MobileSocialPostDto(
    Guid Id,
    MobileSocialAuthorDto Author,
    string ContentType,
    string Body,
    string Audience,
    string? Location,
    bool CommentsEnabled,
    DateTime PostedUtc,
    DateTime? ExpiresUtc,
    int ReactionCount,
    int CommentCount,
    bool ReactedByCurrentActor,
    bool FollowedByCurrentActor,
    bool FollowRequestPending,
    bool SavedByCurrentActor,
    bool RepostedByCurrentActor,
    MobileSocialPostMetricsDto Metrics,
    MobileSocialMusicDto? Music,
    IReadOnlyList<MobileSocialMediaDto> Media,
    IReadOnlyList<MobileSocialCommentDto> Comments);
public sealed record MobileSocialActivityDto(Guid Id, string Kind, MobileSocialAuthorDto Actor, Guid? PostId, DateTime OccurredUtc);
public sealed record MobileSocialSnapshotDto(
    IReadOnlyList<MobileSocialPostDto> Stories,
    IReadOnlyList<MobileSocialPostDto> Posts,
    IReadOnlyList<MobileSocialPostDto> Hacs,
    IReadOnlyList<MobileSocialActivityDto> Activity,
    int ActivityCount,
    MobileSocialProfileMetricsDto CurrentProfileMetrics,
    MobileSocialCreatorInsightsDto CreatorInsights,
    IReadOnlyList<MobileSocialPromotedGroupDto> PromotedGroups);
public sealed record MobileSocialPromotedGroupDto(
    Guid ConversationId,
    string Subject,
    MobileSocialAuthorDto Owner,
    MobileAvatarDto? GroupAvatar,
    int ActiveMemberCount,
    bool IsJoinedByCurrentActor,
    DateTime PromotionStartedUtc);

public sealed record MobileSocialPostMetricsDto(
    int ViewCount,
    int UniqueViewerCount,
    int ReactionCount,
    int CommentCount,
    int ReplyCount,
    int RepostCount,
    int SaveCount,
    int ShareCount,
    int ProfileVisitCount,
    int FollowsGenerated,
    decimal? AverageWatchDurationSeconds,
    decimal? AverageWatchCompletionPercentage,
    int StoryExitCount,
    int StoryTapForwardCount,
    int StoryTapBackwardCount);

public sealed record MobileSocialMusicDto(
    string ProviderId,
    string ProviderTrackId,
    string TrackTitle,
    string ArtistName,
    decimal TrackDurationSeconds,
    string? AudioUrl,
    decimal? TrimStartSeconds,
    decimal? TrimEndSeconds,
    decimal? MusicVolume,
    decimal? OriginalAudioVolume);

public sealed record MobileSocialPostInsightDto(
    Guid PostId,
    string ContentType,
    DateTime PostedUtc,
    MobileSocialPostMetricsDto Metrics,
    decimal EngagementRatePercentage);

public sealed record MobileSocialCreatorInsightsDto(
    DateTime GeneratedUtc,
    int TotalViews,
    int TotalReach,
    int FollowerCount,
    int FollowingCount,
    int FollowersGained,
    int ProfileVisits,
    int TotalReactions,
    int TotalComments,
    int TotalReplies,
    int TotalShares,
    int TotalReposts,
    int TotalSaves,
    decimal EngagementRatePercentage,
    IReadOnlyList<MobileSocialPostInsightDto> TopPosts,
    IReadOnlyList<MobileSocialPostInsightDto> TopVideos,
    IReadOnlyList<MobileSocialPostInsightDto> TopStories);

public sealed record MobileSocialProfileMetricsDto(
    MobileSocialAuthorDto Profile,
    int PostCount,
    int VideoCount,
    int StoryCount,
    int FollowerCount,
    int FollowingCount,
    int TotalReactionCount,
    int TotalContentViewCount,
    int TotalReachCount,
    int? PrivateProfileVisitCount);
