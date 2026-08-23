using System.Diagnostics;
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
/// OpenAI is an escalation path for unsupported requests. Governed native
/// LEGEND inference is always attempted first in normal LEGEND mode.
/// </summary>
public sealed class LegendFounderAiConversationService
{
    private const int MaximumConversationMessages = 30;
    private const int MaximumMessageCharacters = 500_000;
    private const int MaximumConversationCharacters = 750_000;
    private const int MinimumProviderConversationCharacters = 60_000;
    private const int MaximumProviderConversationCharacters = 180_000;
    private const int MinimumLatestMessageTailCharacters = 12_000;
    private const int MinimumToolRounds = 4;
    private const int MaximumToolRounds = 10;
    private const int MinimumFinalizationReserveSeconds = 6;
    private const int MinimumFinalSynthesisWindowSeconds = 20;
    private const int MaximumProviderRoundSeconds = 75;
    private const int MinimumCasualOutputTokens = 256;
    private const int MaximumCasualOutputTokens = 1_200;
    private const int MinimumRetainedKnowledgeLookupSeconds = 4;
    private const int MaximumRetainedKnowledgeLookupSeconds = 12;
    private const int MinimumReadOnlyToolSeconds = 8;
    private const int MaximumReadOnlyToolSeconds = 20;
    private const int MinimumToolOutputCharacters = 20_000;
    private const int MaximumToolOutputCharacters = 80_000;
    private const int MinimumRetainedContextCharacters = 16_000;
    private const int MaximumRetainedContextCharacters = 64_000;
    private const int MinimumProviderAttemptWindowSeconds = 3;
    private const int MaximumProviderCooldownSeconds = 300;
    private const int MaximumTransientProviderAttempts = 3;
    private const int MaximumDiscourseObservationSeconds = 2;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly FounderLegendConnectService _legend;
    private readonly LegendFounderAiDiscourseStateService? _discourse;
    private readonly ILogger<LegendFounderAiConversationService> _logger;
    private readonly int _timeoutSeconds;
    private readonly int _maxOutputTokens;
    private readonly string _reasoningEffort;
    private readonly string _serviceTier;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    public LegendFounderAiConversationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        FounderLegendConnectService legend,
        ILogger<LegendFounderAiConversationService> logger,
        LegendFounderAiDiscourseStateService? discourse = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _legend = legend;
        _discourse = discourse;
        _logger = logger;

        _timeoutSeconds =
            Math.Clamp(
                configuration.GetValue<int?>(
                    "OpenAI:LegendFounderAiTimeoutSeconds") ??
                    120,
                45,
                240);

        _maxOutputTokens =
            Math.Clamp(
                configuration.GetValue<int?>(
                    "OpenAI:LegendFounderAiMaxOutputTokens") ??
                    8_000,
                1_500,
                16_000);

        _reasoningEffort =
            NormalizeReasoningEffort(
                configuration[
                    "OpenAI:LegendFounderAiReasoningEffort"]);

