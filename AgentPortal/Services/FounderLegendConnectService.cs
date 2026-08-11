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
    private readonly ITranslationEntitlementAuthority? _entitlements;
    private readonly IMessagingService? _messaging;
    private readonly ILegendConnectRuntimePolicyAuthority? _runtimePolicy;

    public FounderLegendConnectService(
        ILegendConnectOperations operations,
        ITranslationEntitlementAuthority? entitlements = null,
        IMessagingService? messaging = null,
        ILegendConnectRuntimePolicyAuthority? runtimePolicy = null)
    {
        _operations = operations;
        _entitlements = entitlements;
        _messaging = messaging;
        _runtimePolicy = runtimePolicy;
    }

    public async Task<FounderLegendConnectDashboardVm> GetDashboardAsync(
        ClaimsPrincipal user,
        string? language,
        string? pair,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        var dashboard = await _operations.GetDashboardAsync(cancellationToken);
        var selectedLanguageKnowledge = string.IsNullOrWhiteSpace(language)
            ? null
            : await _operations.GetLanguageKnowledgeAsync(language, cancellationToken);
        var selectedPair = string.IsNullOrWhiteSpace(pair)
            ? null
            : await _operations.GetPairHealthAsync(pair, cancellationToken);
        var accountUsage = _entitlements is null
            ? Array.Empty<TranslationFounderAccountUsageSnapshot>()
            : await _entitlements.ListFounderAccountsAsync(cancellationToken);
        var accountScale = _entitlements is null
            ? new TranslationFounderScaleSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0)
            : await _entitlements.GetFounderScaleAsync(cancellationToken);
        var runtimePolicy = _runtimePolicy is null
            ? new LegendConnectRuntimePolicySnapshot(false, 0, 0, 0, false, true, "Shadow", 0.98m, "Automatic", null, null, null, null, null, DateTime.MinValue)
            : await _runtimePolicy.GetEffectiveAsync(cancellationToken);
        var readiness = _runtimePolicy is null
            ? new LegendConnectProductionReadinessSnapshot("BLOCKED", false, "Legend Connect runtime policy authority is unavailable.", Array.Empty<LegendConnectReadinessCheck>(), 0, 0, 0, 0, 0)
            : await _runtimePolicy.GetReadinessAsync(cancellationToken);
        var priorityProgress = _runtimePolicy is null
            ? new LegendConnectPriorityProgressSnapshot("AUTOMATIC — DEMAND DRIVEN", 0, 0, 0, 0m, 0, null, null)
            : await _runtimePolicy.GetPriorityProgressAsync(cancellationToken);
        var runtimeAudit = _runtimePolicy is null
            ? Array.Empty<LegendConnectFounderOperationalAuditSnapshot>()
            : await _runtimePolicy.GetRecentAuditAsync(cancellationToken: cancellationToken);
        return new FounderLegendConnectDashboardVm
        {
            Dashboard = dashboard,
            SelectedLanguage = selectedLanguageKnowledge?.Health,
            SelectedLanguageKnowledge = selectedLanguageKnowledge,
            SelectedPair = selectedPair,
            AccountUsage = accountUsage,
            AccountScale = accountScale,
            EntitlementPresets = _entitlements?.GetFounderEntitlementPresets() ?? Array.Empty<TranslationEntitlementPreset>(),
            RuntimePolicy = runtimePolicy,
            ProductionReadiness = readiness,
            PriorityProgress = priorityProgress,
            RuntimeAudit = runtimeAudit
        };
    }

    public async Task<FounderLegendConnectOperationResult> UpdateRuntimePolicyAsync(
        ClaimsPrincipal user,
        FounderLegendConnectRuntimePolicyInput input,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        if (_runtimePolicy is null)
            return new FounderLegendConnectOperationResult(false, "Legend Connect runtime policy authority is unavailable.");
        try
        {
            await _runtimePolicy.UpdateAsync(FounderId(user), new LegendConnectRuntimePolicyMutation(
                input.MonthlyProviderCapacityCharacters,
                input.LiveTranslationReserveCharacters,
                input.MaximumSafeCorpusConsumptionCharacters,
                input.LearningEnabled,
                input.ContextualCompositionMode,
                input.ContextualMinimumConfidence), cancellationToken);
            return new FounderLegendConnectOperationResult(true, "Legend Connect runtime policy was saved across the deployment.");
        }
        catch (ArgumentException exception)
        {
            return new FounderLegendConnectOperationResult(false, exception.Message);
        }
    }

    public async Task<FounderLegendConnectOperationResult> ActivateAutonomousAcquisitionAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        if (_runtimePolicy is null)
            return new FounderLegendConnectOperationResult(false, "Legend Connect runtime policy authority is unavailable.");
        var readiness = await _runtimePolicy.ActivateAsync(FounderId(user), cancellationToken);
        return new FounderLegendConnectOperationResult(
            readiness.State is "ACTIVE" or "ACTIVE — NO ELIGIBLE WORK",
            readiness.Summary);
    }

    public async Task<FounderLegendConnectOperationResult> PauseAutonomousAcquisitionAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        if (_runtimePolicy is null)
            return new FounderLegendConnectOperationResult(false, "Legend Connect runtime policy authority is unavailable.");
        await _runtimePolicy.PauseAsync(FounderId(user), cancellationToken);
        return new FounderLegendConnectOperationResult(true, "Autonomous acquisition is paused. Live communication and Azure fallback remain available.");
    }

    public async Task<FounderLegendConnectOperationResult> ConfigurePriorityOverrideAsync(
        ClaimsPrincipal user,
        FounderLegendConnectPriorityOverrideInput input,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        if (_runtimePolicy is null)
            return new FounderLegendConnectOperationResult(false, "Legend Connect runtime policy authority is unavailable.");
        try
        {
            await _runtimePolicy.ConfigurePriorityOverrideAsync(FounderId(user),
                new LegendConnectPriorityOverrideMutation(input.LanguageCode, input.PairKey, null), cancellationToken);
            return new FounderLegendConnectOperationResult(true, "Founder priority override is active. The existing planner now orders only eligible matching work first.");
        }
        catch (ArgumentException exception)
        {
            return new FounderLegendConnectOperationResult(false, exception.Message);
        }
    }

    public async Task<FounderLegendConnectOperationResult> DisablePriorityOverrideAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        if (_runtimePolicy is null)
            return new FounderLegendConnectOperationResult(false, "Legend Connect runtime policy authority is unavailable.");
        await _runtimePolicy.DisablePriorityOverrideAsync(FounderId(user), cancellationToken);
        return new FounderLegendConnectOperationResult(true, "Founder priority override is disabled. The existing demand-driven planner is active immediately.");
    }

    public async Task<FounderLegendConnectEntitlementResult> UpdateEntitlementAsync(
        ClaimsPrincipal user,
        FounderLegendConnectEntitlementInput input,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        if (_entitlements is null || _messaging is null)
            return new FounderLegendConnectEntitlementResult(false, "Legend Connect entitlement authority is unavailable.");

        var targetUserId = input.TargetUserId?.Trim().ToLowerInvariant() ?? string.Empty;
        var participantType = input.TargetParticipantType?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetUserId) || string.IsNullOrWhiteSpace(participantType))
            return new FounderLegendConnectEntitlementResult(false, "Choose a valid LEGEND account.");

        var grant = await _messaging.SetControlledResourceGrantAsync(
            new SetControlledResourceGrantCommand(
                new MessagingActor(FounderId(user), MessagingParticipantTypes.Agent),
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

        await _entitlements.SetEntitlementAsync(
            FounderId(user),
            new TranslationEntitlementMutation(
                new MessagingActor(targetUserId, participantType),
                allowance,
                unlimited,
                source,
                IsFounderOverride: true),
            cancellationToken);
        return new FounderLegendConnectEntitlementResult(
            true,
            unlimited
                ? "Unlimited translation entitlement was saved and remains fully metered."
                : "Translation access and the server-owned monthly allowance were saved.");
    }

    public Task<LegendConnectKnowledgeSubmissionResult> SubmitAsync(
        ClaimsPrincipal user,
        FounderLegendConnectKnowledgeInput input,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        return _operations.SubmitFounderKnowledgeAsync(
            FounderId(user),
            ToSubmission(input),
            cancellationToken);
    }

    public Task<LegendConnectKnowledgeSubmissionResult> CorrectAsync(
        ClaimsPrincipal user,
        FounderLegendConnectCorrectionInput input,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);
        return _operations.CorrectFounderKnowledgeAsync(
            FounderId(user),
            input.SupersededAlignmentId,
            ToSubmission(input),
            cancellationToken);
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

    private static string FounderId(ClaimsPrincipal user) =>
        user.FindFirst("oid")?.Value?.Trim() ?? string.Empty;
}
