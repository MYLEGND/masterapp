using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPortal.Services.Analytics;
using Domain.Messaging;

namespace AgentPortal.Services;

/// <summary>
/// Provider-neutral authority for the Founder tool catalog and execution.
/// Conversational providers may request a tool, but only this authority
/// classifies and executes it through the existing governed LEGEND services.
/// </summary>
internal sealed class LegendFounderToolAuthority
{
    private const int MinimumSemanticFrameDimensions = 1;
    private const int MaximumSemanticFrameDimensions = 12;
    private const int MaximumSemanticFrameDimensionLength = 80;
    private const int MaximumSemanticFrameValueLength = 160;
    private const int MaximumNativeReadArgumentsCharacters = 4096;
    private const int MaximumNativeReadOutputCharacters = 1_000_000;
    private const int MaximumNativeReadQueryCharacters = 500;

    private static readonly string[] RequiredFunctionToolProperties =
    [
        "type",
        "name",
        "description",
        "parameters",
        "strict"
    ];

    private readonly FounderLegendConnectService _legend;
    private readonly IFounderSoftwareRemediationService? _softwareRemediation;
    private readonly HashSet<string> _consumedMutationAuthorizations =
        new(StringComparer.Ordinal);
    private readonly object _mutationAuthorizationLock = new();

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

    internal LegendFounderToolAuthority(
        FounderLegendConnectService legend,
        IFounderSoftwareRemediationService? softwareRemediation)
    {
        _legend = legend;
        _softwareRemediation = softwareRemediation;
    }

    internal IReadOnlyList<object> Tools => BuildFounderTools();

    internal IReadOnlyList<object> Capabilities =>
        DescribeFounderCapabilities();

    internal bool IsReadOnly(string name) =>
        IsReadOnlyFounderTool(name);

    internal bool IsGovernedEvidence(string name) =>
        IsGovernedEvidenceTool(name);

    internal bool IsNativeContentBindingRead(string name) =>
        IsNativeContentBindingReadTool(name);

    private static bool IsGovernedEvidenceTool(string name) =>
        IsReadOnlyFounderTool(name) &&
        name is not "legend_capabilities" and not "legend_research_internet";

    // This is a classification inside the one executable registry, not a
    // second registry. Native binding is restricted to bounded LEGEND data
    // reads and deliberately excludes repository/remediation, Azure/provider,
    // web-search, mutation, and capability-discovery tools.
    private static bool IsNativeContentBindingReadTool(string name) =>
        name is
            "legend_language_knowledge" or
            "legend_pair_health" or
            "legend_translation_quality" or
            "legend_target_realizations" or
            "legend_search_retained_knowledge" or
            "legend_research_internet" or
            "legend_metric_detail" or
            "legend_language_state";

    private static bool IsReadOnlyFounderTool(
        string name) =>
        name is
            "legend_capabilities" or
            "legend_software_remediation_status" or
            "legend_inspect_repository" or
            "legend_inspect_repair_validation" or
            "legend_request_repair_release" or
            "legend_verify_repair_deployment" or
            "legend_system_overview" or
            "legend_operational_diagnostics" or
            "legend_provider_capacity" or
            "legend_language_knowledge" or
            "legend_pair_health" or
            "legend_translation_quality" or
            "legend_target_realizations" or
            "legend_search_retained_knowledge" or
            "legend_metric_detail" or
            "legend_language_state";

    /// <summary>
    /// Binds one scalar through the same Founder authorization and tool
    /// executor used by provider calls. The selected semantic frame supplies
    /// every input; native reasoning cannot choose a tool or inspect the full
    /// output payload.
    /// </summary>
    internal async Task<LegendConnectReadOnlyContentBindingResult>
        BindReadOnlyResultAsync(
            ClaimsPrincipal founder,
            LegendConnectReadOnlyContentBindingRequest request,
            CancellationToken cancellationToken)
    {
        if (!TryResolveFounderFunctionParameters(
                request.ToolName,
                out var parameterSchema))
            return new(false, "read_only_content_binding_tool_unavailable", null);
        if (!IsReadOnlyFounderTool(request.ToolName))
            return new(false, "read_only_content_binding_tool_not_read_only", null);
        if (!IsNativeContentBindingReadTool(request.ToolName))
            return new(false, "read_only_content_binding_tool_not_permitted", null);
        if (!TryValidateNativeReadArguments(
                request,
                parameterSchema,
                out var argumentsReason))
            return new(false, argumentsReason, null);

        var output = await ExecuteAsync(
            founder,
            new FounderAiToolCall(
                request.RequestIdentity,
                request.ToolName,
                request.ArgumentsJson),
            "legend",
            cancellationToken);
        return TryCreateReadOnlyContentBindingReceipt(
            request,
            output,
            DateTime.UtcNow,
            out var receipt,
            out var receiptReason)
                ? new(true, "read_only_content_binding_receipt_governed", receipt)
                : new(false, receiptReason, null);
    }

    /// <summary>
    /// The single authorization boundary for canonical internet research.
    /// Public research is authenticated, bounded, read-only and zero-write.
    /// Every other access class consumes the exact existing one-request
    /// Founder authorization before the operations authority can execute it.
    /// </summary>
    internal async Task<LegendConnectResearchOutcome> ResearchAsync(
        ClaimsPrincipal founder,
        string question,
        string sourceLanguageCode,
        LegendConnectNativeInferenceSnapshot? internalInference,
        FounderAiMutationAuthorization? restrictedAuthorization,
        CancellationToken cancellationToken)
    {
        var decision = internalInference?.ResearchDecision ??
            await _legend.DecideResearchNeededAsync(
                founder,
                question,
                sourceLanguageCode,
                internalInference,
                cancellationToken);
        var requestId = Guid.NewGuid();
        if (!decision.ResearchRequired)
        {
            return ResearchFailure(
                requestId,
                decision,
                "research_not_authorized_by_serving_decision",
                decision.InternalKnowledgeAvailable
                    ? "LEGEND did not use the internet because existing governed knowledge answers this request."
                    : "LEGEND did not use the internet because the governed decision found no research trigger.",
                decision.InternalKnowledgeAvailable
                    ? LegendConnectResearchEvidenceOrigin.InternalKnowledge
                    : LegendConnectResearchEvidenceOrigin.UnresolvedEvidence);
        }

        LegendConnectResearchAuthorization authorization;
        if (decision.AccessClass == LegendConnectResearchAccessClass.PublicReadOnly)
        {
            await _legend.EnsureFounderAuthorizedAsync(
                founder,
                cancellationToken);
            authorization = new LegendConnectResearchAuthorization(
                true,
                LegendConnectResearchContracts.PublicAuthorizationProvenance,
                null,
                decision.AccessClass,
                true,
                true);
        }
        else
        {
            var authorizationFailure =
                await TryConsumeMutationAuthorizationAsync(
                    founder,
                    restrictedAuthorization,
                    cancellationToken);
            if (authorizationFailure is not null)
            {
                return ResearchFailure(
                    requestId,
                    decision,
                    ReadFailureCode(
                        authorizationFailure,
                        "research_restricted_authorization_required"),
                    "LEGEND did not execute restricted research because the exact existing Founder authorization was absent, invalid, or already consumed.",
                    LegendConnectResearchEvidenceOrigin.UnresolvedEvidence);
            }

            authorization = new LegendConnectResearchAuthorization(
                true,
                LegendConnectResearchContracts.RestrictedAuthorizationProvenance,
                restrictedAuthorization!.CorrelationId,
                decision.AccessClass,
                true,
                true);
        }

        var normalizedQuestion = question.Trim();
        if (normalizedQuestion.Length >
            LegendConnectResearchContracts.MaximumQueryCharacters)
        {
            normalizedQuestion = normalizedQuestion[
                ..LegendConnectResearchContracts.MaximumQueryCharacters];
        }
        var queryIdentity = LegendLanguageIdentity.TextHash(
            "legend-research-query|v1|" +
            decision.SourceLanguageCode + "|" +
            normalizedQuestion);
        var request = new LegendConnectResearchRequest(
            requestId,
            normalizedQuestion,
            decision,
            [
                new LegendConnectBoundedSearchQuery(
                    queryIdentity,
                    1,
                    normalizedQuestion,
                    decision.SourceLanguageCode,
                    LegendConnectResearchContracts.MaximumResults)
            ],
            LegendConnectResearchContracts.MaximumResults,
            LegendConnectResearchContracts.MaximumDocuments,
            LegendConnectResearchContracts.MaximumClaims,
            LegendConnectResearchContracts.MaximumDocumentCharacters,
            decision.Need == LegendConnectResearchNeed.NamedExternalDocumentOrSource
                ? 1
                : 2,
            authorization,
            internalInference is { Supported: true }
                ? BoundResearchInternalAnswer(internalInference.Answer)
                : null,
            internalInference?.ReasonCode,
            internalInference?.EvidenceCount ?? 0,
            DateTime.UtcNow);
        return await _legend.ExecuteResearchAsync(
            founder,
            request,
            cancellationToken);
    }


