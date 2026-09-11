using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Domain.Social;
using Infrastructure.Social;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class SocialMediaBlobRangeTests
{
    [Fact]
    public async Task BlobDelivery_FileStreamExecutorHonorsRangeOverNonSeekableHttpBody()
    {
        using var fixture = new RangeFixture();
        var read = await fixture.Storage.OpenReadAsync("originals/video.mp4");
        Assert.Equal(SocialMediaReadStatus.Available, read.Status);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=1000000-1000015";
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var result = new FileStreamResult(read.Content!, "video/mp4") { EnableRangeProcessing = true };
        await new FileStreamResultExecutor(NullLoggerFactory.Instance).ExecuteAsync(
            new ActionContext(context, new RouteData(), new ActionDescriptor()), result);
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 1000000-1000015/2097152", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(fixture.Handler.Bytes.Skip(1000000).Take(16).ToArray(), responseBody.ToArray());
        Assert.All(fixture.Handler.Downloads, request =>
        {
            Assert.Equal("\"version-one\"", request.IfMatch);
            Assert.InRange(request.Length, 1, 80 * 1024);
        });
    }

    [Fact]
    public async Task BlobDelivery_DefersBoundedReadsAndRefusesChangedVersion()
    {
        using var fixture = new RangeFixture();
        var read = await fixture.Storage.OpenReadAsync("originals/video.mp4");
        await using var stream = read.Content!;
        Assert.Empty(fixture.Handler.Downloads);
        Assert.True(stream.CanSeek);
        Assert.Equal(fixture.Handler.Bytes.Length, stream.Length);
        stream.Seek(1500000, SeekOrigin.Begin);
        fixture.Handler.ETag = "\"version-two\"";
        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => stream.ReadAsync(new byte[16]).AsTask());
        Assert.Equal(412, failure.Status);
        Assert.All(fixture.Handler.Downloads, request => Assert.Equal("\"version-one\"", request.IfMatch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobDelivery_LegacyMigrationSuccessOrRaceReturnsSeekableReaderAndRetainsOriginal(bool raced)
    {
        using var fixture = new RangeFixture();
        fixture.Handler.Exists = false;
        fixture.Handler.MigrationRace = raced;
        var local = Path.Combine(fixture.Root, "originals", "video.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await File.WriteAllBytesAsync(local, fixture.Handler.Bytes);
        var read = await fixture.Storage.OpenReadAsync("originals/video.mp4");
        await using var stream = read.Content!;
        Assert.Equal(SocialMediaReadStatus.Available, read.Status);
        Assert.True(stream.CanSeek);
        Assert.Equal(1, fixture.Handler.UploadAttempts);
        Assert.True(File.Exists(local));
        Assert.Empty(fixture.Handler.Downloads);
        stream.Position = 1024;
        var bytes = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(bytes));
        Assert.Equal(fixture.Handler.Bytes.Skip(1024).Take(5).ToArray(), bytes);
        Assert.All(fixture.Handler.Downloads, request => Assert.Equal("\"version-one\"", request.IfMatch));
    }

    [Fact]
    public async Task BlobDelivery_MissingWithoutLegacyFileDoesNotCreateObject()
    {
        using var fixture = new RangeFixture();
        fixture.Handler.Exists = false;
        var read = await fixture.Storage.OpenReadAsync("originals/missing.mp4");
        Assert.Equal(SocialMediaReadStatus.Missing, read.Status);
        Assert.Null(read.Content);
        Assert.Equal(0, fixture.Handler.UploadAttempts);
    }

    [Fact]
    public async Task BlobDelivery_CancelledCallerNeverStartsBodyDownload()
    {
        using var fixture = new RangeFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Storage.OpenReadAsync("originals/video.mp4", cancellation.Token));
        Assert.Empty(fixture.Handler.Downloads);
    }

    private sealed class RangeFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "legend-blob-range-" + Guid.NewGuid().ToString("N"));
        public RangeHandler Handler { get; } = new();
        public SocialMediaStorage Storage { get; }
        public RangeFixture()
        {
            Directory.CreateDirectory(Root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Social:Media:RootPath"] = Root,
                ["Social:Media:StorageConnectionString"] = "DefaultEndpointsProtocol=https;AccountName=legendmedia;AccountKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=;EndpointSuffix=core.windows.net",
                ["Social:Media:ContainerName"] = "range-fixture"
            }).Build();
            var options = new BlobClientOptions { Transport = new HttpClientTransport(Handler) };
            options.Retry.MaxRetries = 0;
            Storage = new SocialMediaStorage(config, NullLogger<SocialMediaStorage>.Instance, options);
        }
        public void Dispose() { Handler.Dispose(); Directory.Delete(Root, recursive: true); }
    }

    private sealed class RangeHandler : HttpMessageHandler
    {
        public byte[] Bytes { get; } = Enumerable.Range(0, 2 * 1024 * 1024).Select(x => (byte)(x % 251)).ToArray();
        public bool Exists { get; set; } = true;
        public bool MigrationRace { get; set; }
        public string ETag { get; set; } = "\"version-one\"";
        public int UploadAttempts { get; private set; }
        public List<(long Start, long Length, string? IfMatch)> Downloads { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Put)
            {
                UploadAttempts++;
                Assert.Equal("*", request.Headers.GetValues("If-None-Match").Single());
                Exists = true;
                return Task.FromResult(Response(MigrationRace ? HttpStatusCode.PreconditionFailed : HttpStatusCode.Created));
            }
            if (!Exists) return Task.FromResult(Response(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Head)
            {
                var properties = Response(HttpStatusCode.OK);
                properties.Content.Headers.ContentLength = Bytes.Length;
                return Task.FromResult(properties);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            var ifMatch = request.Headers.TryGetValues("If-Match", out var conditions) ? conditions.Single() : null;
            var range = request.Headers.TryGetValues("x-ms-range", out var ranges) ? ranges.Single() : request.Headers.Range?.ToString();
            var parts = range?.Replace("bytes=", "", StringComparison.Ordinal).Split('-');
            var start = parts == null ? 0 : long.Parse(parts[0]);
            var end = parts == null || string.IsNullOrEmpty(parts[1]) ? Bytes.Length - 1 : Math.Min(long.Parse(parts[1]), Bytes.Length - 1);
            Downloads.Add((start, end - start + 1, ifMatch));
            if (ifMatch != null && ifMatch != ETag) return Task.FromResult(Response(HttpStatusCode.PreconditionFailed));
            var response = Response(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent);
            response.Content = new StreamContent(new NonSeekableBody(Bytes.Skip((int)start).Take((int)(end - start + 1)).ToArray()));
            response.Content.Headers.ContentLength = end - start + 1;
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            if (range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Bytes.Length);
            return Task.FromResult(response);
        }
        private HttpResponseMessage Response(HttpStatusCode status)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
            response.Headers.ETag = new EntityTagHeaderValue(ETag);
            response.Headers.Add("x-ms-blob-type", "BlockBlob");
            response.Headers.Add("x-ms-request-id", "controlled-test");
            response.Headers.Add("x-ms-version", "2023-11-03");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            if (status == HttpStatusCode.NotFound) response.Headers.Add("x-ms-error-code", "BlobNotFound");
            if (status == HttpStatusCode.PreconditionFailed) response.Headers.Add("x-ms-error-code", "ConditionNotMet");
            return response;
        }
    }

    private sealed class NonSeekableBody(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
