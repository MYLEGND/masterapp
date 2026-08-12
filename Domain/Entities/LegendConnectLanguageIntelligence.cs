namespace Domain.Entities;

/// <summary>
/// Server-owned language control-plane record. LanguageCode is the canonical
/// BCP-47 identifier used by mobile contracts and every Legend Connect
/// partition; display metadata is data rather than client or service branches.
/// </summary>
public sealed class LegendLanguageDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LanguageCode { get; set; } = string.Empty;
    public string BaseLanguageCode { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsTranslationEnabled { get; set; }
    public bool IsLearningEnabled { get; set; }
    public string DatasetNamespace { get; set; } = string.Empty;
    public string StoragePartition { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A directional relationship between two language datasets. PairKey is always
/// SourceLanguageCode:TargetLanguageCode and never substitutes for either
/// language's monolingual dataset identity.
/// </summary>
public sealed class LegendLanguagePair
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PairKey { get; set; } = string.Empty;
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string TargetLanguageCode { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string TranslationMemoryPartition { get; set; } = string.Empty;
    public int CorpusCoverage { get; set; }
    public string QualityState { get; set; } = "Observation";
    public string? ActiveModelVersion { get; set; }
    public string ProviderFallbackPolicy { get; set; } = "AzureTranslator";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One normalized, deduplicated text asset within exactly one language
/// dataset. Raw message bodies are never inserted here unless a centralized
/// policy has established training eligibility.
/// </summary>
public sealed class LegendLanguageTextUnit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LanguageCode { get; set; } = string.Empty;
    public string StoragePartition { get; set; } = string.Empty;
    public string NormalizedHash { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public Guid? GlobalConceptId { get; set; }
    public string Provenance { get; set; } = string.Empty;
    public bool IsTrainingEligible { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Optional language-neutral relationship holder. It references language text
/// units rather than carrying multilingual text itself.
/// </summary>
public sealed class LegendGlobalConcept
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ConceptKey { get; set; } = string.Empty;
    public string? Category { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Reusable directional translation-memory relationship. This is deliberately
/// separate from MessageTranslations, which remains an operational message
/// presentation cache.
/// </summary>
public sealed class LegendTranslationAlignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PairKey { get; set; } = string.Empty;
    public Guid SourceTextUnitId { get; set; }
    public Guid TargetTextUnitId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ProviderModel { get; set; }
    public decimal? Confidence { get; set; }
    public string QualityState { get; set; } = "Observation";
    public bool HumanVerified { get; set; }
    public int ObservationCount { get; set; }
    public DateTime? SupersededUtc { get; set; }
    public Guid? SupersededByAlignmentId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A generic, reusable contextual relationship derived only from approved
/// language knowledge. It captures structural and usage metadata without
/// encoding grammar rules for any individual language.
/// </summary>
public sealed class LegendLanguageContextRelationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? PairKey { get; set; }
    public Guid SourceTextUnitId { get; set; }
    public Guid RelatedTextUnitId { get; set; }
    public string RelationshipKind { get; set; } = "ContextualExample";
    public string ContextSignature { get; set; } = string.Empty;
    public string SourcePatternSignature { get; set; } = string.Empty;
    public string? ContextCategory { get; set; }
    public string? UsageRegister { get; set; }
    public string? RegionalVariant { get; set; }
    public decimal Confidence { get; set; }
    public string QualityState { get; set; } = "Observation";
    public string Provenance { get; set; } = string.Empty;
    public int ObservationCount { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Durable, idempotent hand-off from production translation to the optional
/// Legend Connect learning pipeline. Source and target text are populated only
/// when the central eligibility policy has approved their retention.
/// </summary>
public sealed class LegendTranslationLearningEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? SourceMessageId { get; set; }
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string TargetLanguageCode { get; set; } = string.Empty;
    public string PairKey { get; set; } = string.Empty;
    public string SourceTextHash { get; set; } = string.Empty;
    public string TargetTextHash { get; set; } = string.Empty;
    public string? SourceText { get; set; }
    public string? TargetText { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Provenance { get; set; } = string.Empty;
    public string? ContextCategory { get; set; }
    public string EligibilityState { get; set; } = string.Empty;
    public string ProcessingState { get; set; } = "Pending";
    /// <summary>
    /// Privacy-safe result of canonical corpus processing. This distinguishes
    /// newly promoted knowledge from an observation that reused an existing
    /// directional alignment without adding another corpus record.
    /// </summary>
    public string? PromotionOutcome { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedUtc { get; set; }
    public string? FailureCode { get; set; }
}

/// <summary>
/// An administrator-approved, provenance-carrying acquisition candidate.
/// There is intentionally no mobile route to author these records.
/// </summary>
public sealed class LegendCorpusCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = string.Empty;
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string TargetLanguageCode { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string SourceTextHash { get; set; } = string.Empty;
    public string Category { get; set; } = "EverydayConversation";
    public string Provenance { get; set; } = string.Empty;
    /// <summary>
    /// Optional structured-curriculum provenance. This extends the existing
    /// candidate authority rather than introducing another expansion queue.
    /// </summary>
    public Guid? CurriculumFamilyId { get; set; }
    public Guid? SourceCurriculumExampleId { get; set; }
    public bool IsApproved { get; set; }
    public int Priority { get; set; }
    public string ProcessingState { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    /// <summary>
    /// Characters actually sent to the provider for this approved candidate.
    /// It is written only after a successful provider result and contains no
    /// corpus text, allowing Founder operations to report priority progress
    /// without creating a second billing ledger.
    /// </summary>
    public long ProviderCharactersConsumed { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedUtc { get; set; }
    public string? FailureCode { get; set; }
}

/// <summary>
/// A Founder-authored semantic curriculum family. It identifies the shared
/// meaning being taught; it is deliberately not a cross-language grammar rule.
/// </summary>
public sealed class LegendCurriculumFamily
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FamilyKey { get; set; } = string.Empty;
    public string? SemanticCategory { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Membership of a canonical language asset in a curriculum family. Target
/// examples retain their source example lineage while remaining assets in
/// their own language dataset.
/// </summary>
public sealed class LegendCurriculumExample
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CurriculumFamilyId { get; set; }
    public Guid TextUnitId { get; set; }
    public string LanguageCode { get; set; } = string.Empty;
    public Guid? DerivedFromCurriculumExampleId { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One controlled semantic variation attached to one curriculum example.
/// Values are relational records so they can be compared independently of
/// language-specific textual realization.
/// </summary>
public sealed class LegendCurriculumExampleVariation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CurriculumExampleId { get; set; }
    public string Dimension { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Generic, language-scoped structural pattern maturity. The realization
/// signature is computed from examples in this language only; the family and
/// variation describe shared semantics but never copy English grammar.
/// </summary>
public sealed class LegendLanguageStructuralPattern
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CurriculumFamilyId { get; set; }
    public string LanguageCode { get; set; } = string.Empty;
    public string VariationDimension { get; set; } = string.Empty;
    public string RealizationSignature { get; set; } = string.Empty;
    public string MaturityState { get; set; } = "Observation";
    public int SupportCount { get; set; }
    public int ContradictionCount { get; set; }
    public bool IsProductionEligible { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An auditable same-language comparison supporting one structural pattern.
/// It records the controlled semantic change and the two canonical examples,
/// not an invented grammatical rule.
/// </summary>
public sealed class LegendLanguageStructuralEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StructuralPatternId { get; set; }
    public Guid CurriculumFamilyId { get; set; }
    public string LanguageCode { get; set; } = string.Empty;
    public string VariationDimension { get; set; } = string.Empty;
    public Guid BaselineCurriculumExampleId { get; set; }
    public Guid ComparedCurriculumExampleId { get; set; }
    public string BaselineVariationValue { get; set; } = string.Empty;
    public string ComparedVariationValue { get; set; } = string.Empty;
    public string EvidenceSignature { get; set; } = string.Empty;
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Reconciled, per-billing-period provider consumption and reservations.
/// Reservations make live and background capacity decisions safe across server
/// instances without turning an in-memory counter into authority.
/// </summary>
public sealed class LegendTranslationProviderCapacity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = string.Empty;
    public DateOnly BillingPeriodStart { get; set; }
    public long ConfiguredCapacityCharacters { get; set; }
    public long ReservedLiveCharacters { get; set; }
    public long LiveCharactersConsumed { get; set; }
    public long BootstrapCharactersConsumed { get; set; }
    public long TrainingCharactersConsumed { get; set; }
    public long ReservedLiveCapacityCharacters { get; set; }
    public long ProjectedLiveCharacters { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// A durable, bounded lease backing one provider-capacity reservation. This is
/// owned by the existing provider-capacity ledger: it is not a second queue or
/// billing authority. Its sole purpose is to let a later instance release an
/// interrupted reservation exactly once before retrying the canonical work.
/// </summary>
public sealed class LegendTranslationProviderReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = string.Empty;
    public DateOnly BillingPeriodStart { get; set; }
    public string ReservationReference { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public long Characters { get; set; }
    public string State { get; set; } = "Reserved";
    public DateTime ReservationExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Privacy-safe aggregate demand for one directional pair. It carries no
/// source/target text or member identity and supplies corpus prioritization
/// with real community demand rather than named-language code paths.
/// </summary>
public sealed class LegendTranslationPairDemand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PairKey { get; set; } = string.Empty;
    public long TranslationRequestCount { get; set; }
    public long ProviderCharacterCount { get; set; }
    public long TranslationMemoryHitCount { get; set; }
    /// <summary>
    /// Requests for which trusted internal knowledge did not satisfy the
    /// route, so provider-backed work was required next. This legacy column
    /// name is retained for deployed-schema compatibility; it is not a count
    /// of completed Azure calls. Completed provider operations live in the
    /// usage ledgers/system usage aggregate.
    /// </summary>
    public long AzureFallbackCount { get; set; }
    public long ContextualCompositionObservationCount { get; set; }
    public long ContextualInternalServeCount { get; set; }
    public DateTime LastRequestedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Daily server-owned aggregate for translation paths that are not directional
/// pairs, notably same-language bypasses. It deliberately contains no text,
/// identity, or provider credential data.
/// </summary>
public sealed class LegendTranslationSystemUsage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly UsageDate { get; set; }
    public long SameLanguageBypassCount { get; set; }
    public long ProviderOperationCount { get; set; }
    public long ProviderBillableCharacters { get; set; }
    public long SameLanguageCharactersAvoided { get; set; }
    public long TranslationMemoryCharactersAvoided { get; set; }
    public long ContextualCharactersAvoided { get; set; }
    public long QuotaDeniedRequestCount { get; set; }
    public long ProviderFailureCount { get; set; }
    public long GroupUniqueTargetReuseCount { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Bounded, sanitized operational evidence for the Founder health surface.
/// It is intentionally separate from private message/audit content.
/// </summary>
public sealed class LegendConnectOperationalEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public string Status { get; set; } = string.Empty;
    public string? LanguageCode { get; set; }
    public string? PairKey { get; set; }
    public string? CorrelationId { get; set; }
    public string? ErrorCode { get; set; }
    public string? Summary { get; set; }
    public bool IsResolved { get; set; }
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Append-only Founder operation evidence. Corrections supersede prior
/// knowledge rather than erasing its audit history.
/// </summary>
public sealed class LegendConnectKnowledgeAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FounderUserId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = string.Empty;
    public string? PairKey { get; set; }
    public Guid? TextUnitId { get; set; }
    public Guid? AlignmentId { get; set; }
    public Guid? SupersededAlignmentId { get; set; }
    public string? Detail { get; set; }
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The one durable, deployment-wide runtime policy for autonomous Legend
/// Connect learning. Source configuration is used only until this singleton
/// record is first changed by a Founder; thereafter this is the authority
/// observed by every web and worker instance.
/// </summary>
public sealed class LegendConnectRuntimePolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = "Global";
    public long MonthlyProviderCapacityCharacters { get; set; }
    public long LiveTranslationReserveCharacters { get; set; }
    public long MaximumSafeCorpusConsumptionCharacters { get; set; }
    public bool CorpusAcquisitionEnabled { get; set; }
    public bool LearningEnabled { get; set; } = true;
    public string ContextualCompositionMode { get; set; } = "Shadow";
    public decimal ContextualMinimumConfidence { get; set; } = 0.98m;
    public string PriorityMode { get; set; } = "Automatic";
    public string? PriorityLanguageCode { get; set; }
    public string? PriorityPairKey { get; set; }
    public string? PriorityLevel { get; set; }
    public DateTime? LastLearningWorkerHeartbeatUtc { get; set; }
    public DateTime? LastAcquisitionWorkerHeartbeatUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// A Founder-selected target dataset for the one autonomous acquisition
/// policy. It is relational rather than a serialized list so each selected
/// language remains independently auditable and the planner can apply one
/// clear English-source focus without introducing another queue.
/// </summary>
public sealed class LegendConnectAutonomousLanguageFocus
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuntimePolicyId { get; set; }
    public string TargetLanguageCode { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
