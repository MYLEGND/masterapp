using Domain.Messaging;
using Infrastructure.Messaging;

namespace AgentPortal.Mobile;

internal static class MobileAvatarProjection
{
    public static MobileAvatarDto? FromGroupImage(MessagingGroupImage? image) =>
        image is { Content.Length: > 0 } &&
        image.ContentType is "image/png" or "image/jpeg" or "image/webp"
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
}
