Warning: truncated output (original token count: 89184)
Total output lines: 7033

using System.Globalization;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

namespace Infrastructure.Messaging;

/// <summary>
/// The one operational read/write facade for Founder surfaces. It composes
/// existing registry, corpus, capacity, demand, and audit records; it does not
/// introduce a second language store, provider, or learning pipeline.
/// </summary>
internal sealed class LegendConnectOperations : ILegendConnectOperations
{
    private const int LanguageKnowledgeDetailRecordLimit = 250;
    private const int TranslationRouteAuditRecordLimit = 250;
    private const int MaximumRetainedRetrievalLanguages = 64;
    private const int MaximumRetainedSemanticCandidates = 512;

    private readonly MasterAppDbContext _db;
    private readonly ILegendLanguageRegistry _registry;
    private readonly LegendConnectCorpusService _corpus;
    private readonly IConfiguration _configuration;
    private readonly ILegendConnectOperationalEventWriter? _operationalEvents;
    private readonly ILegendConnectRuntimePolicyAuthority? _runtimePolicy;
    private readonly LegendConnectCurriculumService? _curriculum;
    private readonly LegendConnectFounderTrainingIngestionAuthority? _founderTrainingIngestion;
    private readonly ILegendConnectTranslationIntelligence? _intelligence;
    private readonly ITranslationCapacityAuthority? _capacityAuthority;
    private readonly LegendConnectAutonomousLearningService? _autonomousLearning;
    private readonly ILegendConnectActiveModelInference? _activeModelInference;
    private readonly ILegendConnectResearchSearchTransport? _researchSearch;
    private readonly ILegendConnectResearchPageRetriever? _researchPages;

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
        ITranslationCapacityAuthority? capacityAuthority = null,
        LegendConnectAutonomousLearningService? autonomousLearning = null,
        ILegendConnectActiveModelInference? activeModelInference = null,
        ILegendConnectResearchSearchTransport? researchSearch = null,
        ILegendConnectResearchPageRetriever? researchPages = null)
    {
        _db = db;
        _registry = registry;
        _corpus = corpus;
        _configuration = configuration;
        _operationalEvents = operationalEvents;
        _runtimePolicy = runtimePolicy;
        // The production graph supplies these concrete authorities through
        // DI. Operations intentionally owns no constructor-created fallback:
        // a missing authority must fail at its explicit use boundary rather
        // than silently creating a competing curriculum or intelligence path.
        _curriculum = curriculum;
        _founderTrainingIngestion = founderTrainingIngestion;
        _intelligence = intelligence;
        _capacityAuthority = capacityAuthority;
        _autonomousLearning = autonomousLearning;
        _activeModelInference = activeModelInference;
        _researchSearch = researchSearch;
        _researchPages = researchPages;
    }

    private LegendConnectCurriculumService Curriculum => _curriculum ??
        throw new InvalidOperationException("Legend Connect curriculum authority is not available from the DI service graph.");

    private LegendConnectFounderTrainingIngestionAuthority FounderTrainingIngestion => _founderTrainingIngestion ??
        throw new InvalidOperationException("Legend Connect Founder-training ingestion authority is not available from the DI service graph.");

    private ILegendConnectTranslationIntelligence Intelligence => _intelligence ??
        throw new InvalidOperationException("Legend Connect translation-intelligence authority is not available from the DI service graph.");

    public async Task<LegendConnectDashboardSnapshot> GetDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        // Ensures the data-backed baseline is available for a newly initialized
        // environment without treating the baseline list as a runtime authority.
        await _registry.ListEnabledTranslationLanguagesAsync(cancellationToken);
        return await BuildDashboardAsync(await LoadStateAsync(cancellationToken), cancellationToken);
    }

    public async Task<LegendConnectDashboardProjectionSnapshot> GetDashboardProjectionAsync(
        string? languageCode,
        string? pairKey,
        CancellationToken cancellationToken = default)
    {
        // The registry baseline and all Founder-facing projections intentionally
        // share one read boundary. This preserves the existing authorities while
        // preventing a selected language or pair from reloading the full state.
        await _registry.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
        var dashboard = await BuildDashboardAsync(state, cancellationToken);
        var selectedLanguage = string.IsNullOrWhiteSpace(languageCode)
            ? null
            : await BuildLanguageKnowledgeAsync(state, languageCode, cancellationToken);
        var pair = ResolvePair(state.Pairs, pairKey);

        return new LegendConnectDashboardProjectionSnapshot(
            dashboard,
            selectedLanguage,
            pair is null ? null : BuildPairHealth(pair, state));
    }

    // Founder page reads deliberately begin here instead of the historical
    // dashboard projection above. The dashboard remains available to its
    // existing non-page consumers, but opening /founder/legend-connect never
    // materializes its complete corpus, evidence, or operational state.
    public async Task<LegendConnectFounderShellSnapshot> GetFounderShellAsync(
        string? languageCode,
        CancellationToken cancellationToken = default)
    {
        var languages = await _db.Set<LegendLanguageDefinition>().AsNoTracking()
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.CanonicalName)
            .Select(item => new LegendConnectFounderLanguageOptionSnapshot(
                item.LanguageCode,
                item.CanonicalName,
                item.IsEnabled))
            .ToListAsync(cancellationToken);
        var selectedCode = NormalizeFounderLanguageCode(languageCode, languages);
        if (selectedCode is null)
            return new LegendConnectFounderShellSnapshot(languages, null);

        var canonicalEntries = _db.Set<LegendLanguageTextUnit>().AsNoTracking()
            .Where(item => item.LanguageCode == selectedCode && item.IsTrainingEligible);
        var activeExamples = _db.Set<LegendCurriculumExample>().AsNoTracking()
            .Where(item => item.LanguageCode == selectedCode && item.SupersededUtc == null);
        var activeRelationships = _db.Set<LegendLanguageStructuralRelationship>().AsNoTracking()
            .Where(item => item.LanguageCode == selectedCode && item.SupersededUtc == null);
        var pendingLearning = _db.Set<LegendTranslationLearningEvent>().AsNoTracking()
            .Where(item => (item.SourceLanguageCode == selectedCode || item.TargetLanguageCode == selectedCode) &&
                item.EligibilityState == "Eligible" &&
                (item.ProcessingState == "Pending" || item.ProcessingState == "Processing"));
        var candidates = _db.Set<LegendLanguageTargetRealizationCandidate>().AsNoTracking()
            .Where(item => (item.SourceLanguageCode == selectedCode || item.TargetLanguageCode == selectedCode) &&
                item.SupersededUtc == null);
        var openIssues = _db.Set<LegendConnectOperationalEvent>().AsNoTracking()
            .Where(item => item.LanguageCode == selectedCode && !item.IsResolved &&
                (item.Severity == "Warning" || item.Severity == "Error"));
        var openIssueCount = await openIssues.LongCountAsync(cancellationToken);

        var summary = new LegendConnectFounderLanguageSummarySnapshot(
            selectedCode,
            languages.Single(item => item.LanguageCode == selectedCode).DisplayName,
            openIssueCount > 0 ? "Warning" : "Healthy",
            await canonicalEntries.LongCountAsync(cancellationToken),
            await activeExamples.LongCountAsync(cancellationToken),
            await _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
                .LongCountAsync(item => item.LanguageCode == selectedCode && item.SupersededUtc == null, cancellationToken),
            await activeRelationships.LongCountAsync(cancellationToken),
            await pendingLearning.LongCountAsync(cancellationToken),
            await candidates.LongCountAsync(cancellationToken),
            openIssueCount,
            await canonicalEntries.Select(item => (DateTime?)item.UpdatedUtc).MaxAsync(cancellationToken));
        return new LegendConnectFounderShellSnapshot(languages, summary);
    }

    public async Task<LegendConnectFounderSectionPageSnapshot> GetFounderSectionPageAsync(
        string section,
        string? languageCode,
        string? search,
        string? cursor,
        Guid? curriculumFamilyId = null,
        CancellationToken cancellationToken = default)
    {
        var language = await ResolveFounderLanguageCodeAsync(languageCode, cancellationToken);
        var normalizedSection = (section ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedSearch = NormalizeFounderSectionSearch(search);
        var pageCursor = ParseFounderSectionCursor(cursor);

        return normalizedSection switch
        {
            "curriculum" => await GetCurriculumFamilyPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "curriculum-examples" when curriculumFamilyId.HasValue => await GetCurriculumExamplePageAsync(language, curriculumFamilyId.Value, normalizedSearch, pageCursor, cancellationToken),
            "submissions" => await GetFounderSubmissionProcessingPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "candidates" => await GetCandidatePageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "evidence" => await GetAnchorPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "relationships" => await GetRelationshipPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "learning" => await GetLearningPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "machine-learning-lifecycle" => await GetMachineLearningLifecyclePageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "research-observability" => await GetResearchObservabilityPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "models" => await GetModelPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "health" => await GetHealthPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "retained-knowledge" => await GetRetainedKnowledgePageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "language-pairs" => await GetPairPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            "provider-observations" => await GetProviderObservationPageAsync(language, normalizedSearch, pageCursor, cancellationToken),
            _ => throw new ArgumentException("The requested Founder section is unavailable.", nameof(section))
        };
    }

    private async Task<LegendConnectDashboardSnapshot> BuildDashboardAsync(
        LegendConnectOperationalState state,
        CancellationToken cancellationToken)
    {
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
        var duplicateCount = state.DuplicateOperationalEventCount + state.DuplicateKnowledgeAuditCount;
        var translationOpportunities = state.Demand.Sum(item => item.TranslationRequestCount);
        var exactMemoryServed = state.Demand.Sum(item => item.TranslationMemoryHitCount);
        var contextualInternalServed = state.Demand.Sum(item => item.ContextualInternalServeCount);
        var structuralInternalServed = state.Demand.Sum(item => item.StructuralInternalServeCount);
        var promotedTranslationModelServed = state.Demand.Sum(item => item.NeuralModelServeCount);
        var promotedTranslationModelFailed = state.Demand.Sum(item => item.NeuralModelFailureCount);
        var providerObservationReused = state.Demand.Sum(item => item.ProviderObservationReuseCount);
        var nativeTranslationIntelligenceServed =
            exactMemoryServed +
            structuralInternalServed +
            contextualInternalServed +
            promotedTranslationModelServed;
        var azureFallbacks = state.Demand.Sum(item => item.AzureFallbackCount);
        var providerAvoidedRequests =
            nativeTranslationIntelligenceServed +
            providerObservationReused;
        var reconciledTerminalRoutes = providerAvoidedRequests + azureFallbacks;
        var translationRoutingReconciliationGap =
            translationOpportunities - reconciledTerminalRoutes;
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
            exactMemoryServed,
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
            translationOpportunities == 0 ? 0m : Math.Round((decimal)providerAvoidedRequests / translationOpportunities, 4),
            translationOpportunities == 0 ? 0m : Math.Round((decimal)azureFallbacks / translationOpportunities, 4),
            translationOpportunities == 0 ? 0m : Math.Round((decimal)nativeTranslationIntelligenceServed / translationOpportunities, 4),
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
            state.FounderTrainingSubmissionCount,
            state.FounderTrainingSubmissionUnitCount,
            state.RetiredLegacyFounderTrainingSubmissionCount,
            state.Alignments.LongCount(item => item.SupersededUtc is null &&
                state.TextUnits.Any(unit => unit.Id == item.SourceTextUnitId && unit.IsTrainingEligible) &&
                state.TextUnits.Any(unit => unit.Id == item.TargetTextUnitId && unit.IsTrainingEligible)),
            providerCapacity,
            StructuralCompositionCharactersAvoided:
                state.SystemUsage.Sum(item => item.StructuralCompositionCharactersAvoided),
            StructuralInternalServeCount:
                structuralInternalServed,
            PromotedTranslationModelServeCount:
                promotedTranslationModelServed,
            PromotedTranslationModelFailureCount:
                promotedTranslationModelFailed,
            ProviderObservationReuseCount:
                providerObservationReused,
            NativeTranslationIntelligenceServeCount:
                nativeTranslationIntelligenceServed,
            ReconciledTerminalRouteCount:
                reconciledTerminalRoutes,
            TranslationRoutingReconciliationGap:
                translationRoutingReconciliationGap,
            PromotedTranslationModelCharactersAvoided:
                state.SystemUsage.Sum(item => item.PromotedTranslationModelCharactersAvoided),
            ProviderObservationCharactersAvoided:
                state.SystemUsage.Sum(item => item.ProviderObservationCharactersAvoided),
            CrossLanguageTranslationRequestCount:
                translationOpportunities);
    }

    public Task<LegendConnectMachineTeachingSubmissionResult>
        SubmitMachineTeachingProposalAsync(
            LegendConnectMachineTeachingSubmission submission,
            CancellationToken cancellationToken = default) =>
        _autonomousLearning is null
            ? Task.FromResult(
                new LegendConnectMachineTeachingSubmissionResult(
                    false,
                    false,
                    "Unavailable",
                    "autonomous_learning_unavailable",
                    "The existing autonomous learning authority is unavailable.",
                    null,
                    null))
            : _autonomousLearning
                .SubmitConversationMachineProposalAsync(
                    submission,
                    cancellationToken);

    public async Task<LegendConnectResearchNeededDecision>
        DecideResearchNeededAsync(
            string input,
            string sourceLanguageCode,
            LegendConnectNativeInferenceSnapshot? internalInference,
            CancellationToken cancellationToken = default)
    {
        var governedLanguage =
            await _registry.NormalizeEnabledTranslationLanguageAsync(
                sourceLanguageCode,
                cancellationToken);
        return DecideResearchNeeded(
            input,
            governedLanguage ?? sourceLanguageCode,
            internalInference,
            DateTime.UtcNow,
            languageGoverned: governedLanguage is not null);
    }

    internal static LegendConnectResearchNeededDecision DecideResearchNeeded(
        string input,
        string sourceLanguageCode,
        LegendConnectNativeInferenceSnapshot? internalInference,
        DateTime decidedUtc,
        bool languageGoverned = true,
        LegendConnectDiscourseStateSnapshot? discourseState = null)
    {
        var question = (input ?? string.Empty).Trim();
        var normalized = question.ToLowerInvariant();
        var accessClass = ClassifyResearchAccess(normalized);
        var internalAvailable = internalInference is
        {
            Supported: true,
            Answer: not null
        };
        var reasonCode = (internalInference?.ReasonCode ?? string.Empty)
            .ToLowerInvariant();
        var authoritySummary = (internalInference?.AuthoritySummary ?? string.Empty)
            .ToLowerInvariant();
        var stale = ContainsResearchSignal(
                reasonCode,
                "stale", "expired", "outdated", "superseded") ||
            ContainsResearchSignal(
                authoritySummary,
                "stale evidence", "expired evidence", "outdated evidence",
                "superseded evidence");
        var conflicted = ContainsResearchSignal(
                reasonCode,
                "conflict", "contradict", "unresolved_evidence") ||
            ContainsResearchSignal(
                authoritySummary,
                "unresolved conflict", "conflicting evidence",
                "contradictory evidence", "retained contradiction");
        var namedSource = TryIdentifyNamedExternalSource(question);

        LegendConnectResearchNeededDecision Decision(
            bool required,
            LegendConnectResearchNeed need,
            string reasonCode) =>
            new(
                required,
                need,
                reasonCode,
                accessClass,
                sourceLanguageCode,
                internalAvailable,
                stale,
                conflicted,
                namedSource,
                decidedUtc);

        if (!languageGoverned || string.IsNullOrWhiteSpace(question))
        {
            return Decision(
                false,
                LegendConnectResearchNeed.NotResearchable,
                languageGoverned
                    ? "research_input_empty"
                    : "research_source_language_not_governed");
        }

        if (namedSource is not null)
        {
            return Decision(
                true,
                LegendConnectResearchNeed.NamedExternalDocumentOrSource,
                "named_external_source_requires_research");
        }

        // Current internal LEGEND state must stay with existing governed
        // operational tools. Internet research is never a substitute for the
        // database, runtime, model, training, capacity, or readiness authority.
        if (IsInternalLegendSystemQuestion(normalized))
        {
            return Decision(
                false,
                internalAvailable
                    ? LegendConnectResearchNeed.ExistingGovernedKnowledge
                    : LegendConnectResearchNeed.NotResearchable,
                internalAvailable
                    ? "existing_governed_knowledge_answers_request"
                    : "internal_legend_state_requires_governed_tools");
        }

        if (conflicted)
        {
            return Decision(
                true,
                LegendConnectResearchNeed.ConflictingInternalEvidence,
                "conflicting_internal_evidence_requires_research");
        }

        if (stale)
        {
            return Decision(
                true,
                LegendConnectResearchNeed.StaleInternalEvidence,
                "stale_internal_evidence_requires_research");
        }

        if (ContainsResearchSignal(
                normalized,
                "verify ", "verify that", "fact-check", "fact check",
                "confirm with a source", "check the source", "cite sources",
                "provide citations", "look this up", "look up ",
                "research this", "research "))
        {
            return Decision(
                true,
                LegendConnectResearchNeed.ExplicitVerificationRequest,
                "explicit_verification_requires_research");
        }

        // A governed reference binding on the current user turn proves that
        // the request depends on conversation-scoped semantic state. Public
        // research cannot supply authority for that state. Apply this before
        // time-sensitive and external-knowledge-gap classification so words
        // such as "which" or "now" cannot turn a discourse reference into an
        // unrelated internet request. An unresolved governed reference is
        // equally conversation-scoped and must remain on the conversational
        // fail-closed/escalation path rather than be researched externally.
        if (HasCurrentTurnDiscourseAuthority(discourseState))
        {
            return Decision(
                false,
                LegendConnectResearchNeed.NotResearchable,
                "conversation_context_is_not_external_research");
        }

        if (ContainsResearchSignal(
                normalized,
                "current ", "currently ", "latest ", "today", "right now",
                "as of ", "recent ", "this week", "this month", "this year",
                "up-to-date", "up to date") &&
            IsExternalFactualQuestion(normalized))
        {
            return Decision(
                true,
                LegendConnectResearchNeed.CurrentOrTimeSensitiveInformation,
                "time_sensitive_information_requires_research");
        }

        if (internalAvailable)
        {
            return Decision(
                false,
                LegendConnectResearchNeed.ExistingGovernedKnowledge,
                "existing_governed_knowledge_answers_request");
        }

        // Questions whose evidence is the current conversation cannot gain
        // authority from the public internet.  When native discourse binding
        // is incomplete, leave the request to the normal conversational
        // provider path with its transcript rather than launching irrelevant
        // web research.
        if (IsConversationInternalQuestion(normalized))
        {
            return Decision(
                false,
                LegendConnectResearchNeed.NotResearchable,
                "conversation_context_is_not_external_research");
        }

        if (internalInference is { RequiresEscalation: true } &&
            IsExternalFactualQuestion(normalized))
        {
            return Decision(
                true,
                LegendConnectResearchNeed.InternalKnowledgeGap,
                "external_factual_internal_knowledge_gap");
        }

        return Decision(
            false,
            LegendConnectResearchNeed.NotResearchable,
            "unfamiliar_wording_is_not_research_authority");
    }

    public async Task<LegendConnectResearchOutcome> ExecuteResearchAsync(
        LegendConnectResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedUtc = DateTime.UtcNow;
        var sessionId = Guid.NewGuid();

        LegendConnectResearchOutcome Failure(
            string reasonCode,
            string text,
            bool retryable = false,
            string transport = "Unavailable",
            string? model = null,
            string settings = "Unavailable",
            long latency = 0,
            long? cost = null,
            IReadOnlyList<LegendConnectResearchSearchQueryReceipt>? searchQueryReceipts = null,
            IReadOnlyList<LegendConnectResearchPageReceipt>? pageReceipts = null,
            LegendConnectResearchLanguageLineage? languageLineage = null,
            string? searchProvider = null,
            IReadOnlyList<LegendConnectBoundedSearchQuery>? executedQueries = null,
            IReadOnlyList<LegendConnectSearchResult>? searchResults = null,
            IReadOnlyList<LegendConnectResearchSourceIdentity>? sources = null,
            IReadOnlyList<LegendConnectRetrievedDocument>? documents = null,
            IReadOnlyList<LegendConnectClaimEvidence>? claimEvidence = null,
            IReadOnlyList<LegendConnectContradictingEvidence>? contradictingEvidence = null,
            IReadOnlyList<LegendConnectCitation>? citations = null,
            long searchLatency = 0,
            long retrievalLatency = 0,
            long reasoningLatency = 0,
            long? searchCost = null,
            long? modelCost = null)
        {
            var completedUtc = DateTime.UtcNow;
            var session = new LegendConnectResearchSession(
                sessionId,
                request.RequestId,
                startedUtc,
                completedUtc,
                executedQueries ?? [],
                searchResults ?? [],
                sources ?? [],
                documents ?? [],
                claimEvidence ?? [],
                contradictingEvidence ?? [],
                citations ?? [],
                latency,
                cost,
                "Failure",
                reasonCode,
                searchQueryReceipts,
                pageReceipts,
                languageLineage,
                SearchLatencyMilliseconds: searchLatency,
                RetrievalLatencyMilliseconds: retrievalLatency,
                ReasoningLatencyMilliseconds: reasoningLatency,
                SearchCostMicrounits: searchCost,
                ModelCostMicrounits: modelCost);
            var provenance = BuildResearchProvenance(
                request,
                session,
                LegendConnectResearchEvidenceOrigin.UnresolvedEvidence,
                transport,
                model,
                settings,
                searchProvider);
            return new LegendConnectResearchOutcome(
                LegendConnectResearchOutcomeState.Failure,
                LegendConnectResearchEvidenceOrigin.UnresolvedEvidence,
                request.Decision,
                session,
                null,
                null,
                null,
                new LegendConnectResearchFailureResult(
                    reasonCode,
                    "LEGEND_RESEARCH_FAILURE[" + reasonCode + "]",
                    retryable,
                    text),
                provenance);
        }

        if (!TryValidateResearchRequest(request, out var requestFailure))
        {
            return Failure(
                requestFailure,
                "LEGEND did not start internet research because the bounded request or authorization was invalid.");
        }

        if (request.Decision.AccessClass is
            LegendConnectResearchAccessClass.AuthenticatedReadOnly or
            LegendConnectResearchAccessClass.PrivateReadOnly)
        {
            return Failure(
                "research_authenticated_private_transport_unavailable",
                "LEGEND cannot use the public zero-write research transport to access authenticated or private material.");
        }

        if (request.Decision.AccessClass ==
            LegendConnectResearchAccessClass.MutationCapable)
        {
            return Failure(
                "research_zero_write_boundary",
                "LEGEND research is read-only and cannot perform a mutation-capable internet operation.");
        }

        if (_researchSearch is null || _researchPages is null)
        {
            return Failure(
                "internet_research_transport_unavailable",
                "LEGEND could not perform the required external research because its bounded search or canonical page transport is unavailable.");
        }

        using var totalResearchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalResearchCancellation.CancelAfter(
            TimeSpan.FromSeconds(
                LegendConnectResearchContracts.TotalResearchDeadlineSeconds));
        var researchDeadlineUtc = startedUtc.AddSeconds(
            LegendConnectResearchContracts.TotalResearchDeadlineSeconds);

        LegendConnectResearchSearchTransportResult searchResult;
        try
        {
            searchResult = await _researchSearch.SearchAsync(
                new LegendConnectResearchSearchTransportRequest(
                    sessionId,
                    request.Decision.SourceLanguageCode,
                    request.Queries,
                    request.MaximumResults,
                    request.MaximumClaims),
                totalResearchCancellation.Token);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(
                "internet_research_total_deadline_exceeded",
                "LEGEND external research exceeded its total bounded deadline.",
                retryable: true);
        }
        catch
        {
            return Failure(
                "internet_research_search_transport_failed",
                "LEGEND external research search failed before public page candidates were returned.",
                retryable: true);
        }

        if (!searchResult.Succeeded)
        {
            return Failure(
                searchResult.FailureReason ??
                    "internet_research_search_transport_failed",
                "LEGEND external research did not return bounded public page candidates.",
                searchResult.Retryable,
                searchResult.Transport,
                searchResult.ModelVersion,
                searchResult.SettingsIdentity,
                searchResult.LatencyMilliseconds,
                searchResult.CostMicrounits,
                searchResult.QueryReceipts,
                [],
                searchProvider: searchResult.Provider,
                executedQueries: searchResult.ExecutedQueries,
                searchLatency: searchResult.LatencyMilliseconds,
                searchCost: searchResult.CostMicrounits);
        }

        if (!HasCompleteResearchSearchLineage(request, searchResult))
        {
            return Failure(
                "internet_research_search_provenance_incomplete",
                "LEGEND rejected the search candidates because their query, provider, URL, or language receipts were incomplete.",
                transport: searchResult.Transport,
                model: searchResult.ModelVersion,
                settings: searchResult.SettingsIdentity,
                latency: searchResult.LatencyMilliseconds,
                cost: searchResult.CostMicrounits,
                searchQueryReceipts: searchResult.QueryReceipts,
                pageReceipts: [],
                searchProvider: searchResult.Provider,
                executedQueries: searchResult.ExecutedQueries,
                searchLatency: searchResult.LatencyMilliseconds,
                searchCost: searchResult.CostMicrounits);
        }

        LegendConnectResearchPageRetrievalResult pageResult;
        try
        {
            pageResult = await _researchPages.RetrieveAsync(
                new LegendConnectResearchPageRetrievalRequest(
                    sessionId,
                    request.Decision.SourceLanguageCode,
                    searchResult.SearchResults,
                    searchResult.Sources,
                    request.MaximumDocuments,
                    request.MaximumDocumentCharacters,
                    Math.Min(
                        LegendConnectResearchContracts.MaximumTotalDocumentCharacters,
                        request.MaximumDocuments * request.MaximumDocumentCharacters),
                    researchDeadlineUtc),
                totalResearchCancellation.Token);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(
                "internet_research_total_deadline_exceeded",
                "LEGEND external research exceeded its total bounded deadline.",
                retryable: true,
                transport: searchResult.Transport,
                model: searchResult.ModelVersion,
                settings: searchResult.SettingsIdentity,
                latency: searchResult.LatencyMilliseconds,
                cost: searchResult.CostMicrounits,
                searchQueryReceipts: searchResult.QueryReceipts,
                pageReceipts: [],
                searchProvider: searchResult.Provider,
                executedQueries: searchResult.ExecutedQueries,
                searchResults: searchResult.SearchResults,
                sources: searchResult.Sources,
                searchLatency: searchResult.LatencyMilliseconds,
                searchCost: searchResult.CostMicrounits);
        }
        catch
        {
            return Failure(
                "internet_research_page_transport_failed",
                "LEGEND could not retrieve public evidence through its canonical read-only page path.",
                retryable: true,
                transport: searchResult.Transport,
                model: searchResult.ModelVersion,
                settings: searchResult.SettingsIdentity,
                latency: searchResult.LatencyMilliseconds,
                cost: searchResult.CostMicrounits,
                searchQueryReceipts: searchResult.QueryReceipts,
                pageReceipts: [],
                searchProvider: searchResult.Provider,
                executedQueries: searchResult.ExecutedQueries,
                searchResults: searchResult.SearchResults,
                sources: searchResult.Sources,
                searchLatency: searchResult.LatencyMilliseconds,
                searchCost: searchResult.CostMicrounits);
        }

        if (!pageResult.Succeeded)
        {
            return Failure(
                pageResult.FailureReason ?? "internet_research_page_retrieval_failed",
                "LEGEND could not retrieve an admissible public document through its canonical read-only page path.",
                pageResult.Retryable,
                searchResult.Transport + "->" + pageResult.Transport,
                searchResult.ModelVersion,
                LegendLanguageIdentity.TextHash(
                    searchResult.SettingsIdentity + "|" + pageResult.SettingsIdentity),
                searchResult.LatencyMilliseconds + pageResult.LatencyMilliseconds,
                searchResult.CostMicrounits,
                searchResult.QueryReceipts,
                pageResult.Receipts,
                new LegendConnectResearchLanguageLineage(
                    request.Decision.SourceLanguageCode,
                    searchResult.ExecutedQueries
                        .Select(item => item.QueryLanguageCode ?? item.SourceLanguageCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    [],
                    request.Decision.SourceLanguageCode,
                    request.Decision.SourceLanguageCode,
                    [],
                    "FailurePresentedAsLanguageNeutralReasonCode",
                    searchResult.Transport),
                searchResult.Provider,
                executedQueries: searchResult.ExecutedQueries,
                searchResults: searchResult.SearchResults,
                sources: searchResult.Sources,
                documents: pageResult.Documents,
                citations: pageResult.Citations,
                searchLatency: searchResult.LatencyMilliseconds,
                retrievalLatency: pageResult.LatencyMilliseconds,
                searchCost: searchResult.CostMicrounits);
        }

        var evidencePacket = BuildResearchEvidencePacket(
            request,
            searchResult,
            pageResult,
            (long)Math.Ceiling((DateTime.UtcNow - startedUtc).TotalMilliseconds));
        var transportLineageFailure =
            ResearchTransportLineageFailure(request, evidencePacket);
        if (transportLineageFailure is not null)
        {
            return Failure(
                transportLineageFailure,
                "LEGEND rejected the external research packet at the exact incomplete lineage boundary named by the failure code.",
                transport: evidencePacket.Transport,
                model: evidencePacket.ModelVersion,
                settings: evidencePacket.SettingsIdentity,
                latency: evidencePacket.LatencyMilliseconds,
                cost: evidencePacket.CostMicrounits,
                searchQueryReceipts: evidencePacket.SearchQueryReceipts,
                pageReceipts: evidencePacket.PageReceipts,
                languageLineage: evidencePacket.LanguageLineage,
                searchProvider: evidencePacket.SearchProvider,
                executedQueries: evidencePacket.ExecutedQueries,
                searchResults: evidencePacket.SearchResults,
                sources: evidencePacket.Sources,
                documents: evidencePacket.Documents,
                claimEvidence: evidencePacket.ClaimEvidence,
                contradictingEvidence: evidencePacket.ContradictingEvidence,
                citations: evidencePacket.Citations,
                searchLatency: searchResult.LatencyMilliseconds,
                retrievalLatency: pageResult.LatencyMilliseconds,
                searchCost: searchResult.CostMicrounits);
        }

        var reasoningStartedUtc = DateTime.UtcNow;
        var assessment =
            LegendConnectGovernedReasoningExecutor.AssessResearchEvidence(
                evidencePacket.Sources,
                evidencePacket.Documents,
                evidencePacket.Citations,
                evidencePacket.ClaimEvidence,
                evidencePacket.ContradictingEvidence,
                request.MinimumIndependentSources,
                reasoningStartedUtc,
                evidencePacket.LanguageLineage);
        var unresolvedInternalConflict =
            request.Decision.Need ==
                LegendConnectResearchNeed.ConflictingInternalEvidence &&
            assessment.State == LegendResearchEvidenceAssessmentState.Conclusion;
        var effectiveAssessmentState = unresolvedInternalConflict
            ? LegendResearchEvidenceAssessmentState.UnresolvedConflict
            : assessment.State;
        var effectiveReasonCode = unresolvedInternalConflict
            ? "internal_conflict_requires_discriminating_lineage"
            : assessment.ReasonCode;
        var evidenceOrigin = effectiveAssessmentState ==
            LegendResearchEvidenceAssessmentState.UnresolvedConflict
                ? LegendConnectResearchEvidenceOrigin.UnresolvedEvidence
                : !string.IsNullOrWhiteSpace(request.InternalAnswer)
                    ? LegendConnectResearchEvidenceOrigin.Combined
                    : LegendConnectResearchEvidenceOrigin.ExternalResearch;
        var presentationResult = LegendConnectCurriculumService.PresentResearchEvidence(
            effectiveAssessmentState,
            evidenceOrigin,
            request.Question,
            request.InternalAnswer,
            assessment.MaterialEvidence,
            assessment.Claims,
            assessment.Contradictions,
            assessment.ClaimResolutions,
            evidencePacket.Sources,
            evidencePacket.Documents,
            evidencePacket.Citations,
            evidencePacket.LanguageLineage,
            request.PresentationConstraints,
            effectiveReasonCode,
            reasoningStartedUtc);
        var completed = DateTime.UtcNow;
        var session = new LegendConnectResearchSession(
            sessionId,
            request.RequestId,
            startedUtc,
            completed,
            evidencePacket.ExecutedQueries,
            evidencePacket.SearchResults,
            evidencePacket.Sources,
            evidencePacket.Documents,
            evidencePacket.ClaimEvidence,
            evidencePacket.ContradictingEvidence,
            evidencePacket.Citations,
            Math.Max(
                0,
                (long)Math.Ceiling((completed - startedUtc).TotalMilliseconds)),
            evidencePacket.CostMicrounits,
            presentationResult.Succeeded
                ? effectiveAssessmentState.ToString()
                : "Failure",
            presentationResult.Succeeded
                ? null
                : presentationResult.ReasonCode,
            evidencePacket.SearchQueryReceipts,
            evidencePacket.PageReceipts,
            evidencePacket.LanguageLineage,
            LegendConnectResearchEvidenceAdmissibilityPolicy.PolicyIdentity,
            assessment.Admissibility,
            assessment.MaterialEvidence,
            assessment.ClaimResolutions,
            LegendConnectResearchContracts.ClaimEvidencePolicy,
            presentationResult.Presentation.CitationValidation,
            searchResult.LatencyMilliseconds,
            pageResult.LatencyMilliseconds,
            Math.Max(
                0,
                (long)Math.Ceiling(
                    (completed - reasoningStartedUtc).TotalMilliseconds)),
            searchResult.CostMicrounits,
            null);
        var provenance = BuildResearchProvenance(
            request,
            session,
            presentationResult.Succeeded
                ? evidenceOrigin
                : LegendConnectResearchEvidenceOrigin.UnresolvedEvidence,
            evidencePacket.Transport,
            evidencePacket.ModelVersion,
            evidencePacket.SettingsIdentity,
            evidencePacket.SearchProvider);
        var admittedCitationSet = presentationResult.Presentation.InlineCitations
            .Select(item => item.CitationIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var admittedCitations = evidencePacket.Citations
            .Where(item => admittedCitationSet.Contains(item.CitationIdentity))
            .ToArray();
        var presented = presentationResult.Presentation.PresentedText;

        if (!presentationResult.Succeeded)
        {
            return new LegendConnectResearchOutcome(
                LegendConnectResearchOutcomeState.Failure,
                LegendConnectResearchEvidenceOrigin.UnresolvedEvidence,
                request.Decision,
                session,
                null,
                null,
                null,
                new LegendConnectResearchFailureResult(
                    presentationResult.ReasonCode,
                    presented,
                    false,
                    "The existing LEGEND presentation authority rejected the research response because its exact claim/citation/passage or presentation binding was invalid."),
                provenance,
                presentationResult.Presentation);
        }

        if (effectiveAssessmentState == LegendResearchEvidenceAssessmentState.Conclusion)
        {
            var conclusion = new LegendConnectResearchConclusion(
                LegendLanguageIdentity.TextHash(
                    "research-conclusion|v1|" +
                    string.Join('|', assessment.Claims.Select(item => item.EvidenceIdentity))),
                presented,
                assessment.Claims,
                admittedCitations);
            return new LegendConnectResearchOutcome(
                LegendConnectResearchOutcomeState.Conclusion,
                evidenceOrigin,
                request.Decision,
                session,
                conclusion,
                null,
                null,
                null,
                provenance,
                presentationResult.Presentation);
        }

        if (effectiveAssessmentState == LegendResearchEvidenceAssessmentState.UnresolvedConflict)
        {
            return new LegendConnectResearchOutcome(
                LegendConnectResearchOutcomeState.UnresolvedConflict,
                LegendConnectResearchEvidenceOrigin.UnresolvedEvidence,
                request.Decision,
                session,
                null,
                null,
                new LegendConnectResearchUnresolvedConflictResult(
                    effectiveReasonCode,
                    presented,
                    assessment.Claims,
                    assessment.Contradictions,
                    admittedCitations),
                null,
                provenance,
                presentationResult.Presentation);
        }

        return new LegendConnectResearchOutcome(
            LegendConnectResearchOutcomeState.InsufficientEvidence,
            LegendConnectResearchEvidenceOrigin.UnresolvedEvidence,
            request.Decision,
            session,
            null,
            new LegendConnectResearchInsufficientEvidenceResult(
                assessment.ReasonCode,
                presented,
                assessment.Claims.Count,
                assessment.IndependentSourceCount,
                assessment.RequiredIndependentSourceCount,
                admittedCitations),
            null,
            null,
            provenance with
            {
                EvidenceOrigin = LegendConnectResearchEvidenceOrigin.UnresolvedEvidence
            },
            presentationResult.Presentation);
    }

    /// <summary>
    /// Adds bounded operational receipts to the existing diagnostics ledger.
    /// These rows contain sanitized observation metadata only and are never a
    /// source for retained retrieval, native inference, learning, or admission.
    /// </summary>
    public async Task RecordResearchObservabilityAsync(
        LegendConnectResearchOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (_operationalEvents is null)
            return;

        var session = outcome.Session;
        var correlation = session.SessionId.ToString("N");
        var language = session.LanguageLineage?.UserLanguageCode ??
            outcome.Decision.SourceLanguageCode;
        var failureCode = outcome.Failure?.ReasonCode ?? session.FailureReason;
        var externalObservation = outcome.RetentionLineage ??
            LegendConnectResearchRetentionContracts.CreateExternalObservation(outcome);
        var retention = outcome.Retention ?? new LegendConnectResearchRetentionReceipt(
            externalObservation is null
                ? LegendConnectResearchRetentionState.Failed
                : LegendConnectResearchRetentionState.ExternalObservation,
            LegendConnectResearchRetentionContracts.ObservationIdentity(outcome),
            null,
            null,
            externalObservation is null ? "NoRetention" : "ExternalObservation",
            "NonServing",
            "NonCanonical",
            externalObservation is null
                ? "research_retention_observation_ineligible"
                : null);

        await RecordResearchObservationFacetAsync(
            "Session",
            outcome.State.ToString(),
            language,
            correlation,
            failureCode,
            $"code_sha={Observe(outcome.Provenance.CodeSha, 40)};configuration={Observe(outcome.Provenance.ConfigurationIdentity, 64)};reason={Observe(outcome.Decision.ReasonCode, 80)};provider={Observe(outcome.Provenance.SearchProvider, 80)};authorization={Observe(outcome.Provenance.AuthorizationProvenance, 80)}",
            cancellationToken);

        await RecordResearchObservationFacetAsync(
            "Accounting",
            session.CostMicrounits.HasValue ? "Measured" : "Unavailable",
            language,
            correlation,
            failureCode,
            $"search_ms={session.SearchLatencyMilliseconds};retrieval_ms={session.RetrievalLatencyMilliseconds};reasoning_ms={session.ReasoningLatencyMilliseconds};total_ms={session.LatencyMilliseconds};search_cost_micro={Cost(session.SearchCostMicrounits)};model_cost_micro={Cost(session.ModelCostMicrounits)};total_cost_micro={Cost(session.CostMicrounits)}",
            cancellationToken);

        foreach (var query in session.Queries.Take(LegendConnectResearchContracts.MaximumQueries))
        {
            await RecordResearchObservationFacetAsync(
                "Query",
                outcome.State.ToString(),
                language,
                correlation,
                failureCode,
                $"ordinal={query.Ordinal};identity={Observe(query.QueryIdentity, 80)};language={Observe(query.QueryLanguageCode ?? query.SourceLanguageCode, 32)};query={Observe(query.Query, 240)}",
                cancellationToken);
        }

        var openedSources = session.Documents
            .Where(item => item.RetrievalSucceeded)
            .Select(item => item.SourceIdentity)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var source in session.Sources.Take(LegendConnectResearchContracts.MaximumResults))
        {
            var lineages = session.MaterialClaimEvidence?
                .Where(item => string.Equals(item.SourceIdentity, source.SourceIdentity, StringComparison.Ordinal))
                .Select(item => item.IndependentSourceLineage)
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToArray() ?? [];
            await RecordResearchObservationFacetAsync(
                "Source",
                openedSources.Contains(source.SourceIdentity) ? "Opened" : "Discovered",
                language,
                correlation,
                null,
                $"source={Observe(source.SourceIdentity, 80)};class={source.SourceClass};opened={openedSources.Contains(source.SourceIdentity).ToString().ToLowerInvariant()};independence={Observe(string.Join(',', lineages), 160)};uri={ObservePublicUri(source.CanonicalUri)}",
                cancellationToken);
        }

        foreach (var claim in (session.MaterialClaimEvidence ?? [])
                     .Take(LegendConnectResearchContracts.MaximumClaims))
        {
            await RecordResearchObservationFacetAsync(
                "Claim",
                claim.Relationship == LegendConnectResearchEvidenceRelationship.Contradiction
                    ? "Contradicted"
                    : "Supported",
                language,
                correlation,
                null,
                $"claim={Observe(claim.NormalizedClaimIdentity, 80)};evidence={Observe(claim.EvidenceIdentity, 80)};relationship={claim.Relationship};verification={claim.VerificationState};lineage={Observe(claim.IndependentSourceLineage, 120)}",
                cancellationToken);
        }

        foreach (var resolution in (session.ClaimResolutions ?? [])
                     .Where(item => item.State is
                         LegendConnectResearchClaimVerificationState.Disputed or
                         LegendConnectResearchClaimVerificationState.UnresolvedConflict)
                     .Take(LegendConnectResearchContracts.MaximumClaims))
        {
            await RecordResearchObservationFacetAsync(
                "Conflict",
                resolution.State.ToString(),
                language,
                correlation,
                resolution.ReasonCode,
                $"claim={Observe(resolution.NormalizedClaimIdentity, 80)};reason={Observe(resolution.ReasonCode, 80)};requires_discriminating={resolution.RequiresDiscriminatingEvidence.ToString().ToLowerInvariant()}",
                cancellationToken);
        }

        var inlineCitations = outcome.Presentation?.InlineCitations ?? [];
        foreach (var citation in inlineCitations.Take(LegendConnectResearchContracts.MaximumClaims))
        {
            await RecordResearchObservationFacetAsync(
                "Citation",
                "Used",
                language,
                correlation,
                null,
                $"ordinal={citation.Ordinal};citation={Observe(citation.CitationIdentity, 80)};claim={Observe(citation.NormalizedClaimIdentity, 80)};source={Observe(citation.SourceIdentity, 80)}",
                cancellationToken);
        }

        await RecordResearchObservationFacetAsync(
            "Retention",
            retention.State.ToString(),
            language,
            correlation,
            retention.FailureCode,
            $"observation={Observe(retention.ObservationIdentity, 80)};candidate={retention.CandidateId?.ToString("N") ?? "none"};proposal={retention.ProposalId?.ToString("N") ?? "none"};provenance={Observe(retention.Provenance, 80)};serving={retention.ServingStatus};canonical={retention.CanonicalStatus}",
            cancellationToken);
    }

    public Task RecordResearchRetentionAsync(
        LegendConnectResearchRetentionLineage lineage,
        LegendConnectMachineTeachingSubmissionResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(result);
        if (_operationalEvents is null)
            return Task.CompletedTask;

        var accepted = result.Succeeded && !result.ProposalAlreadyExisted;
        var state = accepted
            ? LegendConnectResearchRetentionState.MachineProposed.ToString()
            : LegendConnectResearchRetentionState.Failed.ToString();
        return RecordResearchObservationFacetAsync(
            "Retention",
            state,
            lineage.MaterialClaims.FirstOrDefault()?.TranslationLineage.FinalResponseLanguageCode ?? "und",
            lineage.SessionId.ToString("N"),
            result.ProposalAlreadyExisted
                ? "machine_learning_mutation_replay"
                : result.ErrorCode,
            $"observation={Observe(lineage.ObservationIdentity, 80)};candidate={result.CorpusCandidateId?.ToString("N") ?? "none"};proposal={result.ProposalId?.ToString("N") ?? "none"};provenance={(accepted ? "MachineProposed" : "ExternalObservation")};research_authorization={Observe(lineage.ResearchAuthorizationProvenance, 80)};serving=NonServing;canonical=NonCanonical",
            cancellationToken);
    }

    private Task RecordResearchObservationFacetAsync(
        string facet,
        string status,
        string language,
        string correlation,
        string? failureCode,
        string summary,
        CancellationToken cancellationToken) =>
        _operationalEvents!.TryRecordAsync(
            LegendConnectResearchContracts.ObservabilityCategory,
            failureCode is null ? "Info" : "Warning",
            facet + ":" + status,
            language,
            errorCode: failureCode,
            correlationId: correlation,
            summary: SanitizeResearchObservationSummary(summary),
            isResolved: failureCode is null,
            cancellationToken: cancellationToken);

    private static string Cost(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";

    private static string Observe(string? value, int maximumLength)
    {
        var normalized = (value ?? "Unavailable")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace(';', ',')
            .Replace('=', ':')
            .Trim();
        normalized = new string(normalized
            .Where(character => !char.IsControl(character))
            .ToArray());
        if (LegendConnectResearchExternalDataPolicy.IsPotentialInstruction(normalized))
            return "withheld_untrusted_instruction_like_content";
        return normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private static string ObservePublicUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return "withheld_invalid_public_uri";
        }
        var display = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        }.Uri.AbsoluteUri;
        return Observe(display, 180);
    }

    public Task<LegendConnectNativeInferenceSnapshot>
        TryInferConversationWithDiscourseAsync(
            string input,
            IReadOnlyList<LegendConnectConversationContextItem> context,
            LegendConnectDiscourseStateSnapshot? discourseState,
            CancellationToken cancellationToken = default,
            string sourceLanguageCode = "en") =>
        TryInferConversationCoreAsync(
            input,
            context,
            discourseState,
            readOnlyContentReceipt: null,
            cancellationToken: cancellationToken,
            sourceLanguageCode: sourceLanguageCode);

    public Task<LegendConnectNativeInferenceSnapshot>
        TryInferConversationWithReadOnlyContentAsync(
            string input,
            IReadOnlyList<LegendConnectConversationContextItem> context,
            LegendConnectDiscourseStateSnapshot? discourseState,
            LegendConnectReadOnlyContentBindingReceipt receipt,
            CancellationToken cancellationToken = default,
            string sourceLanguageCode = "en") =>
        TryInferConversationCoreAsync(
            input,
            context,
            discourseState,
            receipt,
            cancellationToken,
            sourceLanguageCode);

    private async Task<LegendConnectNativeInferenceSnapshot>
        TryInferConversationCoreAsync(
            string input,
            IReadOnlyList<LegendConnectConversationContextItem> context,
            LegendConnectDiscourseStateSnapshot? discourseState,
            LegendConnectReadOnlyContentBindingReceipt? readOnlyContentReceipt,
            CancellationToken cancellationToken,
            string sourceLanguageCode)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LegendConnectNativeInferenceSnapshot Finish(
            LegendConnectNativeInferenceSnapshot inference) =>
            WithResearchDecision(
                input ?? string.Empty,
                sourceLanguageCode,
                inference,
                discourseState);
        if (string.IsNullOrWhiteSpace(LegendLanguageIdentity.NormalizeText(input ?? string.Empty)))
            return Finish(NativeInferenceUnsupported("invalid_input"));

        var composed = await Curriculum.TryInferComposedSemanticTransitionAsync(
            sourceLanguageCode,
            input ?? string.Empty,
            context,
            discourseState,
            cancellationToken,
            readOnlyContentReceipt);
        if (string.Equals(
                composed.State,
                LegendSemanticTransitionInference.ReadOnlyContentRequired,
                StringComparison.Ordinal) &&
            composed.ReadOnlyContentRequest is not null)
        {
            return Finish(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "read_only_content_binding_required",
                composed.EvidenceCount,
                "LEGEND selected a governed result frame that explicitly requires one Founder-authorized read-only content binding.",
                false,
                "Unavailable",
                "Unavailable",
                composed.ReadOnlyContentRequest,
                ModelAssistance: DormantModelAssistance(
                    "read_only_content_authorization_pending")));
        }
        if (string.Equals(composed.State, LegendSemanticTransitionInference.Supported, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(composed.RealizedText))
        {
            var evidenceStandard = composed.Reasons.Contains(
                "higher_standard_semantic_transition",
                StringComparer.Ordinal)
                    ? "HigherStandard"
                    : "BroadGoverned";
            var articulationMode = composed.Reasons.Contains(
                "original_compositional_anchor_realization",
                StringComparer.Ordinal)
                    ? "OriginalComposition"
                    : "CanonicalGovernedEndpoint";
            var symbolic = new LegendConnectNativeInferenceSnapshot(
                true,
                0m,
                composed.RealizedText,
                "semantic_transition_governed_composed",
                composed.EvidenceCount,
                composed.ContentBindingProvenance is { Count: > 0 }
                    ? $"LEGEND composed governed meaning, selected {evidenceStandard} evidence, bound current content through the Founder-authorized read-only tool authority, and preserved its receipt provenance during {articulationMode}."
                    : articulationMode == "OriginalComposition"
                        ? $"LEGEND composed governed meaning, selected {evidenceStandard} evidence, and articulated original wording from governed semantic anchors."
                        : $"LEGEND composed governed meaning, selected {evidenceStandard} evidence, and articulated the canonical endpoint authorized by that same transition.",
                false,
                evidenceStandard,
                articulationMode,
                null,
                composed.ContentBindingProvenance,
                PresentationConstraints: composed.PresentationConstraints);

            var served = await TryApplyPromotedReasoningModelAsync(
                input ?? string.Empty,
                sourceLanguageCode,
                symbolic,
                cancellationToken);
            return Finish(served);
        }

        // Existing explicit frame evidence remains governed by the same
        // selector and realization authority. Retain it only when the newer
        // compositional graph cannot establish a source meaning; a composed
        // contradiction/ambiguity must fail closed rather than be masked.
        if (composed.State is LegendSemanticTransitionInference.Ambiguous or
            LegendSemanticTransitionInference.Contradicted)
        {
            return Finish(NativeInferenceUnsupported(composed.Reasons.FirstOrDefault() ?? "semantic_transition_not_governed"));
        }
        var reasonCode =
            composed.Reasons.FirstOrDefault() ??
            "reusable_meaning_graph_not_governed";

        // V20.3: once reusable source meaning has been established, a
        // contradiction, unresolved discourse binding, unavailable governed
        // fact, ambiguous governed content, or canonical-realization failure
        // is a semantic fail-closed boundary. An external model may not
        // manufacture an answer across that boundary.
        //
        // External escalation remains available when reusable source meaning
        // or its required governed transition is missing. Ambiguous,
        // contradicted, unresolved-content, and unrealizable governed states
        // remain fail-closed.
        if (CanEscalateFromUnavailableComposedSource(composed))
        {
            var modelResponse = await TryApplyPromotedReasoningModelDirectlyAsync(
                input ?? string.Empty,
                context,
                sourceLanguageCode,
                cancellationToken);
            if (modelResponse is not null)
                return Finish(modelResponse);
        }

        return Finish(NativeInferenceUnsupported(
            reasonCode,
            CanEscalateFromUnavailableComposedSource(composed)));
    }

    private async Task<LegendConnectNativeInferenceSnapshot?>
        TryApplyPromotedReasoningModelDirectlyAsync(
            string founderInput,
            IReadOnlyList<LegendConnectConversationContextItem> context,
            string sourceLanguageCode,
            CancellationToken cancellationToken)
    {
        if (_activeModelInference is null)
            return null;

        var governedSourceLanguage =
            await _registry.NormalizeEnabledTranslationLanguageAsync(
                sourceLanguageCode,
                cancellationToken);
        if (governedSourceLanguage is null)
            return null;

        var generated = await _activeModelInference
            .TryGenerateGovernedReasoningCandidateAsync(
                new LegendConnectGovernedReasoningCandidateRequest(
                    governedSourceLanguage,
                    founderInput,
                    AuthorizedSymbolicText: null,
                    EvidenceCount: 0,
                    EvidenceStandard: "EvaluatedPromotedModel",
                    ArticulationMode: "EvaluatedPromotedModelResponse",
                    ConversationContext: context),
                cancellationToken);

        if (!generated.Succeeded || string.IsNullOrWhiteSpace(generated.Text))
            return null;

        return new LegendConnectNativeInferenceSnapshot(
            true,
            0m,
            generated.Text,
            "active_reasoning_model_governed",
            0,
            "The exact evaluated and promoted LEGEND reasoning model answered the general reasoning request without claiming canonical data, tool execution, or learning authority.",
            false,
            "EvaluatedPromotedModel",
            "EvaluatedPromotedModelResponse",
            ModelAssistance: new LegendConnectNativeModelAssistanceSnapshot(
                "Applied",
                "active_reasoning_model_response_governed",
                LegendConnectNativeModelAssistanceContracts.GovernedReasoningCapability,
                generated.ModelVersion,
                generated.ModelTrainingRunId,
                LegendConnectNativeModelAssistanceContracts.ResponseProvenance,
                generated.CostMicrounits));
    }

    private async Task<LegendConnectNativeInferenceSnapshot>
        TryApplyPromotedReasoningModelAsync(
            string founderInput,
            string sourceLanguageCode,
            LegendConnectNativeInferenceSnapshot symbolic,
            CancellationToken cancellationToken)
    {
        if (!symbolic.Supported ||
            string.IsNullOrWhiteSpace(symbolic.Answer))
        {
            return symbolic with
            {
                ModelAssistance = DormantModelAssistance(
                    "symbolic_authority_not_supported")
            };
        }

        if (symbolic.RequiresEscalation ||
            symbolic.EvidenceCount <= 0 ||
            symbolic.EvidenceStandard is not ("HigherStandard" or "BroadGoverned") ||
            !string.Equals(
                symbolic.ReasonCode,
                "semantic_transition_governed_composed",
                StringComparison.Ordinal))
        {
            return symbolic with
            {
                ModelAssistance = DormantModelAssistance(
                    "active_reasoning_model_symbolic_evidence_not_authorized")
            };
        }

        if (symbolic.ContentBindingProvenance is { Count: > 0 })
        {
            return symbolic with
            {
                ModelAssistance = DormantModelAssistance(
                    "active_reasoning_model_read_only_content_not_authorized")
            };
        }

        if (_activeModelInference is null)
        {
            return symbolic with
            {
                ModelAssistance = UnavailableModelAssistance(
                    "active_reasoning_model_authority_unavailable")
            };
        }

        var governedSourceLanguage =
            await _registry.NormalizeEnabledTranslationLanguageAsync(
                sourceLanguageCode,
                cancellationToken);
        if (governedSourceLanguage is null)
        {
            return symbolic with
            {
                ModelAssistance = DormantModelAssistance(
                    "active_reasoning_model_source_language_not_governed")
            };
        }

        var generated =
            await _activeModelInference
                .TryGenerateGovernedReasoningCandidateAsync(
                    new LegendConnectGovernedReasoningCandidateRequest(
                        governedSourceLanguage,
                        founderInput,
                        symbolic.Answer,
                        symbolic.EvidenceCount,
                        symbolic.EvidenceStandard,
                        symbolic.ArticulationMode),
                    cancellationToken);

        var generatedText = generated.Text;
        if (!generated.Succeeded ||
            string.IsNullOrWhiteSpace(generatedText))
        {
            var unavailable = string.Equals(
                generated.ErrorCode,
                "active_reasoning_model_unavailable",
                StringComparison.Ordinal);
            var rejected = string.Equals(
                generated.ErrorCode,
                "active_reasoning_model_malformed_output",
                StringComparison.Ordinal);
            return symbolic with
            {
                ModelAssistance = new LegendConnectNativeModelAssistanceSnapshot(
                    unavailable
                        ? "Unavailable"
                        : rejected
                            ? "Rejected"
                            : "Failed",
                    generated.ErrorCode ??
                        "active_reasoning_model_inference_failed",
                    LegendConnectNativeModelAssistanceContracts
                        .GovernedReasoningCapability,
                    generated.ModelVersion,
                    generated.ModelTrainingRunId,
                    LegendConnectNativeModelAssistanceContracts
                        .CandidateAttemptProvenance,
                    generated.CostMicrounits)
            };
        }

        if (!await Curriculum.IsGovernedEquivalentRealizationAsync(
                governedSourceLanguage,
                symbolic.Answer,
                generatedText,
                cancellationToken))
        {
            return symbolic with
            {
                ModelAssistance = new LegendConnectNativeModelAssistanceSnapshot(
                    "Rejected",
                    "active_reasoning_model_semantic_lineage_unproven",
                    LegendConnectNativeModelAssistanceContracts
                        .GovernedReasoningCapability,
                    generated.ModelVersion,
                    generated.ModelTrainingRunId,
                    LegendConnectNativeModelAssistanceContracts
                        .CandidateAttemptProvenance,
                    generated.CostMicrounits)
            };
        }

        return symbolic with
        {
            Answer = generated.Text,
            ArticulationMode = "EvaluatedPromotedModelRealization",
            AuthoritySummary =
                symbolic.AuthoritySummary +
                " An evaluated and promoted LEGEND reasoning model proposed only the surface wording; the existing governed meaning-graph authority proved identical semantic lineage before serving, while symbolic evidence and contradiction decisions remained unchanged.",
            ModelAssistance = new LegendConnectNativeModelAssistanceSnapshot(
                "Applied",
                "active_reasoning_model_candidate_governed",
                LegendConnectNativeModelAssistanceContracts
                    .GovernedReasoningCapability,
                generated.ModelVersion,
                generated.ModelTrainingRunId,
                LegendConnectNativeModelAssistanceContracts.Provenance,
                generated.CostMicrounits)
        };
    }

    private static LegendConnectNativeModelAssistanceSnapshot
        DormantModelAssistance(
            string reasonCode) =>
        new(
            "Dormant",
            reasonCode,
            LegendConnectNativeModelAssistanceContracts
                .GovernedReasoningCapability,
            null,
            null,
            null);

    private static LegendConnectNativeModelAssistanceSnapshot
        UnavailableModelAssistance(
            string reasonCode) =>
        new(
            "Unavailable",
            reasonCode,
            LegendConnectNativeModelAssistanceContracts
                .GovernedReasoningCapability,
            null,
            null,
            null);

    private static bool CanEscalateFromUnavailableComposedSource(
        LegendSemanticTransitionInference inference) =>
        inference.Reasons.FirstOrDefault() is
            "meaning_graph_input_invalid" or
            "meaning_graph_component_unknown" or
            "meaning_graph_retrieval_bound_exceeded" or
            "meaning_graph_relation_unproven" or
            "semantic_transition_not_supported" or
            "semantic_transition_evidence_unknown";

    private static LegendConnectNativeInferenceSnapshot WithResearchDecision(
        string input,
        string sourceLanguageCode,
        LegendConnectNativeInferenceSnapshot inference,
        LegendConnectDiscourseStateSnapshot? discourseState = null) =>
        inference with
        {
            ResearchDecision = DecideResearchNeeded(
                input,
                sourceLanguageCode,
                inference,
                DateTime.UtcNow,
                discourseState: discourseState)
        };

    private static bool HasCurrentTurnDiscourseAuthority(
        LegendConnectDiscourseStateSnapshot? discourseState)
    {
        var currentTurn = discourseState?.Turns.LastOrDefault();
        return currentTurn is { Role: "user", Bindings.Count: > 0 } &&
            currentTurn.Bindings.Any(binding =>
                binding.ResolutionState is "bound" or "unresolved" &&
                !string.IsNullOrWhiteSpace(binding.SelectorSemanticSignature));
    }

    private static LegendConnectResearchAccessClass ClassifyResearchAccess(
        string normalized)
    {
        if (ContainsResearchSignal(
                normalized,
                "post this", "submit this", "send this", "delete this",
                "change this", "purchase this", "buy this", "book this",
                "and execute", "and perform the action"))
        {
            return LegendConnectResearchAccessClass.MutationCapable;
        }

        if (ContainsResearchSignal(
                normalized,
                "private source", "private document", "private account",
                "confidential", "non-public", "nonpublic"))
        {
            return LegendConnectResearchAccessClass.PrivateReadOnly;
        }

        if (ContainsResearchSignal(
                normalized,
                "sign in", "signed in", "log in", "logged in",
                "authenticated", "behind a login", "paywall", "my account"))
        {
            return LegendConnectResearchAccessClass.AuthenticatedReadOnly;
        }

        if (ContainsResearchSignal(
                normalized,
                "restricted source", "restricted research", "classified source"))
        {
            return LegendConnectResearchAccessClass.RestrictedReadOnly;
        }

        if (ContainsResearchSignal(
                normalized,
                "medical advice", "legal advice", "investment advice",
                "financial advice", "security vulnerability", "personal data",
                "personally identifiable", "health diagnosis"))
        {
            return LegendConnectResearchAccessClass.SensitiveReadOnly;
        }

        return LegendConnectResearchAccessClass.PublicReadOnly;
    }

    private static bool IsInternalLegendSystemQuestion(string normalized) =>
        ContainsResearchSignal(normalized, "legend", "our system", "our database", "our model") &&
        ContainsResearchSignal(
            normalized,
            "system state", "database", "readiness", "training state",
            "model state", "model version", "provider capacity", "coverage",
            "retained knowledge", "currently know", "current knowledge");

    private static bool IsExternalFactualQuestion(string normalized)
    {
        if (ContainsResearchSignal(
                normalized,
                "how are you", "can you help", "hello", "hi legend",
                "thanks", "thank you", "write ", "rewrite ", "brainstorm "))
        {
            return false;
        }

        return new[]
        {
            "who ", "what ", "when ", "where ", "which ", "how many ",
            "how much ", "is there ", "are there ", "does ", "do we know "
        }.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsConversationInternalQuestion(string normalized) =>
        ContainsResearchSignal(
            normalized,
            "did i say", "did i mean", "i said", "i meant",
            "you said", "you meant", "we said", "we discussed",
            "earlier in this conversation", "previous message",
            "previous answer", "above", "first option", "second option",
            "that answer", "those two");

    private static string? TryIdentifyNamedExternalSource(string question)
    {
        var normalized = question.ToLowerInvariant();
        if (!ContainsResearchSignal(
                normalized,
                "https://", "http://", "according to ", " rfc ", "rfc ",
                " iso ", "iso ", ".pdf", "the paper ", "the study ",
                "official documentation", "official document"))
        {
            return null;
        }

        return question.Length <= 240
            ? question
            : question[..240];
    }

    private static bool ContainsResearchSignal(
        string value,
        params string[] candidates) =>
        candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.Ordinal));

    internal static bool TryValidateResearchRequest(
        LegendConnectResearchRequest request,
        out string failureReason)
    {
        failureReason = "research_request_governed";
        if (!request.Decision.ResearchRequired ||
            request.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Question) ||
            request.Question.Length > LegendConnectResearchContracts.MaximumQueryCharacters ||
            request.Queries.Count is < 1 or > LegendConnectResearchContracts.MaximumQueries ||
            request.MaximumResults is < 1 or > LegendConnectResearchContracts.MaximumResults ||
            request.MaximumDocuments is < 1 or > LegendConnectResearchContracts.MaximumDocuments ||
            request.MaximumClaims is < 1 or > LegendConnectResearchContracts.MaximumClaims ||
            request.MaximumDocumentCharacters is < 1 or > LegendConnectResearchContracts.MaximumDocumentCharacters ||
            request.MinimumIndependentSources is < 1 or > 3 ||
            !request.Authorization.FounderAuthorized ||
            !request.Authorization.IsReadOnly ||
            !request.Authorization.ZeroWrite ||
            request.Authorization.AccessClass != request.Decision.AccessClass ||
            !LegendConnectCurriculumService.AreGovernedPresentationConstraintsValid(
                request.PresentationConstraints))
        {
            failureReason = "research_request_invalid";
            return false;
        }

        if (request.Queries.Select(item => item.QueryIdentity)
                .Distinct(StringComparer.Ordinal).Count() != request.Queries.Count ||
            request.Queries.Any(item =>
                string.IsNullOrWhiteSpace(item.QueryIdentity) ||
                string.IsNullOrWhiteSpace(item.Query) ||
                item.Query.Length > LegendConnectResearchContracts.MaximumQueryCharacters ||
                !LegendConnectResearchExternalDataPolicy.IsSafePublicSearchQuery(item.Query) ||
                item.MaximumResults is < 1 or > LegendConnectResearchContracts.MaximumResults ||
                !string.Equals(
                    item.SourceLanguageCode,
                    request.Decision.SourceLanguageCode,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    item.QueryLanguageCode ?? item.SourceLanguageCode,
                    request.Decision.SourceLanguageCode,
                    StringComparison.OrdinalIgnoreCase)))
        {
            failureReason = "research_query_bounds_invalid";
            return false;
        }

        if (request.Decision.AccessClass != LegendConnectResearchAccessClass.PublicReadOnly &&
            (!string.Equals(
                 request.Authorization.AuthorizationProvenance,
                 LegendConnectResearchContracts.RestrictedAuthorizationProvenance,
                 StringComparison.Ordinal) ||
             !Guid.TryParseExact(
                 request.Authorization.AuthorizationCorrelationId,
                 "N",
                 out _)))
        {
            failureReason = "research_restricted_authorization_required";
            return false;
        }

        if (request.Decision.AccessClass == LegendConnectResearchAccessClass.PublicReadOnly &&
            request.Authorization.AuthorizationProvenance is not
                LegendConnectResearchContracts.PublicAuthorizationProvenance and not
                LegendConnectResearchContracts.LockedEvaluationAuthorizationProvenance)
        {
            failureReason = "research_public_authorization_invalid";
            return false;
        }

        return true;
    }

    internal static bool HasCompleteResearchSearchLineage(
        LegendConnectResearchRequest request,
        LegendConnectResearchSearchTransportResult result)
    {
        if (!result.Succeeded ||
            string.IsNullOrWhiteSpace(result.Transport) ||
            string.IsNullOrWhiteSpace(result.Provider) ||
            string.IsNullOrWhiteSpace(result.SettingsIdentity) ||
            result.LatencyMilliseconds < 0 ||
            result.ExecutedQueries.Count is < 1 or > LegendConnectResearchContracts.MaximumQueries ||
            result.QueryReceipts.Count != result.ExecutedQueries.Count ||
            result.SearchResults.Count is < 1 ||
            result.SearchResults.Count > request.MaximumResults ||
            result.Sources.Count is < 1 ||
            result.Sources.Count > request.MaximumResults ||
            result.ClaimCandidates.Count > request.MaximumClaims ||
            result.ContradictionCandidates.Count > request.MaximumClaims)
            return false;

        var queryIds = result.ExecutedQueries
            .Select(item => item.QueryIdentity)
            .ToHashSet(StringComparer.Ordinal);
        if (queryIds.Count != result.ExecutedQueries.Count ||
            result.ExecutedQueries.Any(item =>
                string.IsNullOrWhiteSpace(item.QueryIdentity) ||
                !LegendConnectResearchExternalDataPolicy.IsSafePublicSearchQuery(item.Query) ||
                !string.Equals(
                    item.QueryLanguageCode ?? item.SourceLanguageCode,
                    request.Decision.SourceLanguageCode,
                    StringComparison.OrdinalIgnoreCase)) ||
            result.QueryReceipts.Select(item => item.ReceiptIdentity)
                .Distinct(StringComparer.Ordinal).Count() != result.QueryReceipts.Count ||
            result.QueryReceipts.Any(item =>
                !queryIds.Contains(item.QueryIdentity) ||
                !string.Equals(item.Provider, result.Provider, StringComparison.Ordinal) ||
                !string.Equals(
                    item.QueryLanguageCode,
                    request.Decision.SourceLanguageCode,
                    StringComparison.OrdinalIgnoreCase) ||
                item.LatencyMilliseconds < 0 ||
                string.IsNullOrWhiteSpace(item.CostState) ||
                !item.IsReadOnly ||
                !item.ZeroWrite ||
                !item.Succeeded ||
                item.FailureReason is not null))
            return false;

        var sourceIds = result.Sources.Select(item => item.SourceIdentity).ToArray();
        var sourceUris = result.Sources.Select(item => item.CanonicalUri)
            .ToHashSet(StringComparer.Ordinal);
        if (sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Length ||
            sourceUris.Count != result.Sources.Count ||
            result.Sources.Any(item =>
                string.IsNullOrWhiteSpace(item.SourceIdentity) ||
                LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(item.CanonicalUri) is null ||
                !item.IsUntrustedExternalData))
            return false;
        var sourceSet = sourceIds.ToHashSet(StringComparer.Ordinal);
        if (result.SearchResults.Select(item => item.SearchResultIdentity)
                .Distinct(StringComparer.Ordinal).Count() != result.SearchResults.Count ||
            result.SearchResults.Any(item =>
                !queryIds.Contains(item.QueryIdentity) ||
                !sourceSet.Contains(item.SourceIdentity) ||
                !sourceUris.Contains(item.CanonicalUri) ||
                item.Rank < 1 ||
                !item.IsUntrustedExternalData))
            return false;

        return result.ClaimCandidates
            .Concat(result.ContradictionCandidates)
            .All(item =>
                item.IsUntrustedExternalData &&
                !string.IsNullOrWhiteSpace(item.ClaimIdentity) &&
                !string.IsNullOrWhiteSpace(item.Statement) &&
                !LegendConnectResearchExternalDataPolicy.IsPotentialInstruction(item.Statement) &&
                string.Equals(
                    item.EvidenceLanguageCode,
                    request.Decision.SourceLanguageCode,
                    StringComparison.OrdinalIgnoreCase) &&
                item.CanonicalUris.Count is >= 1 and <= LegendConnectResearchContracts.MaximumResults &&
                item.CanonicalUris.All(sourceUris.Contains));
    }

    private static LegendConnectResearchEvidencePacket BuildResearchEvidencePacket(
        LegendConnectResearchRequest request,
        LegendConnectResearchSearchTransportResult search,
        LegendConnectResearchPageRetrievalResult pages,
        long latencyMilliseconds)
    {
        var artifactsByUri = pages.Lineage
            .SelectMany(item => new[]
            {
                new { Uri = item.RequestedCanonicalUri, Lineage = item },
                new { Uri = item.FinalCanonicalUri, Lineage = item }
            })
            .GroupBy(item => item.Uri, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Lineage, StringComparer.Ordinal);

        var claims = new List<LegendConnectClaimEvidence>();
        foreach (var candidate in search.ClaimCandidates)
        {
            foreach (var uri in candidate.CanonicalUris)
            {
                if (!artifactsByUri.TryGetValue(uri, out var artifact))
                    continue;
                claims.Add(new LegendConnectClaimEvidence(
                    LegendLanguageIdentity.TextHash(
                        "research-evidence|v2|" + candidate.ClaimIdentity + "|" +
                        artifact.SourceIdentity + "|" + candidate.Statement),
                    candidate.ClaimIdentity,
                    candidate.Statement,
                    artifact.SourceIdentity,
                    artifact.DocumentIdentity,
                    artifact.CitationIdentity,
                    candidate.ObservedUtc,
                    candidate.Subject,
                    candidate.StatementKind,
                    candidate.Support,
                    candidate.RequiredAuthorityScope,
                    candidate.AsOfUtc,
                    candidate.SupportingExcerpt,
                    candidate.EvidenceLanguageCode,
                    LegendConnectResearchExtractionMethod.ModelAssistedProposal,
                    candidate.PremiseClaimIdentities,
                    candidate.DiscriminatingClaimIdentity,
                    candidate.CorrectsCanonicalUri is not null &&
                    artifactsByUri.TryGetValue(candidate.CorrectsCanonicalUri, out var correctedArtifact)
                        ? correctedArtifact.SourceIdentity
                        : null));
                if (claims.Count >= request.MaximumClaims)
                    break;
            }
            if (claims.Count >= request.MaximumClaims)
                break;
        }

        var contradictions = new List<LegendConnectContradictingEvidence>();
        foreach (var candidate in search.ContradictionCandidates)
        {
            foreach (var uri in candidate.CanonicalUris)
            {
                if (!artifactsByUri.TryGetValue(uri, out var artifact))
                    continue;
                contradictions.Add(new LegendConnectContradictingEvidence(
                    LegendLanguageIdentity.TextHash(
                        "research-contradiction|v2|" + candidate.ClaimIdentity + "|" +
                        artifact.SourceIdentity + "|" + candidate.Statement),
                    candidate.ClaimIdentity,
                    candidate.Statement,
                    artifact.SourceIdentity,
                    artifact.DocumentIdentity,
                    artifact.CitationIdentity,
                    candidate.ObservedUtc,
                    candidate.Subject,
                    candidate.StatementKind,
                    candidate.Support,
                    candidate.RequiredAuthorityScope,
                    candidate.AsOfUtc,
                    candidate.SupportingExcerpt,
                    candidate.EvidenceLanguageCode,
                    LegendConnectResearchExtractionMethod.ModelAssistedProposal,
                    candidate.PremiseClaimIdentities,
                    candidate.DiscriminatingClaimIdentity,
                    candidate.CorrectsCanonicalUri is not null &&
                    artifactsByUri.TryGetValue(candidate.CorrectsCanonicalUri, out var correctedArtifact)
                        ? correctedArtifact.SourceIdentity
                        : null));
                if (contradictions.Count >= request.MaximumClaims)
                    break;
            }
            if (contradictions.Count >= request.MaximumClaims)
                break;
        }

        var userLanguage = request.Decision.SourceLanguageCode;
        var documentLanguages = pages.Documents
            .Select(item => item.DocumentLanguageCode)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var translationReceipts = pages.Documents
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.DocumentLanguageCode) &&
                !string.Equals(item.DocumentLanguageCode, userLanguage, StringComparison.OrdinalIgnoreCase))
            .Select(item =>
            {
                var outputIdentity = LegendLanguageIdentity.TextHash(string.Join(
                    '|',
                    claims.Where(claim => claim.DocumentIdentity == item.DocumentIdentity)
                        .Select(claim => claim.Statement)
                        .Concat(contradictions
                            .Where(contradiction => contradiction.DocumentIdentity == item.DocumentIdentity)
                            .Select(contradiction => contradiction.Statement))));
                return new LegendConnectResearchTranslationReceipt(
                    LegendLanguageIdentity.TextHash(
                        "research-translation-receipt|v1|" + item.DocumentIdentity + "|" + userLanguage),
                    item.DocumentLanguageCode!,
                    userLanguage,
                    search.Transport,
                    item.ContentHash,
                    outputIdentity,
                    item.RetrievedUtc,
                    "EvidenceExtractionLanguageDeclared");
            })
            .ToArray();
        var languageLineag…39184 tokens truncated…ult = result.Succeeded ? "Succeeded" : result.ErrorCode ?? "Rejected",
            LanguageCode = result.SourceLanguageCode ?? string.Empty,
            PairKey = result.PairKey,
            AlignmentId = alignmentId,
            Detail = Bound(result.Message, 500),
            OccurredUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task WriteTargetRealizationReviewAuditAsync(
        string founderUserId,
        string action,
        LegendTargetRealizationReviewActionResult result,
        CancellationToken cancellationToken)
    {
        _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
        {
            Id = Guid.NewGuid(),
            FounderUserId = founderUserId,
            Action = action,
            Result = result.Succeeded ? "Succeeded" : result.ErrorCode ?? "Rejected",
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
        var structuralInternal = demand?.StructuralInternalServeCount ?? 0;
        var promotedTranslationModelServed = demand?.NeuralModelServeCount ?? 0;
        var promotedTranslationModelFailed = demand?.NeuralModelFailureCount ?? 0;
        var providerObservationReused = demand?.ProviderObservationReuseCount ?? 0;
        var nativeTranslationIntelligenceServed =
            memoryHits +
            structuralInternal +
            contextualInternal +
            promotedTranslationModelServed;
        var providerAvoidedRequests =
            nativeTranslationIntelligenceServed +
            providerObservationReused;
        var reconciledTerminalRoutes = providerAvoidedRequests + fallback;
        var routingReconciliationGap = total - reconciledTerminalRoutes;
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
            total == 0 ? 0m : Math.Round((decimal)providerAvoidedRequests / total, 4),
            total == 0 ? 0m : Math.Round((decimal)fallback / total, 4),
            total == 0 ? 0m : Math.Round((decimal)nativeTranslationIntelligenceServed / total, 4),
            internalQuality,
            coverageAdditions,
            approvedBacklog,
            lastProviderAcquisition,
            structuralInternal,
            promotedTranslationModelServed,
            promotedTranslationModelFailed,
            providerObservationReused,
            nativeTranslationIntelligenceServed,
            reconciledTerminalRoutes,
            routingReconciliationGap);
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

    private const int FounderSectionPageSize = 50;

    private async Task<string> ResolveFounderLanguageCodeAsync(
        string? languageCode,
        CancellationToken cancellationToken)
    {
        var languages = await _db.Set<LegendLanguageDefinition>().AsNoTracking()
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.CanonicalName)
            .Select(item => new LegendConnectFounderLanguageOptionSnapshot(
                item.LanguageCode,
                item.CanonicalName,
                item.IsEnabled))
            .ToListAsync(cancellationToken);
        return NormalizeFounderLanguageCode(languageCode, languages)
            ?? throw new ArgumentException("An enabled Legend language is required.", nameof(languageCode));
    }

    private static string? NormalizeFounderLanguageCode(
        string? languageCode,
        IReadOnlyList<LegendConnectFounderLanguageOptionSnapshot> languages)
    {
        var requested = languageCode?.Trim();
        var selected = languages.FirstOrDefault(item =>
            string.Equals(item.LanguageCode, requested, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
            return selected.LanguageCode;
        return languages.FirstOrDefault(item =>
            string.Equals(item.LanguageCode, "en", StringComparison.OrdinalIgnoreCase))?.LanguageCode
            ?? languages.FirstOrDefault()?.LanguageCode;
    }

    /// <summary>
    /// Bounded Founder read-through of the existing submission, manifest,
    /// evaluator, candidate, and transition authorities. It deliberately
    /// stores no derived lifecycle state: every displayed value is calculated
    /// from the durable records the workers already own.
    /// </summary>
    private async Task<LegendConnectFounderSectionPageSnapshot> GetFounderSubmissionProcessingPageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var rawQuery = _db.Set<LegendFounderTrainingSubmission>().AsNoTracking()
            .Where(item => item.SourceLanguageCode == language);
        if (search is not null)
        {
            rawQuery = rawQuery.Where(item => item.ProcessingState.ToLower().Contains(search) ||
                item.RawTextHash.ToLower().Contains(search));
        }
        if (cursor is { } after)
        {
            rawQuery = rawQuery.Where(item => item.CreatedUtc < after.UpdatedUtc ||
                (item.CreatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        }

        var raw = await rawQuery
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .ToListAsync(cancellationToken);

        var manifest = Array.Empty<LegendCurriculumManifestWorkItem>();
        {
            var manifestQuery = _db.Set<LegendCurriculumManifestWorkItem>().AsNoTracking()
                .Where(item => item.SourceLanguageCode == language);
            if (search is not null)
            {
                manifestQuery = manifestQuery.Where(item => item.ProcessingState.ToLower().Contains(search) ||
                    item.ManifestHash.ToLower().Contains(search));
            }
            if (cursor is { } manifestAfter)
            {
                manifestQuery = manifestQuery.Where(item => item.CreatedUtc < manifestAfter.UpdatedUtc ||
                    (item.CreatedUtc == manifestAfter.UpdatedUtc && item.Id.CompareTo(manifestAfter.Id) < 0));
            }

            manifest = await manifestQuery
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id)
                .Take(FounderSectionPageSize + 1)
                .ToArrayAsync(cancellationToken);
        }

        var sources = raw.Select(item => FounderSubmissionStatusSource.FromTraining(item, language))
            .Concat(manifest.Select(FounderSubmissionStatusSource.FromManifest))
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .ThenByDescending(item => item.KindOrder)
            .Take(FounderSectionPageSize + 1)
            .ToList();
        var page = sources.Take(FounderSectionPageSize).ToList();
        if (page.Count == 0)
        {
            return new LegendConnectFounderSectionPageSnapshot(
                "submissions", language, search, FounderSectionPageSize, null,
                FounderSubmissionStatusColumns,
                Array.Empty<IReadOnlyList<string>>(),
                "No Founder curriculum submissions match this language and filter.");
        }

        var rawIds = page.Where(item => item.Training is not null).Select(item => item.Id).ToArray();
        var manifestSources = page.Where(item => item.Manifest is not null).ToArray();
        var rawExampleLinks = rawIds.Length == 0
            ? []
            : await (
                from submissionUnit in _db.Set<LegendFounderTrainingSubmissionUnit>().AsNoTracking()
                join example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                    on submissionUnit.TextUnitId equals example.TextUnitId
                where rawIds.Contains(submissionUnit.SubmissionId) &&
                    example.LanguageCode == language && example.SupersededUtc == null
                select new FounderExampleOwner(submissionUnit.SubmissionId, example.Id))
                .ToListAsync(cancellationToken);

        var rawCoverage = rawIds.Length == 0
            ? []
            : await (
                from submissionUnit in _db.Set<LegendFounderTrainingSubmissionUnit>().AsNoTracking()
                join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on submissionUnit.TextUnitId equals unit.Id
                join candidate in _db.Set<LegendCorpusCandidate>().AsNoTracking()
                    on unit.NormalizedHash equals candidate.SourceTextHash
                where rawIds.Contains(submissionUnit.SubmissionId) &&
                    candidate.SourceLanguageCode == language &&
                    candidate.CurriculumFamilyId == null &&
                    candidate.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
                select new FounderCoverageOwner(
                    submissionUnit.SubmissionId,
                    candidate.ProcessingState,
                    candidate.ProcessedUtc))
                .ToListAsync(cancellationToken);

        // V20.2: retain the canonical curriculum-family ownership of legacy
        // atomic submissions so current-evaluator SourceFamilies durable work
        // can be projected back to the submission that supplied the evidence.
        // This is read-only projection metadata; it creates no processing work.
        var rawFamilyLinks = rawIds.Length == 0
            ? []
            : await (
                from submissionUnit in _db.Set<LegendFounderTrainingSubmissionUnit>().AsNoTracking()
                join example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                    on submissionUnit.TextUnitId equals example.TextUnitId
                where rawIds.Contains(submissionUnit.SubmissionId) &&
                    example.LanguageCode == language &&
                    example.SupersededUtc == null
                select new FounderFamilyOwner(
                    submissionUnit.SubmissionId,
                    example.CurriculumFamilyId))
                .Distinct()
                .ToListAsync(cancellationToken);

        var familyKeysByManifest = manifestSources.ToDictionary(
            item => item.Id,
            item => ReadManifestFamilyKeys(item.Manifest!.PayloadJson));
        var familyKeys = familyKeysByManifest.Values.SelectMany(item => item)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var families = familyKeys.Length == 0
            ? []
            : await _db.Set<LegendCurriculumFamily>().AsNoTracking()
                .Where(item => familyKeys.Contains(item.FamilyKey))
                .Select(item => new FounderFamilyIdentity(item.Id, item.FamilyKey))
                .ToListAsync(cancellationToken);
        var familyIdsByManifest = familyKeysByManifest.ToDictionary(
            item => item.Key,
            item => families.Where(family => item.Value.Contains(family.FamilyKey, StringComparer.Ordinal))
                .Select(family => family.Id)
                .ToHashSet());
        var familyIds = familyIdsByManifest.Values.SelectMany(item => item).Distinct().ToArray();
        var manifestExamples = familyIds.Length == 0
            ? []
            : await _db.Set<LegendCurriculumExample>().AsNoTracking()
                .Where(item => familyIds.Contains(item.CurriculumFamilyId) &&
                    item.LanguageCode == language && item.SupersededUtc == null)
                .Select(item => new FounderManifestExample(item.CurriculumFamilyId, item.Id))
                .ToListAsync(cancellationToken);
        var manifestCoverage = familyIds.Length == 0
            ? []
            : await _db.Set<LegendCorpusCandidate>().AsNoTracking()
                .Where(item => item.CurriculumFamilyId != null && familyIds.Contains(item.CurriculumFamilyId.Value))
                .Select(item => new FounderManifestCoverage(
                    item.CurriculumFamilyId!.Value,
                    item.ProcessingState,
                    item.ProcessedUtc))
                .ToListAsync(cancellationToken);

        var exampleOwners = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var item in rawExampleLinks)
            AddFounderSubmissionOwner(exampleOwners, item.ExampleId, item.SubmissionId);
        foreach (var item in manifestExamples)
        {
            foreach (var work in familyIdsByManifest.Where(entry => entry.Value.Contains(item.FamilyId)))
                AddFounderSubmissionOwner(exampleOwners, item.ExampleId, work.Key);
        }

        var exampleIds = exampleOwners.Keys.ToArray();
        var transitionEvidence = exampleIds.Length == 0
            ? []
            : await _db.Set<LegendSemanticTransitionEvidence>().AsNoTracking()
                .Where(item => item.SupersededUtc == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    (exampleIds.Contains(item.SourceCurriculumExampleId) ||
                     exampleIds.Contains(item.ResultCurriculumExampleId)))
                .Select(item => new FounderTransitionEvidence(
                    item.Id,
                    item.TransitionSignature,
                    item.SourceCurriculumExampleId,
                    item.ResultCurriculumExampleId))
                .ToListAsync(cancellationToken);
        var transitionEvidenceBySubmission = new Dictionary<Guid, HashSet<FounderTransitionEvidence>>();
        foreach (var evidence in transitionEvidence)
        {
            AddFounderTransitionOwners(transitionEvidenceBySubmission, exampleOwners, evidence.SourceExampleId, evidence);
            AddFounderTransitionOwners(transitionEvidenceBySubmission, exampleOwners, evidence.ResultExampleId, evidence);
        }

        var productionEligibleSignatures = await Curriculum
            .GetProductionEligibleSemanticTransitionSignaturesAsync(
                language,
                transitionEvidence.Select(item => item.TransitionSignature).Distinct().ToArray(),
                cancellationToken);
        var coverageBySubmission = new Dictionary<Guid, List<FounderCoverageItem>>();
        foreach (var item in rawCoverage)
        {
            AddFounderCoverageOwner(
                coverageBySubmission,
                item.SubmissionId,
                NormalizeCorpusCoverageState(item.ProcessingState),
                item.ProcessedUtc);
        }

        foreach (var item in manifestCoverage)
        {
            foreach (var work in familyIdsByManifest.Where(
                entry => entry.Value.Contains(item.FamilyId)))
            {
                AddFounderCoverageOwner(
                    coverageBySubmission,
                    work.Key,
                    NormalizeCorpusCoverageState(item.ProcessingState),
                    item.ProcessedUtc);
            }
        }

        var currentEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;

        // V20.2: current-evaluator durable work is the authoritative processing
        // lifecycle when it exists. The prior projection looked only at corpus
        // candidates, which made live V20 Pending/Processing/Completed work
        // appear as zero on the Founder Curriculum Processing page.
        //
        // Preserve the existing corpus-candidate projection strictly as the
        // compatibility fallback for submissions that do not yet have current
        // durable descendants.
        var durableCoverageBySubmission =
            new Dictionary<Guid, List<FounderCoverageItem>>();

        var manifestIds = manifestSources.Select(item => item.Id).ToArray();
        if (manifestIds.Length > 0)
        {
            var durableManifestCoverage = await _db
                .Set<LegendHistoricalReevaluationWorkItem>()
                .AsNoTracking()
                .Where(item =>
                    item.EvaluatorVersion == currentEvaluatorVersion &&
                    item.Phase ==
                        LegendConnectHistoricalReevaluationWorkAuthority.FounderCurriculumPhase &&
                    item.WorkKind ==
                        LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestFamilyWorkKind &&
                    item.SubjectId != null &&
                    manifestIds.Contains(item.SubjectId.Value))
                .Select(item => new FounderDurableCoverage(
                    item.SubjectId!.Value,
                    item.ProcessingState,
                    item.CompletedUtc ?? item.UpdatedUtc))
                .ToListAsync(cancellationToken);

            foreach (var item in durableManifestCoverage)
            {
                AddFounderCoverageOwner(
                    durableCoverageBySubmission,
                    item.OwnerId,
                    NormalizeDurableCoverageState(item.ProcessingState),
                    item.ProcessedUtc);
            }
        }

        var rawFamilyIds = rawFamilyLinks
            .Select(item => item.FamilyId)
            .Distinct()
            .ToArray();

        if (rawFamilyIds.Length > 0)
        {
            var durableFamilyCoverage = await _db
                .Set<LegendHistoricalReevaluationWorkItem>()
                .AsNoTracking()
                .Where(item =>
                    item.EvaluatorVersion == currentEvaluatorVersion &&
                    item.Phase ==
                        LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies &&
                    item.WorkKind ==
                        "Canonical" &&
                    item.SubjectId != null &&
                    rawFamilyIds.Contains(item.SubjectId.Value))
                .Select(item => new FounderDurableCoverage(
                    item.SubjectId!.Value,
                    item.ProcessingState,
                    item.CompletedUtc ?? item.UpdatedUtc))
                .ToListAsync(cancellationToken);

            foreach (var item in durableFamilyCoverage)
            {
                foreach (var owner in rawFamilyLinks.Where(
                    link => link.FamilyId == item.OwnerId))
                {
                    AddFounderCoverageOwner(
                        durableCoverageBySubmission,
                        owner.SubmissionId,
                        NormalizeDurableCoverageState(item.ProcessingState),
                        item.ProcessedUtc);
                }
            }
        }
        // One bounded global convergence projection is sufficient for this
        // page. It does not materialize per-submission evidence or create a
        // second status authority: submission completion remains derived from
        // its existing durable child work, while this row explains whether a
        // newer evaluator reused prior contracts or has a real dependency
        // frontier in flight.
        var convergence = await _db.Set<LegendLanguageDerivationConvergence>()
            .AsNoTracking()
            .Where(item => item.TargetEvaluatorVersion == currentEvaluatorVersion)
            .Select(item => new FounderDerivationConvergence(
                item.State,
                item.EarliestAffectedPhase,
                item.ExistingCanonicalArtifactCount,
                item.ReusedCanonicalArtifactCount,
                item.AffectedCanonicalArtifactCount,
                item.RequiresDependencyInventory,
                item.DependencyInventoryWorkItemCount,
                item.PlannedWorkItemCount,
                item.BlockingDependencyIdentity,
                item.UpdatedUtc))
            .SingleOrDefaultAsync(cancellationToken);
        var rows = new List<IReadOnlyList<string>>(page.Count);
        foreach (var item in page)
        {
            var currentDurableCoverage =
                durableCoverageBySubmission.GetValueOrDefault(item.Id);
            var coverage = FounderCoverageSummary.From(
                currentDurableCoverage is { Count: > 0 }
                    ? currentDurableCoverage
                    : coverageBySubmission.GetValueOrDefault(item.Id) ?? []);
            var transitions = transitionEvidenceBySubmission.GetValueOrDefault(item.Id) ?? [];
            var completedVersion = item.Training?.CompletedLanguageIntelligenceEvaluatorVersion ??
                item.Manifest!.CompletedLanguageIntelligenceEvaluatorVersion;
            var evaluatorTarget = item.Manifest is null
                ? $"v{currentEvaluatorVersion}"
                : $"current v{currentEvaluatorVersion}; work v{Math.Max(0, item.Manifest.TargetLanguageIntelligenceEvaluatorVersion)}";
            var workState = item.Training is not null
                ? item.Training.ProcessingState
                : $"{item.Manifest!.ProcessingState}; {item.Manifest.NextFamilyIndex:N0}/{item.Manifest.FamilyCount:N0} families; attempts {item.Manifest.AttemptCount:N0}";
            var failure = item.Manifest?.LastErrorCode ?? (coverage.Failed > 0 ? "coverage_failed" : null);
            var evaluatorCurrent = completedVersion >= currentEvaluatorVersion;
            var activelyProcessing = item.Manifest?.ProcessingState == "Processing" ||
                item.Training?.LanguageIntelligenceReevaluationLeaseExpiresUtc is not null ||
                coverage.Processing > 0;
            var status = DeriveFounderSubmissionStatus(
                evaluatorCurrent,
                activelyProcessing,
                item.Manifest?.ProcessingState == "Failed" || coverage.Failed > 0,
                coverage);
            var lastProcessedUtc = MaxFounderProcessingTime(
                item.Training?.ProcessedUtc,
                item.Manifest?.UpdatedUtc,
                coverage.LastProcessedUtc);
            var convergenceState = FormatFounderDerivationConvergence(
                convergence,
                completedVersion,
                currentEvaluatorVersion);
            rows.Add(new[]
            {
                item.Kind,
                item.Id.ToString("D"),
                item.CreatedUtc.ToString("u", CultureInfo.InvariantCulture),
                item.AcceptedUnitDisplay,
                FormatFounderReceiptCount(item.Training?.NewCanonicalUnitCount, item.Training is not null),
                FormatFounderReceiptCount(item.Training?.ReusedCanonicalUnitCount, item.Training is not null),
                item.Training?.QueuedCoverageCount?.ToString(CultureInfo.InvariantCulture) ?? coverage.Total.ToString(CultureInfo.InvariantCulture),
                coverage.Pending.ToString(CultureInfo.InvariantCulture),
                coverage.Processing.ToString(CultureInfo.InvariantCulture),
                coverage.Completed.ToString(CultureInfo.InvariantCulture),
                coverage.Failed.ToString(CultureInfo.InvariantCulture),
                evaluatorTarget,
                $"v{completedVersion}",
                workState,
                transitions.Count.ToString(CultureInfo.InvariantCulture),
                transitions.Count(item => productionEligibleSignatures.Contains(item.TransitionSignature)).ToString(CultureInfo.InvariantCulture),
                lastProcessedUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "—",
                convergenceState,
                status,
                failure ?? "—"
            });
        }

        return new LegendConnectFounderSectionPageSnapshot(
            "submissions",
            language,
            search,
            FounderSectionPageSize,
            sources.Count > FounderSectionPageSize
                ? FormatFounderSectionCursor(page[^1].CreatedUtc, page[^1].Id)
                : null,
            FounderSubmissionStatusColumns,
            rows,
            "No Founder curriculum submissions match this language and filter.");
    }

    private static IReadOnlyList<string> ReadManifestFamilyKeys(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LegendConnectCurriculumManifestSubmission>(payloadJson)?.Families?
                .Select(item => item.FamilyKey?.Trim().ToLowerInvariant())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddFounderSubmissionOwner(
        IDictionary<Guid, HashSet<Guid>> owners,
        Guid exampleId,
        Guid submissionId)
    {
        if (!owners.TryGetValue(exampleId, out var values))
        {
            values = [];
            owners[exampleId] = values;
        }
        values.Add(submissionId);
    }

    private static void AddFounderTransitionOwners(
        IDictionary<Guid, HashSet<FounderTransitionEvidence>> owners,
        IReadOnlyDictionary<Guid, HashSet<Guid>> exampleOwners,
        Guid exampleId,
        FounderTransitionEvidence evidence)
    {
        if (!exampleOwners.TryGetValue(exampleId, out var submissionIds))
            return;
        foreach (var submissionId in submissionIds)
        {
            if (!owners.TryGetValue(submissionId, out var values))
            {
                values = [];
                owners[submissionId] = values;
            }
            values.Add(evidence);
        }
    }

    private static void AddFounderCoverageOwner(
        IDictionary<Guid, List<FounderCoverageItem>> owners,
        Guid submissionId,
        FounderCoverageState state,
        DateTime? processedUtc)
    {
        if (!owners.TryGetValue(submissionId, out var values))
        {
            values = [];
            owners[submissionId] = values;
        }

        values.Add(new FounderCoverageItem(state, processedUtc));
    }

    private static FounderCoverageState NormalizeCorpusCoverageState(
        string processingState) =>
        processingState switch
        {
            "Pending" => FounderCoverageState.Pending,
            "Queued" => FounderCoverageState.Pending,
            "Processing" => FounderCoverageState.Processing,
            "Processed" => FounderCoverageState.Completed,
            "Deduplicated" => FounderCoverageState.Completed,
            "Superseded" => FounderCoverageState.Completed,
            "Failed" => FounderCoverageState.Failed,
            _ => FounderCoverageState.Unresolved
        };

    private static FounderCoverageState NormalizeDurableCoverageState(
        string processingState) =>
        processingState switch
        {
            LegendConnectHistoricalReevaluationWorkAuthority.Pending =>
                FounderCoverageState.Pending,

            LegendConnectHistoricalReevaluationWorkAuthority.Processing =>
                FounderCoverageState.Processing,

            LegendConnectHistoricalReevaluationWorkAuthority.Completed =>
                FounderCoverageState.Completed,

            LegendConnectHistoricalReevaluationWorkAuthority.Failed =>
                FounderCoverageState.Failed,

            _ => FounderCoverageState.Unresolved
        };

    private static string DeriveFounderSubmissionStatus(
        bool evaluatorCurrent,
        bool activelyProcessing,
        bool failed,
        FounderCoverageSummary coverage)
    {
        if (failed)
            return "FAILED";
        if (evaluatorCurrent && coverage.Pending == 0 && coverage.Processing == 0 && coverage.Unresolved == 0)
            return "COMPLETED";
        if (activelyProcessing)
            return "PROCESSING";
        // A capability-version advance is not in progress merely because an
        // earlier evaluation completed. Until the existing worker claims the
        // stale durable record, it is accurately queued for reconciliation.
        if (!evaluatorCurrent)
            return "QUEUED";
        if (coverage.Pending > 0)
            return coverage.Completed > 0 ? "PROCESSING" : "QUEUED";
        if (coverage.Unresolved > 0)
            return "PROCESSING";
        return "QUEUED";
    }

    private static string FormatFounderReceiptCount(int? value, bool isTrainingSubmission) =>
        !isTrainingSubmission ? "—" : value?.ToString(CultureInfo.InvariantCulture) ?? "Not captured";

    private static DateTime? MaxFounderProcessingTime(params DateTime?[] values)
    {
        var timestamps = values.Where(item => item.HasValue).Select(item => item!.Value).ToArray();
        return timestamps.Length == 0 ? null : timestamps.Max();
    }

    private static string FormatFounderDerivationConvergence(
        FounderDerivationConvergence? convergence,
        int completedEvaluatorVersion,
        int currentEvaluatorVersion)
    {
        if (convergence is null)
        {
            return completedEvaluatorVersion >= currentEvaluatorVersion
                ? "CURRENT — pre-contract baseline"
                : "STALE — awaiting dependency assessment";
        }

        var reuse = convergence.ExistingArtifacts == 0
            ? "0/0 reused"
            : $"{convergence.ReusedArtifacts:N0}/{convergence.ExistingArtifacts:N0} reused";
        if (convergence.RequiresDependencyInventory)
        {
            return convergence.State == "Processing"
                ? $"PROCESSING — dependency inventory; {convergence.DependencyInventoryWork:N0} bounded item(s); {reuse}"
                : $"QUEUED — dependency inventory; {reuse}";
        }
        return convergence.State switch
        {
            "Reused" => $"REUSED — NO REEVALUATION REQUIRED; {reuse}",
            "Completed" => $"COMPLETED — {reuse}; {convergence.AffectedArtifacts:N0} affected",
            "Processing" => $"PROCESSING — {convergence.EarliestPhase ?? "dependency"}; {convergence.PlannedWork:N0} queued",
            "Queued" => $"QUEUED — {convergence.EarliestPhase ?? "dependency"}; {convergence.BlockingDependency ?? "dependency frontier"}",
            _ => $"{convergence.State.ToUpperInvariant()} — {convergence.EarliestPhase ?? "dependency"}"
        };
    }

    private static readonly IReadOnlyList<string> FounderSubmissionStatusColumns =
    [
        "Submission", "Submission ID", "Accepted", "Accepted atomic units", "New units", "Reused units",
        "Coverage queued", "Coverage pending", "Coverage processing", "Coverage completed", "Coverage failed",
        "Evaluator target", "Evaluator completed", "Manifest / work-item state", "Transition evidence",
        "Production-eligible transitions", "Last processed", "Derivation convergence", "Status", "Failure"
    ];

    private sealed record FounderFamilyOwner(
        Guid SubmissionId,
        Guid FamilyId);

    private sealed record FounderDurableCoverage(
        Guid OwnerId,
        string ProcessingState,
        DateTime? ProcessedUtc);

    private sealed record FounderSubmissionStatusSource(
        Guid Id,
        string Kind,
        int KindOrder,
        string LanguageCode,
        DateTime CreatedUtc,
        string AcceptedUnitDisplay,
        LegendFounderTrainingSubmission? Training,
        LegendCurriculumManifestWorkItem? Manifest)
    {
        internal static FounderSubmissionStatusSource FromTraining(
            LegendFounderTrainingSubmission item,
            string language) => new(
            item.Id,
            "Atomic training",
            0,
            language,
            item.CreatedUtc,
            item.AtomicUnitCount.ToString(CultureInfo.InvariantCulture),
            item,
            null);

        internal static FounderSubmissionStatusSource FromManifest(
            LegendCurriculumManifestWorkItem item) => new(
            item.Id,
            "Semantic manifest",
            1,
            item.SourceLanguageCode,
            item.CreatedUtc,
            $"{item.ExampleCount:N0} declared examples",
            null,
            item);
    }

    private sealed record FounderExampleOwner(Guid SubmissionId, Guid ExampleId);
    private sealed record FounderFamilyIdentity(Guid Id, string FamilyKey);
    private sealed record FounderManifestExample(Guid FamilyId, Guid ExampleId);
    private sealed record FounderCoverageOwner(Guid SubmissionId, string ProcessingState, DateTime? ProcessedUtc);
    private sealed record FounderManifestCoverage(Guid FamilyId, string ProcessingState, DateTime? ProcessedUtc);
    private sealed record FounderDerivationConvergence(
        string State,
        string? EarliestPhase,
        long ExistingArtifacts,
        long ReusedArtifacts,
        long AffectedArtifacts,
        bool RequiresDependencyInventory,
        long DependencyInventoryWork,
        long PlannedWork,
        string? BlockingDependency,
        DateTime UpdatedUtc);
    private enum FounderCoverageState
    {
        Unresolved = 0,
        Pending = 1,
        Processing = 2,
        Completed = 3,
        Failed = 4
    }

    private sealed record FounderCoverageItem(
        FounderCoverageState State,
        DateTime? ProcessedUtc);
    private sealed record FounderTransitionEvidence(
        Guid Id,
        string TransitionSignature,
        Guid SourceExampleId,
        Guid ResultExampleId);
    private sealed record FounderCoverageSummary(
        int Total,
        int Pending,
        int Processing,
        int Completed,
        int Failed,
        int Unresolved,
        DateTime? LastProcessedUtc)
    {
        internal static FounderCoverageSummary From(IReadOnlyCollection<FounderCoverageItem> items)
        {
            var pending = items.Count(
                item => item.State == FounderCoverageState.Pending);

            var processing = items.Count(
                item => item.State == FounderCoverageState.Processing);

            var completed = items.Count(
                item => item.State == FounderCoverageState.Completed);

            var failed = items.Count(
                item => item.State == FounderCoverageState.Failed);

            var unresolved = items.Count(
                item => item.State == FounderCoverageState.Unresolved);
            return new FounderCoverageSummary(
                items.Count,
                pending,
                processing,
                completed,
                failed,
                unresolved,
                MaxFounderProcessingTime(items.Select(item => item.ProcessedUtc).ToArray()));
        }
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetCurriculumFamilyPageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendCurriculumFamily>().AsNoTracking()
            .Where(family => _db.Set<LegendCurriculumExample>().Any(example =>
                example.CurriculumFamilyId == family.Id && example.LanguageCode == language && example.SupersededUtc == null));
        if (search is not null)
            query = query.Where(item => item.FamilyKey.ToLower().Contains(search) ||
                (item.SemanticCategory ?? string.Empty).ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.UpdatedUtc < after.UpdatedUtc ||
                (item.UpdatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));

        var families = await query
            .OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new { item.Id, item.FamilyKey, item.SemanticCategory, item.Provenance, item.UpdatedUtc })
            .ToListAsync(cancellationToken);
        var ids = families.Take(FounderSectionPageSize).Select(item => item.Id).ToArray();
        var counts = ids.Length == 0
            ? new Dictionary<Guid, long>()
            : await _db.Set<LegendCurriculumExample>().AsNoTracking()
                .Where(item => ids.Contains(item.CurriculumFamilyId) && item.LanguageCode == language && item.SupersededUtc == null)
                .GroupBy(item => item.CurriculumFamilyId)
                .Select(group => new { Id = group.Key, Count = group.LongCount() })
                .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
        return FounderPage("curriculum", language, search, families,
            item => item.Id, item => item.UpdatedUtc,
            item => new[] { item.Id.ToString("D"), item.FamilyKey, item.SemanticCategory ?? "—", counts.GetValueOrDefault(item.Id).ToString(CultureInfo.InvariantCulture), item.Provenance, item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Family ID", "Family", "Semantic category", "Examples", "Provenance", "Updated" },
            "No curriculum family matches this language and filter.");
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetCurriculumExamplePageAsync(
        string language,
        Guid familyId,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query =
            from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
            join text in _db.Set<LegendLanguageTextUnit>().AsNoTracking() on example.TextUnitId equals text.Id
            where example.CurriculumFamilyId == familyId && example.LanguageCode == language &&
                example.SupersededUtc == null && text.IsTrainingEligible
            select new { example.Id, Text = text.Text, example.Provenance, example.UpdatedUtc };
        if (search is not null)
            query = query.Where(item => item.Text.ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.UpdatedUtc < after.UpdatedUtc ||
                (item.UpdatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var examples = await query.OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1).ToListAsync(cancellationToken);
        var ids = examples.Take(FounderSectionPageSize).Select(item => item.Id).ToArray();
        var anchorCounts = ids.Length == 0
            ? new Dictionary<Guid, long>()
            : await _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
                .Where(item => ids.Contains(item.CurriculumExampleId) && item.SupersededUtc == null)
                .GroupBy(item => item.CurriculumExampleId)
                .Select(group => new { Id = group.Key, Count = group.LongCount() })
                .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
        return FounderPage("curriculum-examples", language, search, examples,
            item => item.Id, item => item.UpdatedUtc,
            item => new[] { item.Id.ToString("D"), item.Text, anchorCounts.GetValueOrDefault(item.Id).ToString(CultureInfo.InvariantCulture), item.Provenance, item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Example ID", "Canonical example", "Anchors", "Provenance", "Updated" },
            "No active examples match this curriculum family and filter.");
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetCandidatePageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendLanguageTargetRealizationCandidate>().AsNoTracking()
            .Where(item => item.SupersededUtc == null &&
                (item.SourceLanguageCode == language || item.TargetLanguageCode == language));
        if (search is not null)
            query = query.Where(item => item.PairKey.ToLower().Contains(search) ||
                item.VariationDimension.ToLower().Contains(search) || item.SemanticValue.ToLower().Contains(search) ||
                item.TargetRealization.ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.UpdatedUtc < after.UpdatedUtc ||
                (item.UpdatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var candidates = await query.OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new { item.Id, item.PairKey, item.VariationDimension, item.SemanticValue, item.VerificationState, item.MaturityState, item.SupportCount, item.ContradictionCount, item.IsProductionEligible, item.UpdatedUtc })
            .ToListAsync(cancellationToken);
        return FounderPage("candidates", language, search, candidates,
            item => item.Id, item => item.UpdatedUtc,
            item => new[] { item.PairKey, item.VariationDimension, item.SemanticValue, item.VerificationState, item.MaturityState, item.SupportCount.ToString(CultureInfo.InvariantCulture), item.ContradictionCount.ToString(CultureInfo.InvariantCulture), item.IsProductionEligible ? "Eligible" : "Closed", item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Pair", "Dimension", "Value", "Verification", "Maturity", "Support", "Contradictions", "Production", "Updated" },
            "No target-realization candidates match this language and filter.");
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetAnchorPageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
            .Where(item => item.LanguageCode == language && item.SupersededUtc == null);
        if (search is not null)
            query = query.Where(item => item.Dimension.ToLower().Contains(search) || item.Value.ToLower().Contains(search) ||
                (item.PairKey ?? string.Empty).ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.CreatedUtc < after.UpdatedUtc ||
                (item.CreatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var anchors = await query.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new { item.Id, item.PairKey, item.Dimension, item.Value, item.Provenance, item.CreatedUtc })
            .ToListAsync(cancellationToken);
        return FounderPage("evidence", language, search, anchors,
            item => item.Id, item => item.CreatedUtc,
            item => new[] { item.Id.ToString("D"), item.PairKey ?? "Source", item.Dimension, item.Value, item.Provenance, item.CreatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Anchor ID", "Scope", "Dimension", "Value", "Provenance", "Created" },
            "No active compositional anchors match this language and filter.");
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetRelationshipPageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendLanguageStructuralRelationship>().AsNoTracking()
            .Where(item => item.LanguageCode == language && item.SupersededUtc == null);
        if (search is not null)
            query = query.Where(item => item.PairKey.ToLower().Contains(search) || item.VariationDimension.ToLower().Contains(search) ||
                item.MaturityState.ToLower().Contains(search) || item.Provenance.ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.UpdatedUtc < after.UpdatedUtc ||
                (item.UpdatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var relationships = await query.OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new { item.Id, item.PairKey, item.VariationDimension, item.MaturityState, item.SupportCount, item.IndependentSourceCount, item.ContradictionCount, item.IsProductionEligible, item.UpdatedUtc })
            .ToListAsync(cancellationToken);
        return FounderPage("relationships", language, search, relationships,
            item => item.Id, item => item.UpdatedUtc,
            item => new[] { item.PairKey, item.VariationDimension, item.MaturityState, item.SupportCount.ToString(CultureInfo.InvariantCulture), item.IndependentSourceCount.ToString(CultureInfo.InvariantCulture), item.ContradictionCount.ToString(CultureInfo.InvariantCulture), item.IsProductionEligible ? "Eligible" : "Closed", item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Pair", "Dimension", "Maturity", "Support", "Independent", "Contradictions", "Production", "Updated" },
            "No active structural relationships match this language and filter.");
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetLearningPageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendTranslationLearningEvent>().AsNoTracking()
            .Where(item => item.SourceLanguageCode == language || item.TargetLanguageCode == language);
        if (search is not null)
            query = query.Where(item => item.PairKey.ToLower().Contains(search) || item.Provenance.ToLower().Contains(search) ||
                item.ProcessingState.ToLower().Contains(search) || (item.FailureCode ?? string.Empty).ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.CreatedUtc < after.UpdatedUtc ||
                (item.CreatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var events = await query.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new { item.Id, item.PairKey, item.Provenance, item.EligibilityState, item.ProcessingState, item.AttemptCount, item.FailureCode, item.CreatedUtc })
            .ToListAsync(cancellationToken);
        return FounderPage("learning", language, search, events,
            item => item.Id, item => item.CreatedUtc,
            item => new[] { item.PairKey, item.Provenance, item.EligibilityState, item.ProcessingState, item.AttemptCount.ToString(CultureInfo.InvariantCulture), item.FailureCode ?? "—", item.CreatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Pair", "Provenance", "Eligibility", "State", "Attempts", "Failure", "Queued" },
            "No translation events match this language and filter.");
    }

    /// <summary>
    /// Read-only Founder projection of the existing MachineProposed lifecycle.
    /// The candidate primary key is the durable correlation identity: the
    /// proposal foreign key carries it through critic, validator, and admission,
    /// whose state and audit fields remain authoritative. No proposal state is
    /// inferred back into storage and no private candidate or proposal content
    /// is selected.
    /// </summary>
    private async Task<LegendConnectFounderSectionPageSnapshot>
        GetMachineLearningLifecyclePageAsync(
            string language,
            string? search,
            FounderSectionCursor? cursor,
            CancellationToken cancellationToken)
    {
        var searchedId = Guid.TryParse(search, out var parsedId)
            ? parsedId
            : (Guid?)null;
        var hasSearchedId = searchedId.HasValue;
        var searchedGuid = searchedId ?? Guid.Empty;
        var query =
            from candidate in _db.Set<LegendCorpusCandidate>().AsNoTracking()
            join proposal in _db.Set<LegendLanguageTeacherProposal>().AsNoTracking()
                on candidate.Id equals proposal.CorpusCandidateId into proposals
            from proposal in proposals.DefaultIfEmpty()
            where (candidate.SourceLanguageCode == language ||
                    candidate.TargetLanguageCode == language) &&
                (candidate.TeacherProposalProcessingState != "NotStarted" ||
                    proposal != null)
            select new MachineLearningLifecycleSource(
                candidate.Id,
                candidate.SourceLanguageCode,
                candidate.TargetLanguageCode,
                candidate.Provenance,
                candidate.TeacherProposalProcessingState,
                candidate.TeacherProposalAttemptCount,
                candidate.TeacherProposalFailureCode,
                candidate.CreatedUtc,
                candidate.TeacherProposalProcessedUtc,
                proposal == null ? null : proposal.Id,
                proposal == null ? null : proposal.PairKey,
                proposal == null ? null : proposal.FamilyKey,
                proposal == null ? null : proposal.Provenance,
                proposal == null ? null : proposal.ValidationState,
                proposal != null && proposal.CriticApproved,
                proposal == null ? null : proposal.CriticConfidence,
                proposal == null ? null : proposal.CriticReasonCodesJson,
                proposal == null ? 0 : proposal.CanonicalValidationAttemptCount,
                proposal == null ? null : proposal.CanonicalValidatedUtc,
                proposal == null ? null : proposal.CanonicalValidationFailureCode,
                proposal == null ? 0 : proposal.CurriculumAdmissionAttemptCount,
                proposal == null ? null : proposal.CurriculumAdmittedUtc,
                proposal == null ? null : proposal.CurriculumAdmissionFailureCode,
                proposal == null ? null : proposal.CreatedUtc,
                proposal == null ? null : proposal.UpdatedUtc,
                proposal == null
                    ? candidate.TeacherProposalProcessedUtc ?? candidate.ProcessedUtc ?? candidate.CreatedUtc
                    : proposal.UpdatedUtc,
                proposal == null ? candidate.Id : proposal.Id);

        if (search is not null)
        {
            query = query.Where(item =>
                (hasSearchedId &&
                    (item.CorrelationId == searchedGuid ||
                        item.ProposalId == searchedGuid)) ||
                item.SourceLanguageCode.ToLower().Contains(search) ||
                item.TargetLanguageCode.ToLower().Contains(search) ||
                item.CandidateProvenance.ToLower().Contains(search) ||
                item.CandidateState.ToLower().Contains(search) ||
                (item.PairKey ?? string.Empty).ToLower().Contains(search) ||
                (item.ProposalProvenance ?? string.Empty).ToLower().Contains(search) ||
                (item.ActualProposalState ?? string.Empty).ToLower().Contains(search) ||
                (item.CandidateFailureCode ?? string.Empty).ToLower().Contains(search) ||
                (item.ValidatorFailureCode ?? string.Empty).ToLower().Contains(search) ||
                (item.AdmissionFailureCode ?? string.Empty).ToLower().Contains(search));
        }

        if (cursor is { } after)
        {
            query = query.Where(item => item.SortUpdatedUtc < after.UpdatedUtc ||
                (item.SortUpdatedUtc == after.UpdatedUtc &&
                    item.SortId.CompareTo(after.Id) < 0));
        }

        var values = await query
            .OrderByDescending(item => item.SortUpdatedUtc)
            .ThenByDescending(item => item.SortId)
            .Take(FounderSectionPageSize + 1)
            .ToListAsync(cancellationToken);
        var page = values.Take(FounderSectionPageSize).ToList();
        var admittedFamilyKeys = page
            .Where(IsCompletedLifecycleAdmission)
            .Select(item => item.FamilyKey!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<AdmittedLifecycleFamilySource> admittedFamilies =
            admittedFamilyKeys.Length == 0
            ? []
            : await _db.Set<LegendCurriculumFamily>()
                .AsNoTracking()
                .Where(item => admittedFamilyKeys.Contains(item.FamilyKey))
                .Select(item => new AdmittedLifecycleFamilySource(
                    item.FamilyKey,
                    item.Id))
                .ToListAsync(cancellationToken);
        var admittedFamilyIds = admittedFamilies.ToDictionary(
            item => item.FamilyKey,
            item => item.Id,
            StringComparer.Ordinal);
        var hasMore = values.Count > FounderSectionPageSize;
        var nextCursor = hasMore && page.Count > 0
            ? FormatFounderSectionCursor(
                page[^1].SortUpdatedUtc,
                page[^1].SortId)
            : null;

        return new LegendConnectFounderSectionPageSnapshot(
            "machine-learning-lifecycle",
            language,
            search,
            FounderSectionPageSize,
            nextCursor,
            MachineLearningLifecycleColumns,
            page.Select(item =>
                MapMachineLearningLifecycleRow(item, admittedFamilyIds)).ToList(),
            page.Count == 0
                ? "No MachineProposed lifecycle records match this language and filter."
                : null);
    }

    /// <summary>
    /// Founder-visible projection over the existing operational-event ledger.
    /// It is deliberately paged and read-only and is not a research document,
    /// memory, evidence, learning, or serving query path.
    /// </summary>
    private async Task<LegendConnectFounderSectionPageSnapshot>
        GetResearchObservabilityPageAsync(
            string language,
            string? search,
            FounderSectionCursor? cursor,
            CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendConnectOperationalEvent>()
            .AsNoTracking()
            .Where(item =>
                item.Category == LegendConnectResearchContracts.ObservabilityCategory &&
                item.LanguageCode == language);
        if (search is not null)
        {
            query = query.Where(item =>
                item.Status.ToLower().Contains(search) ||
                (item.CorrelationId ?? string.Empty).ToLower().Contains(search) ||
                (item.ErrorCode ?? string.Empty).ToLower().Contains(search) ||
                (item.Summary ?? string.Empty).ToLower().Contains(search));
        }
        if (cursor is { } after)
        {
            query = query.Where(item =>
                item.OccurredUtc < after.UpdatedUtc ||
                (item.OccurredUtc == after.UpdatedUtc &&
                 item.Id.CompareTo(after.Id) < 0));
        }

        var events = await query
            .OrderByDescending(item => item.OccurredUtc)
            .ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new
            {
                item.Id,
                item.CorrelationId,
                item.Status,
                item.ErrorCode,
                item.Summary,
                item.OccurredUtc
            })
            .ToListAsync(cancellationToken);
        return FounderPage(
            "research-observability",
            language,
            search,
            events,
            item => item.Id,
            item => item.OccurredUtc,
            item => new[]
            {
                SanitizeLifecycleMetadata(item.CorrelationId, "unavailable_session"),
                SanitizeLifecycleMetadata(item.Status, "unavailable_facet"),
                SanitizeLifecycleFailure(item.ErrorCode),
                SanitizeResearchObservationSummary(item.Summary),
                item.OccurredUtc.ToString("u", CultureInfo.InvariantCulture)
            },
            new[] { "Session", "Facet / state", "Failure", "Sanitized receipt", "Observed" },
            "No governed research observations match this language and filter.");
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetModelPageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendConnectModelTrainingRun>().AsNoTracking();
        if (search is not null)
            query = query.Where(item => item.ScopeKey.ToLower().Contains(search) || item.State.ToLower().Contains(search) ||
                item.EvaluationState.ToLower().Contains(search) || item.PromotionState.ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.UpdatedUtc < after.UpdatedUtc ||
                (item.UpdatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var models = await query.OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new { item.Id, item.ScopeKey, item.Generation, item.State, item.EvaluationState, item.PromotionState, item.TrainingExampleCount, item.ValidationExampleCount, item.UpdatedUtc })
            .ToListAsync(cancellationToken);
        return FounderPage("models", language, search, models,
            item => item.Id, item => item.UpdatedUtc,
            item => new[] { item.ScopeKey, item.Generation.ToString(CultureInfo.InvariantCulture), item.State, item.EvaluationState, item.PromotionState, item.TrainingExampleCount.ToString(CultureInfo.InvariantCulture), item.ValidationExampleCount.ToString(CultureInfo.InvariantCulture), item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Scope", "Generation", "Training", "Evaluation", "Promotion", "Training examples", "Validation examples", "Updated" },
            "No model lifecycle runs match this filter.");
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetHealthPageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendConnectOperationalEvent>().AsNoTracking()
            .Where(item => item.LanguageCode == language);
        if (search is not null)
            query = query.Where(item => item.Category.ToLower().Contains(search) || item.Severity.ToLower().Contains(search) ||
                item.Status.ToLower().Contains(search) || (item.ErrorCode ?? string.Empty).ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.OccurredUtc < after.UpdatedUtc ||
                (item.OccurredUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var events = await query.OrderByDescending(item => item.OccurredUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new { item.Id, item.Category, item.Severity, item.Status, item.PairKey, item.ErrorCode, item.Summary, item.IsResolved, item.OccurredUtc })
            .ToListAsync(cancellationToken);
        return FounderPage("health", language, search, events,
            item => item.Id, item => item.OccurredUtc,
            item => new[] { item.Category, item.Severity, item.Status, item.PairKey ?? "—", item.ErrorCode ?? "—", item.Summary ?? "—", item.IsResolved ? "Resolved" : "Open", item.OccurredUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Category", "Severity", "Status", "Pair", "Code", "Summary", "Resolution", "Occurred" },
            "No health events match this language and filter.");
    }

    // Paged Founder inventory only. This optional SQL display filter is not a
    // candidate source for SearchRetainedKnowledgeAsync or native serving and
    // therefore cannot compete with the indexed semantic retrieval authority.
    private async Task<LegendConnectFounderSectionPageSnapshot> GetRetainedKnowledgePageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendLanguageTextUnit>().AsNoTracking()
            .Where(item => item.LanguageCode == language && item.IsTrainingEligible &&
                item.Provenance != "ConsentedLiveTranslation");
        if (search is not null)
            query = query.Where(item => item.Text.ToLower().Contains(search) || item.Provenance.ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.UpdatedUtc < after.UpdatedUtc ||
                (item.UpdatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var entries = await query.OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new { item.Id, item.Text, item.Provenance, item.CreatedUtc, item.UpdatedUtc })
            .ToListAsync(cancellationToken);
        return FounderPage("retained-knowledge", language, search, entries,
            item => item.Id, item => item.UpdatedUtc,
            item => new[] { item.Id.ToString("D"), item.Text, item.Provenance, item.CreatedUtc.ToString("u", CultureInfo.InvariantCulture), item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Text unit ID", "Canonical retained text", "Provenance", "Created", "Updated" },
            "No retained canonical knowledge matches this language and filter.");
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetPairPageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<LegendLanguagePair>().AsNoTracking()
            .Where(item => item.SourceLanguageCode == language || item.TargetLanguageCode == language);
        if (search is not null)
            query = query.Where(item => item.PairKey.ToLower().Contains(search) || item.QualityState.ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.UpdatedUtc < after.UpdatedUtc ||
                (item.UpdatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var pairs = await query.OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1)
            .Select(item => new { item.Id, item.PairKey, item.SourceLanguageCode, item.TargetLanguageCode, item.CorpusCoverage, item.QualityState, item.IsEnabled, item.UpdatedUtc })
            .ToListAsync(cancellationToken);
        return FounderPage("language-pairs", language, search, pairs,
            item => item.Id, item => item.UpdatedUtc,
            item => new[] { item.PairKey, item.SourceLanguageCode, item.TargetLanguageCode, item.CorpusCoverage.ToString(CultureInfo.InvariantCulture), item.QualityState, item.IsEnabled ? "Enabled" : "Disabled", item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Pair", "Source", "Target", "Coverage", "Quality", "State", "Updated" },
            "No language pairs match this language and filter.");
    }

    private async Task<LegendConnectFounderSectionPageSnapshot> GetProviderObservationPageAsync(
        string language,
        string? search,
        FounderSectionCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query =
            from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking() on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking() on alignment.TargetTextUnitId equals target.Id
            where alignment.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived && alignment.SupersededUtc == null &&
                source.IsTrainingEligible && target.IsTrainingEligible && source.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                (source.LanguageCode == language || target.LanguageCode == language)
            select new { alignment.Id, alignment.PairKey, Source = source.Text, Target = target.Text, alignment.QualityState, alignment.HumanVerified, alignment.UpdatedUtc };
        if (search is not null)
            query = query.Where(item => item.PairKey.ToLower().Contains(search) || item.Source.ToLower().Contains(search) || item.Target.ToLower().Contains(search) || item.QualityState.ToLower().Contains(search));
        if (cursor is { } after)
            query = query.Where(item => item.UpdatedUtc < after.UpdatedUtc ||
                (item.UpdatedUtc == after.UpdatedUtc && item.Id.CompareTo(after.Id) < 0));
        var observations = await query.OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id)
            .Take(FounderSectionPageSize + 1).ToListAsync(cancellationToken);
        return FounderPage("provider-observations", language, search, observations,
            item => item.Id, item => item.UpdatedUtc,
            item => new[] { item.Id.ToString("D"), item.PairKey, item.Source, item.Target, item.QualityState, item.HumanVerified ? "Human verified" : "Observation", item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture) },
            new[] { "Alignment ID", "Pair", "Founder source", "Provider target", "Quality", "Authority", "Updated" },
            "No provider observations match this language and filter.");
    }

    private static LegendConnectFounderSectionPageSnapshot FounderPage<T>(
        string section,
        string language,
        string? search,
        IReadOnlyList<T> values,
        Func<T, Guid> id,
        Func<T, DateTime> updatedUtc,
        Func<T, IReadOnlyList<string>> map,
        IReadOnlyList<string> columns,
        string emptyMessage)
    {
        var hasMore = values.Count > FounderSectionPageSize;
        var page = values.Take(FounderSectionPageSize).ToList();
        var nextCursor = hasMore && page.Count > 0
            ? FormatFounderSectionCursor(updatedUtc(page[^1]), id(page[^1]))
            : null;
        return new LegendConnectFounderSectionPageSnapshot(
            section,
            language,
            search,
            FounderSectionPageSize,
            nextCursor,
            columns,
            page.Select(map).ToList(),
            page.Count == 0 ? emptyMessage : null);
    }

    private static string? NormalizeFounderSectionSearch(string? search)
    {
        var normalized = search?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, 160)].ToLowerInvariant();
    }

    private static FounderSectionCursor? ParseFounderSectionCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split(':', 2);
            return parts.Length == 2 && long.TryParse(parts[0], out var ticks) && Guid.TryParseExact(parts[1], "N", out var id)
                ? new FounderSectionCursor(new DateTime(ticks, DateTimeKind.Utc), id)
                : throw new ArgumentException("The Founder section cursor is invalid.", nameof(value));
        }
        catch (FormatException)
        {
            throw new ArgumentException("The Founder section cursor is invalid.", nameof(value));
        }
    }

    private static string FormatFounderSectionCursor(DateTime updatedUtc, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{updatedUtc.Ticks}:{id:N}"));

    private sealed record FounderSectionCursor(DateTime UpdatedUtc, Guid Id);

    private static readonly string[] MachineLearningLifecycleColumns =
    [
        "Lifecycle / candidate ID",
        "Proposal ID",
        "Pair",
        "Candidate provenance",
        "Proposal provenance",
        "Actual state",
        "Candidate attempts",
        "Candidate failure",
        "Critic result",
        "Critic confidence",
        "Critic reasons",
        "Validator attempts",
        "Validator result",
        "Validator failure",
        "Admission attempts",
        "Admission result",
        "Admission failure",
        "Admission identity",
        "Candidate created",
        "Candidate processed",
        "Proposal created",
        "Proposal updated",
        "Validator completed",
        "Admission completed"
    ];

    private static IReadOnlyList<string> MapMachineLearningLifecycleRow(
        MachineLearningLifecycleSource item,
        IReadOnlyDictionary<string, Guid> admittedFamilyIds)
    {
        var validatorResult = MachineLearningValidatorResult(item);
        var admissionResult = MachineLearningAdmissionResult(item, validatorResult);
        var admissionIdentity = IsCompletedLifecycleAdmission(item) &&
            admittedFamilyIds.TryGetValue(item.FamilyKey!, out var familyId)
                ? familyId.ToString("D")
                : "—";

        return
        [
            item.CorrelationId.ToString("D"),
            item.ProposalId?.ToString("D") ?? "—",
            SanitizeLifecycleMetadata(
                item.PairKey ??
                    $"{item.SourceLanguageCode}:{item.TargetLanguageCode}",
                "unavailable_pair"),
            SanitizeLifecycleMetadata(
                item.CandidateProvenance,
                "unavailable_provenance"),
            SanitizeLifecycleMetadata(
                item.ProposalProvenance,
                "—"),
            SanitizeLifecycleMetadata(
                item.ActualProposalState ?? item.CandidateState,
                "unavailable_state"),
            item.CandidateAttemptCount.ToString(CultureInfo.InvariantCulture),
            SanitizeLifecycleFailure(item.CandidateFailureCode),
            MachineLearningCriticResult(item),
            item.CriticConfidence?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "—",
            SanitizeLifecycleCriticReasons(item.CriticReasonCodesJson),
            item.ValidatorAttemptCount.ToString(CultureInfo.InvariantCulture),
            validatorResult,
            SanitizeLifecycleFailure(item.ValidatorFailureCode),
            item.AdmissionAttemptCount.ToString(CultureInfo.InvariantCulture),
            admissionResult,
            SanitizeLifecycleFailure(item.AdmissionFailureCode),
            admissionIdentity,
            FormatLifecycleTimestamp(item.CandidateCreatedUtc),
            FormatLifecycleTimestamp(item.CandidateProcessedUtc),
            FormatLifecycleTimestamp(item.ProposalCreatedUtc),
            FormatLifecycleTimestamp(item.ProposalUpdatedUtc),
            FormatLifecycleTimestamp(item.ValidatorCompletedUtc),
            FormatLifecycleTimestamp(item.AdmissionCompletedUtc)
        ];
    }

    private static bool IsCompletedLifecycleAdmission(
        MachineLearningLifecycleSource item) =>
        item.ProposalId.HasValue &&
        item.AdmissionCompletedUtc.HasValue &&
        item.AdmissionFailureCode is null &&
        string.Equals(
            item.ActualProposalState,
            "CurriculumAdmitted",
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(item.FamilyKey);

    private static string MachineLearningCriticResult(
        MachineLearningLifecycleSource item)
    {
        if (!item.ProposalId.HasValue)
            return "Not created";
        if (string.Equals(
                item.ActualProposalState,
                "AwaitingCritic",
                StringComparison.Ordinal) &&
            item.CriticConfidence is null)
        {
            return "Pending";
        }

        return item.CriticApproved ? "Approved" : "Rejected";
    }

    private static string MachineLearningValidatorResult(
        MachineLearningLifecycleSource item)
    {
        if (!item.ProposalId.HasValue || !item.CriticApproved)
            return "Not started";
        if (!item.ValidatorCompletedUtc.HasValue)
            return item.ValidatorAttemptCount == 0 ? "Pending" : "Processing";
        if (item.ValidatorFailureCode is not null)
        {
            return SanitizeLifecycleMetadata(
                item.ActualProposalState,
                "Failed");
        }

        return string.Equals(
                item.ProposalProvenance,
                LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                StringComparison.Ordinal)
            ? "SystemValidated"
            : SanitizeLifecycleMetadata(
                item.ActualProposalState,
                "Completed");
    }

    private static string MachineLearningAdmissionResult(
        MachineLearningLifecycleSource item,
        string validatorResult)
    {
        if (!string.Equals(
                validatorResult,
                "SystemValidated",
                StringComparison.Ordinal))
        {
            return "Not started";
        }
        if (!item.AdmissionCompletedUtc.HasValue)
            return item.AdmissionAttemptCount == 0 ? "Pending" : "Processing";

        return SanitizeLifecycleMetadata(
            item.ActualProposalState,
            "Completed");
    }

    private static string SanitizeLifecycleCriticReasons(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "—";

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return "withheld_invalid_critic_result";
            var values = document.RootElement
                .EnumerateArray()
                .Take(17)
                .ToArray();
            if (values.Length > 16 ||
                values.Any(item => item.ValueKind != JsonValueKind.String))
            {
                return "withheld_invalid_critic_result";
            }

            var reasons = values
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => SanitizeLifecycleMetadata(
                    item,
                    "withheld_invalid_critic_result"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return reasons.Length == 0
                ? "—"
                : string.Join(", ", reasons);
        }
        catch (JsonException)
        {
            return "withheld_invalid_critic_result";
        }
    }

    private static string SanitizeLifecycleMetadata(
        string? value,
        string emptyOrInvalid)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Length > 160 ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '_' and not '-' and not '.' and not ':' and not '/'))
        {
            return emptyOrInvalid;
        }

        return normalized;
    }

    private static string SanitizeLifecycleFailure(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "—"
            : SanitizeLifecycleMetadata(
                value,
                "withheld_invalid_diagnostic");

    private static string SanitizeResearchObservationSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "—";
        var normalized = new string(value.Trim()
            .Where(character => !char.IsControl(character))
            .ToArray());
        return normalized[..Math.Min(normalized.Length, 500)];
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string FormatLifecycleTimestamp(DateTime? value) =>
        value?.ToString("u", CultureInfo.InvariantCulture) ?? "—";

    private sealed record MachineLearningLifecycleSource(
        Guid CorrelationId,
        string SourceLanguageCode,
        string TargetLanguageCode,
        string CandidateProvenance,
        string CandidateState,
        int CandidateAttemptCount,
        string? CandidateFailureCode,
        DateTime CandidateCreatedUtc,
        DateTime? CandidateProcessedUtc,
        Guid? ProposalId,
        string? PairKey,
        string? FamilyKey,
        string? ProposalProvenance,
        string? ActualProposalState,
        bool CriticApproved,
        decimal? CriticConfidence,
        string? CriticReasonCodesJson,
        int ValidatorAttemptCount,
        DateTime? ValidatorCompletedUtc,
        string? ValidatorFailureCode,
        int AdmissionAttemptCount,
        DateTime? AdmissionCompletedUtc,
        string? AdmissionFailureCode,
        DateTime? ProposalCreatedUtc,
        DateTime? ProposalUpdatedUtc,
        DateTime SortUpdatedUtc,
        Guid SortId);

    private sealed record AdmittedLifecycleFamilySource(
        string FamilyKey,
        Guid Id);

    private async Task<LegendConnectOperationalState> LoadStateAsync(
        CancellationToken cancellationToken,
        bool includeMetricDetailRecords = false)
    {
        var operationalEvents = _db.Set<LegendConnectOperationalEvent>().AsNoTracking();
        var auditEntries = _db.Set<LegendConnectKnowledgeAuditEntry>().AsNoTracking();
        var founderTrainingSubmissions = _db.Set<LegendFounderTrainingSubmission>().AsNoTracking();
        var founderTrainingSubmissionUnits = _db.Set<LegendFounderTrainingSubmissionUnit>().AsNoTracking();

        // Dashboard and language/pair views show a bounded event feed plus
        // aggregate governance counts. Full immutable evidence is fetched only
        // when a Founder explicitly opens a metric-detail view.
        var dashboardEvents = includeMetricDetailRecords
            ? await operationalEvents.ToListAsync(cancellationToken)
            : await operationalEvents
                .OrderByDescending(item => item.OccurredUtc)
                .Take(50)
                .ToListAsync(cancellationToken);
        IReadOnlyList<LegendConnectKnowledgeAuditEntry> dashboardAuditEntries = includeMetricDetailRecords
            ? await auditEntries.ToListAsync(cancellationToken)
            : Array.Empty<LegendConnectKnowledgeAuditEntry>();
        IReadOnlyList<LegendFounderTrainingSubmission> dashboardFounderTrainingSubmissions = includeMetricDetailRecords
            ? await founderTrainingSubmissions.ToListAsync(cancellationToken)
            : Array.Empty<LegendFounderTrainingSubmission>();
        IReadOnlyList<LegendFounderTrainingSubmissionUnit> dashboardFounderTrainingSubmissionUnits = includeMetricDetailRecords
            ? await founderTrainingSubmissionUnits.ToListAsync(cancellationToken)
            : Array.Empty<LegendFounderTrainingSubmissionUnit>();

        var duplicateOperationalEventCount = includeMetricDetailRecords
            ? dashboardEvents.LongCount(item => item.Category == "DuplicatePrevention" && item.Status == "Prevented")
            : await operationalEvents.LongCountAsync(
                item => item.Category == "DuplicatePrevention" && item.Status == "Prevented",
                cancellationToken);
        var duplicateKnowledgeAuditCount = includeMetricDetailRecords
            ? dashboardAuditEntries.LongCount(item => item.Result == "DuplicatePrevented")
            : await auditEntries.LongCountAsync(item => item.Result == "DuplicatePrevented", cancellationToken);
        var founderTrainingSubmissionCount = includeMetricDetailRecords
            ? dashboardFounderTrainingSubmissions.LongCount()
            : await founderTrainingSubmissions.LongCountAsync(cancellationToken);
        var founderTrainingSubmissionUnitCount = includeMetricDetailRecords
            ? dashboardFounderTrainingSubmissionUnits.LongCount()
            : await founderTrainingSubmissionUnits.LongCountAsync(cancellationToken);
        var retiredLegacyFounderTrainingSubmissionCount = await (
            from submission in founderTrainingSubmissions
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on submission.LegacySourceTextUnitId equals unit.Id
            where submission.LegacySourceTextUnitId != null && !unit.IsTrainingEligible
            select submission.Id)
            .LongCountAsync(cancellationToken);

        return new LegendConnectOperationalState(
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
            dashboardEvents,
            dashboardAuditEntries,
            dashboardFounderTrainingSubmissions,
            dashboardFounderTrainingSubmissionUnits,
            duplicateOperationalEventCount,
            duplicateKnowledgeAuditCount,
            founderTrainingSubmissionCount,
            founderTrainingSubmissionUnitCount,
            retiredLegacyFounderTrainingSubmissionCount);
    }

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

    private static LegendLanguagePair? ResolvePair(IEnumerable<LegendLanguagePair> pairs, string? pairKey) =>
        pairs.SingleOrDefault(item =>
            string.Equals(item.PairKey, pairKey?.Trim(), StringComparison.OrdinalIgnoreCase));

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
        IReadOnlyList<LegendFounderTrainingSubmissionUnit> FounderTrainingSubmissionUnits,
        long DuplicateOperationalEventCount,
        long DuplicateKnowledgeAuditCount,
        long FounderTrainingSubmissionCount,
        long FounderTrainingSubmissionUnitCount,
        long RetiredLegacyFounderTrainingSubmissionCount);

    private sealed record TranslationRouteAuditRow(
        Guid MessageId,
        string? SenderPreferredLanguage,
        string? DetectedLanguage,
        string TargetLanguageCode,
        string Provider,
        DateTime CreatedUtc);

    private sealed record TranslationRouteLearningRow(
        Guid MessageId,
        string SourceLanguageCode,
        string TargetLanguageCode,
        string Provenance,
        string EligibilityState,
        string ProcessingState,
        string? PromotionOutcome,
        DateTime CreatedUtc);

    private sealed record TranslationRouteLedgerRow(
        string RequestReference,
        bool ProviderExecuted,
        bool Succeeded,
        string State,
        string? FailureCode,
        DateTime? CompletedUtc,
        DateTime CreatedUtc);

    private sealed record ProviderRouteOutcomeRow(
        string RequestReference,
        string SourceLanguageCode,
        string TargetLanguageCode,
        bool ProviderExecuted,
        bool Succeeded,
        string State,
        string? FailureCode,
        DateTime? CompletedUtc,
        DateTime CreatedUtc);

    private sealed record TranslationRouteDescription(string Route, string KnowledgeBasis);
}
