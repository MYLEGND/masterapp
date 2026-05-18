using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services.Analytics;

/// <summary>
/// Calls OpenAI Responses API with a fully redacted, aggregate-only analytics payload.
/// Never touches PII. Never exposes the API key to any client.
/// Configured independently of other OpenAI features via OpenAI:WebsiteAnalytics* keys.
/// </summary>
public sealed class OpenAiWebsiteAnalyticsReviewService
{
    private const string DefaultBaseUrl = "https://api.openai.com";
    private const int MaxPayloadChars = 12_000;

    private const string SystemPrompt =
        "You are a digital marketing performance analyst. Analyze ONLY the data provided — do not invent figures.\n\n" +

        "FOLLOW THIS EXACT ANALYSIS ORDER — do not skip any step:\n\n" +

        "STEP 1 — ACTIVE META ADS (analyze this FIRST, always):\n" +
        "  • This data is in the 'activeCampaigns' field. Evaluate every campaign listed.\n" +
        "  • Report: spend, impressions, clicks, CTR, CPC, and leads per campaign.\n" +
        "  • If leads = 0 for a campaign, you MUST state: " +
        "'Traffic is generating clicks but not converting — issue is post-click (landing page or funnel), not ad delivery.'\n" +
        "  • If activeCampaigns is empty, state: 'No active Meta Ads campaigns found for this period.'\n" +
        "  • The summary's FIRST sentence MUST reference ad performance " +
        "(e.g., spend level, click volume, whether ads are converting).\n\n" +

        "STEP 2 — META SIGNAL INTELLIGENCE: analyze high-intent visitors, lead-ready visitors, submit attempts without confirmed lead, signal-to-lead conversion, contact-step abandons, and the recommended optimization event.\n\n" +

        "STEP 3 — LANDING PAGE PERFORMANCE: conversion rate, exit rate, top pages.\n\n" +

        "STEP 4 — QUOTE FUNNEL: drop-off from starts → form starts → submits.\n\n" +

        "STEP 5 — BEHAVIOR: session duration, quick-exit rate, engaged session rate.\n\n" +

        "STEP 6 — LEADS / FOLLOW-UP: verified leads, form abandonment.\n\n" +

        "STRICT RULES:\n" +
        "  • NEVER skip ads analysis, even if the data shows zero spend or zero clicks.\n" +
        "  • NEVER give generic CRO advice before completing Step 1.\n" +
        "  • ALWAYS clearly separate: Ad problem vs Signal-quality problem vs Landing page problem vs Form problem.\n" +
        "  • If the metaSignal section shows many high-intent or lead-ready visitors but very few submitted leads, you MUST call out contact-step or form friction as a likely bottleneck.\n" +
        "  • If submitAttemptsWithoutLead is elevated, you MUST call out validation friction, technical submission failure, or contact-step trust issues as likely causes.\n" +
        "  • If submitted Lead volume is low but lead-ready or high-intent volume is healthy, recommend the best Meta optimization event from the payload instead of defaulting to Lead.\n" +
        "  • If campaign data shows clicks but zero website leads, output: " +
        "'Ad traffic is present but not converting — primary issue is landing page or funnel, not traffic generation.'\n" +
        "  • Be blunt. No padding. Return ONLY: " +
        "one summary sentence (must mention ads), up to 3 breakpoints, up to 3 actions, up to 2 tests, up to 3 confidence notes.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<OpenAiWebsiteAnalyticsReviewService> _logger;

    // Website-analytics-specific config — isolated from other OpenAI feature settings
    private readonly string _waModel;
    private readonly int _waTimeoutSeconds;
    private readonly bool _waStrictSchema;
    private readonly string _responsesEndpoint;

    public OpenAiWebsiteAnalyticsReviewService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<OpenAiWebsiteAnalyticsReviewService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;

        // Feature-specific model — falls back to global model, then hard default
        var waModel = config["OpenAI:WebsiteAnalyticsModel"];
        var globalModel = config["OpenAI:Model"];
        _waModel = !string.IsNullOrWhiteSpace(waModel) ? waModel
                 : !string.IsNullOrWhiteSpace(globalModel) ? globalModel
                 : "gpt-4.1";

        // Feature-specific timeout — falls back to 60 s default
        _waTimeoutSeconds = int.TryParse(config["OpenAI:WebsiteAnalyticsTimeoutSeconds"], out var wt) && wt > 0
            ? wt : 60;

        // Strict JSON schema enforcement — default off for speed
        _waStrictSchema = bool.TryParse(config["OpenAI:WebsiteAnalyticsStrictSchema"], out var ws) && ws;

        // Resolve base URL: IsNullOrWhiteSpace so an empty config value ("") falls through
        // to the default, preventing a schemeless URI that would resolve to file:///.
        var configuredBase = config["OpenAI:BaseUrl"];
        var resolvedBase = !string.IsNullOrWhiteSpace(configuredBase)
            ? configuredBase.TrimEnd('/')
            : DefaultBaseUrl.TrimEnd('/');

