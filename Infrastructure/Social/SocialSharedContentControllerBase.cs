using Domain.Messaging;
using Domain.Social;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Shared.Messaging;
using Shared.Social;

namespace Infrastructure.Social;

[Authorize]
public abstract class SocialSharedContentControllerBase(
    IMessagingActorContextResolver actors,
    IMessagingProfileImageResolver identities,
    ISocialFeedService social) : Controller
{
    private async Task<SocialFeedActor?> ActorAsync(CancellationToken cancellationToken)
    {
        var resolved = await actors.ResolveAsync(HttpContext, cancellationToken);
        if (resolved is null) return null;
        var actor = new MessagingActor(resolved.Value.UserId, resolved.Value.ParticipantType);
        var profiles = await identities.ResolveIdentitiesAsync(
            [new MessagingParticipantReference(actor.UserId, actor.ParticipantType)], cancellationToken);
        return profiles.TryGetValue(MessagingParticipantIdentityKey.Create(actor.UserId, actor.ParticipantType), out var profile)
            ? new SocialFeedActor(actor, profile.ProfileId, profile.DisplayName) : null;
    }

    [HttpGet("/Social/Posts/{postId:guid}")]
    public async Task<IActionResult> Open(Guid postId, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(cancellationToken);
        if (actor is null) return Forbid();
        var result = await social.GetPostAsync(actor, postId, cancellationToken);
        if (!result.Succeeded || result.Value is null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return View("~/Views/SocialSharedContent/Open.cshtml", ViewModel(result.Value));
    }

    // Browser adapters use the same social mutation authority as the native API.
    // A shared link never grants access to content or bypasses moderation.
    [HttpPost("/Social/Posts/{postId:guid}/reaction")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reaction(Guid postId, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(cancellationToken);
        if (actor is null) return Forbid();
        var result = await social.ToggleReactionAsync(new SocialPostMutationCommand(actor, postId), cancellationToken);
        return result.Succeeded && result.Value is not null
            ? Presented(result.Value) : Rejected(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("/Social/Posts/{postId:guid}/comments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Comment(Guid postId, [FromForm] string? body,
        [FromForm] Guid? parentCommentId, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(cancellationToken);
        if (actor is null) return Forbid();
        var result = await social.AddCommentAsync(new CreateSocialCommentCommand(actor, postId, body ?? string.Empty, parentCommentId), cancellationToken);
        return result.Succeeded ? await Refresh(actor, postId, cancellationToken) : Rejected(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("/Social/Posts/{postId:guid}/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Guid postId, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(cancellationToken);
        if (actor is null) return Forbid();
        var result = await social.ToggleSaveAsync(new SocialPostMutationCommand(actor, postId), cancellationToken);
        return result.Succeeded ? await Refresh(actor, postId, cancellationToken) : Rejected(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("/Social/Posts/{postId:guid}/repost")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Repost(Guid postId, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(cancellationToken);
        if (actor is null) return Forbid();
        var result = await social.ToggleRepostAsync(new SocialPostMutationCommand(actor, postId), cancellationToken);
        return result.Succeeded ? await Refresh(actor, postId, cancellationToken) : Rejected(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("/Social/Posts/{postId:guid}/share")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Share(Guid postId, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(cancellationToken);
        if (actor is null) return Forbid();
        var result = await social.RecordShareAsync(new SocialPostMutationCommand(actor, postId), cancellationToken);
        return result.Succeeded ? await Refresh(actor, postId, cancellationToken) : Rejected(result.ErrorCode, result.ErrorMessage);
    }

    private async Task<IActionResult> Refresh(SocialFeedActor actor, Guid postId, CancellationToken cancellationToken)
    {
        var result = await social.GetPostAsync(actor, postId, cancellationToken);
        return result.Succeeded && result.Value is not null ? Presented(result.Value) : NotFound();
    }

    private IActionResult Presented(SocialPostView post)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(Present(post));
    }

    private IActionResult Rejected(string? code, string? message) =>
        StatusCode(code switch
        {
            "social_actor_invalid" => StatusCodes.Status403Forbidden,
            "social_post_unavailable" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest
        }, new { errorCode = code, errorMessage = message ?? "This action is unavailable." });

    private static SharedSocialPostViewModel ViewModel(SocialPostView post) => new(
        post.Id, new(post.Author.DisplayName, $"/Social/Posts/{post.Id:D}/author-image"), post.ContentType, post.Body, post.Location,
        post.CommentsEnabled, post.PostedUtc, post.ReactionCount, post.CommentCount,
        post.ReactedByCurrentActor, post.SavedByCurrentActor, post.RepostedByCurrentActor,
        new(post.Metrics.ShareCount, post.Metrics.RepostCount),
        post.Music is null ? null : new(post.Music.TrackTitle, post.Music.ArtistName, post.Music.AudioUrl),
        post.Media.Select(media => new SharedSocialMediaView(media.Id, media.DisplayOrder, media.MediaKind, media.AccessibilityText)).ToArray(),
        post.Comments.Select(comment => new SharedSocialCommentView(comment.Id, new(comment.Author.DisplayName), comment.ParentCommentId, comment.Body, comment.CreatedUtc)).ToArray());

    private static SocialPostView Present(SocialPostView post) => post with
    {
        // Phone is an internal field, distinct from the explicitly public phone.
        Author = post.Author with { Phone = null },
        Comments = post.Comments.Select(comment => comment with { Author = comment.Author with { Phone = null } }).ToArray()
    };

    [HttpGet("/Social/Posts/{postId:guid}/author-image")]
    public async Task<IActionResult> AuthorImage(Guid postId, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(cancellationToken);
        if (actor is null) return Forbid();
        var result = await social.GetPostAsync(actor, postId, cancellationToken);
        if (!result.Succeeded || result.Value is null) return NotFound();
        var author = result.Value.Author;
        var image = await identities.ResolveAsync(new MessagingParticipantIdentity(author.UserId,
            author.ParticipantType, author.ProfileId, author.DisplayName, null, string.Empty), cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return image is null ? NotFound() : File(image.Content, image.ContentType);
    }

    [HttpGet("/Social/Media/{mediaId:guid}")]
    public async Task<IActionResult> Media(Guid mediaId, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(cancellationToken);
        if (actor is null) return Forbid();
        var result = await social.GetMediaAsync(actor, mediaId, cancellationToken);
        if (!result.Succeeded || result.Value is null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return File(result.Value.Content, result.Value.MimeType, enableRangeProcessing: true);
    }
}
