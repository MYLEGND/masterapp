using System.Security.Claims;
using AgentPortal.Models;
using AgentPortal.Security;
using Domain.Entities;
using Domain.Messaging;

namespace AgentPortal.Services;

/// <summary>
/// Founder-gated presentation adapter. The server-owned Legend Connect
/// operations facade remains the only language/learning authority.
/// </summary>
public sealed class FounderLegendConnectService
{
    private readonly ILegendConnectOperations _operations;
    private readonly AgentProfileAccessResolver _agentProfiles;
    private readonly ITranslationEntitlementAuthority? _entitlements;
    private readonly IMessagingService? _messaging;
    private readonly ILegendConnectRuntimePolicyAuthority? _runtimePolicy;
    private readonly IFounderSoftwareRemediationService? _softwareRemediation;
    private readonly ILegendIntelligenceEvaluationService? _intelligenceEvaluation;

    public FounderLegendConnectService(
        ILegendConnectOperations operations,
        AgentProfileAccessResolver agentProfiles,
        ITranslationEntitlementAuthority? entitlements = null,
        IMessagingService? messaging = null,
        ILegendConnectRuntimePolicyAuthority? runtimePolicy = null,
        IFounderSoftwareRemediationService? softwareRemediation = null,
        ILegendIntelligenceEvaluationService? intelligenceEvaluation = null)
    {
        _operations = operations;
        _agentProfiles = agentProfiles;
        _entitlements = entitlements;
        _messaging = messaging;
        _runtimePolicy = runtimePolicy;
        _softwareRemediation = softwareRemediation;
        _intelligenceEvaluation = intelligenceEvaluation;
    }

