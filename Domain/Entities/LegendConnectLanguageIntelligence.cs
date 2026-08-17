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
    /// <summary>
    /// Origin of this directional observation. Provider output keeps
    /// ProviderDerived provenance even when a later human action validates or
    /// supersedes the alignment.
    /// </summary>
    public string Provenance { get; set; } = string.Empty;
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
/// One durable, idempotent quality signal about a provider-derived directional
/// observation. It links the observation to canonical supporting or
/// conflicting evidence without turning a provider result into verified
/// knowledge. Historical evidence is retained after an alignment is corrected,
/// rejected, or otherwise superseded.
/// </summary>
public sealed class LegendTranslationQualityEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ObservedAlignmentId { get; set; }
    public string PairKey { get; set; } = string.Empty;
    public Guid SourceTextUnitId { get; set; }
    public Guid TargetTextUnitId { get; set; }
    public Guid? RelatedAlignmentId { get; set; }
    public Guid? StructuralPatternId { get; set; }
    public Guid? ContextRelationshipId { get; set; }
    /// <summary>
    /// Supported, Contradictory, or Insufficient. This is an evidence signal,
    /// not a promotion or replacement decision.
    /// </summary>
    public string Signal { get; set; } = "Insufficient";
    public string ReasonCode { get; set; } = string.Empty;
    /// <summary>
    /// Open, Approved, Corrected, or Rejected. The transition is made only by
    /// the canonical Founder validation path.
    /// </summary>
    public string ResolutionState { get; set; } = "Open";
    /// <summary>
    /// Stable identity derived from the observation and canonical evidence.
    /// It prevents retries or repeated provider calls from manufacturing
    /// artificial support.
    /// </summary>
    public string EvidenceIdentity { get; set; } = string.Empty;
    /// <summary>
    /// Optional language-neutral semantic component identity supplied by
    /// Founder-controlled curriculum evidence. Empty means the observation
    /// did not have sufficient semantic information; it is never guessed
    /// from source or target words.
    /// </summary>
    public string? SemanticSignature { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public Guid? ResolvedByAlignmentId { get; set; }
    public DateTime? SupersededUtc { get; set; }
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
    /// <summary>
    /// Historical relationships remain auditable after their source identity
    /// is reconciled, but cannot participate in contextual reuse.
    /// </summary>
    public DateTime? SupersededUtc { get; set; }
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

    /// <summary>
    /// Phase-3 teacher/critic orchestration remains subordinate to this
    /// existing autonomous candidate. It does not create another work queue.
    /// Existing rows remain NotStarted and are not historically replayed by
    /// this milestone; newly processed acquisition work explicitly enters
    /// Pending after canonical provider-observation processing succeeds.
    /// </summary>
    public string TeacherProposalProcessingState { get; set; } = "NotStarted";
    public int TeacherProposalAttemptCount { get; set; }
    public DateTime? TeacherProposalLeaseExpiresUtc { get; set; }
    public DateTime? TeacherProposalProcessedUtc { get; set; }
    public string? TeacherProposalFailureCode { get; set; }
}

