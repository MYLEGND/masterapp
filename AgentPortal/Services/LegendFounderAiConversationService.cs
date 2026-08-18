using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPortal.Services.Analytics;
using Domain.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services;

/// <summary>
/// Founder-only conversational orchestration over existing LEGEND authorities.
///
/// This is intentionally NOT a language-learning authority, corpus writer,
/// translation router, model lifecycle authority, or durable chat store.
///
/// OpenAI supplies conversational reasoning. All current LEGEND facts are read
/// through the already-governed FounderLegendConnectService.
/// </summary>
public sealed class LegendFounderAiConversationService
{
    private const int MaximumConversationMessages = 30;
    private const int MaximumMessageCharacters = 20_000;
    private const int MaximumConversationCharacters = 120_000;
    private const int MaximumProviderConversationCharacters = 60_000;
    private const int MaximumToolRounds = 6;
    private const int MaximumToolOutputCharacters = 20_000;
    private const int MaximumRetainedContextCharacters = 16_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly FounderLegendConnectService _legend;
    private readonly ILogger<LegendFounderAiConversationService> _logger;
    private readonly int _timeoutSeconds;
    private readonly int _maxOutputTokens;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    public LegendFounderAiConversationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        FounderLegendConnectService legend,
        ILogger<LegendFounderAiConversationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _legend = legend;
        _logger = logger;

        _timeoutSeconds =
            Math.Clamp(
                configuration.GetValue<int?>(
                    "OpenAI:LegendFounderAiTimeoutSeconds") ??
                    120,
                30,
                180);

