using System.Text;
using System.Text.Encodings.Web;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;

namespace AgentPortal.Controllers;

[Authorize]
public sealed class SocialSharedContentController(
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
        return profiles.TryGetValue((actor.UserId, actor.ParticipantType), out var profile)
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
        var post = result.Value;
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>LEGEND shared content</title></head><body><main>");
        html.Append("<h1>").Append(HtmlEncoder.Default.Encode(post.Author.DisplayName)).Append("</h1><p>")
            .Append(HtmlEncoder.Default.Encode(post.Body)).Append("</p>");
        foreach (var media in post.Media.OrderBy(media => media.DisplayOrder))
        {
            var url = $"/Social/Media/{media.Id:D}";
            if (media.MediaKind == "Image")
                html.Append("<img style=\"max-width:100%\" src=\"").Append(url).Append("\" alt=\"")
                    .Append(HtmlEncoder.Default.Encode(media.AccessibilityText ?? "Shared image")).Append("\">");
            else if (media.MediaKind == "Video")
                html.Append("<video style=\"max-width:100%\" controls playsinline src=\"").Append(url).Append("\"></video>");
        }
        html.Append("</main></body></html>");
        return Content(html.ToString(), "text/html; charset=utf-8");
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
