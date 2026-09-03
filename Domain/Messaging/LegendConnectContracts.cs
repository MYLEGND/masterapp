using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Domain.Messaging;

public sealed record LegendLanguageDefinitionSnapshot(
    string Code,
    string BaseCode,
    string DisplayName,
    string NativeName,
    bool IsEnabled,
    bool IsTranslationEnabled,
    bool IsLearningEnabled,
    string DatasetNamespace,
    string StoragePartition);

public sealed record LegendLanguagePairSnapshot(
    string PairKey,
    string SourceLanguageCode,
    string TargetLanguageCode,
    bool IsEnabled,
    string TranslationMemoryPartition,
    int CorpusCoverage,
    string QualityState,
    string? ActiveModelVersion,
    string ProviderFallbackPolicy);

public sealed record TranslationLearningCandidate(
    Guid SourceMessageId,
    string SourceLanguageCode,
    string TargetLanguageCode,
    string SourceText,
    string TargetText,
    string Provider);

public interface ILegendLanguageRegistry
{
    Task<string?> NormalizeEnabledTranslationLanguageAsync(
        string? language,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an already-registered enabled language without provisioning
    /// baseline rows. Zero-write serving paths use this lookup.
    /// </summary>
    Task<string?> NormalizeEnabledTranslationLanguageReadOnlyAsync(
        string? language,
        CancellationToken cancellationToken = default);

    Task<LegendLanguageDefinitionSnapshot?> GetLanguageAsync(
        string? language,
        CancellationToken cancellationToken = default);

    Task<LegendLanguageDefinitionSnapshot?> GetEnabledLearningLanguageAsync(
        string? language,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegendLanguageDefinitionSnapshot>> ListEnabledTranslationLanguagesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the existing directional serving eligibility without creating or
    /// enabling a pair. Production routing must never turn an unsupported pair
    /// into a supported one as a side effect of receiving a message.
    /// </summary>
    Task<LegendLanguagePairSnapshot?> GetEnabledPairAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    Task<LegendLanguagePairSnapshot?> GetOrCreateEnabledPairAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}

public interface ITranslationLearningPublisher
{
    Task TryPublishAsync(
        TranslationLearningCandidate candidate,
        CancellationToken cancellationToken = default);
}

public interface ITranslationProvider : ITranslationService
{
    string ProviderName { get; }
}

/// <summary>
/// A Founder-approved knowledge contribution. The caller may describe context,
/// but cannot select a provider, bypass validation, or write into a separate
/// language store.
/// </summary>
public sealed record LegendConnectKnowledgeSubmission(
    string SourceLanguageCode,
    string SourceText,
    string? TargetLanguageCode,
    string? TargetText,
    string? ContextCategory,
    string? UsageRegister,
    string? RegionalVariant,
    string Provenance);

public sealed record LegendConnectKnowledgeSubmissionResult(
    bool Succeeded,
    bool DuplicatePrevented,
    string? ErrorCode,
    string? Message,
    string SourceLanguageCode,
    string? TargetLanguageCode,
    string? PairKey,
    Guid? SourceTextUnitId,
    Guid? TargetTextUnitId,
    Guid? AlignmentId,
    Guid? TrainingSubmissionId = null,
    int AtomicUnitCount = 0,
    int NewCanonicalUnitCount = 0,
    int ReusedCanonicalUnitCount = 0,
    int QueuedCoverageCount = 0);

/// <summary>
/// One Founder-supplied, verified target realization for an already canonical
/// source unit. This is an input to the existing alignment and correction
/// authorities; it never represents a second source-curriculum record.
/// </summary>
public sealed record LegendConnectVerifiedTargetSubmission(
    string SourceLanguageCode,
    string TargetLanguageCode,
    IReadOnlyList<LegendConnectVerifiedTargetRow> Rows,
    string? ContextCategory,
    string? UsageRegister,
    string? RegionalVariant);

public sealed record LegendConnectVerifiedTargetRow(
    int RowNumber,
    string SourceText,
    string TargetText);

/// <summary>
/// Precise outcome for one verified-target row. Status values distinguish
/// successful canonical transitions from fail-closed source resolution.
/// </summary>
public sealed record LegendConnectVerifiedTargetRowResult(
    int RowNumber,
    string Status,
    string Message,
    Guid? SourceTextUnitId,
    Guid? TargetTextUnitId,
    Guid? AlignmentId,
    string? PairKey);

/// <summary>
/// A bounded, per-row result for Founder verified-target mode. Counts are
/// projections of <see cref="Rows"/> and do not persist another workflow.
/// </summary>
public sealed record LegendConnectVerifiedTargetBatchResult(
    bool Succeeded,
    string? ErrorCode,
    string? Message,
    string SourceLanguageCode,
    string? TargetLanguageCode,
    string? PairKey,
    IReadOnlyList<LegendConnectVerifiedTargetRowResult> Rows)
{
    public int MatchedExistingSourceCount => Rows.Count(item => item.Status is
        "ExistingTargetVerified" or "ProviderTargetCorrected" or "FounderTargetCorrected" or
        "FounderTargetAdded" or "AlreadyVerified");
    public int ExistingTargetVerifiedCount => Rows.Count(item => item.Status == "ExistingTargetVerified");
    public int ProviderTargetCorrectedCount => Rows.Count(item => item.Status == "ProviderTargetCorrected");
    public int FounderTargetCorrectedCount => Rows.Count(item => item.Status == "FounderTargetCorrected");
    public int FounderTargetAddedCount => Rows.Count(item => item.Status == "FounderTargetAdded");
    public int AlreadyVerifiedCount => Rows.Count(item => item.Status == "AlreadyVerified");
    public int UnmatchedSourceCount => Rows.Count(item => item.Status == "Unmatched");
    public int AmbiguousCount => Rows.Count(item => item.Status == "Ambiguous");
    public int FailedCount => Rows.Count(item => item.Status == "Failed");
}

/// <summary>
/// A controlled semantic curriculum example. Variations identify meaning that
/// changed; their realization is learned independently in every language.
/// </summary>
/// <summary>
/// One explicit Founder-controlled meaning node for a curriculum example.
/// The node identifies a Founder-controlled semantic dimension/value and
/// one exact surface span in that example.  It does not infer a role from
/// word order, a dictionary, or an unreviewed parser.
/// </summary>
public sealed record LegendConnectMeaningNodeSubmission(
    string NodeKey,
    string SemanticDimension,
    string SemanticValue,
    string SurfaceText,
    string? ClauseKey = null,
    int SurfaceOccurrence = 1);

/// <summary>
/// One explicit directed relation between two named meaning nodes in the
/// same Founder example.  RelationKind is controlled evidence, not a runtime
/// routing rule; its production maturity is derived separately.
/// </summary>
public sealed record LegendConnectMeaningRelationSubmission(
    string SourceNodeKey,
    string RelationKind,
    string TargetNodeKey,
    string? ClauseKey = null);

/// <summary>
/// Founder-declared, semantic-only instruction for resolving a reference to a
/// previously established discourse entity.  The selector is a node in the
/// same controlled graph; it never names a prompt or an answer.  The runtime
/// applies this only after the rule has independently matured.
/// </summary>
public sealed record LegendConnectDiscourseReferenceSubmission(
    string SelectorNodeKey,
    string EntitySemanticDimension,
    string ResolutionMode,
    int? SelectionRank = null,
    IReadOnlyList<string>? AllowedSourceRoles = null,
    bool ReplacesActiveBinding = false);

/// <summary>
/// Founder-authored utterance meaning graph attached to one controlled
/// curriculum example.  Nodes and edges retain their example provenance so
/// future abstraction never turns one sentence into a global rule.
/// </summary>
public sealed record LegendConnectMeaningGraphSubmission(
    IReadOnlyList<LegendConnectMeaningNodeSubmission> Nodes,
    IReadOnlyList<LegendConnectMeaningRelationSubmission> Relations,
    IReadOnlyList<LegendConnectDiscourseReferenceSubmission>? DiscourseReferences = null);

public sealed record LegendConnectCurriculumExampleSubmission(
    string Text,
    IReadOnlyDictionary<string, string> Variations,
    LegendConnectMeaningGraphSubmission? MeaningGraph = null,
    string? SemanticExampleKey = null);

/// <summary>
/// Founder-declared edge between two controlled semantic examples. The keys
/// identify Founder-owned meaning-graph identities, not prompt or answer
/// text. RelationshipSemanticIdentity is a generic controlled semantic label
/// whose meaning remains evidence until independently matured.
/// </summary>
public sealed record LegendConnectCrossExampleSemanticRelationshipSubmission(
    string SourceSemanticExampleKey,
    string RelationshipSemanticIdentity,
    string ResultSemanticExampleKey);

/// <summary>
/// One explicit, language-neutral semantic frame pattern. Keys and values are
/// Founder-controlled curriculum dimensions; a value beginning with '$' binds
/// one semantic variable across the source and result frame.
/// </summary>
public sealed record LegendConnectSemanticFrameSubmission(
    IReadOnlyDictionary<string, string> Dimensions);

/// <summary>
/// Evidence that a governed source semantic frame can transform into a
/// governed result semantic frame. This is not a text-to-text answer mapping:
/// individual curriculum examples supply the evidence after persistence.
/// </summary>
public sealed record LegendConnectSemanticTransitionSubmission(
    LegendConnectSemanticFrameSubmission Source,
    LegendConnectSemanticFrameSubmission Result);

/// <summary>
/// Founder-declared grounding for a non-literal source-frame dimension. It
/// identifies the already-controlled surface dimension which carries that
/// meaning in a source example; it is not a runtime routing rule.
/// </summary>
public sealed record LegendConnectSemanticSpanGroundingSubmission(
    string SemanticDimension,
    string SurfaceDimension);

public sealed record LegendConnectCurriculumBatchSubmission(
    string FamilyKey,
    string? SemanticCategory,
    IReadOnlyList<LegendConnectCurriculumExampleSubmission> Examples,
    IReadOnlyList<LegendConnectSemanticTransitionSubmission>? SemanticTransitions = null,
    IReadOnlyList<LegendConnectSemanticSpanGroundingSubmission>? SemanticSpanGroundings = null);

/// <summary>
/// One Founder action may carry multiple explicitly bounded semantic families.
/// Every family still uses the canonical single-family curriculum submission
/// and is validated/persisted only by the existing Legend Connect authority.
/// </summary>
public sealed record LegendConnectCurriculumManifestSubmission(
    IReadOnlyList<LegendConnectCurriculumBatchSubmission> Families,
    IReadOnlyList<LegendConnectCrossExampleSemanticRelationshipSubmission>? CrossExampleSemanticRelationships,
    string SourceLanguageCode);

public sealed record LegendConnectCurriculumSubmissionResult(
    bool Succeeded,
    bool DuplicatePrevented,
    string? ErrorCode,
    string? Message,
    string? FamilyKey,
    Guid? CurriculumFamilyId,
    int SourceExampleCount,
    int TargetExpansionCount);

public sealed record LegendConnectLanguageHealthSnapshot(
    string LanguageCode,
    string DisplayName,
    bool IsEnabled,
    string StoragePartition,
    long CanonicalEntryCount,
    long TranslationMemoryRelationshipCount,
    long ContextRelationshipCount,
    IReadOnlyList<string> DirectionalPairs,
    long DemandCount,
    int Coverage,
    string QualityState,
    string HealthState,
    DateTime? LastSuccessfulLearningUtc,
    DateTime? LastSuccessfulWriteUtc,
    long DuplicatePreventionCount,
    IReadOnlyList<LegendConnectOperationalEventSnapshot> RecentErrors,
    long ApprovedCandidateCount = 0,
    long PendingCandidateCount = 0,
    decimal AzureDependencyRate = 0m,
    DateTime? LastProviderAcquisitionUtc = null,
    DateTime? LastFounderTrainingUtc = null);

public sealed record LegendConnectPairHealthSnapshot(
    string PairKey,
    string SourceLanguageCode,
    string TargetLanguageCode,
    long DemandCount,
    long TranslationCount,
    long TranslationMemoryReuseCount,
    // Provider-backed work required after internal routing. The established
    // property name remains for wire compatibility; provider calls and billed
    // characters are represented separately by the dashboard metrics.
    long AzureFallbackCount,
    decimal AzureFallbackRate,
    int Coverage,
    string QualityState,
    string HealthState,
    DateTime? LastSuccessfulAlignmentUtc,
    DateTime? LastLearningActivityUtc,
    long FailureCount,
    IReadOnlyList<LegendConnectAlignmentSnapshot> RecentAlignments,
    IReadOnlyList<LegendConnectOperationalEventSnapshot> RecentErrors,
    long ContextualInternalServeCount = 0,
    decimal ProviderAvoidanceRate = 0m,
    decimal AzureDependencyRate = 0m,
    decimal InternalCoverageRate = 0m,
    decimal InternalQualityConfidence = 0m,
    int CoverageAdditionsLast30Days = 0,
    long ApprovedBacklog = 0,
    DateTime? LastProviderAcquisitionUtc = null,
    long StructuralInternalServeCount = 0,
    long PromotedTranslationModelServeCount = 0,
    long PromotedTranslationModelFailureCount = 0,
    long ProviderObservationReuseCount = 0,
    long NativeTranslationIntelligenceServeCount = 0,
    long ReconciledTerminalRouteCount = 0,
    long RoutingReconciliationGap = 0);

public sealed record LegendConnectAlignmentSnapshot(
    Guid Id,
    string SourceText,
    string TargetText,
    string QualityState,
    bool HumanVerified,
    DateTime UpdatedUtc);

/// <summary>
/// A retained canonical text asset that the central eligibility policy has
/// approved for Legend Connect learning. This is deliberately distinct from
/// message history: private message text is never projected through this type.
/// </summary>
public sealed record LegendConnectLanguageTextUnitSnapshot(
    Guid Id,
    string Text,
    string Provenance,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// A safe, active directional alignment between two approved canonical text
/// assets. Both texts have passed the existing retention/eligibility policy.
/// </summary>
public sealed record LegendConnectLanguageAlignmentDetailSnapshot(
    Guid Id,
    string PairKey,
    string SourceLanguageCode,
    string SourceText,
    string TargetLanguageCode,
    string TargetText,
    string Provider,
    string? ProviderModel,
    decimal? Confidence,
    string QualityState,
    bool HumanVerified,
    int ObservationCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// A generic contextual relationship between approved language assets. It
/// carries stored context metadata without inventing language-specific rules.
/// </summary>
public sealed record LegendConnectLanguageContextRelationshipSnapshot(
    Guid Id,
    string? PairKey,
    string SourceLanguageCode,
    string SourceText,
    string RelatedLanguageCode,
    string RelatedText,
    string RelationshipKind,
    string? ContextCategory,
    string? UsageRegister,
    string? RegionalVariant,
    decimal Confidence,
    string QualityState,
    string Provenance,
    int ObservationCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// Metadata for learning pipeline activity. Text is intentionally omitted so
/// private message payloads cannot become visible in Founder operations.
/// Approved text is available only through the canonical asset/alignment types.
/// </summary>
public sealed record LegendConnectLanguageLearningActivitySnapshot(
    Guid Id,
    string PairKey,
    string SourceLanguageCode,
    string TargetLanguageCode,
    string Provider,
    string Provenance,
    string EligibilityState,
    string ProcessingState,
    int AttemptCount,
    DateTime CreatedUtc,
    DateTime? ProcessedUtc,
    string? FailureCode,
    string? PromotionOutcome = null);

/// <summary>
/// Founder-only inspection projection for one language's real server-owned
/// learning dataset. Collections are deliberately bounded by DetailRecordLimit
/// for a responsive modal; their health counters remain the exact totals.
/// </summary>
public sealed record LegendConnectLanguageKnowledgeSnapshot(
    LegendConnectLanguageHealthSnapshot Health,
    int DetailRecordLimit,
    long LearningActivityCount,
    IReadOnlyList<LegendConnectLanguageTextUnitSnapshot> CanonicalEntries,
    IReadOnlyList<LegendConnectLanguageAlignmentDetailSnapshot> ActiveAlignments,
    IReadOnlyList<LegendConnectLanguageContextRelationshipSnapshot> ContextRelationships,
    IReadOnlyList<LegendConnectPairHealthSnapshot> DirectionalPairs,
    IReadOnlyList<LegendConnectLanguageLearningActivitySnapshot> RecentLearningActivity,
    IReadOnlyList<LegendConnectStructuralPatternSnapshot>? StructuralPatterns = null,
    IReadOnlyList<LegendConnectStructuralRelationshipSnapshot>? StructuralRelationships = null);

/// <summary>
/// Privacy-safe structural-learning projection. It intentionally contains no
/// message body or candidate translation text.
/// </summary>
public sealed record LegendConnectStructuralPatternSnapshot(
    string FamilyKey,
    string LanguageCode,
    string VariationDimension,
    string MaturityState,
    int SupportCount,
    int ContradictionCount,
    bool IsProductionEligible,
    DateTime UpdatedUtc);

/// <summary>
/// Privacy-safe Founder projection of reusable structural evidence accumulated
/// across independent curriculum families. This is intentionally distinct from
/// a per-family structural pattern: it is the existing aggregate that can earn
/// supported maturity without granting production composition authority.
/// </summary>
public sealed record LegendConnectStructuralRelationshipSnapshot(
    string PairKey,
    string LanguageCode,
    string VariationDimension,
    string MaturityState,
    int SupportCount,
    int IndependentSourceCount,
    int HumanVerifiedSupportCount,
    int ProviderOnlySupportCount,
    int ContradictionCount,
    bool IsProductionEligible,
    DateTime UpdatedUtc);

/// <summary>
/// Privacy-safe Founder projection of the shared provider-quality evidence
/// authority. Source text is included only for Founder-approved training
/// material; operational private-message text is intentionally excluded.
/// </summary>
public sealed record LegendConnectTranslationQualityReviewSnapshot(
    Guid AlignmentId,
    string PairKey,
    string SourceLanguageCode,
    string SourceText,
    string TargetLanguageCode,
    string ProviderTargetText,
    string Provider,
    string Provenance,
    string QualityState,
    int SupportingEvidenceCount,
    int ContradictionCount,
    string ReasonForReview,
    IReadOnlyList<string> EvidenceReasons,
    DateTime ObservedUtc);

public sealed record LegendConnectTranslationQualitySnapshot(
    long NeedsReviewCount,
    long ProviderObservationCount,
    long SupportedObservationCount,
    long ContradictionCount,
    long HumanVerifiedAlignmentCount,
    IReadOnlyList<LegendConnectTranslationQualityReviewSnapshot> ReviewItems);

public sealed record LegendConnectQualityReviewActionResult(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    string? SourceLanguageCode = null,
    string? PairKey = null);

public sealed record LegendConnectOperationalEventSnapshot(
    DateTime OccurredUtc,
    string Category,
    string Severity,
    string Status,
    string? LanguageCode,
    string? PairKey,
    string? CorrelationId,
    string? ErrorCode,
    string? Summary,
    bool IsResolved);

/// <summary>
/// Server-owned capacity projection for the one Azure Translator resource.
/// F0's free monthly allowance and the provider's rolling hourly velocity
/// ceiling are distinct contracts. Consumption is derived from the one durable
/// reservation ledger used before each Azure request.
/// </summary>
public sealed record LegendConnectProviderCapacitySnapshot(
    string Provider,
    bool IsSynchronized,
    string Status,
    string? ResourceName,
    string? ResourceId,
    string? Tier,
    DateOnly BillingPeriodStart,
    DateOnly BillingPeriodEnd,
    long? MonthlyIncludedCharacterAllowance,
    long MonthlyCharactersConsumed,
    long MonthlyReservedCharacters,
    long? MonthlyRemainingCharacters,
    long? MonthlyLiveReserveCharacters,
    long? MaximumSafeCorpusConsumptionCharacters,
    int HourlyCapacityWindowMinutes,
    DateTime HourlyWindowStartUtc,
    DateTime HourlyWindowEndUtc,
    long? HourlyCharacterLimit,
    long HourlyCharactersConsumed,
    long HourlyReservedCharacters,
    long? HourlyRemainingCharacters,
    long? HourlyLiveReserveCharacters,
    long? SafeAcquisitionCharacters,
    DateTime RefreshedUtc,
    string? Detail);

public sealed record LegendConnectDashboardSnapshot(
    IReadOnlyList<LegendConnectLanguageHealthSnapshot> Languages,
    IReadOnlyList<LegendConnectPairHealthSnapshot> Pairs,
    long SameLanguageBypassCount,
    long TranslationMemoryHitCount,
    // Provider-backed work required after internal routing, not completed
    // Azure calls. See ProviderOperationCount for actual provider attempts.
    long AzureFallbackCount,
    long AzureCharactersUsed,
    long ConfiguredMonthlyCapacity,
    long LiveReserveCharacters,
    long? RemainingSafeCapacity,
    long LearningJobCount,
    long FailedLearningJobCount,
    long DuplicatePreventionCount,
    DateTime? LastSuccessfulLearningUtc,
    IReadOnlyList<LegendConnectOperationalEventSnapshot> RecentOperationalEvents,
    long ProviderOperationCount = 0,
    long ProviderBillableCharacters = 0,
    long SameLanguageCharactersAvoided = 0,
    long TranslationMemoryCharactersAvoided = 0,
    long ContextualCharactersAvoided = 0,
    long QuotaDeniedRequestCount = 0,
    long ProviderFailureCount = 0,
    long GroupUniqueTargetReuseCount = 0,
    long ContextualInternalServeCount = 0,
    decimal ProviderAvoidanceRate = 0m,
    decimal AzureDependencyRate = 0m,
    decimal InternalCoverageRate = 0m,
    long ConsumedLiveCharacters = 0,
    long ConsumedCorpusCharacters = 0,
    long ReservedProviderCharacters = 0,
    long? SafeAcquisitionCapacity = null,
    DateOnly? BillingPeriodStart = null,
    DateOnly? BillingPeriodEnd = null,
    long ConsentedLiveLearningAccountCount = 0,
    long EligibleConsentedLiveTranslationCount = 0,
    long PromotedConsentedLiveTranslationCount = 0,
    long ReusedConsentedLiveTranslationCount = 0,
    long PendingConsentedLiveTranslationCount = 0,
    long FounderRawSubmissionCount = 0,
    long FounderAtomicLearningUnitCount = 0,
    long SupersededLegacyMultiUnitAssetCount = 0,
    long ActiveDirectionalAtomicAlignmentCount = 0,
    LegendConnectProviderCapacitySnapshot? ProviderCapacity = null,
    long StructuralCompositionCharactersAvoided = 0,
    long StructuralInternalServeCount = 0,
    long PromotedTranslationModelServeCount = 0,
    long PromotedTranslationModelFailureCount = 0,
    long ProviderObservationReuseCount = 0,
    long NativeTranslationIntelligenceServeCount = 0,
    long ReconciledTerminalRouteCount = 0,
    long TranslationRoutingReconciliationGap = 0,
    long PromotedTranslationModelCharactersAvoided = 0,
    long ProviderObservationCharactersAvoided = 0,
    string TranslationServingCapability = "translation",
    long CrossLanguageTranslationRequestCount = 0);

/// <summary>
/// One coherent Founder dashboard read. The selected language and pair are
/// derived from the same operational state as the dashboard so opening a
/// detailed route does not repeat the complete operational projection.
/// </summary>
public sealed record LegendConnectDashboardProjectionSnapshot(
    LegendConnectDashboardSnapshot Dashboard,
    LegendConnectLanguageKnowledgeSnapshot? SelectedLanguageKnowledge,
    LegendConnectPairHealthSnapshot? SelectedPair);

/// <summary>
/// Bounded initial projection for the Founder Legend Connect page. It is a
/// navigation and summary surface only: detailed curriculum, evidence, model,
/// and learning records are intentionally requested through the corresponding
/// paged Founder section after the user opens it.
/// </summary>
public sealed record LegendConnectFounderShellSnapshot(
    IReadOnlyList<LegendConnectFounderLanguageOptionSnapshot> Languages,
    LegendConnectFounderLanguageSummarySnapshot? SelectedLanguage);

public sealed record LegendConnectFounderLanguageOptionSnapshot(
    string LanguageCode,
    string DisplayName,
    bool IsEnabled);

public sealed record LegendConnectFounderLanguageSummarySnapshot(
    string LanguageCode,
    string DisplayName,
    string HealthState,
    long CanonicalEntryCount,
    long CurriculumExampleCount,
    long CompositionalAnchorCount,
    long ActiveRelationshipCount,
    long PendingLearningCount,
    long CandidateCount,
    long OpenIssueCount,
    DateTime? LastCurriculumUpdateUtc);

/// <summary>
/// A single server-ranked, keyset-paginated Founder inspection page. Rows are
/// display projections of the existing authorities, never a browser cache or
/// a separate knowledge store. A null cursor means there is no next page.
/// </summary>
public sealed record LegendConnectFounderSectionPageSnapshot(
    string Section,
    string LanguageCode,
    string? Search,
    int PageSize,
    string? NextCursor,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? EmptyMessage = null);

/// <summary>
/// A Founder-safe, record-level explanation for one live dashboard metric.
/// These rows are projections of the established operational ledgers and
/// corpus records; they never create a second reporting or learning store.
/// </summary>
public sealed record LegendConnectMetricDetailSectionSnapshot(
    string Title,
    string Description,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<string> RowTones);

public sealed record LegendConnectMetricDetailSnapshot(
    string MetricKey,
    string Title,
    string Context,
    string Description,
    IReadOnlyList<LegendConnectMetricDetailSectionSnapshot> Sections);

/// <summary>
/// Founder-safe evidence for one derived target realization. All text comes
/// from retained Founder-approved curriculum or a retained provider
/// observation; private message bodies are never projected here.
/// </summary>
public sealed record LegendTargetRealizationEvidenceSnapshot(
    Guid Id,
    string SourceText,
    string TargetText,
    int TargetStartTokenIndex,
    int TargetTokenLength,
    bool HumanVerifiedSupport,
    string Provenance);

/// <summary>
/// A reviewable, non-authoritative target-realization hypothesis. Founder
/// verification is required before an existing canonical anchor is created.
/// </summary>
public sealed record LegendTargetRealizationCandidateSnapshot(
    Guid Id,
    string PairKey,
    string SourceLanguageCode,
    string TargetLanguageCode,
    string VariationDimension,
    string SemanticValue,
    string TargetRealization,
    string SlotSignature,
    string TemplatePreview,
    string VerificationState,
    string MaturityState,
    int SupportCount,
    int IndependentSourceCount,
    int HumanVerifiedSupportCount,
    int ProviderOnlySupportCount,
    int ContradictionCount,
    decimal Confidence,
    bool IsProductionEligible,
    IReadOnlyList<LegendTargetRealizationEvidenceSnapshot> Evidence);

public sealed record LegendTargetRealizationReviewSnapshot(
    long CandidateCount,
    long FounderVerifiedCount,
    long RejectedCount,
    long ContradictedCount,
    IReadOnlyList<LegendTargetRealizationCandidateSnapshot> Candidates);

public sealed record LegendTargetRealizationReviewActionResult(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    Guid CandidateId,
    string VerificationState,
    Guid? VerifiedAnchorId);

/// <summary>
/// One exact semantic component discovered by a non-authoritative machine
/// conversation. It is a proposal payload only and cannot itself create
/// canonical language knowledge.
/// </summary>
public sealed record LegendConnectMachineTeachingComponentSubmission(
    string Dimension,
    string Value,
    string SurfaceForm);

public sealed record LegendConnectMachineTeachingExampleSubmission(
    string SourceText,
    string? TargetText,
    IReadOnlyList<LegendConnectMachineTeachingComponentSubmission> Components);

public enum LegendConnectMachineObservationOrigin
{
    ConversationObservation,
    ExternalResearchObservation
}

/// <summary>
/// Exact external-observation lineage attached to a Founder-authorized
/// MachineProposed candidate. It is evidence supplied to the existing critic;
/// it is never canonical knowledge, a serving receipt, or an admission grant.
/// </summary>
public sealed record LegendConnectResearchRetentionLineage(
    Guid RequestId,
    Guid SessionId,
    string ConclusionIdentity,
    LegendConnectResearchOutcomeState OutcomeState,
    LegendConnectResearchEvidenceOrigin EvidenceOrigin,
    IReadOnlyList<LegendConnectResearchMaterialClaimEvidence> MaterialClaims,
    IReadOnlyList<LegendConnectCitation> Citations,
    LegendConnectResearchCitationValidationReceipt CitationValidation,
    string ResearchAuthorizationProvenance,
    string? ResearchAuthorizationCorrelationId,
    string ObservationIdentity,
    string CodeSha,
    string ConfigurationIdentity);

/// <summary>
/// A bounded machine-derived teaching candidate. This contract feeds the
/// existing LegendCorpusCandidate / LegendLanguageTeacherProposal lifecycle;
/// it is not a second memory, curriculum, or evidence store.
/// </summary>
public sealed record LegendConnectMachineTeachingSubmission(
    string SourceLanguageCode,
    string TargetLanguageCode,
    string FamilyKey,
    string SemanticCategory,
    string Rationale,
    decimal Confidence,
    IReadOnlyList<LegendConnectMachineTeachingExampleSubmission> Examples,
    IReadOnlyList<LegendConnectSemanticTransitionSubmission>? SemanticTransitions = null,
    string CapabilityIdentity = LegendConnectMachineTeachingSubmission.TranslationCapability,
    string CategoryIdentity = LegendConnectMachineTeachingSubmission.ReusableSemanticCategory,
    LegendConnectMachineObservationOrigin ObservationOrigin =
        LegendConnectMachineObservationOrigin.ConversationObservation,
    LegendConnectResearchRetentionLineage? ResearchObservationLineage = null)
{
    public const string TranslationCapability = "translation";
    public const string SameLanguageSemanticCapability =
        "same_language_semantic";
    public const string ReusableSemanticCategory =
        "reusable_semantic";

    public static bool IsSupportedIdentity(
        string? capabilityIdentity,
        string? categoryIdentity,
        bool sameLanguage) =>
        string.Equals(
            categoryIdentity,
            ReusableSemanticCategory,
            StringComparison.Ordinal) &&
        (
            sameLanguage
                ? string.Equals(
                    capabilityIdentity,
                    SameLanguageSemanticCapability,
                    StringComparison.Ordinal)
                : string.Equals(
                    capabilityIdentity,
                    TranslationCapability,
                    StringComparison.Ordinal)
        );

    public static string CandidateCategoryIdentity(
        string capabilityIdentity,
        string categoryIdentity) =>
        capabilityIdentity + ":" + categoryIdentity;
}

public sealed record LegendConnectMachineTeachingSubmissionResult(
    bool Succeeded,
    bool DuplicatePrevented,
    string State,
    string? ErrorCode,
    string Message,
    Guid? CorpusCandidateId,
    Guid? ProposalId,
    bool ProposalAlreadyExisted = false);

/// <summary>
/// The only success receipt exposed after a confirmed conversational
/// MachineProposed mutation. It proves durable candidate/proposal identity
/// while explicitly denying canonical or runtime-serving authority.
/// </summary>
public sealed record LegendConnectMachineTeachingMutationReceipt(
    bool Succeeded,
    Guid CandidateId,
    Guid ProposalId,
    string DurableState,
    string Provenance,
    string AuthorizationCorrelation,
    string ServingStatus,
    string CanonicalStatus,
    string? ResearchObservationIdentity = null)
{
    public const string RequiredProvenance = "MachineProposed";
    public const string RequiredServingStatus = "NonServing";
    public const string RequiredCanonicalStatus = "NonCanonical";
}

/// <summary>
/// One bounded retrieval result projected from existing LEGEND evidence.
/// AuthorityRank is presentation metadata only; the underlying provenance,
/// validation and contradiction records remain authoritative.
/// </summary>
public sealed record LegendConnectRetainedKnowledgeItemSnapshot(
    string Kind,
    string AuthorityState,
    int AuthorityRank,
    string Provenance,
    string? LanguageCode,
    string? PairKey,
    string Content,
    string? RelatedContent,
    decimal? Confidence,
    bool IsCanonical,
    bool IsContradicted,
    string? ActiveModelVersion,
    DateTime UpdatedUtc);

public sealed record LegendConnectRetainedKnowledgeSearchSnapshot(
    string Query,
    int ResultCount,
    IReadOnlyList<LegendConnectRetainedKnowledgeItemSnapshot> Items);

/// <summary>
/// A bounded prior turn supplied to the existing LEGEND operations authority
/// for a read-only conversational inference attempt. It is request context,
/// not durable chat memory.
/// </summary>
public sealed record LegendConnectConversationContextItem(
    string Role,
    string Content);

/// <summary>
/// An observational, provenance-carrying analysis of reusable Founder-taught
/// meaning components in one utterance. It is deliberately separate from a
/// serving transition until later evaluator stages establish all governing
/// eligibility gates.
/// </summary>
public sealed record LegendConnectUtteranceMeaningNode(
    string SemanticSignature,
    string SemanticDimension,
    string SemanticValue,
    int StartTokenIndex,
    int TokenLength,
    int IndependentSupportCount);

public sealed record LegendConnectUtteranceMeaningRelation(
    string RelationSignature,
    string RelationKind,
    int SourceNodeIndex,
    int TargetNodeIndex,
    int IndependentSupportCount);

public sealed record LegendConnectUtteranceMeaningGraphSnapshot(
    bool IsComposed,
    IReadOnlyList<LegendConnectUtteranceMeaningNode> Nodes,
    IReadOnlyList<LegendConnectUtteranceMeaningRelation> Relations,
    IReadOnlyList<string> UnknownSurfaceComponents,
    string ReasonCode);

/// <summary>
/// A production-eligible, independently supported discourse-reference rule.
/// It is a curriculum-derived semantic relation, not a surface-form map.
/// </summary>
public sealed record LegendConnectDiscourseReferenceRuleSnapshot(
    string SelectorSemanticSignature,
    string EntitySemanticDimension,
    string ResolutionMode,
    int? SelectionRank,
    IReadOnlyList<string> AllowedSourceRoles,
    bool ReplacesActiveBinding,
    int IndependentSupportCount,
    string RuleSignature);

/// <summary>
/// A bounded, conversation-scoped projection of persisted governed meaning.
/// It deliberately carries neither participant surface text nor a response
/// cache; it is only admissible as semantic context for the one native
/// transition authority.
/// </summary>
public sealed record LegendConnectDiscourseTurnStateSnapshot(
    int SequenceNumber,
    string Role,
    bool IsComposed,
    IReadOnlyList<LegendConnectUtteranceMeaningNode> Nodes,
    IReadOnlyList<LegendConnectUtteranceMeaningRelation> Relations,
    IReadOnlyList<LegendConnectDiscourseReferenceBindingSnapshot> Bindings);

public sealed record LegendConnectDiscourseReferenceBindingSnapshot(
    string ResolutionState,
    string ReasonCode,
    string EntitySemanticDimension,
    string? EntitySemanticSignature,
    string? EntitySemanticValue,
    int? EntityTurnSequence,
    int? EntityNodeIndex,
    bool ReplacesActiveBinding,
    string SelectorSemanticSignature,
    string? ReferenceRuleSignature)
{
    public int? SupersededTurnSequence { get; init; }
    public int? SupersededNodeIndex { get; init; }
    public int? SupersededNodeStartTokenIndex { get; init; }
    public int? SupersededNodeTokenLength { get; init; }
}

public sealed record LegendConnectDiscourseStateSnapshot(
    IReadOnlyList<LegendConnectDiscourseTurnStateSnapshot> Turns);

/// <summary>
/// A text-free result meaning selected before surface realization.  It is the
/// bridge between governed transition reasoning and realization, never a
/// stored answer or response template.
/// </summary>
public sealed record LegendConnectResponseMeaningPlanSnapshot(
    string PlanIdentity,
    string SourceMeaningGraphIdentity,
    string TransitionSignature,
    string ResultSemanticFrameSignature,
    IReadOnlyDictionary<string, string> ResultDimensions,
    IReadOnlyList<LegendConnectDiscourseReferenceBindingSnapshot> ResolvedDiscourseBindings,
    int IndependentEvidenceCount,
    bool UsesDiscourseState,
    IReadOnlyDictionary<string, string>? BoundSemanticVariables = null,
    IReadOnlyDictionary<string, string>? UnboundResultVariables = null,
    IReadOnlyList<string>? ReasoningTransitionPath = null,
    int ReasoningEvidenceCount = 0,
    string EvidenceStandard = "Unavailable",
    LegendConnectResponsePresentationConstraintsSnapshot? PresentationConstraints = null);

/// <summary>
/// Text-free, governed presentation metadata carried by the response meaning
/// plan. It can constrain only realization selection after semantic content
/// and evidence are proven; it never authorizes a fact or surface string.
/// </summary>
public sealed record LegendConnectResponsePresentationConstraintsSnapshot(
    string? Audience,
    string? Expertise,
    string? Tone,
    string? Length,
    int? SentenceCount,
    string? Structure);

public sealed record LegendConnectResponseMeaningPlanResult(
    bool Supported,
    string ReasonCode,
    LegendConnectResponseMeaningPlanSnapshot? Plan);

/// <summary>
/// One content fact assembled exclusively from existing Founder-approved
/// meaning-node and meaning-relation evidence.  It contains controlled
/// semantic identities and provenance state, never a curriculum sentence or
/// a proposed response string.
/// </summary>
public sealed record LegendConnectGovernedContentFactSnapshot(
    string FactIdentity,
    string SubjectSemanticSignature,
    string ContentSemanticSignature,
    string SubjectDimension,
    string SubjectValue,
    string ContentDimension,
    string ContentValue,
    string RelationSignature,
    int SupportCount,
    int IndependentSourceCount,
    int ContradictionCount,
    string MaturityState,
    bool IsProductionEligible);

/// <summary>
/// A text-free Stage-6 response meaning plan.  The selected transition
/// authorizes the response shape; independently matured governed facts supply
/// only the unbound result semantics.  Surface realization remains downstream.
/// </summary>
public sealed record LegendConnectContentBoundResponseMeaningPlanSnapshot(
    LegendConnectResponseMeaningPlanSnapshot ResponsePlan,
    IReadOnlyDictionary<string, string> ContentVariableBindings,
    IReadOnlyList<LegendConnectGovernedContentFactSnapshot> Facts,
    int ContentEvidenceCount);

public sealed record LegendConnectContentBoundResponseMeaningPlanResult(
    bool Supported,
    string ReasonCode,
    LegendConnectContentBoundResponseMeaningPlanSnapshot? Plan);

/// <summary>
/// A governed result frame's bounded request for one current, read-only value.
/// It is not a general tool call: the selected transition fixes the tool,
/// arguments, value path, variable, result dimension, and freshness bound.
/// </summary>
public sealed record LegendConnectReadOnlyContentBindingRequest(
    string RequestIdentity,
    string TransitionSignature,
    string ResultSemanticFrameSignature,
    string ToolName,
    string ArgumentsJson,
    string ValuePath,
    string? ObservedUtcPath,
    int MaximumAgeSeconds,
    string SemanticVariable,
    string ResultDimension);

/// <summary>
/// Proof returned by the existing Founder tool authority after one authorized,
/// zero-write read. Only a bounded scalar is carried into native realization;
/// the complete tool payload remains outside the reasoning executor.
/// </summary>
public sealed record LegendConnectReadOnlyContentBindingReceipt(
    string RequestIdentity,
    string TransitionSignature,
    string ResultSemanticFrameSignature,
    string ToolName,
    string ArgumentsHash,
    string ValuePath,
    string SemanticVariable,
    string ResultDimension,
    string SemanticValue,
    string OutputHash,
    DateTime ObservedUtc,
    DateTime ExecutedUtc,
    string Provenance,
    bool IsReadOnly,
    bool ZeroWrite);

public sealed record LegendConnectReadOnlyContentBindingResult(
    bool Succeeded,
    string ReasonCode,
    LegendConnectReadOnlyContentBindingReceipt? Receipt);

public static class LegendConnectReadOnlyContentBindingContracts
{
    public const string Provenance = "FounderAuthorizedReadOnlyOperational";
    public const int MaximumScalarCharacters = 160;
    public const int MaximumAgeSeconds = 300;
}

public enum LegendConnectResearchNeed
{
    ExistingGovernedKnowledge,
    CurrentOrTimeSensitiveInformation,
    ExplicitVerificationRequest,
    NamedExternalDocumentOrSource,
    InternalKnowledgeGap,
    StaleInternalEvidence,
    ConflictingInternalEvidence,
    NotResearchable
}

public enum LegendConnectResearchAccessClass
{
    PublicReadOnly,
    SensitiveReadOnly,
    AuthenticatedReadOnly,
    PrivateReadOnly,
    RestrictedReadOnly,
    MutationCapable
}

public enum LegendConnectResearchEvidenceOrigin
{
    InternalKnowledge,
    ExternalResearch,
    Combined,
    UnresolvedEvidence
}

public enum LegendConnectResearchOutcomeState
{
    Conclusion,
    InsufficientEvidence,
    UnresolvedConflict,
    Failure
}

/// <summary>
/// The canonical decision made at the existing LEGEND serving boundary before
/// any internet operation is authorized. Unknown wording alone is never a
/// positive decision; the reason identifies the exact governed trigger.
/// </summary>
public sealed record LegendConnectResearchNeededDecision(
    bool ResearchRequired,
    LegendConnectResearchNeed Need,
    string ReasonCode,
    LegendConnectResearchAccessClass AccessClass,
    string SourceLanguageCode,
    bool InternalKnowledgeAvailable,
    bool InternalEvidenceStale,
    bool InternalEvidenceConflicted,
    string? NamedSource,
    DateTime DecidedUtc);

public sealed record LegendConnectResearchAuthorization(
    bool FounderAuthorized,
    string AuthorizationProvenance,
    string? AuthorizationCorrelationId,
    LegendConnectResearchAccessClass AccessClass,
    bool IsReadOnly,
    bool ZeroWrite);

public sealed record LegendConnectBoundedSearchQuery(
    string QueryIdentity,
    int Ordinal,
    string Query,
    string SourceLanguageCode,
    int MaximumResults,
    string? QueryLanguageCode = null);

public sealed record LegendConnectResearchRequest(
    Guid RequestId,
    string Question,
    LegendConnectResearchNeededDecision Decision,
    IReadOnlyList<LegendConnectBoundedSearchQuery> Queries,
    int MaximumResults,
    int MaximumDocuments,
    int MaximumClaims,
    int MaximumDocumentCharacters,
    int MinimumIndependentSources,
    LegendConnectResearchAuthorization Authorization,
    string? InternalAnswer,
    string? InternalReasonCode,
    int InternalEvidenceCount,
    DateTime RequestedUtc,
    LegendConnectResponsePresentationConstraintsSnapshot? PresentationConstraints = null);

public static class LegendConnectResearchRequestFactory
{
    public static LegendConnectResearchRequest Create(
        string question,
        LegendConnectResearchNeededDecision decision,
        LegendConnectResearchAuthorization authorization,
        string? internalAnswer,
        string? internalReasonCode,
        int internalEvidenceCount,
        LegendConnectResponsePresentationConstraintsSnapshot? presentationConstraints = null)
    {
        var normalizedQuestion = (question ?? string.Empty).Trim();
        normalizedQuestion = normalizedQuestion[
            ..Math.Min(normalizedQuestion.Length, LegendConnectResearchContracts.MaximumQueryCharacters)];
        var requestId = Guid.NewGuid();
        var queryIdentity = LegendLanguageIdentity.TextHash(
            "legend-research-query|v1|" +
            decision.SourceLanguageCode + "|" +
            normalizedQuestion);
        return new LegendConnectResearchRequest(
            requestId,
            normalizedQuestion,
            decision,
            [new LegendConnectBoundedSearchQuery(
                queryIdentity,
                1,
                normalizedQuestion,
                decision.SourceLanguageCode,
                LegendConnectResearchContracts.MaximumResults)],
            LegendConnectResearchContracts.MaximumResults,
            LegendConnectResearchContracts.MaximumDocuments,
            LegendConnectResearchContracts.MaximumClaims,
            LegendConnectResearchContracts.MaximumDocumentCharacters,
            decision.Need == LegendConnectResearchNeed.NamedExternalDocumentOrSource ? 1 : 2,
            authorization,
            internalAnswer,
            internalReasonCode,
            internalEvidenceCount,
            DateTime.UtcNow,
            presentationConstraints);
    }
}

public enum LegendConnectResearchSourceClass
{
    PrimaryOfficialRecord,
    LegislatureRegulatorCourtOrGovernmentAuthority,
    PeerReviewedOriginalResearch,
    SystematicReviewOrRecognizedScientificMedicalAuthority,
    RegulatoryFilingOrAuditedFinancialReport,
    OfficialProductOrTechnicalDocumentation,
    FirstPartyCompanyStatement,
    IndependentProfessionalReporting,
    IndependentSecondaryAnalysis,
    Aggregator,
    OpinionOrCommentary,
    UserGeneratedContent,
    AnonymousOrUnverifiableContent,
    UnknownSource
}

public enum LegendConnectResearchClaimSubject
{
    General,
    Legal,
    Medical,
    Scientific,
    Financial,
    Security,
    CurrentEvent,
    Product,
    Operational,
    Historical
}

public enum LegendConnectResearchStatementKind
{
    Fact,
    SourceAssertion,
    Analysis,
    Opinion,
    Inference,
    FirsthandExperience,
    PublicSentiment,
    PublishedStatement
}

public enum LegendConnectResearchEvidenceSupport
{
    Direct,
    CitationChain,
    Observation
}

public enum LegendConnectResearchSourceLineageKind
{
    Original,
    Independent,
    Copied,
    Syndicated,
    PressReleaseDerived,
    CommonOrigin,
    Unknown
}

public enum LegendConnectResearchAuthorityScope
{
    GeneralRecord,
    OwnPublishedPolicy,
    ControllingLegalRecord,
    MedicalScientificEvidence,
    RegulatoryFinancialDisclosure,
    OfficialProductTechnicalDocumentation,
    OwnProductOrService,
    OwnOperations,
    SecurityRecord,
    CurrentEventRecord,
    HistoricalRecord
}

public enum LegendConnectResearchEvidenceDisposition
{
    ControllingEvidence,
    CorroboratingEvidence,
    ObservationOnly,
    Rejected
}

public enum LegendConnectResearchEvidenceRelationship
{
    DirectSupport,
    Contradiction,
    Contextual
}

public enum LegendConnectResearchFreshnessState
{
    Current,
    Stale,
    Undated,
    ConflictingTimestamps
}

public enum LegendConnectResearchExtractionMethod
{
    ModelAssistedProposal,
    ModelAssistedProposalValidatedAgainstExactPassage,
    GovernedTranslationValidated,
    BoundedGovernedInference
}

public enum LegendConnectResearchClaimVerificationState
{
    VerifiedByControllingEvidence,
    SupportedByIndependentlyCorroboratedEvidence,
    SourceReportedButNotIndependentlyVerified,
    ReasonedInferenceFromEvidence,
    Disputed,
    Stale,
    InsufficientEvidence,
    UnresolvedConflict
}

public sealed record LegendConnectResearchPassageLocation(
    string DocumentIdentity,
    int StartCharacterOffset,
    int CharacterLength,
    string ExactPassage,
    string PassageHash,
    string LocationIdentity);

public sealed record LegendConnectResearchClaimTranslationLineage(
    string DocumentLanguageCode,
    string EvidenceLanguageCode,
    string FinalResponseLanguageCode,
    bool TranslationApplied,
    bool GovernedTranslationValidated,
    string? TranslationReceiptIdentity,
    string TranslationState);

public sealed record LegendConnectResearchMaterialClaimProvenance(
    string ProposalIdentity,
    string SourceIdentity,
    string DocumentIdentity,
    string CitationIdentity,
    string PassageLocationIdentity,
    string SourceContentHash,
    string PolicyIdentity,
    DateTime ValidatedUtc,
    bool SourceValidated,
    bool DocumentValidated,
    bool PassageValidated,
    bool TimestampsValidated,
    bool ZeroWrite,
    string? StatementHash = null);

public sealed record LegendConnectResearchMaterialClaimEvidence(
    string EvidenceIdentity,
    string NormalizedClaimIdentity,
    string Statement,
    string SourceIdentity,
    string DocumentIdentity,
    string CitationIdentity,
    LegendConnectResearchPassageLocation Passage,
    LegendConnectResearchSourceClass SourceClass,
    DateTime PublishedUtc,
    DateTime RetrievedUtc,
    LegendConnectResearchClaimSubject Subject,
    LegendConnectResearchAuthorityScope ApplicableScope,
    LegendConnectResearchEvidenceRelationship Relationship,
    string IndependentSourceLineage,
    LegendConnectResearchFreshnessState Freshness,
    string EvidenceStandard,
    int EvidenceStandardRank,
    int RequiredIndependentSourceCount,
    LegendConnectResearchClaimTranslationLineage TranslationLineage,
    LegendConnectResearchExtractionMethod ExtractionMethod,
    LegendConnectResearchMaterialClaimProvenance Provenance,
    LegendConnectResearchStatementKind StatementKind,
    LegendConnectResearchClaimVerificationState VerificationState,
    IReadOnlyList<string>? PremiseClaimIdentities = null,
    string? DiscriminatingClaimIdentity = null,
    string? CorrectsSourceIdentity = null);

public sealed record LegendConnectResearchClaimResolution(
    string NormalizedClaimIdentity,
    LegendConnectResearchClaimVerificationState State,
    string ReasonCode,
    string EvidenceStandard,
    IReadOnlyList<string> MaterialEvidenceIdentities,
    IReadOnlyList<string> IndependentSourceLineages,
    string? SelectedStatement,
    bool RequiresDiscriminatingEvidence);

/// <summary>
/// The semantic role of one realized research statement. These roles are
/// carried as typed response evidence so presentation cannot blur source
/// assertions, verified facts, LEGEND inferences, or unresolved states.
/// </summary>
public enum LegendConnectResearchResponseStatementKind
{
    GovernedInternalKnowledge,
    ExternallyVerifiedFact,
    SourceReportedAssertion,
    LegendReasoningOrInference,
    Uncertainty,
    Contradiction,
    UnresolvedConflict
}

public enum LegendConnectResearchResolvingEvidenceKind
{
    FreshEvidence,
    DirectClaimSupportingEvidence,
    IndependentCorroboration,
    DiscriminatingEvidence,
    HigherAuthorityEvidence,
    CorrectionOrAdjudication
}

/// <summary>
/// An inline marker bound to the exact material evidence and bounded passage
/// that support one response statement. It is not a document-level citation.
/// </summary>
public sealed record LegendConnectResearchInlineCitation(
    int Ordinal,
    string CitationIdentity,
    string NormalizedClaimIdentity,
    IReadOnlyList<string> MaterialEvidenceIdentities,
    string SourceIdentity,
    string DocumentIdentity,
    IReadOnlyList<string> PassageLocationIdentities);

/// <summary>
/// One source actually consulted during the bounded session. This complete
/// list remains separate from the smaller set cited inline.
/// </summary>
public sealed record LegendConnectResearchConsultedSource(
    string SourceIdentity,
    string DocumentIdentity,
    string Title,
    string CanonicalUri,
    LegendConnectResearchSourceClass SourceClass,
    DateTime? PublishedUtc,
    DateTime? UpdatedUtc,
    DateTime? EffectiveUtc,
    DateTime RetrievedUtc,
    string? DocumentLanguageCode,
    bool RetrievalSucceeded,
    string? RetrievalFailureReason,
    bool CitedInline);

public sealed record LegendConnectResearchResponseStatement(
    string StatementIdentity,
    LegendConnectResearchResponseStatementKind Kind,
    string Text,
    string? NormalizedClaimIdentity,
    IReadOnlyList<string> MaterialEvidenceIdentities,
    IReadOnlyList<int> CitationOrdinals,
    string EvidenceState,
    IReadOnlyList<LegendConnectResearchClaimTranslationLineage> TranslationLineage);

public sealed record LegendConnectResearchUncertaintyArticulation(
    IReadOnlyList<string> EstablishedStatementIdentities,
    IReadOnlyList<string> UnresolvedClaimIdentities,
    string UnresolvedQuestion,
    string ReasonCode,
    LegendConnectResearchResolvingEvidenceKind ResolvingEvidence,
    bool RequiresDiscriminatingEvidence);

/// <summary>
/// Fail-closed receipt produced by the existing response presentation
/// authority after verifying every external claim/citation/passage binding.
/// </summary>
public sealed record LegendConnectResearchCitationValidationReceipt(
    bool Succeeded,
    string PolicyIdentity,
    IReadOnlyList<string> RejectionReasons,
    int MaterialClaimCount,
    int InlineCitationCount,
    DateTime ValidatedUtc);

public sealed record LegendConnectResearchPresentation(
    string PresentedText,
    string UserLanguageCode,
    string FinalResponseLanguageCode,
    LegendConnectResearchEvidenceOrigin EvidenceOrigin,
    LegendConnectResponsePresentationConstraintsSnapshot? PresentationConstraints,
    IReadOnlyList<LegendConnectResearchResponseStatement> Statements,
    IReadOnlyList<LegendConnectResearchInlineCitation> InlineCitations,
    IReadOnlyList<LegendConnectResearchConsultedSource> ConsultedSources,
    LegendConnectResearchUncertaintyArticulation? Uncertainty,
    LegendConnectResearchCitationValidationReceipt CitationValidation);

public sealed record LegendConnectResearchEvidenceAdmissibility(
    string EvidenceIdentity,
    string ClaimIdentity,
    string SourceIdentity,
    LegendConnectResearchClaimSubject Subject,
    LegendConnectResearchSourceClass SourceClass,
    LegendConnectResearchEvidenceDisposition Disposition,
    string ReasonCode,
    string IndependentLineageIdentity,
    bool ContradictingEvidence,
    DateTime AssessedUtc,
    string PolicyIdentity,
    string? NormalizedClaimIdentity = null,
    string? MaterialEvidenceIdentity = null);

public sealed record LegendConnectResearchSourceIdentity(
    string SourceIdentity,
    string CanonicalUri,
    string Title,
    string? Publisher,
    LegendConnectResearchSourceClass SourceClass,
    DateTime? PublishedUtc,
    DateTime RetrievedUtc,
    string? DocumentLanguageCode = null,
    bool IsUntrustedExternalData = true,
    string? Author = null,
    DateTime? UpdatedUtc = null,
    DateTime? EffectiveUtc = null,
    bool MethodologyAvailable = false,
    bool ProvenanceComplete = false,
    LegendConnectResearchSourceLineageKind LineageKind =
        LegendConnectResearchSourceLineageKind.Unknown,
    string? OriginalSourceIdentity = null,
    string? CommonOriginIdentity = null,
    IReadOnlyList<string>? CitationTargetSourceIdentities = null,
    IReadOnlyList<LegendConnectResearchAuthorityScope>? AuthorityScopes = null,
    bool IsControllingRecord = false);

public sealed record LegendConnectSearchResult(
    string SearchResultIdentity,
    string QueryIdentity,
    int Rank,
    string SourceIdentity,
    string Title,
    string CanonicalUri,
    string? Snippet,
    string? QueryLanguageCode = null,
    string? DocumentLanguageCode = null,
    bool IsUntrustedExternalData = true);

public sealed record LegendConnectRetrievedDocument(
    string DocumentIdentity,
    string SourceIdentity,
    string CanonicalUri,
    string ContentExcerpt,
    string ContentHash,
    DateTime RetrievedUtc,
    bool RetrievalSucceeded,
    string? FailureReason,
    string? DocumentLanguageCode = null,
    string? ContentType = null,
    int RedirectCount = 0,
    long ReturnedBytes = 0,
    bool IsUntrustedExternalData = true,
    bool ContainsInstructionLikeContent = false);

public sealed record LegendConnectCitation(
    string CitationIdentity,
    string SourceIdentity,
    string DocumentIdentity,
    string Title,
    string CanonicalUri,
    DateTime RetrievedUtc,
    string? DocumentLanguageCode = null,
    bool IsUntrustedExternalData = true);

public sealed record LegendConnectResearchSearchQueryReceipt(
    string ReceiptIdentity,
    string QueryIdentity,
    string Query,
    string QueryLanguageCode,
    DateTime ExecutedUtc,
    string Transport,
    string Provider,
    long LatencyMilliseconds,
    long? CostMicrounits,
    string CostState,
    bool IsReadOnly,
    bool ZeroWrite,
    bool Succeeded = true,
    string? FailureReason = null);

public sealed record LegendConnectResearchPageReceipt(
    string ReceiptIdentity,
    string RequestedCanonicalUri,
    string? FinalCanonicalUri,
    DateTime RequestedUtc,
    DateTime CompletedUtc,
    string Transport,
    string Provider,
    int RequestCount,
    int RedirectCount,
    int? StatusCode,
    string? ContentType,
    long ReturnedBytes,
    long LatencyMilliseconds,
    long? CostMicrounits,
    string CostState,
    bool Succeeded,
    string? FailureReason,
    bool IsReadOnly,
    bool ZeroWrite);

public sealed record LegendConnectResearchTranslationReceipt(
    string ReceiptIdentity,
    string SourceLanguageCode,
    string TargetLanguageCode,
    string Transport,
    string InputIdentity,
    string OutputIdentity,
    DateTime ObservedUtc,
    string State,
    IReadOnlyList<string>? ValidatedProposalIdentities = null);

public sealed record LegendConnectResearchLanguageLineage(
    string UserLanguageCode,
    IReadOnlyList<string> QueryLanguageCodes,
    IReadOnlyList<string> DocumentLanguageCodes,
    string EvidenceLanguageCode,
    string FinalResponseLanguageCode,
    IReadOnlyList<LegendConnectResearchTranslationReceipt> TranslationReceipts,
    string FinalPresentationState = "EvidenceStatementsRequestedInUserLanguage",
    string? FinalPresentationTransport = null);

/// <summary>
/// Non-authoritative extraction proposal. It becomes material evidence only
/// after the existing governed reasoning entry validates its exact passage,
/// source, timestamps, scope, translation, and policy lineage.
/// </summary>
public sealed record LegendConnectClaimEvidence(
    string EvidenceIdentity,
    string ClaimIdentity,
    string Statement,
    string SourceIdentity,
    string DocumentIdentity,
    string CitationIdentity,
    DateTime? ObservedUtc,
    LegendConnectResearchClaimSubject Subject = LegendConnectResearchClaimSubject.General,
    LegendConnectResearchStatementKind StatementKind = LegendConnectResearchStatementKind.Fact,
    LegendConnectResearchEvidenceSupport Support = LegendConnectResearchEvidenceSupport.Direct,
    LegendConnectResearchAuthorityScope RequiredAuthorityScope =
        LegendConnectResearchAuthorityScope.GeneralRecord,
    DateTime? AsOfUtc = null,
    string? SupportingExcerpt = null,
    string EvidenceLanguageCode = "und",
    LegendConnectResearchExtractionMethod ExtractionMethod =
        LegendConnectResearchExtractionMethod.ModelAssistedProposal,
    IReadOnlyList<string>? PremiseClaimIdentities = null,
    string? DiscriminatingClaimIdentity = null,
    string? CorrectsSourceIdentity = null);

public sealed record LegendConnectContradictingEvidence(
    string EvidenceIdentity,
    string ClaimIdentity,
    string Statement,
    string SourceIdentity,
    string DocumentIdentity,
    string CitationIdentity,
    DateTime? ObservedUtc,
    LegendConnectResearchClaimSubject Subject = LegendConnectResearchClaimSubject.General,
    LegendConnectResearchStatementKind StatementKind = LegendConnectResearchStatementKind.Fact,
    LegendConnectResearchEvidenceSupport Support = LegendConnectResearchEvidenceSupport.Direct,
    LegendConnectResearchAuthorityScope RequiredAuthorityScope =
        LegendConnectResearchAuthorityScope.GeneralRecord,
    DateTime? AsOfUtc = null,
    string? SupportingExcerpt = null,
    string EvidenceLanguageCode = "und",
    LegendConnectResearchExtractionMethod ExtractionMethod =
        LegendConnectResearchExtractionMethod.ModelAssistedProposal,
    IReadOnlyList<string>? PremiseClaimIdentities = null,
    string? DiscriminatingClaimIdentity = null,
    string? CorrectsSourceIdentity = null);

public sealed record LegendConnectResearchSession(
    Guid SessionId,
    Guid RequestId,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    IReadOnlyList<LegendConnectBoundedSearchQuery> Queries,
    IReadOnlyList<LegendConnectSearchResult> SearchResults,
    IReadOnlyList<LegendConnectResearchSourceIdentity> Sources,
    IReadOnlyList<LegendConnectRetrievedDocument> Documents,
    IReadOnlyList<LegendConnectClaimEvidence> ClaimEvidence,
    IReadOnlyList<LegendConnectContradictingEvidence> ContradictingEvidence,
    IReadOnlyList<LegendConnectCitation> Citations,
    long LatencyMilliseconds,
    long? CostMicrounits,
    string CompletionState,
    string? FailureReason,
    IReadOnlyList<LegendConnectResearchSearchQueryReceipt>? SearchQueryReceipts = null,
    IReadOnlyList<LegendConnectResearchPageReceipt>? PageReceipts = null,
    LegendConnectResearchLanguageLineage? LanguageLineage = null,
    string? EvidencePolicyIdentity = null,
    IReadOnlyList<LegendConnectResearchEvidenceAdmissibility>? EvidenceAdmissibility = null,
    IReadOnlyList<LegendConnectResearchMaterialClaimEvidence>? MaterialClaimEvidence = null,
    IReadOnlyList<LegendConnectResearchClaimResolution>? ClaimResolutions = null,
    string? ClaimEvidencePolicyIdentity = null,
    LegendConnectResearchCitationValidationReceipt? CitationValidation = null,
    long SearchLatencyMilliseconds = 0,
    long RetrievalLatencyMilliseconds = 0,
    long ReasoningLatencyMilliseconds = 0,
    long? SearchCostMicrounits = null,
    long? ModelCostMicrounits = null);

public sealed record LegendConnectResearchConclusion(
    string ConclusionIdentity,
    string PresentedText,
    IReadOnlyList<LegendConnectResearchMaterialClaimEvidence> SupportedClaims,
    IReadOnlyList<LegendConnectCitation> Citations);

public sealed record LegendConnectResearchInsufficientEvidenceResult(
    string ReasonCode,
    string PresentedText,
    int AdmissibleClaimCount,
    int IndependentSourceCount,
    int RequiredIndependentSourceCount,
    IReadOnlyList<LegendConnectCitation>? Citations = null);

public sealed record LegendConnectResearchUnresolvedConflictResult(
    string ReasonCode,
    string PresentedText,
    IReadOnlyList<LegendConnectResearchMaterialClaimEvidence> ClaimEvidence,
    IReadOnlyList<LegendConnectResearchMaterialClaimEvidence> ContradictingEvidence,
    IReadOnlyList<LegendConnectCitation> Citations);

public sealed record LegendConnectResearchFailureResult(
    string ReasonCode,
    string PresentedText,
    bool Retryable,
    string? DiagnosticDetail = null);

public enum LegendConnectResearchRetentionState
{
    ExternalObservation,
    MachineProposed,
    CriticRejected,
    AwaitingCanonicalNoveltyValidation,
    SystemValidatedMachine,
    GovernedAdmission,
    CanonicalEligible,
    Failed
}

public sealed record LegendConnectResearchRetentionReceipt(
    LegendConnectResearchRetentionState State,
    string ObservationIdentity,
    Guid? CandidateId,
    Guid? ProposalId,
    string Provenance,
    string ServingStatus,
    string CanonicalStatus,
    string? FailureCode = null);

/// <summary>
/// Complete, zero-write lineage for one research outcome. External evidence is
/// never converted into FounderApproved, canonical, learned, or serving
/// knowledge by appearing in this receipt.
/// </summary>
public sealed record LegendConnectResearchProvenance(
    Guid RequestId,
    Guid SessionId,
    string DecisionReasonCode,
    string SourceLanguageCode,
    string QuestionHash,
    DateTime RequestedUtc,
    LegendConnectResearchEvidenceOrigin EvidenceOrigin,
    string? InternalReasonCode,
    int InternalEvidenceCount,
    string Transport,
    string? ModelVersion,
    string SettingsIdentity,
    IReadOnlyList<string> QueryIdentities,
    IReadOnlyList<string> SourceIdentities,
    IReadOnlyList<string> DocumentIdentities,
    IReadOnlyList<string> ClaimEvidenceIdentities,
    IReadOnlyList<string> ContradictingEvidenceIdentities,
    IReadOnlyList<string> CitationIdentities,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    long LatencyMilliseconds,
    long? CostMicrounits,
    string CostState,
    string AuthorizationProvenance,
    string? AuthorizationCorrelationId,
    bool IsReadOnly,
    bool ZeroWrite,
    string Provenance,
    string? SearchProvider = null,
    IReadOnlyList<string>? SearchQueryReceiptIdentities = null,
    IReadOnlyList<string>? PageReceiptIdentities = null,
    LegendConnectResearchLanguageLineage? LanguageLineage = null,
    string? EvidencePolicyIdentity = null,
    IReadOnlyList<LegendConnectResearchEvidenceAdmissibility>? EvidenceAdmissibility = null,
    IReadOnlyList<string>? MaterialClaimEvidenceIdentities = null,
    IReadOnlyList<LegendConnectResearchClaimResolution>? ClaimResolutions = null,
    string? ClaimEvidencePolicyIdentity = null,
    LegendConnectResearchCitationValidationReceipt? CitationValidation = null,
    string? CitationPresentationPolicyIdentity = null,
    string CodeSha = "Unavailable",
    string ConfigurationIdentity = "Unavailable");

public sealed record LegendConnectResearchOutcome(
    LegendConnectResearchOutcomeState State,
    LegendConnectResearchEvidenceOrigin EvidenceOrigin,
    LegendConnectResearchNeededDecision Decision,
    LegendConnectResearchSession Session,
    LegendConnectResearchConclusion? Conclusion,
    LegendConnectResearchInsufficientEvidenceResult? InsufficientEvidence,
    LegendConnectResearchUnresolvedConflictResult? UnresolvedConflict,
    LegendConnectResearchFailureResult? Failure,
    LegendConnectResearchProvenance Provenance,
    LegendConnectResearchPresentation? Presentation = null,
    LegendConnectResearchRetentionReceipt? Retention = null,
    LegendConnectResearchRetentionLineage? RetentionLineage = null)
{
    public string PresentedText =>
        Presentation?.PresentedText ??
        Conclusion?.PresentedText ??
        InsufficientEvidence?.PresentedText ??
        UnresolvedConflict?.PresentedText ??
        Failure?.PresentedText ??
        "LEGEND could not establish a research outcome.";
}

public static class LegendConnectResearchRetentionContracts
{
    public static string ObservationIdentity(LegendConnectResearchOutcome outcome) =>
        ObservationIdentity(
            outcome.Provenance.RequestId,
            outcome.Provenance.SessionId,
            outcome.Conclusion?.ConclusionIdentity ?? outcome.State.ToString(),
            outcome.State,
            outcome.EvidenceOrigin,
            outcome.Conclusion?.SupportedClaims ?? [],
            outcome.Conclusion?.Citations ?? [],
            outcome.Session.CitationValidation,
            outcome.Provenance.AuthorizationProvenance,
            outcome.Provenance.AuthorizationCorrelationId,
            outcome.Provenance.CodeSha,
            outcome.Provenance.ConfigurationIdentity);

    public static string ObservationIdentity(
        LegendConnectResearchRetentionLineage lineage) =>
        ObservationIdentity(
            lineage.RequestId,
            lineage.SessionId,
            lineage.ConclusionIdentity,
            lineage.OutcomeState,
            lineage.EvidenceOrigin,
            lineage.MaterialClaims,
            lineage.Citations,
            lineage.CitationValidation,
            lineage.ResearchAuthorizationProvenance,
            lineage.ResearchAuthorizationCorrelationId,
            lineage.CodeSha,
            lineage.ConfigurationIdentity);

    private static string ObservationIdentity(
        Guid requestId,
        Guid sessionId,
        string conclusionIdentity,
        LegendConnectResearchOutcomeState outcomeState,
        LegendConnectResearchEvidenceOrigin evidenceOrigin,
        IReadOnlyList<LegendConnectResearchMaterialClaimEvidence> materialClaims,
        IReadOnlyList<LegendConnectCitation> citations,
        LegendConnectResearchCitationValidationReceipt? citationValidation,
        string researchAuthorizationProvenance,
        string? researchAuthorizationCorrelationId,
        string codeSha,
        string configurationIdentity) =>
        LegendLanguageIdentity.TextHash(
            JsonSerializer.Serialize(new
            {
                LegendConnectResearchContracts.RetentionPolicy,
                RequestId = requestId.ToString("N"),
                SessionId = sessionId.ToString("N"),
                ConclusionIdentity = conclusionIdentity,
                OutcomeState = outcomeState.ToString(),
                EvidenceOrigin = evidenceOrigin.ToString(),
                MaterialClaims = materialClaims
                    .OrderBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
                    .ToArray(),
                Citations = citations
                    .OrderBy(item => item.CitationIdentity, StringComparer.Ordinal)
                    .ToArray(),
                CitationValidation = citationValidation,
                ResearchAuthorizationProvenance = researchAuthorizationProvenance,
                ResearchAuthorizationCorrelationId = researchAuthorizationCorrelationId,
                CodeSha = codeSha,
                ConfigurationIdentity = configurationIdentity
            }));

    public static bool IsStructurallyValid(
        LegendConnectResearchRetentionLineage lineage)
    {
        if (lineage.RequestId == Guid.Empty ||
            lineage.SessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(lineage.ConclusionIdentity) ||
            lineage.ConclusionIdentity.Length > 80 ||
            lineage.OutcomeState != LegendConnectResearchOutcomeState.Conclusion ||
            lineage.EvidenceOrigin is not (
                LegendConnectResearchEvidenceOrigin.ExternalResearch or
                LegendConnectResearchEvidenceOrigin.Combined) ||
            lineage.MaterialClaims is null ||
            lineage.MaterialClaims.Count is < 2 or > 8 ||
            lineage.MaterialClaims.Any(item => item is null) ||
            lineage.Citations is null ||
            lineage.Citations.Count is < 1 or > LegendConnectResearchContracts.MaximumClaims ||
            lineage.Citations.Any(item => item is null) ||
            lineage.CitationValidation is null ||
            !lineage.CitationValidation.Succeeded ||
            !string.Equals(
                lineage.CitationValidation.PolicyIdentity,
                LegendConnectResearchContracts.CitationPresentationPolicy,
                StringComparison.Ordinal) ||
            lineage.CitationValidation.MaterialClaimCount != lineage.MaterialClaims.Count ||
            lineage.CitationValidation.InlineCitationCount < 1 ||
            !HasValidResearchAuthorization(lineage) ||
            !IsCanonicalTextHash(lineage.ObservationIdentity) ||
            !IsLowerHex(lineage.CodeSha, 40) ||
            !IsLowerHex(lineage.ConfigurationIdentity, 64))
        {
            return false;
        }

        var evidenceIds = lineage.MaterialClaims
            .Select(item => item.EvidenceIdentity)
            .ToArray();
        var citations = lineage.Citations
            .GroupBy(item => item.CitationIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        if (evidenceIds.Distinct(StringComparer.Ordinal).Count() != evidenceIds.Length ||
            citations.Count != lineage.Citations.Count ||
            lineage.MaterialClaims.Any(item =>
                string.IsNullOrWhiteSpace(item.Statement) ||
                item.Statement.Length > 2_000 ||
                string.IsNullOrWhiteSpace(item.EvidenceIdentity) ||
                item.EvidenceIdentity.Length > 80 ||
                string.IsNullOrWhiteSpace(item.SourceIdentity) ||
                item.SourceIdentity.Length > 80 ||
                string.IsNullOrWhiteSpace(item.DocumentIdentity) ||
                item.DocumentIdentity.Length > 80 ||
                string.IsNullOrWhiteSpace(item.CitationIdentity) ||
                item.CitationIdentity.Length > 80 ||
                item.Passage is null ||
                item.Provenance is null ||
                !citations.TryGetValue(item.CitationIdentity, out var citation) ||
                !string.Equals(citation.SourceIdentity, item.SourceIdentity, StringComparison.Ordinal) ||
                !string.Equals(citation.DocumentIdentity, item.DocumentIdentity, StringComparison.Ordinal) ||
                !string.Equals(item.Passage.DocumentIdentity, item.DocumentIdentity, StringComparison.Ordinal) ||
                !string.Equals(item.Passage.LocationIdentity, item.Provenance.PassageLocationIdentity, StringComparison.Ordinal) ||
                !IsCanonicalTextHash(item.Passage.PassageHash) ||
                !IsCanonicalTextHash(item.Provenance.SourceContentHash) ||
                !string.Equals(item.Provenance.SourceIdentity, item.SourceIdentity, StringComparison.Ordinal) ||
                !string.Equals(item.Provenance.DocumentIdentity, item.DocumentIdentity, StringComparison.Ordinal) ||
                !string.Equals(item.Provenance.CitationIdentity, item.CitationIdentity, StringComparison.Ordinal) ||
                (item.Provenance.StatementHash is { } statementHash &&
                 !string.Equals(
                     statementHash,
                     LegendLanguageIdentity.TextHash(item.Statement),
                     StringComparison.Ordinal)) ||
                !item.Provenance.SourceValidated ||
                !item.Provenance.DocumentValidated ||
                !item.Provenance.PassageValidated ||
                !item.Provenance.TimestampsValidated ||
                !item.Provenance.ZeroWrite ||
                !string.Equals(
                    item.Provenance.PolicyIdentity,
                    LegendConnectResearchContracts.ClaimEvidencePolicy,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        return string.Equals(
            lineage.ObservationIdentity,
            ObservationIdentity(lineage),
            StringComparison.Ordinal);
    }

    public static LegendConnectResearchRetentionLineage? CreateExternalObservation(
        LegendConnectResearchOutcome outcome)
    {
        if (outcome.State != LegendConnectResearchOutcomeState.Conclusion ||
            outcome.Conclusion is null ||
            outcome.EvidenceOrigin is not (
                LegendConnectResearchEvidenceOrigin.ExternalResearch or
                LegendConnectResearchEvidenceOrigin.Combined) ||
            outcome.Conclusion.SupportedClaims is not { Count: >= 2 and <= 8 } claims ||
            outcome.Session.CitationValidation is not { Succeeded: true } validation ||
            !IsLowerHex(outcome.Provenance.CodeSha, 40) ||
            !IsLowerHex(outcome.Provenance.ConfigurationIdentity, 64))
        {
            return null;
        }
        var lineage = new LegendConnectResearchRetentionLineage(
            outcome.Provenance.RequestId,
            outcome.Provenance.SessionId,
            outcome.Conclusion.ConclusionIdentity,
            outcome.State,
            outcome.EvidenceOrigin,
            claims,
            outcome.Conclusion.Citations,
            validation,
            outcome.Provenance.AuthorizationProvenance,
            outcome.Provenance.AuthorizationCorrelationId,
            ObservationIdentity(outcome),
            outcome.Provenance.CodeSha,
            outcome.Provenance.ConfigurationIdentity);
        return IsStructurallyValid(lineage)
            ? lineage
            : null;
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonicalTextHash(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool HasValidResearchAuthorization(
        LegendConnectResearchRetentionLineage lineage) =>
        string.Equals(
            lineage.ResearchAuthorizationProvenance,
            LegendConnectResearchContracts.PublicAuthorizationProvenance,
            StringComparison.Ordinal)
            ? lineage.ResearchAuthorizationCorrelationId is null
            : string.Equals(
                  lineage.ResearchAuthorizationProvenance,
                  LegendConnectResearchContracts.RestrictedAuthorizationProvenance,
                  StringComparison.Ordinal) &&
              Guid.TryParseExact(
                  lineage.ResearchAuthorizationCorrelationId,
                  "N",
                  out _);
}

public sealed record LegendConnectResearchClaimCandidate(
    string ClaimIdentity,
    string Statement,
    IReadOnlyList<string> CanonicalUris,
    DateTime? ObservedUtc,
    string EvidenceLanguageCode,
    bool IsUntrustedExternalData,
    LegendConnectResearchClaimSubject Subject = LegendConnectResearchClaimSubject.General,
    LegendConnectResearchStatementKind StatementKind = LegendConnectResearchStatementKind.Fact,
    LegendConnectResearchEvidenceSupport Support = LegendConnectResearchEvidenceSupport.Direct,
    LegendConnectResearchAuthorityScope RequiredAuthorityScope =
        LegendConnectResearchAuthorityScope.GeneralRecord,
    DateTime? AsOfUtc = null,
    string? SupportingExcerpt = null,
    IReadOnlyList<string>? PremiseClaimIdentities = null,
    string? DiscriminatingClaimIdentity = null,
    string? CorrectsCanonicalUri = null);

public sealed record LegendConnectResearchSearchTransportRequest(
    Guid SessionId,
    string UserLanguageCode,
    IReadOnlyList<LegendConnectBoundedSearchQuery> Queries,
    int MaximumResults,
    int MaximumClaims);

public sealed record LegendConnectResearchSearchTransportResult(
    bool Succeeded,
    string Transport,
    string Provider,
    string? ModelVersion,
    string SettingsIdentity,
    IReadOnlyList<LegendConnectBoundedSearchQuery> ExecutedQueries,
    IReadOnlyList<LegendConnectResearchSearchQueryReceipt> QueryReceipts,
    IReadOnlyList<LegendConnectSearchResult> SearchResults,
    IReadOnlyList<LegendConnectResearchSourceIdentity> Sources,
    IReadOnlyList<LegendConnectResearchClaimCandidate> ClaimCandidates,
    IReadOnlyList<LegendConnectResearchClaimCandidate> ContradictionCandidates,
    long LatencyMilliseconds,
    long? CostMicrounits,
    string? FailureReason,
    bool Retryable);

public interface ILegendConnectResearchSearchTransport
{
    Task<LegendConnectResearchSearchTransportResult> SearchAsync(
        LegendConnectResearchSearchTransportRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record LegendConnectResearchPageRetrievalRequest(
    Guid SessionId,
    string UserLanguageCode,
    IReadOnlyList<LegendConnectSearchResult> SearchResults,
    IReadOnlyList<LegendConnectResearchSourceIdentity> Sources,
    int MaximumDocuments,
    int MaximumDocumentCharacters,
    int MaximumTotalCharacters,
    DateTime DeadlineUtc);

public sealed record LegendConnectRetrievedPageLineage(
    string RequestedCanonicalUri,
    string FinalCanonicalUri,
    string SourceIdentity,
    string DocumentIdentity,
    string CitationIdentity);

public sealed record LegendConnectResearchPageRetrievalResult(
    bool Succeeded,
    string Transport,
    string SettingsIdentity,
    IReadOnlyList<LegendConnectSearchResult> SearchResults,
    IReadOnlyList<LegendConnectResearchSourceIdentity> Sources,
    IReadOnlyList<LegendConnectRetrievedDocument> Documents,
    IReadOnlyList<LegendConnectCitation> Citations,
    IReadOnlyList<LegendConnectRetrievedPageLineage> Lineage,
    IReadOnlyList<LegendConnectResearchPageReceipt> Receipts,
    long LatencyMilliseconds,
    string? FailureReason,
    bool Retryable);

public interface ILegendConnectResearchPageRetriever
{
    Task<LegendConnectResearchPageRetrievalResult> RetrieveAsync(
        LegendConnectResearchPageRetrievalRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record LegendConnectResearchEvidencePacket(
    string Transport,
    string SearchProvider,
    string? ModelVersion,
    string SettingsIdentity,
    IReadOnlyList<LegendConnectBoundedSearchQuery> ExecutedQueries,
    IReadOnlyList<LegendConnectResearchSearchQueryReceipt> SearchQueryReceipts,
    IReadOnlyList<LegendConnectResearchPageReceipt> PageReceipts,
    IReadOnlyList<LegendConnectSearchResult> SearchResults,
    IReadOnlyList<LegendConnectResearchSourceIdentity> Sources,
    IReadOnlyList<LegendConnectRetrievedDocument> Documents,
    IReadOnlyList<LegendConnectClaimEvidence> ClaimEvidence,
    IReadOnlyList<LegendConnectContradictingEvidence> ContradictingEvidence,
    IReadOnlyList<LegendConnectCitation> Citations,
    LegendConnectResearchLanguageLineage LanguageLineage,
    long LatencyMilliseconds,
    long? CostMicrounits);

public static class LegendConnectResearchContracts
{
    public const string Provenance = "FounderAuthorizedGovernedInternetResearch";
    public const string EvidenceAdmissibilityPolicy =
        "LegendResearchSourceAuthorityAndEvidenceAdmissibility:v1";
    public const string ClaimEvidencePolicy =
        "LegendResearchBoundedClaimEvidence:v1";
    public const string CitationPresentationPolicy =
        "LegendResearchCitationAndPresentation:v1";
    public const string RetentionPolicy =
        "LegendResearchExternalObservationRetention:v1";
    public const string ObservabilityCategory = "ResearchObservability";
    public const string PublicAuthorizationProvenance = "FounderAuthenticatedPublicReadOnly";
    public const string LockedEvaluationAuthorizationProvenance =
        "LockedEvaluatorPublicReadOnly";
    public const string RestrictedAuthorizationProvenance = "FounderExplicitRequestAuthorization";
    public const int MaximumQueries = 3;
    public const int MaximumQueryCharacters = 500;
    public const int MaximumResults = 8;
    public const int MaximumDocuments = 6;
    public const int MaximumClaims = 12;
    public const int MaximumDocumentCharacters = 4_000;
    public const int MaximumTotalDocumentCharacters = MaximumDocuments * MaximumDocumentCharacters;
    public const int MaximumPageBytes = 262_144;
    public const int MaximumRedirects = 3;
    public const int RequestTimeoutSeconds = 10;
    public const int TotalResearchDeadlineSeconds = 90;
}

/// <summary>
/// Case-level measurements consumed only by the existing locked evaluator and
/// blind benchmark. A synthetic or manually assembled measurement is useful
/// for aggregation tests but is never promotion or superiority evidence.
/// </summary>
public sealed record LegendConnectResearchEvaluationMeasurements(
    bool IsResearchCase,
    bool AnswerCorrect,
    bool CitationCorrect,
    bool CitationComplete,
    bool ClaimEvidenceEntailed,
    bool PrimarySourceUsed,
    bool SourceIndependent,
    bool FreshnessSatisfied,
    bool ContradictionHandled,
    decimal UnsupportedClaimRate,
    bool PromptInjectionResisted,
    long ResearchLatencyMicroseconds,
    long ResearchCostMicrounits,
    bool NativeResearchCompleted,
    bool GptEscalationAvoided,
    bool CrossLanguageQualitySatisfied,
    bool RuntimeObserved,
    bool SyntheticOrManual,
    string ProvenanceIdentity)
{
    public bool IsCompleteRuntimeEvidence =>
        IsResearchCase &&
        RuntimeObserved &&
        !SyntheticOrManual &&
        UnsupportedClaimRate is >= 0m and <= 1m &&
        ResearchLatencyMicroseconds >= 0 &&
        ResearchCostMicrounits >= 0 &&
        ProvenanceIdentity is { Length: 64 } &&
        ProvenanceIdentity.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public bool MeetsFailClosedQualityBar =>
        IsCompleteRuntimeEvidence &&
        AnswerCorrect &&
        CitationCorrect &&
        CitationComplete &&
        ClaimEvidenceEntailed &&
        SourceIndependent &&
        FreshnessSatisfied &&
        ContradictionHandled &&
        UnsupportedClaimRate == 0m &&
        PromptInjectionResisted &&
        NativeResearchCompleted &&
        GptEscalationAvoided &&
        CrossLanguageQualitySatisfied;
}

/// <summary>
/// Read-only serving provenance for an optional evaluated and promoted model
/// candidate.  The model is an articulation participant only: this receipt
/// never represents canonical evidence, a contradiction decision, or a
/// learning/promotion authority.
/// </summary>
public sealed record LegendConnectNativeModelAssistanceSnapshot(
    string State,
    string ReasonCode,
    string CapabilityKey,
    string? ModelVersion,
    Guid? ModelTrainingRunId,
    string? Provenance,
    long? CostMicrounits = null);

public static class LegendConnectNativeModelAssistanceContracts
{
    public const string GovernedReasoningCapability = "governed.reasoning";
    public const string CandidateAttemptProvenance = "EvaluatedPromotedModelCandidateAttempt";
    public const string Provenance = "EvaluatedPromotedModelArticulation";
}

/// <summary>
/// The governed result of one native LEGEND conversational inference attempt.
/// A successful result is backed only by canonical, contradiction-free
/// evidence; all other states explicitly require escalation.
/// </summary>
public sealed record LegendConnectNativeInferenceSnapshot(
    bool Supported,
    decimal Confidence,
    string? Answer,
    string ReasonCode,
    int EvidenceCount,
    string AuthoritySummary,
    bool RequiresEscalation,
    string EvidenceStandard = "Unavailable",
    string ArticulationMode = "Unavailable",
    LegendConnectReadOnlyContentBindingRequest? ReadOnlyContentRequest = null,
    IReadOnlyList<LegendConnectReadOnlyContentBindingReceipt>? ContentBindingProvenance = null,
    LegendConnectNativeModelAssistanceSnapshot? ModelAssistance = null,
    LegendConnectResearchNeededDecision? ResearchDecision = null,
    LegendConnectResponsePresentationConstraintsSnapshot? PresentationConstraints = null);

/// <summary>
/// The sole read/write authority for Legend Connect operations. Presentation
/// layers may use it only after their established Founder authorization guard
/// succeeds; it owns neither identity authorization nor mobile contracts.
/// </summary>
public interface ILegendConnectOperations
{
    Task<LegendConnectDashboardSnapshot> GetDashboardAsync(
        CancellationToken cancellationToken = default);

    Task<LegendConnectDashboardProjectionSnapshot> GetDashboardProjectionAsync(
        string? languageCode,
        string? pairKey,
        CancellationToken cancellationToken = default);

    Task<LegendConnectFounderShellSnapshot> GetFounderShellAsync(
        string? languageCode,
        CancellationToken cancellationToken = default);

    Task<LegendConnectFounderSectionPageSnapshot> GetFounderSectionPageAsync(
        string section,
        string? languageCode,
        string? search,
        string? cursor,
        Guid? curriculumFamilyId = null,
        CancellationToken cancellationToken = default);

    Task<LegendConnectProviderCapacitySnapshot> GetProviderCapacityAsync(
        CancellationToken cancellationToken = default);

    Task<LegendConnectMetricDetailSnapshot> GetMetricDetailAsync(
        string? metricKey,
        CancellationToken cancellationToken = default);

    Task<LegendConnectLanguageHealthSnapshot?> GetLanguageHealthAsync(
        string languageCode,
        CancellationToken cancellationToken = default);

    Task<LegendConnectLanguageKnowledgeSnapshot?> GetLanguageKnowledgeAsync(
        string languageCode,
        CancellationToken cancellationToken = default);

    Task<LegendConnectPairHealthSnapshot?> GetPairHealthAsync(
        string pairKey,
        CancellationToken cancellationToken = default);

    Task<LegendConnectTranslationQualitySnapshot> GetTranslationQualityAsync(
        CancellationToken cancellationToken = default);

    Task<LegendTargetRealizationReviewSnapshot> GetTargetRealizationReviewAsync(
        CancellationToken cancellationToken = default);

    Task<LegendTargetRealizationReviewActionResult> VerifyTargetRealizationCandidateAsync(
        string founderUserId,
        Guid candidateId,
        CancellationToken cancellationToken = default);

    Task<LegendTargetRealizationReviewActionResult> RejectTargetRealizationCandidateAsync(
        string founderUserId,
        Guid candidateId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectMachineTeachingSubmissionResult> SubmitMachineTeachingProposalAsync(
        LegendConnectMachineTeachingSubmission submission,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches retained governed evidence and admitted knowledge.
    /// MachineProposed candidate and proposal artifacts remain noncanonical
    /// lifecycle records and are never projected through this contract.
    /// </summary>
    Task<LegendConnectRetainedKnowledgeSearchSnapshot> SearchRetainedKnowledgeAsync(
        string query,
        string? sourceLanguageCode = null,
        string? targetLanguageCode = null,
        int take = 12,
        CancellationToken cancellationToken = default);

    Task<LegendConnectResearchNeededDecision> DecideResearchNeededAsync(
        string input,
        string sourceLanguageCode,
        LegendConnectNativeInferenceSnapshot? internalInference,
        CancellationToken cancellationToken = default);

    Task<LegendConnectResearchOutcome> ExecuteResearchAsync(
        LegendConnectResearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes sanitized, bounded operational receipts for the completed
    /// zero-write research session. These receipts are read-only diagnostics;
    /// retained/native serving never queries them as knowledge.
    /// </summary>
    Task RecordResearchObservabilityAsync(
        LegendConnectResearchOutcome outcome,
        CancellationToken cancellationToken = default);

    Task RecordResearchRetentionAsync(
        LegendConnectResearchRetentionLineage lineage,
        LegendConnectMachineTeachingSubmissionResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Native serving through the same transition authority with an optional
    /// persisted, semantic-only discourse snapshot. This is not a second
    /// inference engine: it supplies Stage 1–3 governed inputs to the shared
    /// selector before the existing realization authority runs.
    /// </summary>
    Task<LegendConnectNativeInferenceSnapshot> TryInferConversationWithDiscourseAsync(
        string input,
        IReadOnlyList<LegendConnectConversationContextItem> context,
        LegendConnectDiscourseStateSnapshot? discourseState,
        CancellationToken cancellationToken = default,
        string sourceLanguageCode = "en");

    /// <summary>
    /// Completes the same native inference path with one proof-carrying receipt
    /// from the existing Founder read-only tool authority. The curriculum
    /// authority reselects and validates the exact declared frame; this is not
    /// a second inference or tool execution path.
    /// </summary>
    Task<LegendConnectNativeInferenceSnapshot>
        TryInferConversationWithReadOnlyContentAsync(
            string input,
            IReadOnlyList<LegendConnectConversationContextItem> context,
            LegendConnectDiscourseStateSnapshot? discourseState,
            LegendConnectReadOnlyContentBindingReceipt receipt,
            CancellationToken cancellationToken = default,
            string sourceLanguageCode = "en");

    Task<LegendConnectResponseMeaningPlanResult> TryPlanConversationAsync(
        string input,
        LegendConnectDiscourseStateSnapshot? discourseState,
        CancellationToken cancellationToken = default,
        string sourceLanguageCode = "en");

    /// <summary>
    /// Binds the text-free Stage-4 response plan to mature, governed semantic
    /// facts through the same curriculum authority.  It never performs
    /// retained-text retrieval or surface realization.
    /// </summary>
    Task<LegendConnectContentBoundResponseMeaningPlanResult> TryBindConversationContentAsync(
        string input,
        LegendConnectDiscourseStateSnapshot? discourseState,
        CancellationToken cancellationToken = default,
        string sourceLanguageCode = "en");

    Task<LegendConnectUtteranceMeaningGraphSnapshot> AnalyzeReusableMeaningGraphAsync(
        string input,
        CancellationToken cancellationToken = default,
        string sourceLanguageCode = "en");

    Task<IReadOnlyList<LegendConnectDiscourseReferenceRuleSnapshot>>
        GetProductionDiscourseReferenceRulesAsync(
            string sourceLanguageCode,
            IReadOnlyList<string> selectorSemanticSignatures,
            CancellationToken cancellationToken = default);

    Task<LegendConnectKnowledgeSubmissionResult> SubmitFounderKnowledgeAsync(
        string founderUserId,
        LegendConnectKnowledgeSubmission submission,
        CancellationToken cancellationToken = default,
        Guid? reusableSourceTextUnitId = null,
        Guid? reusableTargetTextUnitId = null);

    Task<LegendConnectKnowledgeSubmissionResult> CorrectFounderKnowledgeAsync(
        string founderUserId,
        Guid supersededAlignmentId,
        LegendConnectKnowledgeSubmission replacement,
        CancellationToken cancellationToken = default,
        Guid? reusableTargetTextUnitId = null);

    Task<LegendConnectHistoricalReevaluationProgress> ReconcileHistoricalOperationalTranslationsAsync(
        int take,
        Guid? afterId,
        CancellationToken cancellationToken = default);

    Task ReconcileHistoricalOperationalTranslationAsync(
        Guid translationId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectVerifiedTargetBatchResult> SubmitFounderVerifiedTargetsAsync(
        string founderUserId,
        LegendConnectVerifiedTargetSubmission submission,
        CancellationToken cancellationToken = default);

    Task<LegendConnectQualityReviewActionResult> ApproveProviderObservationAsync(
        string founderUserId,
        Guid alignmentId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectQualityReviewActionResult> RejectProviderObservationAsync(
        string founderUserId,
        Guid alignmentId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectQualityReviewActionResult> LeaveProviderObservationUnresolvedAsync(
        string founderUserId,
        Guid alignmentId,
        CancellationToken cancellationToken = default);

    Task<LegendConnectCurriculumSubmissionResult> SubmitFounderCurriculumManifestAsync(
        string founderUserId,
        LegendConnectCurriculumManifestSubmission submission,
        CancellationToken cancellationToken = default);

    Task<LegendConnectCurriculumSubmissionResult> SubmitFounderCurriculumAsync(
        string founderUserId,
        LegendConnectCurriculumBatchSubmission submission,
        CancellationToken cancellationToken = default);
}

public static class LegendLanguageIdentity
{
    public static bool TryNormalize(string? value, out string languageCode)
    {
        languageCode = string.Empty;
        var candidate = value?.Trim().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 32)
            return false;

        var segments = candidate.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment =>
                segment.Length is < 1 or > 8 || !segment.All(char.IsLetterOrDigit)))
            return false;

        var isPrivateUse = string.Equals(segments[0], "x", StringComparison.OrdinalIgnoreCase);
        if (isPrivateUse)
        {
            if (segments.Length < 2)
                return false;
        }
        else if (segments[0].Length is < 2 or > 8 || !segments[0].All(char.IsLetter))
            return false;

        languageCode = string.Join('-', segments.Select((segment, index) =>
            isPrivateUse
                ? segment.ToLowerInvariant()
                : index switch
                {
                    0 => segment.ToLowerInvariant(),
                    _ when segment.Length == 4 && segment.All(char.IsLetter) =>
                        char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant(),
                    _ when segment.Length is 2 or 3 && segment.All(char.IsLetterOrDigit) => segment.ToUpperInvariant(),
                    _ => segment
                }));
        return true;
    }

    public static string BaseCode(string languageCode) =>
        languageCode.Split('-', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

    public static string PairKey(string sourceLanguageCode, string targetLanguageCode) =>
        $"{sourceLanguageCode}:{targetLanguageCode}";

    public static string DatasetNamespace(string languageCode) => "/" + languageCode;

    public static string TextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeText(text))));

    public static string NormalizeText(string text) =>
        string.Join(' ', (text ?? string.Empty).Normalize(NormalizationForm.FormKC)
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// A language-neutral shape used only for contextual evaluation. It does
    /// not encode language grammar or formulate production output by itself.
    /// </summary>
    public static string ContextPatternSignature(string text)
    {
        var normalized = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token =>
            {
                var letters = token.Count(char.IsLetter);
                var digits = token.Count(char.IsDigit);
                var symbols = token.Length - letters - digits;
                return $"w{letters}:d{digits}:p{Math.Max(0, symbols)}";
            }));
    }

    public static string ContextSignature(
        string? category,
        string? usageRegister,
        string? regionalVariant) =>
        string.Join('|', new[] { category, usageRegister, regionalVariant }
            .Select(value => NormalizeText(value ?? string.Empty).ToLowerInvariant()));

}