/// <summary>
/// One independently identifiable machine-generated curriculum-family proposal
/// produced by the governed Phase-2 teacher and independently reviewed by its
/// critic. This is an auditable proposal artifact only: it is not a corpus
/// unit, curriculum family, alignment, structural assertion, training example,
/// SystemValidated fact, or production-eligible authority.
///
/// Work ownership, leasing, retry, gap selection, and scheduling remain on the
/// existing LegendCorpusCandidate and existing acquisition worker.
/// </summary>
public sealed class LegendLanguageTeacherProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CorpusCandidateId { get; set; }

    /// <summary>
    /// Stable identity derived from the originating candidate, exact governed
    /// evidence identities, and canonicalized family proposal payload.
    /// </summary>
    public string ProposalIdentity { get; set; } = string.Empty;

    public string PairKey { get; set; } = string.Empty;
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string TargetLanguageCode { get; set; } = string.Empty;
    public string EvidenceIdentityHash { get; set; } = string.Empty;

    public string FamilyKey { get; set; } = string.Empty;
    public string SemanticCategory { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public decimal Confidence { get; set; }

    /// <summary>
    /// Exact bounded Phase-2 family proposal serialized for later canonical
    /// Phase-4 validation. Persisting it does not admit any contained text into
    /// a language dataset.
    /// </summary>
    public string ProposalPayloadJson { get; set; } = string.Empty;

    public bool CriticApproved { get; set; }
    public decimal? CriticConfidence { get; set; }
    public string CriticReasonCodesJson { get; set; } = "[]";

    /// <summary>
    /// AwaitingCanonicalValidation or CriticRejected in Phase 3.
    /// Phase 4 alone may introduce further canonical-validation outcomes.
    /// </summary>
    public string ValidationState { get; set; } = string.Empty;

    /// <summary>
    /// Explicitly prevents machine output from masquerading as Founder or
    /// human authority.
    /// </summary>
    public string Provenance { get; set; } = "MachineProposed";

    /// <summary>
    /// Phase-4 canonical validation reuses ValidationState as the sole
    /// proposal-state authority. These fields provide only durable claim,
    /// retry, failure, and completion metadata; they do not create a second
    /// queue or validation state machine.
    /// </summary>
    public int CanonicalValidationAttemptCount { get; set; }
    public DateTime? CanonicalValidationLeaseExpiresUtc { get; set; }
    public DateTime? CanonicalValidatedUtc { get; set; }
    public string? CanonicalValidationFailureCode { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
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
    public DateTime? SupersededUtc { get; set; }
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
    /// <summary>
    /// Stable identity of the explicitly controlled proposition being
    /// observed. It is derived only from Founder-supplied variation
    /// dimension/value pairs, never from a family, submission, or source
    /// asset. Those records remain the provenance of individual evidence.
    /// </summary>
    public string PropositionSignature { get; set; } = string.Empty;
    public Guid CurriculumFamilyId { get; set; }
    /// <summary>
    /// Target-language realization evidence is directional. The empty scope
    /// is reserved for monolingual Founder source observations; no target
    /// pair may borrow their maturity.
    /// </summary>
    public string PairKey { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = string.Empty;
    public string VariationDimension { get; set; } = string.Empty;
    public string RealizationSignature { get; set; } = string.Empty;
    public string MaturityState { get; set; } = "Observation";
    public int SupportCount { get; set; }
    public int ContradictionCount { get; set; }
    public int IndependentSourceCount { get; set; }
    public int HumanVerifiedSupportCount { get; set; }
    public int ProviderOnlySupportCount { get; set; }
    public decimal Confidence { get; set; }
    public bool IsProductionEligible { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A reusable observation drawn from multiple proposition-level structural
/// comparisons. Its identity is limited to explicit Founder-controlled
/// dimensions and the observed layouts of known components. It is therefore
/// neither a grammar rule nor a replacement for <see cref="LegendLanguageStructuralPattern"/>:
/// proposition evidence remains the durable source lineage for this aggregate.
/// </summary>
public sealed class LegendLanguageStructuralRelationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>
    /// Directional pair scope. The empty scope remains a monolingual source
    /// observation and cannot provide target-language maturity.
    /// </summary>
    public string PairKey { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = string.Empty;
    /// <summary>
    /// The explicitly controlled dimension that changed between each pair of
    /// examples. It is never inferred from text.
    /// </summary>
    public string VariationDimension { get; set; } = string.Empty;
    /// <summary>
    /// Stable identity of a candidate reusable relationship. It deliberately
    /// excludes curriculum family, exact text, lexical values, and
    /// PropositionSignature so independently controlled propositions can
    /// contribute without being conflated with one another.
    /// </summary>
    public string RelationshipSignature { get; set; } = string.Empty;
    /// <summary>
    /// The first explicitly anchored component layout observed for this
    /// relationship. A later trusted comparison with the same controlled
    /// identity but a different layout is retained as contradictory evidence.
    /// </summary>
    public string AnchorLayoutSignature { get; set; } = string.Empty;
    public string MaturityState { get; set; } = "Observation";
    public int SupportCount { get; set; }
    public int ContradictionCount { get; set; }
    public int IndependentSourceCount { get; set; }
    public int HumanVerifiedSupportCount { get; set; }
    public int ProviderOnlySupportCount { get; set; }
    public decimal Confidence { get; set; }
    public bool IsProductionEligible { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
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
    /// <summary>
    /// Optional link to the broader reusable relationship that this already
    /// auditable proposition comparison can support or contradict. Null means
    /// the comparison lacked sufficiently explicit known component evidence.
    /// </summary>
    public Guid? StructuralRelationshipId { get; set; }
    /// <summary>
    /// The contribution of this comparison to its broader reusable
    /// relationship. It is intentionally separate from
    /// <see cref="ContributionState"/>, which remains the contribution to the
    /// exact controlled proposition.
    /// </summary>
    public string? StructuralRelationshipContributionState { get; set; }
    public Guid CurriculumFamilyId { get; set; }
    /// <summary>
    /// The same non-null pair scope as the owning structural pattern. Empty
    /// denotes a monolingual Founder source comparison.
    /// </summary>
    public string PairKey { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = string.Empty;
    public string VariationDimension { get; set; } = string.Empty;
    public Guid BaselineCurriculumExampleId { get; set; }
    public Guid ComparedCurriculumExampleId { get; set; }
    public string BaselineVariationValue { get; set; } = string.Empty;
    public string ComparedVariationValue { get; set; } = string.Empty;
    public string EvidenceSignature { get; set; } = string.Empty;
    /// <summary>
    /// Language-local component layouts observed in the two examples. Their
    /// lexical identities live in the normalized lexeme records; these
    /// signatures preserve the structural comparison without asserting an
    /// English rule for another language.
    /// </summary>
    public string BaselineComponentSignature { get; set; } = string.Empty;
    public string ComparedComponentSignature { get; set; } = string.Empty;
    /// <summary>
    /// Stable identity of the distinct source assets that supplied this
    /// comparison. Repeated provider calls and exact duplicates therefore
    /// cannot manufacture independent support.
    /// </summary>
    public string IndependentSourceIdentity { get; set; } = string.Empty;
    /// <summary>
    /// Supported, Contradictory, or Insufficient. Provider-derived evidence
    /// starts Insufficient and cannot mature a target-language pattern on its
    /// own.
    /// </summary>
    public string ContributionState { get; set; } = "Insufficient";
    public bool IsHumanVerifiedSupport { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A reusable, language-local lexical identity. It is deliberately an exact
/// observed surface form, not a guessed lemma or a universal grammar concept.
/// Controlled curriculum anchors may later connect it to an explicit meaning.
/// </summary>
public sealed class LegendLanguageLexeme
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LanguageCode { get; set; } = string.Empty;
    public string NormalizedHash { get; set; } = string.Empty;
    public string SurfaceForm { get; set; } = string.Empty;
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An auditable occurrence of one lexical identity inside one atomic language
/// unit. Ordered positions and character boundaries preserve compositional
/// evidence without assigning a part of speech or semantic role by guesswork.
/// </summary>
public sealed class LegendLanguageLexicalOccurrence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TextUnitId { get; set; }
    public Guid LexemeId { get; set; }
    public int TokenIndex { get; set; }
    public int CharacterOffset { get; set; }
    public int CharacterLength { get; set; }
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A directly observed relationship between adjacent lexical components in
/// one atomic unit. It preserves phrase/sequence evidence only; it never
/// promotes adjacency into a grammatical or semantic assertion on its own.
/// </summary>
public sealed class LegendLanguageLexicalRelationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TextUnitId { get; set; }
    public Guid SourceLexemeId { get; set; }
    public Guid RelatedLexemeId { get; set; }
    public string RelationshipKind { get; set; } = "AdjacentToken";
    public int SourceTokenIndex { get; set; }
    public int RelatedTokenIndex { get; set; }
    public int ObservationCount { get; set; } = 1;
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An explicit Founder curriculum meaning anchor. It connects a controlled
/// dimension/value to the whole example and, when an exact supplied component
/// is present, its lexical occurrence. It records evidence; it never infers
/// grammar or semantic roles from English surface form alone.
/// </summary>
public sealed class LegendLanguageCompositionalAnchor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LanguageCode { get; set; } = string.Empty;
    /// <summary>
    /// Directional scope for a Founder-verified target realization. Source
    /// curriculum anchors predate directional target realization review and
    /// intentionally retain an empty scope.
    /// </summary>
    public string? PairKey { get; set; }
    public Guid TextUnitId { get; set; }
    public Guid? LexemeId { get; set; }
    public int? ComponentStartTokenIndex { get; set; }
    public int? ComponentLength { get; set; }
    public Guid CurriculumFamilyId { get; set; }
    public Guid CurriculumExampleId { get; set; }
    public string Dimension { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    /// <summary>
    /// Stable language-neutral identity for one Founder-controlled semantic
    /// dimension/value. It does not contain a curriculum-family, source-word,
    /// or target-language rule, so independent controlled examples may
    /// accumulate meaning evidence safely. It stays blank for historical rows
    /// until existing controlled evidence is safely reconciled.
    /// </summary>
    public string? SemanticSignature { get; set; }
    public string AnchorSignature { get; set; } = string.Empty;
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A bounded, cross-example hypothesis about one directional target-language
/// realization. This is deliberately distinct from a
/// <see cref="LegendLanguageCompositionalAnchor"/>: candidates preserve the
/// inference and review lifecycle, while anchors remain the sole trusted
/// span authority after a Founder explicitly verifies a candidate.
/// </summary>
public sealed class LegendLanguageTargetRealizationCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PairKey { get; set; } = string.Empty;
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string TargetLanguageCode { get; set; } = string.Empty;
    /// <summary>
    /// Existing language-neutral controlled component identity. It is never
    /// inferred from source or target words.
    /// </summary>
    public string SemanticSignature { get; set; } = string.Empty;
    public string VariationDimension { get; set; } = string.Empty;
    public string SemanticValue { get; set; } = string.Empty;
    /// <summary>Observed target-language span proposed for Founder review.</summary>
    public string TargetRealization { get; set; } = string.Empty;
    /// <summary>
    /// Stable source-context identity. It prevents distinct controlled
    /// contexts from being conflated as a single realization claim.
    /// </summary>
    public string ContextSignature { get; set; } = string.Empty;
    /// <summary>
    /// A privacy-safe hash of the observed target template with this span
    /// represented as a slot. It describes evidence only; it is not an
    /// executable production template.
    /// </summary>
    public string TemplateSignature { get; set; } = string.Empty;
    public string SlotSignature { get; set; } = string.Empty;
    /// <summary>Stable canonical identity for idempotent derivation.</summary>
    public string CandidateIdentity { get; set; } = string.Empty;
    /// <summary>Candidate, FounderVerified, Rejected, or Contradicted.</summary>
    public string VerificationState { get; set; } = "Candidate";
    /// <summary>Observation, Candidate, Supported, Validated, or Superseded.</summary>
    public string MaturityState { get; set; } = "Observation";
    public int SupportCount { get; set; }
    public int IndependentSourceCount { get; set; }
    public int HumanVerifiedSupportCount { get; set; }
    public int ProviderOnlySupportCount { get; set; }
    public int ContradictionCount { get; set; }
    public decimal Confidence { get; set; }
    /// <summary>
    /// This milestone never opens composition. The field makes that gate
    /// explicit and remains false until a later composition milestone proves
    /// all independent requirements.
    /// </summary>
    public bool IsProductionEligible { get; set; }
    public Guid? VerifiedAnchorId { get; set; }
    public DateTime? VerifiedUtc { get; set; }
    public string? VerifiedByFounderUserId { get; set; }
    public DateTime? RejectedUtc { get; set; }
    public string? RejectedByFounderUserId { get; set; }
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One source/target controlled contrast that supports a target-realization
/// candidate. It extends the existing cross-example lineage with the exact
/// observed target span; it is not a second corpus, learner, or evidence
/// engine.
/// </summary>
public sealed class LegendLanguageTargetRealizationEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateId { get; set; }
    public Guid SourceCurriculumExampleId { get; set; }
    public Guid TargetCurriculumExampleId { get; set; }
    public Guid SourceTextUnitId { get; set; }
    public Guid TargetTextUnitId { get; set; }
    public Guid? SourceAlignmentId { get; set; }
    public int TargetStartTokenIndex { get; set; }
    public int TargetTokenLength { get; set; }
    public string EvidenceIdentity { get; set; } = string.Empty;
    public bool IsHumanVerifiedSupport { get; set; }
    public string Provenance { get; set; } = string.Empty;
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Immutable Founder raw-training provenance. It records a submitted source
/// block separately from the canonical atomic language assets derived from it.
/// The row is never a translation-memory asset or an expansion candidate.
/// </summary>
public sealed class LegendFounderTrainingSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? FounderUserId { get; set; }
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string RawTextHash { get; set; } = string.Empty;
    public string? ContextCategory { get; set; }
    public string? UsageRegister { get; set; }
    public string? RegionalVariant { get; set; }
    /// <summary>
    /// Links a pre-decomposition canonical source asset when this record was
    /// created by the bounded legacy reconciliation path.
    /// </summary>
    public Guid? LegacySourceTextUnitId { get; set; }
    public int RawCharacterCount { get; set; }
    public int AtomicUnitCount { get; set; }
    public string ProcessingState { get; set; } = "Ingested";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedUtc { get; set; }
}

/// <summary>
/// Ordered provenance from one raw Founder submission to one canonical atomic
/// language asset. Unit classification and paragraph position describe the
/// ingestion decision without creating another corpus or curriculum store.
/// </summary>
public sealed class LegendFounderTrainingSubmissionUnit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubmissionId { get; set; }
    public Guid TextUnitId { get; set; }
    public int SequenceNumber { get; set; }
    public int ParagraphNumber { get; set; }
    public string UnitType { get; set; } = string.Empty;
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
    public long StructuralInternalServeCount { get; set; }
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
    public long StructuralCompositionCharactersAvoided { get; set; }
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
    public DateTime? LastLearningWorkerHeartbeatUtc { get; set; }
    public DateTime? LastAcquisitionWorkerHeartbeatUtc { get; set; }
    /// <summary>
    /// The last canonical language-intelligence evaluator version that
    /// completed a bounded replay of all active historical evidence. Raw
    /// Founder provenance and historical observations are never versioned or
    /// rewritten by this watermark.
    /// </summary>
    public int CompletedLanguageIntelligenceEvaluatorVersion { get; set; }
    /// <summary>
    /// The evaluator version currently being replayed through the existing
    /// learning worker. A zero value means no replay has been initialized.
    /// </summary>
    public int TargetLanguageIntelligenceEvaluatorVersion { get; set; }
    /// <summary>
    /// Durable phase of the one canonical historical reevaluation cycle.
    /// It is operational state only; the curriculum, intelligence, and
    /// correction authorities remain the source of derived knowledge.
    /// </summary>
    public string LanguageIntelligenceReevaluationPhase { get; set; } = "Complete";
    /// <summary>
    /// The last stable historical identity completed in the current phase.
    /// The existing worker uses it solely to continue a bounded replay.
    /// </summary>
    public Guid? LanguageIntelligenceReevaluationCursor { get; set; }
    public DateTime? LanguageIntelligenceReevaluationStartedUtc { get; set; }
    public DateTime? LanguageIntelligenceReevaluationCompletedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    // Historical persistence only. These members have no runtime-policy
    // contract or consumer; autonomous focus rows are the sole scope source.
    private string PriorityMode { get; set; } = "Automatic";
    private string? PriorityLanguageCode { get; set; }
    private string? PriorityPairKey { get; set; }
    private string? PriorityLevel { get; set; }
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

/// <summary>
/// Durable orchestration state for one Founder-authored multi-family curriculum
/// manifest. This is not a second curriculum authority. It stores only the
/// accepted payload, progress, retry, and lease state needed so large work
/// survives HTTP completion, cancellation, and App Service recycle.
/// </summary>
public sealed class LegendCurriculumManifestWorkItem
{
    public Guid Id { get; set; }
    public string FounderUserId { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public int FamilyCount { get; set; }
    public int ExampleCount { get; set; }
    public int NextFamilyIndex { get; set; }
    public string ProcessingState { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

/// <summary>
/// Durable orchestration state for one governed LEGEND model-training
/// generation. This record is not language knowledge and cannot promote
/// translations, structural evidence, or itself into production.
///
/// Canonical corpus/evidence remains the sole source of training truth.
/// Future teacher, training, evaluation, and promotion phases must move
/// through this lifecycle rather than creating independent schedulers,
/// queues, or model authorities.
/// </summary>
public sealed class LegendConnectModelTrainingRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Deterministic idempotency identity for the exact governed training
    /// generation. Concurrent instances must converge on this identity.
    /// </summary>
    public string RunKey { get; set; } = string.Empty;

    /// <summary>
    /// Logical model scope. "Global" is valid for a multilingual model;
    /// a future directional scope may use the canonical pair identity.
    /// This field does not itself grant language authority.
    /// </summary>
    public string ScopeKey { get; set; } = "Global";

    /// <summary>
    /// Monotonically increasing generation within ScopeKey.
    /// </summary>
    public int Generation { get; set; }

    /// <summary>
    /// Hash of the exact canonical training manifest selected from existing
    /// governed LEGEND evidence. No corpus text is stored on this record.
    /// </summary>
    public string DatasetIdentity { get; set; } = string.Empty;

    /// <summary>
    /// Language-intelligence evaluator version under which the dataset was
    /// assembled. This prevents an old interpretation from silently training
    /// a new generation after evaluator semantics change.
    /// </summary>
    public int DatasetEvaluatorVersion { get; set; }

    /// <summary>
    /// Provider/model lineage only. These values never imply approval.
    /// </summary>
    public string TrainingProvider { get; set; } = string.Empty;
    public string BaseModel { get; set; } = string.Empty;

    /// <summary>
    /// External artifact/job identities are populated by a later training
    /// phase. They remain null during this foundation phase.
    /// </summary>
    public string? TrainingFileId { get; set; }
    public string? ExternalJobId { get; set; }
    public string? ChallengerModelVersion { get; set; }

    /// <summary>
    /// Durable orchestration state. Future phases transition this state through
    /// one canonical service using leases and idempotent writes.
    /// </summary>
    public string State { get; set; } = "PendingDataset";

    /// <summary>
    /// Evaluation and promotion are deliberately independent from training.
    /// A completed training job therefore cannot become active merely because
    /// the provider returned a model.
    /// </summary>
    public string EvaluationState { get; set; } = "NotStarted";
    public string PromotionState { get; set; } = "NotEvaluated";

    public int TrainingExampleCount { get; set; }
    public int ValidationExampleCount { get; set; }

    public decimal? HeldOutScore { get; set; }
    public decimal? RegressionScore { get; set; }

    public int AttemptCount { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }

    public string? FailureCode { get; set; }
    public string? FailureDetail { get; set; }

    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime? PromotedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
