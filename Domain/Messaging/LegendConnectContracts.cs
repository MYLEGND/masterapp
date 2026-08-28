using System.Security.Cryptography;
using System.Text;

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

    Task<LegendLanguageDefinitionSnapshot?> GetLanguageAsync(
        string? language,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegendLanguageDefinitionSnapshot>> ListEnabledTranslationLanguagesAsync(
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
    IReadOnlyList<LegendConnectCrossExampleSemanticRelationshipSubmission>? CrossExampleSemanticRelationships = null);

public sealed record LegendConnectCurriculumSubmissionResult(
    bool Succeeded,
    bool DuplicatePrevented,
    string? ErrorCode,
    string? Message,
    string? FamilyKey,
    Guid? CurriculumFamilyId,
    int EnglishExampleCount,
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
    long StructuralInternalServeCount = 0);

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
    long StructuralInternalServeCount = 0);

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
    IReadOnlyList<LegendConnectMachineTeachingExampleSubmission> Examples);

public sealed record LegendConnectMachineTeachingSubmissionResult(
    bool Succeeded,
    bool DuplicatePrevented,
    string State,
    string? ErrorCode,
    string Message,
    Guid? CorpusCandidateId,
    Guid? ProposalId);

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
    int IndependentSupportCount);

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
    bool ReplacesActiveBinding);

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
    int ReasoningEvidenceCount = 0);

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
    bool RequiresEscalation);

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

    Task<LegendConnectRetainedKnowledgeSearchSnapshot> SearchRetainedKnowledgeAsync(
        string query,
        string? sourceLanguageCode = null,
        string? targetLanguageCode = null,
        int take = 12,
        CancellationToken cancellationToken = default);

    Task<LegendConnectNativeInferenceSnapshot> TryInferConversationAsync(
        string input,
        IReadOnlyList<LegendConnectConversationContextItem> context,
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
        CancellationToken cancellationToken = default);

    Task<LegendConnectResponseMeaningPlanResult> TryPlanConversationAsync(
        string input,
        LegendConnectDiscourseStateSnapshot? discourseState,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds the text-free Stage-4 response plan to mature, governed semantic
    /// facts through the same curriculum authority.  It never performs
    /// retained-text retrieval or surface realization.
    /// </summary>
    Task<LegendConnectContentBoundResponseMeaningPlanResult> TryBindConversationContentAsync(
        string input,
        LegendConnectDiscourseStateSnapshot? discourseState,
        CancellationToken cancellationToken = default);

    Task<LegendConnectUtteranceMeaningGraphSnapshot> AnalyzeReusableMeaningGraphAsync(
        string input,
        CancellationToken cancellationToken = default);

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