        // Validate scheme — reject anything that is not http or https.
        if (!Uri.TryCreate(resolvedBase, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            logger.LogError(
                "OpenAI:BaseUrl is not a valid http/https URI (scheme={Scheme}). " +
                "Falling back to default: {Default}.",
                baseUri?.Scheme ?? "(unparseable)", DefaultBaseUrl);
            resolvedBase = DefaultBaseUrl.TrimEnd('/');
        }

        _responsesEndpoint = $"{resolvedBase}/v1/responses";
    }

    public async Task<AiInsightsResultDto> ReviewAsync(
        AiSafeAnalyticsPayload payload,
        CancellationToken ct = default)
    {
        var userContent = BuildUserContent(payload);
        return await CallOpenAiAsync(SystemPrompt, userContent, ct);
    }

    public async Task<AiInsightsResultDto> FollowUpAsync(
        AiSafeAnalyticsPayload payload,
        string question,
        string? priorSummary,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildUserContent(payload));

        if (!string.IsNullOrWhiteSpace(priorSummary))
        {
            sb.AppendLine();
            sb.AppendLine("PRIOR ANALYSIS SUMMARY:");
            var truncated = priorSummary.Length > 1000 ? priorSummary[..1000] + "…" : priorSummary;
            sb.AppendLine(truncated);
        }

        sb.AppendLine();
        sb.AppendLine("FOLLOW-UP QUESTION:");
        sb.AppendLine(question);

