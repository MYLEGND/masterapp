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

public sealed class SocialMediaBlobProcessingTests
{
    // These tests execute the real process-launch authority with controlled
    // executable fixtures; they test storage orchestration, not media codecs.
    public sealed class UnixVideoTheoryAttribute : TheoryAttribute
    {
        public UnixVideoTheoryAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "Controlled executable fixtures require a POSIX shell.";
        }
    }

    [UnixVideoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobVideo_UsesExistingProcessorAndConditionallyReplacesOnlyObservedVersion(bool conflict)
    {
        using var fixture = new BlobFixture();
        await fixture.PrepareProcessorAsync();
        fixture.Handler.Conflict = conflict;
        var storage = fixture.CreateStorage();
        var result = await storage.ProcessAsync("originals/2026/09/asset/source.mp4");
        Assert.Equal(!conflict, result.Succeeded);
        Assert.Equal("\"original\"", fixture.Handler.IfMatch);
        Assert.Equal(1, fixture.Handler.UploadAttempts);
        if (conflict)
        {
            Assert.Equal("SOCIAL_VIDEO_SOURCE_CHANGED", result.ErrorCode);
            Assert.Equal(fixture.Original, fixture.Handler.Persisted);
        }
        else
        {
            Assert.Equal(fixture.Original.Concat(new byte[] { 42 }).ToArray(), fixture.Handler.Persisted);
            Assert.Equal(fixture.Handler.Persisted.Length, result.FileSizeBytes);
        }
        Assert.Empty(fixture.Workspaces());
        Assert.Equal("keep", await File.ReadAllTextAsync(fixture.UnrelatedFile));
    }

    [Theory]
    [InlineData(257, 257, "SOCIAL_VIDEO_SIZE_INVALID")]
    [InlineData(257, 12, "SOCIAL_VIDEO_SIZE_INVALID")]
    [InlineData(48, 49, "SOCIAL_MEDIA_SIZE_MISMATCH")]
    public async Task BlobVideo_RejectsOversizedOrIncompleteSourceWithoutOverwriting(int actual, int declared, string code)
    {
        using var fixture = new BlobFixture();
        fixture.Handler.Persisted = new byte[actual];
        fixture.Handler.DeclaredLength = declared;
        var result = await fixture.CreateStorage(maximumBytes: 128).ProcessAsync("originals/source.mp4");
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(0, fixture.Handler.UploadAttempts);
        Assert.Empty(fixture.Workspaces());
    }

    [Fact]
    public async Task BlobVideo_MissingProcessorRetainsBlobAndCleansTemporaryFiles()
    {
        using var fixture = new BlobFixture();
        var result = await fixture.CreateStorage().ProcessAsync("originals/source.mp4");
        Assert.False(result.Succeeded);
        Assert.NotEqual("SOCIAL_VIDEO_PROCESSING_LOCAL_REQUIRED", result.ErrorCode);
        Assert.Equal(fixture.Original, fixture.Handler.Persisted);
        Assert.Equal(0, fixture.Handler.UploadAttempts);
        Assert.Empty(fixture.Workspaces());
    }

    [Fact]
    public async Task BlobVideo_CancellationDuringDownloadCleansOnlyOwnWorkspace()
    {
        using var fixture = new BlobFixture();
        fixture.Handler.CancelDownload = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CreateStorage().ProcessAsync("originals/source.mp4"));
        Assert.Equal(0, fixture.Handler.UploadAttempts);
        Assert.Empty(fixture.Workspaces());
        Assert.True(File.Exists(fixture.UnrelatedFile));
    }

    private sealed class BlobFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "legend-blob-video-" + Guid.NewGuid().ToString("N"));
        public byte[] Original { get; } = new byte[48];
        public VideoBlobHandler Handler { get; }
        public string UnrelatedFile => Path.Combine(Root, "unrelated.mp4");
        public BlobFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(UnrelatedFile, "keep");
            Handler = new VideoBlobHandler { Persisted = Original.ToArray() };
        }
        public IEnumerable<string> Workspaces() => Directory.Exists(Path.Combine(Root, ".processing"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(Root, ".processing")) : Array.Empty<string>();
        public SocialMediaStorage CreateStorage(int maximumBytes = 1024)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Social:Media:StorageConnectionString"] = "DefaultEndpointsProtocol=https;AccountName=legendmedia;AccountKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=;EndpointSuffix=core.windows.net",
                ["Social:Media:ContainerName"] = "controlled-test",
                ["Social:Media:RootPath"] = Root,
                ["Social:Media:MaximumBytes"] = maximumBytes.ToString(),
                ["Social:Media:FFmpeg:ExecutablePath"] = Path.Combine(Root, "processor"),
                ["Social:Media:FFmpeg:ProbeExecutablePath"] = Path.Combine(Root, "probe")
            }).Build();
            var options = new BlobClientOptions { Transport = new HttpClientTransport(Handler) };
            options.Retry.MaxRetries = 0;
            return new SocialMediaStorage(configuration, NullLogger<SocialMediaStorage>.Instance, options);
        }
        public async Task PrepareProcessorAsync()
        {
            await File.WriteAllTextAsync(Path.Combine(Root, "probe"), "#!/bin/sh\nprintf '1.0\\n'\n");
            await File.WriteAllTextAsync(Path.Combine(Root, "processor"), """
                #!/bin/sh
                input=''
                last=''
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = '-i' ]; then shift; input="$1"; fi
                  last="$1"
                  shift
                done
                cat "$input" > "$last"
                printf '*' >> "$last"
                """ + "\n");
            if (!OperatingSystem.IsWindows())
                foreach (var name in new[] { "probe", "processor" })
                    File.SetUnixFileMode(Path.Combine(Root, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose()
        {
            Handler.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class VideoBlobHandler : HttpMessageHandler
    {
        public byte[] Persisted { get; set; } = [];
        public long? DeclaredLength { get; set; }
        public bool Conflict { get; set; }
        public bool CancelDownload { get; set; }
        public int UploadAttempts { get; private set; }
        public string? IfMatch { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = Response(HttpStatusCode.OK);
                response.Content = CancelDownload
                    ? new StreamContent(new CancelledDownloadStream()) : new ByteArrayContent(Persisted);
                response.Content.Headers.ContentLength = DeclaredLength ?? Persisted.Length;
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                response.Headers.Add("x-ms-blob-type", "BlockBlob");
                return response;
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            UploadAttempts++;
            IfMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null;
            Assert.Equal("\"original\"", IfMatch);
            if (Conflict)
            {
                var failure = Response(HttpStatusCode.PreconditionFailed);
                failure.Headers.Add("x-ms-error-code", "ConditionNotMet");
                return failure;
            }
            Persisted = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return Response(HttpStatusCode.Created);
        }
        private static HttpResponseMessage Response(HttpStatusCode status)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
            response.Headers.ETag = new EntityTagHeaderValue("\"original\"");
            response.Headers.Add("x-ms-request-id", "controlled-test");
            response.Headers.Add("x-ms-version", "2023-11-03");
            response.Headers.Date = DateTimeOffset.UtcNow;
            return response;
        }
    }

    private sealed class CancelledDownloadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new OperationCanceledException());
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException<int>(new OperationCanceledException());
    }
}
