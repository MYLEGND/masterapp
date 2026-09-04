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

        // Records this deployment owns are authenticated governed resources and
        // the public internet holds no authority over them. The typed intent
        // was established by the meaning-graph analysis that produced this
        // inference; absent an admitted relation it is Unknown and the request
        // is not diverted here.
        if (internalInference?.OwnedRecordIntent?.Intent ==
            LegendConnectOwnedRecordIntent.OwnedRecordStateInspection)
        {
            return Decision(
                false,
                LegendConnectResearchNeed.NotResearchable,
                "internal_operational_data_requires_governed_tools");
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

        // The typed operational intent is produced by the single meaning-graph
        // analysis below and carried on every finished result, so research
        // classification and Founder tool routing consume one classification
        // instead of re-analyzing or matching text.
        LegendConnectOwnedRecordClassification? ownedRecordIntent = null;

        LegendConnectNativeInferenceSnapshot Finish(
            LegendConnectNativeInferenceSnapshot inference) =>
            WithResearchDecision(
                input ?? string.Empty,
                sourceLanguageCode,
                inference with
                {
                    OwnedRecordIntent = inference.OwnedRecordIntent ?? ownedRecordIntent
                },
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
        ownedRecordIntent = composed.OwnedRecordIntent;
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
            // A contradiction is always a governed boundary. Ambiguity is only
            // a governed boundary once governed evidence was actually
            // selected: with zero selected evidence the authority never
            // established a governed answer, so the request is an unavailable
            // source meaning and remains eligible for the single existing
            // escalation path owned by the conversation service.
            return Finish(NativeInferenceUnsupported(
                composed.Reasons.FirstOrDefault() ?? "semantic_transition_not_governed",
                CanEscalateFromUnprovenSourceAmbiguity(composed)));
        }
        // V20.3: native Founder conversation inference is governed by the
        // reusable meaning-graph authority only.
        //
        // Do not fall backward into the legacy source-frame evaluator when
        // Stage 1 cannot establish reusable meaning. That older path is kept
        // for historical diagnostics/regression compatibility, but it is no
        // longer a production chat authority.
        //
        // Unknown, unproven, ambiguous, contradicted, unresolved-content, and
        // unrealizable meaning all remain fail-closed and may use the existing
        // external escalation path owned by LegendFounderAiConversationService.
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
        return Finish(NativeInferenceUnsupported(
            reasonCode,
            CanEscalateFromUnavailableComposedSource(composed)));
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

    /// <summary>
    /// A composed ambiguity is only a governed boundary once governed evidence
    /// was actually selected. With zero selected evidence the authority never
    /// established a governed answer, so the request is unavailable source
    /// meaning and stays eligible for the single existing escalation path.
    /// A contradiction is never escalatable.
    /// </summary>
    private static bool CanEscalateFromUnprovenSourceAmbiguity(
        LegendSemanticTransitionInference inference) =>
        string.Equals(
            inference.State,
            LegendSemanticTransitionInference.Ambiguous,
            StringComparison.Ordinal) &&
        inference.EvidenceCount <= 0;

    private static bool CanEscalateFromUnavailableComposedSource(
        LegendSemanticTransitionInference inference) =>
        inference.Reasons.FirstOrDefault() is
            "meaning_graph_component_unknown" or
            "meaning_graph_retrieval_bound_exceeded" or
            "meaning_graph_processing_bound_exceeded" or
            "meaning_graph_relation_unproven" or
            "semantic_transition_evidence_unknown" or
            "semantic_transition_not_supported" or
            // The discourse authority returned no conversation state at all.
            // Nothing governed was resolved, refused, or contradicted, so this
            // is an unavailable input rather than a governed boundary. An
            // unresolved, mismatched, or invalid binding stays fail-closed.
            "discourse_reference_state_unavailable";

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
        var languageLineage = new LegendConnectResearchLanguageLineage(
            userLanguage,
            search.ExecutedQueries
                .Select(item => item.QueryLanguageCode ?? item.SourceLanguageCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            documentLanguages,
            userLanguage,
            userLanguage,
            translationReceipts,
            "EvidenceStatementsRequestedInUserLanguage",
            search.Transport);

        return new LegendConnectResearchEvidencePacket(
            search.Transport + "->" + pages.Transport,
            search.Provider,
            search.ModelVersion,
            LegendLanguageIdentity.TextHash(
                search.SettingsIdentity + "|" + pages.SettingsIdentity),
            search.ExecutedQueries,
            search.QueryReceipts,
            pages.Receipts,
            pages.SearchResults,
            pages.Sources,
            pages.Documents,
            claims,
            contradictions,
            pages.Citations,
            languageLineage,
            latencyMilliseconds,
            search.CostMicrounits);
    }

    internal static bool HasCompleteResearchTransportLineage(
        LegendConnectResearchRequest request,
        LegendConnectResearchEvidencePacket result) =>
        ResearchTransportLineageFailure(request, result) is null;

    internal static string? ResearchTransportLineageFailure(
        LegendConnectResearchRequest request,
        LegendConnectResearchEvidencePacket result)
    {
        if (string.IsNullOrWhiteSpace(result.Transport) ||
            string.IsNullOrWhiteSpace(result.SearchProvider) ||
            string.IsNullOrWhiteSpace(result.SettingsIdentity) ||
            result.LatencyMilliseconds < 0 ||
            result.ExecutedQueries.Count is < 1 or > LegendConnectResearchContracts.MaximumQueries ||
            result.SearchQueryReceipts.Count != result.ExecutedQueries.Count ||
            // Receipts include failed/blocked candidate attempts as well as
            // successful documents.  The canonical retriever may examine up
            // to MaximumResults candidates while still returning no more than
            // MaximumDocuments documents.  Capping receipts at the document
            // limit rejected complete fail-closed lineage after ordinary page
            // failures.
            result.PageReceipts.Count < 1 ||
            result.PageReceipts.Count > request.MaximumResults ||
            result.SearchResults.Count > request.MaximumResults ||
            result.Sources.Count > request.MaximumResults ||
            result.Documents.Count > request.MaximumDocuments ||
            result.ClaimEvidence.Count > request.MaximumClaims ||
            result.ContradictingEvidence.Count > request.MaximumClaims ||
            result.Citations.Count > request.MaximumClaims)
        {
            return "internet_research_provenance_packet_bounds_incomplete";
        }

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
                    StringComparison.OrdinalIgnoreCase)))
        {
            return "internet_research_provenance_query_lineage_incomplete";
        }
        var sourceIds = result.Sources
            .Select(item => item.SourceIdentity)
            .ToArray();
        if (sourceIds.Length == 0 ||
            sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Length ||
            result.Sources.Any(item =>
                string.IsNullOrWhiteSpace(item.SourceIdentity) ||
                LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(item.CanonicalUri) is null ||
                !item.IsUntrustedExternalData))
        {
            return "internet_research_provenance_source_lineage_incomplete";
        }
        var sourceSet = sourceIds.ToHashSet(StringComparer.Ordinal);

        if (result.SearchResults.Select(item => item.SearchResultIdentity)
                .Distinct(StringComparer.Ordinal).Count() != result.SearchResults.Count ||
            result.SearchResults.Any(item =>
                !queryIds.Contains(item.QueryIdentity) ||
                !sourceSet.Contains(item.SourceIdentity) ||
                item.Rank < 1 ||
                !item.IsUntrustedExternalData))
        {
            return "internet_research_provenance_search_result_lineage_incomplete";
        }

        var documentIds = result.Documents
            .Select(item => item.DocumentIdentity)
            .ToArray();
        if (documentIds.Distinct(StringComparer.Ordinal).Count() != documentIds.Length ||
            result.Documents.Any(item =>
                !item.RetrievalSucceeded ||
                !sourceSet.Contains(item.SourceIdentity) ||
                string.IsNullOrWhiteSpace(item.ContentExcerpt) ||
                item.ContentExcerpt.Length > request.MaximumDocumentCharacters ||
                item.ReturnedBytes is < 1 or > LegendConnectResearchContracts.MaximumPageBytes ||
                !LegendConnectResearchNetworkPolicy.IsSupportedContentType(item.ContentType) ||
                item.RedirectCount is < 0 or > LegendConnectResearchContracts.MaximumRedirects ||
                !item.IsUntrustedExternalData ||
                !string.Equals(
                    item.ContentHash,
                    LegendLanguageIdentity.TextHash(item.ContentExcerpt),
                    StringComparison.Ordinal)))
        {
            return "internet_research_provenance_document_lineage_incomplete";
        }
        var documentSet = documentIds.ToHashSet(StringComparer.Ordinal);

        var citationIds = result.Citations
            .Select(item => item.CitationIdentity)
            .ToArray();
        if (citationIds.Distinct(StringComparer.Ordinal).Count() != citationIds.Length ||
            result.Citations.Any(item =>
                !sourceSet.Contains(item.SourceIdentity) ||
                !documentSet.Contains(item.DocumentIdentity)))
        {
            return "internet_research_provenance_citation_lineage_incomplete";
        }
        var citationSet = citationIds.ToHashSet(StringComparer.Ordinal);

        if (result.Documents.Sum(item => item.ContentExcerpt.Length) >
                LegendConnectResearchContracts.MaximumTotalDocumentCharacters ||
            result.PageReceipts.Select(item => item.ReceiptIdentity)
                .Distinct(StringComparer.Ordinal).Count() != result.PageReceipts.Count ||
            result.PageReceipts.Any(item =>
                string.IsNullOrWhiteSpace(item.ReceiptIdentity) ||
                LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(item.RequestedCanonicalUri) is null ||
                (item.FinalCanonicalUri is not null &&
                 LegendConnectResearchNetworkPolicy.NormalizePublicHttpUri(item.FinalCanonicalUri) is null) ||
                item.CompletedUtc < item.RequestedUtc ||
                item.RequestCount is < 1 or > LegendConnectResearchContracts.MaximumRedirects + 1 ||
                item.RedirectCount is < 0 or > LegendConnectResearchContracts.MaximumRedirects ||
                item.ReturnedBytes is < 0 or > LegendConnectResearchContracts.MaximumPageBytes ||
                item.LatencyMilliseconds < 0 ||
                string.IsNullOrWhiteSpace(item.CostState) ||
                !item.IsReadOnly ||
                !item.ZeroWrite) ||
            result.PageReceipts.Count(item => item.Succeeded) < result.Documents.Count ||
            result.SearchQueryReceipts.Any(item =>
                !item.IsReadOnly ||
                !item.ZeroWrite ||
                !item.Succeeded ||
                item.FailureReason is not null ||
                string.IsNullOrWhiteSpace(item.CostState) ||
                !string.Equals(item.Provider, result.SearchProvider, StringComparison.Ordinal)) ||
            !string.Equals(
                result.LanguageLineage.UserLanguageCode,
                request.Decision.SourceLanguageCode,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                result.LanguageLineage.EvidenceLanguageCode,
                request.Decision.SourceLanguageCode,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                result.LanguageLineage.FinalResponseLanguageCode,
                request.Decision.SourceLanguageCode,
                StringComparison.OrdinalIgnoreCase) ||
            result.LanguageLineage.QueryLanguageCodes.Any(item =>
                !string.Equals(item, request.Decision.SourceLanguageCode, StringComparison.OrdinalIgnoreCase)))
        {
            return "internet_research_provenance_receipt_or_language_lineage_incomplete";
        }

        var claimLineageComplete = result.ClaimEvidence
                   .Cast<object>()
                   .Concat(result.ContradictingEvidence)
                   .All(item => item switch
                   {
                       LegendConnectClaimEvidence claim =>
                           !LegendConnectResearchExternalDataPolicy.IsPotentialInstruction(claim.Statement) &&
                           !string.IsNullOrWhiteSpace(claim.SupportingExcerpt) &&
                           claim.SupportingExcerpt.Length <= 800 &&
                           !LegendConnectResearchExternalDataPolicy.IsPotentialInstruction(
                               claim.SupportingExcerpt) &&
                           string.Equals(
                               claim.EvidenceLanguageCode,
                               request.Decision.SourceLanguageCode,
                               StringComparison.OrdinalIgnoreCase) &&
                           claim.ExtractionMethod ==
                               LegendConnectResearchExtractionMethod.ModelAssistedProposal &&
                           (claim.PremiseClaimIdentities?.Count ?? 0) <= 3 &&
                           (claim.PremiseClaimIdentities ?? []).All(identity =>
                               !string.IsNullOrWhiteSpace(identity) &&
                               !string.Equals(identity, claim.ClaimIdentity, StringComparison.Ordinal)) &&
                           (claim.StatementKind != LegendConnectResearchStatementKind.Inference ||
                            (claim.Support == LegendConnectResearchEvidenceSupport.Direct &&
                             claim.PremiseClaimIdentities?.Count is >= 2 and <= 3 &&
                             !string.IsNullOrWhiteSpace(claim.DiscriminatingClaimIdentity))) &&
                           (claim.CorrectsSourceIdentity is null ||
                            sourceSet.Contains(claim.CorrectsSourceIdentity)) &&
                           sourceSet.Contains(claim.SourceIdentity) &&
                           documentSet.Contains(claim.DocumentIdentity) &&
                           citationSet.Contains(claim.CitationIdentity),
                       LegendConnectContradictingEvidence contradiction =>
                           !LegendConnectResearchExternalDataPolicy.IsPotentialInstruction(contradiction.Statement) &&
                           !string.IsNullOrWhiteSpace(contradiction.SupportingExcerpt) &&
                           contradiction.SupportingExcerpt.Length <= 800 &&
                           !LegendConnectResearchExternalDataPolicy.IsPotentialInstruction(
                               contradiction.SupportingExcerpt) &&
                           string.Equals(
                               contradiction.EvidenceLanguageCode,
                               request.Decision.SourceLanguageCode,
                               StringComparison.OrdinalIgnoreCase) &&
                           contradiction.ExtractionMethod ==
                               LegendConnectResearchExtractionMethod.ModelAssistedProposal &&
                           (contradiction.PremiseClaimIdentities?.Count ?? 0) <= 3 &&
                           (contradiction.PremiseClaimIdentities ?? []).All(identity =>
                               !string.IsNullOrWhiteSpace(identity) &&
                               !string.Equals(
                                   identity,
                                   contradiction.ClaimIdentity,
                                   StringComparison.Ordinal)) &&
                           (contradiction.StatementKind != LegendConnectResearchStatementKind.Inference ||
                            (contradiction.Support == LegendConnectResearchEvidenceSupport.Direct &&
                             contradiction.PremiseClaimIdentities?.Count is >= 2 and <= 3 &&
                             !string.IsNullOrWhiteSpace(
                                 contradiction.DiscriminatingClaimIdentity))) &&
                           (contradiction.CorrectsSourceIdentity is null ||
                            sourceSet.Contains(contradiction.CorrectsSourceIdentity)) &&
                           sourceSet.Contains(contradiction.SourceIdentity) &&
                           documentSet.Contains(contradiction.DocumentIdentity) &&
                           citationSet.Contains(contradiction.CitationIdentity),
                       _ => false
                   });
        return claimLineageComplete
            ? null
            : "internet_research_provenance_claim_lineage_incomplete";
    }

    private LegendConnectResearchProvenance BuildResearchProvenance(
        LegendConnectResearchRequest request,
        LegendConnectResearchSession session,
        LegendConnectResearchEvidenceOrigin origin,
        string transport,
        string? model,
        string settings,
        string? searchProvider = null)
    {
        var configuredCodeSha =
            (_configuration["LegendConnect:Research:CodeSha"] ??
             _configuration["LegendConnect:ModelEvaluation:CodeSha"] ??
             string.Empty).Trim();
        var codeSha = IsLowerHex(configuredCodeSha, 40)
            ? configuredCodeSha
            : "Unavailable";
        var configurationIdentity = LegendLanguageIdentity.TextHash(
            string.Join(
                "|",
                "legend-research-configuration:v1",
                codeSha,
                transport,
                searchProvider ?? "Unavailable",
                settings,
                LegendConnectResearchEvidenceAdmissibilityPolicy.PolicyIdentity,
                LegendConnectResearchContracts.ClaimEvidencePolicy,
                LegendConnectResearchContracts.CitationPresentationPolicy,
                LegendConnectResearchContracts.MaximumQueries,
                LegendConnectResearchContracts.MaximumResults,
                LegendConnectResearchContracts.MaximumDocuments,
                LegendConnectResearchContracts.MaximumClaims,
                LegendConnectResearchContracts.MaximumDocumentCharacters,
                LegendConnectResearchContracts.MaximumTotalDocumentCharacters,
                LegendConnectResearchContracts.MaximumRedirects,
                LegendConnectResearchContracts.MaximumPageBytes,
                LegendConnectResearchContracts.RequestTimeoutSeconds,
                LegendConnectResearchContracts.TotalResearchDeadlineSeconds,
                request.MaximumResults,
                request.MaximumDocuments,
                request.MaximumClaims,
                request.MaximumDocumentCharacters,
                request.MinimumIndependentSources));
        return new(
            request.RequestId,
            session.SessionId,
            request.Decision.ReasonCode,
            request.Decision.SourceLanguageCode,
            LegendLanguageIdentity.TextHash(request.Question),
            request.RequestedUtc,
            origin,
            request.InternalReasonCode,
            request.InternalEvidenceCount,
            transport,
            model,
            settings,
            session.Queries.Select(item => item.QueryIdentity).ToArray(),
            session.Sources.Select(item => item.SourceIdentity).ToArray(),
            session.Documents.Select(item => item.DocumentIdentity).ToArray(),
            session.ClaimEvidence.Select(item => item.EvidenceIdentity).ToArray(),
            session.ContradictingEvidence.Select(item => item.EvidenceIdentity).ToArray(),
            session.Citations.Select(item => item.CitationIdentity).ToArray(),
            session.StartedUtc,
            session.CompletedUtc,
            session.LatencyMilliseconds,
            session.CostMicrounits,
            session.CostMicrounits.HasValue ? "Measured" : "Unavailable",
            request.Authorization.AuthorizationProvenance,
            request.Authorization.AuthorizationCorrelationId,
            request.Authorization.IsReadOnly,
            request.Authorization.ZeroWrite,
            LegendConnectResearchContracts.Provenance,
            searchProvider,
            session.SearchQueryReceipts?.Select(item => item.ReceiptIdentity).ToArray() ?? [],
            session.PageReceipts?.Select(item => item.ReceiptIdentity).ToArray() ?? [],
            session.LanguageLineage,
            session.EvidencePolicyIdentity,
            session.EvidenceAdmissibility,
            session.MaterialClaimEvidence?.Select(item => item.EvidenceIdentity).ToArray() ?? [],
            session.ClaimResolutions,
            session.ClaimEvidencePolicyIdentity,
            session.CitationValidation,
            session.CitationValidation is null
                ? null
                : LegendConnectResearchContracts.CitationPresentationPolicy,
            codeSha,
            configurationIdentity);
    }

    /// <summary>
    /// Observational Stage 4 boundary. It deliberately returns governed result
    /// meaning before any surface realization and is not invoked by serving.
    /// </summary>
    public Task<LegendConnectResponseMeaningPlanResult> TryPlanConversationAsync(
        string input,
        LegendConnectDiscourseStateSnapshot? discourseState,
        CancellationToken cancellationToken = default,
        string sourceLanguageCode = "en") =>
        Curriculum.TryPlanResponseMeaningAsync(
            sourceLanguageCode,
            input ?? string.Empty,
            discourseState,
            cancellationToken);

    public Task<LegendConnectContentBoundResponseMeaningPlanResult>
        TryBindConversationContentAsync(
            string input,
            LegendConnectDiscourseStateSnapshot? discourseState,
            CancellationToken cancellationToken = default,
            string sourceLanguageCode = "en") =>
        Curriculum.TryBindResponseContentAsync(
            sourceLanguageCode,
            input ?? string.Empty,
            discourseState,
            cancellationToken);

    private static LegendConnectNativeInferenceSnapshot NativeInferenceUnsupported(
        string reasonCode,
        bool requiresEscalation = true) => new(
        false,
        0m,
        null,
        reasonCode,
        0,
        "LEGEND could not establish one independently supported, contradiction-free semantic transition and original compositional realization for this request.",
        requiresEscalation,
        "Unavailable",
        "Unavailable",
        ModelAssistance: DormantModelAssistance(
            "symbolic_authority_not_supported"));

    /// <summary>
    /// Stage-2 observational composition through the one canonical curriculum
    /// authority. This is intentionally not an alternate inference path and
    /// cannot authorize a response.
    /// </summary>
    public Task<LegendConnectUtteranceMeaningGraphSnapshot> AnalyzeReusableMeaningGraphAsync(
        string input,
        CancellationToken cancellationToken = default,
        string sourceLanguageCode = "en") =>
        Curriculum.AnalyzeReusableMeaningGraphAsync(
            sourceLanguageCode,
            input ?? string.Empty,
            cancellationToken);

    public Task<IReadOnlyList<LegendConnectDiscourseReferenceRuleSnapshot>>
        GetProductionDiscourseReferenceRulesAsync(
            string sourceLanguageCode,
            IReadOnlyList<string> selectorSemanticSignatures,
            CancellationToken cancellationToken = default) =>
        Curriculum.GetProductionDiscourseReferenceRulesAsync(
            sourceLanguageCode,
            selectorSemanticSignatures,
            cancellationToken);

    public async Task<LegendConnectRetainedKnowledgeSearchSnapshot>
        SearchRetainedKnowledgeAsync(
            string query,
            string? sourceLanguageCode = null,
            string? targetLanguageCode = null,
            int take = 12,
            CancellationToken cancellationToken = default)
    {
        var normalizedQuery =
            LegendLanguageIdentity.NormalizeText(
                query ?? string.Empty);

        if (string.IsNullOrWhiteSpace(
                normalizedQuery))
        {
            return new LegendConnectRetainedKnowledgeSearchSnapshot(
                string.Empty,
                0,
                []);
        }

        var boundedTake =
            Math.Clamp(
                take,
                1,
                64);

        var queryComponents = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim(
                '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}'))
            .Where(item => item.Length >= 2 && item.Any(char.IsLetterOrDigit))
            .Select(item => item.Normalize().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        if (queryComponents.Length == 0)
        {
            return new LegendConnectRetainedKnowledgeSearchSnapshot(
                normalizedQuery,
                0,
                []);
        }
        var queryLexemeHashes = queryComponents
            .Select(LegendLanguageIdentity.TextHash)
            .ToArray();

        var sourceLanguage = string.IsNullOrWhiteSpace(sourceLanguageCode)
            ? null
            : await _registry.NormalizeEnabledTranslationLanguageAsync(
                sourceLanguageCode,
                cancellationToken);
        if (!string.IsNullOrWhiteSpace(sourceLanguageCode) && sourceLanguage is null)
        {
            return new LegendConnectRetainedKnowledgeSearchSnapshot(normalizedQuery, 0, []);
        }
        var targetLanguage = string.IsNullOrWhiteSpace(targetLanguageCode)
            ? null
            : await _registry.NormalizeEnabledTranslationLanguageAsync(
                targetLanguageCode,
                cancellationToken);
        if (!string.IsNullOrWhiteSpace(targetLanguageCode) && targetLanguage is null)
        {
            return new LegendConnectRetainedKnowledgeSearchSnapshot(normalizedQuery, 0, []);
        }

        var retrievalLanguages = sourceLanguage is not null
            ? new[] { sourceLanguage! }
            : await _db.Set<LegendLanguageDefinition>()
                .AsNoTracking()
                .Where(item => item.IsEnabled)
                .OrderBy(item => item.LanguageCode)
                .Select(item => item.LanguageCode)
                .Take(MaximumRetainedRetrievalLanguages + 1)
                .ToArrayAsync(cancellationToken);
        if (retrievalLanguages.Length == 0 ||
            retrievalLanguages.Length > MaximumRetainedRetrievalLanguages)
        {
            return new LegendConnectRetainedKnowledgeSearchSnapshot(normalizedQuery, 0, []);
        }

        var candidateBudget = Math.Min(
            MaximumRetainedSemanticCandidates,
            Math.Max(64, boundedTake * 8));
        var semanticCandidates = await (
            from lexeme in _db.Set<LegendLanguageLexeme>().AsNoTracking()
            join occurrence in _db.Set<LegendLanguageLexicalOccurrence>().AsNoTracking()
                on lexeme.Id equals occurrence.LexemeId
            join anchor in _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
                on occurrence.TextUnitId equals anchor.TextUnitId
            join node in _db.Set<LegendLanguageMeaningNodeEvidence>().AsNoTracking()
                on anchor.Id equals node.CompositionalAnchorId
            join primitive in _db.Set<LegendLanguageMeaningPrimitive>().AsNoTracking()
                on new { node.LanguageCode, node.SemanticSignature }
                equals new { primitive.LanguageCode, primitive.SemanticSignature }
            join example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                on node.CurriculumExampleId equals example.Id
            where retrievalLanguages.Contains(lexeme.LanguageCode) &&
                queryLexemeHashes.Contains(lexeme.NormalizedHash) &&
                occurrence.SupersededUtc == null &&
                anchor.LanguageCode == lexeme.LanguageCode &&
                anchor.SupersededUtc == null &&
                anchor.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                node.LanguageCode == lexeme.LanguageCode &&
                node.SupersededUtc == null &&
                node.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                primitive.SupersededUtc == null &&
                primitive.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                primitive.MaturityState != "Contradicted" &&
                primitive.ContradictionCount == 0 &&
                primitive.IndependentSourceCount >= 1 &&
                primitive.HumanVerifiedSupportCount >= 1 &&
                example.SupersededUtc == null &&
                example.LanguageCode == lexeme.LanguageCode &&
                example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
            select new RetainedSemanticCandidate(
                example.CurriculumFamilyId,
                node.SemanticSignature,
                lexeme.NormalizedHash)
        ).Distinct()
            .OrderBy(item => item.CurriculumFamilyId)
            .ThenBy(item => item.SemanticSignature)
            .ThenBy(item => item.LexemeHash)
            .Take(MaximumRetainedSemanticCandidates)
            .ToArrayAsync(cancellationToken);

        var rankedFamilies = semanticCandidates
            .GroupBy(item => item.CurriculumFamilyId)
            .Select(group => new
            {
                CurriculumFamilyId = group.Key,
                Match = group.Select(item => item.LexemeHash).Distinct(StringComparer.Ordinal).Count(),
                PrimitiveCount = group.Select(item => item.SemanticSignature).Distinct(StringComparer.Ordinal).Count()
            })
            .OrderByDescending(item => item.Match)
            .ThenByDescending(item => item.PrimitiveCount)
            .ThenBy(item => item.CurriculumFamilyId)
            .Take(candidateBudget)
            .ToArray();
        var familyIds = rankedFamilies.Select(item => item.CurriculumFamilyId).ToArray();
        var matchByFamily = rankedFamilies.ToDictionary(
            item => item.CurriculumFamilyId,
            item => item.Match);

        var exactQueryHash = LegendLanguageIdentity.TextHash(normalizedQuery);
        var exactUnits = await _db.Set<LegendLanguageTextUnit>()
            .AsNoTracking()
            .Where(item => retrievalLanguages.Contains(item.LanguageCode) &&
                item.NormalizedHash == exactQueryHash &&
                item.IsTrainingEligible &&
                (item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved ||
                 item.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine))
            .OrderByDescending(item => item.UpdatedUtc)
            .ThenBy(item => item.Id)
            .Take(candidateBudget)
            .ToArrayAsync(cancellationToken);
        var familyUnits = familyIds.Length == 0
            ? Array.Empty<RetainedFamilyTextUnit>()
            : await (
                from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on example.TextUnitId equals unit.Id
                where familyIds.Contains(example.CurriculumFamilyId) &&
                    retrievalLanguages.Contains(example.LanguageCode) &&
                    example.SupersededUtc == null &&
                    (example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved ||
                     example.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine) &&
                    unit.IsTrainingEligible &&
                    (unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved ||
                     unit.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine)
                orderby unit.UpdatedUtc descending, unit.Id
                select new RetainedFamilyTextUnit(
                    example.CurriculumFamilyId,
                    unit.Id,
                    unit.LanguageCode,
                    unit.Text,
                    unit.Provenance,
                    unit.UpdatedUtc)
            ).Take(candidateBudget).ToArrayAsync(cancellationToken);

        var units = familyUnits
            .Concat(exactUnits.Select(unit => new RetainedFamilyTextUnit(
                Guid.Empty,
                unit.Id,
                unit.LanguageCode,
                unit.Text,
                unit.Provenance,
                unit.UpdatedUtc)))
            .GroupBy(item => item.TextUnitId)
            .Select(group => group
                .OrderByDescending(item => item.CurriculumFamilyId != Guid.Empty)
                .First())
            .Take(candidateBudget)
            .ToArray();

        var candidateUnitIds = units.Select(item => item.TextUnitId).ToArray();
        var pairKey = sourceLanguage is not null && targetLanguage is not null
            ? LegendLanguageIdentity.PairKey(sourceLanguage, targetLanguage)
            : null;
        var alignments = pairKey is null || candidateUnitIds.Length == 0
            ? Array.Empty<RetainedAlignmentCandidate>()
            : await (
                from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
                join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on alignment.SourceTextUnitId equals source.Id
                join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on alignment.TargetTextUnitId equals target.Id
                where alignment.PairKey == pairKey &&
                    alignment.SupersededUtc == null &&
                    candidateUnitIds.Contains(alignment.SourceTextUnitId) &&
                    source.LanguageCode == sourceLanguage &&
                    target.LanguageCode == targetLanguage &&
                    source.IsTrainingEligible &&
                    target.IsTrainingEligible &&
                    (alignment.HumanVerified ||
                     alignment.QualityState == "SystemValidated" ||
                     alignment.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived)
                orderby alignment.UpdatedUtc descending, alignment.Id
                select new RetainedAlignmentCandidate(
                    alignment.Id,
                    alignment.PairKey,
                    alignment.Provenance,
                    alignment.QualityState,
                    alignment.HumanVerified,
                    alignment.Confidence,
                    alignment.SourceTextUnitId,
                    source.LanguageCode,
                    source.Text,
                    target.Text,
                    alignment.UpdatedUtc)
            ).Take(candidateBudget).ToArrayAsync(cancellationToken);

        var alignmentIds = alignments.Select(item => item.Id).ToArray();

        var contradictedIds =
            alignmentIds.Length == 0
                ? new HashSet<Guid>()
                : (
                    await _db
                        .Set<LegendTranslationQualityEvidence>()
                        .AsNoTracking()
                        .Where(item =>
                            alignmentIds.Contains(
                                item.ObservedAlignmentId) &&
                            item.Signal ==
                                "Contradictory" &&
                            item.ResolutionState ==
                                "Open" &&
                            item.SupersededUtc ==
                                null)
                        .Select(item =>
                            item.ObservedAlignmentId)
                        .Distinct()
                        .ToListAsync(
                            cancellationToken)
                ).ToHashSet();

        // MachineProposed artifacts are intentionally absent. A model row is
        // relevant only for the exact requested directional pair; query text
        // similarity can never imply that a model is active or authoritative.
        var activeModel = pairKey is null
            ? null
            : await _db.Set<LegendLanguagePair>()
                .AsNoTracking()
                .Where(item => item.PairKey == pairKey &&
                    item.IsEnabled &&
                    item.ActiveModelVersion != null)
                .SingleOrDefaultAsync(cancellationToken);

        var scored =
            new List<(
                int Match,
                LegendConnectRetainedKnowledgeItemSnapshot Item)>();

        foreach (var unit in units)
        {
            var match = unit.CurriculumFamilyId == Guid.Empty
                ? queryComponents.Length + 1
                : matchByFamily.GetValueOrDefault(unit.CurriculumFamilyId);
            if (match <= 0) continue;

            var founder =
                string.Equals(
                    unit.Provenance,
                    LegendConnectKnowledgeProvenance.FounderApproved,
                    StringComparison.Ordinal);

            scored.Add(
                (
                    match,
                    new LegendConnectRetainedKnowledgeItemSnapshot(
                        "CanonicalText",
                        founder
                            ? "FounderApproved"
                            : "SystemValidatedMachine",
                        founder ? 95 : 88,
                        unit.Provenance,
                        unit.LanguageCode,
                        null,
                        unit.Text,
                        null,
                        founder ? 1m : 0.98m,
                        true,
                        false,
                        null,
                        unit.UpdatedUtc)
                ));
        }

        foreach (var row in alignments)
        {
            var unit = units.First(item => item.TextUnitId == row.SourceTextUnitId);
            var match = unit.CurriculumFamilyId == Guid.Empty
                ? queryComponents.Length + 1
                : matchByFamily.GetValueOrDefault(unit.CurriculumFamilyId);
            if (match <= 0) continue;

            var contradicted =
                contradictedIds.Contains(
                    row.Id);

            var systemValidated =
                string.Equals(
                    row.QualityState,
                    "SystemValidated",
                    StringComparison.Ordinal);

            var authority =
                row.HumanVerified
                    ? "HumanVerified"
                    : systemValidated
                        ? "SystemValidatedMachine"
                        : row.QualityState;

            var rank =
                row.HumanVerified
                    ? 100
                    : systemValidated
                        ? 90
                        : contradicted
                            ? 15
                            : 50;

            scored.Add(
                (
                    match,
                    new LegendConnectRetainedKnowledgeItemSnapshot(
                        "DirectionalAlignment",
                        authority,
                        rank,
                        row.Provenance,
                        row.SourceLanguage,
                        row.PairKey,
                        row.SourceText,
                        row.TargetText,
                        row.Confidence,
                        row.HumanVerified ||
                        systemValidated,
                        contradicted,
                        null,
                        row.UpdatedUtc)
                ));
        }

        if (activeModel is not null)
        {
            scored.Add(
                (
                    1,
                    new LegendConnectRetainedKnowledgeItemSnapshot(
                        "ActiveModel",
                        "Promoted",
                        80,
                        "GovernedModelPromotion",
                        activeModel.SourceLanguageCode,
                        activeModel.PairKey,
                        "Promoted LEGEND neural model for this directional pair.",
                        null,
                        null,
                        true,
                        false,
                        activeModel.ActiveModelVersion,
                        activeModel.UpdatedUtc)
                ));
        }

        var selected =
            scored
                .OrderByDescending(item =>
                    item.Match)
                .ThenByDescending(item =>
                    item.Item.AuthorityRank)
                .ThenByDescending(item =>
                    item.Item.UpdatedUtc)
                .ThenBy(item =>
                    item.Item.Kind,
                    StringComparer.Ordinal)
                .ThenBy(item =>
                    item.Item.Content,
                    StringComparer.Ordinal)
                .Take(boundedTake)
                .Select(item =>
                    item.Item)
                .ToArray();

        return new LegendConnectRetainedKnowledgeSearchSnapshot(
            normalizedQuery,
            selected.Length,
            selected);
    }

    private sealed record RetainedSemanticCandidate(
        Guid CurriculumFamilyId,
        string SemanticSignature,
        string LexemeHash);

    private sealed record RetainedFamilyTextUnit(
        Guid CurriculumFamilyId,
        Guid TextUnitId,
        string LanguageCode,
        string Text,
        string Provenance,
        DateTime UpdatedUtc);

    private sealed record RetainedAlignmentCandidate(
        Guid Id,
        string PairKey,
        string Provenance,
        string QualityState,
        bool HumanVerified,
        decimal? Confidence,
        Guid SourceTextUnitId,
        string SourceLanguage,
        string SourceText,
        string TargetText,
        DateTime UpdatedUtc);

    private static string BoundRetainedKnowledge(
        string value,
        int maximumLength) =>
        value.Length <= maximumLength
            ? value
            : value[..maximumLength] +
              "\n[BOUNDED]";

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

    /// <summary>
    /// Returns the underlying, privacy-safe records for one dashboard metric.
    /// The dashboard values and this detail projection deliberately read the
    /// same ledgers, corpus lineage, and operational evidence; this is a read
    /// surface only and does not create another metrics authority.
    /// </summary>
    public async Task<LegendConnectMetricDetailSnapshot> GetMetricDetailAsync(
        string? metricKey,
        CancellationToken cancellationToken = default)
    {
        var key = metricKey?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key))
            return EmptyMetricDetail("unknown", "Metric details", "A Legend Connect metric was not specified.");

        if (key == "translation-routing-audit")
            return await BuildTranslationRoutingAuditMetricDetailAsync(cancellationToken);

        if (key.StartsWith("capacity-", StringComparison.Ordinal) ||
            key is "azure-characters-used" or "consumed-live-characters" or "consumed-corpus-characters" or
                "provider-characters-reserved")
            return await BuildCapacityMetricDetailAsync(key, cancellationToken);

        if (key is "provider-operations" or "provider-billable-characters" or "same-language-avoided" or
            "memory-avoided" or "structural-avoided" or "context-avoided" or "promoted-translation-model-avoided" or
            "provider-observation-avoided" or "quota-denied" or "provider-failures" or
            "group-target-reuse" or "high-consumption-accounts")
            return await BuildUsageMetricDetailAsync(key, cancellationToken);

        var state = await LoadStateAsync(cancellationToken, includeMetricDetailRecords: true);
        return key switch
        {
            "active-languages" => BuildLanguageMetricDetail(state),
            "directional-pairs" => BuildPairMetricDetail(state),
            "learning-failures" => BuildLearningFailureMetricDetail(state),
            "duplicate-prevention" or "readiness-duplicates-prevented" => BuildDuplicateMetricDetail(state, key),
            "approved-candidates" or "eligible-pending" or "rejected-ineligible" or "pairs-awaiting-knowledge" => BuildCandidateMetricDetail(state, key),
            "same-language-bypasses" or "cross-language-translation-requests" or "translation-memory-hits" or "provider-fallback-required" or "trusted-structural-served" or "trusted-contextual-served" or "promoted-translation-model-served" or "promoted-translation-model-failures" or "provider-observation-reused" or "native-translation-intelligence-served" or "translation-routing-reconciliation" or "internal-coverage" or "provider-avoidance" or "provider-dependency" => BuildDemandMetricDetail(state, key),
            "pending-learning-jobs" => BuildPendingLearningMetricDetail(state),
            "quality-needs-review" or "quality-provider-observations" or "quality-supported-observations" or "quality-contradictions" or "quality-human-verified" => await BuildQualityMetricDetailAsync(state, key, cancellationToken),
            "consented-accounts" or "eligible-live-translations" or "promoted-to-learning" or "canonical-reuse-prevented-duplicates" or "awaiting-corpus-processing" => BuildConsentedLearningMetricDetail(state, key),
            "raw-submissions-retained" or "atomic-learning-units" or "active-directional-alignments" or "legacy-multi-unit-assets-retired" => BuildFounderTrainingMetricDetail(state, key),
            _ => EmptyMetricDetail(key, "Metric details", "This card has no configured Legend Connect detail projection.")
        };
    }

    /// <summary>
    /// Projects the actual persisted route of each completed message translation
    /// from the operational presentation cache, then joins only the existing
    /// privacy-governed learning hand-off and provider usage ledger. This is a
    /// read-only explanation of the single router's outcome; it never stores
    /// message bodies, participant identities, or a parallel route authority.
    /// </summary>
    private async Task<LegendConnectMetricDetailSnapshot> BuildTranslationRoutingAuditMetricDetailAsync(
        CancellationToken cancellationToken)
    {
        var routes = await (
                from translation in _db.MessageTranslations.AsNoTracking()
                join message in _db.InternalMessages.AsNoTracking()
                    on translation.InternalMessageId equals message.Id
                where !message.IsDeleted
                orderby translation.CreatedUtc descending
                select new TranslationRouteAuditRow(
                    message.Id,
                    message.SenderPreferredLanguage,
                    message.OriginalLanguage,
                    translation.TargetLanguage,
                    translation.Provider,
                    translation.CreatedUtc))
            .Take(TranslationRouteAuditRecordLimit)
            .ToListAsync(cancellationToken);

        var messageIds = routes.Select(item => item.MessageId).Distinct().ToArray();
        var learningEvents = messageIds.Length == 0
            ? new List<TranslationRouteLearningRow>()
            : await _db.Set<LegendTranslationLearningEvent>().AsNoTracking()
                .Where(item => item.SourceMessageId != null && messageIds.Contains(item.SourceMessageId.Value))
                .Select(item => new TranslationRouteLearningRow(
                    item.SourceMessageId!.Value,
                    item.SourceLanguageCode,
                    item.TargetLanguageCode,
                    item.Provenance,
                    item.EligibilityState,
                    item.ProcessingState,
                    item.PromotionOutcome,
                    item.CreatedUtc))
                .ToListAsync(cancellationToken);
        var learningByRoute = learningEvents
            .GroupBy(item => TranslationRouteKey(item.MessageId, item.TargetLanguageCode), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.CreatedUtc).First(),
                StringComparer.Ordinal);

        var references = routes
            .Select(item => TranslationUsageReference.ForMessage(item.MessageId, item.TargetLanguageCode))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var ledgerByReference = references.Length == 0
            ? new Dictionary<string, TranslationRouteLedgerRow>(StringComparer.Ordinal)
            : (await _db.Set<LegendTranslationUsageLedger>().AsNoTracking()
                    .Where(item => references.Contains(item.RequestReference))
                    .Select(item => new TranslationRouteLedgerRow(
                        item.RequestReference,
                        item.ProviderExecuted,
                        item.Succeeded,
                        item.State,
                        item.FailureCode,
                        item.CompletedUtc,
                        item.CreatedUtc))
                    .ToListAsync(cancellationToken))
                .GroupBy(item => item.RequestReference, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => item.CompletedUtc ?? item.CreatedUtc).First(),
                    StringComparer.Ordinal);

        var routeRows = routes.Select(item =>
        {
            var reference = TranslationUsageReference.ForMessage(item.MessageId, item.TargetLanguageCode);
            learningByRoute.TryGetValue(TranslationRouteKey(item.MessageId, item.TargetLanguageCode), out var learning);
            ledgerByReference.TryGetValue(reference, out var ledger);
            var route = DescribeTranslationRoute(item.Provider);
            return new[]
            {
                reference,
                RoutedSourceLanguage(item, learning),
                item.TargetLanguageCode,
                route.Route,
                AzureInvocation(item.Provider, ledger),
                route.KnowledgeBasis,
                LearningHandoff(learning),
                Display(item.CreatedUtc)
            };
        });

        var providerOutcomeRecords = await _db.Set<LegendTranslationUsageLedger>().AsNoTracking()
            .Where(item => item.Provider == "AzureTranslator")
            .OrderByDescending(item => item.CompletedUtc ?? item.CreatedUtc)
            .Take(TranslationRouteAuditRecordLimit)
            .Select(item => new ProviderRouteOutcomeRow(
                item.RequestReference,
                item.SourceLanguageCode,
                item.TargetLanguageCode,
                item.ProviderExecuted,
                item.Succeeded,
                item.State,
                item.FailureCode,
                item.CompletedUtc,
                item.CreatedUtc))
            .ToListAsync(cancellationToken);
        var providerOutcomes = providerOutcomeRecords.Select(item => new[]
        {
            item.RequestReference,
            item.SourceLanguageCode,
            item.TargetLanguageCode,
            item.ProviderExecuted ? "Called" : "Not called",
            item.Succeeded ? "Succeeded" : item.State,
            item.FailureCode ?? string.Empty,
            Display(item.CompletedUtc ?? item.CreatedUtc)
        });

        return Detail(
            "translation-routing-audit",
            "Translation route audit",
            "Canonical router, persisted translation, learning, and usage authorities",
            "Completed translations show the actual persisted route. Azure invocation is cross-checked against the existing one-way usage ledger when one exists. Learning status is the existing consent-governed hand-off only; this view never exposes message bodies or account identities.",
            Section(
                "Completed translation routes",
                $"Newest {TranslationRouteAuditRecordLimit} persisted translation results. The request reference is a one-way server identifier; routed source prefers the sender preference captured at send time, then the canonical learning hand-off, then only persisted detection metadata.",
                new[] { "Request reference", "Routed source", "Target", "Actual route", "Azure invocation", "Knowledge basis", "Learning hand-off", "Completed" },
                routeRows),
            Section(
                "Azure fallback outcomes",
                "Recent Azure-accounting outcomes from the existing usage ledger, including denied and failed attempts that cannot create a completed translation result.",
                new[] { "Request reference", "Source", "Target", "Azure invocation", "Outcome", "Failure", "Completed" },
                providerOutcomes));
    }

    private async Task<LegendConnectMetricDetailSnapshot> BuildCapacityMetricDetailAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetProviderCapacityAsync(cancellationToken);
        var capacities = await _db.Set<LegendTranslationProviderCapacity>().AsNoTracking()
            .Where(item => item.Provider == "AzureTranslator")
            .OrderByDescending(item => item.BillingPeriodStart)
            .ToListAsync(cancellationToken);
        var reservations = await _db.Set<LegendTranslationProviderReservation>().AsNoTracking()
            .Where(item => item.Provider == "AzureTranslator")
            .OrderByDescending(item => item.CreatedUtc)
            .ToListAsync(cancellationToken);
        var currentCapacity = capacities.FirstOrDefault(item => item.BillingPeriodStart == snapshot.BillingPeriodStart);

        var fields = new[]
        {
            new[] { "Selected metric", CapacityValueFor(key, snapshot, currentCapacity) },
            new[] { "Resource", snapshot.ResourceName ?? "Azure Translator" },
            new[] { "Resource tier", snapshot.Tier ?? "Unavailable" },
            new[] { "Synchronization", snapshot.Status },
            new[] { "Billing period", $"{snapshot.BillingPeriodStart:yyyy-MM-dd} through {snapshot.BillingPeriodEnd:yyyy-MM-dd}" },
            new[] { "Monthly allowance", Display(snapshot.MonthlyIncludedCharacterAllowance) },
            new[] { "Monthly consumed", Display(snapshot.MonthlyCharactersConsumed) },
            new[] { "Monthly reserved", Display(snapshot.MonthlyReservedCharacters) },
            new[] { "Monthly remaining", Display(snapshot.MonthlyRemainingCharacters) },
            new[] { "Protected live reserve", Display(snapshot.MonthlyLiveReserveCharacters) },
            new[] { "Maximum safe corpus", Display(snapshot.MaximumSafeCorpusConsumptionCharacters) },
            new[] { "Hourly window", $"{snapshot.HourlyWindowStartUtc:u} through {snapshot.HourlyWindowEndUtc:u}" },
            new[] { "Hourly limit", Display(snapshot.HourlyCharacterLimit) },
            new[] { "Hourly consumed", Display(snapshot.HourlyCharactersConsumed) },
            new[] { "Hourly reserved", Display(snapshot.HourlyReservedCharacters) },
            new[] { "Hourly remaining", Display(snapshot.HourlyRemainingCharacters) },
            new[] { "Safe acquisition now", Display(snapshot.SafeAcquisitionCharacters) },
            new[] { "Last synchronized", snapshot.RefreshedUtc.ToString("u", CultureInfo.InvariantCulture) }
        };

        return Detail(key, TitleFor(key), "Azure Translator capacity authority",
            snapshot.Detail ?? "The selected value is calculated from the synchronized provider subscription and the canonical capacity reservation ledger.",
            Section("Live capacity projection", "The exact current subscription and capacity values used by this metric.",
                new[] { "Field", "Value" }, fields),
            Section("Monthly capacity ledger", "Persisted billing-period capacity rows used by the planner.",
                new[] { "Period", "Configured", "Live consumed", "Corpus consumed", "Reserved", "Updated" },
                capacities.Select(item => new[]
                {
                    item.BillingPeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Display(item.ConfiguredCapacityCharacters),
                    Display(item.LiveCharactersConsumed), Display(item.BootstrapCharactersConsumed + item.TrainingCharactersConsumed),
                    Display(item.ReservedLiveCharacters), item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture)
                })),
            Section("Capacity reservations", "Individual provider-capacity reservations. These are operational reservations, not translated message content.",
                new[] { "Reference", "Purpose", "Characters", "State", "Created", "Completed" },
                reservations.Select(item => new[]
                {
                    item.ReservationReference, item.Purpose, Display(item.Characters), item.State,
                    item.CreatedUtc.ToString("u", CultureInfo.InvariantCulture), Display(item.CompletedUtc)
                })));
    }

    private static LegendConnectMetricDetailSnapshot BuildLanguageMetricDetail(LegendConnectOperationalState state) =>
        Detail("active-languages", "Active languages", "Language registry authority",
            "Each row is one enabled language definition and its current canonical dataset identity.",
            Section("Enabled language records", "The server-owned language registry records behind the count.",
                new[] { "Language", "Name", "Dataset namespace", "Storage partition", "Translation", "Learning", "Updated" },
                state.Languages.Where(item => item.IsEnabled).OrderBy(item => item.CanonicalName).Select(item => new[]
                {
                    item.LanguageCode, item.CanonicalName, item.DatasetNamespace, item.StoragePartition,
                    YesNo(item.IsTranslationEnabled), YesNo(item.IsLearningEnabled), item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture)
                })));

    private static LegendConnectMetricDetailSnapshot BuildPairMetricDetail(LegendConnectOperationalState state) =>
        Detail("directional-pairs", "Directional pairs", "Language pair registry authority",
            "Each row is an enabled directional pair. Directionality and pair state are not inferred by the browser.",
            Section("Enabled directional pairs", "Canonical pair records behind the dashboard total.",
                new[] { "Pair", "Source", "Target", "Coverage", "Quality", "Provider fallback", "Updated" },
                state.Pairs.Where(item => item.IsEnabled).OrderBy(item => item.PairKey).Select(item => new[]
                {
                    item.PairKey, item.SourceLanguageCode, item.TargetLanguageCode, Display(item.CorpusCoverage), item.QualityState,
                    item.ProviderFallbackPolicy, item.UpdatedUtc.ToString("u", CultureInfo.InvariantCulture)
                })));

    private static LegendConnectMetricDetailSnapshot BuildLearningFailureMetricDetail(LegendConnectOperationalState state)
    {
        var eventRows = state.LearningEvents.Where(item => !string.IsNullOrWhiteSpace(item.FailureCode))
            .OrderByDescending(item => item.ProcessedUtc ?? item.CreatedUtc)
            .Select(item => new[] { item.PairKey, item.Provenance, item.EligibilityState, item.ProcessingState, item.FailureCode ?? string.Empty, Display(item.ProcessedUtc ?? item.CreatedUtc) });
        var candidateRows = state.Candidates.Where(item => !string.IsNullOrWhiteSpace(item.FailureCode))
            .OrderByDescending(item => item.ProcessedUtc ?? item.CreatedUtc)
            .Select(item => new[] { Pair(item.SourceLanguageCode, item.TargetLanguageCode), item.Provenance, item.ProcessingState, item.FailureCode ?? string.Empty, Display(item.ProviderCharactersConsumed), Display(item.ProcessedUtc ?? item.CreatedUtc) });
        return Detail("learning-failures", "Learning failures", "Canonical learning and acquisition records",
            "Only persisted failure codes are shown. Opening this modal neither retries nor changes a worker record.",
            Section("Learning event failures", "Failed canonical learning hand-offs.", new[] { "Pair", "Provenance", "Eligibility", "State", "Failure", "Last activity" }, eventRows),
            Section("Corpus acquisition failures", "Failed approved acquisition candidates.", new[] { "Pair", "Provenance", "State", "Failure", "Provider characters", "Last activity" }, candidateRows));
    }

    private static LegendConnectMetricDetailSnapshot BuildDuplicateMetricDetail(LegendConnectOperationalState state, string key) =>
        Detail(key, TitleFor(key), "Canonical duplicate-prevention authority",
            "These are the auditable events and Founder actions that the existing idempotency rules prevented from creating duplicate knowledge.",
            Section("Operational duplicate-prevention events", "Sanitized operational events recorded by the canonical pipeline.",
                new[] { "When", "Language", "Pair", "Code", "Summary", "Resolved" },
                state.OperationalEvents.Where(item => item.Category == "DuplicatePrevention" && item.Status == "Prevented")
                    .OrderByDescending(item => item.OccurredUtc).Select(item => new[] { Display(item.OccurredUtc), item.LanguageCode ?? string.Empty, item.PairKey ?? string.Empty, item.ErrorCode ?? string.Empty, item.Summary ?? string.Empty, YesNo(item.IsResolved) })),
            Section("Founder duplicate-prevention audit", "Append-only Founder action evidence; no duplicate corpus or alignment is created.",
                new[] { "When", "Action", "Language", "Pair", "Detail" },
                state.AuditEntries.Where(item => item.Result == "DuplicatePrevented").OrderByDescending(item => item.OccurredUtc)
                    .Select(item => new[] { Display(item.OccurredUtc), item.Action, item.LanguageCode, item.PairKey ?? string.Empty, item.Detail ?? string.Empty })));

    private static LegendConnectMetricDetailSnapshot BuildCandidateMetricDetail(LegendConnectOperationalState state, string key)
    {
        var candidates = key switch
        {
            "approved-candidates" => state.Candidates.Where(item => item.IsApproved),
            "eligible-pending" => state.Candidates.Where(item => item.IsApproved && item.ProcessingState is "Pending" or "Processing"),
            "rejected-ineligible" => state.Candidates.Where(item => !item.IsApproved || item.ProcessingState == "Rejected"),
            _ => state.Candidates.Where(item => item.IsApproved && item.ProcessingState is "Pending" or "Processing")
        };
        var title = key == "pairs-awaiting-knowledge" ? "Pairs awaiting knowledge" : TitleFor(key);
        var candidateSection = Section("Canonical corpus candidates", "Exact candidate records behind this readiness metric.",
            new[] { "Pair", "Category", "Provenance", "Approved", "State", "Attempts", "Provider characters", "Created" },
            candidates.OrderByDescending(item => item.CreatedUtc).Select(item => new[]
            {
                Pair(item.SourceLanguageCode, item.TargetLanguageCode), item.Category, item.Provenance, YesNo(item.IsApproved), item.ProcessingState,
                Display(item.AttemptCount), Display(item.ProviderCharactersConsumed), Display(item.CreatedUtc)
            }));
        if (key != "pairs-awaiting-knowledge")
            return Detail(key, title, "Corpus readiness authority", "The table contains the actual approved-corpus queue records, not a duplicate dashboard summary.", candidateSection);
        return Detail(key, title, "Corpus readiness authority", "Each row identifies a directional pair with actual approved work still awaiting knowledge acquisition.",
            Section("Pairs with eligible pending work", "Grouped from the existing approved candidate queue.", new[] { "Pair", "Pending candidates", "Earliest queued", "Latest queued" },
                candidates.GroupBy(item => Pair(item.SourceLanguageCode, item.TargetLanguageCode)).OrderBy(item => item.Key).Select(group => new[]
                {
                    group.Key, Display(group.LongCount()), Display(group.Min(item => item.CreatedUtc)), Display(group.Max(item => item.CreatedUtc))
                })), candidateSection);
    }

    private static LegendConnectMetricDetailSnapshot BuildDemandMetricDetail(LegendConnectOperationalState state, string key)
    {
        if (key == "same-language-bypasses")
            return Detail(key, TitleFor(key), "Privacy-safe system usage authority", "Same-language routes are recorded only in the system aggregate; they have no directional pair or message body record.",
                Section("Daily same-language bypasses", "Daily aggregate records behind the count.", new[] { "Date", "Bypasses", "Updated" }, state.SystemUsage.OrderByDescending(item => item.UsageDate).Select(item => new[] { item.UsageDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Display(item.SameLanguageBypassCount), Display(item.UpdatedUtc) })));
        return Detail(key, TitleFor(key), "Directional demand authority", "Each row is the server-owned directional demand record used for routing and planner decisions.",
            Section("Directional routing evidence", "The relevant routing counters by canonical pair.",
                new[] { "Pair", "Requests", "Exact memory", "Structural", "Contextual", "Promoted translation model", "Model failures", "Provider observation reuse", "Provider work required", "Reconciliation gap", "Provider characters", "Last request" },
                state.Demand.OrderByDescending(item => item.LastRequestedUtc).Select(item => new[]
                {
                    item.PairKey, Display(item.TranslationRequestCount), Display(item.TranslationMemoryHitCount),
                    Display(item.StructuralInternalServeCount), Display(item.ContextualInternalServeCount),
                    Display(item.NeuralModelServeCount), Display(item.NeuralModelFailureCount),
                    Display(item.ProviderObservationReuseCount), Display(item.AzureFallbackCount),
                    Display(item.TranslationRequestCount - item.TranslationMemoryHitCount -
                        item.StructuralInternalServeCount - item.ContextualInternalServeCount -
                        item.NeuralModelServeCount - item.ProviderObservationReuseCount -
                        item.AzureFallbackCount),
                    Display(item.ProviderCharacterCount), Display(item.LastRequestedUtc)
                })));
    }

    private static LegendConnectMetricDetailSnapshot BuildPendingLearningMetricDetail(LegendConnectOperationalState state) =>
        Detail("pending-learning-jobs", "Pending learning jobs", "Canonical learning hand-off authority",
            "These are the existing eligible learning events that remain pending or are currently being processed. No job is created or advanced by viewing this data.",
            Section("Eligible learning events", "Privacy-safe pipeline records; retained text is intentionally not exposed here.",
                new[] { "Pair", "Provenance", "Provider", "State", "Attempts", "Queued", "Lease expires" },
                ActiveLearningEvents(state).Where(item => item.EligibilityState == "Eligible" && item.ProcessingState is "Pending" or "Processing")
                    .OrderBy(item => item.CreatedUtc).Select(item => new[]
                    {
                        item.PairKey, item.Provenance, item.Provider, item.ProcessingState, Display(item.AttemptCount),
                        Display(item.CreatedUtc), Display(item.LeaseExpiresUtc)
                    })));

    private async Task<LegendConnectMetricDetailSnapshot> BuildQualityMetricDetailAsync(
        LegendConnectOperationalState state,
        string key,
        CancellationToken cancellationToken)
    {
        var evidence = await _db.Set<LegendTranslationQualityEvidence>().AsNoTracking()
            .OrderByDescending(item => item.UpdatedUtc)
            .ToListAsync(cancellationToken);
        var quality = await Intelligence.GetTranslationQualityAsync(cancellationToken);
        if (key == "quality-needs-review")
            return Detail(key, TitleFor(key), "Translation quality evidence authority",
                "These are the actual provider observations the existing quality authority has placed in review; the modal does not approve, reject, or modify them.",
                Section("Observations requiring Founder review", "The current review queue from the canonical quality projection.",
                    new[] { "Pair", "Source", "Provider target", "Provider", "Provenance", "Reason", "Observed" },
                    quality.ReviewItems.Select(item => new[]
                    {
                        item.PairKey, $"{item.SourceLanguageCode}: {item.SourceText}", $"{item.TargetLanguageCode}: {item.ProviderTargetText}", item.Provider,
                        item.Provenance, item.ReasonForReview, Display(item.ObservedUtc)
                    })));

        if (key == "quality-human-verified")
        {
            var textById = state.TextUnits.Where(item => item.IsTrainingEligible &&
                    !string.Equals(item.Provenance, "ConsentedLiveTranslation", StringComparison.Ordinal))
                .ToDictionary(item => item.Id);
            return Detail(key, TitleFor(key), "Translation alignment authority",
                "Human verification is shown only where the existing alignment record carries that state. Provider observations do not gain this authority by appearing here.",
                Section("Human-verified active alignments", "Active alignment records with explicit human verification.",
                    new[] { "Pair", "Source", "Target", "Provider", "Quality", "Observations", "Updated" },
                    state.Alignments.Where(item => item.SupersededUtc is null && item.HumanVerified && textById.ContainsKey(item.SourceTextUnitId) && textById.ContainsKey(item.TargetTextUnitId))
                        .OrderByDescending(item => item.UpdatedUtc).Select(item => new[]
                        {
                            item.PairKey, $"{textById[item.SourceTextUnitId].LanguageCode}: {textById[item.SourceTextUnitId].Text}",
                            $"{textById[item.TargetTextUnitId].LanguageCode}: {textById[item.TargetTextUnitId].Text}", item.Provider,
                            item.QualityState, Display(item.ObservationCount), Display(item.UpdatedUtc)
                        })));
        }

        var filtered = key switch
        {
            "quality-supported-observations" => evidence.Where(item => item.Signal == "Supported"),
            "quality-contradictions" => evidence.Where(item => item.Signal == "Contradictory"),
            _ => evidence
        };
        return Detail(key, TitleFor(key), "Translation quality evidence authority",
            "Every row is a persisted quality-evidence record. Signals are evidence, not automatic promotion to trusted or production-eligible knowledge.",
            Section("Quality evidence records", "The actual evidence attached to provider observations.",
                new[] { "Pair", "Signal", "Reason", "Resolution", "Observed alignment", "Related alignment", "Structural pattern", "Updated" },
                filtered.Select(item => new[]
                {
                    item.PairKey, item.Signal, item.ReasonCode, item.ResolutionState, item.ObservedAlignmentId.ToString("N"),
                    item.RelatedAlignmentId?.ToString("N") ?? string.Empty, item.StructuralPatternId?.ToString("N") ?? string.Empty, Display(item.UpdatedUtc)
                })));
    }

    private async Task<LegendConnectMetricDetailSnapshot> BuildUsageMetricDetailAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var usage = await _db.Set<LegendTranslationSystemUsage>().AsNoTracking()
            .OrderByDescending(item => item.UsageDate).ToListAsync(cancellationToken);
        var periods = await _db.Set<LegendTranslationUsagePeriod>().AsNoTracking()
            .OrderByDescending(item => item.PeriodStart).ThenBy(item => item.ParticipantType).ToListAsync(cancellationToken);
        var ledger = await _db.Set<LegendTranslationUsageLedger>().AsNoTracking()
            .OrderByDescending(item => item.CompletedUtc ?? item.CreatedUtc).ToListAsync(cancellationToken);

        if (key == "high-consumption-accounts")
        {
            var period = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            var entitlements = await _db.Set<LegendTranslationEntitlement>().AsNoTracking().ToListAsync(cancellationToken);
            var entitlementByAccount = entitlements.ToDictionary(item => (item.UserId.Trim().ToLowerInvariant(), item.ParticipantType), item => item);
            var defaultAllowance = Math.Max(0, _configuration.GetValue<long?>("LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance") ?? 0);
            return Detail(key, TitleFor(key), "Translation entitlement and usage authority",
                "This uses the same current-period 80% finite-allowance threshold as the Founder scale projection. Unlimited accounts are excluded.",
                Section("Accounts at or above 80%", "Current period consumption plus active reservations against the canonical finite allowance.",
                    new[] { "Account reference", "Type", "Allowance", "Consumed", "Reserved", "Utilization", "Last activity" },
                    periods.Where(item => item.PeriodStart == period).Select(item => new
                        {
                            Usage = item,
                            Entitlement = entitlementByAccount.GetValueOrDefault((item.UserId.Trim().ToLowerInvariant(), item.ParticipantType)),
                            Allowance = Math.Max(0, entitlementByAccount.GetValueOrDefault((item.UserId.Trim().ToLowerInvariant(), item.ParticipantType))?.MonthlyCharacterAllowance ?? defaultAllowance)
                        })
                        .Where(item => item.Entitlement is not { IsUnlimited: true } && item.Allowance > 0 &&
                            ((decimal)(Math.Max(0, item.Usage.ConsumedCharacters) + Math.Max(0, item.Usage.ReservedCharacters)) / item.Allowance) >= 0.8m)
                        .OrderByDescending(item => item.Usage.ConsumedCharacters + item.Usage.ReservedCharacters).Select(item => new[]
                        {
                            item.Usage.UserId, item.Usage.ParticipantType, Display(item.Allowance), Display(item.Usage.ConsumedCharacters),
                            Display(item.Usage.ReservedCharacters), ((decimal)(Math.Max(0, item.Usage.ConsumedCharacters) + Math.Max(0, item.Usage.ReservedCharacters)) / item.Allowance).ToString("P1", CultureInfo.InvariantCulture),
                            Display(item.Usage.LastTranslationActivityUtc)
                        })));
        }

        var ledgerRows = key switch
        {
            "provider-operations" => ledger.Where(item => item.ProviderExecuted),
            "provider-billable-characters" => ledger.Where(item => item.ProviderExecuted && item.BillableCharacters > 0),
            "quota-denied" => ledger.Where(item => item.State == "QuotaDenied"),
            "provider-failures" => ledger.Where(item => item.ProviderExecuted && !item.Succeeded),
            _ => Enumerable.Empty<LegendTranslationUsageLedger>()
        };
        var aggregateColumn = UsageColumnFor(key);
        var accountColumn = UsagePeriodColumnFor(key);
        var sections = new List<LegendConnectMetricDetailSectionSnapshot>();
        if (ledgerRows.Any())
        {
            sections.Add(Section("Translation usage ledger", "Individual privacy-safe ledger rows behind this operational metric. Request references are one-way identifiers; conversation bodies are not retained here.",
                new[] { "Request reference", "Account reference", "Type", "Source", "Target", "Provider", "Characters", "State", "Failure", "Completed" },
                ledgerRows.Select(item => new[]
                {
                    item.RequestReference, item.UserId, item.ParticipantType, item.SourceLanguageCode, item.TargetLanguageCode, item.Provider,
                    Display(item.BillableCharacters), item.State, item.FailureCode ?? string.Empty, Display(item.CompletedUtc ?? item.CreatedUtc)
                })));
        }
        sections.Add(Section("Daily system aggregate", "The deployed aggregate record that supplies the dashboard total without exposing conversation content.",
            new[] { "Date", "Metric value", "Updated" }, usage.Select(item => new[] { item.UsageDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Display(UsageValue(item, aggregateColumn)), Display(item.UpdatedUtc) })));
        sections.Add(Section("Account-period aggregate", "The account usage authority for the same metric, shown without message content.",
            new[] { "Period", "Account reference", "Type", "Metric value", "Updated" }, periods.Select(item => new[] { item.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), item.UserId, item.ParticipantType, Display(UsagePeriodValue(item, accountColumn)), Display(item.UpdatedUtc) })));
        return new LegendConnectMetricDetailSnapshot(key, TitleFor(key), "Translation usage authority", UsageDescriptionFor(key), sections);
    }

    private static LegendConnectMetricDetailSnapshot BuildConsentedLearningMetricDetail(LegendConnectOperationalState state, string key)
    {
        var events = state.LearningEvents.Where(item => item.Provenance == "ConsentedLiveTranslation");
        events = key switch
        {
            "eligible-live-translations" => events.Where(item => item.EligibilityState == "Eligible"),
            "promoted-to-learning" => events.Where(item => item.PromotionOutcome == "Promoted"),
            "canonical-reuse-prevented-duplicates" => events.Where(item => item.PromotionOutcome == "Reused"),
            "awaiting-corpus-processing" => events.Where(item => item.ProcessingState is "Pending" or "Processing"),
            _ => events
        };
        return Detail(key, TitleFor(key), "Consented live-learning authority",
            key == "consented-accounts"
                ? "Consent is intentionally represented as a privacy-safe aggregate. The detailed event records below contain no account identity or conversation text."
                : "These are the consent-governed pipeline events behind this metric. Conversation bodies and account identities are intentionally excluded from Founder telemetry.",
            Section("Consented learning events", "The existing live-learning hand-off records, displayed without private retained text or account identity.",
                new[] { "Pair", "Provider", "Eligibility", "State", "Promotion", "Attempts", "Queued", "Processed" },
                events.OrderByDescending(item => item.ProcessedUtc ?? item.CreatedUtc).Select(item => new[]
                {
                    item.PairKey, item.Provider, item.EligibilityState, item.ProcessingState, item.PromotionOutcome ?? string.Empty,
                    Display(item.AttemptCount), Display(item.CreatedUtc), Display(item.ProcessedUtc)
                })));
    }

    private static LegendConnectMetricDetailSnapshot BuildFounderTrainingMetricDetail(LegendConnectOperationalState state, string key)
    {
        if (key == "raw-submissions-retained")
            return Detail(key, TitleFor(key), "Founder training provenance authority",
                "Each row is one immutable Founder raw-training submission. This is source provenance, not a second corpus.",
                Section("Raw Founder submissions", "Retained raw submission provenance behind the count.",
                    new[] { "Submitted", "Source language", "Characters", "Atomic units", "Context", "Usage", "State" },
                    state.FounderTrainingSubmissions.OrderByDescending(item => item.CreatedUtc).Select(item => new[]
                    {
                        Display(item.CreatedUtc), item.SourceLanguageCode, Display(item.RawCharacterCount), Display(item.AtomicUnitCount),
                        item.ContextCategory ?? string.Empty, item.UsageRegister ?? string.Empty, item.ProcessingState
                    })));

        if (key == "atomic-learning-units")
        {
            var submissionById = state.FounderTrainingSubmissions.ToDictionary(item => item.Id);
            var textById = state.TextUnits.ToDictionary(item => item.Id);
            return Detail(key, TitleFor(key), "Founder training atomic-unit authority",
                "Each row is an existing submission-to-atomic-unit relationship. It is the canonical decomposition lineage, not a re-parse or a new corpus.",
                Section("Atomic learning units", "Atomic units produced from retained Founder submissions.",
                    new[] { "Submission", "Sequence", "Paragraph", "Unit type", "Language", "Atomic text", "Created" },
                    state.FounderTrainingSubmissionUnits.Where(item => submissionById.ContainsKey(item.SubmissionId) && textById.ContainsKey(item.TextUnitId))
                        .OrderByDescending(item => item.CreatedUtc).Select(item => new[]
                        {
                            submissionById[item.SubmissionId].CreatedUtc.ToString("u", CultureInfo.InvariantCulture), Display(item.SequenceNumber), Display(item.ParagraphNumber), item.UnitType,
                            textById[item.TextUnitId].LanguageCode, textById[item.TextUnitId].Text, Display(item.CreatedUtc)
                        }),
                    rowTone: "success"));
        }

        if (key == "active-directional-alignments")
        {
            var textById = state.TextUnits.Where(item => item.IsTrainingEligible && !string.Equals(item.Provenance, "ConsentedLiveTranslation", StringComparison.Ordinal))
                .ToDictionary(item => item.Id);
            return Detail(key, TitleFor(key), "Directional alignment authority",
                "Only active alignments whose source and target are canonical training-eligible assets are shown. Private consented text is not exposed.",
                Section("Active canonical directional alignments", "Existing reusable directional alignment records.",
                    new[] { "Pair", "Source", "Target", "Provider", "Provenance", "Quality", "Human verified", "Updated" },
                    state.Alignments.Where(item => item.SupersededUtc is null && textById.ContainsKey(item.SourceTextUnitId) && textById.ContainsKey(item.TargetTextUnitId))
                        .OrderByDescending(item => item.UpdatedUtc).Select(item => new[]
                        {
                            item.PairKey, $"{textById[item.SourceTextUnitId].LanguageCode}: {textById[item.SourceTextUnitId].Text}",
                            $"{textById[item.TargetTextUnitId].LanguageCode}: {textById[item.TargetTextUnitId].Text}", item.Provider, item.Provenance,
                            item.QualityState, YesNo(item.HumanVerified), Display(item.UpdatedUtc)
                        })));
        }

        var legacyById = state.TextUnits.ToDictionary(item => item.Id);
        return Detail(key, TitleFor(key), "Founder legacy reconciliation authority",
            "These rows are the existing raw-submission provenance records whose legacy multi-unit asset has been retired from reusable training eligibility.",
            Section("Retired legacy multi-unit assets", "Founder submissions linked to a legacy source asset that is no longer training eligible.",
                new[] { "Submitted", "Language", "Characters", "Atomic units", "Legacy asset", "State" },
                state.FounderTrainingSubmissions.Where(item => item.LegacySourceTextUnitId is Guid legacyId && legacyById.TryGetValue(legacyId, out var unit) && !unit.IsTrainingEligible)
                    .OrderByDescending(item => item.CreatedUtc).Select(item => new[]
                    {
                        Display(item.CreatedUtc), item.SourceLanguageCode, Display(item.RawCharacterCount), Display(item.AtomicUnitCount), item.LegacySourceTextUnitId!.Value.ToString("N"), item.ProcessingState
                    })));
    }

    private static LegendConnectMetricDetailSnapshot Detail(
        string key,
        string title,
        string context,
        string description,
        params LegendConnectMetricDetailSectionSnapshot[] sections) =>
        new(key, title, context, description, sections);

    private static LegendConnectMetricDetailSnapshot EmptyMetricDetail(string key, string title, string description) =>
        Detail(key, title, "Legend Connect", description,
            Section("No matching records", "The selected metric currently has no configured record-level detail.", Array.Empty<string>(), Array.Empty<string[]>()));

    private static string TranslationRouteKey(Guid messageId, string targetLanguageCode) =>
        $"{messageId:N}:{targetLanguageCode.Trim().ToUpperInvariant()}";

    private static string RoutedSourceLanguage(
        TranslationRouteAuditRow route,
        TranslationRouteLearningRow? learning) =>
        !string.IsNullOrWhiteSpace(route.SenderPreferredLanguage)
            ? $"{route.SenderPreferredLanguage} (sender preference at send time)"
            : !string.IsNullOrWhiteSpace(learning?.SourceLanguageCode)
                ? $"{learning.SourceLanguageCode} (learning hand-off)"
                : !string.IsNullOrWhiteSpace(route.DetectedLanguage)
                    ? $"{route.DetectedLanguage} (detected fallback)"
                    : "Not retained";

    private static TranslationRouteDescription DescribeTranslationRoute(string provider) => provider switch
    {
        "LegendConnectSameLanguage" => new(
            "Legend same-language bypass",
            "Same language; no translation provider is needed."),
        "LegendConnectTranslationMemory" => new(
            "Legend trusted exact memory",
            "Trusted exact directional memory; provider was not called."),
        "LegendConnectContextualComposition" => new(
            "Legend verified contextual knowledge",
            "Existing contextual relationship served inside the active canonical boundary."),
        "LegendConnectStructuralComposition" => new(
            "Legend structural composition",
            "Existing structural composition gate served the result; provider was not called."),
        "LegendConnectPromotedTranslationModel" => new(
            "LEGEND promoted translation model",
            "The promoted translation capability served this route; Founder-chat reasoning is a separate capability."),
        "LegendConnectNeuralModel" => new(
            "LEGEND promoted translation model (legacy label)",
            "A previously persisted translation-model route; it remains separate from Founder-chat reasoning."),
        "LegendConnectProviderObservation" => new(
            "Exact provider observation reuse",
            "An eligible provider-derived output was reused without a new Azure call; it is not native LEGEND intelligence."),
        "AzureTranslator" => new(
            "Azure Translator full fallback",
            "Azure result is provider-derived evidence and is never trusted merely because it was returned."),
        _ => new(
            provider,
            "Recorded provider route; the provider name is the persisted operational result.")
    };

    private static string AzureInvocation(string provider, TranslationRouteLedgerRow? ledger)
    {
        if (!string.Equals(provider, "AzureTranslator", StringComparison.Ordinal))
            return "Not called";

        if (ledger is null)
            return "Called · persisted result";

        if (ledger.ProviderExecuted && ledger.Succeeded)
            return "Called · completed";

        return ledger.ProviderExecuted
            ? $"Called · {ledger.State}"
            : $"Not called · {ledger.State}";
    }

    private static string LearningHandoff(TranslationRouteLearningRow? learning)
    {
        if (learning is null)
            return "No persisted learning hand-off";

        var promotion = string.IsNullOrWhiteSpace(learning.PromotionOutcome)
            ? "No promotion outcome yet"
            : learning.PromotionOutcome;
        return string.Join(" · ", learning.Provenance, learning.EligibilityState, learning.ProcessingState, promotion);
    }

    private static LegendConnectMetricDetailSectionSnapshot Section(
        string title,
        string description,
        IReadOnlyList<string> columns,
        IEnumerable<string[]> rows,
        string? rowTone = null)
    {
        var detailRows = rows.Select(item => (IReadOnlyList<string>)item).ToList();
        return new LegendConnectMetricDetailSectionSnapshot(
            title,
            description,
            columns,
            detailRows,
            detailRows.Select(row => rowTone ?? DetailRowTone(row)).ToList());
    }

    /// <summary>
    /// Derives a presentation-only row tone from the canonical state already
    /// included in a Founder-safe detail row. It neither changes evidence nor
    /// infers language data: it makes trusted, provider-observed, pending, and
    /// blocked records visually distinguishable in every metric detail modal.
    /// </summary>
    private static string DetailRowTone(IReadOnlyList<string> row)
    {
        var values = string.Join(' ', row).ToUpperInvariant();

        if (ContainsAny(values, "QUOTADENIED", "FAILED", "FAILURE", "REJECTED", "BLOCKED", "INVALID", "SUPERSEDED", "DENIED", "ERROR"))
            return "danger";

        if (ContainsAny(values, "NOT HUMAN VERIFIED", "NOTPROCESSED", "PENDING", "PROCESSING", "AWAITING", "QUEUED", "REVIEW", "HOLD", "INSUFFICIENT", "LEGACY", "IMPORTED"))
            return "warning";

        if (ContainsAny(values, "FOUNDERAPPROVED", "FOUNDER APPROVED", "HUMANVERIFIED", "HUMAN VERIFIED"))
            return "success";

        if (ContainsAny(values, "PROVIDERDERIVED", "PROVIDER DERIVED", "AZURETRANSLATOR", "AZURE TRANSLATOR", "OBSERVATION"))
            return "info";

        if (ContainsAny(values, "VALIDATED", "TRUSTED", "SUPPORTED", "APPROVED", "ELIGIBLE", "COMPLETED", "PROCESSED", "ACTIVE", "YES"))
            return "success";

        return "neutral";
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);

    private static string CapacityValueFor(
        string key,
        LegendConnectProviderCapacitySnapshot snapshot,
        LegendTranslationProviderCapacity? currentCapacity) => key switch
    {
        "capacity-monthly-limit" => Display(snapshot.MonthlyIncludedCharacterAllowance),
        "capacity-monthly-consumed" or "azure-characters-used" => Display(snapshot.MonthlyCharactersConsumed),
        "capacity-monthly-reserved" or "provider-characters-reserved" => Display(snapshot.MonthlyReservedCharacters),
        "capacity-monthly-remaining" => Display(snapshot.MonthlyRemainingCharacters),
        "capacity-monthly-reserve" => Display(snapshot.MonthlyLiveReserveCharacters),
        "capacity-monthly-corpus" => Display(snapshot.MaximumSafeCorpusConsumptionCharacters),
        "capacity-hourly-limit" => Display(snapshot.HourlyCharacterLimit),
        "capacity-hourly-consumed" => Display(snapshot.HourlyCharactersConsumed),
        "capacity-hourly-remaining" => Display(snapshot.HourlyRemainingCharacters),
        "capacity-safe" => Display(snapshot.SafeAcquisitionCharacters),
        "consumed-live-characters" => Display(currentCapacity?.LiveCharactersConsumed),
        "consumed-corpus-characters" => Display(currentCapacity is null ? null : currentCapacity.BootstrapCharactersConsumed + currentCapacity.TrainingCharactersConsumed),
        _ => snapshot.Status
    };

    private static string TitleFor(string key) => key switch
    {
        "approved-candidates" => "Approved candidates",
        "eligible-pending" => "Eligible pending",
        "rejected-ineligible" => "Rejected / ineligible",
        "pairs-awaiting-knowledge" => "Pairs awaiting knowledge",
        "readiness-duplicates-prevented" => "Duplicates prevented",
        "same-language-bypasses" => "Same-language bypasses",
        "cross-language-translation-requests" => "Cross-language translation requests",
        "translation-memory-hits" => "Translation Memory hits",
        "provider-fallback-required" => "Provider fallback required",
        "trusted-structural-served" => "Trusted structural served",
        "trusted-contextual-served" => "Trusted contextual served",
        "promoted-translation-model-served" => "Promoted translation model served",
        "promoted-translation-model-failures" => "Promoted translation model failures",
        "provider-observation-reused" => "Provider observation reused",
        "native-translation-intelligence-served" => "Native translation intelligence served",
        "translation-routing-reconciliation" => "Translation routing reconciliation",
        "internal-coverage" => "Native translation coverage",
        "provider-avoidance" => "Provider avoidance",
        "provider-dependency" => "Provider dependency",
        "azure-characters-used" => "Azure characters used",
        "consumed-live-characters" => "Consumed live characters",
        "consumed-corpus-characters" => "Consumed corpus characters",
        "provider-characters-reserved" => "Provider characters reserved",
        "pending-learning-jobs" => "Pending learning jobs",
        "quality-needs-review" => "Quality needs review",
        "quality-provider-observations" => "Provider observations",
        "quality-supported-observations" => "Supported observations",
        "quality-contradictions" => "Quality contradictions",
        "quality-human-verified" => "Human-verified alignments",
        "provider-operations" => "Provider operations",
        "provider-billable-characters" => "Provider-billable characters",
        "same-language-avoided" => "Same-language avoided",
        "memory-avoided" => "Memory avoided",
        "structural-avoided" => "Structural composition avoided",
        "context-avoided" => "Context avoided",
        "promoted-translation-model-avoided" => "Promoted translation model avoided",
        "provider-observation-avoided" => "Provider observation reuse avoided",
        "quota-denied" => "Quota denied",
        "provider-failures" => "Provider failures",
        "group-target-reuse" => "Group target reuse",
        "high-consumption-accounts" => "High consumption accounts",
        "consented-accounts" => "Consented accounts",
        "eligible-live-translations" => "Eligible live translations",
        "promoted-to-learning" => "Promoted to learning",
        "canonical-reuse-prevented-duplicates" => "Canonical reuse prevented duplicates",
        "awaiting-corpus-processing" => "Awaiting corpus processing",
        "raw-submissions-retained" => "Raw submissions retained",
        "atomic-learning-units" => "Atomic learning units",
        "active-directional-alignments" => "Active directional alignments",
        "legacy-multi-unit-assets-retired" => "Legacy multi-unit assets retired",
        "capacity-status" => "Azure capacity status",
        _ => "Legend Connect metric details"
    };

    private static string UsageDescriptionFor(string key) => key switch
    {
        "quota-denied" => "Individual quota denials are shown from the one-way usage ledger, alongside the privacy-safe daily and account-period authorities that produce the current total.",
        "provider-billable-characters" => "The ledger rows show each provider-billable request reference, route, provider, character count, state, and completion time without conversation text.",
        "provider-operations" => "The ledger rows show actual provider execution attempts; provider fallback-required remains a separate routing measure.",
        "provider-failures" => "Only persisted failed provider execution rows are included, with their existing failure code.",
        _ => "The table shows the deployed daily and account-period records that calculate this privacy-safe operational total."
    };

    private static string UsageColumnFor(string key) => key switch
    {
        "provider-operations" => nameof(LegendTranslationSystemUsage.ProviderOperationCount),
        "provider-billable-characters" => nameof(LegendTranslationSystemUsage.ProviderBillableCharacters),
        "same-language-avoided" => nameof(LegendTranslationSystemUsage.SameLanguageCharactersAvoided),
        "memory-avoided" => nameof(LegendTranslationSystemUsage.TranslationMemoryCharactersAvoided),
        "structural-avoided" => nameof(LegendTranslationSystemUsage.StructuralCompositionCharactersAvoided),
        "context-avoided" => nameof(LegendTranslationSystemUsage.ContextualCharactersAvoided),
        "promoted-translation-model-avoided" => nameof(LegendTranslationSystemUsage.PromotedTranslationModelCharactersAvoided),
        "provider-observation-avoided" => nameof(LegendTranslationSystemUsage.ProviderObservationCharactersAvoided),
        "quota-denied" => nameof(LegendTranslationSystemUsage.QuotaDeniedRequestCount),
        "provider-failures" => nameof(LegendTranslationSystemUsage.ProviderFailureCount),
        "group-target-reuse" => nameof(LegendTranslationSystemUsage.GroupUniqueTargetReuseCount),
        _ => string.Empty
    };

    private static string UsagePeriodColumnFor(string key) => key switch
    {
        "provider-operations" => nameof(LegendTranslationUsagePeriod.ProviderOperationCount),
        "provider-billable-characters" => nameof(LegendTranslationUsagePeriod.ProviderBillableCharacters),
        "same-language-avoided" => nameof(LegendTranslationUsagePeriod.SameLanguageCharactersAvoided),
        "memory-avoided" => nameof(LegendTranslationUsagePeriod.TranslationMemoryCharactersAvoided),
        "structural-avoided" => nameof(LegendTranslationUsagePeriod.StructuralCompositionCharactersAvoided),
        "context-avoided" => nameof(LegendTranslationUsagePeriod.ContextualCharactersAvoided),
        "promoted-translation-model-avoided" => nameof(LegendTranslationUsagePeriod.PromotedTranslationModelCharactersAvoided),
        "provider-observation-avoided" => nameof(LegendTranslationUsagePeriod.ProviderObservationCharactersAvoided),
        "quota-denied" => nameof(LegendTranslationUsagePeriod.QuotaDeniedRequestCount),
        "provider-failures" => nameof(LegendTranslationUsagePeriod.ProviderFailureCount),
        "group-target-reuse" => nameof(LegendTranslationUsagePeriod.GroupUniqueTargetReuseCount),
        _ => string.Empty
    };

    private static long UsageValue(LegendTranslationSystemUsage usage, string column) => column switch
    {
        nameof(LegendTranslationSystemUsage.ProviderOperationCount) => usage.ProviderOperationCount,
        nameof(LegendTranslationSystemUsage.ProviderBillableCharacters) => usage.ProviderBillableCharacters,
        nameof(LegendTranslationSystemUsage.SameLanguageCharactersAvoided) => usage.SameLanguageCharactersAvoided,
        nameof(LegendTranslationSystemUsage.TranslationMemoryCharactersAvoided) => usage.TranslationMemoryCharactersAvoided,
        nameof(LegendTranslationSystemUsage.StructuralCompositionCharactersAvoided) => usage.StructuralCompositionCharactersAvoided,
        nameof(LegendTranslationSystemUsage.ContextualCharactersAvoided) => usage.ContextualCharactersAvoided,
        nameof(LegendTranslationSystemUsage.PromotedTranslationModelCharactersAvoided) => usage.PromotedTranslationModelCharactersAvoided,
        nameof(LegendTranslationSystemUsage.ProviderObservationCharactersAvoided) => usage.ProviderObservationCharactersAvoided,
        nameof(LegendTranslationSystemUsage.QuotaDeniedRequestCount) => usage.QuotaDeniedRequestCount,
        nameof(LegendTranslationSystemUsage.ProviderFailureCount) => usage.ProviderFailureCount,
        nameof(LegendTranslationSystemUsage.GroupUniqueTargetReuseCount) => usage.GroupUniqueTargetReuseCount,
        _ => 0
    };

    private static long UsagePeriodValue(LegendTranslationUsagePeriod usage, string column) => column switch
    {
        nameof(LegendTranslationUsagePeriod.ProviderOperationCount) => usage.ProviderOperationCount,
        nameof(LegendTranslationUsagePeriod.ProviderBillableCharacters) => usage.ProviderBillableCharacters,
        nameof(LegendTranslationUsagePeriod.SameLanguageCharactersAvoided) => usage.SameLanguageCharactersAvoided,
        nameof(LegendTranslationUsagePeriod.TranslationMemoryCharactersAvoided) => usage.TranslationMemoryCharactersAvoided,
        nameof(LegendTranslationUsagePeriod.StructuralCompositionCharactersAvoided) => usage.StructuralCompositionCharactersAvoided,
        nameof(LegendTranslationUsagePeriod.ContextualCharactersAvoided) => usage.ContextualCharactersAvoided,
        nameof(LegendTranslationUsagePeriod.PromotedTranslationModelCharactersAvoided) => usage.PromotedTranslationModelCharactersAvoided,
        nameof(LegendTranslationUsagePeriod.ProviderObservationCharactersAvoided) => usage.ProviderObservationCharactersAvoided,
        nameof(LegendTranslationUsagePeriod.QuotaDeniedRequestCount) => usage.QuotaDeniedRequestCount,
        nameof(LegendTranslationUsagePeriod.ProviderFailureCount) => usage.ProviderFailureCount,
        nameof(LegendTranslationUsagePeriod.GroupUniqueTargetReuseCount) => usage.GroupUniqueTargetReuseCount,
        _ => 0
    };

    private static string Pair(string source, string target) => $"{source}:{target}";

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string Display(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Display(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "Unavailable";

    private static string Display(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Display(DateTime value) => value.ToString("u", CultureInfo.InvariantCulture);

    private static string Display(DateTime? value) => value?.ToString("u", CultureInfo.InvariantCulture) ?? "—";

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
        return await BuildLanguageKnowledgeAsync(await LoadStateAsync(cancellationToken), languageCode, cancellationToken);
    }

    private async Task<LegendConnectLanguageKnowledgeSnapshot?> BuildLanguageKnowledgeAsync(
        LegendConnectOperationalState state,
        string languageCode,
        CancellationToken cancellationToken)
    {
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
        var pair = ResolvePair(state.Pairs, pairKey);
        return pair is null ? null : BuildPairHealth(pair, state);
    }

    public Task<LegendConnectTranslationQualitySnapshot> GetTranslationQualityAsync(
        CancellationToken cancellationToken = default) =>
        Intelligence.GetTranslationQualityAsync(cancellationToken);

    public Task<LegendTargetRealizationReviewSnapshot> GetTargetRealizationReviewAsync(
        CancellationToken cancellationToken = default) =>
        Curriculum.GetTargetRealizationReviewAsync(cancellationToken);

    public async Task<LegendTargetRealizationReviewActionResult> VerifyTargetRealizationCandidateAsync(
        string founderUserId,
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null)
            return new LegendTargetRealizationReviewActionResult(
                false, "founder_identity_required", "A verified Founder identity is required.", candidateId, "Unavailable", null);

        var result = await Curriculum.VerifyTargetRealizationCandidateAsync(founder, candidateId, cancellationToken);
        await WriteTargetRealizationReviewAuditAsync(founder, "FounderTargetRealizationVerified", result, cancellationToken);
        return result;
    }

    public async Task<LegendTargetRealizationReviewActionResult> RejectTargetRealizationCandidateAsync(
        string founderUserId,
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null)
            return new LegendTargetRealizationReviewActionResult(
                false, "founder_identity_required", "A verified Founder identity is required.", candidateId, "Unavailable", null);

        var result = await Curriculum.RejectTargetRealizationCandidateAsync(founder, candidateId, cancellationToken);
        await WriteTargetRealizationReviewAuditAsync(founder, "FounderTargetRealizationRejected", result, cancellationToken);
        return result;
    }

    public async Task<LegendConnectKnowledgeSubmissionResult> SubmitFounderKnowledgeAsync(
        string founderUserId,
        LegendConnectKnowledgeSubmission submission,
        CancellationToken cancellationToken = default,
        Guid? reusableSourceTextUnitId = null,
        Guid? reusableTargetTextUnitId = null)
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
                ? await FounderTrainingIngestion.SubmitAsync(founder, approved, cancellationToken)
                : await _corpus.SubmitApprovedKnowledgeAsync(
                    approved,
                    cancellationToken,
                    reusableSourceTextUnitId,
                    reusableTargetTextUnitId);
            if (result.Succeeded && result.AlignmentId is { } alignmentId)
                await Curriculum.AttachValidatedAlignmentAsync(alignmentId, cancellationToken);
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
        CancellationToken cancellationToken = default,
        Guid? reusableTargetTextUnitId = null)
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
                reusableSourceTextUnitId: reusableSourceTextUnitId,
                reusableTargetTextUnitId: reusableTargetTextUnitId);
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

            // MessageTranslations is an operational projection, never
            // language truth. Immediate correction and historical replay use
            // the same trusted-memory reconciliation decision.
            var correctionProjectionRows = await (
                from translation in _db.MessageTranslations
                join message in _db.InternalMessages
                    on translation.InternalMessageId equals message.Id
                where translation.TargetLanguage == target &&
                      (message.OriginalLanguage == source ||
                       ((message.OriginalLanguage == null ||
                         message.OriginalLanguage == string.Empty) &&
                        message.SenderPreferredLanguage == source))
                select new
                {
                    Translation = translation,
                    Message = message
                }
            ).ToListAsync(cancellationToken);

            foreach (var row in correctionProjectionRows.Where(row =>
                         LegendLanguageIdentity.TextHash(row.Message.Body) ==
                         priorSource.NormalizedHash))
            {
                await ReconcileOperationalTranslationFromTrustedMemoryAsync(
                    row.Translation,
                    row.Message,
                    cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);
            await Curriculum.ReconcileSupersededAlignmentAsync(
                prior.PairKey,
                prior.SourceTextUnitId,
                prior.TargetTextUnitId,
                cancellationToken);
            if (string.Equals(prior.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal))
            {
                var retiredTargetTextUnitId = await Intelligence.RecordHumanCorrectionAsync(
                    prior.Id,
                    result.AlignmentId.Value,
                    cancellationToken);
                if (retiredTargetTextUnitId is not null)
                    await Curriculum.ReconcileSupersededExamplesAsync([retiredTargetTextUnitId.Value], cancellationToken);
            }
            await _corpus.RefreshPairCoverageAsync(prior.PairKey, cancellationToken);
            await Curriculum.AttachValidatedAlignmentAsync(result.AlignmentId.Value, cancellationToken);
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

    /// <summary>
    /// Replays historical operational translation projections through the
    /// same trusted exact-memory authority used by current corrections.
    /// </summary>
    public async Task<LegendConnectHistoricalReevaluationProgress>
        ReconcileHistoricalOperationalTranslationsAsync(
            int take,
            Guid? afterId,
            CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(take, 1, 250);

        var rows = await (
            from translation in _db.MessageTranslations
            join message in _db.InternalMessages
                on translation.InternalMessageId equals message.Id
            where !afterId.HasValue ||
                  translation.Id.CompareTo(afterId.Value) > 0
            orderby translation.Id
            select new
            {
                Translation = translation,
                Message = message
            }
        ).Take(pageSize).ToListAsync(cancellationToken);

        var changed = false;

        foreach (var row in rows)
        {
            changed |= await ReconcileOperationalTranslationFromTrustedMemoryAsync(
                row.Translation,
                row.Message,
                cancellationToken);
        }

        if (changed)
            await _db.SaveChangesAsync(cancellationToken);

        return new LegendConnectHistoricalReevaluationProgress(
            rows.Count,
            rows.Count == 0 ? null : rows[^1].Translation.Id,
            rows.Count < pageSize);
    }

    public async Task ReconcileHistoricalOperationalTranslationAsync(
        Guid translationId,
        CancellationToken cancellationToken = default)
    {
        var row = await (
            from translation in _db.MessageTranslations
            join message in _db.InternalMessages on translation.InternalMessageId equals message.Id
            where translation.Id == translationId
            select new { Translation = translation, Message = message }
        ).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            return;

        if (await ReconcileOperationalTranslationFromTrustedMemoryAsync(
                row.Translation,
                row.Message,
                cancellationToken))
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Single reconciliation decision shared by present correction and
    /// historical replay. Only trusted exact memory may rewrite presentation.
    /// </summary>
    private async Task<bool> ReconcileOperationalTranslationFromTrustedMemoryAsync(
        MessageTranslation translation,
        InternalMessage message,
        CancellationToken cancellationToken)
    {
        var sourceLanguage = await _registry.NormalizeEnabledTranslationLanguageAsync(
            message.OriginalLanguage,
            cancellationToken);

        if (sourceLanguage is null)
        {
            sourceLanguage = await _registry.NormalizeEnabledTranslationLanguageAsync(
                message.SenderPreferredLanguage,
                cancellationToken);
        }

        var targetLanguage = await _registry.NormalizeEnabledTranslationLanguageAsync(
            translation.TargetLanguage,
            cancellationToken);

        if (sourceLanguage is null ||
            targetLanguage is null ||
            string.Equals(
                sourceLanguage,
                targetLanguage,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trusted = await Intelligence.TryGetTrustedExactMemoryAsync(
            sourceLanguage,
            targetLanguage,
            message.Body,
            cancellationToken);

        if (trusted is null || string.IsNullOrWhiteSpace(trusted.Text))
            return false;

        var trustedText = trusted.Text.Trim();

        if (string.Equals(
                translation.TranslatedText,
                trustedText,
                StringComparison.Ordinal) &&
            string.Equals(
                translation.Provider,
                "LegendConnectTranslationMemory",
                StringComparison.Ordinal))
        {
            return false;
        }

        translation.TranslatedText = trustedText;
        translation.Provider = "LegendConnectTranslationMemory";
        return true;
    }

    /// <summary>
    /// Founder-facing entry point for attaching verified target realizations
    /// to existing canonical source units. Resolution happens by the same
    /// normalized text identity used by the corpus; every resulting mutation
    /// delegates to the existing approval, correction, or submission path.
    /// It intentionally owns no parallel alignment or evidence behavior.
    /// </summary>
    public async Task<LegendConnectVerifiedTargetBatchResult> SubmitFounderVerifiedTargetsAsync(
        string founderUserId,
        LegendConnectVerifiedTargetSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        var sourceLanguage = await _registry.NormalizeEnabledTranslationLanguageAsync(
            submission.SourceLanguageCode,
            cancellationToken);
        var targetLanguage = await _registry.NormalizeEnabledTranslationLanguageAsync(
            submission.TargetLanguageCode,
            cancellationToken);
        if (founder is null || sourceLanguage is null || targetLanguage is null ||
            string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return VerifiedTargetBatchRejected(
                sourceLanguage ?? string.Empty,
                targetLanguage,
                "invalid_verified_target_batch",
                "A verified Founder identity and two enabled, distinct languages are required.",
                submission.Rows);
        }
        if (submission.Rows.Count is 0 or > 500)
        {
            return VerifiedTargetBatchRejected(
                sourceLanguage,
                targetLanguage,
                "invalid_verified_target_batch",
                "Submit from 1 to 500 verified target rows.",
                submission.Rows);
        }

        var rows = new List<LegendConnectVerifiedTargetRowResult>(submission.Rows.Count);
        foreach (var row in submission.Rows.OrderBy(item => item.RowNumber))
        {
            rows.Add(await ApplyFounderVerifiedTargetRowAsync(
                founder,
                sourceLanguage,
                targetLanguage,
                row,
                submission.ContextCategory,
                submission.UsageRegister,
                submission.RegionalVariant,
                cancellationToken));
        }

        var pairKey = LegendLanguageIdentity.PairKey(sourceLanguage, targetLanguage);
        var result = new LegendConnectVerifiedTargetBatchResult(
            rows.Any(IsVerifiedTargetSuccess),
            rows.All(IsVerifiedTargetSuccess) ? null : "verified_target_rows_require_review",
            null,
            sourceLanguage,
            targetLanguage,
            pairKey,
            rows);
        return result with { Message = DescribeVerifiedTargetBatch(result) };
    }

    private async Task<LegendConnectVerifiedTargetRowResult> ApplyFounderVerifiedTargetRowAsync(
        string founder,
        string sourceLanguage,
        string targetLanguage,
        LegendConnectVerifiedTargetRow row,
        string? contextCategory,
        string? usageRegister,
        string? regionalVariant,
        CancellationToken cancellationToken)
    {
        var sourceText = LegendLanguageIdentity.NormalizeText(row.SourceText);
        var targetText = LegendLanguageIdentity.NormalizeText(row.TargetText);
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(targetText) ||
            sourceText.Length > 10_000 || targetText.Length > 10_000)
        {
            return VerifiedTargetRow(
                row.RowNumber,
                "Failed",
                "Each source and verified target must be non-empty and no longer than 10,000 characters.",
                null,
                null,
                null,
                null);
        }

        var sourceHash = LegendLanguageIdentity.TextHash(sourceText);
        var sourceMatches = await _db.Set<LegendLanguageTextUnit>()
            .AsNoTracking()
            .Where(item => item.LanguageCode == sourceLanguage &&
                item.NormalizedHash == sourceHash &&
                item.IsTrainingEligible &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                (_db.Set<LegendFounderTrainingSubmissionUnit>()
                    .Any(unit => unit.TextUnitId == item.Id) ||
                 _db.Set<LegendCurriculumExample>()
                    .Any(example => example.TextUnitId == item.Id &&
                        example.LanguageCode == sourceLanguage &&
                        example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                        example.SupersededUtc == null) ||
                 _db.Set<LegendTranslationAlignment>()
                    .Any(alignment => alignment.SourceTextUnitId == item.Id &&
                        alignment.HumanVerified && alignment.SupersededUtc == null &&
                        alignment.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)))
            .Take(2)
            .ToListAsync(cancellationToken);
        if (sourceMatches.Count == 0)
        {
            return VerifiedTargetRow(
                row.RowNumber,
                "Unmatched",
                "No active Founder-approved canonical source matched this row; no target evidence was attached.",
                null,
                null,
                null,
                null);
        }
        if (sourceMatches.Count != 1)
        {
            return VerifiedTargetRow(
                row.RowNumber,
                "Ambiguous",
                "More than one active Founder-approved canonical source matched this row; no target evidence was attached.",
                null,
                null,
                null,
                null);
        }

        var source = sourceMatches[0];
        var pair = await _registry.GetOrCreateEnabledPairAsync(sourceLanguage, targetLanguage, cancellationToken);
        if (pair is null)
        {
            return VerifiedTargetRow(
                row.RowNumber,
                "Failed",
                "The selected directional pair is not enabled.",
                source.Id,
                null,
                null,
                null);
        }

        var targetHash = LegendLanguageIdentity.TextHash(targetText);
        var targetMatches = await _db.Set<LegendLanguageTextUnit>()
            .AsNoTracking()
            .Where(item => item.LanguageCode == targetLanguage && item.NormalizedHash == targetHash)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (targetMatches.Count > 1)
        {
            return VerifiedTargetRow(
                row.RowNumber,
                "Failed",
                "More than one canonical target matched this row; no target evidence was attached.",
                source.Id,
                null,
                null,
                pair.PairKey);
        }
        var canonicalTarget = targetMatches.SingleOrDefault(item => item.IsTrainingEligible);

        var activeAlignments = await _db.Set<LegendTranslationAlignment>()
            .Where(item => item.PairKey == pair.PairKey &&
                item.SourceTextUnitId == source.Id &&
                item.SupersededUtc == null)
            .OrderBy(item => item.CreatedUtc)
            .ToListAsync(cancellationToken);
        var exactAlignments = canonicalTarget is null
            ? new List<LegendTranslationAlignment>()
            : activeAlignments.Where(item => item.TargetTextUnitId == canonicalTarget.Id).ToList();
        if (exactAlignments.Count > 1)
        {
            return VerifiedTargetRow(
                row.RowNumber,
                "Ambiguous",
                "Multiple active alignments match this target; no verification was guessed.",
                source.Id,
                canonicalTarget!.Id,
                null,
                pair.PairKey);
        }
        var exactAlignment = exactAlignments.SingleOrDefault();
        if (exactAlignment is not null)
        {
            if (exactAlignment.HumanVerified)
            {
                return VerifiedTargetRow(
                    row.RowNumber,
                    "AlreadyVerified",
                    "The active canonical target is already Founder-verified; no duplicate alignment was created.",
                    source.Id,
                    canonicalTarget!.Id,
                    exactAlignment.Id,
                    pair.PairKey);
            }

            if (string.Equals(exactAlignment.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal))
            {
                var approved = await ApproveProviderObservationAsync(founder, exactAlignment.Id, cancellationToken);
                return approved.Succeeded
                    ? VerifiedTargetRow(
                        row.RowNumber,
                        "ExistingTargetVerified",
                        "The matching provider target was Founder-verified through the canonical trust path.",
                        source.Id,
                        canonicalTarget!.Id,
                        exactAlignment.Id,
                        pair.PairKey)
                    : VerifiedTargetRow(
                        row.RowNumber,
                        "Failed",
                        approved.Message,
                        source.Id,
                        canonicalTarget!.Id,
                        exactAlignment.Id,
                        pair.PairKey);
            }
        }

        var trustedActive = activeAlignments.Where(item => item.HumanVerified).ToList();
        if (trustedActive.Count > 1)
        {
            return VerifiedTargetRow(
                row.RowNumber,
                "Ambiguous",
                "Multiple active verified target alignments exist for this source and directional pair; no correction was guessed.",
                source.Id,
                canonicalTarget?.Id,
                null,
                pair.PairKey);
        }

        var providerActive = activeAlignments
            .Where(item => string.Equals(item.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal))
            .ToList();
        if (trustedActive.Count == 0 && providerActive.Count > 1)
        {
            return VerifiedTargetRow(
                row.RowNumber,
                "Ambiguous",
                "Multiple active provider observations exist for this source and directional pair; no correction was guessed.",
                source.Id,
                canonicalTarget?.Id,
                null,
                pair.PairKey);
        }
        var prior = trustedActive.SingleOrDefault() ?? providerActive.SingleOrDefault();

        var verifiedSubmission = new LegendConnectKnowledgeSubmission(
            sourceLanguage,
            sourceText,
            targetLanguage,
            targetText,
            contextCategory,
            usageRegister,
            regionalVariant,
            LegendConnectKnowledgeProvenance.FounderApproved);
        if (prior is not null)
        {
            var corrected = await CorrectFounderKnowledgeAsync(
                founder,
                prior.Id,
                verifiedSubmission,
                cancellationToken,
                canonicalTarget?.Id);
            if (!corrected.Succeeded)
            {
                return VerifiedTargetRow(
                    row.RowNumber,
                    "Failed",
                    corrected.Message ?? "The canonical correction was not applied.",
                    source.Id,
                    canonicalTarget?.Id,
                    prior.Id,
                    pair.PairKey);
            }

            return VerifiedTargetRow(
                row.RowNumber,
                string.Equals(prior.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal)
                    ? "ProviderTargetCorrected"
                    : "FounderTargetCorrected",
                "The prior target alignment was superseded through the canonical correction lineage.",
                source.Id,
                corrected.TargetTextUnitId,
                corrected.AlignmentId,
                corrected.PairKey);
        }

        var added = await SubmitFounderKnowledgeAsync(
            founder,
            verifiedSubmission,
            cancellationToken,
            reusableSourceTextUnitId: source.Id,
            reusableTargetTextUnitId: canonicalTarget?.Id);
        return added.Succeeded
            ? VerifiedTargetRow(
                row.RowNumber,
                "FounderTargetAdded",
                "A Founder-verified target alignment was attached to the existing canonical source.",
                source.Id,
                added.TargetTextUnitId,
                added.AlignmentId,
                added.PairKey)
            : VerifiedTargetRow(
                row.RowNumber,
                "Failed",
                added.Message ?? "The Founder-verified target could not be attached.",
                source.Id,
                canonicalTarget?.Id,
                null,
                pair.PairKey);
    }

    private static LegendConnectVerifiedTargetBatchResult VerifiedTargetBatchRejected(
        string sourceLanguage,
        string? targetLanguage,
        string errorCode,
        string message,
        IReadOnlyList<LegendConnectVerifiedTargetRow> rows) =>
        new(
            false,
            errorCode,
            message,
            sourceLanguage,
            targetLanguage,
            null,
            rows.Select(row => VerifiedTargetRow(row.RowNumber, "Failed", message, null, null, null, null)).ToList());

    private static LegendConnectVerifiedTargetRowResult VerifiedTargetRow(
        int rowNumber,
        string status,
        string message,
        Guid? sourceTextUnitId,
        Guid? targetTextUnitId,
        Guid? alignmentId,
        string? pairKey) => new(
            rowNumber,
            status,
            message,
            sourceTextUnitId,
            targetTextUnitId,
            alignmentId,
            pairKey);

    private static bool IsVerifiedTargetSuccess(LegendConnectVerifiedTargetRowResult row) => row.Status is
        "ExistingTargetVerified" or "ProviderTargetCorrected" or "FounderTargetAdded" or
        "FounderTargetCorrected" or "AlreadyVerified";

    private static string DescribeVerifiedTargetBatch(LegendConnectVerifiedTargetBatchResult result)
    {
        var reviewRows = result.Rows
            .Where(row => !IsVerifiedTargetSuccess(row))
            .Take(50)
            .Select(row => $"{row.RowNumber} {row.Status}");
        var reviewSuffix = string.Join(", ", reviewRows);
        if (result.Rows.Count(row => !IsVerifiedTargetSuccess(row)) > 50)
            reviewSuffix += ", additional rows";
        return $"Matched existing sources: {result.MatchedExistingSourceCount}; existing targets verified: {result.ExistingTargetVerifiedCount}; " +
            $"provider targets corrected: {result.ProviderTargetCorrectedCount}; Founder targets corrected: {result.FounderTargetCorrectedCount}; " +
            $"Founder targets added: {result.FounderTargetAddedCount}; already verified: {result.AlreadyVerifiedCount}; " +
            $"unmatched: {result.UnmatchedSourceCount}; ambiguous: {result.AmbiguousCount}; failed: {result.FailedCount}." +
            (string.IsNullOrWhiteSpace(reviewSuffix) ? string.Empty : $" Review rows: {reviewSuffix}.");
    }

    public async Task<LegendConnectQualityReviewActionResult> ApproveProviderObservationAsync(
        string founderUserId,
        Guid alignmentId,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null || alignmentId == Guid.Empty)
            return InvalidQualityReviewAction();

        var result = await Intelligence.ApproveProviderObservationAsync(alignmentId, cancellationToken);
        if (result.Succeeded)
        {
            await Curriculum.AttachValidatedAlignmentAsync(alignmentId, cancellationToken);
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

        var result = await Intelligence.RejectProviderObservationAsync(alignmentId, cancellationToken);
        if (result.Succeeded)
        {
            if (result.RetiredTargetTextUnitId is not null)
                await Curriculum.ReconcileSupersededExamplesAsync([result.RetiredTargetTextUnitId.Value], cancellationToken);
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

        var result = await Intelligence.LeaveProviderObservationUnresolvedAsync(alignmentId, cancellationToken);
        if (result.Succeeded)
            await WriteQualityReviewAuditAsync(founder, "FounderProviderObservationLeftUnresolved", result, alignmentId, cancellationToken);
        return ToQualityReviewActionResult(result);
    }

    /// <summary>
    /// Executes one Founder-authored multi-family curriculum manifest without
    /// introducing a second curriculum engine. Every family is preflighted by
    /// the existing curriculum authority before any mutation. Only after the
    /// complete manifest is valid are the same canonical single-family writes
    /// executed, under one database transaction.
    /// </summary>
    public async Task<LegendConnectCurriculumSubmissionResult> SubmitFounderCurriculumManifestAsync(
        string founderUserId,
        LegendConnectCurriculumManifestSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null)
        {
            return new LegendConnectCurriculumSubmissionResult(
                false, false, "founder_identity_required",
                "A verified Founder identity is required.", null, null, 0, 0);
        }

        var families = submission.Families?.ToArray() ?? [];
        if (families.Length == 0)
        {
            return new LegendConnectCurriculumSubmissionResult(
                false, false, "empty_curriculum_manifest",
                "The curriculum manifest must contain at least one explicit semantic family.",
                null, null, 0, 0);
        }

        var sourceDefinition = await _registry.GetEnabledLearningLanguageAsync(
            submission.SourceLanguageCode,
            cancellationToken);
        if (sourceDefinition is null)
        {
            return new LegendConnectCurriculumSubmissionResult(
                false, false, "source_language_training_unavailable",
                "Select one explicitly enabled source language for the complete curriculum manifest.",
                null, null, 0, 0);
        }
        var sourceLanguage = sourceDefinition.Code;

        // Manifest-wide preflight remains synchronous and mutation-free.
        // One invalid family or cross-example semantic declaration rejects the
        // complete manifest before durable acceptance. Expensive learning does
        // not run inside the HTTP request.
        var manifestValidation = await Curriculum.PreflightFounderManifestAsync(
            new LegendConnectCurriculumManifestSubmission(
                families,
                submission.CrossExampleSemanticRelationships,
                sourceLanguage),
            sourceLanguage,
            cancellationToken);
        if (manifestValidation is not null)
            return manifestValidation;

        // Retain the complete preflighted manifest.  Cross-example semantic
        // relationships are Founder-governed curriculum declarations just as
        // the families are; omitting them here would make accepted durable
        // work unable to project their governed evidence later.
        var payload = JsonSerializer.Serialize(
            new LegendConnectCurriculumManifestSubmission(
                families,
                submission.CrossExampleSemanticRelationships,
                sourceLanguage));
        var manifestHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var workIdentityBytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{founder}|{manifestHash}"));
        var workId = new Guid(workIdentityBytes.AsSpan(0, 16));
        var exampleCount = families.Sum(item => item.Examples?.Count ?? 0);
        var now = DateTime.UtcNow;

        var work = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleOrDefaultAsync(item => item.Id == workId, cancellationToken);
        if (work is not null)
        {
            if (string.Equals(work.ProcessingState, "Failed", StringComparison.Ordinal))
            {
                work.ProcessingState = "Pending";
                work.AttemptCount = 0;
                work.LastErrorCode = null;
                work.LastErrorMessage = null;
                work.LeaseExpiresUtc = null;
                work.UpdatedUtc = now;
                await _db.SaveChangesAsync(cancellationToken);
                return new LegendConnectCurriculumSubmissionResult(
                    true, true, null,
                    $"Curriculum manifest was requeued for bounded background processing. " +
                    $"{work.FamilyCount:N0} families / {work.ExampleCount:N0} examples are durable; " +
                    $"processing resumes from family {work.NextFamilyIndex + 1:N0}.",
                    null, null, work.ExampleCount, 0);
            }

            var completed = string.Equals(work.ProcessingState, "Completed", StringComparison.Ordinal);
            var capabilityReplayPending = completed &&
                work.CompletedLanguageIntelligenceEvaluatorVersion <
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
            return new LegendConnectCurriculumSubmissionResult(
                true, true, null,
                capabilityReplayPending
                    ? $"This exact curriculum manifest already completed, and its retained Founder-approved evidence is eligible for bounded evaluator v{LegendConnectLanguageIntelligenceEvaluatorVersion.Current} reconciliation. " +
                      $"{work.FamilyCount:N0} families / {work.ExampleCount:N0} examples remain canonical without duplicate confidence."
                    : completed
                    ? $"This exact curriculum manifest already completed. " +
                      $"{work.FamilyCount:N0} families / {work.ExampleCount:N0} examples were retained without duplicate confidence."
                    : $"This exact curriculum manifest is already {work.ProcessingState.ToLowerInvariant()}. " +
                      $"{work.FamilyCount:N0} families / {work.ExampleCount:N0} examples are durably queued; " +
                      $"{work.NextFamilyIndex:N0} families have completed.",
                null, null, work.ExampleCount, 0);
        }

        work = new LegendCurriculumManifestWorkItem
        {
            Id = workId,
            FounderUserId = founder,
            SourceLanguageCode = sourceLanguage,
            ManifestHash = manifestHash,
            PayloadJson = payload,
            FamilyCount = families.Length,
            ExampleCount = exampleCount,
            NextFamilyIndex = 0,
            TargetLanguageIntelligenceEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            ProcessingState = "Pending",
            AttemptCount = 0,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        _db.Set<LegendCurriculumManifestWorkItem>().Add(work);
        _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
        {
            Id = Guid.NewGuid(),
            FounderUserId = founder,
            Action = "FounderCurriculumManifestAccepted",
            Result = "Accepted",
            LanguageCode = sourceLanguage,
            Detail = Bound(
                $"Manifest {manifestHash[..12]} accepted: {families.Length} families / {exampleCount} examples. " +
                "Full learning executes through the existing curriculum authority in bounded background work.",
                500),
            OccurredUtc = now
        });
        await _db.SaveChangesAsync(cancellationToken);

        return new LegendConnectCurriculumSubmissionResult(
            true, false, null,
            $"Curriculum accepted. {families.Length:N0} families / {exampleCount:N0} examples are durably queued for bounded background learning. " +
            "The browser request no longer performs full structural analysis. Existing curriculum, corpus, Azure expansion, evidence, contradiction, maturity, and production gates remain authoritative.",
            null, null, exampleCount, 0);
    }

    public async Task<LegendConnectCurriculumSubmissionResult> SubmitFounderCurriculumAsync(
        string founderUserId,
        LegendConnectCurriculumBatchSubmission submission,
        CancellationToken cancellationToken = default)
    {
        // Compatibility surface only: one-family submissions enter the same
        // retained manifest and durable work authority as every other Founder
        // curriculum submission. No HTTP path may mutate a family directly.
        return await SubmitFounderCurriculumManifestAsync(
            founderUserId,
            new LegendConnectCurriculumManifestSubmission([submission], [], "en"),
            cancellationToken);
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