    public async Task<FounderLegendConnectPageVm> GetDashboardAsync(
        ClaimsPrincipal user,
        string? language,
        string? pair,
        CancellationToken cancellationToken = default,
        string? accountSearch = null)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        // Pair/account detail remains available through scoped Founder actions;
        // it is intentionally not loaded merely to render the page shell.
        _ = pair;
        _ = accountSearch;
        var shell = await _operations.GetFounderShellAsync(language, cancellationToken);
        var runtimePolicy = _runtimePolicy is null
            ? new LegendConnectRuntimePolicySnapshot(false, 0, 0, 0, false, true, "Shadow", 0.98m, null, null, DateTime.MinValue)
            : await _runtimePolicy.GetEffectiveAsync(cancellationToken);
        var remediation = _softwareRemediation is null
            ? FounderSoftwareRemediationStatusSnapshot.Unconfigured("Founder software remediation is unavailable because its canonical service is not registered.")
            : ToRemediationSnapshot(await _softwareRemediation.GetStatusAsync(cancellationToken));
        var intelligenceEvaluation = _intelligenceEvaluation is null
            ? LegendIntelligenceEvaluationDashboardSnapshot.NotEvaluated()
            : await _intelligenceEvaluation.GetDashboardAsync(cancellationToken);
        return new FounderLegendConnectPageVm
        {
            Shell = shell,
            RuntimePolicy = runtimePolicy,
            SoftwareRemediation = remediation,
            IntelligenceEvaluation = intelligenceEvaluation
        };
    }

    public async Task<object> ConnectSoftwareRemediationAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        return _softwareRemediation is null
            ? FounderSoftwareRemediationStatusSnapshot.Unconfigured("Founder software remediation is unavailable because its canonical service is not registered.")
            : await _softwareRemediation.ConnectAsync(founder, cancellationToken);
    }

    public async Task<object> VerifySoftwareRemediationAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        return _softwareRemediation is null
            ? FounderSoftwareRemediationStatusSnapshot.Unconfigured("Founder software remediation is unavailable because its canonical service is not registered.")
            : await _softwareRemediation.VerifyAuthorityAsync(cancellationToken);
    }

    public async Task<object> TestSoftwareRemediationAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        return _softwareRemediation is null
            ? FounderSoftwareRemediationStatusSnapshot.Unconfigured("Founder software remediation is unavailable because its canonical service is not registered.")
            : await _softwareRemediation.TestRepairPreparationAsync(cancellationToken);
    }

    public async Task<object> RevokeSoftwareRemediationAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        return _softwareRemediation is null
            ? FounderSoftwareRemediationStatusSnapshot.Unconfigured("Founder software remediation is unavailable because its canonical service is not registered.")
            : await _softwareRemediation.RevokeAsync(founder, cancellationToken);
    }

    public async Task<LegendIntelligenceEvaluationDashboardSnapshot> CreateIntelligenceEvaluationSnapshotAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        return _intelligenceEvaluation is null
            ? LegendIntelligenceEvaluationDashboardSnapshot.NotEvaluated()
            : await _intelligenceEvaluation.CreateEvidenceSnapshotAsync(founder, cancellationToken);
    }

    public async Task<LegendConnectFounderSectionPageSnapshot> GetSectionPageAsync(
        ClaimsPrincipal user,
        string section,
        string? language,
        string? search,
        string? cursor,
        Guid? curriculumFamilyId,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        return await _operations.GetFounderSectionPageAsync(
            section,
            language,
            search,
            cursor,
            curriculumFamilyId,
            cancellationToken);
    }

    /// <summary>
    /// Explicit Founder-tool inspection remains separate from the page shell.
    /// It retains the existing comprehensive operational projection only when
    /// the caller deliberately asks the Founder tool for language state.
    /// </summary>
    public async Task<FounderLegendConnectDashboardVm> GetLanguageStateAsync(
        ClaimsPrincipal user,
        string language,
        string? pair,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        var projection = await _operations.GetDashboardProjectionAsync(language, pair, cancellationToken);
        var runtimePolicy = _runtimePolicy is null
            ? new LegendConnectRuntimePolicySnapshot(false, 0, 0, 0, false, true, "Shadow", 0.98m, null, null, DateTime.MinValue)
            : await _runtimePolicy.GetEffectiveAsync(cancellationToken);
        var readiness = _runtimePolicy is null
            ? new LegendConnectProductionReadinessSnapshot("BLOCKED", false, "Legend Connect runtime policy authority is unavailable.", Array.Empty<LegendConnectReadinessCheck>(), 0, 0, 0, 0, 0)
            : await _runtimePolicy.GetReadinessAsync(cancellationToken);
        return new FounderLegendConnectDashboardVm
        {
            Dashboard = projection.Dashboard,
            SelectedLanguage = projection.SelectedLanguageKnowledge?.Health,
            SelectedLanguageKnowledge = projection.SelectedLanguageKnowledge,
            SelectedPair = projection.SelectedPair,
            TranslationQuality = await _operations.GetTranslationQualityAsync(cancellationToken),
            RuntimePolicy = runtimePolicy,
            ProductionReadiness = readiness
        };
    }

    public async Task<LegendConnectLanguageKnowledgeSnapshot?> GetLanguageKnowledgeAsync(
        ClaimsPrincipal user,
        string languageCode,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);

        return await _operations.GetLanguageKnowledgeAsync(
            languageCode,
            cancellationToken);
    }

    public async Task<LegendConnectPairHealthSnapshot?> GetPairHealthAsync(
        ClaimsPrincipal user,
        string pairKey,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);

        return await _operations.GetPairHealthAsync(
            pairKey,
            cancellationToken);
    }

    public async Task<LegendConnectTranslationQualitySnapshot> GetTranslationQualityAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);

        return await _operations.GetTranslationQualityAsync(
            cancellationToken);
    }

    public async Task<LegendTargetRealizationReviewSnapshot> GetTargetRealizationReviewAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);

        return await _operations.GetTargetRealizationReviewAsync(
            cancellationToken);
    }

    public async Task<LegendConnectRetainedKnowledgeSearchSnapshot>
        SearchRetainedKnowledgeAsync(
            ClaimsPrincipal user,
            string query,
            string? sourceLanguageCode = null,
            string? targetLanguageCode = null,
            int take = 12,
            CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(
            user,
            cancellationToken);

        return await _operations
            .SearchRetainedKnowledgeAsync(
                query,
                sourceLanguageCode,
                targetLanguageCode,
                take,
                cancellationToken);
    }

    public async Task<LegendConnectNativeInferenceSnapshot>
        TryInferConversationAsync(
            ClaimsPrincipal user,
            string input,
            IReadOnlyList<LegendConnectConversationContextItem> context,
            CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(
            user,
            cancellationToken);

        return await _operations.TryInferConversationAsync(
            input,
            context,
            cancellationToken);
    }

    public async Task<LegendConnectNativeInferenceSnapshot>
        TryInferConversationWithDiscourseAsync(
            ClaimsPrincipal user,
            string input,
            IReadOnlyList<LegendConnectConversationContextItem> context,
            LegendConnectDiscourseStateSnapshot? discourseState,
            CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        return await _operations.TryInferConversationWithDiscourseAsync(
            input,
            context,
            discourseState,
            cancellationToken);
    }

    /// <summary>
    /// Founder-gated read-through to the canonical observational meaning
    /// analysis. It does not serve, write curriculum, or bypass inference.
    /// </summary>
    public async Task<LegendConnectUtteranceMeaningGraphSnapshot>
        AnalyzeReusableMeaningGraphAsync(
            ClaimsPrincipal user,
            string input,
            CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        return await _operations.AnalyzeReusableMeaningGraphAsync(input, cancellationToken);
    }

    public async Task<LegendConnectMachineTeachingSubmissionResult>
        QueueMachineTeachingProposalAsync(
            ClaimsPrincipal user,
            LegendConnectMachineTeachingSubmission submission,
            CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(
            user,
            cancellationToken);

        return await _operations
            .SubmitMachineTeachingProposalAsync(
                submission,
                cancellationToken);
    }

    public async Task<LegendConnectKnowledgeSubmissionResult> QueueFounderLearningSeedAsync(
        ClaimsPrincipal user,
        string sourceLanguageCode,
        string sourceText,
        string? contextCategory,
        string? usageRegister,
        string? regionalVariant,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(
            user,
            cancellationToken);

        return await _operations.SubmitFounderKnowledgeAsync(
            founder,
            new LegendConnectKnowledgeSubmission(
                sourceLanguageCode,
                sourceText,
                null,
                null,
                contextCategory,
                usageRegister,
                regionalVariant,
                "FounderApproved"),
            cancellationToken);
    }

    public async Task<LegendConnectCurriculumSubmissionResult> QueueFounderCurriculumAsync(
        ClaimsPrincipal user,
        LegendConnectCurriculumManifestSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(
            user,
            cancellationToken);

        return await _operations.SubmitFounderCurriculumManifestAsync(
            founder,
            submission,
            cancellationToken);
    }

    public async Task<FounderLegendConnectOperationResult> EnsureAutonomousLearningActiveAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(
            user,
            cancellationToken);

        if (_runtimePolicy is null)
        {
            return new FounderLegendConnectOperationResult(
                false,
                "Legend Connect runtime policy authority is unavailable.");
        }

        var effective =
            await _runtimePolicy.GetEffectiveAsync(
                cancellationToken);

        if (effective.CorpusAcquisitionEnabled &&
            effective.LearningEnabled)
        {
            return new FounderLegendConnectOperationResult(
                true,
                "Existing autonomous learning is already active.");
        }

        if (!effective.LearningEnabled)
        {
            await _runtimePolicy.UpdateCompositionAsync(
                founder,
                learningEnabled: true,
                contextualCompositionMode: null,
                contextualMinimumConfidence:
                    effective.ContextualMinimumConfidence,
                cancellationToken: cancellationToken);
        }

        var readiness =
            await _runtimePolicy.ActivateAsync(
                founder,
                cancellationToken);

        var active =
            readiness.State is
                "ACTIVE" or
                "ACTIVE — NO ELIGIBLE WORK";

        return new FounderLegendConnectOperationResult(
            active,
            readiness.Summary);
    }

    public async Task<LegendConnectProviderCapacitySnapshot> GetProviderCapacityAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        return await _operations.GetProviderCapacityAsync(cancellationToken);
    }

    /// <summary>
    /// Founder-gated read-through to the existing Legend Connect operational
    /// authorities for the record-level evidence behind a dashboard metric.
    /// </summary>
    public async Task<LegendConnectMetricDetailSnapshot> GetMetricDetailAsync(
        ClaimsPrincipal user,
        string? metricKey,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        return await _operations.GetMetricDetailAsync(metricKey, cancellationToken);
    }

    /// <summary>
    /// Retrieves the current aggregate display metrics from the existing
    /// server-owned authorities. This deliberately has no client-side
    /// calculations, cached counters, or operational side effects.
    /// </summary>
    public async Task<FounderLegendConnectLiveMetricsSnapshot> GetLiveMetricsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        var dashboard = await _operations.GetDashboardAsync(cancellationToken);
        var translationQuality = await _operations.GetTranslationQualityAsync(cancellationToken);
        var accountScale = _entitlements is null
            ? new TranslationFounderScaleSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0)
            : await _entitlements.GetFounderScaleAsync(cancellationToken);
        var readiness = _runtimePolicy is null
            ? new LegendConnectProductionReadinessSnapshot("BLOCKED", false, "Legend Connect runtime policy authority is unavailable.", Array.Empty<LegendConnectReadinessCheck>(), 0, 0, 0, 0, 0)
            : await _runtimePolicy.GetReadinessAsync(cancellationToken);
        var runtimeAuditCount = _runtimePolicy is null
            ? 0
            : (await _runtimePolicy.GetRecentAuditAsync(cancellationToken: cancellationToken)).Count;

        return FounderLegendConnectLiveMetricsSnapshot.Create(
            dashboard,
            translationQuality,
            accountScale,
            readiness,
            runtimeAuditCount);
    }

    public async Task<FounderLegendConnectOperationResult> UpdateRuntimePolicyAsync(
        ClaimsPrincipal user,
        FounderLegendConnectRuntimePolicyInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        if (_runtimePolicy is null)
            return new FounderLegendConnectOperationResult(false, "Legend Connect runtime policy authority is unavailable.");
        try
        {
            await _runtimePolicy.UpdateCompositionAsync(
                founder,
                input.LearningEnabled,
                contextualCompositionMode: null,
                contextualMinimumConfidence: input.ContextualMinimumConfidence,
                cancellationToken: cancellationToken);
            return new FounderLegendConnectOperationResult(true, "Legend Connect learning and confidence settings were saved across the deployment. Production composition remains under the top-level server control.");
        }
        catch (ArgumentException exception)
        {
            return new FounderLegendConnectOperationResult(false, exception.Message);
        }
    }

    public async Task<FounderLegendConnectOperationResult> SetCompositionModeAsync(
        ClaimsPrincipal user,
        FounderLegendConnectCompositionModeInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        if (_runtimePolicy is null)
            return new FounderLegendConnectOperationResult(false, "Legend Connect runtime policy authority is unavailable.");
        try
        {
            var policy = await _runtimePolicy.SetContextualCompositionModeAsync(
                founder,
                input.ContextualCompositionMode,
                cancellationToken);
            return new FounderLegendConnectOperationResult(
                true,
                policy.ContextualCompositionMode switch
                {
                    "Active" => "Production composition is active. Existing eligibility, quality, language-isolation, and Azure fallback gates remain in force.",
                    "Shadow" => "Production composition is in Shadow mode. It remains observational and does not serve internal composed output.",
                    _ => "Production composition is disabled. Azure fallback remains available where internal routing does not serve a translation."
                });
        }
        catch (ArgumentException exception)
        {
            return new FounderLegendConnectOperationResult(false, exception.Message);
        }
    }

    public async Task<FounderLegendConnectOperationResult> ActivateAutonomousAcquisitionAsync(
        ClaimsPrincipal user,
        FounderLegendConnectActivationInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        if (_runtimePolicy is null)
            return new FounderLegendConnectOperationResult(false, "Legend Connect runtime policy authority is unavailable.");
        try
        {
            var focus = await _runtimePolicy.ConfigureAutonomousLanguageFocusAsync(
                founder,
                new LegendConnectAutonomousLanguageFocusMutation(
                    input.FocusEnabled,
                    input.FocusLanguageCodes),
                cancellationToken);
            var readiness = await _runtimePolicy.ActivateAsync(founder, cancellationToken);
            var activated = readiness.State is "ACTIVE" or "ACTIVE — NO ELIGIBLE WORK";
            var focusMessage = focus.FocusedTargetLanguageCodes.Count == 0
                ? "Automatic demand-driven acquisition is restored across enabled language pairs."
                : $"Founder language focus is on for {focus.FocusedTargetLanguageCodes.Count} selected target language(s).";
            return new FounderLegendConnectOperationResult(
                activated,
                activated ? $"{focusMessage} {readiness.Summary}" : readiness.Summary);
        }
        catch (ArgumentException exception)
        {
            return new FounderLegendConnectOperationResult(false, exception.Message);
        }
    }

    public async Task<FounderLegendConnectOperationResult> PauseAutonomousAcquisitionAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        if (_runtimePolicy is null)
            return new FounderLegendConnectOperationResult(false, "Legend Connect runtime policy authority is unavailable.");
        await _runtimePolicy.PauseAsync(founder, cancellationToken);
        return new FounderLegendConnectOperationResult(true, "Autonomous acquisition is paused. Live communication and Azure fallback remain available.");
    }

    public async Task<FounderLegendConnectEntitlementResult> UpdateEntitlementAsync(
        ClaimsPrincipal user,
        FounderLegendConnectEntitlementInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        if (_entitlements is null || _messaging is null)
            return new FounderLegendConnectEntitlementResult(false, "Legend Connect entitlement authority is unavailable.");

        var targetUserId = input.TargetUserId?.Trim().ToLowerInvariant() ?? string.Empty;
        var participantType = input.TargetParticipantType?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetUserId) || string.IsNullOrWhiteSpace(participantType))
            return new FounderLegendConnectEntitlementResult(false, "Choose a valid LEGEND account.");

        var target = new MessagingActor(targetUserId, participantType);
        if (!await _entitlements.IsFounderEntitlementEligibleAsync(target, cancellationToken))
        {
            return new FounderLegendConnectEntitlementResult(
                false,
                "Translation access can be managed only for active, current-paying Client CRM accounts.");
        }

        var grant = await _messaging.SetControlledResourceGrantAsync(
            new SetControlledResourceGrantCommand(
                new MessagingActor(founder, MessagingParticipantTypes.Agent),
                ControlledResourceTypes.LanguageTranslation,
                targetUserId,
                participantType,
                input.AccessGranted),
            cancellationToken);
        if (!grant.Succeeded)
            return new FounderLegendConnectEntitlementResult(false, grant.ErrorMessage ?? "Legend could not update translation access.");

        // Permission controls whether a provider call can start. Retaining a
        // prior entitlement on revoke preserves the audit trail while ensuring
        // it cannot be consumed until access is granted again.
        if (!input.AccessGranted)
            return new FounderLegendConnectEntitlementResult(true, "Translation access was revoked. Existing entitlement history remains auditable.");

        var mode = input.EntitlementMode?.Trim() ?? string.Empty;
        var unlimited = string.Equals(mode, "Unlimited", StringComparison.OrdinalIgnoreCase);
        long allowance;
        string source;
        if (unlimited)
        {
            allowance = 0;
            source = "FounderUnlimited";
        }
        else if (string.Equals(mode, "Preset", StringComparison.OrdinalIgnoreCase))
        {
            var preset = _entitlements.GetFounderEntitlementPresets().FirstOrDefault(item =>
                string.Equals(item.Key, input.PresetKey?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (preset is null)
                return new FounderLegendConnectEntitlementResult(false, "Choose a configured server allowance preset.");
            allowance = preset.CharacterAllowance;
            source = $"Preset:{preset.Key}";
        }
        else
        {
            allowance = input.CustomCharacterAllowance ?? -1;
            source = "FounderCustom";
        }

        if (allowance < 0)
            return new FounderLegendConnectEntitlementResult(false, "Enter a non-negative monthly character allowance.");

        try
        {
            await _entitlements.SetEntitlementAsync(
                founder,
                new TranslationEntitlementMutation(
                    target,
                    allowance,
                    unlimited,
                    source,
                    IsFounderOverride: true),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return new FounderLegendConnectEntitlementResult(false, exception.Message);
        }
        return new FounderLegendConnectEntitlementResult(
            true,
            unlimited
                ? "Unlimited translation entitlement was saved and remains fully metered."
                : "Translation access and the server-owned monthly allowance were saved.");
    }

    public async Task<LegendConnectKnowledgeSubmissionResult> SubmitAsync(
        ClaimsPrincipal user,
        FounderLegendConnectKnowledgeInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        return await _operations.SubmitFounderKnowledgeAsync(
            founder,
            ToSubmission(input),
            cancellationToken);
    }

    /// <summary>
    /// Converts only the Founder form's explicit row syntax, then delegates
    /// source resolution and every mutation to the existing operations facade.
    /// Normal curriculum submission never enters this method.
    /// </summary>
    public async Task<LegendConnectVerifiedTargetBatchResult> SubmitVerifiedTargetsAsync(
        ClaimsPrincipal user,
        FounderLegendConnectKnowledgeInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        if (!TryToVerifiedTargetSubmission(input, out var submission, out var error))
        {
            return new LegendConnectVerifiedTargetBatchResult(
                false,
                "invalid_verified_target_rows",
                error,
                input.SourceLanguageCode?.Trim() ?? string.Empty,
                input.TargetLanguageCode?.Trim(),
                null,
                [new LegendConnectVerifiedTargetRowResult(1, "Failed", error ?? "Invalid verified target rows.", null, null, null, null)]);
        }

        return await _operations.SubmitFounderVerifiedTargetsAsync(founder, submission!, cancellationToken);
    }

    public async Task<LegendConnectKnowledgeSubmissionResult> CorrectAsync(
        ClaimsPrincipal user,
        FounderLegendConnectCorrectionInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        return await _operations.CorrectFounderKnowledgeAsync(
            founder,
            input.SupersededAlignmentId,
            ToSubmission(input),
            cancellationToken);
    }

    public async Task<FounderLegendConnectOperationResult> ApproveProviderObservationAsync(
        ClaimsPrincipal user,
        FounderLegendConnectQualityReviewInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        var result = await _operations.ApproveProviderObservationAsync(founder, input.AlignmentId, cancellationToken);
        return new FounderLegendConnectOperationResult(result.Succeeded, result.Message);
    }

    public async Task<FounderLegendConnectOperationResult> RejectProviderObservationAsync(
        ClaimsPrincipal user,
        FounderLegendConnectQualityReviewInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        var result = await _operations.RejectProviderObservationAsync(founder, input.AlignmentId, cancellationToken);
        return new FounderLegendConnectOperationResult(result.Succeeded, result.Message);
    }

    public async Task<FounderLegendConnectOperationResult> LeaveProviderObservationUnresolvedAsync(
        ClaimsPrincipal user,
        FounderLegendConnectQualityReviewInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        var result = await _operations.LeaveProviderObservationUnresolvedAsync(founder, input.AlignmentId, cancellationToken);
        return new FounderLegendConnectOperationResult(result.Succeeded, result.Message);
    }

    public async Task<FounderLegendConnectOperationResult> VerifyTargetRealizationCandidateAsync(
        ClaimsPrincipal user,
        FounderLegendConnectTargetRealizationReviewInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        var result = await _operations.VerifyTargetRealizationCandidateAsync(founder, input.CandidateId, cancellationToken);
        return new FounderLegendConnectOperationResult(result.Succeeded, result.Message);
    }

    public async Task<FounderLegendConnectOperationResult> RejectTargetRealizationCandidateAsync(
        ClaimsPrincipal user,
        FounderLegendConnectTargetRealizationReviewInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        var result = await _operations.RejectTargetRealizationCandidateAsync(founder, input.CandidateId, cancellationToken);
        return new FounderLegendConnectOperationResult(result.Succeeded, result.Message);
    }

    public async Task<LegendConnectCurriculumSubmissionResult> SubmitCurriculumAsync(
        ClaimsPrincipal user,
        FounderLegendConnectCurriculumInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        if (!TryToCurriculumManifest(input, out var submission, out var error))
        {
            return new LegendConnectCurriculumSubmissionResult(
                false, false, "invalid_curriculum_manifest", error, null, null, 0, 0);
        }

        return await _operations.SubmitFounderCurriculumManifestAsync(founder, submission!, cancellationToken);
    }

    private static LegendConnectKnowledgeSubmission ToSubmission(FounderLegendConnectKnowledgeInput input) => new(
        input.SourceLanguageCode,
        input.SourceText,
        input.TargetLanguageCode,
        input.TargetText,
        input.ContextCategory,
        input.UsageRegister,
        input.RegionalVariant,
        "FounderApproved");

    private static bool TryToVerifiedTargetSubmission(
        FounderLegendConnectKnowledgeInput input,
        out LegendConnectVerifiedTargetSubmission? submission,
        out string? error)
    {
        submission = null;
        error = null;
        if (string.IsNullOrWhiteSpace(input.SourceLanguageCode) || string.IsNullOrWhiteSpace(input.TargetLanguageCode))
        {
            error = "Select both the existing source language and the target language to verify.";
            return false;
        }

        var lines = input.TargetTranslationRows?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        if (lines.Length == 0 || lines.Length > 500)
        {
            error = "Enter from 1 to 500 exact source | verified target rows.";
            return false;
        }

        var rows = new List<LegendConnectVerifiedTargetRow>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            var parts = lines[index].Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                error = $"Row {index + 1} must use exact source text | Founder-approved target text.";
                return false;
            }
            rows.Add(new LegendConnectVerifiedTargetRow(index + 1, parts[0], parts[1]));
        }

        submission = new LegendConnectVerifiedTargetSubmission(
            input.SourceLanguageCode,
            input.TargetLanguageCode,
            rows,
            input.ContextCategory,
            input.UsageRegister,
            input.RegionalVariant);
        return true;
    }

    /// <summary>
    /// Parses only explicit Founder-authored family boundaries. It does not
    /// infer semantic families or duplicate core curriculum validation.
    ///
    /// Format:
    /// @family conversation.greeting.basic | Conversation greeting
    /// @ground function -> salutation
    /// Hi. | function=greeting; intent=start_conversation
    /// @meaning
    /// @node act | predicate=greet | surface=Hi
    /// @node addressee | recipient=legend | surface=Legend
    /// @edge act -> addressee | relation=recipient
    /// @endmeaning
    /// Hello. | function=greeting; intent=start_conversation
    /// @transition
    /// @source function=request; intent=ask_information; subject=$subject
    /// @result function=inform; intent=provide_information; subject=$subject
    /// @endtransition
    /// @end
    ///
    /// A transition is a generic controlled semantic relation. It contains no
    /// stored prompt, answer, response template, or language-specific action;
    /// source and result curriculum examples remain its only surface evidence.
    /// </summary>
    private static bool TryToCurriculumManifest(
        FounderLegendConnectCurriculumInput input,
        out LegendConnectCurriculumManifestSubmission? submission,
        out string? error)
    {
        submission = null;
        error = null;

        var lines = input.Manifest?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        if (lines.Length == 0)
        {
            error = "Enter at least one explicit @family ... @end curriculum block.";
            return false;
        }

        var families = new List<LegendConnectCurriculumBatchSubmission>();
        var seenFamilyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? familyKey = null;
        string? semanticCategory = null;
        List<LegendConnectCurriculumExampleSubmission>? examples = null;
        List<LegendConnectSemanticTransitionSubmission>? transitions = null;
        List<LegendConnectSemanticSpanGroundingSubmission>? semanticSpanGroundings = null;
        int? meaningGraphExampleIndex = null;
        List<LegendConnectMeaningNodeSubmission>? meaningNodes = null;
        List<LegendConnectMeaningRelationSubmission>? meaningRelations = null;
        List<LegendConnectDiscourseReferenceSubmission>? meaningReferences = null;
        var crossExampleRelationships = new List<LegendConnectCrossExampleSemanticRelationshipSubmission>();
        IReadOnlyDictionary<string, string>? transitionSource = null;
        IReadOnlyDictionary<string, string>? transitionResult = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (TryReadCurriculumDirective(line, "relationship", out var relationshipSuffix))
            {
                if (familyKey is not null || transitionSource is not null || transitionResult is not null ||
                    meaningGraphExampleIndex is not null)
                {
                    error = $"Line {index + 1}: @relationship must appear after complete @family blocks, never inside a family or meaning graph.";
                    return false;
                }
                if (!TryParseCrossExampleSemanticRelationship(
                        relationshipSuffix,
                        out var relationship,
                        out var relationshipError))
                {
                    error = relationshipError ?? $"Line {index + 1}: use @relationship source_semantic_key -> result_semantic_key | semantic=controlled_relationship.";
                    return false;
                }
                if (crossExampleRelationships.Any(item =>
                        string.Equals(item.SourceSemanticExampleKey, relationship.SourceSemanticExampleKey, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.RelationshipSemanticIdentity, relationship.RelationshipSemanticIdentity, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.ResultSemanticExampleKey, relationship.ResultSemanticExampleKey, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"Line {index + 1}: the same governed cross-example relationship may appear only once per manifest.";
                    return false;
                }
                crossExampleRelationships.Add(relationship);
                continue;
            }

            if (TryReadCurriculumDirective(line, "semantic-example", out var semanticExampleSuffix))
            {
                if (familyKey is null || examples is null || examples.Count == 0 ||
                    transitionSource is not null || transitionResult is not null ||
                    meaningGraphExampleIndex is not null || string.IsNullOrWhiteSpace(semanticExampleSuffix))
                {
                    error = $"Line {index + 1}: @semantic-example must immediately follow one curriculum example before its @meaning block.";
                    return false;
                }
                var current = examples[^1];
                if (!string.IsNullOrWhiteSpace(current.SemanticExampleKey))
                {
                    error = $"Line {index + 1}: an example may declare only one opaque semantic-example identity.";
                    return false;
                }
                examples[^1] = current with { SemanticExampleKey = semanticExampleSuffix.Trim() };
                continue;
            }

            if (TryReadCurriculumDirective(line, "ground", out var groundingSuffix))
            {
                if (familyKey is null || examples is null || transitions is null ||
                    semanticSpanGroundings is null || transitionSource is not null || transitionResult is not null ||
                    meaningGraphExampleIndex is not null)
                {
                    error = $"Line {index + 1}: @ground must appear inside a family before a @transition block.";
                    return false;
                }
                if (!TryParseSemanticSpanGrounding(groundingSuffix, out var grounding, out var groundingError))
                {
                    error = groundingError ?? $"Line {index + 1}: use @ground semantic_dimension -> surface_dimension.";
                    return false;
                }
                if (semanticSpanGroundings.Any(item =>
                        string.Equals(item.SemanticDimension, grounding.SemanticDimension, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.SurfaceDimension, grounding.SurfaceDimension, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"Line {index + 1}: the same @ground relation may appear only once per family.";
                    return false;
                }
                semanticSpanGroundings.Add(grounding);
                continue;
            }

            if (TryReadCurriculumDirective(line, "meaning", out var meaningSuffix))
            {
                if (familyKey is null || examples is null || examples.Count == 0 ||
                    transitionSource is not null || transitionResult is not null ||
                    meaningGraphExampleIndex is not null || !string.IsNullOrWhiteSpace(meaningSuffix))
                {
                    error = $"Line {index + 1}: @meaning must immediately follow one curriculum example and cannot appear inside a transition.";
                    return false;
                }

                meaningGraphExampleIndex = examples.Count - 1;
                meaningNodes = [];
                meaningRelations = [];
                meaningReferences = [];
                continue;
            }

            if (TryReadCurriculumDirective(line, "node", out var nodeSuffix))
            {
                if (meaningGraphExampleIndex is null || meaningNodes is null || meaningRelations is null)
                {
                    error = $"Line {index + 1}: @node must appear inside a @meaning ... @endmeaning block.";
                    return false;
                }
                if (!TryParseMeaningNode(nodeSuffix, out var node, out var nodeError))
                {
                    error = nodeError ?? $"Line {index + 1}: use @node local_key | semantic_dimension=value | surface=exact span.";
                    return false;
                }
                if (meaningNodes.Any(item => string.Equals(item.NodeKey, node.NodeKey, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"Line {index + 1}: meaning node '{node.NodeKey}' appears more than once in this example.";
                    return false;
                }
                meaningNodes.Add(node);
                continue;
            }

            if (TryReadCurriculumDirective(line, "edge", out var edgeSuffix))
            {
                if (meaningGraphExampleIndex is null || meaningNodes is null || meaningRelations is null)
                {
                    error = $"Line {index + 1}: @edge must appear inside a @meaning ... @endmeaning block.";
                    return false;
                }
                if (!TryParseMeaningRelation(edgeSuffix, out var relation, out var relationError))
                {
                    error = relationError ?? $"Line {index + 1}: use @edge source_key -> target_key | relation=controlled_relation.";
                    return false;
                }
                if (meaningRelations.Any(item =>
                        string.Equals(item.SourceNodeKey, relation.SourceNodeKey, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.TargetNodeKey, relation.TargetNodeKey, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.RelationKind, relation.RelationKind, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.ClauseKey, relation.ClauseKey, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"Line {index + 1}: the same directed meaning relation may appear only once per example.";
                    return false;
                }
                meaningRelations.Add(relation);
                continue;
            }

            if (TryReadCurriculumDirective(line, "reference", out var referenceSuffix))
            {
                if (meaningGraphExampleIndex is null || meaningNodes is null || meaningRelations is null ||
                    meaningReferences is null)
                {
                    error = $"Line {index + 1}: @reference must appear inside a @meaning ... @endmeaning block.";
                    return false;
                }
                if (!TryParseDiscourseReference(referenceSuffix, out var reference, out var referenceError))
                {
                    error = referenceError ?? $"Line {index + 1}: use @reference selector_node | entity_dimension=controlled_dimension | resolution=ordinal|unique.";
                    return false;
                }
                if (meaningReferences.Any(item =>
                        string.Equals(item.SelectorNodeKey, reference.SelectorNodeKey, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.EntitySemanticDimension, reference.EntitySemanticDimension, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.ResolutionMode, reference.ResolutionMode, StringComparison.OrdinalIgnoreCase) &&
                        item.SelectionRank == reference.SelectionRank &&
                        item.ReplacesActiveBinding == reference.ReplacesActiveBinding))
                {
                    error = $"Line {index + 1}: the same governed discourse reference may appear only once per example.";
                    return false;
                }
                meaningReferences.Add(reference);
                continue;
            }

            if (TryReadCurriculumDirective(line, "endmeaning", out var endMeaningSuffix) &&
                string.IsNullOrWhiteSpace(endMeaningSuffix))
            {
                if (meaningGraphExampleIndex is null || meaningNodes is null || meaningRelations is null || meaningReferences is null ||
                    examples is null || meaningNodes.Count == 0)
                {
                    error = $"Line {index + 1}: @endmeaning requires at least one explicit @node.";
                    return false;
                }

                var example = examples[meaningGraphExampleIndex.Value];
                examples[meaningGraphExampleIndex.Value] = example with
                {
                    MeaningGraph = new LegendConnectMeaningGraphSubmission(
                        meaningNodes.ToArray(),
                        meaningRelations.ToArray(),
                        meaningReferences.ToArray())
                };
                meaningGraphExampleIndex = null;
                meaningNodes = null;
                meaningRelations = null;
                meaningReferences = null;
                continue;
            }

            if (TryReadCurriculumDirective(line, "transition", out var transitionSuffix))
            {
                if (familyKey is null || examples is null || transitions is null)
                {
                    error = $"Line {index + 1}: a semantic transition must be inside an explicit @family ... @end block.";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(transitionSuffix) || transitionSource is not null || transitionResult is not null ||
                    meaningGraphExampleIndex is not null)
                {
                    error = $"Line {index + 1}: use @transition, then explicit @source and @result semantic frames, then @endtransition.";
                    return false;
                }
                transitionSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (TryReadCurriculumDirective(line, "source", out var sourceSuffix))
            {
                if (transitionSource is null || transitionResult is not null)
                {
                    error = $"Line {index + 1}: @source must appear once inside an open @transition block before @result.";
                    return false;
                }
                if (!TryParseSemanticDimensions(sourceSuffix, out var sourceDimensions, out var sourceError))
                {
                    error = sourceError ?? $"Line {index + 1}: @source requires one or more unique dimension=value entries.";
                    return false;
                }
                transitionSource = sourceDimensions;
                continue;
            }

            if (TryReadCurriculumDirective(line, "result", out var resultSuffix))
            {
                if (transitionSource is null || transitionResult is not null)
                {
                    error = $"Line {index + 1}: @result must appear once after @source inside an open @transition block.";
                    return false;
                }
                if (!TryParseSemanticDimensions(resultSuffix, out var resultDimensions, out var resultError))
                {
                    error = resultError ?? $"Line {index + 1}: @result requires one or more unique dimension=value entries.";
                    return false;
                }
                transitionResult = resultDimensions;
                continue;
            }

            if (TryReadCurriculumDirective(line, "endtransition", out var endTransitionSuffix) &&
                string.IsNullOrWhiteSpace(endTransitionSuffix))
            {
                if (transitionSource is null || transitionSource.Count == 0 || transitionResult is null ||
                    transitionResult.Count == 0 || transitions is null)
                {
                    error = $"Line {index + 1}: @endtransition requires one explicit @source and one explicit @result frame.";
                    return false;
                }
                transitions.Add(new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(transitionSource),
                    new LegendConnectSemanticFrameSubmission(transitionResult)));
                transitionSource = null;
                transitionResult = null;
                continue;
            }

            if (TryReadCurriculumDirective(line, "family", out var header))
            {
                if (familyKey is not null || transitionSource is not null || transitionResult is not null ||
                    meaningGraphExampleIndex is not null)
                {
                    error = $"Line {index + 1}: close family '{familyKey}' with @end before starting another family.";
                    return false;
                }

                var headerParts = header.Split('|', 2, StringSplitOptions.TrimEntries);
                if (headerParts.Length == 0 || string.IsNullOrWhiteSpace(headerParts[0]))
                {
                    error = $"Line {index + 1}: use @family family.key | Semantic category.";
                    return false;
                }

                familyKey = headerParts[0];
                semanticCategory = headerParts.Length == 2 && !string.IsNullOrWhiteSpace(headerParts[1])
                    ? headerParts[1]
                    : null;
                if (!seenFamilyKeys.Add(familyKey))
                {
                    error = $"Line {index + 1}: family '{familyKey}' appears more than once in this manifest. Put all controlled examples for that family in one block.";
                    return false;
                }

                examples = [];
                transitions = [];
                semanticSpanGroundings = [];
                meaningNodes = null;
                meaningRelations = null;
                meaningReferences = null;
                continue;
            }

            if (TryReadCurriculumDirective(line, "end", out var endSuffix) &&
                string.IsNullOrWhiteSpace(endSuffix))
            {
                if (familyKey is null || examples is null || transitions is null)
                {
                    error = $"Line {index + 1}: @end has no open @family block.";
                    return false;
                }
                if (transitionSource is not null || transitionResult is not null)
                {
                    error = $"Line {index + 1}: close the semantic transition with @endtransition before closing family '{familyKey}'.";
                    return false;
                }
                if (meaningGraphExampleIndex is not null)
                {
                    error = $"Line {index + 1}: close the meaning graph with @endmeaning before closing family '{familyKey}'.";
                    return false;
                }

                families.Add(new LegendConnectCurriculumBatchSubmission(
                    familyKey,
                    semanticCategory,
                    examples,
                    transitions,
                    semanticSpanGroundings));
                familyKey = null;
                semanticCategory = null;
                examples = null;
                transitions = null;
                semanticSpanGroundings = null;
                meaningNodes = null;
                meaningRelations = null;
                meaningReferences = null;
                continue;
            }

            if (familyKey is null || examples is null)
            {
                error = $"Line {index + 1}: curriculum examples must be inside an explicit @family ... @end block.";
                return false;
            }
            if (meaningGraphExampleIndex is not null)
            {
                error = $"Line {index + 1}: close the current @meaning block before entering another curriculum example.";
                return false;
            }

            var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
            {
                error = $"Line {index + 1}: enter each example as English text | dimension=value; dimension=value.";
                return false;
            }

            if (!TryParseSemanticDimensions(parts[1], out var variations, out _))
            {
                error = $"Line {index + 1}: each curriculum example needs at least one controlled variation.";
                return false;
            }

            examples.Add(new LegendConnectCurriculumExampleSubmission(parts[0], variations));
        }

        if (familyKey is not null)
        {
            error = $"Family '{familyKey}' is missing its closing @end.";
            return false;
        }
        if (transitionSource is not null || transitionResult is not null)
        {
            error = "The curriculum manifest has an unclosed semantic transition.";
            return false;
        }
        if (meaningGraphExampleIndex is not null)
        {
            error = "The curriculum manifest has an unclosed @meaning block.";
            return false;
        }
        if (families.Count == 0)
        {
            error = "The curriculum manifest did not contain a complete @family ... @end block.";
            return false;
        }

        submission = new LegendConnectCurriculumManifestSubmission(
            families,
            crossExampleRelationships);
        return true;
    }

    private static bool TryParseCrossExampleSemanticRelationship(
        string input,
        out LegendConnectCrossExampleSemanticRelationshipSubmission relationship,
        out string? error)
    {
        relationship = null!;
        error = null;
        var parts = input.Split('|', 2, StringSplitOptions.TrimEntries);
        var endpoints = parts[0].Split("->", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || endpoints.Length != 2 ||
            string.IsNullOrWhiteSpace(endpoints[0]) || string.IsNullOrWhiteSpace(endpoints[1]))
        {
            error = "A governed cross-example relationship requires source_key -> result_key | semantic=relationship_identity.";
            return false;
        }
        var semantic = parts[1].Split('=', 2, StringSplitOptions.TrimEntries);
        if (semantic.Length != 2 || !string.Equals(semantic[0], "semantic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(semantic[1]))
        {
            error = "A governed cross-example relationship requires the semantic=relationship_identity field.";
            return false;
        }
        relationship = new LegendConnectCrossExampleSemanticRelationshipSubmission(
            endpoints[0], semantic[1], endpoints[1]);
        return true;
    }

    private static bool TryParseSemanticDimensions(
        string input,
        out IReadOnlyDictionary<string, string> dimensions,
        out string? error)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) ||
                string.IsNullOrWhiteSpace(pair[1]) || !parsed.TryAdd(pair[0], pair[1]))
            {
                dimensions = parsed;
                error = "Controlled semantic dimensions must use unique dimension=value entries separated by semicolons.";
                return false;
            }
        }

        dimensions = parsed;
        error = parsed.Count == 0
            ? "At least one controlled semantic dimension is required."
            : null;
        return parsed.Count > 0;
    }

    private static bool TryParseSemanticSpanGrounding(
        string input,
        out LegendConnectSemanticSpanGroundingSubmission grounding,
        out string? error)
    {
        grounding = new LegendConnectSemanticSpanGroundingSubmission(string.Empty, string.Empty);
        error = null;
        var parts = input.Split("->", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]) ||
            !IsSemanticDimensionName(parts[0]) || !IsSemanticDimensionName(parts[1]))
        {
            error = "A grounding must use semantic_dimension -> surface_dimension with valid dimension names.";
            return false;
        }

        grounding = new LegendConnectSemanticSpanGroundingSubmission(parts[0], parts[1]);
        return true;
    }

    private static bool TryParseMeaningNode(
        string input,
        out LegendConnectMeaningNodeSubmission node,
        out string? error)
    {
        node = new LegendConnectMeaningNodeSubmission(string.Empty, string.Empty, string.Empty, string.Empty);
        error = null;
        var parts = input.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 5 || !IsSemanticDimensionName(parts[0]))
        {
            error = "A meaning node must use local_key | semantic_dimension=value | surface=exact span [| clause=local_clause] [| occurrence=positive_integer].";
            return false;
        }

        var semantic = parts[1].Split('=', 2, StringSplitOptions.TrimEntries);
        var surface = parts[2].Split('=', 2, StringSplitOptions.TrimEntries);
        if (semantic.Length != 2 || !IsSemanticDimensionName(semantic[0]) ||
            string.IsNullOrWhiteSpace(semantic[1]) || surface.Length != 2 ||
            !string.Equals(surface[0], "surface", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(surface[1]))
        {
            error = "A meaning node needs one valid semantic_dimension=value and one surface=exact span declaration.";
            return false;
        }

        string? clause = null;
        var occurrence = 1;
        var occurrenceDeclared = false;
        for (var partIndex = 3; partIndex < parts.Length; partIndex++)
        {
            var option = parts[partIndex].Split('=', 2, StringSplitOptions.TrimEntries);
            if (option.Length != 2)
            {
                error = "A meaning node option must use clause=local_clause or occurrence=positive_integer.";
                return false;
            }
            if (string.Equals(option[0], "clause", StringComparison.OrdinalIgnoreCase) &&
                clause is null && IsSemanticDimensionName(option[1]))
            {
                clause = option[1];
                continue;
            }
            if (string.Equals(option[0], "occurrence", StringComparison.OrdinalIgnoreCase) &&
                !occurrenceDeclared && int.TryParse(option[1], out var parsedOccurrence) &&
                parsedOccurrence is >= 1 and <= 32)
            {
                occurrence = parsedOccurrence;
                occurrenceDeclared = true;
                continue;
            }

            error = "A meaning node option must use one clause=local_clause and/or one occurrence=positive_integer.";
            return false;
        }

        node = new LegendConnectMeaningNodeSubmission(
            parts[0], semantic[0], semantic[1], surface[1], clause, occurrence);
        return true;
    }

    private static bool TryParseMeaningRelation(
        string input,
        out LegendConnectMeaningRelationSubmission relation,
        out string? error)
    {
        relation = new LegendConnectMeaningRelationSubmission(string.Empty, string.Empty, string.Empty);
        error = null;
        var parts = input.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3)
        {
            error = "A meaning relation must use source_key -> target_key | relation=controlled_relation [| clause=local_clause].";
            return false;
        }

        var endpoints = parts[0].Split("->", 2, StringSplitOptions.TrimEntries);
        var relationParts = parts[1].Split('=', 2, StringSplitOptions.TrimEntries);
        if (endpoints.Length != 2 || !IsSemanticDimensionName(endpoints[0]) ||
            !IsSemanticDimensionName(endpoints[1]) ||
            string.Equals(endpoints[0], endpoints[1], StringComparison.OrdinalIgnoreCase) ||
            relationParts.Length != 2 || !string.Equals(relationParts[0], "relation", StringComparison.OrdinalIgnoreCase) ||
            !IsSemanticDimensionName(relationParts[1]))
        {
            error = "A meaning relation needs distinct valid source/target keys and relation=controlled_relation.";
            return false;
        }

        string? clause = null;
        if (parts.Length == 3)
        {
            var clauseParts = parts[2].Split('=', 2, StringSplitOptions.TrimEntries);
            if (clauseParts.Length != 2 || !string.Equals(clauseParts[0], "clause", StringComparison.OrdinalIgnoreCase) ||
                !IsSemanticDimensionName(clauseParts[1]))
            {
                error = "A meaning relation clause must use clause=local_clause.";
                return false;
            }
            clause = clauseParts[1];
        }

        relation = new LegendConnectMeaningRelationSubmission(
            endpoints[0], relationParts[1], endpoints[1], clause);
        return true;
    }

    private static bool TryParseDiscourseReference(
        string input,
        out LegendConnectDiscourseReferenceSubmission reference,
        out string? error)
    {
        reference = new LegendConnectDiscourseReferenceSubmission(
            string.Empty, string.Empty, string.Empty);
        error = null;
        var parts = input.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 6 || !IsSemanticDimensionName(parts[0]))
        {
            error = "A discourse reference must use selector_node | entity_dimension=controlled_dimension | resolution=ordinal|unique [| rank=positive_integer] [| roles=user,assistant] [| replace_active=true].";
            return false;
        }

        string? entityDimension = null;
        string? resolution = null;
        int? rank = null;
        IReadOnlyList<string>? roles = null;
        var replaceActive = false;
        for (var index = 1; index < parts.Length; index++)
        {
            var option = parts[index].Split('=', 2, StringSplitOptions.TrimEntries);
            if (option.Length != 2)
            {
                error = "Each @reference option must use name=value.";
                return false;
            }
            if (string.Equals(option[0], "entity_dimension", StringComparison.OrdinalIgnoreCase) &&
                entityDimension is null && IsSemanticDimensionName(option[1]))
            {
                entityDimension = option[1];
                continue;
            }
            if (string.Equals(option[0], "resolution", StringComparison.OrdinalIgnoreCase) &&
                resolution is null && option[1] is "ordinal" or "unique")
            {
                resolution = option[1];
                continue;
            }
            if (string.Equals(option[0], "rank", StringComparison.OrdinalIgnoreCase) && rank is null &&
                int.TryParse(option[1], out var parsedRank) && parsedRank is >= 1 and <= 16)
            {
                rank = parsedRank;
                continue;
            }
            if (string.Equals(option[0], "roles", StringComparison.OrdinalIgnoreCase) && roles is null)
            {
                var parsedRoles = option[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(item => item.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                if (parsedRoles.Length is < 1 or > 2 || parsedRoles.Any(item => item is not ("user" or "assistant")))
                {
                    error = "A discourse reference roles declaration may contain only user and/or assistant.";
                    return false;
                }
                roles = parsedRoles;
                continue;
            }
            if (string.Equals(option[0], "replace_active", StringComparison.OrdinalIgnoreCase) &&
                !replaceActive && bool.TryParse(option[1], out var parsedReplace) && parsedReplace)
            {
                replaceActive = true;
                continue;
            }

            error = "A discourse reference may declare entity_dimension, resolution, rank, roles, and replace_active=true once each.";
            return false;
        }

        if (entityDimension is null || resolution is null ||
            (resolution == "ordinal" && rank is null) ||
            (resolution == "unique" && rank is not null))
        {
            error = "An ordinal discourse reference requires rank=positive_integer; a unique discourse reference must not declare rank.";
            return false;
        }

        reference = new LegendConnectDiscourseReferenceSubmission(
            parts[0], entityDimension, resolution, rank, roles ?? ["user", "assistant"], replaceActive);
        return true;
    }

    private static bool IsSemanticDimensionName(string value) =>
        value.Length <= 80 && value.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    private static FounderSoftwareRemediationStatusSnapshot ToRemediationSnapshot(object result) =>
        result as FounderSoftwareRemediationStatusSnapshot ??
        FounderSoftwareRemediationStatusSnapshot.Unconfigured(
            "The remediation authority returned an unavailable state. No repository or production action was attempted.");

    /// <summary>
    /// The live Founder form and authoring guide use @@family / @@end, while
    /// early integrations used @family / @end. Both are explicit delimiter
    /// syntaxes for the same manifest format; accepting them here keeps one
    /// parser and one curriculum authority without rewriting submitted input.
    /// </summary>
    private static bool TryReadCurriculumDirective(
        string line,
        string directive,
        out string suffix)
    {
        suffix = string.Empty;
        var doublePrefix = "@@" + directive;
        var singlePrefix = "@" + directive;
        var prefix = line.StartsWith(doublePrefix, StringComparison.OrdinalIgnoreCase)
            ? doublePrefix
            : line.StartsWith(singlePrefix, StringComparison.OrdinalIgnoreCase)
                ? singlePrefix
                : null;

        if (prefix is null ||
            (line.Length > prefix.Length &&
             !char.IsWhiteSpace(line[prefix.Length])))
        {
            return false;
        }

        suffix = line[prefix.Length..].Trim();
        return true;
    }

    /// <summary>
    /// Connects the same authenticated principal that passed <see cref="FounderGuard"/>
    /// to its existing, active AgentPortal account. This is deliberately not a
    /// Legend Connect lookup: <see cref="AgentProfileAccessResolver"/> is the
    /// portal's canonical Agent reconciliation authority, including its
    /// server-side object-ID-first resolution and historical directory-email
    /// reconciliation for an already-provisioned profile.
    /// </summary>
    private async Task<string> ResolveFounderActorAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(user);

        var profile = await _agentProfiles.ResolveCurrentAsync(
            user,
            requireActive: true,
            cancellationToken);
        var agentUserId = profile?.AgentUserId?.Trim();
        if (string.IsNullOrWhiteSpace(agentUserId))
            throw new ForbidResultException();

        return agentUserId.ToLowerInvariant();
    }
}
