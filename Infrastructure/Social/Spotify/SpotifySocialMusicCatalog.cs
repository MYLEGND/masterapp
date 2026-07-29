using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Domain.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Social.Spotify;

/// <summary>
/// Spotify catalog metadata adapter for Legend's linked-track experience.
/// Spotify audio is never downloaded, proxied, mixed, transcoded, or synchronized.
/// </summary>
public sealed class SpotifySocialMusicCatalog : ISocialMusicCatalog
{
    public const string ProviderId = "spotify";

    private static readonly SemaphoreSlim TokenGate = new(1, 1);

    private readonly HttpClient _http;
    private readonly SpotifySocialMusicOptions _options;
    private readonly ILogger<SpotifySocialMusicCatalog> _logger;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresUtc;

    public SpotifySocialMusicCatalog(
        HttpClient http,
        IOptions<SpotifySocialMusicOptions> options,
        ILogger<SpotifySocialMusicCatalog> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SocialOperationResult<IReadOnlyList<SocialMusicTrack>>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        query = query?.Trim() ?? string.Empty;
        if (!_options.IsConfigured)
            return Unavailable<IReadOnlyList<SocialMusicTrack>>();

        if (query.Length is 0 or > 120)
        {
            return SocialOperationResult<IReadOnlyList<SocialMusicTrack>>.Failure(
                "social_music_query_invalid",
                "Enter a Spotify music search between 1 and 120 characters.");
        }

        try
        {
            var limit = Math.Clamp(_options.SearchLimit, 1, 10);
            var market = NormalizeMarket(_options.Market);
            var path =
                $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}" +
                $"&type=track&limit={limit}&market={Uri.EscapeDataString(market)}";

            using var response = await SendAuthorizedAsync(HttpMethod.Get, path, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return SpotifyFailure<IReadOnlyList<SocialMusicTrack>>(response.StatusCode);

            var payload = await response.Content.ReadFromJsonAsync<SpotifySearchResponse>(
                cancellationToken: cancellationToken);

            var tracks = payload?.Tracks?.Items?
                .Where(track =>
                    !string.IsNullOrWhiteSpace(track.Id) &&
                    !string.IsNullOrWhiteSpace(track.Name) &&
                    track.DurationMs > 0)
                .Select(ToSocialTrack)
                .ToArray()
                ?? [];

            return SocialOperationResult<IReadOnlyList<SocialMusicTrack>>.Success(tracks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify social music search failed.");
            return SocialOperationResult<IReadOnlyList<SocialMusicTrack>>.Failure(
                "spotify_catalog_unavailable",
                "Spotify music search is temporarily unavailable.");
        }
    }

    public async Task<SocialOperationResult<SocialMusicTrack>> ResolveAsync(
        string providerId,
        string providerTrackId,
        CancellationToken cancellationToken = default)
    {
        providerId = providerId?.Trim() ?? string.Empty;
        providerTrackId = providerTrackId?.Trim() ?? string.Empty;

        if (!_options.IsConfigured)
            return Unavailable<SocialMusicTrack>();

        if (!string.Equals(providerId, ProviderId, StringComparison.OrdinalIgnoreCase) ||
            providerTrackId.Length is 0 or > 256)
        {
            return SocialOperationResult<SocialMusicTrack>.Failure(
                "social_music_invalid",
                "The selected Spotify track is invalid.");
        }

        try
        {
            var market = NormalizeMarket(_options.Market);
            var path =
                $"https://api.spotify.com/v1/tracks/{Uri.EscapeDataString(providerTrackId)}" +
                $"?market={Uri.EscapeDataString(market)}";

            using var response = await SendAuthorizedAsync(HttpMethod.Get, path, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return SocialOperationResult<SocialMusicTrack>.Failure(
                    "spotify_track_not_found",
                    "That Spotify track is no longer available.");
            }

            if (!response.IsSuccessStatusCode)
                return SpotifyFailure<SocialMusicTrack>(response.StatusCode);

            var track = await response.Content.ReadFromJsonAsync<SpotifyTrack>(
                cancellationToken: cancellationToken);

            if (track is null ||
                string.IsNullOrWhiteSpace(track.Id) ||
                string.IsNullOrWhiteSpace(track.Name) ||
                track.DurationMs <= 0)
            {
                return SocialOperationResult<SocialMusicTrack>.Failure(
                    "spotify_track_invalid",
                    "Spotify returned an incomplete track record.");
            }

            return SocialOperationResult<SocialMusicTrack>.Success(ToSocialTrack(track));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify track resolution failed. TrackId={TrackId}", providerTrackId);
            return SocialOperationResult<SocialMusicTrack>.Failure(
                "spotify_catalog_unavailable",
                "Spotify could not verify that track right now.");
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        InvalidateToken();

        token = await GetAccessTokenAsync(cancellationToken);
        using var retry = new HttpRequestMessage(method, url);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        retry.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await _http.SendAsync(retry, cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (HasUsableToken())
            return _accessToken!;

        await TokenGate.WaitAsync(cancellationToken);
        try
        {
            if (HasUsableToken())
                return _accessToken!;

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://accounts.spotify.com/api/token");

            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials"
                });

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Spotify token request failed with status {(int)response.StatusCode}.");

            var token = await response.Content.ReadFromJsonAsync<SpotifyTokenResponse>(
                cancellationToken: cancellationToken);

            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException("Spotify returned no access token.");

            _accessToken = token.AccessToken;
            _accessTokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(
                Math.Max(60, token.ExpiresIn - 60));

            return _accessToken;
        }
        finally
        {
            TokenGate.Release();
        }
    }

    private bool HasUsableToken() =>
        !string.IsNullOrWhiteSpace(_accessToken) &&
        _accessTokenExpiresUtc > DateTimeOffset.UtcNow;

    private void InvalidateToken()
    {
        _accessToken = null;
        _accessTokenExpiresUtc = DateTimeOffset.MinValue;
    }

    private static SocialMusicTrack ToSocialTrack(SpotifyTrack track)
    {
        var artist = string.Join(
            ", ",
            track.Artists?
                .Select(value => value.Name?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                ?? []);

        if (string.IsNullOrWhiteSpace(artist))
            artist = "Spotify Artist";

        return new SocialMusicTrack(
            ProviderId,
            track.Id!,
            track.Name!,
            artist,
            decimal.Round(track.DurationMs / 1000m, 3),
            null);
    }

    private static string NormalizeMarket(string? value)
    {
        var market = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return market.Length == 2 && market.All(char.IsLetter) ? market : "US";
    }

    private static SocialOperationResult<T> Unavailable<T>() =>
        SocialOperationResult<T>.Failure(
            "social_music_provider_unavailable",
            "Spotify is not configured for Legend.");

    private static SocialOperationResult<T> SpotifyFailure<T>(HttpStatusCode statusCode) =>
        SocialOperationResult<T>.Failure(
            statusCode == HttpStatusCode.TooManyRequests
                ? "spotify_rate_limited"
                : "spotify_catalog_unavailable",
            statusCode == HttpStatusCode.TooManyRequests
                ? "Spotify is receiving too many requests. Try again shortly."
                : "Spotify music search is temporarily unavailable.");

    private sealed record SpotifyTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record SpotifySearchResponse(
        [property: JsonPropertyName("tracks")] SpotifyTrackPage? Tracks);

    private sealed record SpotifyTrackPage(
        [property: JsonPropertyName("items")] IReadOnlyList<SpotifyTrack>? Items);

    private sealed record SpotifyTrack(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("duration_ms")] int DurationMs,
        [property: JsonPropertyName("artists")] IReadOnlyList<SpotifyArtist>? Artists);

    private sealed record SpotifyArtist(
        [property: JsonPropertyName("name")] string? Name);
}
