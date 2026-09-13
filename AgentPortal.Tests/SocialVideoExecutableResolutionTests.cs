using System;
using System.IO;
using Infrastructure.Social;
using Xunit;

namespace AgentPortal.Tests;

public sealed class SocialVideoExecutableResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legend-video-runtime-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ffmpeg", "ffmpeg.exe")]
    [InlineData("ffprobe", "ffprobe.exe")]
    [InlineData("ffmpeg.exe", "ffmpeg.exe")]
    public void WindowsBareNameFindsPublishedExecutableWithoutPathDependency(string configured, string published)
    {
        var expected = Package(published);
        Assert.Equal(expected, LocalFfmpegSocialVideoProcessor.ResolvePackagedOrPath(configured, _root, true));
    }

    [Fact]
    public void UnixPackagedNameAndMissingPackagePreserveExistingResolution()
    {
        Package("ffmpeg.exe");
        Assert.Equal("ffmpeg", LocalFfmpegSocialVideoProcessor.ResolvePackagedOrPath("ffmpeg", _root, false));
        Assert.Equal("ffprobe", LocalFfmpegSocialVideoProcessor.ResolvePackagedOrPath("ffprobe", _root, true));
        var expected = Package("ffmpeg");
        Assert.Equal(expected, LocalFfmpegSocialVideoProcessor.ResolvePackagedOrPath("ffmpeg", _root, false));
    }

    [Fact]
    public void ExplicitExecutablePathCannotBeReplacedByPackagedNamesake()
    {
        Package("ffmpeg.exe");
        var configured = Path.Combine(_root, "configured", "ffmpeg.exe");
        Assert.Equal(configured, LocalFfmpegSocialVideoProcessor.ResolvePackagedOrPath(configured, _root, true));
    }

    private string Package(string name)
    {
        var directory = Path.Combine(_root, "tools", "ffmpeg");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name);
        File.WriteAllBytes(file, Array.Empty<byte>());
        return file;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
