using Domain.Messaging;
using Domain.Social;
namespace Infrastructure.Messaging;

internal sealed partial class MessagingService
{
    private readonly ISocialFeedService? _social;

    private async Task<MessagingSharedContent> ResolveSharedContentAsync(MessagingActor actor,
        Guid postId, CancellationToken cancellationToken)
        => await ResolveSharedContentAsync(await ResolveSharedSocialActorAsync(actor, cancellationToken), postId, cancellationToken);

    private async Task<MessagingSharedContent> ResolveSharedContentAsync(SocialFeedActor? socialActor,
        Guid postId, CancellationToken cancellationToken)
    {
        var unavailable = new MessagingSharedContent(postId, "unavailable", null, null, null,
            Array.Empty<SocialMediaAssetView>(), $"/Social/Posts/{postId:D}");
        if (_social is null || postId == Guid.Empty)
            return unavailable;
        if (socialActor is null) return unavailable;
        var result = await _social.GetPostAsync(socialActor, postId, cancellationToken);
        return result.Succeeded && result.Value is { } post
            ? unavailable with { Status = "available", ContentType = post.ContentType,
                Body = post.Body, AuthorDisplayName = post.Author.DisplayName, Media = post.Media }
            : unavailable;
    }

    private async Task<SocialFeedActor?> ResolveSharedSocialActorAsync(MessagingActor actor, CancellationToken cancellationToken)
    {
        if (_social is null) return null;
        var identities = await _participantIdentities.ResolveIdentitiesAsync(
            [new MessagingParticipantReference(actor.UserId, actor.ParticipantType)], cancellationToken);
        return identities.TryGetValue(MessagingParticipantIdentityKey.Create(actor.UserId, actor.ParticipantType), out var identity)
            ? new SocialFeedActor(actor, identity.ProfileId, identity.DisplayName) : null;
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
        List<MessagingMessageSummary> messages, IReadOnlyDictionary<Guid, Guid> sources,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
            return messages;
        var socialActor = await ResolveSharedSocialActorAsync(actor, cancellationToken);
        var cards = new Dictionary<Guid, MessagingSharedContent>();
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].IsDeleted || !sources.TryGetValue(messages[index].Id, out var source)) continue;
            if (!cards.TryGetValue(source, out var card))
                cards[source] = card = await ResolveSharedContentAsync(socialActor, source, cancellationToken);
            messages[index] = messages[index] with { SharedContent = card };
        }
        return messages;
    }
}