    internal async Task<string> ExecuteAsync(
        ClaimsPrincipal founder,
        FounderAiToolCall call,
        string mode,
        CancellationToken cancellationToken)
    {
        if (!IsReadOnlyFounderTool(call.Name))
        {
            var authorizationFailure = await TryConsumeMutationAuthorizationAsync(
                founder,
                call.MutationAuthorization,
                cancellationToken);
            if (authorizationFailure is not null)
                return authorizationFailure;
        }

        switch (call.Name)
        {
            case "legend_capabilities":
            {
                return SerializeUnbounded(DescribeFounderCapabilities());
            }

            case "legend_software_remediation_status":
            {
                if (_softwareRemediation is null)
                    return SerializeUnbounded(SoftwareRemediationNotAvailable());

                return SerializeUnbounded(
                    await _softwareRemediation.GetStatusAsync(cancellationToken));
            }

            case "legend_inspect_repository":
            {
                if (_softwareRemediation is null)
                    return SerializeUnbounded(SoftwareRemediationNotAvailable());

                using var arguments = JsonDocument.Parse(call.Arguments);
                return SerializeUnbounded(
                    await _softwareRemediation.InspectRepositoryAsync(
                        ReadOptionalString(arguments.RootElement, "path"),
                        ReadOptionalString(arguments.RootElement, "git_reference"),
                        cancellationToken));
            }

            case "legend_prepare_software_repair":
            {
                if (_softwareRemediation is null)
                    return SerializeUnbounded(SoftwareRemediationNotAvailable());

                using var arguments = JsonDocument.Parse(call.Arguments);
                var baseSha = ReadRequiredString(arguments.RootElement, "base_sha");
                var title = ReadRequiredString(arguments.RootElement, "title");
                var summary = ReadRequiredString(arguments.RootElement, "summary");
                if (string.IsNullOrWhiteSpace(baseSha) ||
                    string.IsNullOrWhiteSpace(title) ||
                    string.IsNullOrWhiteSpace(summary))
                {
                    return "{\"error\":\"invalid_repair_proposal\",\"detail\":\"An exact base SHA, title, and summary are required.\"}";
                }

                if (!arguments.RootElement.TryGetProperty("changes", out var changesElement) ||
                    changesElement.ValueKind != JsonValueKind.Array)
                {
                    return "{\"error\":\"invalid_repair_proposal\",\"detail\":\"At least one bounded source or test file change is required.\"}";
                }

                var changes = new List<FounderSoftwareRepairChange>();
                foreach (var change in changesElement.EnumerateArray())
                {
                    if (change.ValueKind != JsonValueKind.Object)
                        return "{\"error\":\"invalid_repair_proposal\"}";

                    var path = ReadRequiredString(change, "path");
                    var content = ReadRequiredString(change, "content");
                    if (string.IsNullOrWhiteSpace(path) || content is null)
                        return "{\"error\":\"invalid_repair_proposal\"}";

                    changes.Add(new FounderSoftwareRepairChange(path, content));
                }

                return SerializeUnbounded(
                    await _softwareRemediation.PrepareAsync(
                        mode,
                        new FounderSoftwareRepairProposal(baseSha, title, summary, changes),
                        cancellationToken));
            }

            case "legend_inspect_repair_validation":
            {
                if (_softwareRemediation is null)
                    return SerializeUnbounded(SoftwareRemediationNotAvailable());

                using var arguments = JsonDocument.Parse(call.Arguments);
                var headSha = ReadRequiredString(arguments.RootElement, "head_sha");
                if (string.IsNullOrWhiteSpace(headSha))
                    return "{\"error\":\"invalid_release_identity\"}";
                return SerializeUnbounded(
                    await _softwareRemediation.InspectValidationAsync(
                        ReadRequiredInt(arguments.RootElement, "pull_request_number"),
                        headSha,
                        cancellationToken));
            }

            case "legend_request_repair_release":
            {
                if (_softwareRemediation is null)
                    return SerializeUnbounded(SoftwareRemediationNotAvailable());

                using var arguments = JsonDocument.Parse(call.Arguments);
                var headSha = ReadRequiredString(arguments.RootElement, "head_sha");
                if (string.IsNullOrWhiteSpace(headSha))
                    return "{\"error\":\"invalid_release_identity\"}";
                return SerializeUnbounded(
                    await _softwareRemediation.RequestReleaseAsync(
                        ReadRequiredInt(arguments.RootElement, "pull_request_number"),
                        headSha,
                        cancellationToken));
            }

            case "legend_release_approved_repair":
            {
                if (_softwareRemediation is null)
                    return SerializeUnbounded(SoftwareRemediationNotAvailable());

                using var arguments = JsonDocument.Parse(call.Arguments);
                var headSha = ReadRequiredString(arguments.RootElement, "head_sha");
                if (string.IsNullOrWhiteSpace(headSha))
                    return "{\"error\":\"invalid_release_identity\"}";
                return SerializeUnbounded(
                    await _softwareRemediation.ReleaseApprovedAsync(
                        ReadRequiredInt(arguments.RootElement, "pull_request_number"),
                        headSha,
                        cancellationToken));
            }

            case "legend_verify_repair_deployment":
            {
                if (_softwareRemediation is null)
                    return SerializeUnbounded(SoftwareRemediationNotAvailable());

                using var arguments = JsonDocument.Parse(call.Arguments);
                var commitSha = ReadRequiredString(arguments.RootElement, "commit_sha");
                if (string.IsNullOrWhiteSpace(commitSha))
                    return "{\"error\":\"invalid_deployment_identity\"}";
                return SerializeUnbounded(
                    await _softwareRemediation.VerifyDeploymentAsync(
                        commitSha,
                        cancellationToken));
            }

            case "legend_system_overview":
            {
                var snapshot =
                    await _legend.GetLiveMetricsAsync(
                        founder,
                        cancellationToken);

                return SerializeUnbounded(snapshot);
            }

            case "legend_operational_diagnostics":
            {
                // Preserve the current production readiness projection: the
                // diagnostic must expose the governing readiness authority,
                // rather than infer a claim decision from aggregate metrics.
                var state = await _legend.GetLanguageStateAsync(
                    founder,
                    "en",
                    null,
                    cancellationToken);
                var capacity = await _legend.GetProviderCapacityAsync(
                    founder,
                    cancellationToken);
                return SerializeUnbounded(new
                {
                    state.RuntimePolicy,
                    state.ProductionReadiness,
                    ProviderCapacity = capacity,
                    acquisitionContract = new
                    {
                        queueAuthority = "LegendCorpusCandidate",
                        downstreamLearningEvent = "LegendTranslationLearningEvent",
                        rule = "Approved candidates are claimed directly by the existing acquisition worker. Learning events are downstream results, not a prerequisite job queue.",
                        safety = "BLOCKED or DEGRADED readiness intentionally prevents new acquisition claims. Never bypass historical convergence, worker health, capacity, live reserve, or language-registry gates."
                    }
                });
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

            case "legend_research_internet":
            {
                using var arguments = JsonDocument.Parse(call.Arguments);
                var question = ReadRequiredString(
                    arguments.RootElement,
                    "question");
                var sourceLanguage = ReadRequiredString(
                    arguments.RootElement,
                    "source_language");
                if (string.IsNullOrWhiteSpace(question) ||
                    string.IsNullOrWhiteSpace(sourceLanguage))
                {
                    return """{"error":"research_question_and_language_required"}""";
                }

                return SerializeUnbounded(
                    await ResearchAsync(
                        founder,
                        question,
                        sourceLanguage,
                        internalInference: null,
                        call.MutationAuthorization,
                        cancellationToken));
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

                var capabilityIdentity =
                    ReadRequiredString(
                        arguments.RootElement,
                        "capability_identity");

                var categoryIdentity =
                    ReadRequiredString(
                        arguments.RootElement,
                        "category_identity");

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
                        capabilityIdentity) ||
                    string.IsNullOrWhiteSpace(
                        categoryIdentity) ||
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

                if (!TryReadMachineSemanticTransitions(
                        arguments.RootElement,
                        out var semanticTransitions))
                {
                    return """{"error":"invalid_machine_learning_semantic_transitions"}""";
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
                                examples,
                                semanticTransitions,
                                capabilityIdentity,
                                categoryIdentity),
                            cancellationToken);

                if (!result.Succeeded)
                    return SerializeUnbounded(result);
                if (result.ProposalAlreadyExisted)
                {
                    return MutationFailure(
                        "machine_learning_mutation_replay",
                        "The exact MachineProposed mutation already exists and was not accepted as a new authorized success receipt.");
                }
                if (result.CorpusCandidateId is not { } candidateId ||
                    candidateId == Guid.Empty ||
                    result.ProposalId is not { } proposalId ||
                    proposalId == Guid.Empty ||
                    candidateId == proposalId ||
                    string.IsNullOrWhiteSpace(result.State) ||
                    result.State is not ("AwaitingCritic" or "InsufficientEvidence") ||
                    call.MutationAuthorization is null)
                {
                    return MutationFailure(
                        "machine_learning_mutation_receipt_incomplete",
                        "The governed learning authority did not return the complete durable identity required for a success receipt.");
                }

                return SerializeUnbounded(
                    new LegendConnectMachineTeachingMutationReceipt(
                        true,
                        candidateId,
                        proposalId,
                        result.State,
                        LegendConnectMachineTeachingMutationReceipt.RequiredProvenance,
                        call.MutationAuthorization.CorrelationId,
                        LegendConnectMachineTeachingMutationReceipt.RequiredServingStatus,
                        LegendConnectMachineTeachingMutationReceipt.RequiredCanonicalStatus));
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

    private async Task<string?> TryConsumeMutationAuthorizationAsync(
        ClaimsPrincipal founder,
        FounderAiMutationAuthorization? authorization,
        CancellationToken cancellationToken)
    {
        if (authorization is null)
        {
            return MutationFailure(
                "founder_command_confirmation_required",
                "This durable LEGEND mutation was not executed. The authenticated Founder must explicitly confirm the action for this request.");
        }
        if (!Guid.TryParseExact(authorization.CorrelationId, "N", out _))
        {
            return MutationFailure(
                "founder_mutation_authorization_invalid",
                "The Founder mutation authorization correlation was malformed.");
        }

        await _legend.EnsureFounderAuthorizedAsync(founder, cancellationToken);

        lock (_mutationAuthorizationLock)
        {
            if (!_consumedMutationAuthorizations.Add(authorization.CorrelationId))
            {
                return MutationFailure(
                    "founder_mutation_authorization_replayed",
                    "The one-request Founder mutation authorization was already consumed.");
            }
        }
        return null;
    }

    private static string MutationFailure(string error, string detail) =>
        JsonSerializer.Serialize(
            new
            {
                succeeded = false,
                error,
                detail
            },
            JsonOptions);

    private static LegendConnectResearchOutcome ResearchFailure(
        Guid requestId,
        LegendConnectResearchNeededDecision decision,
        string reasonCode,
        string detail,
        LegendConnectResearchEvidenceOrigin origin)
    {
        var now = DateTime.UtcNow;
        var sessionId = Guid.NewGuid();
        var session = new LegendConnectResearchSession(
            sessionId,
            requestId,
            now,
            now,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            0,
            null,
            "Failure",
            reasonCode);
        var provenance = new LegendConnectResearchProvenance(
            requestId,
            sessionId,
            decision.ReasonCode,
            decision.SourceLanguageCode,
            LegendLanguageIdentity.TextHash(string.Empty),
            now,
            origin,
            null,
            0,
            "Unavailable",
            null,
            "Unavailable",
            [],
            [],
            [],
            [],
            [],
            [],
            now,
            now,
            0,
            null,
            "Unavailable",
            "Unavailable",
            null,
            true,
            true,
            LegendConnectResearchContracts.Provenance);
        return new LegendConnectResearchOutcome(
            LegendConnectResearchOutcomeState.Failure,
            origin,
            decision,
            session,
            null,
            null,
            null,
            new LegendConnectResearchFailureResult(
                reasonCode,
                detail,
                false),
            provenance);
    }

    private static string ReadFailureCode(
        string json,
        string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(error.GetString())
                ? error.GetString()!
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static string? BoundResearchInternalAnswer(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        return normalized.Length <= 8_000
            ? normalized
            : normalized[..8_000];
    }

    private static bool TryResolveFounderFunctionParameters(
        string name,
        out JsonElement parameters)
    {
        parameters = default;
        foreach (var tool in BuildFounderTools())
        {
            using var document = JsonSerializer.SerializeToDocument(tool, JsonOptions);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "function", StringComparison.Ordinal) &&
                root.TryGetProperty("name", out var declaredName) &&
                string.Equals(declaredName.GetString(), name, StringComparison.Ordinal) &&
                root.TryGetProperty("parameters", out var declaredParameters) &&
                declaredParameters.ValueKind == JsonValueKind.Object)
            {
                parameters = declaredParameters.Clone();
                return true;
            }
        }
        return false;
    }

    private static bool TryValidateNativeReadArguments(
        LegendConnectReadOnlyContentBindingRequest request,
        JsonElement parameterSchema,
        out string reasonCode)
    {
        reasonCode = "read_only_content_binding_arguments_governed";
        if (string.IsNullOrWhiteSpace(request.ArgumentsJson) ||
            request.ArgumentsJson.Length > MaximumNativeReadArgumentsCharacters)
        {
            reasonCode = "read_only_content_binding_arguments_invalid";
            return false;
        }

        try
        {
            using var arguments = JsonDocument.Parse(request.ArgumentsJson);
            var root = arguments.RootElement;
            if (!IsStrictSchemaInstance(parameterSchema, root))
            {
                reasonCode = "read_only_content_binding_arguments_invalid";
                return false;
            }

            // The exact catalog remains the contract authority. These are
            // narrower native-serving bounds on values that are intentionally
            // broader for Founder/provider inspection.
            var valid = request.ToolName switch
            {
                "legend_language_knowledge" =>
                    IsBoundedLanguage(ReadRequiredString(root, "language")),
                "legend_search_retained_knowledge" =>
                    IsBoundedString(
                        ReadRequiredString(root, "query"),
                        1,
                        MaximumNativeReadQueryCharacters) &&
                    IsOptionalBoundedLanguage(root, "source_language") &&
                    IsOptionalBoundedLanguage(root, "target_language"),
                "legend_language_state" =>
                    IsBoundedLanguage(ReadRequiredString(root, "language")),
                _ => true
            };
            if (!valid)
                reasonCode = "read_only_content_binding_arguments_invalid";
            return valid;
        }
        catch (JsonException)
        {
            reasonCode = "read_only_content_binding_arguments_invalid";
            return false;
        }
    }

    private static bool IsStrictSchemaInstance(
        JsonElement schema,
        JsonElement instance)
    {
        if (!schema.TryGetProperty("type", out var declaredTypes) ||
            !MatchesDeclaredSchemaType(declaredTypes, instance))
        {
            return false;
        }
        if (instance.ValueKind == JsonValueKind.Null)
            return true;

        if (schema.TryGetProperty("enum", out var declaredEnum) &&
            !declaredEnum.EnumerateArray().Any(item =>
                string.Equals(
                    item.GetRawText(),
                    instance.GetRawText(),
                    StringComparison.Ordinal)))
        {
            return false;
        }

        return instance.ValueKind switch
        {
            JsonValueKind.Object =>
                IsStrictObjectSchemaInstance(schema, instance),
            JsonValueKind.Array =>
                IsStrictArraySchemaInstance(schema, instance),
            JsonValueKind.String =>
                IsStrictStringSchemaInstance(schema, instance.GetString() ?? string.Empty),
            JsonValueKind.Number =>
                IsStrictNumberSchemaInstance(schema, instance),
            JsonValueKind.True or JsonValueKind.False => true,
            _ => false
        };
    }

    private static bool MatchesDeclaredSchemaType(
        JsonElement declaredTypes,
        JsonElement instance)
    {
        var instanceType = instance.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(instanceType))
            return false;

        bool Matches(JsonElement declaredType)
        {
            if (declaredType.ValueKind != JsonValueKind.String)
                return false;
            var value = declaredType.GetString();
            if (string.Equals(value, instanceType, StringComparison.Ordinal))
                return true;
            return string.Equals(value, "integer", StringComparison.Ordinal) &&
                instance.ValueKind == JsonValueKind.Number &&
                instance.TryGetInt64(out _);
        }

        return declaredTypes.ValueKind == JsonValueKind.String
            ? Matches(declaredTypes)
            : declaredTypes.ValueKind == JsonValueKind.Array &&
              declaredTypes.EnumerateArray().Any(Matches);
    }

