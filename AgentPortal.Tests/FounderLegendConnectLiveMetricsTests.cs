using System;
using AgentPortal.Models;
using Domain.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderLegendConnectLiveMetricsTests
{
    [Fact]
    public void Create_UsesCanonicalSnapshotsForAllFounderAggregateCounters()
    {
        var dashboard = new LegendConnectDashboardSnapshot(
            Array.Empty<LegendConnectLanguageHealthSnapshot>(),
            Array.Empty<LegendConnectPairHealthSnapshot>(),
            SameLanguageBypassCount: 0,
            TranslationMemoryHitCount: 28,
            AzureFallbackCount: 101,
            AzureCharactersUsed: 2292,
            ConfiguredMonthlyCapacity: 0,
            LiveReserveCharacters: 0,
            RemainingSafeCapacity: null,
            LearningJobCount: 0,
            FailedLearningJobCount: 0,
            DuplicatePreventionCount: 0,
            LastSuccessfulLearningUtc: null,
            RecentOperationalEvents: Array.Empty<LegendConnectOperationalEventSnapshot>(),
            ProviderOperationCount: 101,
            ProviderBillableCharacters: 2292,
            SameLanguageCharactersAvoided: 0,
            TranslationMemoryCharactersAvoided: 28,
            ContextualCharactersAvoided: 0,
            QuotaDeniedRequestCount: 871,
            ProviderFailureCount: 0,
            GroupUniqueTargetReuseCount: 0,
            ConsentedLiveLearningAccountCount: 0,
            EligibleConsentedLiveTranslationCount: 0,
            PromotedConsentedLiveTranslationCount: 0,
            ReusedConsentedLiveTranslationCount: 0,
            PendingConsentedLiveTranslationCount: 0,
            FounderRawSubmissionCount: 35,
            FounderAtomicLearningUnitCount: 516,
            SupersededLegacyMultiUnitAssetCount: 2,
            ActiveDirectionalAtomicAlignmentCount: 495,
            InternalCoverageRate: 0.23m,
            StructuralCompositionCharactersAvoided: 13,
            StructuralInternalServeCount: 3,
            PromotedTranslationModelServeCount: 2,
            PromotedTranslationModelFailureCount: 1,
            ProviderObservationReuseCount: 4,
            NativeTranslationIntelligenceServeCount: 33,
            ReconciledTerminalRouteCount: 138,
            TranslationRoutingReconciliationGap: 0,
            PromotedTranslationModelCharactersAvoided: 17,
            ProviderObservationCharactersAvoided: 19,
            CrossLanguageTranslationRequestCount: 138);
        var quality = new LegendConnectTranslationQualitySnapshot(0, 0, 0, 0, 0, Array.Empty<LegendConnectTranslationQualityReviewSnapshot>());
        var accountScale = new TranslationFounderScaleSnapshot(101, 2292, 0, 28, 0, 871, 0, 0, 0);
        var readiness = new LegendConnectProductionReadinessSnapshot(
            "ACTIVE",
            true,
            "Ready",
            Array.Empty<LegendConnectReadinessCheck>(),
            18,
            0,
            0,
            0,
            0);

        var result = FounderLegendConnectLiveMetricsSnapshot.Create(
            dashboard,
            quality,
            accountScale,
            readiness,
            runtimeAuditCount: 0);

        Assert.Equal(101.ToString("N0"), result.Metrics["provider-operations"].DisplayValue);
        Assert.Equal(2292.ToString("N0"), result.Metrics["provider-billable-characters"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["same-language-avoided"].DisplayValue);
        Assert.Equal(28.ToString("N0"), result.Metrics["memory-avoided"].DisplayValue);
        Assert.Equal(3.ToString("N0"), result.Metrics["trusted-structural-served"].DisplayValue);
        Assert.Equal(13.ToString("N0"), result.Metrics["structural-avoided"].DisplayValue);
        Assert.Equal(2.ToString("N0"), result.Metrics["promoted-translation-model-served"].DisplayValue);
        Assert.Equal(1.ToString("N0"), result.Metrics["promoted-translation-model-failures"].DisplayValue);
        Assert.Equal(4.ToString("N0"), result.Metrics["provider-observation-reused"].DisplayValue);
        Assert.Equal(33.ToString("N0"), result.Metrics["native-translation-intelligence-served"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["translation-routing-reconciliation"].DisplayValue);
        Assert.Equal(17.ToString("N0"), result.Metrics["promoted-translation-model-avoided"].DisplayValue);
        Assert.Equal(19.ToString("N0"), result.Metrics["provider-observation-avoided"].DisplayValue);
        Assert.Equal(138.ToString("N0"), result.Metrics["cross-language-translation-requests"].DisplayValue);
        Assert.Equal(0.23m.ToString("P0"), result.Metrics["internal-coverage"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["context-avoided"].DisplayValue);
        Assert.Equal(871.ToString("N0"), result.Metrics["quota-denied"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["provider-failures"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["group-target-reuse"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["high-consumption-accounts"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["consented-accounts"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["eligible-live-translations"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["promoted-to-learning"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["canonical-reuse-prevented-duplicates"].DisplayValue);
        Assert.Equal(0.ToString("N0"), result.Metrics["awaiting-corpus-processing"].DisplayValue);
        Assert.Equal(35.ToString("N0"), result.Metrics["raw-submissions-retained"].DisplayValue);
        Assert.Equal(516.ToString("N0"), result.Metrics["atomic-learning-units"].DisplayValue);
        Assert.Equal(495.ToString("N0"), result.Metrics["active-directional-alignments"].DisplayValue);
        Assert.Equal(2.ToString("N0"), result.Metrics["legacy-multi-unit-assets-retired"].DisplayValue);
        Assert.Equal(LegendConnectMetricTone.Danger, result.Metrics["quota-denied"].Tone);
        Assert.Equal(LegendConnectMetricTone.Success, result.Metrics["memory-avoided"].Tone);
        Assert.Equal(58, result.Metrics.Count);
        Assert.Equal("0 needs review", result.Metrics["translation-quality-needs-review-summary"].DisplayValue);
    }
}
