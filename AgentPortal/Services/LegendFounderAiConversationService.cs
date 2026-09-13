using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentPortal.Services.Analytics;
using Domain.Messaging;
using Infrastructure.Messaging;
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
    private const int MaximumConversationMessages = 60;
    private const int MaximumMessageCharacters = 1_000_000;
    private const int MaximumConversationCharacters = 2_000_000;
    private const int MinimumProviderConversationCharacters = 60_000;
    private const int MaximumProviderConversationCharacters = 600_000;
    private const int MinimumLatestMessageTailCharacters = 24_000;
    private const int MinimumToolRounds = 6;
    private const int MaximumToolRounds = 16;
    private const int MinimumFinalizationReserveSeconds = 45;
    private const int MinimumFinalSynthesisWindowSeconds = 60;
    private const int MaximumProviderRoundSeconds = 75;
    private const int MinimumCasualOutputTokens = 256;
    private const int MaximumCasualOutputTokens = 4_000;
    private const int MinimumRetainedKnowledgeLookupSeconds = 4;
    private const int MaximumRetainedKnowledgeLookupSeconds = 12;
    private const int MinimumReadOnlyToolSeconds = 12;
    // Read-only research, repository inspection and bounded operational
    // projections may legitimately cross one provider-round window.  Keep a
    // hard request-scoped ceiling while leaving the configured 900-second
    // conversation budget enough time for final synthesis.
    private const int MaximumReadOnlyToolSeconds = 90;
    private const int MinimumToolOutputCharacters = 40_000;
    private const int MaximumToolOutputCharacters = 160_000;
    private const int MinimumRetainedContextCharacters = 32_000;
    private const int MaximumRetainedContextCharacters = 128_000;
    private const int MinimumProviderAttemptWindowSeconds = 3;
    private const int MaximumProviderCooldownSeconds = 300;
    private const int MaximumTransientProviderAttempts = 3;
    private const int MaximumDiscourseObservationSeconds = 2;
    private const int ProviderToolCatalogAcceptanceSeconds = 30;
    private const int ProviderToolCatalogAcceptanceOutputTokens = 64;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly FounderLegendConnectService _legend;
    private readonly LegendFounderAiDiscourseStateService _discourse;
    private readonly ILegendLanguageRegistry _languages;
    private readonly ITranslationService _translation;
    private readonly LegendFounderToolAuthority _toolAuthority;
    private readonly ILogger<LegendFounderAiConversationService> _logger;
    private readonly int _timeoutSeconds;
    private readonly int _maxOutputTokens;
    private readonly string _reasoningEffort;
    private readonly string _serviceTier;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

    public LegendFounderAiConversationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        FounderLegendConnectService legend,
        ILogger<LegendFounderAiConversationService> logger,
        LegendFounderAiDiscourseStateService discourse,
        ILegendLanguageRegistry languages,
        ITranslationService translation,
        IFounderSoftwareRemediationService? softwareRemediation = null,
        AgencyCommandService? agencyCommand = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _legend = legend;
        _discourse = discourse ?? throw new ArgumentNullException(nameof(discourse));
        _languages = languages ?? throw new ArgumentNullException(nameof(languages));
        _translation = translation ?? throw new ArgumentNullException(nameof(translation));
        _toolAuthority =
            new LegendFounderToolAuthority(
                legend,
                softwareRemediation,
                agencyCommand);
        _logger = logger;

        _timeoutSeconds =
            Math.Clamp(
                configuration.GetValue<int?>(
                    "OpenAI:LegendFounderAiTimeoutSeconds") ??
                    900,
                120,
                1_800);

        _maxOutputTokens =
            Math.Clamp(
                configuration.GetValue<int?>(
                    "OpenAI:LegendFounderAiMaxOutputTokens") ??
                    32_000,
                2_000,
                64_000);

        _reasoningEffort =
            NormalizeReasoningEffort(
                configuration[
                    "OpenAI:LegendFounderAiReasoningEffort"]);

        _serviceTier =
            NormalizeServiceTier(
                configuration[
                    "OpenAI:LegendFounderAiServiceTier"]);
    }

    internal async Task<string> VerifyProviderToolCatalogAcceptanceAsync(
        CancellationToken cancellationToken = default)
    {
        // This is the existing Responses executor with its normal complete
        // registry. store=false and catalogAcceptanceOnly keep every tool
        // visible to schema validation while making execution impossible.
        var apiKey = OpenAiKeyResolver.Resolve(_configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "The bounded Founder tool-catalog provider canary requires an OpenAI API key.");
        }

        using var canaryBudget =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        canaryBudget.CancelAfter(
            TimeSpan.FromSeconds(
                ProviderToolCatalogAcceptanceSeconds));

        using var response =
            await SendResponseAsync(
                apiKey,
                ResolveProviderModel(),
                "This is a zero-write provider contract canary. Return exactly PROVIDER_CATALOG_ACCEPTED. Do not call a tool.",
                [
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] =
                            "Acknowledge this schema-acceptance request without calling any tool."
                    }
                ],
                _toolAuthority.Tools,
                allowTools: true,
                requireToolCall: false,
                providerBudget: TimeSpan.FromSeconds(
                    ProviderToolCatalogAcceptanceSeconds),
                reasoningEffort: "low",
                maxOutputTokens: ProviderToolCatalogAcceptanceOutputTokens,
                cancellationToken: canaryBudget.Token,
                catalogAcceptanceOnly: true);

        if (response is null ||
            !response.RootElement.TryGetProperty("id", out var responseId) ||
            responseId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(responseId.GetString()))
        {
            throw new InvalidOperationException(
                "The provider accepted no verifiable response for the complete Founder tool catalog.");
        }

        return responseId.GetString()!;
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
        using var requestActivity = Activity.Current is null
            ? new Activity("LegendFounderAi.Reply").SetIdFormat(ActivityIdFormat.W3C).Start()
            : null;

        if (!TryNormalizeMode(
                request.Mode,
                out var mode,
                out var modeValidationError))
        {
            return LegendFounderAiChatResponse.InvalidMode(
                modeValidationError);
        }

        if (!TryNormalizeMessages(
                request.Messages,
                out var conversation,
                out var validationError))
        {
            return LegendFounderAiChatResponse.ModeFailure(
                mode,
                validationError,
                "validation",
                "message_validation",
                "invalid_messages");
        }

        // One immutable external-provider decision for this request. It is
        // established once, before any authority runs, and is then carried
        // explicitly into every boundary that could reach an external
        // provider. Nothing downstream may widen it.
        var providerPolicy = request.NativeOnly
            ? LegendConnectExternalProviderPolicy.NativeOnly
            : LegendConnectExternalProviderPolicy.ProviderEnabled;

        if (request.NativeOnly && IsTeacherMode(mode))
        {
            return LegendFounderAiChatResponse.ModeFailure(
                mode,
                "Native-only testing is available only in Legend® Ai mode. OpenAI Teacher was not contacted.",
                "validation",
                "native_only_validation",
                "native_only_requires_legend_mode");
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
        using var stageScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["LegendRequestTraceId"] = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            ["LegendMode"] = mode,
            ["LegendNativeOnly"] = request.NativeOnly
        });

        LegendConnectNativeInferenceSnapshot? nativeInference = null;
        LegendConnectDiscourseStateSnapshot? currentDiscourseState = null;
        string? nativeFailureDetail = null;
        string? governedSourceLanguageCode = null;
        var sourceLanguageTemporarilyUnavailable = false;

        {
            // Language identification can contact the existing governed
            // translation router. Preserve the Founder boundary before that
            // provider-backed read and before any meaning-graph analysis.
            await TraceNativeStageAsync("founder_authorization", "FounderLegendConnectService.EnsureFounderAuthorizedAsync", async () =>
            {
                await _legend.EnsureFounderAuthorizedAsync(founder, effectiveToken);
                return true;
            });

            var sourceLanguage = await TraceNativeStageAsync("source_language", "LegendFounderAiConversationService.ResolveSourceLanguageAsync", () => ResolveSourceLanguageAsync(
                request.SourceLanguageCode,
                conversation[^1].Content ?? string.Empty,
                effectiveToken,
                providerPolicy));
            _logger.LogInformation(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ProviderPolicy={ProviderPolicy}",
                "SourceLanguageResolved", "LegendFounderAiConversationService.ResolveSourceLanguageAsync", "source_language", sourceLanguage.Outcome.ToString(),
                LegendConnectTelemetry.NormalizeDiagnosticReason(sourceLanguage.Reason), providerPolicy.ForbidsExternalProviders ? "native_only" : "provider_enabled");
            if (!sourceLanguage.Succeeded)
            {
                if (providerPolicy.ForbidsExternalProviders ||
                    !sourceLanguage.IsTransientIdentificationOutage)
                {
                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        $"Legend® Ai could not identify a governed source language. SourceLanguageFailure={sourceLanguage.Reason}.",
                        "language_identification",
                        "source_language_identification",
                        sourceLanguage.Reason);
                }

                // The language authority did not settle meaning or receipt
                // scope. A transient outage may use the already-enabled
                // external responder, with no invented language/native proof.
                sourceLanguageTemporarilyUnavailable = true;
                nativeFailureDetail =
                    $"Governed source-language identification was temporarily unavailable. SourceLanguageFailure={sourceLanguage.Reason}. " +
                    "Native meaning and owned-record receipt scope were not established. Any provider response remains external, unresolved evidence.";
            }
            else
            {
                governedSourceLanguageCode = sourceLanguage.LanguageCode!;
            }
        }

        if (governedSourceLanguageCode is not null)
        {
            currentDiscourseState = await ObserveDiscourseMeaningAsync(
                founder,
                request.ConversationId,
                "user",
                conversation[^1].Content ?? string.Empty,
                effectiveToken,
                cancellationToken,
                governedSourceLanguageCode);
        }

        if (ShouldAttemptNativeInference(mode) && governedSourceLanguageCode is not null)
        {
            var sourceLanguageCode = governedSourceLanguageCode;
            var nativeStarted = Stopwatch.GetTimestamp();
            await ReportProgressAsync(
                progress,
                new LegendFounderAiProgressEvent(
                    "native_inference",
                    "Checking governed LEGEND knowledge before external escalation."),
                effectiveToken);

            try
            {
                var context = conversation
                    .Take(conversation.Count - 1)
                    .Select(message => new LegendConnectConversationContextItem(
                        message.Role ?? string.Empty,
                        message.Content ?? string.Empty))
                    .ToArray();
                nativeInference = await TraceNativeStageAsync("native_semantic_inference", "FounderLegendConnectService.TryInferConversationWithDiscourseAsync", () => _legend.TryInferConversationWithDiscourseAsync(
                    founder,
                    conversation[^1].Content ?? string.Empty,
                    context,
                    currentDiscourseState,
                    sourceLanguageCode,
                    effectiveToken,
                    providerPolicy));
                if (nativeInference.ReadOnlyContentRequest is { } readRequest)
                {
                    var binding = await TraceNativeStageAsync("native_read_binding", "LegendFounderToolAuthority.BindReadOnlyResultAsync", () => _toolAuthority.BindReadOnlyResultAsync(
                        founder,
                        readRequest,
                        effectiveToken,
                        providerPolicy));
                    if (!binding.Succeeded || binding.Receipt is null)
                    {
                        nativeInference = new LegendConnectNativeInferenceSnapshot(
                            false,
                            0m,
                            null,
                            binding.ReasonCode,
                            nativeInference.EvidenceCount,
                            "The selected governed result frame required a Founder-authorized read-only value, but the existing Founder tool authority did not return an admissible zero-write receipt.",
                            false,
                            "Unavailable",
                            "Unavailable");
                    }
                    else
                    {
                        nativeInference = await TraceNativeStageAsync("native_read_realization", "FounderLegendConnectService.TryInferConversationWithReadOnlyContentAsync", () => _legend
                            .TryInferConversationWithReadOnlyContentAsync(
                                founder,
                                conversation[^1].Content ?? string.Empty,
                                context,
                                currentDiscourseState,
                                sourceLanguageCode,
                                binding.Receipt,
                                effectiveToken,
                                providerPolicy));
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AgentPortal.Security.ForbidResultException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Native inference is strictly fail-closed. A read failure
                // cannot manufacture an answer, and any fail-closed boundary
                // already returned by the native authority remains in force.
                nativeFailureDetail = "The native authority failed before producing a verified result. Reason=native_inference_unavailable.";
                _logger.LogWarning(
                    "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ExceptionType={ExceptionType}",
                    "NativeInferenceException", "FounderLegendConnectService.TryInferConversationWithDiscourseAsync", "native_inference", "failed", exception.GetType().Name);
            }
            finally
            {
                _logger.LogInformation(
                    "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ElapsedMs={ElapsedMs} Supported={Supported} RequiresEscalation={RequiresEscalation} EvidenceCount={EvidenceCount}",
                    "NativeInferenceCompleted", "FounderLegendConnectService.TryInferConversationWithDiscourseAsync", "native_inference",
                    nativeInference is null ? "unavailable" : nativeInference.Supported ? "supported" : "unsupported",
                    LegendConnectTelemetry.NormalizeDiagnosticReason(nativeInference?.ReasonCode),
                    (long)Math.Ceiling(Stopwatch.GetElapsedTime(nativeStarted).TotalMilliseconds),
                    nativeInference?.Supported ?? false, nativeInference?.RequiresEscalation ?? false, nativeInference?.EvidenceCount ?? 0);
            }

            var observedResearchDecision = nativeInference?.ResearchDecision;
            _logger.LogInformation(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ResearchRequired={ResearchRequired} ProviderPolicy={ProviderPolicy}",
                "ResearchDecision", "LegendConnectOperations.DecideResearchNeeded", "research_decision",
                observedResearchDecision is null ? "unavailable" : !observedResearchDecision.ResearchRequired ? "not_required" : request.NativeOnly ? "blocked" : "allowed",
                LegendConnectTelemetry.NormalizeDiagnosticReason(observedResearchDecision?.ReasonCode ?? "research_decision_unavailable"),
                observedResearchDecision?.ResearchRequired ?? false, providerPolicy.ForbidsExternalProviders ? "native_only" : "provider_enabled");
            if (nativeInference?.ResearchDecision is
                {
                    ResearchRequired: true
                } researchDecision)
            {
                if (request.NativeOnly)
                {
                    return new LegendFounderAiChatResponse(
                        false,
                        mode,
                        "LEGEND identified that this request requires external research, but native-only isolation blocked every internet operation. " +
                        $"ResearchReason={researchDecision.ReasonCode}; EvidenceOrigin=UnresolvedEvidence.",
                        null,
                        ResponseAuthority: "SystemDiagnostic",
                        Stage: "native_only_research_blocked",
                        Reason: researchDecision.ReasonCode,
                        EvidenceOrigin: LegendConnectResearchEvidenceOrigin.UnresolvedEvidence);
                }

                var remainingResearchBudget =
                    TimeSpan.FromSeconds(_timeoutSeconds) -
                    executionClock.Elapsed;
                using var researchBudget =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        effectiveToken);
                researchBudget.CancelAfter(
                    ResolveReadOnlyToolBudget(
                        remainingResearchBudget));
                await ReportProgressAsync(
                    progress,
                    new LegendFounderAiProgressEvent(
                        "research",
                        "LEGEND identified a governed external-research requirement and is collecting bounded, cited, zero-write evidence."),
                    effectiveToken);
                LegendConnectResearchOutcome researchOutcome;
                try
                {
                    researchOutcome = await TraceNativeStageAsync("research", "LegendFounderToolAuthority.ResearchAsync", () => _toolAuthority.ResearchAsync(
                        founder,
                        conversation[^1].Content ?? string.Empty,
                        governedSourceLanguageCode!,
                        nativeInference,
                        request.FounderCommandConfirmed
                            ? new FounderAiMutationAuthorization(
                                Guid.NewGuid().ToString("N"))
                            : null,
                        researchBudget.Token,
                        providerPolicy));
                }
                catch (OperationCanceledException)
                    when (!effectiveToken.IsCancellationRequested)
                {
                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        "LEGEND could not complete the bounded external research before its read-only tool window ended. EvidenceOrigin=UnresolvedEvidence.",
                        "timeout",
                        "research",
                        "research_budget_exhausted");
                }
                _logger.LogInformation(
                    "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode}",
                    "ResearchCompleted", "LegendFounderToolAuthority.ResearchAsync", "research", researchOutcome.State.ToString(),
                    LegendConnectTelemetry.NormalizeDiagnosticReason(researchOutcome.Failure?.ReasonCode ?? researchOutcome.InsufficientEvidence?.ReasonCode ??
                        researchOutcome.UnresolvedConflict?.ReasonCode ?? researchOutcome.Decision.ReasonCode));
                return ResearchChatResponse(
                    mode,
                    researchOutcome,
                    nativeInference.ModelAssistance);
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
                    cancellationToken,
                    sourceLanguageCode);
                await ReportProgressAsync(
                    progress,
                    new LegendFounderAiProgressEvent(
                        "native_response",
                        $"Answered from {nativeInference.EvidenceCount} governed LEGEND evidence record(s). " +
                        $"EvidenceStandard={nativeInference.EvidenceStandard}; " +
                        $"ArticulationMode={nativeInference.ArticulationMode}; " +
                        $"ModelAssistance={nativeInference.ModelAssistance?.State ?? "Unavailable"}; " +
                        $"ModelAssistanceReason={nativeInference.ModelAssistance?.ReasonCode ?? "model_assistance_receipt_unavailable"}."),
                    effectiveToken);

                _logger.LogInformation(
                    "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} EvidenceCount={EvidenceCount}",
                    "NativeAnswer", "LegendFounderAiConversationService.ReplyAsync", "native_response", "supported", LegendConnectTelemetry.NormalizeDiagnosticReason(nativeInference.ReasonCode), nativeInference.EvidenceCount);
                return new LegendFounderAiChatResponse(
                    true,
                    mode,
                    nativeInference.Answer,
                    null,
                    ResponseAuthority: "LegendAi",
                    Stage: "native_response",
                    ModelAssistanceState: nativeInference.ModelAssistance?.State,
                    ModelAssistanceReason: nativeInference.ModelAssistance?.ReasonCode,
                    ModelVersion: nativeInference.ModelAssistance?.ModelVersion,
                    ModelTrainingRunId: nativeInference.ModelAssistance?.ModelTrainingRunId,
                    ModelProvenance: nativeInference.ModelAssistance?.Provenance,
                    EvidenceOrigin: LegendConnectResearchEvidenceOrigin.InternalKnowledge,
                    ScheduleCertificates: nativeInference.ScheduleCertificates,
                    ReasoningTransitionPath: nativeInference.ReasoningTransitionPath);
            }
        }

        // Founder native-only testing is an absolute provider boundary. Once
        // governed native inference declines or fails, return its real state
        // before resolving an OpenAI key, constructing provider instructions,
        // loading provider tools, or issuing any external request.
        if (request.NativeOnly)
        {
            _logger.LogInformation(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ProviderPolicy={ProviderPolicy}",
                "ProviderEscalation", "LegendFounderAiConversationService.ReplyAsync", "provider_escalation", "blocked", "external_provider_forbidden_by_native_only_policy", "native_only");
            await ReportProgressAsync(
                progress,
                new LegendFounderAiProgressEvent(
                    "native_only_blocked",
                    "Native-only test stopped after governed LEGEND could not produce an answer. OpenAI escalation was blocked."),
                effectiveToken);

            var reason = string.IsNullOrWhiteSpace(nativeInference?.ReasonCode)
                ? nativeInference is null
                    ? "native_inference_unavailable"
                    : "native_inference_unsupported"
                : LegendConnectTelemetry.NormalizeDiagnosticReason(nativeInference.ReasonCode);
            var detail = !string.IsNullOrWhiteSpace(nativeFailureDetail)
                ? NormalizeFailureDetail(nativeFailureDetail)
                : !string.IsNullOrWhiteSpace(nativeInference?.AuthoritySummary)
                    ? nativeInference.AuthoritySummary.Trim()
                    : "The native authority returned no additional detail.";

            var diagnostic = $"LEGEND could not complete this native-only response. " +
                $"NativeFailure={reason}; NativeDetail={detail}; " +
                $"EvidenceCount={nativeInference?.EvidenceCount ?? 0}; " +
                "OpenAIEscalation=blocked.";
            return new LegendFounderAiChatResponse(
                false, mode, diagnostic, diagnostic,
                FailureKind: "native_inference",
                ResponseAuthority: "SystemDiagnostic",
                Stage: "native_only_blocked",
                Reason: reason,
                ModelAssistanceState: nativeInference?.ModelAssistance?.State,
                ModelAssistanceReason: nativeInference?.ModelAssistance?.ReasonCode,
                ModelVersion: nativeInference?.ModelAssistance?.ModelVersion,
                ModelTrainingRunId: nativeInference?.ModelAssistance?.ModelTrainingRunId,
                ModelProvenance: nativeInference?.ModelAssistance?.Provenance);
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
            _logger.LogInformation(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ProviderPolicy={ProviderPolicy}",
                "ProviderEscalation", "LegendFounderAiConversationService.ReplyAsync", "provider_escalation", "blocked",
                LegendConnectTelemetry.NormalizeDiagnosticReason(nativeInference.ReasonCode), "provider_enabled");
            return NativeInferenceUnavailableResponse(
                mode,
                nativeInference,
                nativeFailureDetail);
        }

        var apiKey = OpenAiKeyResolver.Resolve(_configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogInformation(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ProviderPolicy={ProviderPolicy}",
                "ProviderEscalation", "OpenAiKeyResolver.Resolve", "provider_escalation", "blocked", "provider_api_key_unavailable", "provider_enabled");
            return NativeInferenceUnavailableResponse(
                mode,
                nativeInference,
                nativeFailureDetail,
                "provider_api_key_unavailable",
                "The external reasoning provider is not configured for this deployment.");
        }
        _logger.LogInformation(
            "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ProviderPolicy={ProviderPolicy}",
            "ProviderEscalation", "LegendFounderAiConversationService.ReplyAsync", "provider_escalation", "allowed", "provider_enabled_escalation", "provider_enabled");
        var model = ResolveProviderModel();

        // One typed classification for both modes. Legend mode reuses the
        // classification the native meaning-graph analysis already produced;
        // Teacher mode, which never attempts a native answer, obtains it from
        // the same Founder-gated read-only analysis authority. Neither mode
        // falls back to surface text, and no analysis is duplicated.
        var ownedRecordResolution = nativeInference?.OwnedRecordIntent is { } nativeOwnedRecordIntent
            ? new FounderAiOwnedRecordResolution(nativeOwnedRecordIntent, nativeInference.ReadOnlyContentRequest)
            : sourceLanguageTemporarilyUnavailable
                ? new FounderAiOwnedRecordResolution(
                    LegendConnectOwnedRecordRequest.AnalysisUnavailable("source_language_identification_unavailable"), null)
                : await ClassifyOwnedRecordIntentAsync(
                    founder,
                    conversation,
                    currentDiscourseState,
                    governedSourceLanguageCode!,
                    effectiveToken);
        var ownedRecordIntent = ownedRecordResolution.Classification;

        // Unavailable semantic analysis cannot establish native proof or a
        // governed read scope. Actual analysis failures stop the request. Only
        // the typed transient language outage retains the existing external
        // responder permission, with unresolved evidence and no native proof.
        if (ownedRecordIntent?.IsAnalysisUnavailable == true &&
            !sourceLanguageTemporarilyUnavailable)
        {
            return LegendFounderAiChatResponse.ModeFailure(
                mode,
                FailureMessageForMode(
                    mode,
                    "The governed meaning-graph analysis that determines whether this " +
                    "request requires an authenticated LEGEND read was unavailable, " +
                    "so no answer was accepted."),
                "governed_analysis",
                "governed_request_classification",
                ownedRecordIntent.Diagnostic ??
                    "governed_meaning_graph_analysis_unavailable");
        }

        var requiresMandatoryGovernedInspection =
            RequiresGovernedInspection(
                conversation,
                mode,
                ownedRecordIntent);

        var requiresGovernedInspection =
            RequiresProviderGovernedInspection(
                conversation,
                mode,
                ownedRecordIntent,
                nativeInference,
                nativeFailureDetail);

        // Owned-record intent alone proves that a current read is needed;
        // it does not identify which record or operation can satisfy it.
        // Only the native authority's existing governed result frame supplies
        // that scope. A provider-selected unrelated read is not a substitute.
        var requiredReadScope = requiresMandatoryGovernedInspection
            ? ownedRecordResolution.ReadOnlyContentRequest
            : null;
        if (requiresMandatoryGovernedInspection && requiredReadScope is null)
        {
            return LegendFounderAiChatResponse.ModeFailure(
                mode,
                FailureMessageForMode(
                    mode,
                    "The governed request requires a current owned-record read, " +
                    "but no governed result frame established its exact tool and record scope."),
                "governed_inspection",
                "governed_request_classification",
                "owned_record_read_scope_unproven");
        }

        LegendConnectRetainedKnowledgeSearchSnapshot? retainedKnowledge = null;

        // OpenAI Teacher is a direct Founder-to-provider mode.  It may ask
        // for governed LEGEND inspection through the existing function-tool
        // registry. Only source-language and read-only meaning classification
        // precede its response; native answer generation remains bypassed.
        var preloadRetainedKnowledge =
            requiresGovernedInspection &&
            !IsTeacherMode(mode) &&
            !sourceLanguageTemporarilyUnavailable;

        if (preloadRetainedKnowledge)
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

        var nativeDiagnosticContext =
            BuildNativeDiagnosticTeachingContext(
                nativeInference,
                nativeFailureDetail);

        var instructions =
            requiresGovernedInspection
                ? BuildInstructions(mode) +
                  nativeDiagnosticContext +
                  (retainedKnowledge is null
                      ? string.Empty
                      : BuildRetainedKnowledgeContext(
                          retainedKnowledge,
                          ResolveRetainedContextBudget(conversation)))
                : BuildCasualInstructions(mode);
        if (requiredReadScope is not null)
        {
            instructions += "\nGOVERNED_READ_REQUIREMENT:\n" +
                JsonSerializer.Serialize(requiredReadScope, JsonOptions) +
                "\nUse the specified tool and arguments. An unrelated read cannot satisfy this requirement.";
        }

        var tools = _toolAuthority.Tools;

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
                    : 3;

            var failedGovernedReads =
                new Dictionary<string, FounderAiReadDiagnostic>(StringComparer.Ordinal);
            var successfulGovernedReads = new HashSet<string>(StringComparer.Ordinal);

            // Retained-knowledge preload is a passive context read performed by
            // this service, not an executed governed inspection. It can never
            // satisfy a request whose answer depends on current governed state,
            // and it must not withdraw the governed tool catalog from the
            // escalated round.
            var governedInspectionCompleted =
                !requiresGovernedInspection;

            var confirmedLearningMutationRequired =
                request.FounderCommandConfirmed &&
                IsTeacherMode(mode) &&
                RequestsFounderLearningMutation(conversation);

            var mutationAuthorization =
                request.FounderCommandConfirmed
                    ? new FounderAiMutationAuthorization(
                        Guid.NewGuid().ToString("N"))
                    : null;

            var learningMutationCompleted = false;
            string? learningMutationReceipt = null;

            var accumulatedProviderAnswer = string.Empty;
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

                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        FailureMessageForMode(
                            mode,
                            "The current request window ended before another provider round could safely begin. Ask it to continue from the current point."),
                        "timeout",
                        "time_budget",
                        "request_budget_exhausted");
                }

                // Optional tools remain discoverable even when no owned-record
                // intent was admitted. The same registry, authorization, and
                // request budget govern every provider-selected operation.
                var allowTools =
                    round < maximumToolRounds - 1 &&
                    remaining >
                        TimeSpan.FromSeconds(
                            MinimumFinalSynthesisWindowSeconds);

                if (requiresMandatoryGovernedInspection &&
                    !governedInspectionCompleted &&
                    !allowTools)
                {
                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        FailureMessageForMode(
                            mode,
                            "The remaining request window is too small to begin the required governed LEGEND inspection safely."),
                        "governed_inspection",
                        "governed_tool",
                        "required_governed_inspection_budget_unavailable");
                }

                var requireToolCall =
                    allowTools &&
                    (
                        (requiresMandatoryGovernedInspection &&
                         !governedInspectionCompleted) ||
                        (confirmedLearningMutationRequired &&
                         governedInspectionCompleted &&
                         !learningMutationCompleted)
                    );

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

                var providerStarted = Stopwatch.GetTimestamp();
                using var responseDocument =
                    await TraceNativeStageAsync("external_response", "LegendFounderAiConversationService.SendResponseAsync", () => SendResponseAsync(
                        apiKey,
                        model,
                        instructions,
                        input,
                        tools,
                        allowTools,
                        requireToolCall,
                        providerBudget,
                        ResolveReasoningEffortForRound(
                            round,
                            requiresGovernedInspection,
                            _reasoningEffort),
                        ResolveMaxOutputTokens(
                            conversation,
                            requiresGovernedInspection,
                            _maxOutputTokens),
                        effectiveToken));

                _logger.LogInformation(
                    "LEGEND Founder AI stage completed. Mode={Mode} Stage=provider_round Round={Round} AllowTools={AllowTools} BudgetMs={BudgetMs} ElapsedMs={ElapsedMs}",
                    mode,
                    round + 1,
                    allowTools,
                    (long)Math.Ceiling(providerBudget.TotalMilliseconds),
                    (long)Math.Ceiling(
                        Stopwatch.GetElapsedTime(providerStarted).TotalMilliseconds));

                if (responseDocument is null)
                {
                    return NativeInferenceUnavailableResponse(
                        mode,
                        nativeInference,
                        nativeFailureDetail,
                        "provider_no_response",
                        "The provider request completed without a usable response document.");
                }

                var root = responseDocument.RootElement;

                var responseState =
                    ReadResponseState(root);

                if (responseState == "incomplete")
                {
                    if ((requiresMandatoryGovernedInspection &&
                         !governedInspectionCompleted) ||
                        (failedGovernedReads.Count > 0 && successfulGovernedReads.Count == 0))
                    {
                        return LegendFounderAiChatResponse.ModeFailure(
                            mode,
                            FailureMessageForMode(
                                mode,
                                "The provider output ended before the required governed LEGEND inspection completed."),
                            "governed_inspection",
                            "provider_response",
                            "required_governed_inspection_missing");
                    }

                    var partial =
                        ExtractOutputText(root);

                    accumulatedProviderAnswer =
                        MergeProviderAnswerSegment(
                            accumulatedProviderAnswer,
                            partial);

                    var remainingAfterProvider =
                        TimeSpan.FromSeconds(_timeoutSeconds) -
                        executionClock.Elapsed;

                    if (!string.IsNullOrWhiteSpace(partial) &&
                        round < maximumToolRounds - 1 &&
                        remainingAfterProvider > TimeSpan.FromSeconds(8))
                    {
                        input.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = partial.Trim()
                        });
                        input.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = "Continue the same answer exactly where it stopped. Do not restart, summarize, or repeat completed material."
                        });
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(accumulatedProviderAnswer))
                    {
                        if (confirmedLearningMutationRequired &&
                            !learningMutationCompleted)
                        {
                            return LegendFounderAiChatResponse.ModeFailure(
                                mode,
                                FailureMessageForMode(
                                    mode,
                                    "The confirmed teaching request ended before the existing governed learning authority returned a successful receipt."),
                                "learning_submission_incomplete",
                                "governed_tool",
                                "confirmed_learning_mutation_missing");
                        }

                        return new LegendFounderAiChatResponse(
                            true,
                            mode,
                            AppendLearningReceipt(
                                AppendReadDiagnostics(accumulatedProviderAnswer, failedGovernedReads.Values),
                                learningMutationReceipt),
                            null,
                            ResponseAuthority: "OpenAITeacher",
                            Stage: "provider_response",
                            Reason: sourceLanguageTemporarilyUnavailable
                                ? "source_language_identification_unavailable"
                                : failedGovernedReads.Count > 0
                                    ? "partial_governed_inspection"
                                    : null,
                            EvidenceOrigin:
                                LegendConnectResearchEvidenceOrigin.UnresolvedEvidence);
                    }

                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        FailureMessageForMode(
                            mode,
                            "The provider output window ended before usable text was produced."),
                        "provider_incomplete",
                        "provider_response",
                        "provider_output_incomplete");
                }

                if (responseState != "completed")
                {
                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        FailureMessageForMode(
                            mode,
                            "The provider returned an unusable reasoning response."),
                        "provider_response",
                        "provider_response",
                        "provider_response_unusable");
                }

                var toolCalls = ReadFunctionCalls(root);

                if (toolCalls.Count == 0)
                {
                    if ((requiresMandatoryGovernedInspection &&
                         !governedInspectionCompleted) ||
                        (failedGovernedReads.Count > 0 && successfulGovernedReads.Count == 0))
                    {
                        return LegendFounderAiChatResponse.ModeFailure(
                            mode,
                            FailureMessageForMode(
                                mode,
                                "The provider did not perform the required governed LEGEND inspection, so no current-state answer was accepted."),
                            "governed_inspection",
                            "governed_tool",
                            "required_governed_inspection_missing");
                    }

                    if (confirmedLearningMutationRequired &&
                        !learningMutationCompleted)
                    {
                        return LegendFounderAiChatResponse.ModeFailure(
                            mode,
                            FailureMessageForMode(
                                mode,
                                "The provider completed without executing the confirmed governed teaching submission."),
                            "learning_submission_incomplete",
                            "governed_tool",
                            "confirmed_learning_mutation_missing");
                    }

                    await ReportProgressAsync(
                        progress,
                        new LegendFounderAiProgressEvent(
                            "response",
                            "The required checks are complete. Finalizing the response.",
                            round + 1),
                        effectiveToken);

                    var answer = ExtractOutputText(root);

                    accumulatedProviderAnswer =
                        MergeProviderAnswerSegment(
                            accumulatedProviderAnswer,
                            answer);

                    if (string.IsNullOrWhiteSpace(accumulatedProviderAnswer))
                    {
                        return LegendFounderAiChatResponse.ModeFailure(
                            mode,
                            FailureMessageForMode(
                                mode,
                                "The provider completed without usable response text."),
                            "provider_response",
                            "provider_response",
                            "provider_response_empty");
                    }

                    return new LegendFounderAiChatResponse(
                        true,
                        mode,
                        AppendLearningReceipt(
                            AppendReadDiagnostics(accumulatedProviderAnswer, failedGovernedReads.Values),
                            learningMutationReceipt),
                        null,
                        ResponseAuthority: "OpenAITeacher",
                        Stage: "provider_response",
                        Reason: sourceLanguageTemporarilyUnavailable
                            ? "source_language_identification_unavailable"
                            : failedGovernedReads.Count > 0
                                ? "partial_governed_inspection"
                                : null,
                        EvidenceOrigin:
                            LegendConnectResearchEvidenceOrigin.UnresolvedEvidence);
                }

                if (!allowTools)
                {
                    return LegendFounderAiChatResponse.ModeFailure(
                        mode,
                        FailureMessageForMode(mode,
                            "The provider requested a tool after the bounded execution window closed."),
                        "governed_inspection",
                        "governed_tool",
                        "provider_tool_execution_not_allowed");
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

                    // Provider latency and preceding calls consume the same window.
                    remaining = TimeSpan.FromSeconds(_timeoutSeconds) - executionClock.Elapsed;
                    if (ResolveReadOnlyToolBudget(remaining) < TimeSpan.FromSeconds(MinimumReadOnlyToolSeconds))
                    {
                        return LegendFounderAiChatResponse.ModeFailure(
                            mode,
                            FailureMessageForMode(mode, "The bounded execution window closed before the next governed check."),
                            "governed_inspection",
                            "governed_tool",
                            "provider_tool_execution_not_allowed");
                    }
                    effectiveToken.ThrowIfCancellationRequested();

                    var toolOutput =
                        await ExecuteFounderToolWithBudgetAsync(
                            founder,
                            call,
                            mode,
                            mutationAuthorization,
                            ResolveReadOnlyToolBudget(remaining),
                            toolOutputBudget,
                            effectiveToken,
                            providerPolicy);

                    if (string.Equals(
                            call.Name,
                            "legend_research_internet",
                            StringComparison.Ordinal))
                    {
                        if (!TryReadResearchOutcome(
                                toolOutput,
                                out var completedResearch))
                        {
                            _logger.LogInformation(
                                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode}",
                                "ResearchOutcomeValidated", "LegendFounderAiConversationService.TryReadResearchOutcome", "research_tool", "rejected", "research_citation_validation_missing");
                            return LegendFounderAiChatResponse.ModeFailure(
                                mode,
                                "LEGEND rejected an incomplete or unvalidated governed research outcome.",
                                "research_outcome_invalid",
                                "research_failure",
                                "research_citation_validation_missing");
                        }
                        _logger.LogInformation(
                            "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode}",
                            "ResearchCompleted", "LegendFounderAiConversationService.TryReadResearchOutcome", "research_tool", completedResearch!.State.ToString(),
                            LegendConnectTelemetry.NormalizeDiagnosticReason(completedResearch.Failure?.ReasonCode ?? completedResearch.InsufficientEvidence?.ReasonCode ??
                                completedResearch.UnresolvedConflict?.ReasonCode ?? completedResearch.Decision.ReasonCode));
                        return ResearchChatResponse(
                            mode,
                            completedResearch!,
                            nativeInference?.ModelAssistance);
                    }

                    if (_toolAuthority.IsReadOnly(call.Name))
                    {
                        var governedReadSucceeded =
                            IsSuccessfulFounderToolOutput(toolOutput);

                        if (_toolAuthority.IsGovernedEvidence(call.Name))
                        {
                            var readScopeIdentity = ReadScopeIdentity(call.Name, call.Arguments);
                            if (governedReadSucceeded)
                            {
                                failedGovernedReads.Remove(readScopeIdentity);
                                successfulGovernedReads.Add(readScopeIdentity);
                            }
                            else
                            {
                                successfulGovernedReads.Remove(readScopeIdentity);
                                failedGovernedReads[readScopeIdentity] = new FounderAiReadDiagnostic(
                                    call.Name, readScopeIdentity, "governed_read_unavailable");
                            }

                            if (requiredReadScope is not null &&
                                string.Equals(readScopeIdentity,
                                    ReadScopeIdentity(requiredReadScope.ToolName, requiredReadScope.ArgumentsJson),
                                    StringComparison.Ordinal))
                            {
                                governedInspectionCompleted = governedReadSucceeded &&
                                    LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
                                        requiredReadScope,
                                        toolOutput,
                                        DateTime.UtcNow,
                                        out _,
                                        out _);
                            }
                            else if (!requiresMandatoryGovernedInspection)
                            {
                                governedInspectionCompleted = successfulGovernedReads.Count > 0;
                            }
                        }
                    }
                    else if (IsLearningMutationTool(call.Name))
                    {
                        learningMutationCompleted =
                            TryReadLearningMutationReceipt(
                                call.Name,
                                toolOutput,
                                mutationAuthorization?.CorrelationId,
                                out learningMutationReceipt);
                    }

                    await ReportProgressAsync(
                        progress,
                        new LegendFounderAiProgressEvent(
                            IsSuccessfulFounderToolOutput(toolOutput) ? "tool_complete" : "tool_unavailable",
                            IsSuccessfulFounderToolOutput(toolOutput)
                                ? $"Completed: {toolDescription}"
                                : $"Unavailable: {toolDescription}",
                            round + 1,
                            call.Name,
                            ReadScopeIdentity(call.Name, call.Arguments)),
                        effectiveToken);

                    input.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = call.CallId,
                        ["output"] = toolOutput
                    });
                }
            }

            return LegendFounderAiChatResponse.ModeFailure(
                mode,
                FailureMessageForMode(
                    mode,
                    "The current inspection window ended before all governed checks could complete. Ask it to continue."),
                "timeout",
                "governed_tool",
                "inspection_window_exhausted");
        }
        catch (LegendFounderAiToolExecutionException exception)
        {
            return LegendFounderAiChatResponse.ModeFailure(
                mode,
                FailureMessageForMode(
                    mode,
                    "A governed LEGEND inspection could not complete safely."),
                exception.FailureKind,
                "governed_tool",
                exception.Reason);
        }
        catch (AgentPortal.Security.ForbidResultException)
        {
            throw;
        }
        catch (LegendFounderAiProviderException exception)
        {
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} StatusCode={StatusCode}",
                "ProviderEscalationRejected", "LegendFounderAiConversationService.SendResponseAsync", "external_response", "failed", "provider_http_rejection", exception.StatusCode);

            return NativeInferenceUnavailableResponse(
                mode,
                nativeInference,
                nativeFailureDetail,
                $"provider_http_{exception.StatusCode}",
                $"The provider returned HTTP {exception.StatusCode}.") with
            {
                ProviderStatusCode = exception.StatusCode,
                Reference = SafeProviderCorrelation(exception.ProviderRequestId) ?? SafeProviderCorrelation(exception.ClientRequestId)
            };
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            if (IsTeacherMode(mode))
            {
                return LegendFounderAiChatResponse.ModeFailure(
                    mode,
                    "OpenAI Teacher could not complete this request. The current request budget ended before a response was produced.",
                    "timeout",
                    "request_budget",
                    "request_budget_exhausted");
            }

            return NativeInferenceUnavailableResponse(
                mode,
                nativeInference,
                nativeFailureDetail,
                "provider_timeout",
                "The provider response window expired.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ExceptionType={ExceptionType}",
                "ProviderTransportFailed", "LegendFounderAiConversationService.SendResponseAsync", "external_response", "failed", "provider_transport_failure", exception.GetType().Name);

            return NativeInferenceUnavailableResponse(
                mode,
                nativeInference,
                nativeFailureDetail,
                "provider_transport_failure",
                "The provider transport failed.");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ExceptionType={ExceptionType}",
                "ProviderJsonInvalid", "LegendFounderAiConversationService.SendResponseAsync", "external_response", "failed", "provider_invalid_json", exception.GetType().Name);

            return NativeInferenceUnavailableResponse(
                mode,
                nativeInference,
                nativeFailureDetail,
                "provider_invalid_json",
                "The provider returned an invalid response format.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ExceptionType={ExceptionType}",
                "GovernedExecutionFailed", "LegendFounderAiConversationService.ReplyAsync", "governed_execution", "failed", "unexpected_governed_failure", exception.GetType().Name);

            return LegendFounderAiChatResponse.ModeFailure(
                mode,
                FailureMessageForMode(
                    mode,
                    "The governed request could not complete safely before a response was produced."),
                "governed_execution",
                "governed_execution",
                "unexpected_governed_failure");
        }
    }

    /// <summary>
    /// Obtains the typed operational intent for the latest request from the
    /// existing Founder-gated, observational, read-only meaning-graph analysis.
    /// It is used only when the native inference result did not already carry
    /// the classification, so exactly one analysis produces exactly one
    /// classification per request.
    /// </summary>
    private async Task<FounderAiOwnedRecordResolution> ClassifyOwnedRecordIntentAsync(
        ClaimsPrincipal founder,
        IReadOnlyList<LegendFounderAiChatMessage> conversation,
        LegendConnectDiscourseStateSnapshot? currentDiscourseState,
        string sourceLanguageCode,
        CancellationToken cancellationToken)
    {
        var latest = conversation[^1].Content ?? string.Empty;
        try
        {
            // The existing observational content-plan authority selects the
            // same governed result frame without generating a native answer.
            // Both intent and exact read scope come from that one selection.
            var plan = await TraceNativeStageAsync("owned_record_classification", "FounderLegendConnectService.TryBindConversationContentAsync", () => _legend.TryBindConversationContentAsync(
                founder, latest, currentDiscourseState, sourceLanguageCode, cancellationToken));
            _logger.LogInformation(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} RequiresGovernedReadReceipt={RequiresGovernedReadReceipt} ReadScopeEstablished={ReadScopeEstablished}",
                "OwnedRecordClassified", "FounderLegendConnectService.TryBindConversationContentAsync", "owned_record_classification",
                plan.OwnedRecordIntent?.Intent.ToString() ?? "unavailable", LegendConnectTelemetry.NormalizeDiagnosticReason(plan.ReasonCode),
                plan.OwnedRecordIntent?.RequiresGovernedReadReceipt ?? false, plan.ReadOnlyContentRequest is not null);
            return new FounderAiOwnedRecordResolution(
                plan.OwnedRecordIntent ?? LegendConnectOwnedRecordRequest.AnalysisUnavailable(
                    "governed_meaning_graph_analysis_unavailable"),
                plan.ReadOnlyContentRequest);
        }
        catch (AgentPortal.Security.ForbidResultException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ExceptionType={ExceptionType}",
                "OwnedRecordClassificationException", "FounderLegendConnectService.TryBindConversationContentAsync", "owned_record_classification",
                "failed", "governed_meaning_graph_analysis_unavailable", exception.GetType().Name);
            return new FounderAiOwnedRecordResolution(
                LegendConnectOwnedRecordRequest.AnalysisUnavailable(
                    "governed_meaning_graph_analysis_unavailable: " + exception.GetType().Name),
                null);
        }
    }

    private sealed record FounderAiOwnedRecordResolution(
        LegendConnectOwnedRecordClassification Classification,
        LegendConnectReadOnlyContentBindingRequest? ReadOnlyContentRequest);

    private async Task<LegendConnectDiscourseStateSnapshot?> ObserveDiscourseMeaningAsync(
        ClaimsPrincipal founder,
        string? conversationId,
        string role,
        string surface,
        CancellationToken inferenceCancellationToken,
        CancellationToken requestCancellationToken,
        string sourceLanguageCode)
    {
        // There is no durable conversation to observe without a valid key.
        // Skip this otherwise duplicated meaning analysis before its queries.
        if (!Guid.TryParse(conversationId, out _))
            return null;
        using var observationBudget = CancellationTokenSource.CreateLinkedTokenSource(
            inferenceCancellationToken);
        observationBudget.CancelAfter(
            TimeSpan.FromSeconds(MaximumDiscourseObservationSeconds));
        try
        {
            var meaning = await TraceNativeStageAsync("discourse_meaning_analysis", "FounderLegendConnectService.AnalyzeReusableMeaningGraphAsync", () => _legend.AnalyzeReusableMeaningGraphAsync(
                founder,
                surface,
                sourceLanguageCode,
                observationBudget.Token));
            var currentTurnSequence = await TraceNativeStageAsync("discourse_persistence", "LegendFounderAiDiscourseStateService.RecordCurrentObservationAsync", () => _discourse.RecordCurrentObservationAsync(
                founder,
                conversationId,
                role,
                meaning,
                observationBudget.Token,
                sourceLanguageCode));
            if (role != "user" || currentTurnSequence is not int sequence)
                return null;
            var state = await TraceNativeStageAsync("discourse_reload", "LegendFounderAiDiscourseStateService.GetStateAsync", () => _discourse.GetStateAsync(
                founder, conversationId, observationBudget.Token, currentTurnSequence: sequence));
            return state is null ? null : state with
            {
                CurrentTurnAnalysis = new LegendConnectCurrentTurnMeaningAnalysis(
                    LegendLanguageIdentity.TextHash(LegendLanguageIdentity.NormalizeText(surface)),
                    sourceLanguageCode,
                    sequence,
                    meaning)
            };
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
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ExceptionType={ExceptionType}",
                "DiscourseObservationException", "LegendFounderAiConversationService.ObserveDiscourseMeaningAsync", "discourse_observation", "failed", exception.GetType().Name);
        }
        return null;
    }

    private async Task<T> TraceNativeStageAsync<T>(string stage, string authorityMethod, Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "completed";
        var domainOutcome = "unknown";
        var reasonCode = "none";
        var exceptionType = "none";
        _logger.LogInformation(
            "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome}",
            "StageStarted", authorityMethod, stage, "started");
        try
        {
            var result = await action();
            (domainOutcome, reasonCode) = DescribeNativeStageResult(result);
            if (result is LegendConnectUtteranceMeaningGraphSnapshot graph)
            {
                _logger.LogInformation(
                    "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} GraphNodes={GraphNodes} GraphRelations={GraphRelations} UnknownComponents={UnknownComponents}",
                    "MeaningGraphObserved", authorityMethod, stage, graph.IsComposed ? "composed" : "uncomposed",
                    LegendConnectTelemetry.NormalizeDiagnosticReason(graph.ReasonCode), graph.Nodes.Count, graph.Relations.Count, graph.UnknownSurfaceComponents.Count);
            }
            if (result is LegendConnectDiscourseStateSnapshot discourse)
            {
                var nodes = discourse.Turns.SelectMany(turn => turn.Nodes).ToArray();
                var bindings = discourse.Turns.SelectMany(turn => turn.Bindings).ToArray();
                _logger.LogInformation(
                    "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} DiscourseTurns={DiscourseTurns} GraphNodes={GraphNodes} GraphRelations={GraphRelations} FounderNodes={FounderNodes} MachineNodes={MachineNodes} CurrentTurnAssertionNodes={CurrentTurnAssertionNodes} SourceSlotBindingNodes={SourceSlotBindingNodes} BindingsBound={BindingsBound} BindingsUnresolved={BindingsUnresolved}",
                    "DiscourseStateObserved", authorityMethod, stage, "observed", discourse.Turns.Count, nodes.Length,
                    discourse.Turns.Sum(turn => turn.Relations.Count),
                    nodes.Count(node => node.Provenance == "FounderApproved"),
                    nodes.Count(node => node.Provenance == "SystemValidatedMachine"),
                    nodes.Count(node => node.Provenance == "CurrentTurnAssertion"),
                    nodes.Count(node => node.SourceSlotBinding is not null),
                    bindings.Count(binding => binding.ResolutionState == "bound"),
                    bindings.Count(binding => binding.ResolutionState != "bound"));
            }
            return result;
        }
        catch (OperationCanceledException exception)
        {
            outcome = "cancelled";
            exceptionType = exception.GetType().Name;
            throw;
        }
        catch (Exception exception)
        {
            outcome = "failed";
            exceptionType = exception.GetType().Name;
            throw;
        }
        finally
        {
            // Fixed stage names and bounded counts expose the blocking
            // authority without recording a prompt, graph, value or actor.
            _logger.LogInformation(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ExecutionOutcome={ExecutionOutcome} DomainOutcome={DomainOutcome} ReasonCode={ReasonCode} ElapsedMs={ElapsedMs} ExceptionType={ExceptionType} TraceId={TraceId}",
                "StageEnded", authorityMethod, stage, outcome == "completed" ? domainOutcome : outcome, outcome, domainOutcome,
                outcome == "cancelled" ? "operation_cancelled" : outcome == "failed" ? "authority_exception" : reasonCode,
                (long)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds), exceptionType, Activity.Current?.TraceId.ToString());
        }
    }


    internal static (string Outcome, string Reason) DescribeNativeStageResult(object? result)
    {
        var (outcome, reason) = result switch
        {
            TranslationDetectionResult detection => (detection.Succeeded ? "succeeded" : "failed", detection.ErrorCode),
            FounderAiSourceLanguageResolution language => (language.Succeeded ? "succeeded" : "failed", language.Reason),
            LegendConnectNativeInferenceSnapshot inference => (inference.Supported ? "supported" : "unsupported", inference.ReasonCode),
            LegendConnectContentBoundResponseMeaningPlanResult plan => (plan.Supported ? "supported" : "unsupported", plan.ReasonCode),
            LegendConnectResearchOutcome research => (research.State.ToString(),
                research.Failure?.ReasonCode ?? research.InsufficientEvidence?.ReasonCode ??
                research.UnresolvedConflict?.ReasonCode ?? research.Decision.ReasonCode),
            LegendConnectReadOnlyContentBindingResult binding => (binding.Succeeded ? "succeeded" : "failed", binding.ReasonCode),
            LegendConnectUtteranceMeaningGraphSnapshot graph => (graph.IsComposed ? "composed" : "uncomposed", graph.ReasonCode),
            string output when output.TrimStart().StartsWith("{", StringComparison.Ordinal) =>
                (IsSuccessfulFounderToolOutput(output) ? "succeeded" : "failed", (string?)null),
            null => ("unavailable", (string?)null),
            _ => ("observed", (string?)null)
        };
        return (outcome, LegendConnectTelemetry.NormalizeDiagnosticReason(reason));
    }

    private async Task<FounderAiSourceLanguageResolution> ResolveSourceLanguageAsync(
        string? declaredLanguageCode,
        string sourceText,
        CancellationToken cancellationToken,
        LegendConnectExternalProviderPolicy? providerPolicy = null)
    {
        if (!string.IsNullOrWhiteSpace(declaredLanguageCode))
        {
            if (!LegendLanguageIdentity.TryNormalize(
                    declaredLanguageCode,
                    out var normalizedCode))
            {
                return FounderAiSourceLanguageResolution.Failure(
                    FounderAiSourceLanguageOutcome.InvalidDeclaration,
                    "source_language_code_invalid");
            }

            // Founder conversation is a read path. Language identity is read
            // from the seeded governed registry and never provisions baseline
            // rows from a reply; initialization keeps its own authority.
            var enabledLanguage =
                await TraceNativeStageAsync("declared_language_registry", _languages.GetType().Name + "." + nameof(ILegendLanguageRegistry.NormalizeEnabledTranslationLanguageReadOnlyAsync),
                    () => _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(normalizedCode, cancellationToken));
            return enabledLanguage is null
                ? FounderAiSourceLanguageResolution.Failure(
                    FounderAiSourceLanguageOutcome.UnsupportedLanguage,
                    "source_language_unsupported")
                : FounderAiSourceLanguageResolution.Success(
                    enabledLanguage);
        }

        TranslationDetectionResult detected;
        try
        {
            detected = await TraceNativeStageAsync("source_language_detection", _translation.GetType().Name + "." + nameof(ITranslationService.DetectLanguageAsync),
                () => _translation.DetectLanguageAsync(sourceText, cancellationToken, providerPolicy));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ExceptionType={ExceptionType}",
                "SourceLanguageException", _translation.GetType().Name + "." + nameof(ITranslationService.DetectLanguageAsync), "source_language", "failed",
                "source_language_identification_unavailable", exception.GetType().Name);
            return FounderAiSourceLanguageResolution.Failure(
                FounderAiSourceLanguageOutcome
                    .TransientIdentificationUnavailable,
                "source_language_identification_unavailable");
        }

        if (!detected.Succeeded)
        {
            return detected.ErrorCode switch
            {
                "translation_language_ambiguous" =>
                    FounderAiSourceLanguageResolution.Failure(
                        FounderAiSourceLanguageOutcome.SemanticAmbiguity,
                        "source_language_ambiguous"),
                "translation_language_unsupported" =>
                    FounderAiSourceLanguageResolution.Failure(
                        FounderAiSourceLanguageOutcome.UnsupportedLanguage,
                        "source_language_unsupported"),
                // Under a native-only policy the governed identification
                // authority reached its own conclusion without any external
                // provider. That is a semantic result, not a transient outage,
                // so it must never be reported as one or escalated.
                "native_only_governed_source_language_undetermined" =>
                    FounderAiSourceLanguageResolution.Failure(
                        FounderAiSourceLanguageOutcome.SemanticAmbiguity,
                        "native_only_governed_source_language_undetermined"),
                "external_provider_forbidden_by_native_only_policy" or
                "native_only_translation_boundary_not_policy_aware" =>
                    FounderAiSourceLanguageResolution.Failure(
                        FounderAiSourceLanguageOutcome.ProviderPolicyBlocked,
                        detected.ErrorCode),
                _ => FounderAiSourceLanguageResolution.Failure(
                    FounderAiSourceLanguageOutcome
                        .TransientIdentificationUnavailable,
                    "source_language_identification_unavailable")
            };
        }

        if (!LegendLanguageIdentity.TryNormalize(
                detected.Language,
                out var detectedCode))
        {
            return FounderAiSourceLanguageResolution.Failure(
                FounderAiSourceLanguageOutcome.SemanticAmbiguity,
                "source_language_ambiguous");
        }

        var enabledDetectedLanguage =
            await TraceNativeStageAsync("resolved_language_registry", _languages.GetType().Name + "." + nameof(ILegendLanguageRegistry.NormalizeEnabledTranslationLanguageReadOnlyAsync),
                () => _languages.NormalizeEnabledTranslationLanguageReadOnlyAsync(detectedCode, cancellationToken));
        return enabledDetectedLanguage is null
            ? FounderAiSourceLanguageResolution.Failure(
                FounderAiSourceLanguageOutcome.UnsupportedLanguage,
                "source_language_unsupported")
            : FounderAiSourceLanguageResolution.Success(
                enabledDetectedLanguage);
    }

    private async Task<JsonDocument?> SendResponseAsync(
        string apiKey,
        string model,
        string instructions,
        IReadOnlyList<object> input,
        IReadOnlyList<object> tools,
        bool allowTools,
        bool requireToolCall,
        TimeSpan providerBudget,
        string reasoningEffort,
        int maxOutputTokens,
        CancellationToken cancellationToken,
        bool catalogAcceptanceOnly = false)
    {
        if (catalogAcceptanceOnly && !allowTools)
        {
            throw new ArgumentException(
                "A catalog acceptance request must include the Founder tools.",
                nameof(allowTools));
        }

        var serializedTools = allowTools
            ? tools
            : Array.Empty<object>();
        if (catalogAcceptanceOnly)
            allowTools = false;

        var payload = new
        {
            model,
            store = false,
            instructions,
            input,
            tools = serializedTools,

            tool_choice =
                catalogAcceptanceOnly
                    ? "none"
                    : ResolveToolChoice(
                        allowTools,
                        requireToolCall),

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
                        JsonContent.Create(
                            payload,
                            options: JsonOptions)
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
                            "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} StatusCode={StatusCode} Attempt={Attempt} RetryDelayMs={RetryDelayMs}",
                            "ProviderRetry", "LegendFounderAiConversationService.SendResponseAsync", "external_response", "retrying", "provider_transient_rejection",
                            (int)response.StatusCode, attempt, (long)Math.Ceiling(boundedDelay.TotalMilliseconds));

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
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} StatusCode={StatusCode} Attempt={Attempt}",
                "ProviderRejected", "LegendFounderAiConversationService.SendResponseAsync", "external_response", "failed", "provider_http_rejection",
                (int)response.StatusCode, attempt);

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

    private string ResolveProviderModel()
    {
        var model =
            _configuration["OpenAI:LegendFounderAiModel"]?.Trim();

        if (string.IsNullOrWhiteSpace(model))
            model = _configuration["OpenAI:Model"]?.Trim();

        return string.IsNullOrWhiteSpace(model)
            ? "gpt-5"
            : model;
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

    internal static LegendFounderAiChatResponse NativeInferenceUnavailableResponse(
        string mode,
        LegendConnectNativeInferenceSnapshot? nativeInference,
        string? nativeFailureDetail = null,
        string? providerFailureCode = null,
        string? providerFailureDetail = null)
    {
        var nativeReasonCode = string.IsNullOrWhiteSpace(nativeInference?.ReasonCode)
            ? nativeInference is null ? "native_inference_unavailable" : "native_inference_unsupported"
            : LegendConnectTelemetry.NormalizeDiagnosticReason(nativeInference.ReasonCode);
        var nativeDetail = !string.IsNullOrWhiteSpace(nativeFailureDetail)
            ? "The native authority did not produce a verified result."
            : !string.IsNullOrWhiteSpace(nativeInference?.AuthoritySummary)
                ? nativeInference.AuthoritySummary.Trim()
                : "The native authority returned no additional failure detail.";
        var evidenceCount = nativeInference?.EvidenceCount ?? 0;
        var escalationState = nativeInference?.RequiresEscalation == true
            ? "required"
            : "not_permitted";

        var providerCode = string.IsNullOrWhiteSpace(providerFailureCode)
            ? nativeInference?.RequiresEscalation == true
                ? "provider_unavailable_without_detail"
                : "provider_not_attempted"
            : LegendConnectTelemetry.NormalizeDiagnosticReason(providerFailureCode);
        var providerDetail = string.IsNullOrWhiteSpace(providerFailureCode)
            ? nativeInference?.RequiresEscalation == true
                ? "The escalation path did not expose a provider-specific failure detail."
                : "The governed native result did not permit external escalation."
            : $"The provider did not produce a usable response. Reason={providerCode}.";

        var failureKind = providerCode.Contains(
                "timeout",
                StringComparison.OrdinalIgnoreCase)
                ? "timeout"
                : providerCode.StartsWith(
                    "provider_http_",
                    StringComparison.Ordinal)
                    ? "provider_http"
                    : providerCode.Contains(
                        "transport",
                        StringComparison.OrdinalIgnoreCase)
                        ? "transport"
                        : providerCode.Contains(
                            "json",
                            StringComparison.OrdinalIgnoreCase)
                            ? "provider_json"
                            : providerCode == "provider_not_attempted" ? "native_inference" : "configuration";

        if (IsTeacherMode(mode))
        {
            return LegendFounderAiChatResponse.ModeFailure(
                mode,
                $"OpenAI Teacher could not complete this request. Stage=provider; Reason={providerCode}.",
                failureKind,
                "provider",
                providerCode);
        }

        var diagnostic = $"LEGEND could not complete this response. " +
            $"NativeFailure={nativeReasonCode}; NativeDetail={nativeDetail}; " +
            $"EvidenceCount={evidenceCount}; Escalation={escalationState}; " +
            $"ProviderFailure={providerCode}; ProviderDetail={providerDetail}";
        return new LegendFounderAiChatResponse(
            false, mode, diagnostic, diagnostic,
            FailureKind: failureKind,
            ResponseAuthority: "SystemDiagnostic",
            Stage: "native_or_provider_unavailable",
            Reason: providerCode,
            ModelAssistanceState: nativeInference?.ModelAssistance?.State,
            ModelAssistanceReason: nativeInference?.ModelAssistance?.ReasonCode,
            ModelVersion: nativeInference?.ModelAssistance?.ModelVersion,
            ModelTrainingRunId: nativeInference?.ModelAssistance?.ModelTrainingRunId,
            ModelProvenance: nativeInference?.ModelAssistance?.Provenance);
    }

    internal static string? SafeProviderCorrelation(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value : null;

    private static bool IsTeacherMode(string mode) =>
        string.Equals(mode, "teacher", StringComparison.Ordinal);

    private static string FailureMessageForMode(
        string mode,
        string detail) =>
        IsTeacherMode(mode)
            ? $"OpenAI Teacher could not complete this request. {detail}"
            : $"Legend® Ai could not complete this request. {detail}";

    private static string NormalizeFailureDetail(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "No additional detail was supplied."
            : value.Trim();
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }

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

                    "legend_research_internet" =>
                        ReadRequiredString(
                            document.RootElement,
                            "question"),

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

            "legend_research_internet" =>
                "Conducting bounded, zero-write external research through the canonical LEGEND research authority.",

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
        string mode,
        FounderAiMutationAuthorization? mutationAuthorization,
        TimeSpan readOnlyBudget,
        int outputBudgetCharacters,
        CancellationToken cancellationToken,
        LegendConnectExternalProviderPolicy providerPolicy)
    {
        if (!_toolAuthority.IsReadOnly(
                call.Name))
        {
            try
            {
                var mutationOutput = await TraceNativeStageAsync("governed_tool", "LegendFounderToolAuthority.ExecuteAsync", () => _toolAuthority.ExecuteAsync(
                    founder,
                    call with
                    {
                        MutationAuthorization = mutationAuthorization
                    },
                    mode,
                    cancellationToken,
                    providerPolicy));
                return BoundSerializedOutput(mutationOutput, outputBudgetCharacters);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AgentPortal.Security.ForbidResultException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new LegendFounderAiToolExecutionException(
                    call.Name,
                    "tool_timeout",
                    "timeout");
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ExceptionType={ExceptionType}",
                    "GovernedToolFailed", "LegendFounderToolAuthority.ExecuteAsync", "governed_tool", "failed", "tool_execution_failed", exception.GetType().Name);

                throw new LegendFounderAiToolExecutionException(
                    call.Name,
                    "tool_execution_failed",
                    "governed_tool");
            }
        }

        using var toolBudget =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        toolBudget.CancelAfter(readOnlyBudget);

        try
        {
            var output = await TraceNativeStageAsync(
                string.Equals(call.Name, "legend_research_internet", StringComparison.Ordinal) ? "research_tool" : "governed_tool",
                "LegendFounderToolAuthority.ExecuteAsync", () => _toolAuthority.ExecuteAsync(
                founder,
                string.Equals(
                    call.Name,
                    "legend_research_internet",
                    StringComparison.Ordinal)
                        ? call with
                        {
                            MutationAuthorization = mutationAuthorization
                        }
                        : call,
                mode,
                toolBudget.Token,
                providerPolicy));
            toolBudget.Token.ThrowIfCancellationRequested();
            return BoundSerializedOutput(output, outputBudgetCharacters);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode}",
                "GovernedToolTimedOut", "LegendFounderToolAuthority.ExecuteAsync", "governed_tool", "cancelled", "tool_timeout");

            throw new LegendFounderAiToolExecutionException(
                call.Name,
                "tool_timeout",
                "timeout");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentPortal.Security.ForbidResultException)
        {
            throw;
        }
        catch (Exception exception)
            when (cancellationToken.IsCancellationRequested)
        {
            // SQL Server can report an already-cancelled command as a
            // SqlException instead of OperationCanceledException. Preserve
            // the canonical request-budget classification rather than
            // letting that transport detail escape to the controller's 500.
            throw new OperationCanceledException(
                "The Founder AI request budget was cancelled.",
                exception,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ExceptionType={ExceptionType}",
                "GovernedToolFailed", "LegendFounderToolAuthority.ExecuteAsync", "governed_tool", "failed", "tool_execution_failed", exception.GetType().Name);

            return BuildReadOnlyToolFailureOutput(
                call.Name,
                exception);
        }
    }

    internal static string BuildReadOnlyToolFailureOutput(
        string tool,
        Exception exception)
    {
        var permissionDenied = exception is UnauthorizedAccessException;
        var failureCategory = exception switch
        {
            UnauthorizedAccessException => "permission_denied",
            HttpRequestException => "connectivity_failure",
            TimeoutException => "timeout",
            _ => "read_execution_failure"
        };
        var correlationId =
            Activity.Current?.TraceId.ToString() is { Length: > 0 } traceId
                ? traceId
                : Guid.NewGuid().ToString("N");

        return JsonSerializer.Serialize(
            new
            {
                ok = false,
                error = "tool_read_failed",
                failureCategory,
                tool,
                requestedResource = tool,
                authorizationDecision = permissionDenied
                    ? "denied"
                    : "unknown",
                policyOrPermission = permissionDenied
                    ? exception.GetType().Name
                    : null,
                correlationId,
                exceptionType = exception.GetType().Name,
                detail = "The governed authority did not produce a verified result.",
                instruction = "This read failed. Continue any independent governed reads that can still execute, then report this exact failed authority without inventing unavailable state."
            },
            JsonOptions);
    }

    internal static bool IsSuccessfulFounderToolOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return true;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                return false;
            if (root.TryGetProperty("stages", out var stages) &&
                (stages.ValueKind != JsonValueKind.Array ||
                 !stages.EnumerateArray().Any(stage =>
                     stage.ValueKind == JsonValueKind.Object &&
                     stage.TryGetProperty("state", out var state) &&
                     state.ValueKind == JsonValueKind.String &&
                     state.GetString() == "available")))
                return false;
            return (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False) &&
                   (!root.TryGetProperty("succeeded", out var succeeded) || succeeded.ValueKind != JsonValueKind.False);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record FounderAiReadDiagnostic(string Tool, string ScopeIdentity, string Reason);

    private static string AppendReadDiagnostics(
        string answer,
        ICollection<FounderAiReadDiagnostic> failures) =>
        failures.Count == 0
            ? answer
            : answer + "\n\nSome requested governed reads remain unavailable; their state was not verified.\n" +
              "LEGEND_GOVERNED_READ_DIAGNOSTICS\n" + JsonSerializer.Serialize(failures, JsonOptions);

    internal static string ReadScopeIdentity(string tool, string arguments)
    {
        var canonicalArguments = arguments;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            var effectiveArguments = tool == "legend_operational_diagnostics"
                ? LegendFounderToolAuthority.NormalizeOperationalDiagnosticArguments(document.RootElement)
                : document.RootElement;
            canonicalArguments = JsonSerializer.Serialize(CanonicalValue(effectiveArguments), JsonOptions);
        }
        catch (JsonException) { }
        catch (ArgumentException) { }
        return tool + ":" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalArguments)));

        static object? CanonicalValue(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToDictionary(property => property.Name, property => CanonicalValue(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => value.EnumerateArray().Select(CanonicalValue).ToArray(),
            _ => value.Clone()
        };
    }

    private static bool TryReadLearningMutationReceipt(
        string toolName,
        string output,
        string? authorizationCorrelation,
        out string? normalizedReceipt)
    {
        normalizedReceipt = null;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            switch (toolName)
            {
                case "legend_submit_machine_learning_candidate":
                    if (!TryReadMachineTeachingMutationReceipt(
                            output,
                            authorizationCorrelation,
                            out var machineReceipt))
                    {
                        return false;
                    }
                    normalizedReceipt = JsonSerializer.Serialize(
                        machineReceipt,
                        JsonOptions);
                    return true;

                case "legend_submit_founder_seed":
                {
                    var seed = JsonSerializer.Deserialize<LegendConnectKnowledgeSubmissionResult>(
                        output,
                        JsonOptions);
                    if (seed is not { Succeeded: true } ||
                        seed.SourceTextUnitId is not { } sourceTextUnitId ||
                        sourceTextUnitId == Guid.Empty)
                    {
                        return false;
                    }
                    normalizedReceipt = output.Trim();
                    return true;
                }

                case "legend_submit_founder_curriculum":
                {
                    var curriculum = JsonSerializer.Deserialize<LegendConnectCurriculumSubmissionResult>(
                        output,
                        JsonOptions);
                    if (curriculum is not { Succeeded: true } ||
                        curriculum.CurriculumFamilyId is not { } curriculumFamilyId ||
                        curriculumFamilyId == Guid.Empty)
                    {
                        return false;
                    }
                    normalizedReceipt = output.Trim();
                    return true;
                }

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryReadMachineTeachingMutationReceipt(
        string output,
        string? authorizationCorrelation,
        out LegendConnectMachineTeachingMutationReceipt? receipt)
    {
        receipt = null;
        if (string.IsNullOrWhiteSpace(output) ||
            string.IsNullOrWhiteSpace(authorizationCorrelation))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var requiredProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "succeeded",
                "candidateId",
                "proposalId",
                "durableState",
                "provenance",
                "authorizationCorrelation",
                "servingStatus",
                "canonicalStatus"
            };
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != requiredProperties.Count ||
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != requiredProperties.Count ||
                !properties.Select(property => property.Name)
                    .ToHashSet(StringComparer.Ordinal)
                    .SetEquals(requiredProperties))
            {
                return false;
            }

            var parsed = JsonSerializer.Deserialize<LegendConnectMachineTeachingMutationReceipt>(
                output,
                JsonOptions);
            if (parsed is null ||
                !parsed.Succeeded ||
                parsed.CandidateId == Guid.Empty ||
                parsed.ProposalId == Guid.Empty ||
                parsed.CandidateId == parsed.ProposalId ||
                parsed.DurableState is not ("AwaitingCritic" or "InsufficientEvidence") ||
                !string.Equals(
                    parsed.Provenance,
                    LegendConnectMachineTeachingMutationReceipt.RequiredProvenance,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    parsed.AuthorizationCorrelation,
                    authorizationCorrelation,
                    StringComparison.Ordinal) ||
                !Guid.TryParseExact(parsed.AuthorizationCorrelation, "N", out _) ||
                !string.Equals(
                    parsed.ServingStatus,
                    LegendConnectMachineTeachingMutationReceipt.RequiredServingStatus,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    parsed.CanonicalStatus,
                    LegendConnectMachineTeachingMutationReceipt.RequiredCanonicalStatus,
                    StringComparison.Ordinal))
            {
                return false;
            }

            receipt = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadResearchOutcome(
        string output,
        out LegendConnectResearchOutcome? outcome)
    {
        outcome = null;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<LegendConnectResearchOutcome>(
                output,
                JsonOptions);
            if (parsed is null ||
                parsed.Session.SessionId == Guid.Empty ||
                parsed.Provenance.SessionId != parsed.Session.SessionId ||
                parsed.Provenance.RequestId != parsed.Session.RequestId ||
                !parsed.Provenance.IsReadOnly ||
                !parsed.Provenance.ZeroWrite ||
                !string.Equals(
                    parsed.Provenance.Provenance,
                    LegendConnectResearchContracts.Provenance,
                    StringComparison.Ordinal))
            {
                return false;
            }
            if (parsed.State != LegendConnectResearchOutcomeState.Failure &&
                (parsed.Presentation is null ||
                 !parsed.Presentation.CitationValidation.Succeeded ||
                 !string.Equals(
                     parsed.Presentation.CitationValidation.PolicyIdentity,
                     LegendConnectResearchContracts.CitationPresentationPolicy,
                     StringComparison.Ordinal) ||
                 !ResearchCitationReceiptsMatch(
                     parsed.Session.CitationValidation,
                     parsed.Presentation.CitationValidation) ||
                 !ResearchCitationReceiptsMatch(
                     parsed.Provenance.CitationValidation,
                     parsed.Presentation.CitationValidation) ||
                 !string.Equals(
                     parsed.Provenance.CitationPresentationPolicyIdentity,
                     LegendConnectResearchContracts.CitationPresentationPolicy,
                     StringComparison.Ordinal) ||
                 parsed.Presentation.EvidenceOrigin != parsed.EvidenceOrigin ||
                 parsed.Session.LanguageLineage is null ||
                 !string.Equals(
                     parsed.Presentation.FinalResponseLanguageCode,
                     parsed.Session.LanguageLineage.FinalResponseLanguageCode,
                     StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(
                     parsed.Presentation.UserLanguageCode,
                     parsed.Session.LanguageLineage.UserLanguageCode,
                     StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(
                     parsed.PresentedText,
                     parsed.Presentation.PresentedText,
                     StringComparison.Ordinal) ||
                 !HasCompleteResearchPresentationLineage(parsed)))
            {
                return false;
            }

            outcome = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static LegendFounderAiChatResponse ResearchChatResponse(
        string mode,
        LegendConnectResearchOutcome outcome,
        LegendConnectNativeModelAssistanceSnapshot? modelAssistance)
    {
        var succeeded = outcome.State != LegendConnectResearchOutcomeState.Failure;
        return new LegendFounderAiChatResponse(
            succeeded,
            mode,
            succeeded ? outcome.PresentedText : null,
            succeeded ? null : outcome.PresentedText,
            succeeded ? null : "research_failure",
            ResponseAuthority:
                outcome.State == LegendConnectResearchOutcomeState.Conclusion
                    ? "GovernedResearch"
                    : "SystemDiagnostic",
            Stage: outcome.State switch
            {
                LegendConnectResearchOutcomeState.Conclusion => "research_response",
                LegendConnectResearchOutcomeState.InsufficientEvidence =>
                    "research_insufficient_evidence",
                LegendConnectResearchOutcomeState.UnresolvedConflict =>
                    "research_unresolved_conflict",
                _ => "research_failure"
            },
            Reason: outcome.State switch
            {
                LegendConnectResearchOutcomeState.InsufficientEvidence =>
                    outcome.InsufficientEvidence?.ReasonCode,
                LegendConnectResearchOutcomeState.UnresolvedConflict =>
                    outcome.UnresolvedConflict?.ReasonCode,
                LegendConnectResearchOutcomeState.Failure =>
                    outcome.Failure?.ReasonCode,
                _ => outcome.Decision.ReasonCode
            },
            ModelAssistanceState: modelAssistance?.State,
            ModelAssistanceReason: modelAssistance?.ReasonCode,
            ModelVersion: modelAssistance?.ModelVersion,
            ModelTrainingRunId: modelAssistance?.ModelTrainingRunId,
            ModelProvenance: modelAssistance?.Provenance,
            EvidenceOrigin: outcome.EvidenceOrigin,
            ResearchOutcome: outcome);
    }

    private static bool HasCompleteResearchPresentationLineage(
        LegendConnectResearchOutcome outcome)
    {
        var presentation = outcome.Presentation!;
        var sessionMaterialIds = (outcome.Session.MaterialClaimEvidence ?? [])
            .Select(item => item.EvidenceIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var sessionCitationIds = outcome.Session.Citations
            .Select(item => item.CitationIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var sessionDocumentIds = outcome.Session.Documents
            .Select(item => item.DocumentIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var sessionSourceIds = outcome.Session.Sources
            .Select(item => item.SourceIdentity)
            .ToHashSet(StringComparer.Ordinal);
        if (presentation.ConsultedSources.Count != sessionDocumentIds.Count ||
            !presentation.ConsultedSources.Select(item => item.DocumentIdentity)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(sessionDocumentIds) ||
            presentation.ConsultedSources.Any(item =>
                !sessionSourceIds.Contains(item.SourceIdentity)))
        {
            return false;
        }

        var ordinalCitationPairs = presentation.InlineCitations
            .Select(item => (item.Ordinal, item.CitationIdentity))
            .Distinct()
            .ToArray();
        if (ordinalCitationPairs.Any(item =>
                item.Ordinal < 1 ||
                !sessionCitationIds.Contains(item.CitationIdentity)) ||
            ordinalCitationPairs.GroupBy(item => item.Ordinal)
                .Any(group => group.Select(item => item.CitationIdentity)
                    .Distinct(StringComparer.Ordinal).Count() != 1) ||
            ordinalCitationPairs.GroupBy(item => item.CitationIdentity, StringComparer.Ordinal)
                .Any(group => group.Select(item => item.Ordinal).Distinct().Count() != 1))
        {
            return false;
        }

        foreach (var statement in presentation.Statements.Where(item =>
                     item.NormalizedClaimIdentity is not null))
        {
            if (statement.MaterialEvidenceIdentities.Count == 0 ||
                statement.CitationOrdinals.Count == 0 ||
                statement.MaterialEvidenceIdentities.Any(item =>
                    !sessionMaterialIds.Contains(item)) ||
                statement.CitationOrdinals.Any(ordinal =>
                    !ordinalCitationPairs.Any(item => item.Ordinal == ordinal)))
            {
                return false;
            }
        }

        IReadOnlyList<LegendConnectCitation> terminalCitations = outcome.State switch
        {
            LegendConnectResearchOutcomeState.Conclusion =>
                outcome.Conclusion?.Citations ?? [],
            LegendConnectResearchOutcomeState.UnresolvedConflict =>
                outcome.UnresolvedConflict?.Citations ?? [],
            LegendConnectResearchOutcomeState.InsufficientEvidence =>
                outcome.InsufficientEvidence?.Citations ?? [],
            _ => []
        };
        return terminalCitations.Select(item => item.CitationIdentity)
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(ordinalCitationPairs.Select(item => item.CitationIdentity));
    }

    private static bool ResearchCitationReceiptsMatch(
        LegendConnectResearchCitationValidationReceipt? left,
        LegendConnectResearchCitationValidationReceipt right) =>
        left is not null &&
        left.Succeeded == right.Succeeded &&
        string.Equals(left.PolicyIdentity, right.PolicyIdentity, StringComparison.Ordinal) &&
        left.MaterialClaimCount == right.MaterialClaimCount &&
        left.InlineCitationCount == right.InlineCitationCount &&
        left.ValidatedUtc == right.ValidatedUtc &&
        left.RejectionReasons.SequenceEqual(right.RejectionReasons, StringComparer.Ordinal);

    private static bool IsLearningMutationTool(string toolName) =>
        toolName is
            "legend_submit_machine_learning_candidate" or
            "legend_submit_founder_seed" or
            "legend_submit_founder_curriculum";

    private static string AppendLearningReceipt(
        string answer,
        string? receipt)
    {
        if (!string.IsNullOrWhiteSpace(receipt))
        {
            return answer.TrimEnd() +
                   "\n\nLEGEND_GOVERNED_LEARNING_RECEIPT\n" +
                   receipt.Trim();
        }

        return answer;
    }

    private static string MergeProviderAnswerSegment(
        string accumulated,
        string? segment)
    {
        var next = segment?.Trim();
        if (string.IsNullOrWhiteSpace(next))
            return accumulated;

        var current = accumulated.TrimEnd();
        if (current.Length == 0)
            return next;

        // A continuation can either resume at the exact boundary or restart
        // with the complete answer. Preserve every earlier section while
        // removing only text the provider demonstrably repeated.
        if (next.StartsWith(current, StringComparison.Ordinal))
            return next;

        if (current.EndsWith(next, StringComparison.Ordinal))
            return current;

        var maximumOverlap = Math.Min(
            Math.Min(current.Length, next.Length),
            8_192);

        for (var overlap = maximumOverlap; overlap > 0; overlap--)
        {
            if (current.AsSpan(current.Length - overlap)
                .SequenceEqual(next.AsSpan(0, overlap)))
            {
                return current + next[overlap..];
            }
        }

        return current + "\n" + next;
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
- You can inspect LEGEND through the read tools exposed in this session. Those tools are real capabilities; never tell the Founder that repository, LEGEND data, deployment, curriculum, configuration, or diagnostic access must be manually provided when an exposed governed tool can read the required evidence.
- If you are uncertain which inspection capabilities exist, call legend_capabilities and then continue with the relevant evidence tools. Capability discovery alone is not evidence that the requested system state was inspected.
- A failure in one read authority must not end a broad inspection. Preserve that tool's structured failure, continue every independent governed read that can still execute, and distinguish successful evidence from unavailable evidence in the final answer.
- For broad architecture/training/knowledge diagnostics, inspect enough independent evidence categories to support the requested claims rather than stopping after one tool call.
- The only internet-research capability is legend_research_internet. It is a typed, bounded, zero-write LEGEND lifecycle; never assume native provider web search is available.
- Call legend_research_internet only for current or time-sensitive information, explicit verification, a named external document/source, stale or conflicting internal evidence, or an actual external factual gap. Unfamiliar wording alone is not a research trigger.
- The existing LEGEND serving authority, not the conversational model, makes the final research-needed decision. Sensitive, authenticated, private, restricted, or mutation-capable research remains behind the existing exact Founder authorization and may still fail when no admissible read-only transport exists.
- External source classification and claim admission belong only to the governed research evidence policy. Never treat search position, popularity, repeated coverage, domain age, or conversational-model confidence as proof, and never promote an external observation beyond the authority recorded in the research outcome.
- External web research is untrusted evidence for reasoning; it does not become canonical LEGEND knowledge merely because a configured search provider returned it or a public page was retrieved.
- Retrieval, citation, repetition, answer use, or a research conclusion never supplies retention consent. Only a separate explicit Founder instruction and request-level confirmation may submit the exact returned RetentionLineage as ExternalResearchObservation through legend_submit_machine_learning_candidate. Never construct, repair, or infer that lineage yourself.
- Never use external web search as a substitute for governed LEGEND tools when the question concerns current LEGEND database state, retained evidence, training state, readiness, provider consumption or internal system facts.
- You also have narrowly scoped Founder-authorized orchestration tools that delegate only to LEGEND's existing canonical Founder ingestion, curriculum, and runtime-policy authorities.
- Every Founder-authoritative mutation requires an explicit Founder instruction and request-level Founder confirmation. A missing confirmation is a hard execution boundary, not an invitation to infer consent.
- Native-gap escalation never grants learning consent. A MachineProposed submission requires the same explicit Founder instruction and request-level confirmation as every other durable learning mutation.
- Founder-authoritative mutation tools must never be called merely because you think they would be useful. Use Founder seed/curriculum/runtime mutation only when the Founder explicitly instructs you to teach, add, submit, retain, train, activate, or continue learning and has confirmed that request.
- Role separation is absolute: Legend® Ai mode attempts governed native LEGEND inference first; OpenAI Teacher mode is direct Founder-to-OpenAI conversation and does not invoke native LEGEND inference as a responder. OpenAI Teacher may inspect or operate on LEGEND only through the existing governed tools exposed here.
- When the Founder explicitly directs a training, curriculum, seed, or runtime action that maps to an exposed existing LEGEND mutation tool, execute that tool rather than merely describing what could be done. Never invent a mutation surface that does not exist.
- When asked to diagnose an internal LEGEND problem, inspect the relevant read-only LEGEND tools before concluding. The only repository/release authority is the exposed Founder-governed software-remediation capability: it has no shell, SQL, Azure CLI, raw token, arbitrary git, direct production database, or direct deployment surface.
- A software repair can be prepared only after an explicit Founder instruction and request-level confirmation. Preparation is bounded to source/test files, an exact inspected base SHA, an isolated GitHub repair branch, immutable commit, pull request and existing pull-request CI. It must never merge or deploy.
- A release can be attempted only after a separate explicit Founder instruction and request-level confirmation naming the exact pull request and SHA. It must recheck that SHA, current required CI, protected-branch status checks, pull-request review protection and admin enforcement before GitHub itself accepts a merge. The existing protected-production workflow is the only deployment path.
- OpenAI Teacher may prepare a bounded repair through that capability when configured. Legend® Ai uses the same interface but must fail closed and escalate to OpenAI Teacher until a canonical governed software-repair competency is established. Never claim that code, GitHub state, or production state changed when no bounded tool performed that change.
- Founder-submitted source knowledge and curriculum are FounderApproved because the authenticated Founder explicitly directed the action.
- OpenAI-generated teaching is NOT automatically FounderApproved merely because it appears in conversation.
- Machine-derived teaching must continue through LEGEND's existing teacher, independent critic, canonical validator, curriculum admission, dataset compiler, challenger training, evaluation and promotion authorities.
- Before relying on general OpenAI recall for language knowledge, prefer the retained LEGEND context supplied with this request and use legend_search_retained_knowledge when deeper retrieval is useful.
- When the supplied retained context is sparse, ambiguous or contradicted, search retained knowledge again with narrower semantic queries before concluding that LEGEND lacks the knowledge.
- Prefer evidence synthesis over raw volume: combine high-authority retained records, relevant conversation state and narrowly selected governed tool results; do not repeat duplicate evidence merely because it is available.
- Retained authority precedence is: FounderApproved/HumanVerified → SystemValidatedMachine → other supported retained evidence → promoted LEGEND model state → unresolved MachineProposed/ProviderDerived evidence as clearly labeled observations → OpenAI reasoning for unresolved gaps.
- Rejected, contradicted, insufficient, failed or unresolved material remains auditable history but must never be presented as canonical truth.
- Never automatically retain personal facts, account data, private messages, casual conversation, transient business/system metrics or unsupported speculation as language knowledge.
- After an explicit Founder instruction and confirmation, submit at most one bounded machine-learning family for one coherent semantic distinction unless the Founder expressly directs multiple families.
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
Native LEGEND conversational inference is bypassed in this mode. You are not a second LEGEND responder and must never speak as though a native LEGEND answer was produced.

Your job is to:
- reason deeply about language acquisition, semantics, discourse, grammar, morphology, translation quality and curriculum strategy;
- act as the Founder's comprehensive diagnostic machine for LEGEND through the existing governed read authorities;
- inspect current LEGEND state whenever the Founder's request depends on current architecture, data, curriculum, retained knowledge, retrieval, training, evaluation, provider, repository, deployment, configuration, or operational evidence;
- identify weaknesses and propose high-quality teaching priorities;
- challenge assumptions;
- explain what evidence would be required;
- distinguish linguistic recommendations from established LEGEND knowledge.

You are explicitly NOT LEGEND itself.
You are explicitly NOT Founder authority.
When the authenticated Founder explicitly asks you to teach or train LEGEND and confirms that request, you must execute the matching existing governed training tool in this request rather than only returning instructions or proposed text. Use legend_submit_founder_seed for one exact Founder-authored source, legend_submit_founder_curriculum for explicit controlled curriculum, or legend_submit_machine_learning_candidate for lower-ranked OpenAI-derived teaching. Report the returned lifecycle state exactly; never call the material trained, canonical or production-ready unless later governed evidence proves it.
Machine-derived teaching must declare translation only for distinct language identities. Declare same_language_semantic for governed semantic teaching within one language, and use the reusable_semantic category identity for either capability. These declarations remain proposals until the existing critic, validator, and admission authorities accept them.
When the Founder explicitly directs and confirms a software repair, you may use only the bounded remediation tools to inspect the configured repository, prepare the exact repair branch/commit/pull request, and inspect CI. You may never merge, deploy, request credentials, invoke arbitrary commands, or broaden the requested patch. A separate explicit Founder approval is required for the exact tested SHA before release.
You may prepare a bounded MachineProposed teaching proposal, but you may submit it only after the Founder explicitly instructs and confirms that exact request. That action enters only MachineProposed state; report its returned state accurately and never describe it as canonical, approved, trained or promoted unless later LEGEND tools prove that transition.
""";
        }

        return governance + """

MODE: Legend® Ai

Speak as the conversational interface to LEGEND's governed intelligence.

You can converse naturally, reason, explain, synthesize and ask useful follow-up questions. When the Founder asks about your current LEGEND knowledge, weaknesses, models, evidence, readiness, provider dependence, coverage or learning status, inspect the real system through tools before answering.

For software remediation, use the same bounded capability interface only to inspect status, repository state, validation, release state, or deployment state. Do not prepare a repair unless a future canonical governed software-repair competency explicitly proves your knowledge is sufficient; the current capability must return a fail-closed escalation to OpenAI Teacher instead. Never substitute general language knowledge for that competency.

Use first-person language naturally when describing LEGEND, but distinguish:
- what LEGEND currently knows or has recorded;
- what you infer from the evidence;
- what OpenAI conversational reasoning is contributing;
- what remains only a proposed next action.

Never pretend that OpenAI conversational reasoning itself is canonical LEGEND knowledge.

Before external recall, use LEGEND's retained evidence when it is relevant. Treat unresolved machine/provider observations as evidence to reason about, never as truth.

Submit a bounded MachineProposed family through legend_submit_machine_learning_candidate only when the Founder explicitly directs and confirms the exact submission. Native failure, escalation, provider reasoning, a research result, or the presence of LEGEND_NATIVE_GAP_CONTEXT never supplies that consent. Declare ConversationObservation for conversational evidence. Declare ExternalResearchObservation only with the exact serialized RetentionLineage returned by the completed governed research session; never synthesize or alter it. Declare translation only for distinct language identities; declare same_language_semantic for governed semantic teaching within one language. Both require the reusable_semantic category identity. Every candidate must declare at least one language-neutral semantic_transitions source/result frame over its controlled example components; examples without a governed transition cannot close a native conversation gap and must not be reported as reusable learning.

When LEGEND_NATIVE_GAP_CONTEXT is supplied, the provider is acting as a diagnostic teacher because native LEGEND failed and explicitly allowed escalation. Inspect retained LEGEND evidence first. Identify a reusable distinction when the evidence supports one, but do not submit it unless the Founder separately and explicitly instructed and confirmed that mutation in this request. If evidence is insufficient, state the exact missing distinction and do not fabricate a proposal.
Never retain the one-off generated reply as a canned answer. Retain reusable meaning, semantic components, controlled contrasts, discourse behavior, and realization evidence that explain how the class of utterance should be understood and composed.
If retained evidence is insufficient or contradictory, do not fabricate curriculum. State the exact missing evidence/contrast so the Founder and existing autonomous learning authorities can resolve it.

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

    private static string BuildNativeDiagnosticTeachingContext(
        LegendConnectNativeInferenceSnapshot? nativeInference,
        string? nativeFailureDetail)
    {
        if (nativeInference is not { Supported: false, RequiresEscalation: true } &&
            string.IsNullOrWhiteSpace(nativeFailureDetail))
        {
            return string.Empty;
        }

        var reasonCode = string.IsNullOrWhiteSpace(nativeInference?.ReasonCode)
            ? "native_inference_unavailable"
            : LegendConnectTelemetry.NormalizeDiagnosticReason(nativeInference.ReasonCode);
        var authorityDetail = !string.IsNullOrWhiteSpace(nativeInference?.AuthoritySummary)
            ? NormalizeFailureDetail(nativeInference.AuthoritySummary)
            : "The native authority returned no additional governed summary.";
        var failureDetail = string.IsNullOrWhiteSpace(nativeFailureDetail)
            ? "No native execution exception was recorded."
            : NormalizeFailureDetail(nativeFailureDetail);
        var evidenceCount = nativeInference?.EvidenceCount ?? 0;

        return $"""

LEGEND_NATIVE_GAP_CONTEXT:
NativeReasonCode={reasonCode}
NativeAuthorityDetail={authorityDetail}
NativeEvidenceCount={evidenceCount}
NativeExecutionDetail={failureDetail}

DIAGNOSTIC TEACHER REQUIREMENTS:
- This turn reached external OpenAI under a provider-enabled request policy; native LEGEND did not supply an admissible answer. If NativeExecutionDetail reports unavailable language identification, no native meaning or record-scope classification completed.
- Diagnose the missing linguistic/semantic capability against retained LEGEND evidence before relying on general OpenAI recall.
- Use legend_search_retained_knowledge when a narrower query can distinguish an unknown component, ambiguous composition, missing transition, contradiction, realization gap, discourse gap, or production-eligibility gap.
- If governed evidence supports a reusable controlled semantic family that would reduce recurrence, describe that candidate and the evidence it would require. Do not submit it unless this request contains the Founder's explicit instruction and request-level confirmation.
- Preserve reusable semantics and controlled contrasts, not a generated response template.
- If a valid proposal cannot be supported, state precisely what governed evidence is missing instead of inventing it.
- MachineProposed retention is not canonical approval. The existing independent critic, validator, curriculum admission, evaluator, training, and promotion authorities remain mandatory.
""";
    }

    private static string BuildCasualInstructions(string mode) =>
        IsTeacherMode(mode)
            ? """
You are the external OpenAI Teacher speaking directly with the Founder.
Native LEGEND conversational inference is bypassed in this mode.
Respond naturally, directly, and conversationally.
Do not claim current LEGEND database, training, readiness, provider, evidence, repository, deployment, or system-state facts in this conversational path.
Do not invent internal state, private data, or operational results.
Use the available governed tools when current LEGEND facts are needed. Successful tool receipts support only the records and scope actually inspected. Disclose unavailable reads and do not infer private state from recollection.
"""
            : """
You are Legend® Ai speaking with the Founder.
Respond naturally, directly, and conversationally.
Use the product name exactly as "Legend® Ai" if you name yourself.
Do not claim current LEGEND database, training, readiness, provider, evidence, repository, deployment, or system-state facts in this conversational path.
Do not invent internal state, private data, or operational results.
Use the available governed tools when current LEGEND facts are needed. Successful tool receipts support only the records and scope actually inspected. Disclose unavailable reads and do not infer private state from recollection.
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
                    take: LegendFounderToolAuthority.ResolveRetainedKnowledgeTake(query),
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
        catch (AgentPortal.Security.ForbidResultException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "LEGEND RuntimeDiagnostic Event={Event} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ExceptionType={ExceptionType}",
                "RetainedKnowledgeFailed", "retained_knowledge", "failed", "retained_knowledge_unavailable", exception.GetType().Name);

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

        var target = totalCharacters <= 300_000
            ? 300_000
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



    private static int ResolveMaximumToolRounds(
        IReadOnlyList<LegendFounderAiChatMessage> conversation)
    {
        var totalCharacters = conversation.Sum(message => message.Content?.Length ?? 0);
        var userTurns = conversation.Count(message => string.Equals(message.Role, "user", StringComparison.Ordinal));
        var dynamicRounds = MinimumToolRounds + totalCharacters / 40_000 + userTurns / 4;
        return Math.Clamp(dynamicRounds, MinimumToolRounds, MaximumToolRounds);
    }

    internal static TimeSpan ResolveReadOnlyToolBudget(TimeSpan remaining)
    {
        var seconds = Math.Clamp(
            remaining.TotalSeconds / 4,
            MinimumReadOnlyToolSeconds,
            MaximumReadOnlyToolSeconds);
        return TimeSpan.FromSeconds(Math.Min(seconds,
            Math.Max(0, remaining.TotalSeconds - MinimumFinalSynthesisWindowSeconds)));
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

    private static bool TryNormalizeMode(
        string? mode,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(mode))
        {
            error =
                "Conversation mode is required. Select Legend® Ai or OpenAI Teacher.";
            return false;
        }

        normalized = mode.Trim().ToLowerInvariant();
        if (normalized is "legend" or "teacher")
            return true;

        normalized = string.Empty;
        error =
            "Conversation mode is invalid. Select Legend® Ai or OpenAI Teacher.";
        return false;
    }

    /// <summary>
    /// Governed inspection is a typed semantic decision. It is established only
    /// by the single governed meaning-graph classification for this request, so
    /// no substring, paraphrase, homonym or non-English surface form can force
    /// or suppress it. When the analysis itself was unavailable the request
    /// fails closed onto mandatory inspection rather than being answered from
    /// recollection.
    /// </summary>
    private static bool RequiresGovernedInspection(
        IReadOnlyList<LegendFounderAiChatMessage> conversation,
        string mode,
        LegendConnectOwnedRecordClassification? ownedRecordIntent)
    {
        var latest = conversation
            .Last(message => string.Equals(message.Role, "user", StringComparison.Ordinal))
            .Content?.Trim() ?? string.Empty;

        if (latest.Length == 0)
            return false;

        return ownedRecordIntent?.RequiresMandatoryGovernedInspection == true;
    }

    private static bool RequestsFounderLearningMutation(
        IReadOnlyList<LegendFounderAiChatMessage> conversation)
    {
        var latest = conversation
            .Last(message => string.Equals(message.Role, "user", StringComparison.Ordinal))
            .Content?.Trim()
            .ToLowerInvariant() ?? string.Empty;

        if (latest.Length == 0)
            return false;

        // Mutation authority must come from an actual action directed at the
        // learning system.  Do not infer a write from nouns such as
        // "training status" or from an inspection request that merely
        // discusses teaching.
        var directsLegendTraining = Regex.IsMatch(
            latest,
            @"\b(?:teach|train)\s+(?:legend(?:®)?|the\s+(?:legend(?:®)?\s+)?(?:curriculum|system))\b",
            RegexOptions.CultureInvariant);
        var learningAction =
            directsLegendTraining ||
            Regex.IsMatch(
                latest,
                @"\b(?:submit|retain|add)\b",
                RegexOptions.CultureInvariant);

        var learningSubject =
            latest.Contains("legend", StringComparison.Ordinal) ||
            latest.Contains("curriculum", StringComparison.Ordinal) ||
            latest.Contains("machineproposed", StringComparison.Ordinal) ||
            latest.Contains("machine proposed", StringComparison.Ordinal) ||
            latest.Contains("learning candidate", StringComparison.Ordinal) ||
            latest.Contains("training", StringComparison.Ordinal);

        return learningAction && learningSubject;
    }

    private static bool ShouldAttemptNativeInference(string mode) =>
        string.Equals(mode, "legend", StringComparison.Ordinal);

    private static bool RequiresProviderGovernedInspection(
        IReadOnlyList<LegendFounderAiChatMessage> conversation,
        string mode,
        LegendConnectOwnedRecordClassification? ownedRecordIntent,
        LegendConnectNativeInferenceSnapshot? nativeInference,
        string? nativeFailureDetail) =>
        RequiresGovernedInspection(conversation, mode, ownedRecordIntent) ||
        nativeInference is { Supported: false, RequiresEscalation: true } ||
        !string.IsNullOrWhiteSpace(nativeFailureDetail);

    private static string ResolveReasoningEffortForRound(
        int round,
        bool requiresGovernedInspection,
        string configuredEffort) =>
        !requiresGovernedInspection
            ? "low"
            : round == 0
                ? "low"
                : configuredEffort;

    private static string ResolveToolChoice(
        bool allowTools,
        bool requireToolCall) =>
        !allowTools
            ? "none"
            : requireToolCall
                ? "required"
                : "auto";

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

    private static int ReadRequiredInt(
        JsonElement root,
        string propertyName) =>
        root.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : 0;

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

    /// <summary>
    /// The provider requested one of the existing governed Founder tools but
    /// the tool could not finish within its authority/budget boundary.  The
    /// exception intentionally carries only a safe classification; raw SQL,
    /// transport, and implementation details remain in server logs.
    /// </summary>
    private sealed class LegendFounderAiToolExecutionException
        : Exception
    {
        public LegendFounderAiToolExecutionException(
            string tool,
            string reason,
            string failureKind)
            : base($"Governed Founder tool '{tool}' could not complete.")
        {
            Tool = tool;
            Reason = reason;
            FailureKind = failureKind;
        }

        public string Tool { get; }

        public string Reason { get; }

        public string FailureKind { get; }
    }

    /// <summary>
    /// The typed result category of governed source-language resolution. The
    /// caller routes on this category; the accompanying reason stays the exact
    /// detail for observability and never becomes the routing key.
    /// </summary>
    internal enum FounderAiSourceLanguageOutcome
    {
        Resolved,
        SemanticAmbiguity,
        UnsupportedLanguage,
        InvalidDeclaration,
        ProviderPolicyBlocked,
        TransientIdentificationUnavailable
    }

    internal sealed record FounderAiSourceLanguageResolution(
        bool Succeeded,
        string? LanguageCode,
        string Reason,
        FounderAiSourceLanguageOutcome Outcome)
    {
        /// <summary>
        /// Only a transient outage retains provider-enabled external responder
        /// permission without establishing native meaning. Ambiguity, an unsupported
        /// language, and an invalid declared code are semantic authority
        /// results that fail closed in every mode.
        /// </summary>
        internal bool IsTransientIdentificationOutage =>
            Outcome ==
            FounderAiSourceLanguageOutcome.TransientIdentificationUnavailable;

        internal static FounderAiSourceLanguageResolution Success(
            string languageCode) =>
            new(
                true,
                languageCode,
                string.Empty,
                FounderAiSourceLanguageOutcome.Resolved);

        internal static FounderAiSourceLanguageResolution Failure(
            FounderAiSourceLanguageOutcome outcome,
            string reason) =>
            new(false, null, reason, outcome);
    }

}

public sealed record LegendFounderAiProgressEvent(
    string Stage,
    string Message,
    int? Round = null,
    string? Tool = null,
    string? ScopeIdentity = null);

public sealed record LegendFounderAiChatMessage(
    string? Role,
    string? Content);

public sealed class LegendFounderAiChatRequest
{
    public string? Mode { get; init; }

    /// <summary>
    /// Explicit governed language identity supplied only when the caller
    /// actually knows it. When absent, the conversation service uses the
    /// existing governed identification and registry authorities before any
    /// meaning-graph analysis. No language is inferred from a client default.
    /// </summary>
    [JsonPropertyName("sourceLanguageCode")]
    public string? SourceLanguageCode { get; init; }

    /// <summary>
    /// Founder-selected hard boundary for direct LEGEND testing. When true in
    /// Legend® Ai mode, unsupported native inference fails closed before any
    /// OpenAI configuration or provider request is accessed.
    /// </summary>
    public bool NativeOnly { get; init; }

    /// <summary>
    /// One-request confirmation supplied by the authenticated Founder UI for
    /// a deliberate governed mutation. It is never persisted or inferred
    /// from provider output, and defaults to false for every request.
    /// </summary>
    public bool FounderCommandConfirmed { get; init; }

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
    string? Reference = null,
    string ResponseAuthority = "SystemDiagnostic",
    string? Stage = null,
    string? Reason = null,
    string? OperationId = null,
    IReadOnlyList<string>? CompletedWork = null,
    IReadOnlyList<string>? RemainingWork = null,
    bool Resumable = false,
    string? ModelAssistanceState = null,
    string? ModelAssistanceReason = null,
    string? ModelVersion = null,
    Guid? ModelTrainingRunId = null,
    string? ModelProvenance = null,
    LegendConnectResearchEvidenceOrigin EvidenceOrigin =
        LegendConnectResearchEvidenceOrigin.UnresolvedEvidence,
    LegendConnectResearchOutcome? ResearchOutcome = null,
    IReadOnlyList<LegendConnectGovernedScheduleCertificateSnapshot>? ScheduleCertificates = null,
    IReadOnlyList<string>? ReasoningTransitionPath = null)
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
            reference,
            "SystemDiagnostic");

    public static LegendFounderAiChatResponse InvalidMode(
        string error) =>
        new(
            false,
            "invalid",
            null,
            error,
            "validation",
            null,
            null,
            "NoResponder",
            "mode_validation",
            "invalid_mode");

    public static LegendFounderAiChatResponse ModeFailure(
        string mode,
        string error,
        string failureKind,
        string stage,
        string reason,
        int? providerStatusCode = null,
        string? reference = null) =>
        new(
            false,
            mode,
            null,
            error,
            failureKind,
            providerStatusCode,
            reference,
            string.Equals(mode, "teacher", StringComparison.Ordinal)
                ? "OpenAITeacher"
                : "SystemDiagnostic",
            stage,
            reason);

    public static LegendFounderAiChatResponse UnexpectedFailure(
        string? requestedMode) =>
        string.Equals(
            requestedMode?.Trim(),
            "teacher",
            StringComparison.OrdinalIgnoreCase)
            ? ModeFailure(
                "teacher",
                "OpenAI Teacher could not complete this request. Stage=unhandled; Reason=unexpected_execution_failure.",
                "governed_execution",
                "unhandled",
                "unexpected_execution_failure")
            : string.Equals(
                requestedMode?.Trim(),
                "legend",
                StringComparison.OrdinalIgnoreCase)
                ? ModeFailure(
                    "legend",
                    "Legend® Ai could not complete this request. Stage=unhandled; Reason=unexpected_execution_failure.",
                    "governed_execution",
                    "unhandled",
                    "unexpected_execution_failure")
                : InvalidMode(
                    "Conversation mode is invalid. Select Legend® Ai or OpenAI Teacher.");
}
