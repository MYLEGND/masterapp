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
    private readonly FounderLegendConnectService _legend;
    private readonly IFounderSoftwareRemediationService? _softwareRemediation;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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

    private static bool IsGovernedEvidenceTool(string name) =>
        IsReadOnlyFounderTool(name) &&
        !string.Equals(name, "legend_capabilities", StringComparison.Ordinal);

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


    internal async Task<string> ExecuteAsync(
        ClaimsPrincipal founder,
        FounderAiToolCall call,
        string mode,
        CancellationToken cancellationToken)
    {
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
            capabilities.Add(new
            {
                name,
                description,
                access = readOnly ? "founder_governed_read" : "founder_governed_mutation",
                sourceOfTruth = "BuildFounderTools",
                requiresExplicitFounderCommand = !readOnly,
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
    }

    private static int ResolveRetainedKnowledgeTake(string query)
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
    string Arguments);
