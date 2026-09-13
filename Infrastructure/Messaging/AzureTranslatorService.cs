using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Domain.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

/// <summary>
/// Server-only Azure Translator v3 client. It is deliberately fail-soft: a
/// missing key, timeout, or provider error returns an unavailable result and
/// never blocks the authoritative messaging write.
/// </summary>
internal sealed class AzureTranslatorService : ITranslationProvider
{
    private const string ProviderIdentifier = "AzureTranslator";
    private const decimal MinimumLanguageIdentificationConfidence = 0.50m;
    private const int MaximumAttempts = 3;
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

    public string ProviderName => ProviderIdentifier;
    public string ProviderVersion => "text-api-v3.0";
    public int RequestCharacterCount(string text)
    {
        var prepared = AzureProtectedText.Create(text);
        return prepared.Literals.Count == 0 ? text.Length : prepared.Text.Length;
    }

    public Task<TranslationDetectionResult> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        DetectLanguageAsync(text, cancellationToken, null);

    public async Task<TranslationDetectionResult> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken,
        LegendConnectExternalProviderPolicy? providerPolicy)
    {
        // This is an external provider boundary. A native-only request must
        // never reach it, so it fails closed before any configuration is read
        // or any client is constructed.
        if (LegendConnectExternalProviderPolicy.Resolve(providerPolicy)
            .ForbidsExternalProviders)
        {
            return new TranslationDetectionResult(
                false,
                null,
                "external_provider_forbidden_by_native_only_policy");
        }

        if (!TryGetConfiguration(out var endpoint, out var key, out var region))
            return new TranslationDetectionResult(false, null, "translation_provider_unavailable");

        try
        {
            using var response = await SendWithBoundedRetryAsync(
                () => CreateRequest(endpoint, "/detect?api-version=3.0", key, region, text),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Azure Translator detection failed. StatusCode={StatusCode}", (int)response.StatusCode);
                return new TranslationDetectionResult(false, null, "translation_provider_failed");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                return new TranslationDetectionResult(
                    false,
                    null,
                    "translation_provider_failed");
            }

            var candidate = document.RootElement[0];
            var language = candidate.TryGetProperty("language", out var property)
                ? CommunicationLanguages.NormalizeOrNull(property.GetString())
                : null;
            if (language is null)
            {
                return new TranslationDetectionResult(
                    false,
                    null,
                    "translation_language_unsupported");
            }

            if (!candidate.TryGetProperty("score", out var scoreProperty) ||
                !scoreProperty.TryGetDecimal(out var confidence))
            {
                return new TranslationDetectionResult(
                    false,
                    null,
                    "translation_provider_failed");
            }

            return confidence < MinimumLanguageIdentificationConfidence
                ? new TranslationDetectionResult(
                    false,
                    null,
                    "translation_language_ambiguous",
                    confidence)
                : new TranslationDetectionResult(
                    true,
                    language,
                    Confidence: confidence);
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

    public Task<TranslationProviderResult> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default) =>
        TranslateAsync(text, targetLanguage, sourceLanguage, cancellationToken, null);

    public async Task<TranslationProviderResult> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage,
        CancellationToken cancellationToken,
        LegendConnectExternalProviderPolicy? providerPolicy)
    {
        if (LegendConnectExternalProviderPolicy.Resolve(providerPolicy)
            .ForbidsExternalProviders)
        {
            return new TranslationProviderResult(
                false,
                null,
                null,
                ProviderIdentifier,
                "external_provider_forbidden_by_native_only_policy");
        }

        if (!CommunicationLanguages.TryNormalize(targetLanguage, out var normalizedTarget))
            return new TranslationProviderResult(false, null, null, ProviderIdentifier, "translation_language_unsupported");
        if (!TryGetConfiguration(out var endpoint, out var key, out var region))
            return new TranslationProviderResult(false, null, null, ProviderIdentifier, "translation_provider_unavailable");

        var protectedText = AzureProtectedText.Create(text);
        var useHtml = protectedText.Literals.Count > 0;
        var source = CommunicationLanguages.NormalizeOrNull(sourceLanguage);
        var path = $"/translate?api-version=3.0&textType={(useHtml ? "html" : "plain")}&to={Uri.EscapeDataString(normalizedTarget)}" +
                   (source is null ? string.Empty : $"&from={Uri.EscapeDataString(source)}");
        try
        {
            using var response = await SendWithBoundedRetryAsync(
                () => CreateRequest(endpoint, path, key, region, useHtml ? protectedText.Text : text),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Azure Translator translation failed. StatusCode={StatusCode} TargetLanguage={TargetLanguage}", (int)response.StatusCode, normalizedTarget);
                return new TranslationProviderResult(false, null, null, ProviderIdentifier, "translation_provider_failed");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0 ||
                !document.RootElement[0].TryGetProperty("translations", out var translations) ||
                translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0 ||
                !translations[0].TryGetProperty("text", out var translatedText))
            {
                return new TranslationProviderResult(false, null, null, ProviderIdentifier, "translation_provider_failed");
            }

            var detected = source;
            if (detected is null && document.RootElement[0].TryGetProperty("detectedLanguage", out var detectedLanguage) &&
                detectedLanguage.TryGetProperty("language", out var detectedValue))
            {
                detected = CommunicationLanguages.NormalizeOrNull(detectedValue.GetString());
            }

            var translated = useHtml ? protectedText.Restore(translatedText.GetString()) : translatedText.GetString();
            return string.IsNullOrWhiteSpace(translated)
                ? new TranslationProviderResult(false, null, detected, ProviderIdentifier, "translation_provider_failed")
                : new TranslationProviderResult(true, translated, detected, ProviderIdentifier);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TranslationProviderResult(false, null, null, ProviderIdentifier, "translation_provider_timeout");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Azure Translator translation request failed. TargetLanguage={TargetLanguage}", normalizedTarget);
            return new TranslationProviderResult(false, null, null, ProviderIdentifier, "translation_provider_failed");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Azure Translator translation response was invalid. TargetLanguage={TargetLanguage}", normalizedTarget);
            return new TranslationProviderResult(false, null, null, ProviderIdentifier, "translation_provider_failed");
        }
    }

    public async Task<IReadOnlyList<TranslationProviderResult>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
            return Array.Empty<TranslationProviderResult>();
        if (texts.Count > 100 || texts.Sum(RequestCharacterCount) > 50_000)
            return BatchFailure(texts.Count, "translation_batch_invalid");

        // HTML protection is needed only for literals. Keep plain labels in plain
        // mode, preserving the existing ordinary-text transport contract.
        // Each source is sent exactly once under the caller's aggregate reservation.
        var results = new TranslationProviderResult[texts.Count];
        foreach (var group in texts.Select((text, index) => (text, index))
                     .GroupBy(item => AzureProtectedText.Create(item.text).Literals.Count > 0))
        {
            var items = group.ToArray();
            var translated = await TranslateBatchCoreAsync(items.Select(item => item.text).ToArray(),
                targetLanguage, sourceLanguage, group.Key, cancellationToken);
            for (var index = 0; index < items.Length; index++)
                results[items[index].index] = translated[index];
        }
        return results;
    }

    private async Task<IReadOnlyList<TranslationProviderResult>> TranslateBatchCoreAsync(
        IReadOnlyList<string> texts, string targetLanguage, string? sourceLanguage,
        bool useHtml, CancellationToken cancellationToken)
    {
        var protectedTexts = texts.Select(AzureProtectedText.Create).ToArray();
        if (!CommunicationLanguages.TryNormalize(targetLanguage, out var normalizedTarget))
            return BatchFailure(texts.Count, "translation_language_unsupported");
        if (!TryGetConfiguration(out var endpoint, out var key, out var region))
            return BatchFailure(texts.Count, "translation_provider_unavailable");

        var source = CommunicationLanguages.NormalizeOrNull(sourceLanguage);
        var path = $"/translate?api-version=3.0&textType={(useHtml ? "html" : "plain")}&to={Uri.EscapeDataString(normalizedTarget)}" +
                   (source is null ? string.Empty : $"&from={Uri.EscapeDataString(source)}");
        try
        {
            using var response = await SendWithBoundedRetryAsync(
                () => CreateRequest(endpoint, path, key, region, useHtml ? protectedTexts.Select(text => text.Text).ToArray() : texts),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Azure Translator batch failed. StatusCode={StatusCode} TargetLanguage={TargetLanguage} ItemCount={ItemCount}",
                    (int)response.StatusCode,
                    normalizedTarget,
                    texts.Count);
                return BatchFailure(texts.Count, "translation_provider_failed");
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != texts.Count)
                return BatchFailure(texts.Count, "translation_provider_failed");

            var results = new List<TranslationProviderResult>(texts.Count);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var detected = source;
                if (detected is null && item.TryGetProperty("detectedLanguage", out var detectedLanguage) &&
                    detectedLanguage.TryGetProperty("language", out var detectedValue))
                    detected = CommunicationLanguages.NormalizeOrNull(detectedValue.GetString());

                if (!item.TryGetProperty("translations", out var translations) ||
                    translations.ValueKind != JsonValueKind.Array ||
                    translations.GetArrayLength() == 0 ||
                    !translations[0].TryGetProperty("text", out var translatedText) ||
                    string.IsNullOrWhiteSpace(translatedText.GetString()))
                {
                    results.Add(new TranslationProviderResult(
                        false,
                        null,
                        detected,
                        ProviderIdentifier,
                        "translation_provider_failed"));
                    continue;
                }

                var restored = useHtml ? protectedTexts[results.Count].Restore(translatedText.GetString()) : translatedText.GetString();
                results.Add(new TranslationProviderResult(
                    restored is not null,
                    restored,
                    detected,
                    ProviderIdentifier,
                    restored is null ? "translation_output_invalid" : null));
            }
            return results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BatchFailure(texts.Count, "translation_provider_timeout");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "Azure Translator batch request failed. TargetLanguage={TargetLanguage} ItemCount={ItemCount}",
                normalizedTarget,
                texts.Count);
            return BatchFailure(texts.Count, "translation_provider_failed");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Azure Translator batch response was invalid. TargetLanguage={TargetLanguage} ItemCount={ItemCount}",
                normalizedTarget,
                texts.Count);
            return BatchFailure(texts.Count, "translation_provider_failed");
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

    private async Task<HttpResponseMessage> SendWithBoundedRetryAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                using var request = createRequest();
                var response = await _httpClientFactory.CreateClient("AzureTranslator")
                    .SendAsync(request, cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt == MaximumAttempts)
                    return response;

                var delay = RetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = exception;
                if (attempt == MaximumAttempts)
                    throw;
                await Task.Delay(RetryDelay(attempt), cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                lastFailure = exception;
                if (attempt == MaximumAttempts)
                    throw;
                await Task.Delay(RetryDelay(attempt), cancellationToken);
            }
        }

        throw lastFailure ?? new HttpRequestException("Azure Translator request failed.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        return retryAfter is { } requested && requested > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(Math.Min(requested.TotalMilliseconds, 2_000))
            : RetryDelay(attempt);
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(attempt == 1 ? 125 : 300);

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

    private static HttpRequestMessage CreateRequest(
        string endpoint,
        string path,
        string key,
        string? region,
        IReadOnlyList<string> texts)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint + path)
        {
            Content = JsonContent.Create(texts.Select(text => new AzureTextInput(text)).ToArray())
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Headers.Add("X-ClientTraceId", Guid.NewGuid().ToString("D"));
        if (!string.IsNullOrWhiteSpace(region))
            request.Headers.Add("Ocp-Apim-Subscription-Region", region);
        return request;
    }

    private static IReadOnlyList<TranslationProviderResult> BatchFailure(
        int count,
        string errorCode) => Enumerable.Range(0, count)
        .Select(_ => new TranslationProviderResult(
            false,
            null,
            null,
            ProviderIdentifier,
            errorCode))
        .ToArray();

    private sealed record AzureTextInput(string Text);
}