    private static bool IsStrictObjectSchemaInstance(
        JsonElement schema,
        JsonElement instance)
    {
        if (HasDuplicateProperties(instance) ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var actualNames = instance.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var declaredNames = properties.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualNames.SetEquals(declaredNames))
            return false;

        foreach (var property in properties.EnumerateObject())
        {
            if (!instance.TryGetProperty(property.Name, out var value) ||
                !IsStrictSchemaInstance(property.Value, value))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsStrictArraySchemaInstance(
        JsonElement schema,
        JsonElement instance)
    {
        var length = instance.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var minimum) &&
            (!minimum.TryGetInt32(out var minimumValue) || length < minimumValue))
        {
            return false;
        }
        if (schema.TryGetProperty("maxItems", out var maximum) &&
            (!maximum.TryGetInt32(out var maximumValue) || length > maximumValue))
        {
            return false;
        }
        return schema.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Object &&
            instance.EnumerateArray().All(item => IsStrictSchemaInstance(items, item));
    }

    private static bool IsStrictStringSchemaInstance(
        JsonElement schema,
        string instance)
    {
        if (instance.Any(char.IsControl))
            return false;
        if (schema.TryGetProperty("minLength", out var minimum) &&
            (!minimum.TryGetInt32(out var minimumValue) || instance.Length < minimumValue))
        {
            return false;
        }
        if (schema.TryGetProperty("maxLength", out var maximum) &&
            (!maximum.TryGetInt32(out var maximumValue) || instance.Length > maximumValue))
        {
            return false;
        }
        return true;
    }

