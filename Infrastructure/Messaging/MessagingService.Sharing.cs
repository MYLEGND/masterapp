using Domain.Messaging;
using Domain.Social;
using Microsoft.EntityFrameworkCore;
namespace Infrastructure.Messaging;

internal sealed partial class MessagingService
{
    private readonly ISocialFeedService? _social;

    private async Task<MessagingSharedContent> ResolveSharedContentAsync(MessagingActor actor,
        Guid postId, CancellationToken cancellationToken)
    {
        var unavailable = new MessagingSharedContent(postId, "unavailable", null, null, null,
            Array.Empty<SocialMediaAssetView>(), $"/Social/Posts/{postId:D}");
        if (_social is null || postId == Guid.Empty)
            return unavailable;
        var identities = await _participantIdentities.ResolveIdentitiesAsync(
            [new MessagingParticipantReference(actor.UserId, actor.ParticipantType)], cancellationToken);
        if (!identities.TryGetValue((actor.UserId, actor.ParticipantType), out var identity))
            return unavailable;
        var result = await _social.GetPostAsync(new SocialFeedActor(actor, identity.ProfileId, identity.DisplayName),
            postId, cancellationToken);
        return result.Succeeded && result.Value is { } post
            ? unavailable with { Status = "available", ContentType = post.ContentType,
                Body = post.Body, AuthorDisplayName = post.Author.DisplayName, Media = post.Media }
            : unavailable;
    }

    private async Task<string> SharedNotificationPreviewAsync(MessagingActor recipient, Guid postId,
        CancellationToken cancellationToken)
    {
        var card = await ResolveSharedContentAsync(recipient, postId, cancellationToken);
        // Notification transport receives application copy, never a private
        // source caption, contact field or media URL. Content opens via the card.
        return await LocalizeApplicationCopyAsync(recipient,
            card.Status == "available"
                ? ApplicationCopyText.Source("Shared content")
                : ApplicationCopyText.Source("Shared content is unavailable."),
            null, cancellationToken);
    }

    private async Task<List<MessagingMessageSummary>> ApplySharedContentAsync(MessagingActor actor,
        List<MessagingMessageSummary> messages, CancellationToken cancellationToken)
    {
        var ids = messages.Where(message => !message.IsDeleted).Select(message => message.Id).ToArray();
        var sources = await _db.InternalMessages.AsNoTracking()
            .Where(message => ids.Contains(message.Id) && message.SharedSocialPostId != null)
            .Select(message => new { message.Id, Source = message.SharedSocialPostId!.Value })
            .ToDictionaryAsync(message => message.Id, message => message.Source, cancellationToken);
        var cards = new Dictionary<Guid, MessagingSharedContent>();
        for (var index = 0; index < messages.Count; index++)
        {
            if (!sources.TryGetValue(messages[index].Id, out var source)) continue;
            if (!cards.TryGetValue(source, out var card))
                cards[source] = card = await ResolveSharedContentAsync(actor, source, cancellationToken);
            messages[index] = messages[index] with { SharedContent = card };
        }
        return messages;
    }
}
