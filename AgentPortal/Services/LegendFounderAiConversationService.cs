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
    private const int MaximumConversationMessages = 20;
    private const int MaximumMessageCharacters = 6_000;
    private const int MaximumConversationCharacters = 30_000;
    private const int MaximumToolRounds = 4;
    private const int MaximumToolOutputCharacters = 30_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly FounderLegendConnectService _legend;
    private readonly ILogger<LegendFounderAiConversationService> _logger;

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
            return LegendFounderAiChatResponse.Failure(validationError);
        }

        var apiKey = OpenAiKeyResolver.Resolve(_configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return LegendFounderAiChatResponse.Failure(
                "Legend® Ai is not configured because the OpenAI API key is unavailable.");
        }

        var model =
            _configuration["OpenAI:LegendFounderAiModel"]?.Trim();

        if (string.IsNullOrWhiteSpace(model))
            model = _configuration["OpenAI:Model"]?.Trim();

        if (string.IsNullOrWhiteSpace(model))
            model = "gpt-5";

        var instructions = BuildInstructions(mode);
        var tools = BuildReadOnlyTools();

        var input = new List<object>(conversation.Count + 12);

        foreach (var message in conversation)
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
                        cancellationToken);

                if (responseDocument is null)
                {
                    return LegendFounderAiChatResponse.Failure(
                        "Legend® Ai could not reach its conversational reasoning provider.");
                }

                var root = responseDocument.RootElement;

                if (!TryReadCompletedResponse(root))
                {
                    return LegendFounderAiChatResponse.Failure(
                        "Legend® Ai received an incomplete reasoning response.");
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
                            cancellationToken);

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
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return LegendFounderAiChatResponse.Failure(
                "Legend® Ai timed out while reasoning over the current system state.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI provider request failed.");

            return LegendFounderAiChatResponse.Failure(
                "Legend® Ai could not reach its conversational reasoning provider.");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI received invalid provider JSON.");

            return LegendFounderAiChatResponse.Failure(
                "Legend® Ai received an invalid reasoning response.");
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
            max_output_tokens = 2_500
        };

        using var request =
            new HttpRequestMessage(
                HttpMethod.Post,
                "v1/responses")
            {
                Content = JsonContent.Create(payload)
            };

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                apiKey);

        var client =
            _httpClientFactory.CreateClient("OpenAI");

        using var response =
            await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "LEGEND Founder AI provider returned HTTP {StatusCode}.",
                (int)response.StatusCode);

            return null;
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        return await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
    }

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
                                JsonValueKind.Object)
                        {
                            return """{"error":"invalid_curriculum_example"}""";
                        }

                        var variations =
                            new Dictionary<string, string>(
                                StringComparer.OrdinalIgnoreCase);

                        foreach (var property in
                                 variationsElement
                                     .EnumerateObject())
                        {
                            if (property.Value.ValueKind !=
                                    JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(
                                    property.Value.GetString()))
                            {
                                return """{"error":"invalid_curriculum_variation"}""";
                            }

                            variations[property.Name] =
                                property.Value
                                    .GetString()!
                                    .Trim();
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
                return """{"error":"unknown_read_only_tool"}""";
        }
    }

    private static IReadOnlyList<object> BuildReadOnlyTools()
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
                                                    type = "object",
                                                    additionalProperties =
                                                        new
                                                        {
                                                            type =
                                                                "string",
                                                            minLength = 1,
                                                            maxLength = 240
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
- Never call a mutation tool merely because you think it would be useful. Call one only when the Founder explicitly instructs you to teach, add, submit, retain, train, activate, or continue learning.
- Founder-submitted source knowledge and curriculum are FounderApproved because the authenticated Founder explicitly directed the action.
- OpenAI-generated teaching is NOT automatically FounderApproved merely because it appears in conversation.
- Machine-derived teaching must continue through LEGEND's existing teacher, independent critic, canonical validator, curriculum admission, dataset compiler, challenger training, evaluation and promotion authorities.
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
You may recommend teaching, but you may not claim it entered LEGEND.
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

    private static bool TryReadCompletedResponse(
        JsonElement root)
    {
        return root.TryGetProperty(
                   "status",
                   out var status) &&
               string.Equals(
                   status.GetString(),
                   "completed",
                   StringComparison.Ordinal);
    }

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
    string? Error)
{
    public static LegendFounderAiChatResponse Failure(
        string error) =>
        new(
            false,
            "legend",
            null,
            error);
}
