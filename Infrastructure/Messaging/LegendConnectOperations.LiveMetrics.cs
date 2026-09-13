using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Messaging;

internal sealed partial class LegendConnectOperations
{
    public async Task<LegendConnectDashboardCounters> GetDashboardCountersAsync(
        CancellationToken cancellationToken = default,
        LegendConnectExternalProviderPolicy? providerPolicy = null)
    {
        // Keep the registry and provider policy authorities used by the full
        // dashboard, but never load the corpus to render scalar polling values.
        if (LegendConnectExternalProviderPolicy.Resolve(providerPolicy).ForbidsExternalProviders)
            await _registry.ListEnabledTranslationLanguagesReadOnlyAsync(cancellationToken);
        else
            await _registry.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var usage = await _db.Set<LegendTranslationSystemUsage>().AsNoTracking()
            .GroupBy(_ => 1).Select(group => new LegendConnectDashboardCounters
            {
                SameLanguageBypassCount = group.Sum(item => item.SameLanguageBypassCount),
                ProviderOperationCount = group.Sum(item => item.ProviderOperationCount),
                ProviderBillableCharacters = group.Sum(item => item.ProviderBillableCharacters),
                SameLanguageCharactersAvoided = group.Sum(item => item.SameLanguageCharactersAvoided),
                TranslationMemoryCharactersAvoided = group.Sum(item => item.TranslationMemoryCharactersAvoided),
                ContextualCharactersAvoided = group.Sum(item => item.ContextualCharactersAvoided),
                QuotaDeniedRequestCount = group.Sum(item => item.QuotaDeniedRequestCount),
                ProviderFailureCount = group.Sum(item => item.ProviderFailureCount),
                GroupUniqueTargetReuseCount = group.Sum(item => item.GroupUniqueTargetReuseCount),
                StructuralCompositionCharactersAvoided = group.Sum(item => item.StructuralCompositionCharactersAvoided),
                PromotedTranslationModelCharactersAvoided = group.Sum(item => item.PromotedTranslationModelCharactersAvoided),
                ProviderObservationCharactersAvoided = group.Sum(item => item.ProviderObservationCharactersAvoided),
            }).SingleOrDefaultAsync(cancellationToken) ?? new LegendConnectDashboardCounters();
        var demand = await _db.Set<LegendTranslationPairDemand>().AsNoTracking()
            .GroupBy(_ => 1).Select(group => new LegendConnectDashboardCounters
            {
                CrossLanguageTranslationRequestCount = group.Sum(item => item.TranslationRequestCount),
                TranslationMemoryHitCount = group.Sum(item => item.TranslationMemoryHitCount),
                ContextualInternalServeCount = group.Sum(item => item.ContextualInternalServeCount),
                StructuralInternalServeCount = group.Sum(item => item.StructuralInternalServeCount),
                PromotedTranslationModelServeCount = group.Sum(item => item.NeuralModelServeCount),
                PromotedTranslationModelFailureCount = group.Sum(item => item.NeuralModelFailureCount),
                ProviderObservationReuseCount = group.Sum(item => item.ProviderObservationReuseCount),
                AzureFallbackCount = group.Sum(item => item.AzureFallbackCount),
            }).SingleOrDefaultAsync(cancellationToken) ?? new LegendConnectDashboardCounters();
        var units = _db.Set<LegendLanguageTextUnit>().AsNoTracking().Where(item => item.IsTrainingEligible);
        var events = _db.Set<LegendTranslationLearningEvent>().AsNoTracking();
        // Match ActiveLearningEvents' historical identity rules, including
        // aggregate-only privacy audit rows and pending events without targets.
        var activeEvents = events.Where(item => item.ProcessingState != "Superseded" &&
            (item.EligibilityState != "Eligible" ||
             (units.Any(unit => unit.LanguageCode.Trim().ToUpper() == item.SourceLanguageCode.Trim().ToUpper() &&
                                unit.NormalizedHash.Trim().ToUpper() == item.SourceTextHash.Trim().ToUpper()) &&
              (item.ProcessingState == "Pending" || item.ProcessingState == "Processing" ||
               units.Any(unit => unit.LanguageCode.Trim().ToUpper() == item.TargetLanguageCode.Trim().ToUpper() &&
                                 unit.NormalizedHash.Trim().ToUpper() == item.TargetTextHash.Trim().ToUpper())))));
        var learning = await activeEvents.GroupBy(_ => 1).Select(group => new
        {
            Pending = group.LongCount(item => item.EligibilityState == "Eligible" && (item.ProcessingState == "Pending" || item.ProcessingState == "Processing")),
            Failed = group.LongCount(item => item.FailureCode != null && item.FailureCode.Trim() != "")
        }).SingleOrDefaultAsync(cancellationToken);
        var consent = await events.Where(item => item.Provenance == "ConsentedLiveTranslation")
            .GroupBy(_ => 1).Select(group => new
            {
                Eligible = group.LongCount(item => item.EligibilityState == "Eligible"),
                Promoted = group.LongCount(item => item.PromotionOutcome == "Promoted"),
                Reused = group.LongCount(item => item.PromotionOutcome == "Reused"),
                Pending = group.LongCount(item => item.ProcessingState == "Pending" || item.ProcessingState == "Processing")
            }).SingleOrDefaultAsync(cancellationToken);
        // NormalizeText includes Unicode FormKC and whitespace folding, which
        // cannot safely be replaced with database collation equality. Stream
        // only failed candidate/source pairs; never retain the complete corpus.
        var failedCandidateCount = 0L;
        var failedCandidates =
            from candidate in _db.Set<LegendCorpusCandidate>().AsNoTracking()
            where candidate.FailureCode != null && candidate.FailureCode.Trim() != ""
            from unit in units
            where unit.LanguageCode.Trim().ToUpper() == candidate.SourceLanguageCode.Trim().ToUpper() &&
                  unit.NormalizedHash.Trim().ToUpper() == candidate.SourceTextHash.Trim().ToUpper()
            orderby candidate.Id, unit.Id
            select new { CandidateId = candidate.Id, CanonicalText = unit.Text, candidate.SourceText };
        Guid? previousCandidate = null;
        await foreach (var candidate in failedCandidates.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (previousCandidate == candidate.CandidateId)
                throw new InvalidOperationException("Ambiguous active source identity prevents reliable candidate counters.");
            previousCandidate = candidate.CandidateId;
            if (string.Equals(candidate.CanonicalText, LegendLanguageIdentity.NormalizeText(candidate.SourceText), StringComparison.Ordinal))
                failedCandidateCount++;
        }
        var operational = _db.Set<LegendConnectOperationalEvent>().AsNoTracking();
        var duplicates = await operational.LongCountAsync(item => item.Category == "DuplicatePrevention" && item.Status == "Prevented", cancellationToken)
            + await _db.Set<LegendConnectKnowledgeAuditEntry>().LongCountAsync(item => item.Result == "DuplicatePrevented", cancellationToken);
        var now = DateTime.UtcNow;
        var currentPeriod = new DateOnly(now.Year, now.Month, 1);
        var capacity = await _db.Set<LegendTranslationProviderCapacity>().AsNoTracking()
            .Where(item => item.Provider == "AzureTranslator" && item.BillingPeriodStart == currentPeriod)
            .OrderByDescending(item => item.UpdatedUtc)
            .Select(item => new { item.LiveCharactersConsumed, item.BootstrapCharactersConsumed, item.TrainingCharactersConsumed, item.ReservedLiveCharacters })
            .FirstOrDefaultAsync(cancellationToken);
        var providerCapacity = _capacityAuthority is null ? null :
            LegendConnectExternalProviderPolicy.Resolve(providerPolicy).ForbidsExternalProviders
                ? await _capacityAuthority.GetSnapshotAsync("AzureTranslator", cancellationToken, providerPolicy)
                : await _capacityAuthority.GetSnapshotAsync("AzureTranslator", cancellationToken);
        var native = demand.TranslationMemoryHitCount + demand.ContextualInternalServeCount +
            demand.StructuralInternalServeCount + demand.PromotedTranslationModelServeCount;
        var avoided = native + demand.ProviderObservationReuseCount;
        var routes = avoided + demand.AzureFallbackCount;
        var requests = demand.CrossLanguageTranslationRequestCount;
        return usage with
        {
            CrossLanguageTranslationRequestCount = demand.CrossLanguageTranslationRequestCount,
            TranslationMemoryHitCount = demand.TranslationMemoryHitCount,
            ContextualInternalServeCount = demand.ContextualInternalServeCount,
            StructuralInternalServeCount = demand.StructuralInternalServeCount,
            PromotedTranslationModelServeCount = demand.PromotedTranslationModelServeCount,
            PromotedTranslationModelFailureCount = demand.PromotedTranslationModelFailureCount,
            ProviderObservationReuseCount = demand.ProviderObservationReuseCount,
            AzureFallbackCount = demand.AzureFallbackCount,
            ActiveLanguageCount = await _db.Set<LegendLanguageDefinition>().LongCountAsync(item => item.IsEnabled, cancellationToken),
            DirectionalPairCount = await _db.Set<LegendLanguagePair>().LongCountAsync(item => item.IsEnabled, cancellationToken),
            LearningJobCount = learning?.Pending ?? 0,
            FailedLearningJobCount = (learning?.Failed ?? 0) + failedCandidateCount,
            DuplicatePreventionCount = duplicates,
            NativeTranslationIntelligenceServeCount = native,
            ReconciledTerminalRouteCount = routes,
            TranslationRoutingReconciliationGap = requests - routes,
            InternalCoverageRate = requests == 0 ? 0m : Math.Round((decimal)native / requests, 4),
            ProviderAvoidanceRate = requests == 0 ? 0m : Math.Round((decimal)avoided / requests, 4),
            AzureDependencyRate = requests == 0 ? 0m : Math.Round((decimal)demand.AzureFallbackCount / requests, 4),
            AzureCharactersUsed = providerCapacity?.MonthlyCharactersConsumed ??
                ((capacity?.LiveCharactersConsumed ?? 0) + (capacity?.BootstrapCharactersConsumed ?? 0) + (capacity?.TrainingCharactersConsumed ?? 0)),
            ConsumedLiveCharacters = capacity?.LiveCharactersConsumed ?? 0,
            ConsumedCorpusCharacters = (capacity?.BootstrapCharactersConsumed ?? 0) + (capacity?.TrainingCharactersConsumed ?? 0),
            ReservedProviderCharacters = providerCapacity?.MonthlyReservedCharacters ?? capacity?.ReservedLiveCharacters ?? 0,
            ProviderCapacity = providerCapacity,
            ConsentedLiveLearningAccountCount = await _db.MobileProfileSettings.LongCountAsync(item => item.AllowsConsentedTranslationLearning, cancellationToken),
            EligibleConsentedLiveTranslationCount = consent?.Eligible ?? 0,
            PromotedConsentedLiveTranslationCount = consent?.Promoted ?? 0,
            ReusedConsentedLiveTranslationCount = consent?.Reused ?? 0,
            PendingConsentedLiveTranslationCount = consent?.Pending ?? 0,
            FounderRawSubmissionCount = await _db.Set<LegendFounderTrainingSubmission>().LongCountAsync(cancellationToken),
            FounderAtomicLearningUnitCount = await _db.Set<LegendFounderTrainingSubmissionUnit>().LongCountAsync(cancellationToken),
            ActiveDirectionalAtomicAlignmentCount = await _db.Set<LegendTranslationAlignment>().LongCountAsync(item => item.SupersededUtc == null &&
                units.Any(unit => unit.Id == item.SourceTextUnitId) && units.Any(unit => unit.Id == item.TargetTextUnitId), cancellationToken),
            SupersededLegacyMultiUnitAssetCount = await (from submission in _db.Set<LegendFounderTrainingSubmission>()
                join unit in _db.Set<LegendLanguageTextUnit>() on submission.LegacySourceTextUnitId equals unit.Id
                where submission.LegacySourceTextUnitId != null && !unit.IsTrainingEligible
                select submission.Id).LongCountAsync(cancellationToken),
            RecentOperationalEventCount = Math.Min(50, await operational.LongCountAsync(cancellationToken))
        };
    }
}
