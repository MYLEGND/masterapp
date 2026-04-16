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
/// </summary>
public sealed class OpenAiWebsiteAnalyticsReviewService
{
    private const string DefaultBaseUrl = "https://api.openai.com";
    private const int MaxPayloadChars = 50_000;
    private const string SystemPrompt =
        "You are a digital marketing performance analyst. You only have access to the website " +
        "analytics data provided. Do not invent data you were not given. Do not infer or reveal " +
        "any individual identity. Analyze only aggregate traffic, conversion, and funnel metrics. " +
        "Identify bottlenecks across: ad quality, landing page performance, quote form friction, " +
        "tracking gaps, and follow-up process. Be blunt and operational.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<OpenAiWebsiteAnalyticsReviewService> _logger;
    private readonly string _model;
    private readonly int _timeoutSeconds;
    private readonly string _responsesEndpoint;

    public OpenAiWebsiteAnalyticsReviewService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<OpenAiWebsiteAnalyticsReviewService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _model = config["OpenAI:Model"] ?? "gpt-4.1";
        _timeoutSeconds = int.TryParse(config["OpenAI:TimeoutSeconds"], out var t) && t > 0 ? t : 30;

        var baseUrl = (config["OpenAI:BaseUrl"] ?? DefaultBaseUrl).TrimEnd('/');
        _responsesEndpoint = $"{baseUrl}/v1/responses";
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
            // Truncate prior summary to prevent ballooning payload
            var truncated = priorSummary.Length > 2000 ? priorSummary[..2000] + "…" : priorSummary;
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

        // Guard on total payload size
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
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            var client = _httpClientFactory.CreateClient("OpenAI");

            using var request = new HttpRequestMessage(HttpMethod.Post, _responsesEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "AI review request starting. Model={Model} PayloadChars={Chars}",
                _model, userContent.Length);

            using var response = await client.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "OpenAI API returned {Status}. Body (truncated): {Body}",
                    response.StatusCode,
                    errorBody.Length > 400 ? errorBody[..400] : errorBody);
                return ErrorResult($"AI service returned HTTP {(int)response.StatusCode}. Please try again.");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);

            var result = ParseOpenAiResponse(responseJson);

            _logger.LogInformation(
                "AI review completed. InputTokens={In} OutputTokens={Out}",
                result.inputTokens, result.outputTokens);

            return result.dto;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("AI review request timed out after {Timeout}s.", _timeoutSeconds);
            return ErrorResult("AI review timed out. Try a shorter date range or try again.");
        }
        catch (OperationCanceledException)
        {
            return ErrorResult("AI review was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "AI review HTTP request failed.");
            return ErrorResult("AI service is temporarily unavailable. Please try again.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse OpenAI response.");
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
        // Serialize the safe payload as structured JSON for the prompt
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        return $"WEBSITE ANALYTICS DATA:\n{payloadJson}\n\nAnalyze the above and return your structured review.";
    }

    private object BuildRequestBody(string systemPrompt, string userContent)
    {
        return new
        {
            model = _model,
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
                    strict = true,
                    schema = BuildJsonSchema()
                }
            }
        };
    }

    private static object BuildJsonSchema()
    {
        // JSON schema matching AiInsightsResultDto
        return new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["summary"] = new { type = "string", description = "Executive summary of performance." },
                ["primaryBreakpoints"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["title"] = new { type = "string" },
                            ["severity"] = new { type = "string", @enum = new[] { "Low", "Medium", "High", "Critical" } },
                            ["evidence"] = new { type = "array", items = new { type = "string" } },
                            ["likelyCause"] = new { type = "string" },
                            ["owner"] = new { type = "string", @enum = new[] { "Ad", "LandingPage", "Form", "Tracking", "FollowUp", "Unknown" } }
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
                            ["priority"] = new { type = "integer" },
                            ["action"] = new { type = "string" },
                            ["why"] = new { type = "string" },
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
                            ["name"] = new { type = "string" },
                            ["hypothesis"] = new { type = "string" },
                            ["metric"] = new { type = "string" }
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

        // Extract usage if available
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

        // Deserialize the structured JSON from the model
        var resultOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var dto = JsonSerializer.Deserialize<AiInsightsResultDto>(text, resultOptions)
            ?? ErrorResult("Failed to deserialize AI response.");

        return (dto, inputTokens, outputTokens);
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
