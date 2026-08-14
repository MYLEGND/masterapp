using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Messaging;

/// <summary>
/// The one operational read/write facade for Founder surfaces. It composes
/// existing registry, corpus, capacity, demand, and audit records; it does not
/// introduce a second language store, provider, or learning pipeline.
/// </summary>
internal sealed class LegendConnectOperations : ILegendConnectOperations
{
    private const int LanguageKnowledgeDetailRecordLimit = 250;

    private readonly MasterAppDbContext _db;
    private readonly ILegendLanguageRegistry _registry;
    private readonly LegendConnectCorpusService _corpus;
    private readonly IConfiguration _configuration;
    private readonly ILegendConnectOperationalEventWriter? _operationalEvents;
    private readonly ILegendConnectRuntimePolicyAuthority? _runtimePolicy;
    private readonly LegendConnectCurriculumService _curriculum;
    private readonly LegendConnectFounderTrainingIngestionAuthority _founderTrainingIngestion;
    private readonly ILegendConnectTranslationIntelligence _intelligence;
    private readonly ITranslationCapacityAuthority? _capacityAuthority;

    public LegendConnectOperations(
        MasterAppDbContext db,
        ILegendLanguageRegistry registry,
        LegendConnectCorpusService corpus,
        IConfiguration configuration,
        ILegendConnectOperationalEventWriter? operationalEvents = null,
        ILegendConnectRuntimePolicyAuthority? runtimePolicy = null,
        LegendConnectCurriculumService? curriculum = null,
        LegendConnectFounderTrainingIngestionAuthority? founderTrainingIngestion = null,
        ILegendConnectTranslationIntelligence? intelligence = null,
        ITranslationCapacityAuthority? capacityAuthority = null)
    {
        _db = db;
        _registry = registry;
        _corpus = corpus;
        _configuration = configuration;
        _operationalEvents = operationalEvents;
        _runtimePolicy = runtimePolicy;
        _curriculum = curriculum ?? new LegendConnectCurriculumService(_db, _registry, _corpus);
        _founderTrainingIngestion = founderTrainingIngestion ?? new LegendConnectFounderTrainingIngestionAuthority(
            _db, _registry, _corpus, _curriculum, _operationalEvents);
        _intelligence = intelligence ?? new LegendConnectTranslationIntelligence(_db, _configuration, _runtimePolicy);
        _capacityAuthority = capacityAuthority;
    }