    private static bool IsStrictNumberSchemaInstance(
        JsonElement schema,
        JsonElement instance)
    {
        if (!instance.TryGetDecimal(out var value))
            return false;
        if (schema.TryGetProperty("minimum", out var minimum) &&
            (!minimum.TryGetDecimal(out var minimumValue) || value < minimumValue))
        {
            return false;
        }
        if (schema.TryGetProperty("maximum", out var maximum) &&
            (!maximum.TryGetDecimal(out var maximumValue) || value > maximumValue))
        {
            return false;
        }
        return true;
    }

    internal static bool TryCreateReadOnlyContentBindingReceipt(
        LegendConnectReadOnlyContentBindingRequest request,
        string output,
        DateTime executedUtc,
        out LegendConnectReadOnlyContentBindingReceipt? receipt,
        out string reasonCode)
    {
        receipt = null;
        reasonCode = "read_only_content_binding_output_malformed";
        if (executedUtc.Kind != DateTimeKind.Utc ||
            string.IsNullOrWhiteSpace(output) ||
            output.Length > MaximumNativeReadOutputCharacters)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                HasDuplicateProperties(root) ||
                root.TryGetProperty("error", out _))
            {
                reasonCode = root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("error", out _)
                        ? "read_only_content_binding_tool_error"
                        : reasonCode;
                return false;
            }
            if (!TrySelectPropertyPath(root, request.ValuePath, out var value) ||
                !TryReadBoundedScalar(value, out var scalar))
            {
                return false;
            }

