using Domain.Social;

namespace Infrastructure.Social;

/// <summary>
/// Fail-closed catalog used until a licensed music provider is configured.
/// It never returns sample tracks or authorizes unverifiable music metadata.
/// </summary>
public sealed class UnavailableSocialMusicCatalog : ISocialMusicCatalog
{
    public Task<SocialOperationResult<IReadOnlyList<SocialMusicTrack>>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(SocialOperationResult<IReadOnlyList<SocialMusicTrack>>.Failure(
            "social_music_provider_unavailable",
            "Music search is unavailable until a licensed Legend music provider is configured."));

    public Task<SocialOperationResult<SocialMusicTrack>> ResolveAsync(
        string providerId,
        string providerTrackId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(SocialOperationResult<SocialMusicTrack>.Failure(
            "social_music_provider_unavailable",
            "Music selection is unavailable until a licensed Legend music provider is configured."));
}
