using System.Globalization;
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
    private const int TranslationRouteAuditRecordLimit = 250;

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
        var duplicateCount = state.OperationalEvents.LongCount(item => item.Category == "DuplicatePrevention" && item.Status == "Prevented") +
            state.AuditEntries.LongCount(item => item.Result == "DuplicatePrevented");
        var translationOpportunities = state.Demand.Sum(item => item.TranslationRequestCount);
        var contextualInternalServed = state.Demand.Sum(item => item.ContextualInternalServeCount);
        var structuralInternalServed = state.Demand.Sum(item => item.StructuralInternalServeCount);
        var internalServed =
            state.Demand.Sum(item => item.TranslationMemoryHitCount) +
            structuralInternalServed +
            contextualInternalServed;
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
            providerCapacity,
            StructuralCompositionCharactersAvoided:
                state.SystemUsage.Sum(item => item.StructuralCompositionCharactersAvoided),
            StructuralInternalServeCount:
                structuralInternalServed);
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
            "memory-avoided" or "structural-avoided" or "context-avoided" or "quota-denied" or "provider-failures" or
            "group-target-reuse" or "high-consumption-accounts")
            return await BuildUsageMetricDetailAsync(key, cancellationToken);

        var state = await LoadStateAsync(cancellationToken);
        return key switch
        {
            "active-languages" => BuildLanguageMetricDetail(state),
            "directional-pairs" => BuildPairMetricDetail(state),
            "learning-failures" => BuildLearningFailureMetricDetail(state),
            "duplicate-prevention" or "readiness-duplicates-prevented" => BuildDuplicateMetricDetail(state, key),
            "approved-candidates" or "eligible-pending" or "rejected-ineligible" or "pairs-awaiting-knowledge" => BuildCandidateMetricDetail(state, key),
            "same-language-bypasses" or "translation-memory-hits" or "provider-fallback-required" or "trusted-structural-served" or "trusted-contextual-served" or "provider-avoidance" or "provider-dependency" => BuildDemandMetricDetail(state, key),
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
                new[] { "Pair", "Requests", "Memory hits", "Provider work required", "Structural served", "Context served", "Provider characters", "Last request" },
                state.Demand.OrderByDescending(item => item.LastRequestedUtc).Select(item => new[]
                {
                    item.PairKey, Display(item.TranslationRequestCount), Display(item.TranslationMemoryHitCount), Display(item.AzureFallbackCount),
                    Display(item.StructuralInternalServeCount), Display(item.ContextualInternalServeCount),
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
        var quality = await _intelligence.GetTranslationQualityAsync(cancellationToken);
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
        "translation-memory-hits" => "Translation Memory hits",
        "provider-fallback-required" => "Provider fallback required",
        "trusted-structural-served" => "Trusted structural served",
        "trusted-contextual-served" => "Trusted contextual served",
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
        _intelligence.GetTranslationQualityAsync(cancellationToken);

    public Task<LegendTargetRealizationReviewSnapshot> GetTargetRealizationReviewAsync(
        CancellationToken cancellationToken = default) =>
        _curriculum.GetTargetRealizationReviewAsync(cancellationToken);

    public async Task<LegendTargetRealizationReviewActionResult> VerifyTargetRealizationCandidateAsync(
        string founderUserId,
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        var founder = NormalizeFounder(founderUserId);
        if (founder is null)
            return new LegendTargetRealizationReviewActionResult(
                false, "founder_identity_required", "A verified Founder identity is required.", candidateId, "Unavailable", null);

        var result = await _curriculum.VerifyTargetRealizationCandidateAsync(founder, candidateId, cancellationToken);
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

        var result = await _curriculum.RejectTargetRealizationCandidateAsync(founder, candidateId, cancellationToken);
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
                ? await _founderTrainingIngestion.SubmitAsync(founder, approved, cancellationToken)
                : await _corpus.SubmitApprovedKnowledgeAsync(
                    approved,
                    cancellationToken,
                    reusableSourceTextUnitId,
                    reusableTargetTextUnitId);
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

        var trusted = await _intelligence.TryGetTrustedExactMemoryAsync(
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

        foreach (var family in families)
        {
            var validation = await _curriculum.PreflightFounderEnglishBatchAsync(family, cancellationToken);
            if (validation is not null)
                return validation;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var results = new List<LegendConnectCurriculumSubmissionResult>(families.Length);
        try
        {
            foreach (var family in families)
            {
                var result = await _curriculum.SubmitFounderEnglishBatchAsync(family, cancellationToken);
                if (!result.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _db.ChangeTracker.Clear();
                    return result;
                }

                results.Add(result);
                _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
                {
                    Id = Guid.NewGuid(),
                    FounderUserId = founder,
                    Action = "FounderCurriculumSubmitted",
                    Result = result.DuplicatePrevented ? "DuplicatePrevented" : "Succeeded",
                    LanguageCode = "en",
                    Detail = Bound(result.Message ?? result.ErrorCode, 500),
                    OccurredUtc = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            throw;
        }

        var exampleCount = results.Sum(item => item.EnglishExampleCount);
        var targetExpansionCount = results.Sum(item => item.TargetExpansionCount);
        var duplicateFamilies = results.Count(item => item.DuplicatePrevented);
        return new LegendConnectCurriculumSubmissionResult(
            true,
            duplicateFamilies == results.Count,
            null,
            $"Saved {results.Count:N0} explicit semantic families with {exampleCount:N0} canonical English examples. " +
            $"{duplicateFamilies:N0} families were canonical reuse only. Existing language-isolated evidence, Azure expansion, maturity, contradiction, and production gates remain in force.",
            null,
            null,
            exampleCount,
            targetExpansionCount);
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
        var internalServed = memoryHits + structuralInternal + contextualInternal;
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
            lastProviderAcquisition,
            structuralInternal);
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
        IReadOnlyList<LegendFounderTrainingSubmissionUnit> FounderTrainingSubmissionUnits);

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