            var observedUtc = executedUtc;
            if (!string.IsNullOrWhiteSpace(request.ObservedUtcPath))
            {
                if (!TrySelectPropertyPath(root, request.ObservedUtcPath, out var observed) ||
                    observed.ValueKind != JsonValueKind.String ||
                    !DateTime.TryParse(
                        observed.GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out observedUtc))
                {
                    return false;
                }
                if (executedUtc - observedUtc >
                    TimeSpan.FromSeconds(request.MaximumAgeSeconds))
                {
                    reasonCode = "read_only_content_binding_stale";
                    return false;
                }
            }

            receipt = new LegendConnectReadOnlyContentBindingReceipt(
                request.RequestIdentity,
                request.TransitionSignature,
                request.ResultSemanticFrameSignature,
                request.ToolName,
                LegendLanguageIdentity.TextHash(request.ArgumentsJson),
                request.ValuePath,
                request.SemanticVariable,
                request.ResultDimension,
                scalar,
                LegendLanguageIdentity.TextHash(output),
                observedUtc,
                executedUtc,
                LegendConnectReadOnlyContentBindingContracts.Provenance,
                IsReadOnly: true,
                ZeroWrite: true);
            reasonCode = "read_only_content_binding_receipt_governed";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TrySelectPropertyPath(
        JsonElement root,
        string path,
        out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.', StringSplitOptions.None))
        {
            if (value.ValueKind != JsonValueKind.Object ||
                HasDuplicateProperties(value) ||
                !value.TryGetProperty(segment, out var next))
            {
                return false;
            }
            value = next;
        }
        return true;
    }

    private static bool TryReadBoundedScalar(JsonElement value, out string scalar)
    {
        scalar = value.ValueKind switch
        {
            JsonValueKind.String =>
                LegendLanguageIdentity.NormalizeText(value.GetString() ?? string.Empty),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
        return !string.IsNullOrWhiteSpace(scalar) &&
            scalar.Length <= LegendConnectReadOnlyContentBindingContracts.MaximumScalarCharacters &&
            !scalar.Any(char.IsControl);
    }

    private static bool HasDuplicateProperties(JsonElement root) =>
        root.EnumerateObject()
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);

    private static bool IsBoundedLanguage(string? value) =>
        IsBoundedString(value, 2, 40) &&
        LegendLanguageIdentity.TryNormalize(value, out _);

    private static bool IsOptionalBoundedLanguage(JsonElement root, string propertyName) =>
        !root.TryGetProperty(propertyName, out var property) ||
        property.ValueKind == JsonValueKind.Null ||
        (property.ValueKind == JsonValueKind.String &&
         IsBoundedLanguage(property.GetString()?.Trim()));

    private static bool IsBoundedString(
        string? value,
        int minimumLength,
        int maximumLength) =>
        value is not null &&
        value.Length >= minimumLength &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static IReadOnlyList<object> DescribeFounderCapabilities()
    {
        var capabilities = new List<object>();
        foreach (var tool in BuildFounderTools())
        {
            using var document = JsonDocument.Parse(
                JsonSerializer.Serialize(tool, JsonOptions));
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                !string.Equals(type.GetString(), "function", StringComparison.Ordinal))
            {
                continue;
            }

            var name = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var description = root.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString()
                : null;
            var readOnly = IsReadOnlyFounderTool(name);
            var canPrepareBoundedRepair =
                string.Equals(name, "legend_prepare_software_repair", StringComparison.Ordinal);
            var canMergeExactApprovedRepair =
                string.Equals(name, "legend_release_approved_repair", StringComparison.Ordinal);
            var conditionallyRestrictedResearch =
                string.Equals(name, "legend_research_internet", StringComparison.Ordinal);
            capabilities.Add(new
            {
                name,
                description,
                access = conditionallyRestrictedResearch
                    ? "founder_governed_public_read_or_exact_authorized_restricted_read"
                    : readOnly ? "founder_governed_read" : "founder_governed_mutation",
                sourceOfTruth = "BuildFounderTools",
                requiresExplicitFounderCommand = !readOnly,
                restrictedClassRequiresExistingAuthorization = conditionallyRestrictedResearch,
                zeroWrite = conditionallyRestrictedResearch,
                canOverrideAuthorities = false,
                canModifyRepository = canPrepareBoundedRepair,
                canCreateIsolatedRepairBranch = canPrepareBoundedRepair,
                canMergeExactApprovedRepair,
                canDeploy = false,
                arbitrarySql = false,
                arbitraryShell = false,
                arbitraryCodeExecution = false
            });
        }

