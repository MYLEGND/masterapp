using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
        Assert.Same(readiness, result.ProductionReadiness);
        Assert.Equal(0.23m, result.Metrics["internal-coverage"].Value);
        Assert.Equal("ratio", result.Metrics["internal-coverage"].Unit);
        Assert.Equal(871m, result.Metrics["quota-denied"].Value);
        Assert.Contains("does not identify", result.Metrics["quota-denied"].Definition);
        Assert.Equal("characters", result.Metrics["provider-billable-characters"].Unit);
        Assert.True(result.Metrics["provider-billable-characters"].ObservationAvailable);
    }

    [Fact]
    public void Overview_PreservesBlockedReadinessAndAuthoritativeNumericValuesDespitePositiveDisplayTone()
    {
        var checks = new[] { new LegendConnectReadinessCheck("capacity", "BLOCKED", "No capacity available.") };
        var readiness = new LegendConnectProductionReadinessSnapshot("BLOCKED", false,
            "Acquisition cannot activate.", checks, 48, 17, 2, 1, 0);
        var before = DateTime.UtcNow;
        var result = FounderLegendConnectLiveMetricsSnapshot.Create(
            new LegendConnectDashboardCounters { CrossLanguageTranslationRequestCount = 10000,
                ReconciledTerminalRouteCount = 10000, InternalCoverageRate = 0.0004m,
                ProviderAvoidanceRate = 0.6604m, ProviderObservationReuseCount = 6600 },
            new LegendConnectTranslationQualitySnapshot(0, 0, 0, 0, 0, []),
            new TranslationFounderScaleSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0), readiness, 0);

        Assert.Same(readiness, result.ProductionReadiness);
        Assert.Same(checks, result.ProductionReadiness!.Checks);
        Assert.False(result.ProductionReadiness.CanActivate);
        Assert.Equal(0.0004m, result.Metrics["internal-coverage"].Value);
        Assert.Equal(0.6604m, result.Metrics["provider-avoidance"].Value);
        Assert.Equal(6600m, result.Metrics["provider-observation-reused"].Value);
        Assert.Contains("Excludes retained provider-observation reuse", result.Metrics["internal-coverage"].Definition);
        Assert.Contains("not model confidence", result.Metrics["provider-avoidance"].Definition);
        Assert.Contains("not only pending", result.Metrics["approved-candidates"].Definition);
        Assert.InRange(result.SnapshotCompletedUtc, before, DateTime.UtcNow);
    }

    [Fact]
    public void Overview_ZeroDemandRetainsDenominatorAndDoesNotInventReadiness()
    {
        var readiness = LegendConnectProductionReadinessSnapshot.Unavailable("Readiness authority unavailable.");
        var result = FounderLegendConnectLiveMetricsSnapshot.Create(new LegendConnectDashboardCounters(),
            new LegendConnectTranslationQualitySnapshot(0, 0, 0, 0, 0, []),
            new TranslationFounderScaleSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0), readiness, 0);
        Assert.Equal(0m, result.Metrics["cross-language-translation-requests"].Value);
        Assert.Contains("Zero can mean no requests", result.Metrics["internal-coverage"].Definition);
        Assert.Same(readiness, result.ProductionReadiness);
        Assert.Contains("not general model intelligence", result.Scope);
    }

    [Fact]
    public void Overview_UnavailableSourcesAreUnknownWhileOtherObservedCountersRemainUsable()
    {
        var readiness = LegendConnectProductionReadinessSnapshot.Unavailable("Runtime policy authority is missing.");
        var result = FounderLegendConnectLiveMetricsSnapshot.Create(
            new LegendConnectDashboardCounters { ProviderOperationCount = 6, LearningJobCount = 2 },
            new LegendConnectTranslationQualitySnapshot(0, 0, 0, 0, 0, []),
            new TranslationFounderScaleSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0), readiness, 0,
            accountScaleAvailable: false, runtimeAuditAvailable: false);

        Assert.False(result.ProductionReadiness!.ObservationAvailable);
        Assert.False(result.ProductionReadiness.CanActivate);
        Assert.Equal("BLOCKED", result.ProductionReadiness.State);
        var serialized = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var key in new[] { "approved-candidates", "eligible-pending", "rejected-ineligible",
                     "readiness-duplicates-prevented", "pairs-awaiting-knowledge", "high-consumption-accounts", "runtime-audit-entries" })
        {
            var metric = result.Metrics[key];
            Assert.False(metric.ObservationAvailable);
            Assert.Null(metric.Value);
            Assert.Equal("Unavailable", metric.DisplayValue);
            Assert.Equal(LegendConnectMetricTone.Neutral, metric.Tone);
            Assert.False(string.IsNullOrWhiteSpace(metric.UnavailableReason));
            var json = serialized.GetProperty("metrics").GetProperty(key);
            Assert.False(json.GetProperty("observationAvailable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("value").ValueKind);
        }
        Assert.Equal(6m, result.Metrics["provider-operations"].Value);
        Assert.Equal(2m, result.Metrics["pending-learning-jobs"].Value);
        Assert.True(result.Metrics["pending-learning-jobs"].ObservationAvailable);
        Assert.Equal(58, result.Metrics.Count);
    }

    [Fact]
    public void Overview_ObservedZeroIsDistinctFromUnavailableEvenWhenAcquisitionIsBlocked()
    {
        var readiness = new LegendConnectProductionReadinessSnapshot("BLOCKED", false,
            "Autonomous acquisition admission is blocked.", [], 0, 0, 0, 0, 0);
        var result = FounderLegendConnectLiveMetricsSnapshot.Create(new LegendConnectDashboardCounters(),
            new LegendConnectTranslationQualitySnapshot(0, 0, 0, 0, 0, []),
            new TranslationFounderScaleSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0), readiness, 0);

        Assert.True(readiness.ObservationAvailable);
        Assert.Equal("autonomous_corpus_acquisition_admission", readiness.Scope);
        Assert.Contains("does not assess live translation", readiness.Definition);
        Assert.Contains("historical reevaluation", readiness.Definition);
        Assert.Contains("in-flight work", readiness.Definition);
        foreach (var key in new[] { "approved-candidates", "eligible-pending", "high-consumption-accounts", "runtime-audit-entries" })
        {
            Assert.True(result.Metrics[key].ObservationAvailable);
            Assert.Equal(0m, result.Metrics[key].Value);
            Assert.Null(result.Metrics[key].UnavailableReason);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readiness_RegistryReadFailureIsNotAnObservedEmptyRegistry(bool readFails)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var access = new Mock<IControlledResourceAccessService>(MockBehavior.Strict);
        var languages = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        var read = languages.Setup(item => item.ListEnabledTranslationLanguagesReadOnlyAsync(It.IsAny<CancellationToken>()));
        if (readFails)
            read.ThrowsAsync(new InvalidOperationException("Synthetic registry read failure."));
        else
            read.ReturnsAsync(Array.Empty<LegendLanguageDefinitionSnapshot>());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:CorpusAcquisition:Enabled"] = "true"
        }).Build();
        var authority = new LegendConnectRuntimePolicyAuthority(db, access.Object, languages.Object, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);

        var readiness = await authority.GetReadinessAsync();

        var registry = Assert.Single(readiness.Checks.Where(check => check.Name == "Language Registry"));
        Assert.Equal(!readFails, registry.ObservationAvailable);
        Assert.Equal("BLOCKED", registry.State);
        Assert.False(readiness.CanActivate);
        Assert.True(readiness.ObservationAvailable);
        Assert.Equal("DEGRADED", readiness.State);
        Assert.Contains("admission of a new autonomous acquisition cycle is blocked", readiness.Summary);
        Assert.Contains("does not report whether other processing or in-flight work continues", readiness.Summary);
        if (readFails)
        {
            Assert.Contains("count is unknown", registry.Detail);
            Assert.DoesNotContain("No enabled learning language is available", registry.Detail);
        }
        else
            Assert.Contains("No enabled learning language is available", registry.Detail);
        access.VerifyNoOtherCalls();
    }
}
