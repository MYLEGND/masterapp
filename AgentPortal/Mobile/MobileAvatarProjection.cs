using Domain.Messaging;
using Infrastructure.Messaging;

namespace AgentPortal.Mobile;

internal static class MobileAvatarProjection
{
    public static MobileAvatarDto? FromGroupImage(MessagingGroupImage? image) =>
        image is { Content.Length: > 0 } &&
        image.ContentType is "image/png" or "image/jpeg" or "image/webp" or "image/heic"
            ? new MobileAvatarDto("inline", image.ContentType, Convert.ToBase64String(image.Content))
            : null;

    public static Task<MobileAvatarDto?> ResolveAsync(
        IMessagingProfileImageResolver profiles,
        string participantType,
        Guid profileId,
        CancellationToken cancellationToken) =>
        ResolveAsync(
            profiles,
            new MessagingParticipantIdentity(
                string.Empty,
                participantType,
                profileId,
                string.Empty,
                null,
                string.Empty),
            cancellationToken);

    public static async Task<MobileAvatarDto?> ResolveAsync(
        IMessagingProfileImageResolver profiles,
        MessagingParticipantIdentity identity,
        CancellationToken cancellationToken)
    {
        var image = await profiles.ResolveAsync(identity, cancellationToken);
        return image is null
            ? null
            : new MobileAvatarDto("inline", image.ContentType, Convert.ToBase64String(image.Content));
    }

    /// <summary>
    /// Projects a list of canonical avatars with at most one query per typed
    /// profile authority. The production resolver implements the batch
    /// capability; the single-image fallback keeps alternate implementations
    /// contract-compatible without changing their source of truth.
    /// </summary>
    public static async Task<IReadOnlyDictionary<MessagingProfileImageKey, MobileAvatarDto>> ResolveManyAsync(
        IMessagingProfileImageResolver profiles,
        IEnumerable<MessagingParticipantIdentity> identities,
        CancellationToken cancellationToken)
    {
        var requested = identities
            .Where(identity => identity.ProfileId != Guid.Empty)
            .DistinctBy(MessagingProfileImageKey.From)
            .ToArray();
        if (requested.Length == 0)
            return new Dictionary<MessagingProfileImageKey, MobileAvatarDto>();

        IReadOnlyDictionary<MessagingProfileImageKey, MessagingProfileImage> images;
        if (profiles is IMessagingProfileImageBatchResolver batchResolver)
        {
            images = await batchResolver.ResolveManyAsync(requested, cancellationToken);
        }
        else
        {
            var fallback = new Dictionary<MessagingProfileImageKey, MessagingProfileImage>();
            foreach (var identity in requested)
            {
                var image = await profiles.ResolveAsync(identity, cancellationToken);
                if (image is not null)
                    fallback[MessagingProfileImageKey.From(identity)] = image;
            }

            images = fallback;
        }

        return images.ToDictionary(
            entry => entry.Key,
            entry => new MobileAvatarDto(
                "inline",
                entry.Value.ContentType,
                Convert.ToBase64String(entry.Value.Content)));
    }
}
