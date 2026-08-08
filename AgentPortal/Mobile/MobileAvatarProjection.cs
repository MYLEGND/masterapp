using System.Security.Cryptography;
using Domain.Messaging;
using Infrastructure.Messaging;

namespace AgentPortal.Mobile;

internal static class MobileAvatarProjection
{
    public static MobileAvatarDto? FromGroupImage(
        Guid conversationId,
        MessagingGroupImage? image) =>
        image is { Content.Length: > 0 } &&
        image.ContentType is "image/png" or "image/jpeg" or "image/webp" or "image/heic"
            ? Resource(
                image.ContentType,
                $"/api/v1/mobile/messaging/conversations/{conversationId:D}/image",
                image.Content)
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
            : ProfileResource(identity, image);
    }

    /// <summary>
    /// Resolves canonical profile media in one batch, but transports only
    /// versioned protected resource references. Binary image content never
    /// enters parent mobile JSON payloads.
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

        if (profiles is IMessagingProfileImageVersionBatchResolver versionResolver)
        {
            var versions = await versionResolver.ResolveVersionsAsync(
                requested,
                cancellationToken);

            return versions.ToDictionary(
                entry => entry.Key,
                entry => new MobileAvatarDto(
                    "resource",
                    entry.Value.ContentType,
                    $"{ProfilePath(entry.Key.ParticipantType, entry.Key.ProfileId)}?v={entry.Value.Version}"));
        }

        IReadOnlyDictionary<MessagingProfileImageKey, MessagingProfileImage> images;

        if (profiles is IMessagingProfileImageBatchResolver batchResolver)
        {
            images = await batchResolver.ResolveManyAsync(
                requested,
                cancellationToken);
        }
        else
        {
            var resolved = new Dictionary<
                MessagingProfileImageKey,
                MessagingProfileImage>();

            foreach (var identity in requested)
            {
                var image = await profiles.ResolveAsync(
                    identity,
                    cancellationToken);

                if (image is not null)
                {
                    resolved[
                        MessagingProfileImageKey.From(identity)
                    ] = image;
                }
            }

            images = resolved;
        }

        return images.ToDictionary(
            entry => entry.Key,
            entry => Resource(
                entry.Value.ContentType,
                ProfilePath(
                    entry.Key.ParticipantType,
                    entry.Key.ProfileId),
                entry.Value.Content));
    }

    private static MobileAvatarDto ProfileResource(
        MessagingParticipantIdentity identity,
        MessagingProfileImage image) =>
        Resource(
            image.ContentType,
            ProfilePath(
                identity.ParticipantType,
                identity.ProfileId),
            image.Content);

    private static string ProfilePath(
        string participantType,
        Guid profileId) =>
        $"/api/v1/mobile/profile-images/" +
        $"{Uri.EscapeDataString(participantType)}/{profileId:D}";

    private static MobileAvatarDto Resource(
        string contentType,
        string path,
        byte[] content)
    {
        // The immutable version component changes whenever the authoritative
        // image bytes change. Native caching can therefore be aggressive
        // without serving a stale avatar after an update.
        var version = Convert.ToHexString(
            SHA256.HashData(content))[..16].ToLowerInvariant();

        return new MobileAvatarDto(
            "resource",
            contentType,
            $"{path}?v={version}");
    }
}
