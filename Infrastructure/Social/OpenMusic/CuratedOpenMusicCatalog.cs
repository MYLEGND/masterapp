using Domain.Social;

namespace Infrastructure.Social.OpenMusic;

/// <summary>
/// A deliberately small, curated music catalog for Legend social posts.
///
/// This is a local mock database rather than a streaming-provider integration:
/// <c>id</c>, <c>title</c>, <c>artist</c>, <c>duration</c>, and <c>audio_url</c>
/// are held in <see cref="OpenMusicTrack"/>. The URLs are direct HTTPS MP3 assets;
/// the iOS client streams them directly and Legend neither downloads nor proxies
/// the audio. Additions must be manually reviewed for licensing and clean lyrics
/// before being added to <see cref="LocalTrackDatabase"/>.
/// </summary>
public sealed class CuratedOpenMusicCatalog : ISocialMusicCatalog
{
    public const string ProviderId = "legend-open-fma";

    // Source: Free Music Archive collection mirrored by Internet Archive.
    // Psalters released this collection under the Public Domain Mark 1.0:
    // https://archive.org/details/us_vs_us-11170
    // The three entries below were hand-curated as clean, Christian selections.
    private static readonly IReadOnlyList<OpenMusicTrack> LocalTrackDatabase =
    [
        new(
            Id: "psalters-el-elyon",
            Title: "El Elyon",
            Artist: "Psalters",
            Duration: 367.30m,
            AudioUrl: "https://archive.org/download/us_vs_us-11170/Psalters_-_07_-_El_Elyon.mp3",
            Genre: "Christian worship · global folk",
            IsExplicit: false,
            SourceUrl: "https://archive.org/details/us_vs_us-11170",
            LicenseUrl: "https://creativecommons.org/publicdomain/mark/1.0/"),
        new(
            Id: "psalters-im-free",
            Title: "I'm Free",
            Artist: "Psalters",
            Duration: 231.78m,
            AudioUrl: "https://archive.org/download/us_vs_us-11170/Psalters_-_08_-_Im_free.mp3",
            Genre: "Christian worship · global folk",
            IsExplicit: false,
            SourceUrl: "https://archive.org/details/us_vs_us-11170",
            LicenseUrl: "https://creativecommons.org/publicdomain/mark/1.0/"),
        new(
            Id: "psalters-all-yeshua",
            Title: "All Yeshua",
            Artist: "Psalters",
            Duration: 321.22m,
            AudioUrl: "https://archive.org/download/us_vs_us-11170/Psalters_-_09_-_All_Yeshua.mp3",
            Genre: "Christian worship · global folk",
            IsExplicit: false,
            SourceUrl: "https://archive.org/details/us_vs_us-11170",
            LicenseUrl: "https://creativecommons.org/publicdomain/mark/1.0/")
    ];

    public Task<SocialOperationResult<IReadOnlyList<SocialMusicTrack>>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var terms = (query ?? string.Empty)
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (terms.Length == 0)
        {
            return Task.FromResult(SocialOperationResult<IReadOnlyList<SocialMusicTrack>>.Failure(
                "open_music_query_invalid",
                "Search with between 1 and 120 characters."));
        }

        var tracks = LocalTrackDatabase
            .Where(track => !track.IsExplicit && MatchesEveryTerm(track, terms))
            .Select(ToSocialTrack)
            .ToArray();

        return Task.FromResult(SocialOperationResult<IReadOnlyList<SocialMusicTrack>>.Success(tracks));
    }

    public Task<SocialOperationResult<SocialMusicTrack>> ResolveAsync(
        string providerId,
        string providerTrackId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(providerId?.Trim(), ProviderId, StringComparison.Ordinal))
        {
            return Task.FromResult(SocialOperationResult<SocialMusicTrack>.Failure(
                "open_music_provider_invalid",
                "The selected music source is not available."));
        }

        var track = LocalTrackDatabase.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerTrackId?.Trim(), StringComparison.Ordinal));

        return Task.FromResult(track is null
            ? SocialOperationResult<SocialMusicTrack>.Failure(
                "open_music_track_not_found",
                "That music track is no longer in the Legend catalog.")
            : SocialOperationResult<SocialMusicTrack>.Success(ToSocialTrack(track)));
    }

    private static bool MatchesEveryTerm(OpenMusicTrack track, IReadOnlyList<string> terms)
    {
        var searchableText = string.Join(
            ' ',
            track.Title,
            track.Artist,
            track.Genre,
            "music clean public domain");

        return terms.All(term => searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static SocialMusicTrack ToSocialTrack(OpenMusicTrack track) => new(
        ProviderId,
        track.Id,
        track.Title,
        track.Artist,
        track.Duration,
        track.AudioUrl);
}

/// <summary>
/// Local mock-database schema. In JSON terminology these fields are
/// <c>id</c>, <c>title</c>, <c>artist</c>, <c>duration</c>, and <c>audio_url</c>.
/// </summary>
public sealed record OpenMusicTrack(
    string Id,
    string Title,
    string Artist,
    decimal Duration,
    string AudioUrl,
    string Genre,
    bool IsExplicit,
    string SourceUrl,
    string LicenseUrl);
