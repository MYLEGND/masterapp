using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPortal.Services.Analytics;
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
                        await ExecuteReadOnlyToolAsync(
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

    private async Task<string> ExecuteReadOnlyToolAsync(
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
- You have NO write tools.
- You cannot directly persist curriculum, evidence, corrections, training data, model promotion, runtime policy changes, or translations.
- A proposal made in conversation is only a proposal.
- Any future mutation must flow through LEGEND's existing governed Founder/lifecycle authorities.
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

    private static string SerializeBounded(object value)
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
