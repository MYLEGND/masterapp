using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Infrastructure.Social.OpenMusic;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CuratedOpenMusicCatalogTests
{
    [Fact]
    public async Task Search_ReturnsCleanChristianTracksWithDirectHttpsMp3Streams()
    {
        var catalog = new CuratedOpenMusicCatalog();

        var result = await catalog.SearchAsync("Christian music");

        Assert.True(result.Succeeded);
        var tracks = Assert.IsAssignableFrom<IReadOnlyList<Domain.Social.SocialMusicTrack>>(result.Value);
        Assert.True(tracks.Count >= 3);
        Assert.All(tracks, track =>
        {
            Assert.Equal(CuratedOpenMusicCatalog.ProviderId, track.ProviderId);
            Assert.True(track.TrackDurationSeconds > 0);
            Assert.True(Uri.TryCreate(track.AudioUrl, UriKind.Absolute, out var streamUrl));
            Assert.Equal(Uri.UriSchemeHttps, streamUrl!.Scheme);
            Assert.EndsWith(".mp3", streamUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Resolve_OnlyAcceptsTheCuratedCatalogProviderAndTrackIdentity()
    {
        var catalog = new CuratedOpenMusicCatalog();
        var search = await catalog.SearchAsync("Yeshua");
        Assert.True(search.Succeeded);
        var track = Assert.Single(search.Value!);

        var resolved = await catalog.ResolveAsync(track.ProviderId, track.ProviderTrackId);
        var rejectedProvider = await catalog.ResolveAsync("untrusted-provider", track.ProviderTrackId);

        Assert.True(resolved.Succeeded);
        Assert.Equal(track, resolved.Value);
        Assert.False(rejectedProvider.Succeeded);
        Assert.Equal("open_music_provider_invalid", rejectedProvider.ErrorCode);
    }
}
