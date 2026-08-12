namespace Domain.Messaging;

/// <summary>
/// Effective deployment-wide Legend Connect settings. Values are server-owned
/// and deliberately contain neither provider credentials nor corpus text.
/// </summary>
public sealed record LegendConnectRuntimePolicySnapshot(
    bool IsPersisted,
    long MonthlyProviderCapacityCharacters,
    long LiveTranslationReserveCharacters,
    long MaximumSafeCorpusConsumptionCharacters,
    bool CorpusAcquisitionEnabled,
    bool LearningEnabled,
    string ContextualCompositionMode,
    decimal ContextualMinimumConfidence,
    string PriorityMode,
    string? PriorityLanguageCode,
    string? PriorityPairKey,
    string? PriorityLevel,
    DateTime? LastLearningWorkerHeartbeatUtc,
    DateTime? LastAcquisitionWorkerHeartbeatUtc,
    DateTime UpdatedUtc)
{
    /// <summary>
    /// When populated, autonomous acquisition is deliberately scoped to
    /// English-source Founder learning sets expanded into these targets.
    /// An empty set keeps the ordinary demand-driven planner in control.
    /// </summary>
    public IReadOnlyList<string> FocusedTargetLanguageCodes { get; init; } = Array.Empty<string>();
}

public sealed record LegendConnectRuntimePolicyMutation(
    long MonthlyProviderCapacityCharacters,
    long LiveTranslationReserveCharacters,
    long MaximumSafeCorpusConsumptionCharacters,
    bool LearningEnabled,
    string ContextualCompositionMode,
    decimal ContextualMinimumConfidence);

public sealed record LegendConnectPriorityOverrideMutation(
    string? LanguageCode,
    string? PairKey,
    string? PriorityLevel);

public sealed record LegendConnectAutonomousLanguageFocusMutation(
    bool Enabled,
    IReadOnlyCollection<string>? TargetLanguageCodes);

public sealed record LegendConnectReadinessCheck(
    string Name,
    string State,
    string Detail);

public sealed record LegendConnectProductionReadinessSnapshot(
    string State,
    bool CanActivate,
    string Summary,
    IReadOnlyList<LegendConnectReadinessCheck> Checks,
    long ApprovedCandidateCount,
    long PendingCandidateCount,
    long RejectedOrIneligibleCandidateCount,
    long DuplicateCandidateCount,
    long AwaitingKnowledgePairCount);

public sealed record LegendConnectPriorityProgressSnapshot(
    string Status,
    long EligibleMissingCandidateCount,
    long PendingCandidateCount,
    long CurrentInternalCoverage,
    decimal AzureDependencyRate,
    long CharactersConsumed,
    DateTime? LastSuccessfulExpansionUtc,
    string? Detail);

public sealed record LegendConnectFounderOperationalAuditSnapshot(
    string FounderUserId,
    string Action,
    string Result,
    string? LanguageCode,
    string? PairKey,
    string? Detail,
    DateTime OccurredUtc);

/// <summary>
/// One runtime control-plane authority. It is intentionally distinct from the
/// provider and acquisition pipeline: it persists policy, evaluates readiness,
/// and supplies policy snapshots to those existing components.
/// </summary>
public interface ILegendConnectRuntimePolicyAuthority
{
    Task<LegendConnectRuntimePolicySnapshot> GetEffectiveAsync(
        CancellationToken cancellationToken = default);

    Task<LegendConnectProductionReadinessSnapshot> GetReadinessAsync(
        CancellationToken cancellationToken = default);

    Task<LegendConnectRuntimePolicySnapshot> UpdateAsync(
        string founderUserId,
        LegendConnectRuntimePolicyMutation mutation,
        CancellationToken cancellationToken = default);

    Task<LegendConnectProductionReadinessSnapshot> ActivateAsync(
        string founderUserId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectRuntimePolicySnapshot> PauseAsync(
        string founderUserId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectRuntimePolicySnapshot> ConfigurePriorityOverrideAsync(
        string founderUserId,
        LegendConnectPriorityOverrideMutation mutation,
        CancellationToken cancellationToken = default);

    Task<LegendConnectRuntimePolicySnapshot> DisablePriorityOverrideAsync(
        string founderUserId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectRuntimePolicySnapshot> ConfigureAutonomousLanguageFocusAsync(
        string founderUserId,
        LegendConnectAutonomousLanguageFocusMutation mutation,
        CancellationToken cancellationToken = default);

    Task<LegendConnectPriorityProgressSnapshot> GetPriorityProgressAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegendConnectFounderOperationalAuditSnapshot>> GetRecentAuditAsync(
        int take = 30,
        CancellationToken cancellationToken = default);

    Task RecordWorkerHeartbeatAsync(
        string worker,
        CancellationToken cancellationToken = default);
}
