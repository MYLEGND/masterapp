using System;
using System.Collections.Generic;
using System.IO;
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
                ["Social:Media:BlobContainerUrl"] = "https://legendmedia.blob.core.windows.net/legend-social-media",
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
}