/// Azure's HTML notranslate contract protects executable template literals.
/// The adapter restores the original plain-text contract before validation/storage.
internal sealed record AzureProtectedText(string Text, IReadOnlyDictionary<string, string> Literals)
{
    private static readonly Regex Literal = new(@"\{[A-Za-z][A-Za-z0-9_]*\}|https://[^\s<>{}]+|</?[A-Za-z][^>]*>|\r?\n", RegexOptions.CultureInvariant);
    private static readonly Regex Wrapper = new(@"</?(?:span|div)(?:\s+[^>]*)?>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    internal static AzureProtectedText Create(string source)
    {
        var literals = new Dictionary<string, string>(StringComparer.Ordinal);
        var html = new StringBuilder("<div>");
        var offset = 0;
        foreach (Match match in Literal.Matches(source))
        {
            html.Append(WebUtility.HtmlEncode(source[offset..match.Index]));
            var index = literals.Count;
            string key;
            do { key = $"__legend_literal_{index++}__"; } while (source.Contains(key, StringComparison.Ordinal) || literals.ContainsKey(key));
            literals[key] = match.Value;
            html.Append("<span class=\"notranslate\">").Append(key).Append("</span>");
            offset = match.Index + match.Length;
        }
        html.Append(WebUtility.HtmlEncode(source[offset..])).Append("</div>");
        return new(html.ToString(), literals);
    }
    internal string? Restore(string? translated)
    {
        if (string.IsNullOrWhiteSpace(translated)) return null;
        var restored = WebUtility.HtmlDecode(Wrapper.Replace(translated, string.Empty));
        foreach (var (key, value) in Literals)
        {
            if (restored.Split(key, StringSplitOptions.None).Length != 2) return null;
            restored = restored.Replace(key, value, StringComparison.Ordinal);
        }
        return restored;
    }
}
