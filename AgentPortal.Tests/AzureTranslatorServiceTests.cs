using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AzureTranslatorServiceTests
{
    [Fact]
    public async Task DetectLanguage_UsesTheExistingV3ContractAndNormalizesHaitianCreole()
    {
        var handler = new RecordingHandler(_ => JsonResponse("[{\"language\":\"ht\",\"score\":1.0}]"));
        var service = CreateService(handler);

        var result = await service.DetectLanguageAsync("Bonjou, kijan ou ye?");

        Assert.True(result.Succeeded);
        Assert.Equal("ht", result.Language);
        Assert.Equal(1.0m, result.Confidence);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/detect", handler.RequestUri!.AbsolutePath);
        Assert.Contains("api-version=3.0", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal("test-region", handler.Region);
        Assert.Contains("Bonjou", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectLanguage_LowConfidenceFailsClosedAsAmbiguous()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            "[{\"language\":\"en\",\"score\":0.49}]"));
        var service = CreateService(handler);

        var result = await service.DetectLanguageAsync("Bon");

        Assert.False(result.Succeeded);
        Assert.Null(result.Language);
        Assert.Equal("translation_language_ambiguous", result.ErrorCode);
        Assert.Equal(0.49m, result.Confidence);
    }

    [Fact]
    public async Task Translate_UsesTheRequestedRecipientLanguageAndExplicitDetectedSource()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            "[{\"translations\":[{\"text\":\"Bonjou, kijan ou ye?\",\"to\":\"ht\"}]}]"));
        var service = CreateService(handler);

        var result = await service.TranslateAsync(
            "Hello, how are you?",
            "ht",
            "en");

        Assert.True(result.Succeeded);
        Assert.Equal("Bonjou, kijan ou ye?", result.TranslatedText);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal("AzureTranslator", result.Provider);
        Assert.Equal("/translate", handler.RequestUri!.AbsolutePath);
        Assert.Contains("api-version=3.0", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("to=ht", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("from=en", handler.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingConfiguration_ReturnsAnExplicitUnavailableResult()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var service = new AzureTranslatorService(
            factory.Object,
            new ConfigurationBuilder().Build(),
            NullLogger<AzureTranslatorService>.Instance);

        var detection = await service.DetectLanguageAsync("Hello");
        var translation = await service.TranslateAsync("Hello", "ht", "en");

        Assert.False(detection.Succeeded);
        Assert.Equal("translation_provider_unavailable", detection.ErrorCode);
        Assert.False(translation.Succeeded);
        Assert.Equal("translation_provider_unavailable", translation.ErrorCode);
        factory.VerifyNoOtherCalls();
    }

    private static AzureTranslatorService CreateService(RecordingHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(value => value.CreateClient("AzureTranslator"))
            .Returns(new HttpClient(handler));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTranslator:Endpoint"] = "https://translator.example.test",
                ["AzureTranslator:Key"] = "unit-test-key",
                ["AzureTranslator:Region"] = "test-region"
            })
            .Build();
        return new AzureTranslatorService(
            factory.Object,
            configuration,
            NullLogger<AzureTranslatorService>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? Region { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Region = request.Headers.TryGetValues("Ocp-Apim-Subscription-Region", out var values)
                ? values.Single()
                : null;
            RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
            return Task.FromResult(_response(request));
        }
    }
}