        return await CallOpenAiAsync(SystemPrompt, sb.ToString(), ct);
    }

    // ── Private implementation ────────────────────────────────────────────────

    private async Task<AiInsightsResultDto> CallOpenAiAsync(
        string systemPrompt,
        string userContent,
        CancellationToken ct)
    {
        var apiKey = OpenAiKeyResolver.Resolve(_config);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OpenAI API key is not configured. Returning error result.");
            return ErrorResult("AI review is not configured. Set the OpenAI:ApiKey or OPENAI_API_KEY environment variable.");
        }

        // Guard on total payload size — keep input tokens low
        if (userContent.Length > MaxPayloadChars)
        {
            _logger.LogWarning(
                "AI analytics payload exceeded {Max} chars ({Actual}). Truncating.",
                MaxPayloadChars, userContent.Length);
            userContent = userContent[..MaxPayloadChars] + "\n[PAYLOAD TRUNCATED — SIZE LIMIT]";
        }

        var requestBody = BuildRequestBody(systemPrompt, userContent);
        var requestJson = JsonSerializer.Serialize(requestBody, JsonOptions);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_waTimeoutSeconds));

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var client = _httpClientFactory.CreateClient("OpenAI");

            using var request = new HttpRequestMessage(HttpMethod.Post, _responsesEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "AI review request starting. Model={Model} StrictSchema={Strict} " +
                "PayloadChars={Chars} TimeoutSeconds={Timeout} StartedAt={StartedAt}",
                _waModel, _waStrictSchema, userContent.Length, _waTimeoutSeconds, startedAt);

            using var response = await client.SendAsync(request, cts.Token);

            _logger.LogInformation(
                "AI review HTTP response. Status={Status} ElapsedMs={Elapsed}",
                (int)response.StatusCode,
                (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                var statusCode = (int)response.StatusCode;

                _logger.LogWarning(
                    "OpenAI API returned {Status}. Body (truncated): {Body}",
                    response.StatusCode,
                    errorBody.Length > 400 ? errorBody[..400] : errorBody);

                var openAiMessage = TryExtractOpenAiError(errorBody);

                var userMessage = statusCode switch
                {
                    401 => "OpenAI API key is invalid or unauthorized (HTTP 401). Check your OPENAI_API_KEY configuration.",
                    403 => "OpenAI API access is forbidden (HTTP 403). Your key may not have permission for this model.",
                    429 => "OpenAI rate limit exceeded (HTTP 429). Wait a moment and try again.",
                    >= 500 and <= 599 => $"OpenAI service error (HTTP {statusCode}). Try again in a few minutes.",
                    _ => string.IsNullOrWhiteSpace(openAiMessage)
                        ? $"OpenAI API error (HTTP {statusCode}). Please try again."
                        : $"OpenAI API error (HTTP {statusCode}): {openAiMessage}"
                };

                return ErrorResult(userMessage);
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var result = ParseOpenAiResponse(responseJson);

            var elapsedMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "AI review completed. Model={Model} InputTokens={In} OutputTokens={Out} ElapsedMs={Elapsed}",
                _waModel, result.inputTokens, result.outputTokens, elapsedMs);

            return result.dto;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var elapsedMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogWarning(
                "AI review timed out. Model={Model} TimeoutSeconds={Timeout} ElapsedMs={Elapsed}",
                _waModel, _waTimeoutSeconds, elapsedMs);
            return ErrorResult(
                $"OpenAI did not respond within {_waTimeoutSeconds} seconds. " +
                "This is usually a network connectivity issue (firewall, DNS, or no route to api.openai.com), " +
                "a request the model is struggling with, or OpenAI API overload. " +
                "Check server logs and verify the host can reach api.openai.com.");
        }
        catch (OperationCanceledException)
        {
            return ErrorResult("AI review was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            _logger.LogWarning(ex, "AI review HTTP request failed. Detail={Detail}", detail);
            return ErrorResult($"Could not reach OpenAI API: {detail}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse OpenAI top-level response JSON.");
            return ErrorResult("AI response could not be parsed. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during AI review.");
            return ErrorResult("An unexpected error occurred during AI review.");
        }
    }

    private static string BuildUserContent(AiSafeAnalyticsPayload payload)
    {
        // Compact JSON — no indentation keeps token count low
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        return $"ANALYTICS DATA:\n{payloadJson}\n\nReturn your structured review.";
    }

    private object BuildRequestBody(string systemPrompt, string userContent)
    {
        return new
        {
            model = _waModel,
            input = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "analytics_review",
                    strict = _waStrictSchema,
                    schema = BuildJsonSchema()
                }
            }
        };
    }

    private static object BuildJsonSchema()
    {
        return new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["summary"] = new { type = "string", description = "One concise sentence summarizing performance." },
                ["primaryBreakpoints"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["title"]       = new { type = "string" },
                            ["severity"]    = new { type = "string", @enum = new[] { "Low", "Medium", "High", "Critical" } },
                            ["evidence"]    = new { type = "array", items = new { type = "string" } },
                            ["likelyCause"] = new { type = "string" },
                            ["owner"]       = new { type = "string", @enum = new[] { "Ad", "LandingPage", "Form", "Tracking", "FollowUp", "Unknown" } }
                        },
                        required = new[] { "title", "severity", "evidence", "likelyCause", "owner" },
                        additionalProperties = false
                    }
                },
                ["recommendedActions"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["priority"]       = new { type = "integer" },
                            ["action"]         = new { type = "string" },
                            ["why"]            = new { type = "string" },
                            ["expectedImpact"] = new { type = "string" }
                        },
                        required = new[] { "priority", "action", "why", "expectedImpact" },
                        additionalProperties = false
                    }
                },
                ["testsToRun"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["name"]       = new { type = "string" },
                            ["hypothesis"] = new { type = "string" },
                            ["metric"]     = new { type = "string" }
                        },
                        required = new[] { "name", "hypothesis", "metric" },
                        additionalProperties = false
                    }
                },
                ["confidenceNotes"] = new
                {
                    type = "array",
                    items = new { type = "string" }
                }
            },
            required = new[] { "summary", "primaryBreakpoints", "recommendedActions", "testsToRun", "confidenceNotes" },
            additionalProperties = false
        };
    }

    private (AiInsightsResultDto dto, int inputTokens, int outputTokens) ParseOpenAiResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
        }

        // Navigate output[0].content[0].text
        if (!root.TryGetProperty("output", out var output) || output.GetArrayLength() == 0)
            return (ErrorResult("OpenAI returned empty output."), inputTokens, outputTokens);

        var firstOutput = output[0];
        if (!firstOutput.TryGetProperty("content", out var content) || content.GetArrayLength() == 0)
            return (ErrorResult("OpenAI output had no content."), inputTokens, outputTokens);

        var firstContent = content[0];
        if (!firstContent.TryGetProperty("text", out var textProp))
            return (ErrorResult("OpenAI content had no text field."), inputTokens, outputTokens);

        var text = textProp.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return (ErrorResult("OpenAI returned empty text."), inputTokens, outputTokens);

        var resultOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        AiInsightsResultDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AiInsightsResultDto>(text, resultOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize AI response DTO. Raw text (truncated): {Text}",
                text.Length > 500 ? text[..500] : text);
            return (ErrorResult("AI response could not be parsed. Please try again."), inputTokens, outputTokens);
        }

        dto ??= ErrorResult("Failed to deserialize AI response.");

        return (dto, inputTokens, outputTokens);
    }

    private static string? TryExtractOpenAiError(string errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody)) return null;
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
            {
                var text = msg.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Length > 200 ? text[..200] + "…" : text;
            }
        }
        catch { /* Not valid JSON — ignore */ }
        return null;
    }

    private static AiInsightsResultDto ErrorResult(string message) => new()
    {
        IsError = true,
        ErrorMessage = message,
        Summary = message,
        PrimaryBreakpoints = new List<BreakpointDto>(),
        RecommendedActions = new List<RecommendedActionDto>(),
        TestsToRun = new List<TestToRunDto>(),
        ConfidenceNotes = new List<string>()
    };
}
