using System.Net.Http.Json;
using System.Text.Json;
using Domain.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

/// <summary>
/// Server-only Azure Translator v3 client. It is deliberately fail-soft: a
/// missing key, timeout, or provider error returns an unavailable result and
/// never blocks the authoritative messaging write.
/// </summary>
internal sealed class AzureTranslatorService : ITranslationService
{
    private const string ProviderName = "AzureTranslator";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureTranslatorService> _logger;

    public AzureTranslatorService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AzureTranslatorService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TranslationDetectionResult> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetConfiguration(out var endpoint, out var key, out var region))
            return new TranslationDetectionResult(false, null, "translation_provider_unavailable");

        try
        {
            using var request = CreateRequest(endpoint, "/detect?api-version=3.0", key, region, text);
            using var response = await _httpClientFactory.CreateClient("AzureTranslator")
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Azure Translator detection failed. StatusCode={StatusCode}", (int)response.StatusCode);
                return new TranslationDetectionResult(false, null, "translation_provider_failed");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var language = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0 &&
                           document.RootElement[0].TryGetProperty("language", out var property)
                ? CommunicationLanguages.NormalizeOrNull(property.GetString())
                : null;
            return language is null
                ? new TranslationDetectionResult(false, null, "translation_language_unsupported")
                : new TranslationDetectionResult(true, language);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TranslationDetectionResult(false, null, "translation_provider_timeout");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Azure Translator detection request failed.");
            return new TranslationDetectionResult(false, null, "translation_provider_failed");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Azure Translator detection response was invalid.");
            return new TranslationDetectionResult(false, null, "translation_provider_failed");
        }
    }

    public async Task<TranslationProviderResult> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        if (!CommunicationLanguages.TryNormalize(targetLanguage, out var normalizedTarget))
            return new TranslationProviderResult(false, null, null, ProviderName, "translation_language_unsupported");
        if (!TryGetConfiguration(out var endpoint, out var key, out var region))
            return new TranslationProviderResult(false, null, null, ProviderName, "translation_provider_unavailable");

        var source = CommunicationLanguages.NormalizeOrNull(sourceLanguage);
        var path = $"/translate?api-version=3.0&to={Uri.EscapeDataString(normalizedTarget)}" +
                   (source is null ? string.Empty : $"&from={Uri.EscapeDataString(source)}");
        try
        {
            using var request = CreateRequest(endpoint, path, key, region, text);
            using var response = await _httpClientFactory.CreateClient("AzureTranslator")
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Azure Translator translation failed. StatusCode={StatusCode} TargetLanguage={TargetLanguage}", (int)response.StatusCode, normalizedTarget);
                return new TranslationProviderResult(false, null, null, ProviderName, "translation_provider_failed");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0 ||
                !document.RootElement[0].TryGetProperty("translations", out var translations) ||
                translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0 ||
                !translations[0].TryGetProperty("text", out var translatedText))
            {
                return new TranslationProviderResult(false, null, null, ProviderName, "translation_provider_failed");
            }

            var detected = source;
            if (detected is null && document.RootElement[0].TryGetProperty("detectedLanguage", out var detectedLanguage) &&
                detectedLanguage.TryGetProperty("language", out var detectedValue))
            {
                detected = CommunicationLanguages.NormalizeOrNull(detectedValue.GetString());
            }

            var translated = translatedText.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(translated)
                ? new TranslationProviderResult(false, null, detected, ProviderName, "translation_provider_failed")
                : new TranslationProviderResult(true, translated, detected, ProviderName);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TranslationProviderResult(false, null, null, ProviderName, "translation_provider_timeout");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Azure Translator translation request failed. TargetLanguage={TargetLanguage}", normalizedTarget);
            return new TranslationProviderResult(false, null, null, ProviderName, "translation_provider_failed");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Azure Translator translation response was invalid. TargetLanguage={TargetLanguage}", normalizedTarget);
            return new TranslationProviderResult(false, null, null, ProviderName, "translation_provider_failed");
        }
    }

    private bool TryGetConfiguration(out string endpoint, out string key, out string? region)
    {
        endpoint = (_configuration["AzureTranslator:Endpoint"] ?? string.Empty).Trim().TrimEnd('/');
        key = (_configuration["AzureTranslator:Key"] ??
               Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_KEY") ?? string.Empty).Trim();
        region = (_configuration["AzureTranslator:Region"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(region))
            region = null;

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) &&
               endpointUri.Scheme == Uri.UriSchemeHttps &&
               !string.IsNullOrWhiteSpace(key);
    }

    private static HttpRequestMessage CreateRequest(
        string endpoint,
        string path,
        string key,
        string? region,
        string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint + path)
        {
            Content = JsonContent.Create(new[] { new AzureTextInput(text) })
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Headers.Add("X-ClientTraceId", Guid.NewGuid().ToString("D"));
        if (!string.IsNullOrWhiteSpace(region))
            request.Headers.Add("Ocp-Apim-Subscription-Region", region);
        return request;
    }

    private sealed record AzureTextInput(string Text);
}
