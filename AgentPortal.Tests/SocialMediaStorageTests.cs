using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Infrastructure.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class SocialMediaStorageTests
{
    [Fact]
    public async Task LocalVideoUpload_DefersFfmpegUntilTheHostedLifecycle()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"legend-social-media-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Social:Media:RootPath"] = root,
                    ["Social:Media:MaximumBytes"] = "104857600",
                    ["Social:Media:FFmpeg:ExecutablePath"] = Path.Combine(root, "missing-ffmpeg")
                })
                .Build();
            var storage = new SocialMediaStorage(
                configuration,
                NullLogger<SocialMediaStorage>.Instance);
            var content = new byte[]
            {
                0, 0, 0, 24,
                (byte)'f', (byte)'t', (byte)'y', (byte)'p',
                (byte)'i', (byte)'s', (byte)'o', (byte)'m',
                0, 0, 0, 0,
                (byte)'i', (byte)'s', (byte)'o', (byte)'m'
            };

            await using var stream = new MemoryStream(content);
            var result = await storage.StoreAsync(
                Guid.NewGuid(),
                "legend-hac.mp4",
                content.Length,
                stream);

            Assert.True(result.Succeeded);
            Assert.True(result.Media!.RequiresBackgroundProcessing);
            Assert.Single(Directory.EnumerateFiles(
                root,
                "*.mp4",
                SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalVideoUpload_RejectsOverLimitMp4FromItsInitialHeaderBeforeFfmpeg()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"legend-social-media-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Social:Media:RootPath"] = root,
                    ["Social:Media:MaximumBytes"] = "104857600",
                    ["Social:Media:FFmpeg:ExecutablePath"] = Path.Combine(root, "missing-ffmpeg")
                })
                .Build();
            var storage = new SocialMediaStorage(
                configuration,
                NullLogger<SocialMediaStorage>.Instance);
            var content = Mp4WithMovieHeaderDuration(
                timescale: 1_000,
                duration: 600_001);

            await using var stream = new MemoryStream(content);
            var result = await storage.StoreAsync(
                Guid.NewGuid(),
                "too-long.mp4",
                content.Length,
                stream);

            Assert.False(result.Succeeded);
            Assert.Equal("SOCIAL_VIDEO_DURATION_EXCEEDED", result.ErrorCode);
            Assert.Empty(Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BlobUpload_UsesThePreProvisionedContainer_WithoutAttemptingContainerManagement()
    {
        using var handler = new RecordingBlobHandler();
        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(handler)
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Social:Media:StorageConnectionString"] =
                    "DefaultEndpointsProtocol=https;" +
                    "AccountName=legendmedia;" +
                    "AccountKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=;" +
                    "EndpointSuffix=core.windows.net",
                ["Social:Media:ContainerName"] = "legend-social-media",
                ["Social:Media:MaximumBytes"] = "104857600"
            })
            .Build();
        var storage = new SocialMediaStorage(
            configuration,
            NullLogger<SocialMediaStorage>.Instance,
            options);
        var content = new byte[] { 1, 2, 3, 4 };

        await using var stream = new MemoryStream(content);
        var result = await storage.StoreAsync(
            Guid.NewGuid(),
            "legend-photo.jpg",
            content.Length,
            stream);

        Assert.True(result.Succeeded);
        Assert.NotEmpty(handler.RequestUris);
        Assert.DoesNotContain(
            handler.RequestUris,
            uri => string.Equals(
                uri.AbsolutePath,
                "/legend-social-media",
                StringComparison.Ordinal));
        Assert.All(
            handler.RequestUris,
            uri => Assert.StartsWith(
                "/legend-social-media/originals/",
                uri.AbsolutePath,
                StringComparison.Ordinal));
    }

    private sealed class RecordingBlobHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.NotNull(request.RequestUri);
            RequestUris.Add(request.RequestUri!);

            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new ByteArrayContent([])
            };
            response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString("N"));
            response.Headers.Add("x-ms-version", "2023-11-03");
            response.Headers.Date = DateTimeOffset.UtcNow;
            response.Headers.ETag = new EntityTagHeaderValue("\"legend-test\"");
            return Task.FromResult(response);
        }
    }

    private static byte[] Mp4WithMovieHeaderDuration(uint timescale, uint duration)
    {
        var mvhd = new byte[28];
        WriteUInt32(mvhd, 0, (uint)mvhd.Length);
        WriteAscii(mvhd, 4, "mvhd");
        // Version and flags, followed by creation and modification timestamps,
        // are intentionally zero. The parser only needs timescale and duration.
        WriteUInt32(mvhd, 20, timescale);
        WriteUInt32(mvhd, 24, duration);

        var moov = new byte[mvhd.Length + 8];
        WriteUInt32(moov, 0, (uint)moov.Length);
        WriteAscii(moov, 4, "moov");
        mvhd.CopyTo(moov, 8);

        var ftyp = new byte[16];
        WriteUInt32(ftyp, 0, (uint)ftyp.Length);
        WriteAscii(ftyp, 4, "ftyp");
        WriteAscii(ftyp, 8, "isom");
        WriteAscii(ftyp, 12, "iso2");

        return ftyp.Concat(moov).ToArray();
    }

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteAscii(byte[] target, int offset, string value)
    {
        for (var index = 0; index < value.Length; index++)
            target[offset + index] = (byte)value[index];
    }
}
