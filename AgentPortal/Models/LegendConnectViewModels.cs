using Domain.Messaging;

namespace AgentPortal.Models;

public sealed class FounderLegendConnectDashboardVm
{
    public LegendConnectDashboardSnapshot Dashboard { get; init; } = new(
        Array.Empty<LegendConnectLanguageHealthSnapshot>(),
        Array.Empty<LegendConnectPairHealthSnapshot>(),
        0, 0, 0, 0, 0, 0, null, 0, 0, 0, null,
        Array.Empty<LegendConnectOperationalEventSnapshot>());
    public LegendConnectLanguageHealthSnapshot? SelectedLanguage { get; init; }
    public LegendConnectLanguageKnowledgeSnapshot? SelectedLanguageKnowledge { get; init; }
    public LegendConnectPairHealthSnapshot? SelectedPair { get; init; }
    public LegendConnectTranslationQualitySnapshot TranslationQuality { get; init; } = new(
        0, 0, 0, 0, 0, Array.Empty<LegendConnectTranslationQualityReviewSnapshot>());
    public LegendTargetRealizationReviewSnapshot TargetRealizations { get; init; } = new(
        0, 0, 0, 0, Array.Empty<LegendTargetRealizationCandidateSnapshot>());
    public IReadOnlyList<TranslationFounderAccountUsageSnapshot> AccountUsage { get; init; } =
        Array.Empty<TranslationFounderAccountUsageSnapshot>();
    public string? AccountSearchQuery { get; init; }
    public bool HasAdditionalAccountResults { get; init; }
    public IReadOnlyList<TranslationEntitlementPreset> EntitlementPresets { get; init; } =
        Array.Empty<TranslationEntitlementPreset>();
    public TranslationFounderScaleSnapshot AccountScale { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public LegendConnectRuntimePolicySnapshot RuntimePolicy { get; init; } = new(
        false, 0, 0, 0, false, true, "Shadow", 0.98m, null, null, DateTime.MinValue);
    public LegendConnectProductionReadinessSnapshot ProductionReadiness { get; init; } = new(
        "BLOCKED", false, "Runtime policy authority is unavailable.", Array.Empty<LegendConnectReadinessCheck>(), 0, 0, 0, 0, 0);
    public IReadOnlyList<LegendConnectFounderOperationalAuditSnapshot> RuntimeAudit { get; init; } =
        Array.Empty<LegendConnectFounderOperationalAuditSnapshot>();
}

public class FounderLegendConnectKnowledgeInput
{
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string? TargetLanguageCode { get; set; }
    public string? TargetText { get; set; }
    /// <summary>
    /// Founder-controlled entry mode only. When enabled, target rows resolve
    /// existing canonical source units instead of creating source curriculum.
    /// </summary>
    public bool FixTargetTranslation { get; set; }
    /// <summary>
    /// One exact source/target pair per line, separated by the same pipe
    /// delimiter already used by the Founder structured-curriculum form.
    /// This is transport syntax, not a language parser.
    /// </summary>
    public string? TargetTranslationRows { get; set; }
    public string? ContextCategory { get; set; }
    public string? UsageRegister { get; set; }
    public string? RegionalVariant { get; set; }
}

public sealed class FounderLegendConnectCorrectionInput : FounderLegendConnectKnowledgeInput
{
    public Guid SupersededAlignmentId { get; set; }
}

public sealed class FounderLegendConnectQualityReviewInput
{
    public Guid AlignmentId { get; set; }
}

public sealed class FounderLegendConnectTargetRealizationReviewInput
{
    public Guid CandidateId { get; set; }
}

/// <summary>
/// Founder form transport for a semantic curriculum family. Examples are
/// entered one per line as: English text | dimension=value; dimension=value.
/// Parsing happens at the Founder boundary; persistence remains relational.
/// </summary>
public sealed class FounderLegendConnectCurriculumInput
{
    public string FamilyKey { get; set; } = string.Empty;
    public string? SemanticCategory { get; set; }
    public string Examples { get; set; } = string.Empty;
}

public sealed class FounderLegendConnectEntitlementInput
{
    public string TargetUserId { get; set; } = string.Empty;
    public string TargetParticipantType { get; set; } = string.Empty;
    public string? ReturnAccountSearch { get; set; }
    public bool AccessGranted { get; set; }
    public string EntitlementMode { get; set; } = "Custom";
    public string? PresetKey { get; set; }
    public long? CustomCharacterAllowance { get; set; }
}

public sealed record FounderLegendConnectEntitlementResult(
    bool Succeeded,
    string Message);

public sealed class FounderLegendConnectRuntimePolicyInput
{
    public bool LearningEnabled { get; set; }
    public decimal ContextualMinimumConfidence { get; set; } = 0.98m;
}

/// <summary>
/// Transport only: the value is validated and persisted by the one existing
/// Legend Connect runtime-policy authority.
/// </summary>
public sealed class FounderLegendConnectCompositionModeInput
{
    public string ContextualCompositionMode { get; set; } = string.Empty;
}

public sealed class FounderLegendConnectActivationInput
{
    public bool FocusEnabled { get; set; }
    public List<string> FocusLanguageCodes { get; set; } = [];
}

public sealed record FounderLegendConnectOperationResult(bool Succeeded, string Message);

/// <summary>
/// Read-only display projection for the Founder dashboard's aggregate metrics.
/// It carries values already calculated by the canonical Legend Connect
/// authorities; the browser only renders these server-formatted values.
/// </summary>
public sealed record FounderLegendConnectLiveMetricSnapshot(
    string DisplayValue,
    string Tone);

public sealed record FounderLegendConnectLiveMetricsSnapshot(
    IReadOnlyDictionary<string, FounderLegendConnectLiveMetricSnapshot> Metrics,
    LegendConnectProviderCapacitySnapshot? ProviderCapacity)
{
    public static FounderLegendConnectLiveMetricsSnapshot Create(
        LegendConnectDashboardSnapshot dashboard,
        LegendConnectTranslationQualitySnapshot translationQuality,
        TranslationFounderScaleSnapshot accountScale,
        LegendConnectProductionReadinessSnapshot readiness,
        int runtimeAuditCount)
    {
        var metrics = new Dictionary<string, FounderLegendConnectLiveMetricSnapshot>(StringComparer.Ordinal);
        var routedRequestCount = dashboard.TranslationMemoryHitCount +
            dashboard.StructuralInternalServeCount +
            dashboard.ContextualInternalServeCount +
            dashboard.AzureFallbackCount;

        Add(metrics, "active-languages", dashboard.Languages.Count, LegendConnectMetricTone.InformationalActivity(dashboard.Languages.Count));
        Add(metrics, "directional-pairs", dashboard.Pairs.Count, LegendConnectMetricTone.InformationalActivity(dashboard.Pairs.Count));
        Add(metrics, "learning-failures", dashboard.FailedLearningJobCount, LegendConnectMetricTone.Failure(dashboard.FailedLearningJobCount));
        Add(metrics, "duplicate-prevention", dashboard.DuplicatePreventionCount, LegendConnectMetricTone.BeneficialActivity(dashboard.DuplicatePreventionCount));

        Add(metrics, "approved-candidates", readiness.ApprovedCandidateCount, LegendConnectMetricTone.BeneficialActivity(readiness.ApprovedCandidateCount));
        Add(metrics, "eligible-pending", readiness.PendingCandidateCount, LegendConnectMetricTone.PendingWork(readiness.PendingCandidateCount));
        Add(metrics, "rejected-ineligible", readiness.RejectedOrIneligibleCandidateCount, LegendConnectMetricTone.PendingWork(readiness.RejectedOrIneligibleCandidateCount));
        Add(metrics, "readiness-duplicates-prevented", readiness.DuplicateCandidateCount, LegendConnectMetricTone.BeneficialActivity(readiness.DuplicateCandidateCount));
        Add(metrics, "pairs-awaiting-knowledge", readiness.AwaitingKnowledgePairCount, LegendConnectMetricTone.PendingWork(readiness.AwaitingKnowledgePairCount));

        Add(metrics, "same-language-bypasses", dashboard.SameLanguageBypassCount, LegendConnectMetricTone.BeneficialActivity(dashboard.SameLanguageBypassCount));
        Add(metrics, "translation-memory-hits", dashboard.TranslationMemoryHitCount, LegendConnectMetricTone.BeneficialActivity(dashboard.TranslationMemoryHitCount));
        Add(metrics, "trusted-structural-served", dashboard.StructuralInternalServeCount, LegendConnectMetricTone.BeneficialActivity(dashboard.StructuralInternalServeCount));
        Add(metrics, "trusted-contextual-served", dashboard.ContextualInternalServeCount, LegendConnectMetricTone.BeneficialActivity(dashboard.ContextualInternalServeCount));
        AddPercent(metrics, "internal-coverage", dashboard.InternalCoverageRate, LegendConnectMetricTone.Avoidance(dashboard.InternalCoverageRate, routedRequestCount));
        AddPercent(metrics, "provider-avoidance", dashboard.ProviderAvoidanceRate, LegendConnectMetricTone.Avoidance(dashboard.ProviderAvoidanceRate, routedRequestCount));
        Add(metrics, "provider-fallback-required", dashboard.AzureFallbackCount, LegendConnectMetricTone.PendingWork(dashboard.AzureFallbackCount));
        AddPercent(metrics, "provider-dependency", dashboard.AzureDependencyRate, LegendConnectMetricTone.Dependency(dashboard.AzureDependencyRate, routedRequestCount));
        Add(metrics, "azure-characters-used", dashboard.AzureCharactersUsed, LegendConnectMetricTone.InformationalActivity(dashboard.AzureCharactersUsed));
        Add(metrics, "consumed-live-characters", dashboard.ConsumedLiveCharacters, LegendConnectMetricTone.InformationalActivity(dashboard.ConsumedLiveCharacters));
        Add(metrics, "consumed-corpus-characters", dashboard.ConsumedCorpusCharacters, LegendConnectMetricTone.InformationalActivity(dashboard.ConsumedCorpusCharacters));
        Add(metrics, "provider-characters-reserved", dashboard.ReservedProviderCharacters, LegendConnectMetricTone.PendingWork(dashboard.ReservedProviderCharacters));
        Add(metrics, "pending-learning-jobs", dashboard.LearningJobCount, LegendConnectMetricTone.PendingWork(dashboard.LearningJobCount));

        Add(metrics, "quality-needs-review", translationQuality.NeedsReviewCount, LegendConnectMetricTone.PendingWork(translationQuality.NeedsReviewCount));
        Add(metrics, "quality-provider-observations", translationQuality.ProviderObservationCount, LegendConnectMetricTone.InformationalActivity(translationQuality.ProviderObservationCount));
        Add(metrics, "quality-supported-observations", translationQuality.SupportedObservationCount, LegendConnectMetricTone.BeneficialActivity(translationQuality.SupportedObservationCount));
        Add(metrics, "quality-contradictions", translationQuality.ContradictionCount, LegendConnectMetricTone.Failure(translationQuality.ContradictionCount));
        Add(metrics, "quality-human-verified", translationQuality.HumanVerifiedAlignmentCount, LegendConnectMetricTone.BeneficialActivity(translationQuality.HumanVerifiedAlignmentCount));

        Add(metrics, "provider-operations", dashboard.ProviderOperationCount, LegendConnectMetricTone.InformationalActivity(dashboard.ProviderOperationCount));
        Add(metrics, "provider-billable-characters", dashboard.ProviderBillableCharacters, LegendConnectMetricTone.InformationalActivity(dashboard.ProviderBillableCharacters));
        Add(metrics, "same-language-avoided", dashboard.SameLanguageCharactersAvoided, LegendConnectMetricTone.BeneficialActivity(dashboard.SameLanguageCharactersAvoided));
        Add(metrics, "memory-avoided", dashboard.TranslationMemoryCharactersAvoided, LegendConnectMetricTone.BeneficialActivity(dashboard.TranslationMemoryCharactersAvoided));
        Add(metrics, "structural-avoided", dashboard.StructuralCompositionCharactersAvoided, LegendConnectMetricTone.BeneficialActivity(dashboard.StructuralCompositionCharactersAvoided));
        Add(metrics, "context-avoided", dashboard.ContextualCharactersAvoided, LegendConnectMetricTone.BeneficialActivity(dashboard.ContextualCharactersAvoided));
        Add(metrics, "quota-denied", dashboard.QuotaDeniedRequestCount, LegendConnectMetricTone.Failure(dashboard.QuotaDeniedRequestCount));
        Add(metrics, "provider-failures", dashboard.ProviderFailureCount, LegendConnectMetricTone.Failure(dashboard.ProviderFailureCount));
        Add(metrics, "group-target-reuse", dashboard.GroupUniqueTargetReuseCount, LegendConnectMetricTone.BeneficialActivity(dashboard.GroupUniqueTargetReuseCount));
        Add(metrics, "high-consumption-accounts", accountScale.HighConsumptionAccountCount, LegendConnectMetricTone.Failure(accountScale.HighConsumptionAccountCount));

        Add(metrics, "consented-accounts", dashboard.ConsentedLiveLearningAccountCount, LegendConnectMetricTone.InformationalActivity(dashboard.ConsentedLiveLearningAccountCount));
        Add(metrics, "eligible-live-translations", dashboard.EligibleConsentedLiveTranslationCount, LegendConnectMetricTone.PendingWork(dashboard.EligibleConsentedLiveTranslationCount));
        Add(metrics, "promoted-to-learning", dashboard.PromotedConsentedLiveTranslationCount, LegendConnectMetricTone.BeneficialActivity(dashboard.PromotedConsentedLiveTranslationCount));
        Add(metrics, "canonical-reuse-prevented-duplicates", dashboard.ReusedConsentedLiveTranslationCount, LegendConnectMetricTone.BeneficialActivity(dashboard.ReusedConsentedLiveTranslationCount));
        Add(metrics, "awaiting-corpus-processing", dashboard.PendingConsentedLiveTranslationCount, LegendConnectMetricTone.PendingWork(dashboard.PendingConsentedLiveTranslationCount));

        Add(metrics, "raw-submissions-retained", dashboard.FounderRawSubmissionCount, LegendConnectMetricTone.InformationalActivity(dashboard.FounderRawSubmissionCount));
        Add(metrics, "atomic-learning-units", dashboard.FounderAtomicLearningUnitCount, LegendConnectMetricTone.BeneficialActivity(dashboard.FounderAtomicLearningUnitCount));
        Add(metrics, "active-directional-alignments", dashboard.ActiveDirectionalAtomicAlignmentCount, LegendConnectMetricTone.BeneficialActivity(dashboard.ActiveDirectionalAtomicAlignmentCount));
        Add(metrics, "legacy-multi-unit-assets-retired", dashboard.SupersededLegacyMultiUnitAssetCount, LegendConnectMetricTone.InformationalActivity(dashboard.SupersededLegacyMultiUnitAssetCount));

        AddDisplay(metrics, "translation-quality-needs-review-summary", $"{translationQuality.NeedsReviewCount:N0} needs review", LegendConnectMetricTone.PendingWork(translationQuality.NeedsReviewCount));
        AddDisplay(metrics, "active-pairs-summary", $"{dashboard.Pairs.Count:N0} pairs", LegendConnectMetricTone.InformationalActivity(dashboard.Pairs.Count));
        AddDisplay(metrics, "runtime-audit-entries", $"{runtimeAuditCount:N0} entries", LegendConnectMetricTone.InformationalActivity(runtimeAuditCount));
        AddDisplay(metrics, "operational-events-summary", $"{dashboard.RecentOperationalEvents.Count:N0} events", LegendConnectMetricTone.InformationalActivity(dashboard.RecentOperationalEvents.Count));

        return new FounderLegendConnectLiveMetricsSnapshot(metrics, dashboard.ProviderCapacity);
    }

    private static void Add(
        IDictionary<string, FounderLegendConnectLiveMetricSnapshot> metrics,
        string key,
        long value,
        string tone) =>
        AddDisplay(metrics, key, value.ToString("N0"), tone);

    private static void AddPercent(
        IDictionary<string, FounderLegendConnectLiveMetricSnapshot> metrics,
        string key,
        decimal value,
        string tone) =>
        AddDisplay(metrics, key, value.ToString("P0"), tone);

    private static void AddDisplay(
        IDictionary<string, FounderLegendConnectLiveMetricSnapshot> metrics,
        string key,
        string displayValue,
        string tone) =>
        metrics.Add(key, new FounderLegendConnectLiveMetricSnapshot(displayValue, tone));
}