        return capabilities;
    }

    private static IReadOnlyList<object> BuildFounderTools()
    {
        IReadOnlyList<object> tools =
        [
            new
            {
                type = "function",
                name = "legend_capabilities",
                description =
                    "Discover the exact governed LEGEND capabilities exposed to this Founder AI session from the same tool registry the model can execute. Use this when planning system inspection or remediation instead of guessing that an operation exists. This is read-only and creates no second authority.",
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
                name = "legend_operational_diagnostics",
                description =
                    "Read the existing runtime-policy readiness gates, aggregate operational status, provider capacity, and acquisition contract together. Use this before diagnosing a candidate backlog. A nonzero candidate backlog with zero downstream learning events is not by itself a broken handoff: approved candidates are the durable acquisition queue, and BLOCKED/DEGRADED readiness intentionally prevents claims. This is read-only and cannot reset rows, bypass gates, edit code, or deploy.",
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
                name = "legend_research_internet",
                description =
                    "Request canonical bounded internet research only for current or time-sensitive information, explicit verification, a named external document/source, or an actual governed knowledge gap. The existing LEGEND serving authority decides whether research is needed; the existing Founder tool authority authorizes the access class. Public research is read-only and zero-write. Sensitive, authenticated, private, restricted, or mutation-capable requests fail closed without exact request-level Founder authorization, and unavailable private/authenticated transports remain unavailable even after authorization. This tool never replaces LEGEND operational tools and never writes external evidence into retained knowledge.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        question = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = LegendConnectResearchContracts.MaximumQueryCharacters
                        },
                        source_language = new
                        {
                            type = "string",
                            minLength = 2,
                            maxLength = 40
                        }
                    },
                    required = new[]
                    {
                        "question",
                        "source_language"
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
                    "Retain reusable machine-derived LANGUAGE teaching from the current conversation in LEGEND's existing MachineProposed lifecycle. Declare translation for distinct-language teaching or same_language_semantic for governed semantic teaching within one language, and classify it as reusable_semantic. Conversational learning must include explicit language-neutral semantic_transitions connecting controlled source and result frames. This tool does NOT approve, validate, train, serve or promote the material. The existing independent critic and canonical validator remain authoritative. Never use it for personal facts, private messages, transient platform facts or unsupported speculation.",
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
                        capability_identity = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                LegendConnectMachineTeachingSubmission.TranslationCapability,
                                LegendConnectMachineTeachingSubmission.SameLanguageSemanticCapability
                            }
                        },
                        category_identity = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                LegendConnectMachineTeachingSubmission.ReusableSemanticCategory
                            }
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
                        },
                        semantic_transitions = new
                        {
                            type = new[] { "array", "null" },
                            minItems = 1,
                            maxItems = 12,
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    source = SemanticFrameSchema(),
                                    result = SemanticFrameSchema()
                                },
                                required = new[] { "source", "result" },
                                additionalProperties = false
                            }
                        }
                    },
                    required = new[]
                    {
                        "source_language",
                        "target_language",
                        "capability_identity",
                        "category_identity",
                        "family_key",
                        "semantic_category",
                        "rationale",
                        "confidence",
                        "examples",
                        "semantic_transitions"
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
                name = "legend_software_remediation_status",
                description =
                    "Read whether the single Founder-governed software-remediation authority is configured. It reports only capability state; it never reveals a GitHub token, private key, connection string, or production credential.",
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
                name = "legend_inspect_repository",
                description =
                    "Read a bounded source or test file, or the protected production branch SHA, through the configured GitHub App. This is repository inspection only; it cannot execute commands, change files, open a pull request, merge, or deploy.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = new[] { "string", "null" }, maxLength = 260 },
                        git_reference = new { type = new[] { "string", "null" }, maxLength = 100 }
                    },
                    required = new[] { "path", "git_reference" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_prepare_software_repair",
                description =
                    "After the Founder explicitly directs and confirms a repair, prepare one bounded source/test patch against the exact inspected base SHA. The canonical authority creates an isolated repair branch, immutable commit and pull request, which invokes existing pull-request CI. It cannot merge protected production or deploy. Legend® Ai itself is competency-gated and must fail closed/escalate to OpenAI Teacher until a governed software-repair competency is established.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        base_sha = new { type = "string", minLength = 40, maxLength = 40 },
                        title = new { type = "string", minLength = 1, maxLength = 160 },
                        summary = new { type = "string", minLength = 1, maxLength = 4000 },
                        changes = new
                        {
                            type = "array",
                            minItems = 1,
                            maxItems = 6,
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    path = new { type = "string", minLength = 1, maxLength = 260 },
                                    content = new { type = "string", maxLength = 60000 }
                                },
                                required = new[] { "path", "content" },
                                additionalProperties = false
                            }
                        }
                    },
                    required = new[] { "base_sha", "title", "summary", "changes" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_inspect_repair_validation",
                description =
                    "Read validation for one exact pull-request number and immutable repair SHA. It checks that the pull request is open against protected production and that all required checks on that exact SHA succeeded. This is read-only and cannot merge or deploy.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        pull_request_number = new { type = "integer", minimum = 1 },
                        head_sha = new { type = "string", minLength = 40, maxLength = 40 }
                    },
                    required = new[] { "pull_request_number", "head_sha" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_request_repair_release",
                description =
                    "Prepare a read-only Founder release decision for one exact pull request and SHA. It cannot merge or deploy and always requires a separate explicit Founder confirmation to release.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        pull_request_number = new { type = "integer", minimum = 1 },
                        head_sha = new { type = "string", minLength = 40, maxLength = 40 }
                    },
                    required = new[] { "pull_request_number", "head_sha" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_release_approved_repair",
                description =
                    "Only after an explicit Founder command and request-level confirmation, attempt a protected GitHub merge of one exact pull request/SHA. The canonical authority first rechecks exact identity, required current-SHA CI, strict status checks, pull-request review protection and admin enforcement. It never calls Azure directly; the existing protected-production workflow alone deploys after GitHub accepts the merge.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        pull_request_number = new { type = "integer", minimum = 1 },
                        head_sha = new { type = "string", minLength = 40, maxLength = 40 }
                    },
                    required = new[] { "pull_request_number", "head_sha" },
                    additionalProperties = false
                },
                strict = true
            },
            new
            {
                type = "function",
                name = "legend_verify_repair_deployment",
                description =
                    "Read the existing protected-production GitHub deployment workflow state for one exact merge commit SHA. It never calls Azure directly and cannot alter a deployment.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        commit_sha = new { type = "string", minLength = 40, maxLength = 40 }
                    },
                    required = new[] { "commit_sha" },
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

        var validationErrors =
            ValidateSerializedToolCatalog(tools);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "The LEGEND Founder tool catalog violates the strict provider contract: " +
                string.Join(" | ", validationErrors));
        }

        return tools;
    }

    internal static IReadOnlyList<string> ValidateSerializedToolCatalog(
        IReadOnlyList<object> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        using var document =
            JsonSerializer.SerializeToDocument(
                tools,
                JsonOptions);
        var errors = new List<string>();
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            return ["$: the Founder tool catalog must serialize as an array."];
        }

        if (root.GetArrayLength() == 0)
            errors.Add("$: the Founder tool catalog must not be empty.");

        var functionNames = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var tool in root.EnumerateArray())
        {
            var path = $"$[{index}]";
            index++;
            if (tool.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}: a provider tool must be an object.");
                continue;
            }

            if (!tool.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                errors.Add($"{path}.type: a nonblank provider tool type is required.");
                continue;
            }

            var toolType = typeElement.GetString();
            if (!string.Equals(toolType, "function", StringComparison.Ordinal))
            {
                errors.Add($"{path}.type: unsupported provider tool type '{toolType}'.");
                continue;
            }

            ValidateClosedToolObject(
                tool,
                path,
                RequiredFunctionToolProperties,
                errors);

            if (!tool.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                errors.Add($"{path}.name: a nonblank function name is required.");
            }
            else if (!functionNames.Add(nameElement.GetString()!))
            {
                errors.Add($"{path}.name: duplicate function name '{nameElement.GetString()}'.");
            }

            if (!tool.TryGetProperty("description", out var descriptionElement) ||
                descriptionElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(descriptionElement.GetString()))
            {
                errors.Add($"{path}.description: a nonblank function description is required.");
            }

            if (!tool.TryGetProperty("strict", out var strictElement) ||
                strictElement.ValueKind is not JsonValueKind.True)
            {
                errors.Add($"{path}.strict: every Founder function must be strict.");
            }

            if (!tool.TryGetProperty("parameters", out var parametersElement) ||
                parametersElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}.parameters: a schema object is required.");
                continue;
            }

            ValidateStrictSchema(
                parametersElement,
                $"{path}.parameters",
                errors,
                requireNonNullableObject: true);
        }

        return errors
            .OrderBy(error => error, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateClosedToolObject(
        JsonElement tool,
        string path,
        IReadOnlyCollection<string> expectedProperties,
        ICollection<string> errors)
    {
        var actualProperties = tool
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var expected = expectedProperties.ToHashSet(StringComparer.Ordinal);
        if (actualProperties.SetEquals(expected))
            return;

        errors.Add(
            $"{path}: provider tool properties must be exactly [{string.Join(", ", expected.OrderBy(value => value, StringComparer.Ordinal))}].");
    }

    private static void ValidateStrictSchema(
        JsonElement schema,
        string path,
        ICollection<string> errors,
        bool requireNonNullableObject = false)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: a schema must be an object.");
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            if (!IsSupportedStrictSchemaKeyword(keyword.Name))
            {
                errors.Add(
                    $"{path}.{keyword.Name}: keyword is not supported by the Founder provider schema contract.");
            }
        }

        var types = ReadStrictSchemaTypes(schema, path, errors);
        if (types.Count == 0)
            return;

        var nonNullTypes = types
            .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
            .ToArray();
        if (requireNonNullableObject &&
            (types.Count != 1 ||
             nonNullTypes.Length != 1 ||
             !string.Equals(nonNullTypes[0], "object", StringComparison.Ordinal)))
        {
            errors.Add($"{path}.type: strict function parameters must be a non-nullable object.");
        }

        var isObject = nonNullTypes.Contains("object", StringComparer.Ordinal);
        var isArray = nonNullTypes.Contains("array", StringComparer.Ordinal);
        var isString = nonNullTypes.Contains("string", StringComparer.Ordinal);
        var isNumeric = nonNullTypes.Any(type => type is "number" or "integer");

        ValidateKeywordApplicability(
            schema,
            path,
            errors,
            isObject,
            isArray,
            isString,
            isNumeric);

        if (isObject)
            ValidateStrictObjectSchema(schema, path, errors);

        if (isArray)
            ValidateStrictArraySchema(schema, path, errors);

        if (isString)
        {
            ValidateNonnegativeIntegerBounds(
                schema,
                path,
                "minLength",
                "maxLength",
                errors);
        }

        if (isArray)
        {
            ValidateNonnegativeIntegerBounds(
                schema,
                path,
                "minItems",
                "maxItems",
                errors);
        }

        if (isNumeric)
            ValidateNumericBounds(schema, path, errors);

        if (schema.TryGetProperty("description", out var description) &&
            description.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}.description: expected a string.");
        }

        if (schema.TryGetProperty("enum", out var enumElement))
            ValidateEnum(enumElement, types, path, errors);
    }

    private static HashSet<string> ReadStrictSchemaTypes(
        JsonElement schema,
        string path,
        ICollection<string> errors)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (!schema.TryGetProperty("type", out var typeElement))
        {
            errors.Add($"{path}.type: a schema type is required.");
            return types;
        }

        if (typeElement.ValueKind == JsonValueKind.String)
        {
            AddSchemaType(typeElement, path, types, errors);
            if (types.Contains("null"))
                errors.Add($"{path}.type: null cannot be the only schema type.");
            return types;
        }

        if (typeElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.type: expected a supported type string or nullable type array.");
            return types;
        }

        foreach (var item in typeElement.EnumerateArray())
            AddSchemaType(item, path, types, errors);

        if (typeElement.GetArrayLength() != 2 ||
            types.Count != 2 ||
            !types.Contains("null"))
        {
            errors.Add(
                $"{path}.type: nullable schemas must contain exactly one supported non-null type and null.");
        }

        return types;
    }

    private static void AddSchemaType(
        JsonElement typeElement,
        string path,
        ISet<string> types,
        ICollection<string> errors)
    {
        if (typeElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(typeElement.GetString()))
        {
            errors.Add($"{path}.type: every type entry must be a nonblank string.");
            return;
        }

        var type = typeElement.GetString()!;
        if (!IsSupportedStrictSchemaType(type))
        {
            errors.Add($"{path}.type: unsupported schema type '{type}'.");
            return;
        }

        if (!types.Add(type))
            errors.Add($"{path}.type: duplicate schema type '{type}'.");
    }

    private static void ValidateKeywordApplicability(
        JsonElement schema,
        string path,
        ICollection<string> errors,
        bool isObject,
        bool isArray,
        bool isString,
        bool isNumeric)
    {
        foreach (var property in schema.EnumerateObject())
        {
            var applicable = property.Name switch
            {
                "properties" or "required" or "additionalProperties" => isObject,
                "items" or "minItems" or "maxItems" => isArray,
                "minLength" or "maxLength" => isString,
                "minimum" or "maximum" => isNumeric,
                _ => true
            };

            if (!applicable)
            {
                errors.Add(
                    $"{path}.{property.Name}: keyword does not apply to the declared schema type.");
            }
        }
    }

    private static void ValidateStrictObjectSchema(
        JsonElement schema,
        string path,
        ICollection<string> errors)
    {
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.properties: every strict object requires a properties object.");
            return;
        }

        if (!schema.TryGetProperty("additionalProperties", out var additionalProperties) ||
            additionalProperties.ValueKind is not JsonValueKind.False)
        {
            errors.Add($"{path}.additionalProperties: every strict object must be closed with false.");
        }

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
                errors.Add($"{path}.properties: blank property names are not supported.");
            propertyNames.Add(property.Name);
            ValidateStrictSchema(
                property.Value,
                $"{path}.properties.{property.Name}",
                errors);
        }

        if (!schema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.required: every strict object requires a required array.");
            return;
        }

        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requiredName in required.EnumerateArray())
        {
            if (requiredName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(requiredName.GetString()))
            {
                errors.Add($"{path}.required: entries must be nonblank strings.");
                continue;
            }

            if (!requiredNames.Add(requiredName.GetString()!))
            {
                errors.Add(
                    $"{path}.required: duplicate entry '{requiredName.GetString()}'.");
            }
        }

        if (!requiredNames.SetEquals(propertyNames))
        {
            errors.Add(
                $"{path}.required: required names must exactly match properties; properties=[{string.Join(", ", propertyNames.OrderBy(value => value, StringComparer.Ordinal))}], required=[{string.Join(", ", requiredNames.OrderBy(value => value, StringComparer.Ordinal))}].");
        }
    }

    private static void ValidateStrictArraySchema(
        JsonElement schema,
        string path,
        ICollection<string> errors)
    {
        if (!schema.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.items: every array requires one item schema object.");
            return;
        }

        ValidateStrictSchema(
            items,
            $"{path}.items",
            errors);
    }

    private static void ValidateNonnegativeIntegerBounds(
        JsonElement schema,
        string path,
        string minimumName,
        string maximumName,
        ICollection<string> errors)
    {
        var hasMinimum = schema.TryGetProperty(minimumName, out var minimumElement);
        var hasMaximum = schema.TryGetProperty(maximumName, out var maximumElement);
        var minimum = 0;
        var maximum = 0;
        var minimumValid = !hasMinimum ||
            minimumElement.TryGetInt32(out minimum) && minimum >= 0;
        var maximumValid = !hasMaximum ||
            maximumElement.TryGetInt32(out maximum) && maximum >= 0;

        if (!minimumValid)
            errors.Add($"{path}.{minimumName}: expected a nonnegative integer.");
        if (!maximumValid)
            errors.Add($"{path}.{maximumName}: expected a nonnegative integer.");
        if (hasMinimum && hasMaximum && minimumValid && maximumValid && minimum > maximum)
        {
            errors.Add($"{path}: {minimumName} cannot exceed {maximumName}.");
        }
    }

    private static void ValidateNumericBounds(
        JsonElement schema,
        string path,
        ICollection<string> errors)
    {
        var hasMinimum = schema.TryGetProperty("minimum", out var minimumElement);
        var hasMaximum = schema.TryGetProperty("maximum", out var maximumElement);
        decimal minimum = 0;
        decimal maximum = 0;
        var minimumValid = !hasMinimum || minimumElement.TryGetDecimal(out minimum);
        var maximumValid = !hasMaximum || maximumElement.TryGetDecimal(out maximum);

        if (!minimumValid)
            errors.Add($"{path}.minimum: expected a finite JSON number.");
        if (!maximumValid)
            errors.Add($"{path}.maximum: expected a finite JSON number.");
        if (hasMinimum && hasMaximum && minimumValid && maximumValid && minimum > maximum)
            errors.Add($"{path}: minimum cannot exceed maximum.");
    }

    private static void ValidateEnum(
        JsonElement enumElement,
        IReadOnlySet<string> types,
        string path,
        ICollection<string> errors)
    {
        if (enumElement.ValueKind != JsonValueKind.Array ||
            enumElement.GetArrayLength() == 0)
        {
            errors.Add($"{path}.enum: expected a nonempty array.");
            return;
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in enumElement.EnumerateArray())
        {
            var itemType = item.ValueKind switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                _ => string.Empty
            };
            var numericCompatible =
                string.Equals(itemType, "number", StringComparison.Ordinal) &&
                types.Contains("integer") &&
                item.TryGetInt64(out _);
            if (string.IsNullOrEmpty(itemType) ||
                (!types.Contains(itemType) && !numericCompatible))
            {
                errors.Add($"{path}.enum: value {item.GetRawText()} does not match the declared type.");
            }

            if (!values.Add(item.GetRawText()))
                errors.Add($"{path}.enum: duplicate value {item.GetRawText()}.");
        }
    }

    private static bool IsSupportedStrictSchemaKeyword(string keyword) =>
        keyword is
            "type" or
            "description" or
            "properties" or
            "required" or
            "additionalProperties" or
            "items" or
            "enum" or
            "minLength" or
            "maxLength" or
            "minimum" or
            "maximum" or
            "minItems" or
            "maxItems";

    private static bool IsSupportedStrictSchemaType(string type) =>
        type is
            "string" or
            "number" or
            "integer" or
            "boolean" or
            "object" or
            "array" or
            "null";

    internal static int ResolveRetainedKnowledgeTake(string query)
    {
        var words = query.Split(
            [' ', '\r', '\n', '\t', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        return Math.Clamp(12 + words / 25, 12, 32);
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

    private static bool TryReadMachineSemanticTransitions(
        JsonElement root,
        out IReadOnlyList<LegendConnectSemanticTransitionSubmission>? transitions)
    {
        transitions = null;
        if (!root.TryGetProperty("semantic_transitions", out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() is < 1 or > 12)
        {
            return false;
        }

        var parsed = new List<LegendConnectSemanticTransitionSubmission>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadSemanticFrame(item, "source", out var source) ||
                !TryReadSemanticFrame(item, "result", out var result))
            {
                return false;
            }

            parsed.Add(new LegendConnectSemanticTransitionSubmission(source, result));
        }

        transitions = parsed;
        return true;
    }

    private static bool TryReadSemanticFrame(
        JsonElement root,
        string propertyName,
        out LegendConnectSemanticFrameSubmission frame)
    {
        frame = null!;
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var frameProperties = element.EnumerateObject().ToArray();
        if (frameProperties.Length != 1 ||
            !string.Equals(
                frameProperties[0].Name,
                "dimensions",
                StringComparison.Ordinal) ||
            frameProperties[0].Value.ValueKind != JsonValueKind.Array ||
            frameProperties[0].Value.GetArrayLength() is
                < MinimumSemanticFrameDimensions or
                > MaximumSemanticFrameDimensions)
        {
            return false;
        }

        var dimensionsElement = frameProperties[0].Value;
        var dimensions = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in dimensionsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;

            var properties = item.EnumerateObject().ToArray();
            if (properties.Length != 2)
                return false;

            string? dimension = null;
            string? value = null;
            foreach (var property in properties)
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return false;

                if (string.Equals(
                        property.Name,
                        "dimension",
                        StringComparison.Ordinal) &&
                    dimension is null)
                {
                    dimension = property.Value.GetString()?.Trim();
                    continue;
                }

                if (string.Equals(
                        property.Name,
                        "value",
                        StringComparison.Ordinal) &&
                    value is null)
                {
                    value = property.Value.GetString()?.Trim();
                    continue;
                }

                return false;
            }

            if (string.IsNullOrWhiteSpace(dimension) ||
                string.IsNullOrWhiteSpace(value) ||
                dimension.Length > MaximumSemanticFrameDimensionLength ||
                value.Length > MaximumSemanticFrameValueLength ||
                dimension.Any(character =>
                    !(char.IsLetterOrDigit(character) ||
                      character is '.' or '-' or '_')) ||
                !IsValidSemanticFrameValue(value) ||
                !dimensions.TryAdd(
                    dimension,
                    value))
            {
                return false;
            }
        }

        frame = new LegendConnectSemanticFrameSubmission(dimensions);
        return true;
    }

    private static bool IsValidSemanticFrameValue(string value)
    {
        if (!value.StartsWith('$'))
            return true;

        return value.Length is >= 2 and <= 81 &&
            char.IsLetter(value[1]) &&
            value[2..].All(character =>
                char.IsLetterOrDigit(character) ||
                character is '_' or '-');
    }

    private static object SemanticFrameSchema() => new
    {
        type = "object",
        properties = new
        {
            dimensions = new
            {
                type = "array",
                minItems = MinimumSemanticFrameDimensions,
                maxItems = MaximumSemanticFrameDimensions,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        dimension = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = MaximumSemanticFrameDimensionLength
                        },
                        value = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = MaximumSemanticFrameValueLength
                        }
                    },
                    required = new[] { "dimension", "value" },
                    additionalProperties = false
                }
            }
        },
        required = new[] { "dimensions" },
        additionalProperties = false
    };

    private static object SoftwareRemediationNotAvailable() => new
    {
        error = "software_remediation_not_configured",
        detail = "Founder-governed software remediation is unavailable because its canonical service is not registered.",
        directProductionAccess = false,
        rawTokenInput = false
    };

    private static string SerializeUnbounded(object? value) =>
        JsonSerializer.Serialize(
            value,
            JsonOptions);


}

internal sealed record FounderAiToolCall(
    string CallId,
    string Name,
    string Arguments,
    FounderAiMutationAuthorization? MutationAuthorization = null);

internal sealed record FounderAiMutationAuthorization(
    string CorrelationId);
