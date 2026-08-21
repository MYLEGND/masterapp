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

/// <summary>
/// The durable phases of the single historical replay owned by the existing
/// learning worker. They describe progression only; each phase calls the
/// existing curriculum or quality evaluator rather than another processor.
/// </summary>
public static class LegendConnectLanguageIntelligenceReevaluationPhases
{
    public const string SourceFamilies = "SourceFamilies";
    public const string Alignments = "Alignments";
    public const string ProviderObservations = "ProviderObservations";
    public const string OperationalTranslations = "OperationalTranslations";
    public const string Complete = "Complete";

    public static bool IsWorkPhase(string? phase) =>
        phase is SourceFamilies or Alignments or ProviderObservations or OperationalTranslations;
}

/// <summary>
/// A bounded result from one existing canonical evaluator pass. The cursor is
/// a stable historical identity, never a source-text or provenance rewrite.
/// </summary>
public sealed record LegendConnectHistoricalReevaluationProgress(
    int ProcessedCount,
    Guid? LastProcessedId,
    bool PhaseComplete);

/// <summary>
/// Durable replay state read and advanced by the one existing learning worker.
/// A future material evaluator change advances its version and reuses these
/// same phases against active historical evidence.
/// </summary>
public sealed record LegendConnectLanguageIntelligenceReevaluationSnapshot(
    int TargetEvaluatorVersion,
    int CompletedEvaluatorVersion,
    int CursorReplayCompatibilityEvaluatorVersion,
    string Phase,
    Guid? Cursor,
    DateTime? StartedUtc,
    DateTime? CompletedUtc)
{
    public bool RequiresWork => LegendConnectLanguageIntelligenceReevaluationPhases.IsWorkPhase(Phase);
}

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

    Task<LegendConnectRuntimePolicySnapshot> UpdateCompositionAsync(
        string founderUserId,
        bool learningEnabled,
        string? contextualCompositionMode,
        decimal contextualMinimumConfidence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes only the existing persisted composition mode. This is the
    /// canonical Founder command behind the top-level production control; it
    /// deliberately owns no separate enablement state.
    /// </summary>
    Task<LegendConnectRuntimePolicySnapshot> SetContextualCompositionModeAsync(
        string founderUserId,
        string contextualCompositionMode,
        CancellationToken cancellationToken = default);

    Task<LegendConnectProductionReadinessSnapshot> ActivateAsync(
        string founderUserId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectRuntimePolicySnapshot> PauseAsync(
        string founderUserId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectRuntimePolicySnapshot> ConfigureAutonomousLanguageFocusAsync(
        string founderUserId,
        LegendConnectAutonomousLanguageFocusMutation mutation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts or resumes the one versioned historical replay when the current
    /// canonical evaluator is newer than the completed durable watermark.
    /// </summary>
    Task<LegendConnectLanguageIntelligenceReevaluationSnapshot> GetOrStartLanguageIntelligenceReevaluationAsync(
        int evaluatorVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records progress from an existing curriculum or quality-evidence page.
    /// It never derives language intelligence itself.
    /// </summary>
    Task AdvanceLanguageIntelligenceReevaluationAsync(
        int evaluatorVersion,
        string phase,
        Guid? lastProcessedId,
        bool phaseComplete,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegendConnectFounderOperationalAuditSnapshot>> GetRecentAuditAsync(
        int take = 30,
        CancellationToken cancellationToken = default);

    Task RecordWorkerHeartbeatAsync(
        string worker,
        CancellationToken cancellationToken = default);
}