        _maxOutputTokens =
            Math.Clamp(
                configuration.GetValue<int?>(
                    "OpenAI:LegendFounderAiMaxOutputTokens") ??
                    5_000,
                1_500,
                8_000);
    }

    public async Task<LegendFounderAiChatResponse> ReplyAsync(
        ClaimsPrincipal founder,
        LegendFounderAiChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(founder);
        ArgumentNullException.ThrowIfNull(request);

        var mode = NormalizeMode(request.Mode);

        if (!TryNormalizeMessages(
                request.Messages,
                out var conversation,
                out var validationError))
        {
            return LegendFounderAiChatResponse.Failure(
                validationError,
                "validation");
        }

        var apiKey = OpenAiKeyResolver.Resolve(_configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return LegendFounderAiChatResponse.Failure(
                "Legend® Ai is not configured because the OpenAI API key is unavailable.",
                "configuration");
        }

        var model =
            _configuration["OpenAI:LegendFounderAiModel"]?.Trim();

        if (string.IsNullOrWhiteSpace(model))
            model = _configuration["OpenAI:Model"]?.Trim();

        if (string.IsNullOrWhiteSpace(model))
            model = "gpt-5";

        using var requestBudget =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        requestBudget.CancelAfter(
            TimeSpan.FromSeconds(
                _timeoutSeconds));

        var effectiveToken =
            requestBudget.Token;

        var retainedKnowledge =
            await TryLoadRetainedKnowledgeAsync(
                founder,
                conversation[^1].Content ??
                    string.Empty,
                effectiveToken);

        var instructions =
            BuildInstructions(mode) +
            BuildRetainedKnowledgeContext(
                retainedKnowledge);

        var tools = BuildFounderTools();

        var providerConversation =
            CompactProviderConversation(
                conversation);

        var input =
            new List<object>(
                providerConversation.Count + 12);

        foreach (var message in providerConversation)
        {
            input.Add(new Dictionary<string, object?>
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            });
        }

        try
        {
            for (var round = 0; round < MaximumToolRounds; round++)
            {
                using var responseDocument =
                    await SendResponseAsync(
                        apiKey,
                        model,
                        instructions,
                        input,
                        tools,
                        effectiveToken);

                if (responseDocument is null)
                {
                    return LegendFounderAiChatResponse.Failure(
                        "Legend® Ai could not reach its conversational reasoning provider.");
                }

                var root = responseDocument.RootElement;

                var responseState =
                    ReadResponseState(root);

                if (responseState == "incomplete")
                {
                    var partial =
                        ExtractOutputText(root);

                    if (!string.IsNullOrWhiteSpace(
                            partial))
                    {
                        return new LegendFounderAiChatResponse(
                            true,
                            mode,
                            partial.Trim() +
                            "\n\n[This response reached its bounded output limit. Ask Legend® Ai to continue if you want the remainder.]",
                            null);
                    }

                    return LegendFounderAiChatResponse.Failure(
                        "Legend® Ai reached its bounded output limit before producing usable text.");
                }

                if (responseState != "completed")
                {
                    return LegendFounderAiChatResponse.Failure(
                        "Legend® Ai received an unusable reasoning response.");
                }

                var toolCalls = ReadFunctionCalls(root);

                if (toolCalls.Count == 0)
                {
                    var answer = ExtractOutputText(root);

                    if (string.IsNullOrWhiteSpace(answer))
                    {
                        return LegendFounderAiChatResponse.Failure(
                            "Legend® Ai completed without a usable response.");
                    }

                    return new LegendFounderAiChatResponse(
                        true,
                        mode,
                        answer.Trim(),
                        null);
                }

                // Responses API tool continuation with store=false:
                // preserve the returned output items in local request context,
                // then append bounded function_call_output items.
                if (root.TryGetProperty("output", out var output) &&
                    output.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in output.EnumerateArray())
                        input.Add(item.Clone());
                }

                foreach (var call in toolCalls)
                {
                    var toolOutput =
                        await ExecuteFounderToolAsync(
                            founder,
                            call,
                            effectiveToken);

                    input.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = call.CallId,
                        ["output"] = toolOutput
                    });
                }
            }

            return LegendFounderAiChatResponse.Failure(
                "Legend® Ai reached its bounded inspection limit. Ask a narrower follow-up question.");
        }
        catch (LegendFounderAiProviderException exception)
        {
            var reference =
                !string.IsNullOrWhiteSpace(
                    exception.ProviderRequestId)
                    ? exception.ProviderRequestId
                    : exception.ClientRequestId;

            return LegendFounderAiChatResponse.Failure(
                $"Legend® Ai's reasoning provider rejected the request " +
                $"(HTTP {exception.StatusCode}). Reference: {reference}",
                "provider_http",
                exception.StatusCode,
                reference);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return LegendFounderAiChatResponse.Failure(
                "Legend® Ai timed out while reasoning over the current system state.",
                "timeout");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI provider transport failed.");

            return LegendFounderAiChatResponse.Failure(
                "Legend® Ai could not reach its conversational reasoning provider.",
                "transport");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI received invalid provider JSON.");

            return LegendFounderAiChatResponse.Failure(
                "Legend® Ai received an invalid reasoning response.",
                "provider_json");
        }
    }

    private async Task<JsonDocument?> SendResponseAsync(
        string apiKey,
        string model,
        string instructions,
        IReadOnlyList<object> input,
        IReadOnlyList<object> tools,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model,
            store = false,
            instructions,
            input,
            tools,
            tool_choice = "auto",
            parallel_tool_calls = false,
            max_output_tokens = _maxOutputTokens
        };

        var client =
            _httpClientFactory.CreateClient(
                "OpenAI");

        // Feature-local timeout is governed by the linked request budget.
        // Do not mutate the shared OpenAI registration used by other features.
        client.Timeout =
            Timeout.InfiniteTimeSpan;

        for (var attempt = 1;
             attempt <= 2;
             attempt++)
        {
            var clientRequestId =
                Guid.NewGuid().ToString("D");

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    "v1/responses")
                {
                    Content =
                        JsonContent.Create(payload)
                };

            request.Headers.TryAddWithoutValidation(
                "X-Client-Request-Id",
                clientRequestId);

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    apiKey);

            using var response =
                await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                await using var stream =
                    await response.Content
                        .ReadAsStreamAsync(
                            cancellationToken);

                return await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken:
                        cancellationToken);
            }

            var transient =
                IsTransientOpenAiStatus(
                    response.StatusCode);

            if (transient &&
                attempt < 2)
            {
                var retryAfter =
                    response.Headers
                        .RetryAfter?
                        .Delta;

                var delay =
                    retryAfter is null
                        ? TimeSpan.FromSeconds(1)
                        : TimeSpan.FromSeconds(
                            Math.Clamp(
                                retryAfter.Value.TotalSeconds,
                                1,
                                5));

                await Task.Delay(
                    delay,
                    cancellationToken);

                continue;
            }

            var errorBody =
                await response.Content
                    .ReadAsStringAsync(
                        cancellationToken);

            if (errorBody.Length > 1_000)
            {
                errorBody =
                    errorBody[..1_000];
            }

            var providerRequestId =
                response.Headers.TryGetValues(
                    "x-request-id",
                    out var providerRequestIds)
                    ? providerRequestIds.FirstOrDefault()
                    : null;

            _logger.LogError(
                "LEGEND Founder AI provider rejected request. " +
                "HTTP={StatusCode} ClientRequestId={ClientRequestId} " +
                "ProviderRequestId={ProviderRequestId} Body={Body}",
                (int)response.StatusCode,
                clientRequestId,
                providerRequestId ?? "unavailable",
                errorBody);

            throw new LegendFounderAiProviderException(
                (int)response.StatusCode,
                clientRequestId,
                providerRequestId);
        }

        return null;
    }

    private static bool IsTransientOpenAiStatus(
        HttpStatusCode status) =>
        status is
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private async Task<string> ExecuteFounderToolAsync(
        ClaimsPrincipal founder,
        FounderAiToolCall call,
        CancellationToken cancellationToken)
    {
        switch (call.Name)
        {
            case "legend_system_overview":
            {
                var snapshot =
                    await _legend.GetLiveMetricsAsync(
                        founder,
                        cancellationToken);

                return SerializeBounded(snapshot);
            }

            case "legend_provider_capacity":
            {
                var snapshot =
                    await _legend.GetProviderCapacityAsync(
                        founder,
                        cancellationToken);

                return SerializeBounded(snapshot);
            }

            case "legend_language_knowledge":
            {
                using var arguments =
                    JsonDocument.Parse(call.Arguments);

                var language =
                    ReadRequiredString(
                        arguments.RootElement,
                        "language");

                if (string.IsNullOrWhiteSpace(language))
                    return """{"error":"language_required"}""";

                var snapshot =
                    await _legend.GetLanguageKnowledgeAsync(
                        founder,
                        language,
                        cancellationToken);

                return SerializeBounded(snapshot);
            }

            case "legend_pair_health":
            {
                using var arguments =
                    JsonDocument.Parse(call.Arguments);

                var pair =
                    ReadRequiredString(
                        arguments.RootElement,
                        "pair");

                if (string.IsNullOrWhiteSpace(pair))
                    return """{"error":"pair_required"}""";

                var snapshot =
                    await _legend.GetPairHealthAsync(
                        founder,
                        pair,
                        cancellationToken);

                return SerializeBounded(snapshot);
            }

            case "legend_translation_quality":
            {
                var snapshot =
                    await _legend.GetTranslationQualityAsync(
                        founder,
                        cancellationToken);

                return SerializeBounded(snapshot);
            }

            case "legend_target_realizations":
            {
                var snapshot =
                    await _legend.GetTargetRealizationReviewAsync(
                        founder,
                        cancellationToken);

                return SerializeBounded(snapshot);
            }

            case "legend_search_retained_knowledge":
            {
                using var arguments =
                    JsonDocument.Parse(
                        call.Arguments);

                var query =
                    ReadRequiredString(
                        arguments.RootElement,
                        "query");

                var sourceLanguage =
                    ReadOptionalString(
                        arguments.RootElement,
                        "source_language");

                var targetLanguage =
                    ReadOptionalString(
                        arguments.RootElement,
                        "target_language");

                if (string.IsNullOrWhiteSpace(
                        query))
                {
                    return """{"error":"query_required"}""";
                }

                var snapshot =
                    await _legend
                        .SearchRetainedKnowledgeAsync(
                            founder,
                            query,
                            sourceLanguage,
                            targetLanguage,
                            16,
                            cancellationToken);

                return SerializeBounded(
                    snapshot);
            }

            case "legend_submit_machine_learning_candidate":
            {
                using var arguments =
                    JsonDocument.Parse(
                        call.Arguments);

                var sourceLanguage =
                    ReadRequiredString(
                        arguments.RootElement,
                        "source_language");

                var targetLanguage =
                    ReadRequiredString(
                        arguments.RootElement,
                        "target_language");

                var familyKey =
                    ReadRequiredString(
                        arguments.RootElement,
                        "family_key");

                var semanticCategory =
                    ReadRequiredString(
                        arguments.RootElement,
                        "semantic_category");

                var rationale =
                    ReadRequiredString(
                        arguments.RootElement,
                        "rationale");

                if (string.IsNullOrWhiteSpace(
                        sourceLanguage) ||
                    string.IsNullOrWhiteSpace(
                        targetLanguage) ||
                    string.IsNullOrWhiteSpace(
                        familyKey) ||
                    string.IsNullOrWhiteSpace(
                        semanticCategory) ||
                    string.IsNullOrWhiteSpace(
                        rationale) ||
                    !arguments.RootElement
                        .TryGetProperty(
                            "confidence",
                            out var confidenceElement) ||
                    !confidenceElement
                        .TryGetDecimal(
                            out var confidence) ||
                    !arguments.RootElement
                        .TryGetProperty(
                            "examples",
                            out var examplesElement) ||
                    examplesElement.ValueKind !=
                        JsonValueKind.Array)
                {
                    return """{"error":"invalid_machine_learning_candidate"}""";
                }

                var examples =
                    new List<
                        LegendConnectMachineTeachingExampleSubmission>();

                foreach (var example in
                         examplesElement
                             .EnumerateArray())
                {
                    var sourceText =
                        ReadRequiredString(
                            example,
                            "source_text");

                    var targetText =
                        ReadOptionalString(
                            example,
                            "target_text");

                    if (string.IsNullOrWhiteSpace(
                            sourceText) ||
                        !example.TryGetProperty(
                            "components",
                            out var componentsElement) ||
                        componentsElement.ValueKind !=
                            JsonValueKind.Array)
                    {
                        return """{"error":"invalid_machine_learning_example"}""";
                    }

                    var components =
                        new List<
                            LegendConnectMachineTeachingComponentSubmission>();

                    foreach (var component in
                             componentsElement
                                 .EnumerateArray())
                    {
                        var dimension =
                            ReadRequiredString(
                                component,
                                "dimension");

                        var value =
                            ReadRequiredString(
                                component,
                                "value");

                        var surface =
                            ReadRequiredString(
                                component,
                                "surface_form");

                        if (string.IsNullOrWhiteSpace(
                                dimension) ||
                            string.IsNullOrWhiteSpace(
                                value) ||
                            string.IsNullOrWhiteSpace(
                                surface))
                        {
                            return """{"error":"invalid_machine_learning_component"}""";
                        }

                        components.Add(
                            new LegendConnectMachineTeachingComponentSubmission(
                                dimension,
                                value,
                                surface));
                    }

                    examples.Add(
                        new LegendConnectMachineTeachingExampleSubmission(
                            sourceText,
                            targetText,
                            components));
                }

                var result =
                    await _legend
                        .QueueMachineTeachingProposalAsync(
                            founder,
                            new LegendConnectMachineTeachingSubmission(
                                sourceLanguage,
                                targetLanguage,
                                familyKey,
                                semanticCategory,
                                rationale,
                                confidence,
                                examples),
                            cancellationToken);

                return SerializeBounded(
                    result);
            }

            case "legend_submit_founder_seed":
            {
                using var arguments =
                    JsonDocument.Parse(call.Arguments);

                var sourceLanguage =
                    ReadRequiredString(
                        arguments.RootElement,
                        "source_language");

                var sourceText =
                    ReadRequiredString(
                        arguments.RootElement,
                        "source_text");

                var contextCategory =
                    ReadOptionalString(
                        arguments.RootElement,
                        "context_category");

                var usageRegister =
                    ReadOptionalString(
                        arguments.RootElement,
                        "usage_register");

                var regionalVariant =
                    ReadOptionalString(
                        arguments.RootElement,
                        "regional_variant");

                if (string.IsNullOrWhiteSpace(sourceLanguage) ||
                    string.IsNullOrWhiteSpace(sourceText))
                {
                    return """{"error":"source_language_and_text_required"}""";
                }

                var result =
                    await _legend.QueueFounderLearningSeedAsync(
                        founder,
                        sourceLanguage,
                        sourceText,
                        contextCategory,
                        usageRegister,
                        regionalVariant,
                        cancellationToken);

                return SerializeBounded(result);
            }

            case "legend_submit_founder_curriculum":
            {
                using var arguments =
                    JsonDocument.Parse(call.Arguments);

                var familiesElement =
                    arguments.RootElement.GetProperty(
                        "families");

                var families =
                    new List<LegendConnectCurriculumBatchSubmission>();

                foreach (var family in
                         familiesElement.EnumerateArray())
                {
                    var familyKey =
                        ReadRequiredString(
                            family,
                            "family_key");

                    var semanticCategory =
                        ReadOptionalString(
                            family,
                            "semantic_category");

                    if (string.IsNullOrWhiteSpace(familyKey) ||
                        !family.TryGetProperty(
                            "examples",
                            out var examplesElement) ||
                        examplesElement.ValueKind !=
                            JsonValueKind.Array)
                    {
                        return """{"error":"invalid_curriculum_family"}""";
                    }

                    var examples =
                        new List<
                            LegendConnectCurriculumExampleSubmission>();

                    foreach (var example in
                             examplesElement.EnumerateArray())
                    {
                        var exampleText =
                            ReadRequiredString(
                                example,
                                "text");

                        if (string.IsNullOrWhiteSpace(exampleText) ||
                            !example.TryGetProperty(
                                "variations",
                                out var variationsElement) ||
                            variationsElement.ValueKind !=
                                JsonValueKind.Array)
                        {
                            return """{"error":"invalid_curriculum_example"}""";
                        }

                        var variations =
                            new Dictionary<string, string>(
                                StringComparer.OrdinalIgnoreCase);

                        foreach (var variation in
                                 variationsElement.EnumerateArray())
                        {
                            if (variation.ValueKind !=
                                JsonValueKind.Object)
                            {
                                return """{"error":"invalid_curriculum_variation"}""";
                            }

                            var dimension =
                                ReadRequiredString(
                                    variation,
                                    "dimension");

                            var value =
                                ReadRequiredString(
                                    variation,
                                    "value");

                            if (string.IsNullOrWhiteSpace(dimension) ||
                                string.IsNullOrWhiteSpace(value) ||
                                !variations.TryAdd(
                                    dimension,
                                    value))
                            {
                                return """{"error":"invalid_curriculum_variation"}""";
                            }
                        }

                        if (variations.Count == 0)
                            return """{"error":"curriculum_variations_required"}""";

                        examples.Add(
                            new LegendConnectCurriculumExampleSubmission(
                                exampleText,
                                variations));
                    }

                    if (examples.Count < 2)
                        return """{"error":"curriculum_family_requires_contrasts"}""";

                    families.Add(
                        new LegendConnectCurriculumBatchSubmission(
                            familyKey,
                            semanticCategory,
                            examples));
                }

                if (families.Count == 0)
                    return """{"error":"curriculum_families_required"}""";

                var result =
                    await _legend.QueueFounderCurriculumAsync(
                        founder,
                        new LegendConnectCurriculumManifestSubmission(
                            families),
                        cancellationToken);

                return SerializeBounded(result);
            }

            case "legend_activate_autonomous_learning":
            {
                var result =
                    await _legend.EnsureAutonomousLearningActiveAsync(
                        founder,
                        cancellationToken);

                return SerializeBounded(result);
            }

            case "legend_metric_detail":
            {
                using var arguments =
                    JsonDocument.Parse(call.Arguments);

                var metric =
                    ReadRequiredString(
                        arguments.RootElement,
                        "metric_key");

                if (string.IsNullOrWhiteSpace(metric))
                    return """{"error":"metric_key_required"}""";

                var snapshot =
                    await _legend.GetMetricDetailAsync(
                        founder,
                        metric,
                        cancellationToken);

                return SerializeBounded(snapshot);
            }

            case "legend_language_state":
            {
                using var arguments =
                    JsonDocument.Parse(call.Arguments);

                var language =
                    ReadRequiredString(
                        arguments.RootElement,
                        "language");

                var pair =
                    ReadOptionalString(
                        arguments.RootElement,
                        "pair");

                if (string.IsNullOrWhiteSpace(language))
                    return """{"error":"language_required"}""";

                var dashboard =
                    await _legend.GetDashboardAsync(
                        founder,
                        language,
                        pair,
                        cancellationToken);

                // Explicit privacy boundary:
                // do NOT send Founder account/member usage directory data
                // into the conversational provider.
                var safeProjection = new
                {
                    dashboard = dashboard.Dashboard,
                    selectedLanguage = dashboard.SelectedLanguage,
                    selectedLanguageKnowledge =
                        dashboard.SelectedLanguageKnowledge,
                    selectedPair = dashboard.SelectedPair,
                    translationQuality =
                        dashboard.TranslationQuality,
                    runtimePolicy = dashboard.RuntimePolicy,
                    productionReadiness =
                        dashboard.ProductionReadiness
                };

                return SerializeBounded(safeProjection);
            }

            default:
                return """{"error":"unknown_founder_tool"}""";
        }
    }

    private static IReadOnlyList<object> BuildFounderTools()
    {
        return
        [
            new
            {
                type = "function",
                name = "legend_system_overview",
                description =
                    "Read the current privacy-safe aggregate LEGEND Connect system metrics, readiness and operating state. Use this before making factual claims about current LEGEND system status.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>(),
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_language_state",
                description =
                    "Inspect current governed LEGEND knowledge and health for one language and optionally one directional pair. This is read-only.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        language = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 40
                        },
                        pair = new
                        {
                            type = new[] { "string", "null" },
                            maxLength = 100
                        }
                    },
                    required = new[] { "language", "pair" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_metric_detail",
                description =
                    "Read the existing privacy-safe LEGEND record-level evidence behind one dashboard metric. This is read-only.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        metric_key = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 120
                        }
                    },
                    required = new[] { "metric_key" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_provider_capacity",
                description =
                    "Read current Azure Translator/provider capacity and consumption state through the existing LEGEND Founder authority.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>(),
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_language_knowledge",
                description =
                    "Read the existing bounded canonical knowledge, alignments, contexts, learning activity, structural patterns and directional pair health for one LEGEND language.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        language = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 40
                        }
                    },
                    required = new[] { "language" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_pair_health",
                description =
                    "Read current governed health, coverage, demand, internal reuse, provider dependency and recent learning state for one exact directional LEGEND language pair.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        pair = new
                        {
                            type = "string",
                            minLength = 5,
                            maxLength = 100
                        }
                    },
                    required = new[] { "pair" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_translation_quality",
                description =
                    "Read current retained provider observations, supported observations, contradictions, review-needed evidence and HumanVerified totals from the existing LEGEND quality authority.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>(),
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_target_realizations",
                description =
                    "Read current retained target-realization hypotheses, support, contradictions, verification state and evidence from the existing LEGEND curriculum authority.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>(),
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_search_retained_knowledge",
                description =
                    "Search LEGEND's existing retained language evidence before relying on general OpenAI recall. Results preserve provenance, authority, contradiction and proposal state.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 2_000
                        },
                        source_language = new
                        {
                            type = new[] { "string", "null" },
                            maxLength = 40
                        },
                        target_language = new
                        {
                            type = new[] { "string", "null" },
                            maxLength = 40
                        }
                    },
                    required = new[]
                    {
                        "query",
                        "source_language",
                        "target_language"
                    },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_submit_machine_learning_candidate",
                description =
                    "Retain reusable machine-derived LANGUAGE teaching from the current conversation in LEGEND's existing MachineProposed lifecycle. This tool does NOT approve, validate, train or promote the material. The existing independent critic and canonical validator remain authoritative. Use only for reusable linguistic knowledge with controlled contrasts; never use it for personal facts, private messages, transient platform facts or unsupported speculation.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        source_language = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 32
                        },
                        target_language = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 32
                        },
                        family_key = new
                        {
                            type = "string",
                            minLength = 3,
                            maxLength = 120
                        },
                        semantic_category = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 120
                        },
                        rationale = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 1_000
                        },
                        confidence = new
                        {
                            type = "number",
                            minimum = 0,
                            maximum = 1
                        },
                        examples = new
                        {
                            type = "array",
                            minItems = 2,
                            maxItems = 8,
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    source_text = new
                                    {
                                        type = "string",
                                        minLength = 1,
                                        maxLength = 2_000
                                    },
                                    target_text = new
                                    {
                                        type = new[]
                                        {
                                            "string",
                                            "null"
                                        },
                                        maxLength = 2_000
                                    },
                                    components = new
                                    {
                                        type = "array",
                                        minItems = 1,
                                        maxItems = 16,
                                        items = new
                                        {
                                            type = "object",
                                            properties = new
                                            {
                                                dimension = new
                                                {
                                                    type = "string",
                                                    minLength = 1,
                                                    maxLength = 80
                                                },
                                                value = new
                                                {
                                                    type = "string",
                                                    minLength = 1,
                                                    maxLength = 240
                                                },
                                                surface_form = new
                                                {
                                                    type = "string",
                                                    minLength = 1,
                                                    maxLength = 500
                                                }
                                            },
                                            required = new[]
                                            {
                                                "dimension",
                                                "value",
                                                "surface_form"
                                            },
                                            additionalProperties = false
                                        }
                                    }
                                },
                                required = new[]
                                {
                                    "source_text",
                                    "target_text",
                                    "components"
                                },
                                additionalProperties = false
                            }
                        }
                    },
                    required = new[]
                    {
                        "source_language",
                        "target_language",
                        "family_key",
                        "semantic_category",
                        "rationale",
                        "confidence",
                        "examples"
                    },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_submit_founder_seed",
                description =
                    "Submit an explicit Founder-approved source-language knowledge seed through LEGEND's existing Founder ingestion authority. This is not OpenAI self-approval. Use only when the Founder explicitly instructs Legend® Ai to teach, add, submit, retain, or train this exact source knowledge.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        source_language = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 40
                        },
                        source_text = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 6000
                        },
                        context_category = new
                        {
                            type = new[] { "string", "null" },
                            maxLength = 120
                        },
                        usage_register = new
                        {
                            type = new[] { "string", "null" },
                            maxLength = 80
                        },
                        regional_variant = new
                        {
                            type = new[] { "string", "null" },
                            maxLength = 80
                        }
                    },
                    required = new[]
                    {
                        "source_language",
                        "source_text",
                        "context_category",
                        "usage_register",
                        "regional_variant"
                    },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_submit_founder_curriculum",
                description =
                    "Submit explicit Founder-approved controlled curriculum into LEGEND's existing canonical curriculum authority. Use only when the Founder explicitly asks Legend® Ai to teach/train/add this curriculum. The existing autonomous expansion, teacher, critic, validator and model lifecycle continue afterward.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        families = new
                        {
                            type = "array",
                            minItems = 1,
                            maxItems = 20,
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    family_key = new
                                    {
                                        type = "string",
                                        minLength = 3,
                                        maxLength = 120
                                    },
                                    semantic_category = new
                                    {
                                        type = new[]
                                        {
                                            "string",
                                            "null"
                                        },
                                        maxLength = 120
                                    },
                                    examples = new
                                    {
                                        type = "array",
                                        minItems = 2,
                                        maxItems = 100,
                                        items = new
                                        {
                                            type = "object",
                                            properties = new
                                            {
                                                text = new
                                                {
                                                    type = "string",
                                                    minLength = 1,
                                                    maxLength = 2000
                                                },
                                                variations = new
                                                {
                                                    type = "array",
                                                    minItems = 1,
                                                    maxItems = 32,
                                                    items = new
                                                    {
                                                        type = "object",
                                                        properties = new
                                                        {
                                                            dimension = new
                                                            {
                                                                type = "string",
                                                                minLength = 1,
                                                                maxLength = 80
                                                            },
                                                            value = new
                                                            {
                                                                type = "string",
                                                                minLength = 1,
                                                                maxLength = 240
                                                            }
                                                        },
                                                        required = new[]
                                                        {
                                                            "dimension",
                                                            "value"
                                                        },
                                                        additionalProperties = false
                                                    }
                                                }
                                            },
                                            required = new[]
                                            {
                                                "text",
                                                "variations"
                                            },
                                            additionalProperties = false
                                        }
                                    }
                                },
                                required = new[]
                                {
                                    "family_key",
                                    "semantic_category",
                                    "examples"
                                },
                                additionalProperties = false
                            }
                        }
                    },
                    required = new[] { "families" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_activate_autonomous_learning",
                description =
                    "Activate the existing governed autonomous learning/acquisition runtime if the Founder explicitly asks Legend® Ai to turn on, continue, or automatically run LEGEND learning. This calls the existing runtime-policy authority and creates no new worker.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>(),
                    additionalProperties = false
                },
                strict = true
            }
        ];
    }

    private static string BuildInstructions(string mode)
    {
        const string governance = """
You are operating inside the Founder-only Legend® Ai interface.

CRITICAL GOVERNANCE:
- Your product name is exactly "Legend® Ai".
- Whenever you refer to yourself by product name, always write exactly "Legend® Ai".
- Never write your product name as "LEGEND AI", "LEGEND® Ai", "LEGEND Ai", "Legend AI", or any other variation.
- In Legend® Ai mode, if you introduce yourself by name, say "Legend® Ai".
- You are conversational reasoning, not a new LEGEND authority.
- Never claim a current LEGEND fact without inspecting the provided read-only tools when the answer depends on current system state.
- Never invent database state, evidence, training status, model versions, evaluation results, contradictions, readiness, capacity, or language coverage.
- Tool outputs come from existing LEGEND authorities and are the source of truth for current system facts.
- You can inspect LEGEND through read tools.
- You also have narrowly scoped Founder-authorized orchestration tools that delegate only to LEGEND's existing canonical Founder ingestion, curriculum, and runtime-policy authorities.
- Founder-authoritative mutation tools must never be called merely because you think they would be useful. Use Founder seed/curriculum/runtime mutation only when the Founder explicitly instructs you to teach, add, submit, retain, train, activate, or continue learning.
- The one exception is legend_submit_machine_learning_candidate: it is NON-AUTHORITATIVE retention only. You may use it automatically when the conversation genuinely discovers reusable linguistic knowledge with controlled contrasts. It creates only MachineProposed evidence and cannot approve itself.
- Founder-submitted source knowledge and curriculum are FounderApproved because the authenticated Founder explicitly directed the action.
- OpenAI-generated teaching is NOT automatically FounderApproved merely because it appears in conversation.
- Machine-derived teaching must continue through LEGEND's existing teacher, independent critic, canonical validator, curriculum admission, dataset compiler, challenger training, evaluation and promotion authorities.
- Before relying on general OpenAI recall for language knowledge, prefer the retained LEGEND context supplied with this request and use legend_search_retained_knowledge when deeper retrieval is useful.
- Retained authority precedence is: FounderApproved/HumanVerified → SystemValidatedMachine → other supported retained evidence → promoted LEGEND model state → unresolved MachineProposed/ProviderDerived evidence as clearly labeled observations → OpenAI reasoning for unresolved gaps.
- Rejected, contradicted, insufficient, failed or unresolved material remains auditable history but must never be presented as canonical truth.
- Never automatically retain personal facts, account data, private messages, casual conversation, transient business/system metrics or unsupported speculation as language knowledge.
- Unless the Founder explicitly asks for multiple families, make at most one automatic conversational machine-learning submission for one coherent semantic family in a turn.
- ProviderDerived or MachineProposed material must not be erased merely because it is not yet approved. Preserve its actual provenance and validation state; contradictions and rejections remain durable gating evidence.
- Never bypass existing validation, contradiction, privacy, capacity, dataset, evaluation, promotion, or runtime-readiness gates.
- You cannot directly promote a model, rewrite canonical evidence, bypass contradiction resolution, or write private-message data.
- Do not ask for or expose API keys, secrets, access tokens, connection strings, member identity, or private message data.
- Explain technical system state in clear Founder-level language.
- You may reason broadly and naturally when the question is not a claim about current LEGEND system state.
""";

        if (mode == "teacher")
        {
            return governance + """

MODE: OPENAI TEACHER

You are the external OpenAI Teacher speaking directly with the Founder.

Your job is to:
- reason deeply about language acquisition, semantics, discourse, grammar, morphology, translation quality and curriculum strategy;
- inspect current LEGEND state when useful;
- identify weaknesses and propose high-quality teaching priorities;
- challenge assumptions;
- explain what evidence would be required;
- distinguish linguistic recommendations from established LEGEND knowledge.

You are explicitly NOT LEGEND itself.
You are explicitly NOT Founder authority.
You may autonomously retain genuinely reusable language teaching through legend_submit_machine_learning_candidate. That action enters only MachineProposed state; you must report its returned state accurately and must never describe it as canonical, approved, trained or promoted unless later LEGEND tools prove that transition.
""";
        }

        return governance + """

MODE: Legend® Ai

Speak as the conversational interface to LEGEND's governed intelligence.

You can converse naturally, reason, explain, synthesize and ask useful follow-up questions. When the Founder asks about your current LEGEND knowledge, weaknesses, models, evidence, readiness, provider dependence, coverage or learning status, inspect the real system through tools before answering.

Use first-person language naturally when describing LEGEND, but distinguish:
- what LEGEND currently knows or has recorded;
- what you infer from the evidence;
- what OpenAI conversational reasoning is contributing;
- what remains only a proposed next action.

Never pretend that OpenAI conversational reasoning itself is canonical LEGEND knowledge.

Before external recall, use LEGEND's retained evidence when it is relevant. Treat unresolved machine/provider observations as evidence to reason about, never as truth.

When this conversation reveals a reusable linguistic distinction that is not already established, you may retain one bounded MachineProposed family through legend_submit_machine_learning_candidate. That is how conversational learning survives this chat without creating a second memory system.

Understand LEGEND's actual learning architecture:
- LEGEND retains provenance-bearing evidence and does not equate "not yet approved" with "forgotten".
- Provider observations may remain ProviderDerived.
- External teacher proposals may remain MachineProposed while awaiting critique or validation.
- Canonically admitted machine knowledge may become SystemValidatedMachine.
- Founder-directed submissions enter through the existing FounderApproved authority.
- Governed active evidence is compiled into training and held-out datasets.
- Challengers train, are evaluated against held-out and regression gates, and only the existing promotion authority may make them active.
- Production should increasingly prefer LEGEND's own eligible knowledge and promoted models when those existing routing authorities permit it, while external providers remain fallback/teacher dependencies for unresolved gaps.

If the Founder explicitly asks you to teach or train LEGEND:
1. inspect current state when relevant;
2. use the existing Founder seed or curriculum submission tool for the exact material the Founder is intentionally directing;
3. activate the existing autonomous learning runtime only when explicitly requested;
4. explain that the existing worker will continue provider acquisition, teacher proposals, independent critique, canonical validation, curriculum admission, dataset compilation, training, evaluation and promotion as configured.
""";
    }

    private async Task<LegendConnectRetainedKnowledgeSearchSnapshot>
        TryLoadRetainedKnowledgeAsync(
            ClaimsPrincipal founder,
            string query,
            CancellationToken cancellationToken)
    {
        try
        {
            return await _legend
                .SearchRetainedKnowledgeAsync(
                    founder,
                    query,
                    take: 12,
                    cancellationToken:
                        cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Legend Founder AI retained-knowledge retrieval failed closed.");

            return new LegendConnectRetainedKnowledgeSearchSnapshot(
                query,
                0,
                []);
        }
    }

    private static string BuildRetainedKnowledgeContext(
        LegendConnectRetainedKnowledgeSearchSnapshot snapshot)
    {
        if (snapshot.Items.Count == 0)
            return string.Empty;

        var json =
            JsonSerializer.Serialize(
                snapshot,
                JsonOptions);

        if (json.Length >
            MaximumRetainedContextCharacters)
        {
            json =
                json[
                    ..MaximumRetainedContextCharacters] +
                "\n[LEGEND RETAINED CONTEXT BOUNDED]";
        }

        return
            """

LEGEND_RETAINED_KNOWLEDGE_CONTEXT:
These records were retrieved from LEGEND before external reasoning.
Respect AuthorityState, Provenance, IsCanonical and IsContradicted.
Never upgrade an unresolved, rejected or contradicted record merely because it appears here.
""" +
            json;
    }

    private static IReadOnlyList<LegendFounderAiChatMessage>
        CompactProviderConversation(
            IReadOnlyList<LegendFounderAiChatMessage> conversation)
    {
        var selected =
            new List<LegendFounderAiChatMessage>(
                conversation.Count);

        var remaining =
            MaximumProviderConversationCharacters;

        for (var index =
                 conversation.Count - 1;
             index >= 0;
             index--)
        {
            var message =
                conversation[index];

            var length =
                message.Content?.Length ??
                0;

            if (index ==
                    conversation.Count - 1 ||
                length <= remaining)
            {
                selected.Add(message);

                remaining =
                    Math.Max(
                        0,
                        remaining - length);
            }

            if (remaining == 0)
                break;
        }

        selected.Reverse();

        return selected;
    }

    private static bool TryNormalizeMessages(
        IReadOnlyList<LegendFounderAiChatMessage>? messages,
        out List<LegendFounderAiChatMessage> normalized,
        out string error)
    {
        normalized = [];
        error = string.Empty;

        if (messages is null ||
            messages.Count == 0 ||
            messages.Count > MaximumConversationMessages)
        {
            error =
                $"Conversation must contain between 1 and {MaximumConversationMessages} messages.";
            return false;
        }

        var total = 0;

        foreach (var message in messages)
        {
            var role =
                message.Role?.Trim().ToLowerInvariant();

            if (role is not ("user" or "assistant"))
            {
                error = "Conversation contains an invalid message role.";
                return false;
            }

            var content = message.Content?.Trim();

            if (string.IsNullOrWhiteSpace(content) ||
                content.Length > MaximumMessageCharacters)
            {
                error =
                    $"Each message must contain 1–{MaximumMessageCharacters} characters.";
                return false;
            }

            total += content.Length;

            if (total > MaximumConversationCharacters)
            {
                error =
                    $"Conversation exceeds the {MaximumConversationCharacters}-character Founder AI limit.";
                return false;
            }

            normalized.Add(
                new LegendFounderAiChatMessage(
                    role,
                    content));
        }

        if (normalized[^1].Role != "user")
        {
            error = "The final conversation message must come from the Founder.";
            return false;
        }

        return true;
    }

    private static string NormalizeMode(string? mode) =>
        string.Equals(
            mode?.Trim(),
            "teacher",
            StringComparison.OrdinalIgnoreCase)
            ? "teacher"
            : "legend";

    private static string? ReadResponseState(
        JsonElement root) =>
        root.TryGetProperty(
            "status",
            out var status)
            ? status.GetString()
            : null;

    private static List<FounderAiToolCall> ReadFunctionCalls(
        JsonElement root)
    {
        var calls = new List<FounderAiToolCall>();

        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
            return calls;

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) ||
                !string.Equals(
                    type.GetString(),
                    "function_call",
                    StringComparison.Ordinal))
                continue;

            var name =
                item.TryGetProperty("name", out var nameValue)
                    ? nameValue.GetString()
                    : null;

            var callId =
                item.TryGetProperty("call_id", out var callIdValue)
                    ? callIdValue.GetString()
                    : null;

            var arguments =
                item.TryGetProperty("arguments", out var argsValue)
                    ? argsValue.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(callId))
                continue;

            calls.Add(
                new FounderAiToolCall(
                    callId,
                    name,
                    string.IsNullOrWhiteSpace(arguments)
                        ? "{}"
                        : arguments));
        }

        return calls;
    }

    private static string? ExtractOutputText(
        JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) ||
                !string.Equals(
                    type.GetString(),
                    "message",
                    StringComparison.Ordinal))
                continue;

            if (!item.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("type", out var partType) ||
                    !string.Equals(
                        partType.GetString(),
                        "output_text",
                        StringComparison.Ordinal))
                    continue;

                if (part.TryGetProperty("text", out var text))
                    return text.GetString();
            }
        }

        return null;
    }

    private static string? ReadRequiredString(
        JsonElement root,
        string propertyName) =>
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;

    private static string? ReadOptionalString(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
            return null;

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
    }

    private static string SerializeBounded(object? value)
    {
        var json =
            JsonSerializer.Serialize(
                value,
                JsonOptions);

        if (json.Length <= MaximumToolOutputCharacters)
            return json;

        return json[..MaximumToolOutputCharacters] +
               "\n[LEGEND TOOL OUTPUT TRUNCATED AT BOUNDED LIMIT]";
    }

    private sealed class LegendFounderAiProviderException
        : Exception
    {
        public LegendFounderAiProviderException(
            int statusCode,
            string clientRequestId,
            string? providerRequestId)
            : base(
                $"Legend Founder AI provider returned HTTP {statusCode}.")
        {
            StatusCode = statusCode;
            ClientRequestId = clientRequestId;
            ProviderRequestId = providerRequestId;
        }

        public int StatusCode { get; }

        public string ClientRequestId { get; }

        public string? ProviderRequestId { get; }
    }

    private sealed record FounderAiToolCall(
        string CallId,
        string Name,
        string Arguments);
}

public sealed record LegendFounderAiChatMessage(
    string? Role,
    string? Content);

public sealed class LegendFounderAiChatRequest
{
    public string? Mode { get; init; }

    public IReadOnlyList<LegendFounderAiChatMessage>? Messages
    {
        get;
        init;
    }
}

public sealed record LegendFounderAiChatResponse(
    bool Succeeded,
    string Mode,
    string? Message,
    string? Error,
    string? FailureKind = null,
    int? ProviderStatusCode = null,
    string? Reference = null)
{
    public static LegendFounderAiChatResponse Failure(
        string error,
        string? failureKind = null,
        int? providerStatusCode = null,
        string? reference = null) =>
        new(
            false,
            "legend",
            null,
            error,
            failureKind,
            providerStatusCode,
            reference);
}
