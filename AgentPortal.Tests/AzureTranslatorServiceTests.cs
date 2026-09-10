using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    public async Task PlainLabels_UsePlainTransportAndMixedBatchesPreserveOrder()
    {
        var handler = new RecordingHandler(request =>
        {
            var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var values = body.RootElement.EnumerateArray().Select(item => item.EnumerateObject().Single().Value.GetString()!).ToArray();
            var html = request.RequestUri!.Query.Contains("textType=html", StringComparison.Ordinal);
            foreach (var value in values)
                Assert.Equal(value.Contains("notranslate", StringComparison.Ordinal), html);
            return JsonResponse(JsonSerializer.Serialize(values.Select(value => new
            {
                translations = new[] { new { text = value.Replace("Title", "Tit", StringComparison.Ordinal).Replace("Hello", "Bonjou", StringComparison.Ordinal), to = "ht" } }
            })));
        });
        var service = CreateService(handler);
        var single = await service.TranslateAsync("Title", "ht", "en");
        Assert.True(single.Succeeded);
        Assert.Equal("Tit", single.TranslatedText);
        Assert.Equal(5, service.RequestCharacterCount("Title"));
        var batch = await service.TranslateBatchAsync(["Title", "Hello {name}", "Hello"], "ht", "en");
        Assert.All(batch, result => Assert.True(result.Succeeded));
        Assert.Equal(new[] { "Tit", "Bonjou {name}", "Bonjou" }, batch.Select(result => result.TranslatedText));
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task TranslateBatch_ProtectsAndRestoresPlaceholdersMarkupUrlsAndNewlines()
    {
        const string source = "Welcome, {name}.\nRead <b>{count}</b> at https://mylegnd.com";
        var handler = new RecordingHandler(request =>
        {
            var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var text = body.RootElement[0].EnumerateObject().Single().Value.GetString()!;
            Assert.Contains("notranslate", text, StringComparison.Ordinal);
            Assert.DoesNotContain("{name}", text, StringComparison.Ordinal);
            return JsonResponse(JsonSerializer.Serialize(new[] { new { translations = new[] { new { text = text.Replace("Welcome", "Byenveni", StringComparison.Ordinal), to = "ht" } } } }));
        });
        var service = CreateService(handler);
        var result = Assert.Single(await service.TranslateBatchAsync([source], "ht", "en"));
        Assert.True(result.Succeeded);
        Assert.Equal(source.Replace("Welcome", "Byenveni", StringComparison.Ordinal), result.TranslatedText);
        Assert.Contains("textType=html", handler.RequestUri!.Query, StringComparison.Ordinal);
        Assert.True(service.RequestCharacterCount(source) >= source.Length);
    }

    [Fact]
    public void ProtectedProviderOutput_RejectsMissingOrDuplicatedLiterals()
    {
        var source = AzureProtectedText.Create("Hello {name}\nNext");
        Assert.Equal("Hello {name}\nNext", source.Restore(source.Text));
        Assert.Null(source.Restore(source.Text.Replace("__legend_literal_0__", "", StringComparison.Ordinal)));
        Assert.Null(source.Restore(source.Text + "__legend_literal_0__"));
    }

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

    [Fact]
    public async Task Translate_RetriesTransientThrottleWithinBound_ThenSucceeds()
    {
        var responseCount = 0;
        var handler = new RecordingHandler(_ => ++responseCount < 3
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : JsonResponse("[{\"translations\":[{\"text\":\"Bonjou\",\"to\":\"ht\"}]}]"));
        var service = CreateService(handler);

        var result = await service.TranslateAsync("Hello", "ht", "en");

        Assert.True(result.Succeeded);
        Assert.Equal("Bonjou", result.TranslatedText);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Translate_DoesNotRetryPermanentProviderFailure()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var service = CreateService(handler);

        var result = await service.TranslateAsync("Hello", "ht", "en");

        Assert.False(result.Succeeded);
        Assert.Equal("translation_provider_failed", result.ErrorCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Translate_TimeoutRetriesOnlyWithinBound_ThenFailsSafely()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("provider timeout")));
        var service = CreateService(handler);

        var result = await service.TranslateAsync("Hello", "ht", "en");

        Assert.False(result.Succeeded);
        Assert.Equal("translation_provider_timeout", result.ErrorCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Translate_InvalidProviderPayloadFailsSafelyWithoutInventingCopy()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{not-json"));
        var service = CreateService(handler);

        var result = await service.TranslateAsync("Hello", "ht", "en");

        Assert.False(result.Succeeded);
        Assert.Null(result.TranslatedText);
        Assert.Equal("translation_provider_failed", result.ErrorCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Translate_InvalidLanguageNeverCallsProvider()
    {
        var handler = new RecordingHandler(_ => JsonResponse("[]"));
        var service = CreateService(handler);

        var result = await service.TranslateAsync("Hello", "not a language!", "en");

        Assert.False(result.Succeeded);
        Assert.Equal("translation_language_unsupported", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TranslateBatch_UsesOneProviderRequestAndPreservesResultOrder()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            "[{\"translations\":[{\"text\":\"Youn\",\"to\":\"ht\"}]},{\"translations\":[{\"text\":\"De\",\"to\":\"ht\"}]}]"));
        var service = CreateService(handler);

        var result = await service.TranslateBatchAsync(["One", "Two"], "ht", "en");

        Assert.Equal(2, result.Count);
        Assert.Equal("Youn", result[0].TranslatedText);
        Assert.Equal("De", result[1].TranslatedText);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("One", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("Two", handler.RequestBody, StringComparison.Ordinal);
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
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = (request, _) => Task.FromResult(response(request));
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        {
            _response = response;
        }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? Region { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Region = request.Headers.TryGetValues("Ocp-Apim-Subscription-Region", out var values)
                ? values.Single()
                : null;
            RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
            return _response(request, cancellationToken);
        }
    }
}