    public async Task<LegendConnectDashboardSnapshot> GetDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        // Ensures the data-backed baseline is available for a newly initialized
        // environment without treating the baseline list as a runtime authority.
        await _registry.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
        var activeLearningEvents = ActiveLearningEvents(state).ToList();
        var activeCandidates = ActiveCandidates(state).ToList();
        var languages = state.Languages
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Select(item => BuildLanguageHealth(item, state))
            .ToList();
        var pairs = state.Pairs
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.PairKey, StringComparer.OrdinalIgnoreCase)
            .Select(item => BuildPairHealth(item, state))
            .ToList();

        var currentPeriod = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var providerCapacity = _capacityAuthority is null
            ? null
            : await _capacityAuthority.GetSnapshotAsync("AzureTranslator", cancellationToken);
        var runtime = _runtimePolicy is null ? null : await _runtimePolicy.GetEffectiveAsync(cancellationToken);
        var capacity = state.Capacities
            .Where(item => item.Provider == "AzureTranslator" && item.BillingPeriodStart == currentPeriod)
            .OrderByDescending(item => item.UpdatedUtc)
            .FirstOrDefault();
        var configuredCapacity = providerCapacity is not null
            ? providerCapacity.MonthlyIncludedCharacterAllowance ?? 0
            : runtime?.MonthlyProviderCapacityCharacters ?? capacity?.ConfiguredCapacityCharacters ?? Math.Max(0,
                _configuration.GetValue<long?>("LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters") ?? 0);
        var liveReserve = providerCapacity is not null
            ? providerCapacity.MonthlyLiveReserveCharacters ?? 0
            : runtime?.LiveTranslationReserveCharacters ?? capacity?.ReservedLiveCapacityCharacters ?? Math.Max(0,
                _configuration.GetValue<long?>("LegendConnect:Providers:AzureTranslator:LiveReserveCharacters") ?? 0);
        var used = providerCapacity is not null ? providerCapacity.MonthlyCharactersConsumed : (capacity is null
            ? 0
            : capacity.LiveCharactersConsumed + capacity.BootstrapCharactersConsumed + capacity.TrainingCharactersConsumed);
        var inFlight = providerCapacity is not null ? providerCapacity.MonthlyReservedCharacters : capacity?.ReservedLiveCharacters ?? 0;
        // The synchronized projection owns the aggregate billing total. The
        // existing period ledger remains the one place that distinguishes
        // live traffic from corpus work for the operational breakdown.
        var consumedLive = capacity?.LiveCharactersConsumed ?? 0;
        var consumedCorpus = capacity is null
            ? 0
            : capacity.BootstrapCharactersConsumed + capacity.TrainingCharactersConsumed;
        var corpusLimit = providerCapacity is not null
            ? providerCapacity.MaximumSafeCorpusConsumptionCharacters ?? 0
            : runtime?.MaximumSafeCorpusConsumptionCharacters ?? Math.Max(0, configuredCapacity - liveReserve);
        long? remainingSafe = providerCapacity is not null ? providerCapacity.MonthlyRemainingCharacters : (configuredCapacity > 0
            ? Math.Max(0, configuredCapacity - used - inFlight - liveReserve)
            : null);
        long? safeAcquisition = providerCapacity is not null ? providerCapacity.SafeAcquisitionCharacters : (configuredCapacity > 0
            ? Math.Max(0, Math.Min(
                configuredCapacity - used - inFlight - liveReserve,
                corpusLimit - consumedCorpus - inFlight))
            : null);

        var recentEvents = state.OperationalEvents
            .OrderByDescending(item => item.OccurredUtc)
            .Take(50)
            .Select(ToSnapshot)
            .ToList();
        var lastLearning = activeLearningEvents
            .Where(item => item.ProcessingState == "Processed")
            .Select(item => item.ProcessedUtc)
            .Concat(state.Alignments
                .Where(item => item.SupersededUtc is null &&
                    state.TextUnits.Any(unit => unit.Id == item.SourceTextUnitId && unit.IsTrainingEligible) &&
                    state.TextUnits.Any(unit => unit.Id == item.TargetTextUnitId && unit.IsTrainingEligible))
                .Select(item => (DateTime?)item.UpdatedUtc))
            .Where(item => item != null)
            .Max();
        var duplicateCount = state.OperationalEvents.LongCount(item => item.Category == "DuplicatePrevention" && item.Status == "Prevented") +
            state.AuditEntries.LongCount(item => item.Result == "DuplicatePrevented");
        var translationOpportunities = state.Demand.Sum(item => item.TranslationRequestCount);
        var contextualInternalServed = state.Demand.Sum(item => item.ContextualInternalServeCount);
        var internalServed = state.Demand.Sum(item => item.TranslationMemoryHitCount) + contextualInternalServed;
        var azureFallbacks = state.Demand.Sum(item => item.AzureFallbackCount);
        var consentedLiveEvents = state.LearningEvents
            .Where(item => item.Provenance == "ConsentedLiveTranslation")
            .ToArray();
        var consentedLiveAccountCount = await _db.MobileProfileSettings
            .AsNoTracking()
            .LongCountAsync(item => item.AllowsConsentedTranslationLearning, cancellationToken);

        return new LegendConnectDashboardSnapshot(
            languages,
            pairs,
            state.SystemUsage.Sum(item => item.SameLanguageBypassCount),
            state.Demand.Sum(item => item.TranslationMemoryHitCount),
            azureFallbacks,
            used,
            configuredCapacity,
            liveReserve,
            remainingSafe,
            activeLearningEvents.LongCount(item => item.EligibilityState == "Eligible" && item.ProcessingState is "Pending" or "Processing"),
            activeLearningEvents.LongCount(item => !string.IsNullOrWhiteSpace(item.FailureCode)) +
                activeCandidates.LongCount(item => !string.IsNullOrWhiteSpace(item.FailureCode)),
            duplicateCount,
            lastLearning,
            recentEvents,
            state.SystemUsage.Sum(item => item.ProviderOperationCount),
            state.SystemUsage.Sum(item => item.ProviderBillableCharacters),
            state.SystemUsage.Sum(item => item.SameLanguageCharactersAvoided),
            state.SystemUsage.Sum(item => item.TranslationMemoryCharactersAvoided),
            state.SystemUsage.Sum(item => item.ContextualCharactersAvoided),
            state.SystemUsage.Sum(item => item.QuotaDeniedRequestCount),
            state.SystemUsage.Sum(item => item.ProviderFailureCount),
            state.SystemUsage.Sum(item => item.GroupUniqueTargetReuseCount),
            contextualInternalServed,
            translationOpportunities == 0 ? 0m : Math.Round((decimal)internalServed / translationOpportunities, 4),
            translationOpportunities == 0 ? 0m : Math.Round((decimal)azureFallbacks / translationOpportunities, 4),
            translationOpportunities == 0 ? 0m : Math.Round((decimal)internalServed / translationOpportunities, 4),
            consumedLive,
            consumedCorpus,
            inFlight,
            safeAcquisition,
            currentPeriod,
            currentPeriod.AddMonths(1).AddDays(-1),
            consentedLiveAccountCount,
            consentedLiveEvents.LongCount(item => item.EligibilityState == "Eligible"),
            consentedLiveEvents.LongCount(item => item.PromotionOutcome == "Promoted"),
            consentedLiveEvents.LongCount(item => item.PromotionOutcome == "Reused"),
            consentedLiveEvents.LongCount(item => item.ProcessingState is "Pending" or "Processing"),
            state.FounderTrainingSubmissions.LongCount(),
            state.FounderTrainingSubmissionUnits.LongCount(),
            state.FounderTrainingSubmissions.LongCount(item => item.LegacySourceTextUnitId is not null &&
                state.TextUnits.Any(unit => unit.Id == item.LegacySourceTextUnitId && !unit.IsTrainingEligible)),
            state.Alignments.LongCount(item => item.SupersededUtc is null &&
                state.TextUnits.Any(unit => unit.Id == item.SourceTextUnitId && unit.IsTrainingEligible) &&
                state.TextUnits.Any(unit => unit.Id == item.TargetTextUnitId && unit.IsTrainingEligible)),
            providerCapacity);
    }

    public Task<LegendConnectProviderCapacitySnapshot> GetProviderCapacityAsync(
        CancellationToken cancellationToken = default) =>
        _capacityAuthority is not null
            ? _capacityAuthority.GetSnapshotAsync("AzureTranslator", cancellationToken)
            : Task.FromResult(new LegendConnectProviderCapacitySnapshot(
                "AzureTranslator", false, "Unavailable", null, null, null,
                new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
                new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1).AddDays(-1),
                null, 0, 0, null, null, null,
                AzureTranslatorSubscriptionCapacity.CapacityWindowMinutes,
                DateTime.UtcNow.AddMinutes(-AzureTranslatorSubscriptionCapacity.CapacityWindowMinutes),
                DateTime.UtcNow, null, 0, 0, null, null, null, DateTime.UtcNow,
                "Azure Translator capacity synchronization is unavailable."));

    public async Task<LegendConnectLanguageHealthSnapshot?> GetLanguageHealthAsync(
        string languageCode,
        CancellationToken cancellationToken = default)
    {
        await _registry.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
        var language = ResolveLanguage(state.Languages, languageCode);
        return language is null ? null : BuildLanguageHealth(language, state);
    }

    public async Task<LegendConnectLanguageKnowledgeSnapshot?> GetLanguageKnowledgeAsync(
        string languageCode,
        CancellationToken cancellationToken = default)
    {
        // The registry remains responsible for ensuring its data-backed
        // baseline before this Founder-only operational projection is read.
        await _registry.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
        var language = ResolveLanguage(state.Languages, languageCode);
        if (language is null)
            return null;

        // Founder knowledge inspection intentionally excludes text retained
        // from consented private conversations. Those assets remain usable by
        // the one server-side router, while aggregate event metadata proves
        // their governance without turning Founder operations into a private
        // conversation viewer.
        var displayableTextById = state.TextUnits
            .Where(item => item.IsTrainingEligible &&
                !string.Equals(item.Provenance, "ConsentedLiveTranslation", StringComparison.Ordinal))
            .ToDictionary(item => item.Id);
        var canonicalEntries = displayableTextById.Values
            .Where(item => string.Equals(item.LanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedUtc)
            .Take(LanguageKnowledgeDetailRecordLimit)
            .Select(item => new LegendConnectLanguageTextUnitSnapshot(
                item.Id,
                item.Text,
                item.Provenance,
                item.CreatedUtc,
                item.UpdatedUtc))
            .ToList();

        var activeAlignments = state.Alignments
            .Where(item => item.SupersededUtc is null)
            .Where(item => displayableTextById.ContainsKey(item.SourceTextUnitId) && displayableTextById.ContainsKey(item.TargetTextUnitId))
            .Select(item => new
            {
                Alignment = item,
                Source = displayableTextById[item.SourceTextUnitId],
                Target = displayableTextById[item.TargetTextUnitId]
            })
            .Where(item =>
                string.Equals(item.Source.LanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Target.LanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Alignment.UpdatedUtc)
            .Take(LanguageKnowledgeDetailRecordLimit)
            .Select(item => new LegendConnectLanguageAlignmentDetailSnapshot(
                item.Alignment.Id,
                item.Alignment.PairKey,
                item.Source.LanguageCode,
                item.Source.Text,
                item.Target.LanguageCode,
                item.Target.Text,
                item.Alignment.Provider,
                item.Alignment.ProviderModel,
                item.Alignment.Confidence,
                item.Alignment.QualityState,
                item.Alignment.HumanVerified,
                item.Alignment.ObservationCount,
                item.Alignment.CreatedUtc,
                item.Alignment.UpdatedUtc))
            .ToList();

        var contextRelationships = state.ContextRelationships
            .Where(item => item.SupersededUtc is null)
            .Where(item => displayableTextById.ContainsKey(item.SourceTextUnitId) && displayableTextById.ContainsKey(item.RelatedTextUnitId))
            .Select(item => new
            {
                Relationship = item,
                Source = displayableTextById[item.SourceTextUnitId],
                Related = displayableTextById[item.RelatedTextUnitId]
            })
            .Where(item =>
                string.Equals(item.Source.LanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Related.LanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Relationship.UpdatedUtc)
            .Take(LanguageKnowledgeDetailRecordLimit)
            .Select(item => new LegendConnectLanguageContextRelationshipSnapshot(
                item.Relationship.Id,
                item.Relationship.PairKey,
                item.Source.LanguageCode,
                item.Source.Text,
                item.Related.LanguageCode,
                item.Related.Text,
                item.Relationship.RelationshipKind,
                item.Relationship.ContextCategory,
                item.Relationship.UsageRegister,
                item.Relationship.RegionalVariant,
                item.Relationship.Confidence,
                item.Relationship.QualityState,
                item.Relationship.Provenance,
                item.Relationship.ObservationCount,
                item.Relationship.CreatedUtc,
                item.Relationship.UpdatedUtc))
            .ToList();

        var languagePairs = state.Pairs
            .Where(item => item.IsEnabled)
            .Where(item =>
                string.Equals(item.SourceLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.TargetLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.PairKey, StringComparer.OrdinalIgnoreCase)
            .Select(item => BuildPairHealth(item, state))
            .ToList();

        var learningEvents = ActiveLearningEvents(state)
            .Where(item =>
                string.Equals(item.SourceLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.TargetLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase));
        var learningActivityCount = learningEvents.LongCount();
        var recentLearningActivity = learningEvents
            .OrderByDescending(item => item.CreatedUtc)
            .Take(LanguageKnowledgeDetailRecordLimit)
            .Select(item => new LegendConnectLanguageLearningActivitySnapshot(
                item.Id,
                item.PairKey,
                item.SourceLanguageCode,
                item.TargetLanguageCode,
                item.Provider,
                item.Provenance,
                item.EligibilityState,
                item.ProcessingState,
                item.AttemptCount,
                item.CreatedUtc,
                item.ProcessedUtc,
                item.FailureCode,
                item.PromotionOutcome))
            .ToList();

        var activeCurriculumExampleIds = displayableTextById.Count == 0
            ? Array.Empty<Guid>()
            : await _db.Set<LegendCurriculumExample>().AsNoTracking()
                .Where(item => item.SupersededUtc == null && displayableTextById.Keys.Contains(item.TextUnitId))
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken);
        var activeStructuralPatternIds = activeCurriculumExampleIds.Length == 0
            ? Array.Empty<Guid>()
            : await _db.Set<LegendLanguageStructuralEvidence>().AsNoTracking()
                .Where(item => item.SupersededUtc == null &&
                    activeCurriculumExampleIds.Contains(item.BaselineCurriculumExampleId) &&
                    activeCurriculumExampleIds.Contains(item.ComparedCurriculumExampleId))
                .Select(item => item.StructuralPatternId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
        var structuralPatterns = activeStructuralPatternIds.Length == 0
            ? new List<LegendConnectStructuralPatternSnapshot>()
            : await (
                from pattern in _db.Set<LegendLanguageStructuralPattern>().AsNoTracking()
                join family in _db.Set<LegendCurriculumFamily>().AsNoTracking()
                    on pattern.CurriculumFamilyId equals family.Id
                where activeStructuralPatternIds.Contains(pattern.Id) &&
                    pattern.LanguageCode == language.LanguageCode && pattern.SupersededUtc == null
                orderby pattern.UpdatedUtc descending
                select new LegendConnectStructuralPatternSnapshot(
                    family.FamilyKey,
                    pattern.LanguageCode,
                    pattern.VariationDimension,
                    pattern.MaturityState,
                    pattern.SupportCount,
                    pattern.ContradictionCount,
                    pattern.IsProductionEligible,
                    pattern.UpdatedUtc)
            ).Take(LanguageKnowledgeDetailRecordLimit).ToListAsync(cancellationToken);

        // Patterns retain a single controlled comparison and its owning
        // curriculum family. Reusable relationships are the existing
        // cross-family aggregation authority, so project them separately
        // rather than misrepresenting a per-family observation as the total
        // independent support for that relationship.
        var activeStructuralRelationshipIds = activeCurriculumExampleIds.Length == 0
            ? Array.Empty<Guid>()
            : await _db.Set<LegendLanguageStructuralEvidence>().AsNoTracking()
                .Where(item => item.SupersededUtc == null && item.StructuralRelationshipId != null &&
                    activeCurriculumExampleIds.Contains(item.BaselineCurriculumExampleId) &&
                    activeCurriculumExampleIds.Contains(item.ComparedCurriculumExampleId))
                .Select(item => item.StructuralRelationshipId!.Value)
                .Distinct()
                .ToArrayAsync(cancellationToken);
        var structuralRelationships = activeStructuralRelationshipIds.Length == 0
            ? new List<LegendConnectStructuralRelationshipSnapshot>()
            : await _db.Set<LegendLanguageStructuralRelationship>().AsNoTracking()
                .Where(item => activeStructuralRelationshipIds.Contains(item.Id) &&
                    item.LanguageCode == language.LanguageCode && item.SupersededUtc == null)
                .OrderByDescending(item => item.UpdatedUtc)
                .Take(LanguageKnowledgeDetailRecordLimit)
                .Select(item => new LegendConnectStructuralRelationshipSnapshot(
                    item.PairKey,
                    item.LanguageCode,
                    item.VariationDimension,
                    item.MaturityState,
                    item.SupportCount,
                    item.IndependentSourceCount,
                    item.HumanVerifiedSupportCount,
                    item.ProviderOnlySupportCount,
                    item.ContradictionCount,
                    item.IsProductionEligible,
                    item.UpdatedUtc))
                .ToListAsync(cancellationToken);

        return new LegendConnectLanguageKnowledgeSnapshot(
            BuildLanguageHealth(language, state),
            LanguageKnowledgeDetailRecordLimit,
            learningActivityCount,
            canonicalEntries,
            activeAlignments,
            contextRelationships,
            languagePairs,
            recentLearningActivity,
            structuralPatterns,
            structuralRelationships);
    }

    public async Task<LegendConnectPairHealthSnapshot?> GetPairHealthAsync(
        string pairKey,
        CancellationToken cancellationToken = default)
    {
        await _registry.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
        var pair = state.Pairs.SingleOrDefault(item =>
            string.Equals(item.PairKey, pairKey?.Trim(), StringComparison.OrdinalIgnoreCase));
        return pair is null ? null : BuildPairHealth(pair, state);
    }

    public Task<LegendConnectTranslationQualitySnapshot> GetTranslationQualityAsync(
        CancellationToken cancellationToken = default) =>
        _intelligence.GetTranslationQualityAsync(cancellationToken);

    public async Task<LegendConnectKnowledgeSubmissionResult> SubmitFounderKnowledgeAsync(
        string founderUserId,
        LegendConnectKnowledgeSubmission submission,
        CancellationToken cancellationToken = default,
        Guid? reusableSourceTextUnitId = null)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false, false, "founder_identity_required", "A verified Founder identity is required.",
                string.Empty, null, null, null, null, null);
        }

        var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
        try
        {
            var approved = submission with { Provenance = "FounderApproved" };
            var result = string.IsNullOrWhiteSpace(approved.TargetText)
                && string.IsNullOrWhiteSpace(approved.TargetLanguageCode)
                && reusableSourceTextUnitId is null
                ? await _founderTrainingIngestion.SubmitAsync(founder, approved, cancellationToken)
                : await _corpus.SubmitApprovedKnowledgeAsync(approved, cancellationToken, reusableSourceTextUnitId);
            if (result.Succeeded && result.AlignmentId is { } alignmentId)
                await _curriculum.AttachValidatedAlignmentAsync(alignmentId, cancellationToken);
            await WriteAuditAsync(founder, "FounderKnowledgeSubmitted", result, null, cancellationToken);
            if (result.DuplicatePrevented && _operationalEvents is not null)
            {
                await _operationalEvents.TryRecordAsync(
                    "DuplicatePrevention",
                    "Info",
                    "Prevented",
                    result.SourceLanguageCode,
                    result.PairKey,
                    result.ErrorCode,
                    summary: "Founder knowledge submission matched an existing canonical language entry.",
                    cancellationToken: cancellationToken);
            }
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<LegendConnectKnowledgeSubmissionResult> CorrectFounderKnowledgeAsync(
        string founderUserId,
        Guid supersededAlignmentId,
        LegendConnectKnowledgeSubmission replacement,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null || supersededAlignmentId == Guid.Empty)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false, false, "invalid_correction", "A verified Founder identity and existing alignment are required.",
                string.Empty, null, null, null, null, null);
        }

        var prior = await _db.Set<LegendTranslationAlignment>()
            .SingleOrDefaultAsync(item => item.Id == supersededAlignmentId && item.SupersededUtc == null, cancellationToken);
        if (prior is null)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false, false, "alignment_not_found", "The selected directional alignment is unavailable for correction.",
                string.Empty, null, null, null, null, null);
        }

        var source = await _registry.NormalizeEnabledTranslationLanguageAsync(replacement.SourceLanguageCode, cancellationToken);
        var target = await _registry.NormalizeEnabledTranslationLanguageAsync(replacement.TargetLanguageCode, cancellationToken);
        var expectedPair = source is null || target is null ? null : LegendLanguageIdentity.PairKey(source, target);
        if (!string.Equals(expectedPair, prior.PairKey, StringComparison.OrdinalIgnoreCase))
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false, false, "correction_pair_mismatch", "The replacement must remain in the selected directional pair.",
                source ?? string.Empty, target, expectedPair, null, null, null);
        }

        var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
        try
        {
            var priorSource = await _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                .SingleAsync(item => item.Id == prior.SourceTextUnitId, cancellationToken);
            var reusableSourceTextUnitId = string.Equals(
                LegendLanguageIdentity.TextHash(LegendLanguageIdentity.NormalizeText(replacement.SourceText)),
                priorSource.NormalizedHash,
                StringComparison.Ordinal)
                ? prior.SourceTextUnitId
                : (Guid?)null;
            var result = await SubmitFounderKnowledgeAsync(
                founder,
                replacement,
                cancellationToken,
                reusableSourceTextUnitId: reusableSourceTextUnitId);
            if (!result.Succeeded || result.AlignmentId is null)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                return result;
            }

            prior.SupersededUtc = DateTime.UtcNow;
            prior.SupersededByAlignmentId = result.AlignmentId;
            prior.QualityState = "Superseded";
            prior.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            await _curriculum.ReconcileSupersededAlignmentAsync(
                prior.PairKey,
                prior.SourceTextUnitId,
                prior.TargetTextUnitId,
                cancellationToken);
            if (string.Equals(prior.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal))
            {
                var retiredTargetTextUnitId = await _intelligence.RecordHumanCorrectionAsync(
                    prior.Id,
                    result.AlignmentId.Value,
                    cancellationToken);
                if (retiredTargetTextUnitId is not null)
                    await _curriculum.ReconcileSupersededExamplesAsync([retiredTargetTextUnitId.Value], cancellationToken);
            }
            await _corpus.RefreshPairCoverageAsync(prior.PairKey, cancellationToken);
            await _curriculum.AttachValidatedAlignmentAsync(result.AlignmentId.Value, cancellationToken);
            await WriteAuditAsync(founder, "FounderKnowledgeCorrected", result, supersededAlignmentId, cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<LegendConnectQualityReviewActionResult> ApproveProviderObservationAsync(
        string founderUserId,
        Guid alignmentId,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null || alignmentId == Guid.Empty)
            return InvalidQualityReviewAction();

        var result = await _intelligence.ApproveProviderObservationAsync(alignmentId, cancellationToken);
        if (result.Succeeded)
        {
            await _curriculum.AttachValidatedAlignmentAsync(alignmentId, cancellationToken);
            await WriteQualityReviewAuditAsync(founder, "FounderProviderObservationApproved", result, alignmentId, cancellationToken);
        }
        return ToQualityReviewActionResult(result);
    }

    public async Task<LegendConnectQualityReviewActionResult> RejectProviderObservationAsync(
        string founderUserId,
        Guid alignmentId,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null || alignmentId == Guid.Empty)
            return InvalidQualityReviewAction();

        var result = await _intelligence.RejectProviderObservationAsync(alignmentId, cancellationToken);
        if (result.Succeeded)
        {
            if (result.RetiredTargetTextUnitId is not null)
                await _curriculum.ReconcileSupersededExamplesAsync([result.RetiredTargetTextUnitId.Value], cancellationToken);
            await WriteQualityReviewAuditAsync(founder, "FounderProviderObservationRejected", result, alignmentId, cancellationToken);
        }
        return ToQualityReviewActionResult(result);
    }

    public async Task<LegendConnectQualityReviewActionResult> LeaveProviderObservationUnresolvedAsync(
        string founderUserId,
        Guid alignmentId,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null || alignmentId == Guid.Empty)
            return InvalidQualityReviewAction();

        var result = await _intelligence.LeaveProviderObservationUnresolvedAsync(alignmentId, cancellationToken);
        if (result.Succeeded)
            await WriteQualityReviewAuditAsync(founder, "FounderProviderObservationLeftUnresolved", result, alignmentId, cancellationToken);
        return ToQualityReviewActionResult(result);
    }

    public async Task<LegendConnectCurriculumSubmissionResult> SubmitFounderCurriculumAsync(
        string founderUserId,
        LegendConnectCurriculumBatchSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null)
        {
            return new LegendConnectCurriculumSubmissionResult(
                false, false, "founder_identity_required", "A verified Founder identity is required.", null, null, 0, 0);
        }

        var result = await _curriculum.SubmitFounderEnglishBatchAsync(submission, cancellationToken);
        _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
        {
            Id = Guid.NewGuid(),
            FounderUserId = founder,
            Action = "FounderCurriculumSubmitted",
            Result = result.DuplicatePrevented ? "DuplicatePrevented" : result.Succeeded ? "Succeeded" : result.ErrorCode ?? "Rejected",
            LanguageCode = "en",
            Detail = Bound(result.Message ?? result.ErrorCode, 500),
            OccurredUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task WriteAuditAsync(
        string founderUserId,
        string action,
        LegendConnectKnowledgeSubmissionResult result,
        Guid? supersededAlignmentId,
        CancellationToken cancellationToken)
    {
        _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
        {
            Id = Guid.NewGuid(),
            FounderUserId = founderUserId,
            Action = action,
            Result = result.DuplicatePrevented ? "DuplicatePrevented" : result.Succeeded ? "Succeeded" : result.ErrorCode ?? "Rejected",
            LanguageCode = Bound(result.SourceLanguageCode, 32) ?? string.Empty,
            PairKey = Bound(result.PairKey, 72),
            TextUnitId = result.SourceTextUnitId,
            AlignmentId = result.AlignmentId,
            SupersededAlignmentId = supersededAlignmentId,
            Detail = Bound(result.Message ?? result.ErrorCode, 500),
            OccurredUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task WriteQualityReviewAuditAsync(
        string founderUserId,
        string action,
        LegendProviderObservationResolution result,
        Guid alignmentId,
        CancellationToken cancellationToken)
    {
        _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
        {
            Id = Guid.NewGuid(),
            FounderUserId = founderUserId,
            Action = action,
            Result = result.Succeeded ? "Succeeded" : result.ErrorCode ?? "Rejected",
            LanguageCode = result.SourceLanguageCode ?? string.Empty,
            PairKey = result.PairKey,
            AlignmentId = alignmentId,
            Detail = Bound(result.Message, 500),
            OccurredUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static LegendConnectQualityReviewActionResult ToQualityReviewActionResult(
        LegendProviderObservationResolution result) => new(
        result.Succeeded,
        result.ErrorCode,
        result.Message,
        result.SourceLanguageCode,
        result.PairKey);

    private static LegendConnectQualityReviewActionResult InvalidQualityReviewAction() => new(
        false,
        "invalid_quality_review_action",
        "A verified Founder identity and active provider observation are required.");

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionIfNeededAsync(
        CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
            return null;
        return await _db.Database.BeginTransactionAsync(cancellationToken);
    }

    private static LegendConnectLanguageHealthSnapshot BuildLanguageHealth(
        LegendLanguageDefinition language,
        LegendConnectOperationalState state)
    {
        var activeLearningEvents = ActiveLearningEvents(state).ToList();
        var activeCandidates = ActiveCandidates(state).ToList();
        var approvedTextUnitIds = state.TextUnits
            .Where(item => item.IsTrainingEligible)
            .Select(item => item.Id)
            .ToHashSet();
        var unitIds = state.TextUnits
            .Where(item => item.IsTrainingEligible && item.LanguageCode == language.LanguageCode)
            .Select(item => item.Id)
            .ToHashSet();
        var pairs = state.Pairs
            .Where(item => item.SourceLanguageCode == language.LanguageCode || item.TargetLanguageCode == language.LanguageCode)
            .ToList();
        var pairKeys = pairs.Select(item => item.PairKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relationships = state.ContextRelationships.LongCount(item => item.SupersededUtc is null &&
            approvedTextUnitIds.Contains(item.SourceTextUnitId) &&
            approvedTextUnitIds.Contains(item.RelatedTextUnitId) &&
            (unitIds.Contains(item.SourceTextUnitId) || unitIds.Contains(item.RelatedTextUnitId)));
        var memoryRelationships = state.Alignments.LongCount(item =>
            item.SupersededUtc == null &&
            approvedTextUnitIds.Contains(item.SourceTextUnitId) &&
            approvedTextUnitIds.Contains(item.TargetTextUnitId) &&
            (unitIds.Contains(item.SourceTextUnitId) || unitIds.Contains(item.TargetTextUnitId)));
        var lastLearning = activeLearningEvents
            .Where(item => item.SourceLanguageCode == language.LanguageCode || item.TargetLanguageCode == language.LanguageCode)
            .Where(item => item.ProcessingState == "Processed")
            .Select(item => item.ProcessedUtc)
            .Concat(state.Alignments.Where(item =>
                    item.SupersededUtc is null &&
                    approvedTextUnitIds.Contains(item.SourceTextUnitId) &&
                    approvedTextUnitIds.Contains(item.TargetTextUnitId) &&
                    (unitIds.Contains(item.SourceTextUnitId) || unitIds.Contains(item.TargetTextUnitId)))
                .Select(item => (DateTime?)item.UpdatedUtc))
            .Where(item => item != null)
            .Max();
        var errors = ErrorsFor(state, language.LanguageCode, pairKeys);
        var duplicateCount = state.OperationalEvents.LongCount(item => item.Category == "DuplicatePrevention" &&
            string.Equals(item.LanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase)) +
            state.AuditEntries.LongCount(item => item.Result == "DuplicatePrevented" && item.LanguageCode == language.LanguageCode);
        var coverage = pairs.Count == 0 ? 0 : (int)Math.Round(pairs.Average(item => item.CorpusCoverage));
        var demand = state.Demand.Where(item => pairKeys.Contains(item.PairKey)).Sum(item => item.TranslationRequestCount);
        var azureFallbacks = state.Demand.Where(item => pairKeys.Contains(item.PairKey)).Sum(item => item.AzureFallbackCount);
        var approvedCandidates = activeCandidates.LongCount(item => item.IsApproved &&
            (string.Equals(item.SourceLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.TargetLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase)));
        var pendingCandidates = activeCandidates.LongCount(item => item.IsApproved && item.ProcessingState is "Pending" or "Processing" &&
            (string.Equals(item.SourceLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.TargetLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase)));
        var lastProviderAcquisition = activeCandidates
            .Where(item => item.ProcessingState == "Queued" &&
                (string.Equals(item.SourceLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.TargetLanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase)))
            .Select(item => (DateTime?)item.ProcessedUtc).Max();
        var lastFounderTraining = state.TextUnits
            .Where(item => item.IsTrainingEligible && item.Provenance == "FounderApproved" &&
                string.Equals(item.LanguageCode, language.LanguageCode, StringComparison.OrdinalIgnoreCase))
            .Select(item => (DateTime?)item.UpdatedUtc).Max();
        var quality = pairs.Any(item => item.QualityState == "Validated") ? "Validated" :
            pairs.Select(item => item.QualityState).FirstOrDefault() ?? "Observation";

        return new LegendConnectLanguageHealthSnapshot(
            language.LanguageCode,
            language.CanonicalName,
            language.IsEnabled,
            language.StoragePartition,
            unitIds.Count,
            memoryRelationships,
            relationships,
            pairs.Select(item => item.PairKey).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
            demand,
            coverage,
            quality,
            HealthState(errors.Count, unitIds.Count, demand),
            lastLearning,
            state.TextUnits.Where(item => item.IsTrainingEligible && item.LanguageCode == language.LanguageCode)
                .Select(item => (DateTime?)item.UpdatedUtc).Max(),
            duplicateCount,
            errors,
            approvedCandidates,
            pendingCandidates,
            demand == 0 ? 0m : Math.Round((decimal)azureFallbacks / demand, 4),
            lastProviderAcquisition,
            lastFounderTraining);
    }

    private static LegendConnectPairHealthSnapshot BuildPairHealth(
        LegendLanguagePair pair,
        LegendConnectOperationalState state)
    {
        var activeCandidates = ActiveCandidates(state).ToList();
        var demand = state.Demand.SingleOrDefault(item => item.PairKey == pair.PairKey);
        var errors = ErrorsFor(state, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pair.PairKey });
        var textById = state.TextUnits
            .Where(item => item.IsTrainingEligible)
            .ToDictionary(item => item.Id, item => item.Text);
        var alignments = state.Alignments
            .Where(item => item.PairKey == pair.PairKey && item.SupersededUtc == null)
            .Where(item => textById.ContainsKey(item.SourceTextUnitId) && textById.ContainsKey(item.TargetTextUnitId))
            .ToList();
        var lastLearning = ActiveLearningEvents(state)
            .Where(item => item.PairKey == pair.PairKey && item.ProcessingState == "Processed")
            .Select(item => item.ProcessedUtc)
            .Concat(alignments.Select(item => (DateTime?)item.UpdatedUtc))
            .Where(item => item != null)
            .Max();
        var total = demand?.TranslationRequestCount ?? 0;
        var fallback = demand?.AzureFallbackCount ?? 0;
        var memoryHits = demand?.TranslationMemoryHitCount ?? 0;
        var contextualInternal = demand?.ContextualInternalServeCount ?? 0;
        var internalServed = memoryHits + contextualInternal;
        var approvedBacklog = activeCandidates.LongCount(item => item.IsApproved &&
            item.ProcessingState is "Pending" or "Processing" &&
            string.Equals(LegendLanguageIdentity.PairKey(item.SourceLanguageCode, item.TargetLanguageCode), pair.PairKey, StringComparison.OrdinalIgnoreCase));
        var lastProviderAcquisition = activeCandidates
            .Where(item => item.ProcessingState == "Queued" &&
                string.Equals(LegendLanguageIdentity.PairKey(item.SourceLanguageCode, item.TargetLanguageCode), pair.PairKey, StringComparison.OrdinalIgnoreCase))
            .Select(item => (DateTime?)item.ProcessedUtc)
            .Max();
        var coverageAdditions = alignments.Count(item => item.CreatedUtc >= DateTime.UtcNow.AddDays(-30));
        var internalQuality = alignments.Count == 0
            ? 0m
            : Math.Round(alignments.Average(item => item.HumanVerified ? 1m : item.Confidence ?? 0m), 4);
        var recentAlignments = alignments
            .OrderByDescending(item => item.UpdatedUtc)
            .Take(25)
            .Select(item => new LegendConnectAlignmentSnapshot(
                item.Id,
                textById.GetValueOrDefault(item.SourceTextUnitId, "Unavailable approved entry"),
                textById.GetValueOrDefault(item.TargetTextUnitId, "Unavailable approved entry"),
                item.QualityState,
                item.HumanVerified,
                item.UpdatedUtc))
            .ToList();
        return new LegendConnectPairHealthSnapshot(
            pair.PairKey,
            pair.SourceLanguageCode,
            pair.TargetLanguageCode,
            demand?.TranslationRequestCount ?? 0,
            total,
            memoryHits,
            fallback,
            total == 0 ? 0m : Math.Round((decimal)fallback / total, 4),
            pair.CorpusCoverage,
            pair.QualityState,
            HealthState(errors.Count, alignments.Count, total),
            alignments.Select(item => (DateTime?)item.UpdatedUtc).Max(),
            lastLearning,
            errors.Count,
            recentAlignments,
            errors,
            contextualInternal,
            total == 0 ? 0m : Math.Round((decimal)internalServed / total, 4),
            total == 0 ? 0m : Math.Round((decimal)fallback / total, 4),
            total == 0 ? 0m : Math.Round((decimal)internalServed / total, 4),
            internalQuality,
            coverageAdditions,
            approvedBacklog,
            lastProviderAcquisition);
    }

    private static List<LegendConnectOperationalEventSnapshot> ErrorsFor(
        LegendConnectOperationalState state,
        string? languageCode,
        ISet<string> pairKeys)
    {
        var events = state.OperationalEvents
            .Where(item => item.Severity is "Warning" or "Error")
            .Where(item => !item.IsResolved)
            .Where(item =>
                (!string.IsNullOrWhiteSpace(languageCode) && string.Equals(item.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(item.PairKey) && pairKeys.Contains(item.PairKey)))
            .OrderByDescending(item => item.OccurredUtc)
            .Take(12)
            .Select(ToSnapshot)
            .ToList();

        var inferred = ActiveLearningEvents(state)
            .Where(item => !string.IsNullOrWhiteSpace(item.FailureCode))
            .Where(item =>
                (!string.IsNullOrWhiteSpace(languageCode) &&
                    (item.SourceLanguageCode == languageCode || item.TargetLanguageCode == languageCode)) ||
                pairKeys.Contains(item.PairKey))
            .OrderByDescending(item => item.CreatedUtc)
            .Take(12 - events.Count)
            .Select(item => new LegendConnectOperationalEventSnapshot(
                item.CreatedUtc, "LearningEvent", "Error", item.ProcessingState,
                item.SourceLanguageCode, item.PairKey, null, item.FailureCode,
                "A learning event recorded a bounded failure code.", false));
        events.AddRange(inferred);
        return events;
    }

    private static IEnumerable<LegendTranslationLearningEvent> ActiveLearningEvents(
        LegendConnectOperationalState state)
    {
        var activeTextIdentities = state.TextUnits
            .Where(item => item.IsTrainingEligible)
            .Select(item => TextIdentity(item.LanguageCode, item.NormalizedHash))
            .ToHashSet(StringComparer.Ordinal);
        return state.LearningEvents.Where(item =>
        {
            if (string.Equals(item.ProcessingState, "Superseded", StringComparison.Ordinal))
                return false;

            // Privacy-governance metadata deliberately has no reusable text
            // asset. Keep the aggregate-only audit entry visible without
            // making it an active linguistic authority.
            if (!string.Equals(item.EligibilityState, "Eligible", StringComparison.Ordinal))
                return true;

            if (!activeTextIdentities.Contains(TextIdentity(item.SourceLanguageCode, item.SourceTextHash)))
                return false;

            return item.ProcessingState is "Pending" or "Processing" ||
                activeTextIdentities.Contains(TextIdentity(item.TargetLanguageCode, item.TargetTextHash));
        });
    }

    private static IEnumerable<LegendCorpusCandidate> ActiveCandidates(
        LegendConnectOperationalState state)
    {
        var activeSources = state.TextUnits
            .Where(item => item.IsTrainingEligible)
            .ToDictionary(
                item => TextIdentity(item.LanguageCode, item.NormalizedHash),
                item => item.Text,
                StringComparer.Ordinal);
        return state.Candidates.Where(candidate =>
            activeSources.TryGetValue(TextIdentity(candidate.SourceLanguageCode, candidate.SourceTextHash), out var sourceText) &&
            string.Equals(sourceText, LegendLanguageIdentity.NormalizeText(candidate.SourceText), StringComparison.Ordinal));
    }

    private static string TextIdentity(string languageCode, string normalizedHash) =>
        $"{languageCode.Trim().ToUpperInvariant()}:{normalizedHash.Trim().ToUpperInvariant()}";

    private async Task<LegendConnectOperationalState> LoadStateAsync(CancellationToken cancellationToken) => new(
        await _db.Set<LegendLanguageDefinition>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendLanguagePair>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendLanguageTextUnit>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendTranslationAlignment>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendLanguageContextRelationship>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendTranslationLearningEvent>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendCorpusCandidate>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendTranslationPairDemand>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendTranslationSystemUsage>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendTranslationProviderCapacity>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendConnectOperationalEvent>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendConnectKnowledgeAuditEntry>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendFounderTrainingSubmission>().AsNoTracking().ToListAsync(cancellationToken),
        await _db.Set<LegendFounderTrainingSubmissionUnit>().AsNoTracking().ToListAsync(cancellationToken));

    private static LegendLanguageDefinition? ResolveLanguage(IEnumerable<LegendLanguageDefinition> languages, string value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        return languages.SingleOrDefault(item =>
            string.Equals(item.LanguageCode, candidate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.CanonicalName, candidate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.NativeName, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static LegendConnectOperationalEventSnapshot ToSnapshot(LegendConnectOperationalEvent item) => new(
        item.OccurredUtc, item.Category, item.Severity, item.Status, item.LanguageCode,
        item.PairKey, item.CorrelationId, item.ErrorCode, item.Summary, item.IsResolved);

    private static string HealthState(int errors, int entries, long demand) =>
        errors >= 3 ? "Critical" :
        errors > 0 ? "Warning" :
        entries == 0 && demand == 0 ? "Low activity" : "Healthy";

    private static string? NormalizeFounder(string? value) => Bound(value, 450);

    private static string? Bound(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private sealed record LegendConnectOperationalState(
        IReadOnlyList<LegendLanguageDefinition> Languages,
        IReadOnlyList<LegendLanguagePair> Pairs,
        IReadOnlyList<LegendLanguageTextUnit> TextUnits,
        IReadOnlyList<LegendTranslationAlignment> Alignments,
        IReadOnlyList<LegendLanguageContextRelationship> ContextRelationships,
        IReadOnlyList<LegendTranslationLearningEvent> LearningEvents,
        IReadOnlyList<LegendCorpusCandidate> Candidates,
        IReadOnlyList<LegendTranslationPairDemand> Demand,
        IReadOnlyList<LegendTranslationSystemUsage> SystemUsage,
        IReadOnlyList<LegendTranslationProviderCapacity> Capacities,
        IReadOnlyList<LegendConnectOperationalEvent> OperationalEvents,
        IReadOnlyList<LegendConnectKnowledgeAuditEntry> AuditEntries,
        IReadOnlyList<LegendFounderTrainingSubmission> FounderTrainingSubmissions,
        IReadOnlyList<LegendFounderTrainingSubmissionUnit> FounderTrainingSubmissionUnits);
}
