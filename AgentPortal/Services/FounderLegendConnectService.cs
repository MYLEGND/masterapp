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

    public FounderLegendConnectService(
        ILegendConnectOperations operations,
        AgentProfileAccessResolver agentProfiles,
        ITranslationEntitlementAuthority? entitlements = null,
        IMessagingService? messaging = null,
        ILegendConnectRuntimePolicyAuthority? runtimePolicy = null)
    {
        _operations = operations;
        _agentProfiles = agentProfiles;
        _entitlements = entitlements;
        _messaging = messaging;
        _runtimePolicy = runtimePolicy;
    }

    public async Task<FounderLegendConnectDashboardVm> GetDashboardAsync(
        ClaimsPrincipal user,
        string? language,
        string? pair,
        CancellationToken cancellationToken = default,
        string? accountSearch = null)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        var dashboard = await _operations.GetDashboardAsync(cancellationToken);
        var translationQuality = await _operations.GetTranslationQualityAsync(cancellationToken);
        var selectedLanguageKnowledge = string.IsNullOrWhiteSpace(language)
            ? null
            : await _operations.GetLanguageKnowledgeAsync(language, cancellationToken);
        var selectedPair = string.IsNullOrWhiteSpace(pair)
            ? null
            : await _operations.GetPairHealthAsync(pair, cancellationToken);
        var accountDirectory = _entitlements is null
            ? new TranslationFounderAccountSearchSnapshot(
                Array.Empty<TranslationFounderAccountUsageSnapshot>(),
                null,
                false)
            : await _entitlements.SearchFounderAccountsAsync(accountSearch, 8, cancellationToken);
        var accountScale = _entitlements is null
            ? new TranslationFounderScaleSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0)
            : await _entitlements.GetFounderScaleAsync(cancellationToken);
        var runtimePolicy = _runtimePolicy is null
            ? new LegendConnectRuntimePolicySnapshot(false, 0, 0, 0, false, true, "Shadow", 0.98m, null, null, DateTime.MinValue)
            : await _runtimePolicy.GetEffectiveAsync(cancellationToken);
        var readiness = _runtimePolicy is null
            ? new LegendConnectProductionReadinessSnapshot("BLOCKED", false, "Legend Connect runtime policy authority is unavailable.", Array.Empty<LegendConnectReadinessCheck>(), 0, 0, 0, 0, 0)
            : await _runtimePolicy.GetReadinessAsync(cancellationToken);
        var runtimeAudit = _runtimePolicy is null
            ? Array.Empty<LegendConnectFounderOperationalAuditSnapshot>()
            : await _runtimePolicy.GetRecentAuditAsync(cancellationToken: cancellationToken);
        return new FounderLegendConnectDashboardVm
        {
            Dashboard = dashboard,
            SelectedLanguage = selectedLanguageKnowledge?.Health,
            SelectedLanguageKnowledge = selectedLanguageKnowledge,
            SelectedPair = selectedPair,
            TranslationQuality = translationQuality,
            AccountUsage = accountDirectory.Accounts,
            AccountSearchQuery = accountDirectory.Query,
            HasAdditionalAccountResults = accountDirectory.HasMore,
            AccountScale = accountScale,
            EntitlementPresets = _entitlements?.GetFounderEntitlementPresets() ?? Array.Empty<TranslationEntitlementPreset>(),
            RuntimePolicy = runtimePolicy,
            ProductionReadiness = readiness,
            RuntimeAudit = runtimeAudit
        };
    }

    public async Task<LegendConnectProviderCapacitySnapshot> GetProviderCapacityAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        _ = await ResolveFounderActorAsync(user, cancellationToken);
        return await _operations.GetProviderCapacityAsync(cancellationToken);
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

    public async Task<LegendConnectCurriculumSubmissionResult> SubmitCurriculumAsync(
        ClaimsPrincipal user,
        FounderLegendConnectCurriculumInput input,
        CancellationToken cancellationToken = default)
    {
        var founder = await ResolveFounderActorAsync(user, cancellationToken);
        if (!TryToCurriculumSubmission(input, out var submission, out var error))
        {
            return new LegendConnectCurriculumSubmissionResult(
                false, false, "invalid_curriculum_examples", error, input.FamilyKey?.Trim(), null, 0, 0);
        }
        return await _operations.SubmitFounderCurriculumAsync(founder, submission!, cancellationToken);
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

    private static bool TryToCurriculumSubmission(
        FounderLegendConnectCurriculumInput input,
        out LegendConnectCurriculumBatchSubmission? submission,
        out string? error)
    {
        submission = null;
        error = null;
        var examples = new List<LegendConnectCurriculumExampleSubmission>();
        var lines = input.Examples?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        foreach (var line in lines)
        {
            var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
            {
                error = "Enter each example as English text | dimension=value; dimension=value.";
                return false;
            }
            var variations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pair = item.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || string.IsNullOrWhiteSpace(pair[1]) ||
                    !variations.TryAdd(pair[0], pair[1]))
                {
                    error = "Each controlled variation must use dimension=value and dimensions cannot repeat within an example.";
                    return false;
                }
            }
            if (variations.Count == 0)
            {
                error = "Each curriculum example needs at least one controlled variation.";
                return false;
            }
            examples.Add(new LegendConnectCurriculumExampleSubmission(parts[0], variations));
        }
        submission = new LegendConnectCurriculumBatchSubmission(
            input.FamilyKey,
            input.SemanticCategory,
            examples);
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