        _serviceTier =
            NormalizeServiceTier(
                configuration[
                    "OpenAI:LegendFounderAiServiceTier"]);
    }

    public async Task<LegendFounderAiChatResponse> ReplyAsync(
        ClaimsPrincipal founder,
        LegendFounderAiChatRequest request,
        CancellationToken cancellationToken = default,
        Func<
            LegendFounderAiProgressEvent,
            CancellationToken,
            ValueTask>? progress = null)
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

        await ReportProgressAsync(
            progress,
            new LegendFounderAiProgressEvent(
                "accepted",
                "Request accepted. Preparing the current conversation context."),
            cancellationToken);

        using var requestBudget =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        requestBudget.CancelAfter(
            TimeSpan.FromSeconds(
                _timeoutSeconds));

        var effectiveToken =
            requestBudget.Token;

        var executionClock =
            Stopwatch.StartNew();

        LegendConnectNativeInferenceSnapshot? nativeInference = null;

        if (string.Equals(mode, "legend", StringComparison.Ordinal))
        {
            await ReportProgressAsync(
                progress,
                new LegendFounderAiProgressEvent(
                    "native_inference",
                    "Checking governed LEGEND knowledge before external escalation."),
                effectiveToken);

            try
            {
                await ObserveDiscourseMeaningAsync(
                    founder,
                    request.ConversationId,
                    "user",
                    conversation[^1].Content ?? string.Empty,
                    effectiveToken,
                    cancellationToken);
                var context = conversation
                    .Take(conversation.Count - 1)
                    .Select(message => new LegendConnectConversationContextItem(
                        message.Role ?? string.Empty,
                        message.Content ?? string.Empty))
                    .ToArray();
                var discourseState = _discourse is null
                    ? null
                    : await _discourse.GetStateAsync(founder, request.ConversationId, effectiveToken);
                // V20.3: use the discourse-aware native authority only when
                // this service actually has durable discourse state available.
                // When it does not, preserve the existing governed direct
                // native authority rather than manufacturing an unsupported
                // discourse result and incorrectly crossing into provider
                // escalation. Both branches reuse existing LEGEND authorities;
                // neither branch overrides or duplicates inference logic.
                nativeInference = _discourse is null
                    ? await _legend.TryInferConversationAsync(
                        founder,
                        conversation[^1].Content ?? string.Empty,
                        context,
                        effectiveToken)
                    : await _legend.TryInferConversationWithDiscourseAsync(
                        founder,
                        conversation[^1].Content ?? string.Empty,
                        context,
                        discourseState,
                        effectiveToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Native inference is strictly fail-closed. A read failure
                // cannot manufacture an answer or suppress the existing
                // external escalation path.
                _logger.LogWarning(
                    exception,
                    "LEGEND native conversational inference was unavailable; escalating without a native answer.");
            }

            if (nativeInference is { Supported: true } &&
                !string.IsNullOrWhiteSpace(nativeInference.Answer))
            {
                // Assistant turns participate in the same conversation state
                // only as governed structural observations. No answer text is
                // persisted and this never becomes a reply cache.
                await ObserveDiscourseMeaningAsync(
                    founder,
                    request.ConversationId,
                    "assistant",
                    nativeInference.Answer,
                    effectiveToken,
                    cancellationToken);
                await ReportProgressAsync(
                    progress,
                    new LegendFounderAiProgressEvent(
                        "native_response",
                        $"Answered from {nativeInference.EvidenceCount} governed LEGEND evidence record(s)."),
                    effectiveToken);

                return new LegendFounderAiChatResponse(
                    true,
                    mode,
                    nativeInference.Answer,
                    null);
            }
        }

        // V20.3: the native semantic authority distinguishes between
        // genuinely unknown source meaning, which may use the configured
        // external teacher, and a governed fail-closed boundary, which may
        // not be crossed by generated provider content.
        //
        // ReplyAsync owns no reason-code policy; it consumes the existing
        // RequiresEscalation decision returned by LEGEND operations.
        if (nativeInference is
            {
                Supported: false,
                RequiresEscalation: false
            })
        {
            return NativeInferenceUnavailableResponse(
                mode,
                nativeInference);
        }

        var apiKey = OpenAiKeyResolver.Resolve(_configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
            return NativeInferenceUnavailableResponse(mode, nativeInference);

        var model =
            _configuration["OpenAI:LegendFounderAiModel"]?.Trim();

        if (string.IsNullOrWhiteSpace(model))
            model = _configuration["OpenAI:Model"]?.Trim();

        if (string.IsNullOrWhiteSpace(model))
            model = "gpt-5";

        var requiresGovernedInspection =
            RequiresGovernedInspection(conversation, mode);

        LegendConnectRetainedKnowledgeSearchSnapshot? retainedKnowledge = null;

        if (requiresGovernedInspection)
        {
            await ReportProgressAsync(
                progress,
                new LegendFounderAiProgressEvent(
                    "retained_knowledge",
                    "Checking retained LEGEND knowledge relevant to this request."),
                effectiveToken);

            var retainedKnowledgeQuery =
                BuildRetainedKnowledgeQuery(conversation);

            retainedKnowledge =
                await TryLoadRetainedKnowledgeAsync(
                    founder,
                    retainedKnowledgeQuery,
                    conversation,
                    effectiveToken);

            await ReportProgressAsync(
                progress,
                new LegendFounderAiProgressEvent(
                    "retained_knowledge",
                    retainedKnowledge.Items.Count > 0
                        ? $"Found {retainedKnowledge.Items.Count} relevant retained LEGEND record(s)."
                        : "No directly matching retained LEGEND records were found; continuing with the governed tools available for this request."),
                effectiveToken);
        }

        var instructions =
            requiresGovernedInspection
                ? BuildInstructions(mode) +
                  (retainedKnowledge is null
                      ? string.Empty
                      : BuildRetainedKnowledgeContext(
                          retainedKnowledge,
                          ResolveRetainedContextBudget(conversation)))
                : BuildCasualInstructions();

        var tools = BuildFounderTools();

        var providerConversation =
            CompactProviderConversation(
                conversation,
                ResolveProviderConversationBudget(conversation));

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
            var maximumToolRounds =
                requiresGovernedInspection
                    ? ResolveMaximumToolRounds(conversation)
                    : 1;

            for (var round = 0; round < maximumToolRounds; round++)
            {
                var remaining =
                    TimeSpan.FromSeconds(
                        _timeoutSeconds) -
                    executionClock.Elapsed;

                if (remaining <=
                    TimeSpan.FromSeconds(
                        MinimumFinalizationReserveSeconds))
                {
                    await ReportProgressAsync(
                        progress,
                        new LegendFounderAiProgressEvent(
                            "time_budget",
                            "The current request window is nearly exhausted; stopping additional inspection instead of allowing the gateway to terminate the request.",
                            round + 1),
                        effectiveToken);

                    return LegendFounderAiChatResponse.Failure(
                        "Legend® Ai reached the current request window before another provider round could safely begin. Ask it to continue from the current point.",
                        "timeout");
                }

                var allowTools =
                    requiresGovernedInspection &&
                    round < maximumToolRounds - 1 &&
                    remaining >
                        TimeSpan.FromSeconds(
                            MinimumFinalSynthesisWindowSeconds);

                var providerBudget =
                    ResolveProviderBudget(
                        conversation,
                        requiresGovernedInspection,
                        allowTools,
                        remaining);

                await ReportProgressAsync(
                    progress,
                    new LegendFounderAiProgressEvent(
                        allowTools &&
                        round == 0
                            ? "planning"
                            : "synthesis",
                        allowTools &&
                        round == 0
                            ? "Planning the response and determining which governed LEGEND checks are actually needed."
                            : requiresGovernedInspection
                                ? allowTools
                                    ? "Integrating the governed results already collected and determining whether another check is necessary."
                                    : "Finalizing the best supported response from the governed evidence already collected."
                                : "Preparing the conversational response.",
                        round + 1),
                    effectiveToken);

                using var responseDocument =
                    await SendResponseAsync(
                        apiKey,
                        model,
                        instructions,
                        input,
                        tools,
                        allowTools,
                        providerBudget,
                        ResolveReasoningEffortForRound(
                            round,
                            requiresGovernedInspection,
                            _reasoningEffort),
                        ResolveMaxOutputTokens(
                            conversation,
                            requiresGovernedInspection,
                            _maxOutputTokens),
                        effectiveToken);

                if (responseDocument is null)
                {
                    return NativeInferenceUnavailableResponse(mode, nativeInference);
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
                            "\n\n[This response reached the provider output window. Ask Legend® Ai to continue if you want the remainder.]",
                            null);
                    }

                    return LegendFounderAiChatResponse.Failure(
                        "Legend® Ai reached the provider output window before producing usable text.");
                }

                if (responseState != "completed")
                {
                    return LegendFounderAiChatResponse.Failure(
                        "Legend® Ai received an unusable reasoning response.");
                }

                var toolCalls = ReadFunctionCalls(root);

                if (toolCalls.Count == 0)
                {
                    await ReportProgressAsync(
                        progress,
                        new LegendFounderAiProgressEvent(
                            "response",
                            "The required checks are complete. Finalizing the response.",
                            round + 1),
                        effectiveToken);

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

                await ReportProgressAsync(
                    progress,
                    new LegendFounderAiProgressEvent(
                        "tools",
                        $"The model requested {toolCalls.Count} governed LEGEND check(s) in this step.",
                        round + 1),
                    effectiveToken);

                if (root.TryGetProperty("output", out var output) &&
                    output.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in output.EnumerateArray())
                        input.Add(item.Clone());
                }

                var toolOutputBudget =
                    ResolveToolOutputBudget(
                        providerConversation,
                        input.Count);

                foreach (var call in toolCalls)
                {
                    var toolDescription =
                        DescribeFounderToolCall(
                            call);

                    await ReportProgressAsync(
                        progress,
                        new LegendFounderAiProgressEvent(
                            "tool",
                            toolDescription,
                            round + 1,
                            call.Name),
                        effectiveToken);

                    var toolOutput =
                        await ExecuteFounderToolWithBudgetAsync(
                            founder,
                            call,
                            ResolveReadOnlyToolBudget(remaining),
                            toolOutputBudget,
                            effectiveToken);

                    await ReportProgressAsync(
                        progress,
                        new LegendFounderAiProgressEvent(
                            "tool_complete",
                            $"Completed: {toolDescription}",
                            round + 1,
                            call.Name),
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
                "Legend® Ai reached the current inspection window. Ask it to continue if more governed checks are needed.");
        }
        catch (LegendFounderAiProviderException exception)
        {
            _logger.LogWarning(
                "LEGEND Founder AI provider rejected the escalation. HTTP={StatusCode} ClientRequestId={ClientRequestId} ProviderRequestId={ProviderRequestId}",
                exception.StatusCode,
                exception.ClientRequestId,
                exception.ProviderRequestId);

            return NativeInferenceUnavailableResponse(mode, nativeInference);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return NativeInferenceUnavailableResponse(mode, nativeInference);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI provider transport failed.");

            return NativeInferenceUnavailableResponse(mode, nativeInference);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI received invalid provider JSON.");

            return NativeInferenceUnavailableResponse(mode, nativeInference);
        }
    }

    private async Task ObserveDiscourseMeaningAsync(
        ClaimsPrincipal founder,
        string? conversationId,
        string role,
        string surface,
        CancellationToken inferenceCancellationToken,
        CancellationToken requestCancellationToken)
    {
        using var observationBudget = CancellationTokenSource.CreateLinkedTokenSource(
            inferenceCancellationToken);
        observationBudget.CancelAfter(
            TimeSpan.FromSeconds(MaximumDiscourseObservationSeconds));
        try
        {
            if (_discourse is null)
                return;

            var meaning = await _legend.AnalyzeReusableMeaningGraphAsync(
                founder,
                surface,
                observationBudget.Token);
            await _discourse.RecordObservationAsync(
                founder,
                conversationId,
                role,
                meaning,
                observationBudget.Token);
        }
        catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("LEGEND discourse observation reached its bounded window.");
        }
        catch (Exception exception)
        {
            // Conversation state is durable observability, not a second
            // inference authority. A failed state write must not turn a
            // governed native reply into a provider fallback.
            _logger.LogWarning(exception, "LEGEND discourse observation persistence failed.");
        }
    }

    private async Task<JsonDocument?> SendResponseAsync(
        string apiKey,
        string model,
        string instructions,
        IReadOnlyList<object> input,
        IReadOnlyList<object> tools,
        bool allowTools,
        TimeSpan providerBudget,
        string reasoningEffort,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model,
            store = false,
            instructions,
            input,
            tools =
                allowTools
                    ? tools
                    : Array.Empty<object>(),

            tool_choice =
                allowTools
                    ? "auto"
                    : "none",

            parallel_tool_calls = allowTools,
            truncation = "auto",

            reasoning = new
            {
                effort = reasoningEffort
            },

            service_tier = _serviceTier,
            max_output_tokens = maxOutputTokens
        };

        var client =
            _httpClientFactory.CreateClient(
                "OpenAI");

        client.Timeout =
            Timeout.InfiniteTimeSpan;

        var providerClock =
            Stopwatch.StartNew();

        var attempt = 0;

        while (true)
        {
            attempt++;

            var attemptRemaining =
                providerBudget -
                providerClock.Elapsed;

            if (attemptRemaining <=
                TimeSpan.FromSeconds(
                    MinimumProviderAttemptWindowSeconds))
            {
                throw new OperationCanceledException();
            }

            using var providerAttempt =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken);

            providerAttempt.CancelAfter(
                attemptRemaining);

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
                    providerAttempt.Token);

            if (response.IsSuccessStatusCode)
            {
                await using var stream =
                    await response.Content
                        .ReadAsStreamAsync(
                            providerAttempt.Token);

                return await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken:
                        providerAttempt.Token);
            }

            var errorBody =
                await response.Content
                    .ReadAsStringAsync(
                        providerAttempt.Token);

            if (errorBody.Length > 1_000)
                errorBody = errorBody[..1_000];

            var transient =
                IsTransientOpenAiStatus(
                    response.StatusCode) &&
                attempt < MaximumTransientProviderAttempts &&
                !IsBillingOrQuotaRejection(
                    response.StatusCode,
                    errorBody);

            if (transient)
            {
                var delay =
                    ResolveProviderRetryDelay(
                        response,
                        attempt);

                var remainingAfterResponse =
                    providerBudget -
                    providerClock.Elapsed;

                if (remainingAfterResponse >
                    TimeSpan.FromSeconds(
                        MinimumProviderAttemptWindowSeconds))
                {
                    var maximumDelay =
                        remainingAfterResponse -
                        TimeSpan.FromSeconds(
                            MinimumProviderAttemptWindowSeconds);

                    var boundedDelay =
                        delay <= maximumDelay
                            ? delay
                            : maximumDelay;

                    if (boundedDelay > TimeSpan.Zero)
                    {
                        _logger.LogWarning(
                            "LEGEND Founder AI provider transient rejection. " +
                            "HTTP={StatusCode} Attempt={Attempt} RetryDelayMs={RetryDelayMs} " +
                            "RequestReset={RequestReset} TokenReset={TokenReset}",
                            (int)response.StatusCode,
                            attempt,
                            (long)Math.Ceiling(boundedDelay.TotalMilliseconds),
                            GetProviderHeader(response, "x-ratelimit-reset-requests") ?? "unavailable",
                            GetProviderHeader(response, "x-ratelimit-reset-tokens") ?? "unavailable");

                        await Task.Delay(
                            boundedDelay,
                            cancellationToken);

                        continue;
                    }
                }
            }

            var providerRequestId =
                GetProviderHeader(
                    response,
                    "x-request-id");

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
    }

    private static TimeSpan ResolveProviderRetryDelay(
        HttpResponseMessage response,
        int attempt)
    {
        var retryAfter =
            response.Headers.RetryAfter?.Delta;

        if (retryAfter is null &&
            response.Headers.RetryAfter?.Date is { } retryDate)
        {
            var datedDelay =
                retryDate - DateTimeOffset.UtcNow;

            if (datedDelay > TimeSpan.Zero)
                retryAfter = datedDelay;
        }

        var resetDelay =
            ReadLongestRateLimitReset(response);

        var providerDelay =
            retryAfter is null
                ? resetDelay
                : resetDelay is null || retryAfter >= resetDelay
                    ? retryAfter
                    : resetDelay;

        if (providerDelay is { } hinted &&
            hinted > TimeSpan.Zero)
        {
            return hinted > TimeSpan.FromSeconds(MaximumProviderCooldownSeconds)
                ? TimeSpan.FromSeconds(MaximumProviderCooldownSeconds)
                : hinted;
        }

        var exponent = Math.Min(Math.Max(attempt - 1, 0), 6);
        var seconds = Math.Pow(2, exponent) + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan? ReadLongestRateLimitReset(
        HttpResponseMessage response)
    {
        TimeSpan? longest = null;

        foreach (var name in new[]
                 {
                     "x-ratelimit-reset-requests",
                     "x-ratelimit-reset-tokens"
                 })
        {
            var raw = GetProviderHeader(response, name);
            if (!TryParseProviderDuration(raw, out var parsed))
                continue;

            if (longest is null || parsed > longest.Value)
                longest = parsed;
        }

        return longest;
    }

    private static bool TryParseProviderDuration(
        string? raw,
        out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();
        var index = 0;
        double totalMilliseconds = 0;

        while (index < value.Length)
        {
            var numberStart = index;
            while (index < value.Length &&
                   (char.IsDigit(value[index]) || value[index] == '.'))
            {
                index++;
            }

            if (numberStart == index ||
                !double.TryParse(
                    value[numberStart..index],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var amount))
            {
                return false;
            }

            string unit;
            if (value.AsSpan(index).StartsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                unit = "ms";
                index += 2;
            }
            else if (index < value.Length)
            {
                unit = char.ToLowerInvariant(value[index]).ToString();
                index++;
            }
            else
            {
                return false;
            }

            totalMilliseconds += unit switch
            {
                "ms" => amount,
                "s" => amount * 1_000d,
                "m" => amount * 60_000d,
                "h" => amount * 3_600_000d,
                _ => double.NaN
            };

            if (double.IsNaN(totalMilliseconds))
                return false;
        }

        if (totalMilliseconds <= 0 || double.IsInfinity(totalMilliseconds))
            return false;

        duration = TimeSpan.FromMilliseconds(totalMilliseconds);
        return true;
    }

    private static string? GetProviderHeader(
        HttpResponseMessage response,
        string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;

    private static ValueTask ReportProgressAsync(
        Func<
            LegendFounderAiProgressEvent,
            CancellationToken,
            ValueTask>? progress,
        LegendFounderAiProgressEvent update,
        CancellationToken cancellationToken) =>
        progress is null
            ? ValueTask.CompletedTask
            : progress(
                update,
                cancellationToken);

    private static LegendFounderAiChatResponse NativeInferenceUnavailableResponse(
        string mode,
        LegendConnectNativeInferenceSnapshot? nativeInference) =>
        new(
            true,
            mode,
            nativeInference is
            {
                Supported: false,
                RequiresEscalation: false
            }
                ? "LEGEND established the governed meaning of this request but does not have sufficient governed evidence to complete the answer. No unsupported answer was produced."
                : nativeInference is { ReasonCode.Length: > 0 }
                    ? "LEGEND does not yet have enough governed evidence to answer this directly, and its external teacher is unavailable. No unsupported answer was produced."
                    : "LEGEND could not establish enough governed evidence for a direct answer, and its external teacher is unavailable. No unsupported answer was produced.",
            null);

    private static string DescribeFounderToolCall(
        FounderAiToolCall call)
    {
        string? argument = null;

        try
        {
            using var document =
                JsonDocument.Parse(
                    call.Arguments);

            argument =
                call.Name switch
                {
                    "legend_language_state" or
                    "legend_language_knowledge" =>
                        ReadRequiredString(
                            document.RootElement,
                            "language"),

                    "legend_pair_health" =>
                        ReadRequiredString(
                            document.RootElement,
                            "pair"),

                    "legend_metric_detail" =>
                        ReadRequiredString(
                            document.RootElement,
                            "metric_key"),

                    "legend_search_retained_knowledge" =>
                        ReadRequiredString(
                            document.RootElement,
                            "query"),

                    "legend_submit_machine_learning_candidate" =>
                        ReadRequiredString(
                            document.RootElement,
                            "family_key"),

                    "legend_submit_founder_seed" =>
                        ReadRequiredString(
                            document.RootElement,
                            "source_language"),

                    _ => null
                };
        }
        catch (JsonException)
        {
        }

        if (!string.IsNullOrWhiteSpace(argument) &&
            argument.Length > 120)
        {
            argument =
                argument[..117] +
                "...";
        }

        var subject =
            string.IsNullOrWhiteSpace(argument)
                ? string.Empty
                : $": {argument}";

        return call.Name switch
        {
            "legend_system_overview" =>
                "Reading current governed LEGEND system metrics and readiness.",

            "legend_language_state" =>
                $"Inspecting the current governed language state{subject}.",

            "legend_metric_detail" =>
                $"Reading the governed evidence behind metric{subject}.",

            "legend_provider_capacity" =>
                "Checking current translation-provider capacity and consumption.",

            "legend_language_knowledge" =>
                $"Inspecting retained canonical knowledge and learning evidence{subject}.",

            "legend_pair_health" =>
                $"Checking directional language-pair health{subject}.",

            "legend_translation_quality" =>
                "Reviewing translation-quality evidence, contradictions and verification state.",

            "legend_target_realizations" =>
                "Reviewing retained target-realization hypotheses and their evidence.",

            "legend_search_retained_knowledge" =>
                $"Searching retained LEGEND language evidence{subject}.",

            "legend_submit_machine_learning_candidate" =>
                $"Submitting one bounded MachineProposed family through the existing governed lifecycle{subject}.",

            "legend_submit_founder_seed" =>
                $"Submitting the Founder-directed source seed through the existing Founder authority{subject}.",

            "legend_submit_founder_curriculum" =>
                "Submitting the Founder-directed curriculum through the existing canonical curriculum authority.",

            "legend_activate_autonomous_learning" =>
                "Activating the existing governed autonomous-learning runtime.",

            _ =>
                "Executing the governed LEGEND operation requested for this response."
        };
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

    /// <summary>
    /// A 429 can mean temporary request pressure or a durable billing/quota
    /// refusal. Only the former is retryable. The provider's structured error
    /// classification is authoritative here; status code alone is not.
    /// </summary>
    private static bool IsBillingOrQuotaRejection(
        HttpStatusCode status,
        string? errorBody)
    {
        if (status != HttpStatusCode.TooManyRequests ||
            string.IsNullOrWhiteSpace(errorBody))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(errorBody);
            var error = document.RootElement.TryGetProperty("error", out var nestedError)
                ? nestedError
                : document.RootElement;

            var type = ReadOptionalString(error, "type");
            var code = ReadOptionalString(error, "code");
            return new[] { type, code }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Any(value =>
                    value!.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("billing", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("quota", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<string> ExecuteFounderToolWithBudgetAsync(
        ClaimsPrincipal founder,
        FounderAiToolCall call,
        TimeSpan readOnlyBudget,
        int outputBudgetCharacters,
        CancellationToken cancellationToken)
    {
        if (!IsReadOnlyFounderTool(
                call.Name))
        {
            var mutationOutput = await ExecuteFounderToolAsync(
                founder,
                call,
                cancellationToken);
            return BoundSerializedOutput(mutationOutput, outputBudgetCharacters);
        }

        using var toolBudget =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        toolBudget.CancelAfter(readOnlyBudget);

        try
        {
            var output = await ExecuteFounderToolAsync(
                founder,
                call,
                toolBudget.Token);
            return BoundSerializedOutput(output, outputBudgetCharacters);
        }
        catch (OperationCanceledException)
            when (
                toolBudget.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Legend Founder AI read-only tool {Tool} exceeded its {Seconds:F1}-second dynamic budget; continuing with partial governed evidence.",
                call.Name,
                readOnlyBudget.TotalSeconds);

            return JsonSerializer.Serialize(
                new
                {
                    error = "tool_timeout",
                    tool = call.Name
                },
                JsonOptions);
        }
    }

    private static bool IsReadOnlyFounderTool(
        string name) =>
        name is
            "legend_system_overview" or
            "legend_provider_capacity" or
            "legend_language_knowledge" or
            "legend_pair_health" or
            "legend_translation_quality" or
            "legend_target_realizations" or
            "legend_search_retained_knowledge" or
            "legend_metric_detail" or
            "legend_language_state";

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

                return SerializeUnbounded(snapshot);
            }

            case "legend_provider_capacity":
            {
                var snapshot =
                    await _legend.GetProviderCapacityAsync(
                        founder,
                        cancellationToken);

                return SerializeUnbounded(snapshot);
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

                return SerializeUnbounded(snapshot);
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

                return SerializeUnbounded(snapshot);
            }

            case "legend_translation_quality":
            {
                var snapshot =
                    await _legend.GetTranslationQualityAsync(
                        founder,
                        cancellationToken);

                return SerializeUnbounded(snapshot);
            }

            case "legend_target_realizations":
            {
                var snapshot =
                    await _legend.GetTargetRealizationReviewAsync(
                        founder,
                        cancellationToken);

                return SerializeUnbounded(snapshot);
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
                            ResolveRetainedKnowledgeTake(query),
                            cancellationToken);

                return SerializeUnbounded(
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

                return SerializeUnbounded(
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

                return SerializeUnbounded(result);
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

                return SerializeUnbounded(result);
            }

            case "legend_activate_autonomous_learning":
            {
                var result =
                    await _legend.EnsureAutonomousLearningActiveAsync(
                        founder,
                        cancellationToken);

                return SerializeUnbounded(result);
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

                return SerializeUnbounded(snapshot);
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
                    await _legend.GetLanguageStateAsync(
                        founder,
                        language,
                        pair,
                        cancellationToken);

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

                return SerializeUnbounded(safeProjection);
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
                type = "web_search",
                search_context_size = "medium"
            },
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
                    "Search LEGEND's existing retained language evidence before relying on general OpenAI recall. Results preserve provenance, authority, contradiction and proposal state. Use focused semantic phrases rather than copying an entire long conversation.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 8_000
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
- Tool outputs from existing LEGEND authorities are the source of truth for current LEGEND system facts.
- You can inspect LEGEND through read tools.
- Native OpenAI web search is available for current external research, verification, trusted linguistic references, standards, documentation and other information that is not already established by LEGEND.
- When external research is materially useful, prefer authoritative primary sources, official documentation, recognized linguistic institutions, standards bodies, universities and other high-quality sources over low-authority summaries.
- External web research is evidence for reasoning; it does not become canonical LEGEND knowledge merely because OpenAI found it.
- Never use external web search as a substitute for governed LEGEND tools when the question concerns current LEGEND database state, retained evidence, training state, readiness, provider consumption or internal system facts.
- You also have narrowly scoped Founder-authorized orchestration tools that delegate only to LEGEND's existing canonical Founder ingestion, curriculum, and runtime-policy authorities.
- Founder-authoritative mutation tools must never be called merely because you think they would be useful. Use Founder seed/curriculum/runtime mutation only when the Founder explicitly instructs you to teach, add, submit, retain, train, activate, or continue learning.
- The one exception is legend_submit_machine_learning_candidate: it is NON-AUTHORITATIVE retention only. You may use it automatically when the conversation genuinely discovers reusable linguistic knowledge with controlled contrasts. It creates only MachineProposed evidence and cannot approve itself.
- Founder-submitted source knowledge and curriculum are FounderApproved because the authenticated Founder explicitly directed the action.
- OpenAI-generated teaching is NOT automatically FounderApproved merely because it appears in conversation.
- Machine-derived teaching must continue through LEGEND's existing teacher, independent critic, canonical validator, curriculum admission, dataset compiler, challenger training, evaluation and promotion authorities.
- Before relying on general OpenAI recall for language knowledge, prefer the retained LEGEND context supplied with this request and use legend_search_retained_knowledge when deeper retrieval is useful.
- When the supplied retained context is sparse, ambiguous or contradicted, search retained knowledge again with narrower semantic queries before concluding that LEGEND lacks the knowledge.
- Prefer evidence synthesis over raw volume: combine high-authority retained records, relevant conversation state and narrowly selected governed tool results; do not repeat duplicate evidence merely because it is available.
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

    private static string BuildCasualInstructions() =>
        """
You are Legend® Ai speaking with the Founder.
Respond naturally, directly, and conversationally.
Use the product name exactly as "Legend® Ai" if you name yourself.
Do not claim current LEGEND database, training, readiness, provider, evidence, or system-state facts in this conversational path.
Do not invent internal state, private data, or operational results.
If the Founder asks for current LEGEND system facts, that request belongs to the governed inspection path rather than casual conversation.
""";

    private async Task<LegendConnectRetainedKnowledgeSearchSnapshot>
        TryLoadRetainedKnowledgeAsync(
            ClaimsPrincipal founder,
            string query,
            IReadOnlyList<LegendFounderAiChatMessage> conversation,
            CancellationToken cancellationToken)
    {
        using var lookupBudget =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        var seconds =
            ResolveRetainedKnowledgeLookupSeconds(conversation);

        lookupBudget.CancelAfter(
            TimeSpan.FromSeconds(seconds));

        try
        {
            return await _legend
                .SearchRetainedKnowledgeAsync(
                    founder,
                    query,
                    take: ResolveRetainedKnowledgeTake(query),
                    cancellationToken:
                        lookupBudget.Token);
        }
        catch (OperationCanceledException)
            when (
                lookupBudget.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Legend Founder AI retained-knowledge lookup exceeded its optional {Seconds}-second dynamic budget; continuing without retained context.",
                seconds);

            return new LegendConnectRetainedKnowledgeSearchSnapshot(
                query,
                0,
                []);
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

    private static string BuildRetainedKnowledgeQuery(
        IReadOnlyList<LegendFounderAiChatMessage> conversation)
    {
        var userMessages = conversation
            .Where(message => string.Equals(message.Role, "user", StringComparison.Ordinal))
            .Select(message => message.Content?.Trim())
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .Cast<string>()
            .ToArray();

        if (userMessages.Length == 0)
            return string.Empty;

        var latest = userMessages[^1];
        if (userMessages.Length == 1)
            return CompactRetainedKnowledgeQuery(latest, ResolveRetainedKnowledgeQueryBudget(latest.Length));

        var prior = userMessages[^2];
        var combined = $"Current request:\n{latest}\n\nRelevant prior Founder context:\n{prior}";
        return CompactRetainedKnowledgeQuery(
            combined,
            ResolveRetainedKnowledgeQueryBudget(combined.Length));
    }

    private static string CompactRetainedKnowledgeQuery(
        string query,
        int maximumCharacters)
    {
        if (query.Length <= maximumCharacters)
            return query;

        const string marker =
            "\n...[retained-knowledge query compacted]...\n";

        var available =
            Math.Max(2, maximumCharacters - marker.Length);

        var tailLength =
            available / 2;

        var headLength =
            available -
            tailLength;

        return
            query[..headLength] +
            marker +
            query[^tailLength..];
    }

    private static string BuildRetainedKnowledgeContext(
        LegendConnectRetainedKnowledgeSearchSnapshot snapshot,
        int maximumCharacters)
    {
        if (snapshot.Items.Count == 0)
            return string.Empty;

        var json =
            JsonSerializer.Serialize(
                snapshot,
                JsonOptions);

        if (json.Length > maximumCharacters)
        {
            json =
                json[..maximumCharacters] +
                "\n[LEGEND RETAINED CONTEXT COMPACTED FOR CURRENT PROVIDER WINDOW]";
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
            IReadOnlyList<LegendFounderAiChatMessage> conversation,
            int maximumCharacters)
    {
        var selected =
            new List<LegendFounderAiChatMessage>(
                conversation.Count);

        var remaining = maximumCharacters;

        for (var index =
                 conversation.Count - 1;
             index >= 0;
             index--)
        {
            var message =
                conversation[index];

            var content =
                message.Content ??
                string.Empty;

            if (index == conversation.Count - 1 &&
                content.Length > maximumCharacters)
            {
                selected.Add(
                    new LegendFounderAiChatMessage(
                        message.Role,
                        CompactOversizedLatestMessage(
                            content,
                            maximumCharacters)));

                remaining = 0;
                break;
            }

            if (content.Length <= remaining)
            {
                selected.Add(message);
                remaining -= content.Length;
            }

            if (remaining == 0)
                break;
        }

        selected.Reverse();
        return selected;
    }

    private static string CompactOversizedLatestMessage(
        string content,
        int maximumCharacters)
    {
        if (content.Length <= maximumCharacters)
            return content;

        const string marker =
            "\n\n[EARLIER PORTION OF THIS FOUNDER MESSAGE OMITTED " +
            "FROM THE CURRENT PROVIDER WINDOW; THE ORIGINAL REQUEST " +
            "WAS ACCEPTED IN FULL BY Legend® Ai.]\n\n";

        var available =
            Math.Max(2, maximumCharacters - marker.Length);

        var tailLength =
            Math.Min(
                MinimumLatestMessageTailCharacters,
                available / 2);

        var headLength =
            available -
            tailLength;

        return
            content[..headLength] +
            marker +
            content[^tailLength..];
    }

    private static int ResolveProviderConversationBudget(
        IReadOnlyList<LegendFounderAiChatMessage> conversation)
    {
        var totalCharacters = conversation.Sum(message => message.Content?.Length ?? 0);
        if (totalCharacters <= MinimumProviderConversationCharacters)
            return MinimumProviderConversationCharacters;

        var target = totalCharacters <= 120_000
            ? 120_000
            : MaximumProviderConversationCharacters;

        return Math.Min(totalCharacters, target);
    }

    private static TimeSpan ResolveProviderBudget(
        IReadOnlyList<LegendFounderAiChatMessage> conversation,
        bool requiresGovernedInspection,
        bool allowTools,
        TimeSpan remaining)
    {
        // Provider work is bounded by the request-scoped budget, not a
        // separate short "casual" timeout. Native LEGEND inference has
        // already had first refusal, so an escalation may use the same safe
        // provider window regardless of the request's wording.
        var providerReserveSeconds =
            requiresGovernedInspection && allowTools
                ? MinimumFinalizationReserveSeconds
                : 2;
        return TimeSpan.FromSeconds(
            Math.Min(
                MaximumProviderRoundSeconds,
                Math.Max(
                    5,
                    remaining.TotalSeconds - providerReserveSeconds)));
    }

    private static int ResolveMaxOutputTokens(
        IReadOnlyList<LegendFounderAiChatMessage> conversation,
        bool requiresGovernedInspection,
        int configuredMaximum)
    {
        if (requiresGovernedInspection)
            return configuredMaximum;

        var latest = conversation
            .Last(message => string.Equals(message.Role, "user", StringComparison.Ordinal))
            .Content?.Length ?? 0;

        var adaptiveTokens = MinimumCasualOutputTokens + latest / 4;
        return Math.Clamp(
            adaptiveTokens,
            MinimumCasualOutputTokens,
            Math.Min(MaximumCasualOutputTokens, configuredMaximum));
    }

    private static int ResolveRetainedKnowledgeQueryBudget(int queryLength) =>
        queryLength switch
        {
            <= 2_000 => 2_000,
            <= 8_000 => 8_000,
            _ => 16_000
        };

    private static int ResolveRetainedContextBudget(
        IReadOnlyList<LegendFounderAiChatMessage> conversation)
    {
        var conversationCharacters = conversation.Sum(message => message.Content?.Length ?? 0);
        return Math.Clamp(
            conversationCharacters / 2,
            MinimumRetainedContextCharacters,
            MaximumRetainedContextCharacters);
    }

    private static int ResolveRetainedKnowledgeLookupSeconds(
        IReadOnlyList<LegendFounderAiChatMessage> conversation)
    {
        var totalCharacters = conversation.Sum(message => message.Content?.Length ?? 0);
        var dynamicSeconds = MinimumRetainedKnowledgeLookupSeconds + totalCharacters / 25_000;
        return Math.Clamp(
            dynamicSeconds,
            MinimumRetainedKnowledgeLookupSeconds,
            MaximumRetainedKnowledgeLookupSeconds);
    }

    private static int ResolveRetainedKnowledgeTake(string query)
    {
        var words = query.Split(
            [' ', '\r', '\n', '\t', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        return Math.Clamp(12 + words / 25, 12, 32);
    }

    private static int ResolveMaximumToolRounds(
        IReadOnlyList<LegendFounderAiChatMessage> conversation)
    {
        var totalCharacters = conversation.Sum(message => message.Content?.Length ?? 0);
        var userTurns = conversation.Count(message => string.Equals(message.Role, "user", StringComparison.Ordinal));
        var dynamicRounds = MinimumToolRounds + totalCharacters / 40_000 + userTurns / 4;
        return Math.Clamp(dynamicRounds, MinimumToolRounds, MaximumToolRounds);
    }

    private static TimeSpan ResolveReadOnlyToolBudget(TimeSpan remaining)
    {
        var seconds = Math.Clamp(
            remaining.TotalSeconds / 4,
            MinimumReadOnlyToolSeconds,
            MaximumReadOnlyToolSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static int ResolveToolOutputBudget(
        IReadOnlyList<LegendFounderAiChatMessage> providerConversation,
        int currentInputCount)
    {
        var conversationCharacters = providerConversation.Sum(message => message.Content?.Length ?? 0);
        var pressure = conversationCharacters + currentInputCount * 2_000;
        var target = MaximumToolOutputCharacters - pressure / 4;
        return Math.Clamp(target, MinimumToolOutputCharacters, MaximumToolOutputCharacters);
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

    private static bool RequiresGovernedInspection(
        IReadOnlyList<LegendFounderAiChatMessage> conversation,
        string mode)
    {
        if (string.Equals(mode, "teacher", StringComparison.Ordinal))
            return true;

        var latest = conversation
            .Last(message => string.Equals(message.Role, "user", StringComparison.Ordinal))
            .Content?.Trim() ?? string.Empty;

        if (latest.Length == 0)
            return false;

        var text = latest.ToLowerInvariant();

        var governedSignals = new[]
        {
            "canonical", "retained knowledge", "retained",
            "curriculum", "train ", "training", "teacher", "translation",
            "haitian creole", "language", "alignment", "provenance",
            "evidence", "model readiness", "readiness", "provider",
            "azure", "system state", "system status", "metrics", "metric",
            "knowledge", "learning", "corpus"
        };

        return governedSignals.Any(signal =>
            text.Contains(signal, StringComparison.Ordinal));
    }

    private static string ResolveReasoningEffortForRound(
        int round,
        bool requiresGovernedInspection,
        string configuredEffort) =>
        !requiresGovernedInspection
            ? "none"
            : round == 0
                ? "low"
                : configuredEffort;

    private static string NormalizeReasoningEffort(
        string? value)
    {
        var normalized =
            value?.Trim().ToLowerInvariant();

        return normalized is
            "none" or
            "low" or
            "medium" or
            "high" or
            "xhigh"
                ? normalized
                : "medium";
    }

    private static string NormalizeServiceTier(
        string? value)
    {
        var normalized =
            value?.Trim().ToLowerInvariant();

        // "fast" was a legacy local value. Responses accepts auto/default;
        // normalize any legacy or invalid setting without changing the global
        // OpenAI runtime configuration.
        return normalized is "auto" or "default"
            ? normalized
            : "auto";
    }

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

    private static string SerializeUnbounded(object? value) =>
        JsonSerializer.Serialize(
            value,
            JsonOptions);

    private static string BoundSerializedOutput(
        string value,
        int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
            return value;

        return value[..maximumCharacters] +
               "\n[LEGEND TOOL OUTPUT COMPACTED FOR CURRENT PROVIDER WINDOW]";
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

public sealed record LegendFounderAiProgressEvent(
    string Stage,
    string Message,
    int? Round = null,
    string? Tool = null);

public sealed record LegendFounderAiChatMessage(
    string? Role,
    string? Content);

public sealed class LegendFounderAiChatRequest
{
    public string? Mode { get; init; }

    /// <summary>
    /// Client-generated UUID that scopes durable governed discourse state to
    /// one Founder conversation. It is never used as a knowledge key or as a
    /// response cache; malformed or absent values retain the existing
    /// request-scoped behavior.
    /// </summary>
    public string? ConversationId { get; init; }

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
