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

    public LegendConnectOperations(
        MasterAppDbContext db,
        ILegendLanguageRegistry registry,
        LegendConnectCorpusService corpus,
        IConfiguration configuration,
        ILegendConnectOperationalEventWriter? operationalEvents = null)
    {
        _db = db;
        _registry = registry;
        _corpus = corpus;
        _configuration = configuration;
        _operationalEvents = operationalEvents;
    }

    public async Task<LegendConnectDashboardSnapshot> GetDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        // Ensures the data-backed baseline is available for a newly initialized
        // environment without treating the baseline list as a runtime authority.
        await _registry.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
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
        var capacity = state.Capacities
            .Where(item => item.Provider == "AzureTranslator" && item.BillingPeriodStart == currentPeriod)
            .OrderByDescending(item => item.UpdatedUtc)
            .FirstOrDefault();
        var configuredCapacity = capacity?.ConfiguredCapacityCharacters ?? Math.Max(0,
            _configuration.GetValue<long?>("LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters") ?? 0);
        var liveReserve = capacity?.ReservedLiveCapacityCharacters ?? Math.Max(0,
            _configuration.GetValue<long?>("LegendConnect:Providers:AzureTranslator:LiveReserveCharacters") ?? 0);
        var used = capacity is null
            ? 0
            : capacity.LiveCharactersConsumed + capacity.BootstrapCharactersConsumed + capacity.TrainingCharactersConsumed;
        var inFlight = capacity?.ReservedLiveCharacters ?? 0;
        long? remainingSafe = configuredCapacity > 0
            ? Math.Max(0, configuredCapacity - used - inFlight - liveReserve)
            : null;

        var recentEvents = state.OperationalEvents
            .OrderByDescending(item => item.OccurredUtc)
            .Take(50)
            .Select(ToSnapshot)
            .ToList();
        var lastLearning = state.LearningEvents
            .Where(item => item.ProcessingState == "Processed")
            .Select(item => item.ProcessedUtc)
            .Concat(state.Alignments.Select(item => (DateTime?)item.UpdatedUtc))
            .Where(item => item != null)
            .Max();
        var duplicateCount = state.OperationalEvents.LongCount(item => item.Category == "DuplicatePrevention" && item.Status == "Prevented") +
            state.AuditEntries.LongCount(item => item.Result == "DuplicatePrevented");

        return new LegendConnectDashboardSnapshot(
            languages,
            pairs,
            state.SystemUsage.Sum(item => item.SameLanguageBypassCount),
            state.Demand.Sum(item => item.TranslationMemoryHitCount),
            state.Demand.Sum(item => item.AzureFallbackCount),
            used,
            configuredCapacity,
            liveReserve,
            remainingSafe,
            state.LearningEvents.LongCount(item => item.EligibilityState == "Eligible" && item.ProcessingState is "Pending" or "Processing"),
            state.LearningEvents.LongCount(item => !string.IsNullOrWhiteSpace(item.FailureCode)) +
                state.Candidates.LongCount(item => !string.IsNullOrWhiteSpace(item.FailureCode)),
            duplicateCount,
            lastLearning,
            recentEvents);
    }

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

        // This projection exposes only canonical units the existing central
        // policy has approved for retention and learning. Learning events can
        // include private-message metadata, so their text is never projected.
        var approvedTextById = state.TextUnits
            .Where(item => item.IsTrainingEligible)
            .ToDictionary(item => item.Id);
        var canonicalEntries = approvedTextById.Values
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
            .Where(item => approvedTextById.ContainsKey(item.SourceTextUnitId) && approvedTextById.ContainsKey(item.TargetTextUnitId))
            .Select(item => new
            {
                Alignment = item,
                Source = approvedTextById[item.SourceTextUnitId],
                Target = approvedTextById[item.TargetTextUnitId]
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
            .Where(item => approvedTextById.ContainsKey(item.SourceTextUnitId) && approvedTextById.ContainsKey(item.RelatedTextUnitId))
            .Select(item => new
            {
                Relationship = item,
                Source = approvedTextById[item.SourceTextUnitId],
                Related = approvedTextById[item.RelatedTextUnitId]
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

        var learningEvents = state.LearningEvents
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
                item.FailureCode))
            .ToList();

        return new LegendConnectLanguageKnowledgeSnapshot(
            BuildLanguageHealth(language, state),
            LanguageKnowledgeDetailRecordLimit,
            learningActivityCount,
            canonicalEntries,
            activeAlignments,
            contextRelationships,
            languagePairs,
            recentLearningActivity);
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

    public async Task<LegendConnectKnowledgeSubmissionResult> SubmitFounderKnowledgeAsync(
        string founderUserId,
        LegendConnectKnowledgeSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false, false, "founder_identity_required", "A verified Founder identity is required.",
                string.Empty, null, null, null, null, null);
        }

        var approved = submission with { Provenance = "FounderApproved" };
        var result = await _corpus.SubmitApprovedKnowledgeAsync(approved, cancellationToken);
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
        return result;
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

        var result = await SubmitFounderKnowledgeAsync(founder, replacement, cancellationToken);
        if (!result.Succeeded || result.AlignmentId is null)
            return result;

        prior.SupersededUtc = DateTime.UtcNow;
        prior.SupersededByAlignmentId = result.AlignmentId;
        prior.QualityState = "Superseded";
        prior.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(founder, "FounderKnowledgeCorrected", result, supersededAlignmentId, cancellationToken);
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

    private static LegendConnectLanguageHealthSnapshot BuildLanguageHealth(
        LegendLanguageDefinition language,
        LegendConnectOperationalState state)
    {
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
        var relationships = state.ContextRelationships.LongCount(item =>
            approvedTextUnitIds.Contains(item.SourceTextUnitId) &&
            approvedTextUnitIds.Contains(item.RelatedTextUnitId) &&
            (unitIds.Contains(item.SourceTextUnitId) || unitIds.Contains(item.RelatedTextUnitId)));
        var memoryRelationships = state.Alignments.LongCount(item =>
            item.SupersededUtc == null &&
            approvedTextUnitIds.Contains(item.SourceTextUnitId) &&
            approvedTextUnitIds.Contains(item.TargetTextUnitId) &&
            (unitIds.Contains(item.SourceTextUnitId) || unitIds.Contains(item.TargetTextUnitId)));
        var lastLearning = state.LearningEvents
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
            errors);
    }

    private static LegendConnectPairHealthSnapshot BuildPairHealth(
        LegendLanguagePair pair,
        LegendConnectOperationalState state)
    {
        var demand = state.Demand.SingleOrDefault(item => item.PairKey == pair.PairKey);
        var errors = ErrorsFor(state, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pair.PairKey });
        var textById = state.TextUnits
            .Where(item => item.IsTrainingEligible)
            .ToDictionary(item => item.Id, item => item.Text);
        var alignments = state.Alignments
            .Where(item => item.PairKey == pair.PairKey && item.SupersededUtc == null)
            .Where(item => textById.ContainsKey(item.SourceTextUnitId) && textById.ContainsKey(item.TargetTextUnitId))
            .ToList();
        var lastLearning = state.LearningEvents
            .Where(item => item.PairKey == pair.PairKey && item.ProcessingState == "Processed")
            .Select(item => item.ProcessedUtc)
            .Concat(alignments.Select(item => (DateTime?)item.UpdatedUtc))
            .Where(item => item != null)
            .Max();
        var total = demand?.TranslationRequestCount ?? 0;
        var fallback = demand?.AzureFallbackCount ?? 0;
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
            demand?.TranslationMemoryHitCount ?? 0,
            fallback,
            total == 0 ? 0m : Math.Round((decimal)fallback / total, 4),
            pair.CorpusCoverage,
            pair.QualityState,
            HealthState(errors.Count, alignments.Count, total),
            alignments.Select(item => (DateTime?)item.UpdatedUtc).Max(),
            lastLearning,
            errors.Count,
            recentAlignments,
            errors);
    }

    private static List<LegendConnectOperationalEventSnapshot> ErrorsFor(
        LegendConnectOperationalState state,
        string? languageCode,
        ISet<string> pairKeys)
    {
        var events = state.OperationalEvents
            .Where(item => item.Severity is "Warning" or "Error")
            .Where(item =>
                (!string.IsNullOrWhiteSpace(languageCode) && string.Equals(item.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(item.PairKey) && pairKeys.Contains(item.PairKey)))
            .OrderByDescending(item => item.OccurredUtc)
            .Take(12)
            .Select(ToSnapshot)
            .ToList();

        var inferred = state.LearningEvents
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
        await _db.Set<LegendConnectKnowledgeAuditEntry>().AsNoTracking().ToListAsync(cancellationToken));

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
        IReadOnlyList<LegendConnectKnowledgeAuditEntry> AuditEntries);
}
