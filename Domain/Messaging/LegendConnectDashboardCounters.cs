namespace Domain.Messaging;

/// <summary>Scalar operational projection for live polling; no corpus or health-detail rows.</summary>
public sealed record LegendConnectDashboardCounters
{
    public long ReconciledTerminalRouteCount { get; init; }
    public long ActiveLanguageCount { get; init; }
    public long DirectionalPairCount { get; init; }
    public long FailedLearningJobCount { get; init; }
    public long DuplicatePreventionCount { get; init; }
    public long SameLanguageBypassCount { get; init; }
    public long CrossLanguageTranslationRequestCount { get; init; }
    public long TranslationMemoryHitCount { get; init; }
    public long StructuralInternalServeCount { get; init; }
    public long ContextualInternalServeCount { get; init; }
    public long PromotedTranslationModelServeCount { get; init; }
    public long PromotedTranslationModelFailureCount { get; init; }
    public long ProviderObservationReuseCount { get; init; }
    public long NativeTranslationIntelligenceServeCount { get; init; }
    public long TranslationRoutingReconciliationGap { get; init; }
    public decimal InternalCoverageRate { get; init; }
    public decimal ProviderAvoidanceRate { get; init; }
    public long AzureFallbackCount { get; init; }
    public decimal AzureDependencyRate { get; init; }
    public long AzureCharactersUsed { get; init; }
    public long ConsumedLiveCharacters { get; init; }
    public long ConsumedCorpusCharacters { get; init; }
    public long ReservedProviderCharacters { get; init; }
    public long LearningJobCount { get; init; }
    public long ProviderOperationCount { get; init; }
    public long ProviderBillableCharacters { get; init; }
    public long SameLanguageCharactersAvoided { get; init; }
    public long TranslationMemoryCharactersAvoided { get; init; }
    public long StructuralCompositionCharactersAvoided { get; init; }
    public long ContextualCharactersAvoided { get; init; }
    public long PromotedTranslationModelCharactersAvoided { get; init; }
    public long ProviderObservationCharactersAvoided { get; init; }
    public long QuotaDeniedRequestCount { get; init; }
    public long ProviderFailureCount { get; init; }
    public long GroupUniqueTargetReuseCount { get; init; }
    public long ConsentedLiveLearningAccountCount { get; init; }
    public long EligibleConsentedLiveTranslationCount { get; init; }
    public long PromotedConsentedLiveTranslationCount { get; init; }
    public long ReusedConsentedLiveTranslationCount { get; init; }
    public long PendingConsentedLiveTranslationCount { get; init; }
    public long FounderRawSubmissionCount { get; init; }
    public long FounderAtomicLearningUnitCount { get; init; }
    public long ActiveDirectionalAtomicAlignmentCount { get; init; }
    public long SupersededLegacyMultiUnitAssetCount { get; init; }
    public long RecentOperationalEventCount { get; init; }
    public LegendConnectProviderCapacitySnapshot? ProviderCapacity { get; init; }

    public static LegendConnectDashboardCounters FromDashboard(LegendConnectDashboardSnapshot dashboard) => new()
    {
        ReconciledTerminalRouteCount = dashboard.ReconciledTerminalRouteCount,
        ActiveLanguageCount = dashboard.Languages.Count,
        DirectionalPairCount = dashboard.Pairs.Count,
        FailedLearningJobCount = dashboard.FailedLearningJobCount,
        DuplicatePreventionCount = dashboard.DuplicatePreventionCount,
        SameLanguageBypassCount = dashboard.SameLanguageBypassCount,
        CrossLanguageTranslationRequestCount = dashboard.CrossLanguageTranslationRequestCount,
        TranslationMemoryHitCount = dashboard.TranslationMemoryHitCount,
        StructuralInternalServeCount = dashboard.StructuralInternalServeCount,
        ContextualInternalServeCount = dashboard.ContextualInternalServeCount,
        PromotedTranslationModelServeCount = dashboard.PromotedTranslationModelServeCount,
        PromotedTranslationModelFailureCount = dashboard.PromotedTranslationModelFailureCount,
        ProviderObservationReuseCount = dashboard.ProviderObservationReuseCount,
        NativeTranslationIntelligenceServeCount = dashboard.NativeTranslationIntelligenceServeCount,
        TranslationRoutingReconciliationGap = dashboard.TranslationRoutingReconciliationGap,
        InternalCoverageRate = dashboard.InternalCoverageRate,
        ProviderAvoidanceRate = dashboard.ProviderAvoidanceRate,
        AzureFallbackCount = dashboard.AzureFallbackCount,
        AzureDependencyRate = dashboard.AzureDependencyRate,
        AzureCharactersUsed = dashboard.AzureCharactersUsed,
        ConsumedLiveCharacters = dashboard.ConsumedLiveCharacters,
        ConsumedCorpusCharacters = dashboard.ConsumedCorpusCharacters,
        ReservedProviderCharacters = dashboard.ReservedProviderCharacters,
        LearningJobCount = dashboard.LearningJobCount,
        ProviderOperationCount = dashboard.ProviderOperationCount,
        ProviderBillableCharacters = dashboard.ProviderBillableCharacters,
        SameLanguageCharactersAvoided = dashboard.SameLanguageCharactersAvoided,
        TranslationMemoryCharactersAvoided = dashboard.TranslationMemoryCharactersAvoided,
        StructuralCompositionCharactersAvoided = dashboard.StructuralCompositionCharactersAvoided,
        ContextualCharactersAvoided = dashboard.ContextualCharactersAvoided,
        PromotedTranslationModelCharactersAvoided = dashboard.PromotedTranslationModelCharactersAvoided,
        ProviderObservationCharactersAvoided = dashboard.ProviderObservationCharactersAvoided,
        QuotaDeniedRequestCount = dashboard.QuotaDeniedRequestCount,
        ProviderFailureCount = dashboard.ProviderFailureCount,
        GroupUniqueTargetReuseCount = dashboard.GroupUniqueTargetReuseCount,
        ConsentedLiveLearningAccountCount = dashboard.ConsentedLiveLearningAccountCount,
        EligibleConsentedLiveTranslationCount = dashboard.EligibleConsentedLiveTranslationCount,
        PromotedConsentedLiveTranslationCount = dashboard.PromotedConsentedLiveTranslationCount,
        ReusedConsentedLiveTranslationCount = dashboard.ReusedConsentedLiveTranslationCount,
        PendingConsentedLiveTranslationCount = dashboard.PendingConsentedLiveTranslationCount,
        FounderRawSubmissionCount = dashboard.FounderRawSubmissionCount,
        FounderAtomicLearningUnitCount = dashboard.FounderAtomicLearningUnitCount,
        ActiveDirectionalAtomicAlignmentCount = dashboard.ActiveDirectionalAtomicAlignmentCount,
        SupersededLegacyMultiUnitAssetCount = dashboard.SupersededLegacyMultiUnitAssetCount,
        RecentOperationalEventCount = dashboard.RecentOperationalEvents.Count,
        ProviderCapacity = dashboard.ProviderCapacity,
    };
}
