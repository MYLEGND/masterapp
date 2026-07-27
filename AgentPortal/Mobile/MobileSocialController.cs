using Domain.Messaging;
using Domain.Social;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    public MobileSocialController(
        IMobileActorResolver actorResolver,
        ISocialFeedService social,
        IMessagingProfileImageResolver profiles)
        : base(actorResolver)
    {
        _social = social;
        _profiles = profiles;
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

    [HttpPost("posts")]
    public async Task<IActionResult> CreatePost(
        [FromBody] MobileCreateSocialPostRequest? request,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null)
            return resolved.Error;

        var result = await _social.CreatePostAsync(
            new CreateSocialPostCommand(resolved.Actor!, request?.ContentType ?? string.Empty, request?.Body ?? string.Empty),
            cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Ok(await ToPostDtoAsync(result.Value, cancellationToken))
            : SocialFailure(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("posts/media")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(
        MultipartBodyLengthLimit = 1_048_576_000,
        ValueLengthLimit = 2_000)]
    public async Task<IActionResult> CreateMediaPost(
        [FromForm] MobileCreateSocialMediaPostRequest? request,
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

        var openedStreams = new List<Stream>(files.Length);

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

            var result = await _social.CreateMediaPostAsync(
                new CreateSocialMediaPostCommand(
                    resolved.Actor!,
                    request?.ContentType ?? string.Empty,
                    request?.Body ?? string.Empty,
                    uploads),
                cancellationToken);

            return result.Succeeded && result.Value is not null
                ? Ok(await ToPostDtoAsync(result.Value, cancellationToken))
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
            return result.ErrorCode == "social_actor_invalid"
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
            new CreateSocialCommentCommand(resolved.Actor!, postId, request?.Body ?? string.Empty),
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
            new SocialFollowCommand(resolved.Actor!, request?.FollowedUserId ?? string.Empty, request?.FollowedParticipantType ?? string.Empty),
            cancellationToken);
        return result.Succeeded
            ? Ok(new MobileSocialFollowResultDto(result.Value))
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
        await Task.WhenAll(snapshot.Stories.Select(post => ToPostDtoAsync(post, cancellationToken))),
        await Task.WhenAll(snapshot.Posts.Select(post => ToPostDtoAsync(post, cancellationToken))),
        await Task.WhenAll(snapshot.Activity.Select(item => ToActivityDtoAsync(item, cancellationToken))),
        snapshot.ActivityCount);

    private async Task<MobileSocialPostDto> ToPostDtoAsync(SocialPostView post, CancellationToken cancellationToken) => new(
        post.Id,
        await ToAuthorDtoAsync(post.Author, cancellationToken),
        post.ContentType,
        post.Body,
        post.PostedUtc,
        post.ExpiresUtc,
        post.ReactionCount,
        post.CommentCount,
        post.ReactedByCurrentActor,
        post.FollowedByCurrentActor,
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
            media.AccessibilityText)).ToArray(),
        await Task.WhenAll(post.Comments.Select(comment => ToCommentDtoAsync(comment, cancellationToken))));

    private async Task<MobileSocialCommentDto> ToCommentDtoAsync(SocialCommentView comment, CancellationToken cancellationToken) => new(
        comment.Id,
        await ToAuthorDtoAsync(comment.Author, cancellationToken),
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
            await MobileAvatarProjection.ResolveAsync(_profiles, identity, cancellationToken));
    }

    private IActionResult SocialFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode is
            "social_post_invalid" or
            "social_media_post_invalid" or
            "social_comment_invalid" or
            "social_follow_invalid" or
            "SOCIAL_MEDIA_ID_INVALID" or
            "SOCIAL_MEDIA_CONTENT_INVALID" or
            "SOCIAL_MEDIA_SIZE_INVALID" or
            "SOCIAL_MEDIA_SIZE_MISMATCH" or
            "SOCIAL_MEDIA_NAME_INVALID" or
            "SOCIAL_MEDIA_TYPE_INVALID"
                ? StatusCodes.Status400BadRequest
                : errorCode is
                    "SOCIAL_MEDIA_STORAGE_FAILED" or
                    "social_media_persistence_failed"
                        ? StatusCodes.Status500InternalServerError
                        : StatusCodes.Status403Forbidden;
        return Error(status, errorCode ?? "mobile_social_rejected", errorMessage ?? "This social action is not available.");
    }
}

public sealed record MobileCreateSocialPostRequest(string? ContentType, string? Body);

public sealed class MobileCreateSocialMediaPostRequest
{
    public string? ContentType { get; init; }
    public string? Body { get; init; }
    public string? AccessibilityText { get; init; }
    public List<IFormFile> Files { get; init; } = [];
}

public sealed record MobileCreateSocialCommentRequest(string? Body);
public sealed record MobileToggleSocialFollowRequest(string? FollowedUserId, string? FollowedParticipantType);
public sealed record MobileSocialFollowResultDto(bool IsFollowing);
public sealed record MobileSocialAuthorDto(MobileLogicalIdentityDto Identity, string ProfileId, string DisplayName, MobileAvatarDto? Avatar);
public sealed record MobileSocialCommentDto(Guid Id, MobileSocialAuthorDto Author, string Body, DateTime CreatedUtc);

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
    string? AccessibilityText);

public sealed record MobileSocialPostDto(
    Guid Id,
    MobileSocialAuthorDto Author,
    string ContentType,
    string Body,
    DateTime PostedUtc,
    DateTime? ExpiresUtc,
    int ReactionCount,
    int CommentCount,
    bool ReactedByCurrentActor,
    bool FollowedByCurrentActor,
    IReadOnlyList<MobileSocialMediaDto> Media,
    IReadOnlyList<MobileSocialCommentDto> Comments);
public sealed record MobileSocialActivityDto(Guid Id, string Kind, MobileSocialAuthorDto Actor, Guid? PostId, DateTime OccurredUtc);
public sealed record MobileSocialSnapshotDto(
    IReadOnlyList<MobileSocialPostDto> Stories,
    IReadOnlyList<MobileSocialPostDto> Posts,
    IReadOnlyList<MobileSocialActivityDto> Activity,
    int ActivityCount);
