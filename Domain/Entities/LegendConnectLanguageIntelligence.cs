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
/// One governed observation that an explicitly controlled source semantic
/// frame transforms into an explicitly controlled result semantic frame.
/// Curriculum examples remain the only surface evidence; this record stores
/// neither a prompt nor an answer and is reusable by any LEGEND reasoning
/// consumer. Aggregate support, independence, contradiction, maturity, and
/// production eligibility are derived from active observations sharing the
/// same transition identity.
/// </summary>
public sealed class LegendSemanticTransitionEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TransitionSignature { get; set; } = string.Empty;
    public string SourceSemanticFrameSignature { get; set; } = string.Empty;
    public string ResultSemanticFrameSignature { get; set; } = string.Empty;
    /// <summary>
    /// Canonical, sorted controlled dimensions for the source frame. The
    /// signature remains the compact identity; this payload lets the one
    /// governed authority evaluate a generic frame without a second ontology.
    /// </summary>
    public string SourceSemanticFrame { get; set; } = string.Empty;
    /// <summary>Canonical, sorted controlled dimensions for the result frame.</summary>
    public string ResultSemanticFrame { get; set; } = string.Empty;
    public string SourceLanguageCode { get; set; } = string.Empty;
    public string ResultLanguageCode { get; set; } = string.Empty;
    public Guid SourceCurriculumExampleId { get; set; }
    public Guid ResultCurriculumExampleId { get; set; }
    /// <summary>
    /// Optional immutable Founder cross-example declaration from which this
    /// transition observation was structurally derived.  Legacy explicit
    /// frame declarations deliberately leave this null; both forms converge
    /// on this one transition-evidence authority and its existing gates.
    /// </summary>
    public Guid? FounderSemanticExampleRelationEvidenceId { get; set; }
    /// <summary>
    /// Stable semantic identity of the Founder-declared relationship. It is
    /// not a family, example, prompt, or result-text identity.
    /// </summary>
    public string? FounderRelationshipSemanticSignature { get; set; }
    /// <summary>Evaluator revision that derived this governed projection.</summary>
    public int? DerivationEvaluatorVersion { get; set; }
    /// <summary>
    /// A stable family-pair identity prevents repeated examples or provider
    /// retries from manufacturing independent transition support.
    /// </summary>
    public string IndependentSourceIdentity { get; set; } = string.Empty;
    /// <summary>Supported, Contradictory, or Insufficient.</summary>
    public string ContributionState { get; set; } = "Insufficient";
    public bool IsHumanVerifiedSupport { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
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
    /// <summary>
    /// The durable, opaque correlation identity for this candidate's complete
    /// machine-learning lifecycle. It contains no corpus or actor content.
    /// </summary>
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

    /// <summary>
    /// Carries the originating candidate's durable correlation identity through
    /// critic, canonical validation, and curriculum admission. Those stages
    /// update this proposal rather than allocating another lifecycle identity.
    /// </summary>
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

    /// <summary>
    /// Phase-5 admission remains on this same governed proposal artifact.
    /// ValidationState remains the one lifecycle authority; these fields add
    /// only restart-safe claim/retry/audit metadata for curriculum admission.
    /// </summary>
    public int CurriculumAdmissionAttemptCount { get; set; }
    public DateTime? CurriculumAdmissionLeaseExpiresUtc { get; set; }
    public DateTime? CurriculumAdmittedUtc { get; set; }
    public string? CurriculumAdmissionFailureCode { get; set; }

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
    /// <summary>
    /// Opaque, Founder-declared semantic-example identity. It never contains
    /// a surface sentence and is used only to bind a cross-example semantic
    /// relationship to one governed meaning graph.
    /// </summary>
    public string? SemanticExampleIdentity { get; set; }
    public Guid? DerivedFromCurriculumExampleId { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One immutable Founder declaration that a governed source meaning graph is
/// structurally related to a governed result meaning graph. It is evidence,
/// never an immediately executable response rule. The existing evaluator
/// derives any reusable transition observation through the normal maturity,
/// contradiction, and production-eligibility gates.
/// </summary>
public sealed class LegendFounderSemanticExampleRelationEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RelationIdentity { get; set; } = string.Empty;
    public string RelationshipSemanticIdentity { get; set; } = string.Empty;
    public string RelationshipSemanticSignature { get; set; } = string.Empty;
    public Guid SourceCurriculumFamilyId { get; set; }
    public Guid SourceCurriculumExampleId { get; set; }
    public Guid ResultCurriculumFamilyId { get; set; }
    public Guid ResultCurriculumExampleId { get; set; }
    public string SourceMeaningGraphSignature { get; set; } = string.Empty;
    public string ResultMeaningGraphSignature { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = string.Empty;
    public string IndependentSourceIdentity { get; set; } = string.Empty;
    /// <summary>Supported, Contradictory, or Insufficient.</summary>
    public string ContributionState { get; set; } = "Supported";
    public bool IsHumanVerifiedSupport { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public int EvaluatorVersion { get; set; }
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
/// One Founder-authored node in an utterance meaning graph.  The node is
/// anchored to an exact controlled span and retains the local node identity
/// needed to represent repeated or related meanings without treating an
/// unordered variation map as syntax.
/// </summary>
public sealed class LegendLanguageMeaningNodeEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LanguageCode { get; set; } = string.Empty;
    public Guid CurriculumFamilyId { get; set; }
    public Guid CurriculumExampleId { get; set; }
    public Guid CompositionalAnchorId { get; set; }
    /// <summary>Founder-local identity, unique within the curriculum example.</summary>
    public string NodeKey { get; set; } = string.Empty;
    /// <summary>Stable semantic identity shared only when Founder evidence agrees.</summary>
    public string SemanticSignature { get; set; } = string.Empty;
    public string SemanticDimension { get; set; } = string.Empty;
    public string SemanticValue { get; set; } = string.Empty;
    /// <summary>Optional Founder-declared local clause scope; never inferred.</summary>
    public string? ClauseKey { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A reusable semantic primitive derived only by indexing independently
/// governed meaning nodes. It contains no surface sentence, positional rule,
/// or inferred role. Phase 2 deliberately keeps it observational until a
/// later evaluator proves an eligible serving use.
/// </summary>
public sealed class LegendLanguageMeaningPrimitive
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LanguageCode { get; set; } = string.Empty;
    public string SemanticSignature { get; set; } = string.Empty;
    public string SemanticDimension { get; set; } = string.Empty;
    public string SemanticValue { get; set; } = string.Empty;
    public string MaturityState { get; set; } = "Observation";
    public int SupportCount { get; set; }
    public int ContradictionCount { get; set; }
    public int IndependentSourceCount { get; set; }
    public int HumanVerifiedSupportCount { get; set; }
    public decimal Confidence { get; set; }
    public bool IsProductionEligible { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The immutable provenance edge from an explicit graph node to its reusable
/// primitive. A replayer may rebuild this index, but cannot manufacture a
/// primitive from sentence adjacency or a model suggestion.
/// </summary>
public sealed class LegendLanguageMeaningPrimitiveEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MeaningPrimitiveId { get; set; }
    public Guid MeaningNodeEvidenceId { get; set; }
    public Guid CurriculumFamilyId { get; set; }
    public Guid CurriculumExampleId { get; set; }
    public string EvidenceIdentity { get; set; } = string.Empty;
    public string IndependentSourceIdentity { get; set; } = string.Empty;
    public string ContributionState { get; set; } = "Supported";
    public bool IsHumanVerifiedSupport { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Conversation-scoped state for Founder LEGEND AI. It contains no curriculum
/// authority and no response text; its sole identity boundary is one active
/// Founder actor plus one client-issued conversation UUID.
/// </summary>
public sealed class LegendFounderAiDiscourseConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FounderAgentUserId { get; set; } = string.Empty;
    public Guid ConversationId { get; set; }
    public int NextTurnSequence { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An immutable, bounded observation of a turn's governed meaning graph.
/// The JSON is structured node/relation data only: never prompt text, an
/// answer string, provider output, or a response lookup key.
/// </summary>
public sealed class LegendFounderAiDiscourseTurn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DiscourseConversationId { get; set; }
    public int SequenceNumber { get; set; }
    public string Role { get; set; } = string.Empty;
    public string MeaningGraphJson { get; set; } = string.Empty;
    /// <summary>
    /// Resolution outcomes expressed only as governed semantic identities and
    /// turn/node coordinates.  This never contains either participant's
    /// surface text, provider output, or an answer cache.
    /// </summary>
    public string ResolvedBindingsJson { get; set; } = "[]";
    public string AnalysisReasonCode { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Aggregate over explicit Founder-declared cross-turn reference semantics.
/// A rule becomes executable only through independent Founder support and is
/// intentionally separate from the conversation-state persistence that uses
/// it.
/// </summary>
public sealed class LegendLanguageDiscourseReferenceRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LanguageCode { get; set; } = string.Empty;
    public string RuleSignature { get; set; } = string.Empty;
    public string SelectorSemanticSignature { get; set; } = string.Empty;
    public string EntitySemanticDimension { get; set; } = string.Empty;
    public string ResolutionMode { get; set; } = string.Empty;
    public int? SelectionRank { get; set; }
    public string AllowedSourceRoles { get; set; } = string.Empty;
    public bool ReplacesActiveBinding { get; set; }
    public string MaturityState { get; set; } = "Observation";
    public int SupportCount { get; set; }
    public int ContradictionCount { get; set; }
    public int IndependentSourceCount { get; set; }
    public int HumanVerifiedSupportCount { get; set; }
    public bool IsProductionEligible { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Immutable provenance for one controlled graph declaration supporting a
/// discourse-reference rule.
/// </summary>
public sealed class LegendLanguageDiscourseReferenceRuleEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DiscourseReferenceRuleId { get; set; }
    public Guid CurriculumFamilyId { get; set; }
    public Guid CurriculumExampleId { get; set; }
    public Guid SelectorMeaningNodeId { get; set; }
    public string EvidenceIdentity { get; set; } = string.Empty;
    public string IndependentSourceIdentity { get; set; } = string.Empty;
    public string ContributionState { get; set; } = "Supported";
    public bool IsHumanVerifiedSupport { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The aggregate maturity of one explicit semantic relationship.  It is an
/// index over auditable node-to-node evidence, not an inferred grammar rule.
/// Phase 1 intentionally leaves IsProductionEligible false: later governed
/// lifecycle work must decide when an aggregate may participate in serving.
/// </summary>
public sealed class LegendLanguageMeaningRelation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LanguageCode { get; set; } = string.Empty;
    public string RelationSignature { get; set; } = string.Empty;
    public string RelationKind { get; set; } = string.Empty;
    public string SourceSemanticSignature { get; set; } = string.Empty;
    public string TargetSemanticSignature { get; set; } = string.Empty;
    public string? ClauseKey { get; set; }
    public string MaturityState { get; set; } = "Observation";
    public int SupportCount { get; set; }
    public int ContradictionCount { get; set; }
    public int IndependentSourceCount { get; set; }
    public int HumanVerifiedSupportCount { get; set; }
    public decimal Confidence { get; set; }
    public bool IsProductionEligible { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One provenance-preserving Founder observation of a directed relation in a
/// controlled utterance graph.  Different relation identities are retained
/// side-by-side rather than silently choosing one interpretation.
/// </summary>
public sealed class LegendLanguageMeaningRelationEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MeaningRelationId { get; set; }
    public Guid CurriculumFamilyId { get; set; }
    public Guid CurriculumExampleId { get; set; }
    public Guid SourceMeaningNodeId { get; set; }
    public Guid TargetMeaningNodeId { get; set; }
    public string EvidenceIdentity { get; set; } = string.Empty;
    public string IndependentSourceIdentity { get; set; } = string.Empty;
    public string ContributionState { get; set; } = "Supported";
    public bool IsHumanVerifiedSupport { get; set; }
    public string Provenance { get; set; } = "FounderApproved";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
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
    /// <summary>
    /// Ingestion-time canonicalization accounting. These nullable values are
    /// an operational receipt, not another corpus authority: historical
    /// submissions predate this visibility surface and must not be guessed
    /// from later canonical state.
    /// </summary>
    public int? NewCanonicalUnitCount { get; set; }
    public int? ReusedCanonicalUnitCount { get; set; }
    public int? QueuedCoverageCount { get; set; }
    /// <summary>
    /// The latest deployment-owned language-intelligence revision that has
    /// replayed this retained atomic source through its canonical authority.
    /// It is deliberately independent of the raw-text identity used for
    /// canonical submission deduplication.
    /// </summary>
    public int CompletedLanguageIntelligenceEvaluatorVersion { get; set; }
    /// <summary>
    /// A short durable claim for a bounded historical capability replay.
    /// If a worker stops before completion, the existing reconciliation loop
    /// may safely reclaim the submission after expiry.
    /// </summary>
    public DateTime? LanguageIntelligenceReevaluationLeaseExpiresUtc { get; set; }
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
    /// <summary>
    /// Successful production translations served by the currently promoted
    /// LEGEND neural model. Pair aggregate only; no production text or member
    /// identity is retained here.
    /// </summary>
    public long NeuralModelServeCount { get; set; }

    /// <summary>
    /// Production requests for which an active promoted LEGEND model existed
    /// but did not return a usable translation. This is a governed weakness
    /// signal only and never admits production text into training.
    /// </summary>
    public long NeuralModelFailureCount { get; set; }

    /// <summary>
    /// Requests ultimately served by an eligible reusable ProviderDerived
    /// observation after governed memory, composition, and neural inference.
    /// </summary>
    public long ProviderObservationReuseCount { get; set; }
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
    /// <summary>
    /// Characters served by the promoted translation capability. Founder-chat
    /// governed reasoning is a different capability and is never included.
    /// </summary>
    public long PromotedTranslationModelCharactersAvoided { get; set; }
    /// <summary>
    /// Characters served from exact provider-derived observations. This is
    /// provider reuse, not native LEGEND translation intelligence.
    /// </summary>
    public long ProviderObservationCharactersAvoided { get; set; }
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
    /// <summary>
    /// The highest evaluator version that entered with the historical
    /// single-cursor execution contract. Earlier phases retain that cursor;
    /// a ProviderObservations pass is atomically adopted into durable work at
    /// its persisted ordered boundary. It is execution compatibility state
    /// only; canonical language evidence remains unchanged.
    /// </summary>
    public int CursorReplayCompatibilityEvaluatorVersion { get; set; }
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
    /// <summary>
    /// The deployment-owned evaluator revision this accepted Founder manifest
    /// is currently being processed against.  It is orchestration state only;
    /// the payload and canonical curriculum remain the sole language authority.
    /// </summary>
    public int TargetLanguageIntelligenceEvaluatorVersion { get; set; }
    /// <summary>
    /// The most recent evaluator revision for which every family in this
    /// retained manifest completed through the canonical curriculum service.
    /// A lower value makes the completed work eligible for bounded replay.
    /// </summary>
    public int CompletedLanguageIntelligenceEvaluatorVersion { get; set; }
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
/// Durable execution state for one bounded historical language-intelligence
/// reevaluation unit. It stores no curriculum, source text, target text, or
/// derived evidence: every semantic decision continues through the existing
/// canonical evaluator authorities.
/// </summary>
public sealed class LegendHistoricalReevaluationWorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int EvaluatorVersion { get; set; }
    public string Phase { get; set; } = string.Empty;
    /// <summary>Canonical work or a bounded phase-seeding control item.</summary>
    public string WorkKind { get; set; } = "Canonical";
    /// <summary>
    /// A deterministic operational identity only. It never contains source
    /// or target text.
    /// </summary>
    public string WorkIdentity { get; set; } = string.Empty;
    /// <summary>
    /// Stable canonical identifier when this is a canonical evaluator unit.
    /// Null is reserved for the phase-seeding control item.
    /// </summary>
    public Guid? SubjectId { get; set; }
    /// <summary>
    /// A narrow stable scope for the subject (for example a language code).
    /// It is part of work identity, not language evidence.
    /// </summary>
    public string SubjectScope { get; set; } = string.Empty;
    /// <summary>
    /// The smallest audited mutable lane. A filtered unique index permits at
    /// most one active lease per evaluator/phase/dependency lane.
    /// </summary>
    public string DependencyIdentity { get; set; } = string.Empty;
    /// <summary>
    /// Optional cross-phase ownership fence for a canonical artifact that can
    /// be mutated by more than one durable work phase.  Unlike
    /// <see cref="DependencyIdentity"/>, this lane is not phase-scoped: when
    /// present, one evaluator may hold at most one active lease for it across
    /// the entire durable work authority.  It contains only a stable canonical
    /// identity, never Founder text or a response surface.
    /// </summary>
    public string? CanonicalMutationLane { get; set; }
    public string ProcessingState { get; set; } = "Pending";
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
}

/// <summary>
/// A deployment-declared semantic derivation contract.  It describes how an
/// existing derived artifact class was produced, not curriculum content or a
/// second evaluator.  The single runtime-policy and durable-work lifecycle
/// compare these durable declarations to determine the earliest genuinely
/// affected historical layer on a later deployment.
/// </summary>
public sealed class LegendLanguageDerivationContract
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DerivationKind { get; set; } = string.Empty;
    public string ContractVersion { get; set; } = string.Empty;
    public string ContractIdentity { get; set; } = string.Empty;
    public string EarliestPhase { get; set; } = string.Empty;
    public bool RequiresHistoricalWork { get; set; }
    public int IntroducedEvaluatorVersion { get; set; }
    public string State { get; set; } = "Current";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A durable edge in the evaluator's derivation contract graph.  Both ends
/// are contract identities, never text, family keys, prompt strings, or
/// answer surfaces.  It makes invalidation transitive without introducing an
/// additional learning or replay authority.
/// </summary>
public sealed class LegendLanguageDerivationContractDependency
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DependentContractId { get; set; }
    public string DependencyDerivationKind { get; set; } = string.Empty;
    public string DependencyContractIdentity { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A compact provenance edge from one retained canonical source identity to
/// one derived identity under an immutable derivation contract. It contains
/// identifiers and semantic-version markers only: never Founder surface text,
/// a prompt, a provider payload, or a response. The existing evidence rows
/// remain the semantic authority; this table makes their contract dependency
/// explicit so a future evaluator can invalidate precisely the affected
/// downstream projection rather than treating its evaluator number as a
/// reason to rebuild the corpus.
/// </summary>
public sealed class LegendLanguageDerivationArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ArtifactKind { get; set; } = string.Empty;
    /// <summary>Canonical identity of the resulting derived artifact.</summary>
    public string ResultArtifactIdentity { get; set; } = string.Empty;
    /// <summary>Canonical upstream evidence/node/relationship identity.</summary>
    public string SourceDependencyIdentity { get; set; } = string.Empty;
    /// <summary>
    /// The semantic version of the source identity where an upstream semantic
    /// declaration—not merely evaluator code—has an independent revision.
    /// </summary>
    public string SourceDependencySemanticVersion { get; set; } = string.Empty;
    public string DerivationContractIdentity { get; set; } = string.Empty;
    /// <summary>Current, Stale, or Superseded. Canonical evidence is never deleted.</summary>
    public string State { get; set; } = "Current";
    public DateTime? SupersededUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An observable durable plan for one evaluator convergence.  Counts are
/// derived from canonical identities at planning time; this row contains no
/// evidence payload and cannot alter curriculum maturity or eligibility.
/// </summary>
public sealed class LegendLanguageDerivationConvergence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int TargetEvaluatorVersion { get; set; }
    public int BaselineEvaluatorVersion { get; set; }
    public string State { get; set; } = "Queued";
    public string? EarliestAffectedPhase { get; set; }
    public int ChangedContractCount { get; set; }
    public int ReusedContractCount { get; set; }
    public long ExistingCanonicalArtifactCount { get; set; }
    public long ReusedCanonicalArtifactCount { get; set; }
    public long AffectedCanonicalArtifactCount { get; set; }
    /// <summary>
    /// A completed evaluator that predates the dependency ledger receives a
    /// bounded metadata-only inventory before any semantic frontier runs.
    /// This does not mark canonical evidence stale or re-evaluate it.
    /// </summary>
    public bool RequiresDependencyInventory { get; set; }
    public long DependencyInventoryWorkItemCount { get; set; }
    public long PlannedWorkItemCount { get; set; }
    public string? BlockingDependencyIdentity { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
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

/// <summary>
/// The single durable kill switch for Founder-governed software remediation.
/// It can only remove capability; repository, Key Vault, and GitHub App
/// configuration remain deployment-owned and are never copied here.
/// </summary>
public sealed class FounderSoftwareRemediationAuthorityState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = "Global";
    public bool IsRevoked { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public string? RevokedByUserId { get; set; }
    public DateTime? LastVerifiedUtc { get; set; }
    public bool ProtectedProductionBranchVerified { get; set; }
    public bool SecurityCiVerified { get; set; }
    public bool RepairPreparationVerified { get; set; }
    public string? LastVerificationCode { get; set; }
    public string? LastVerificationDetail { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Immutable declaration of the evidence rubric used to evaluate LEGEND's
/// capabilities. A new contract is introduced for a material rubric change;
/// old snapshots retain their original contract identity.
/// </summary>
public sealed class LegendIntelligenceEvaluationContract
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ContractKey { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ContractIdentity { get; set; } = string.Empty;
    public string State { get; set; } = "Current";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SupersededUtc { get; set; }
}

/// <summary>
/// A source-authority-owned signal for one declared intelligence domain and
/// rubric factor. It records an evidence reference, not curriculum text or a
/// provider answer, so scores cannot be inflated from row volume alone.
/// </summary>
public sealed class LegendIntelligenceEvaluationSignal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContractId { get; set; }
    public string DomainKey { get; set; } = string.Empty;
    public string MetricKey { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string EvidenceAuthority { get; set; } = string.Empty;
    public string EvidenceReference { get; set; } = string.Empty;
    public string State { get; set; } = "Current";
    public DateTime MeasuredUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Historical three-lens evaluation snapshot. The evidence result, LEGEND
/// self-assessment, and OpenAI assessment remain distinct values; no blended
/// score is persisted or displayed as an objective result.
/// </summary>
public sealed class LegendIntelligenceEvaluationSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContractId { get; set; }
    public Guid? PreviousSnapshotId { get; set; }
    public string EvidenceSetIdentity { get; set; } = string.Empty;
    public string State { get; set; } = "EvidenceOnly";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LegendIntelligenceEvaluationDomainSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SnapshotId { get; set; }
    public string DomainKey { get; set; } = string.Empty;
    public decimal? EvidenceScore { get; set; }
    public decimal? LegendSelfAssessment { get; set; }
    public decimal? OpenAiExternalAssessment { get; set; }
    public long EvidenceVolume { get; set; }
    public long ProductionEligibleEvidenceCount { get; set; }
    public decimal? NativeSuccessRate { get; set; }
    public decimal? HeldOutResult { get; set; }
    public decimal? TransferResult { get; set; }
    public decimal? ContradictionRate { get; set; }
    public string EvidenceReferencesJson { get; set; } = "[]";
    public string KnownWeaknessesJson { get; set; } = "[]";
    public string OpenGapsJson { get; set; } = "[]";
}

/// <summary>
/// A separately recorded model perspective over one immutable evidence
/// snapshot. The OpenAI assessor is never given LEGEND's self-score; both
/// perspectives must cite references already present in the snapshot.
/// </summary>
public sealed class LegendIntelligenceEvaluationPerspective
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SnapshotId { get; set; }
    public string PerspectiveKind { get; set; } = string.Empty;
    public string State { get; set; } = "Pending";
    public string AssessmentJson { get; set; } = "{}";
    public string EvidenceReferencesJson { get; set; } = "[]";
    public DateTime? AssessedUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Durable per-pair lineage for one governed model promotion.
///
/// LegendLanguagePair.ActiveModelVersion remains the only active-model
/// authority. This row records only the predecessor and promoted version
/// required to make rollback auditable and exact for a multilingual run.
/// </summary>
public sealed class LegendConnectModelPromotionPair
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ModelTrainingRunId { get; set; }

    public string PairKey { get; set; } = string.Empty;

    public string? PreviousActiveModelVersion { get; set; }

    public string PromotedModelVersion { get; set; } = string.Empty;

    public DateTime PromotedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? RolledBackUtc { get; set; }
}
