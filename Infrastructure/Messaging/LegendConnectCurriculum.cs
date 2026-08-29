using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Data.SqlTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Infrastructure.Messaging;

/// <summary>
/// Generic, server-side curriculum authority. Founder-authored controlled
/// examples describe semantic changes; every language derives its structural
/// observations only by comparing canonical examples in that same language.
/// This service owns neither a corpus nor a provider: it extends the existing
/// corpus candidate and Azure-expansion pipeline.
/// </summary>
internal interface ILegendConnectStructuralCompositionGate
{
    Task<LegendContextualTranslationSuggestion?> TryComposeAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default);

    Task<LegendShadowSourceUnderstanding> AnalyzeShadowSourceSemanticsAsync(
        string sourceLanguageCode,
        string text,
        CancellationToken cancellationToken = default);

    Task<LegendShadowCompositionCapability> EvaluateShadowCompositionAsync(
        LegendShadowCompositionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An explicitly described component of a bounded shadow-composition request.
/// Callers supply the known semantic identity and its proposed realized span;
/// the gate verifies both against canonical Founder-approved evidence and does
/// not infer either from an unseen sentence.
/// </summary>
internal sealed record LegendShadowCompositionComponent(
    string Dimension,
    string Value,
    string SurfaceForm,
    int StartTokenIndex,
    int TokenLength = 1);

/// <summary>
/// One previously learned controlled variation that the request needs. The
/// exact proposition remains authoritative; this type only identifies the
/// existing dimension/value pair to evaluate.
/// </summary>
internal sealed record LegendShadowCompositionRelationshipRequirement(
    string Dimension,
    string FirstValue,
    string SecondValue);

/// <summary>
/// Demand-driven, non-persistent request to assess a proposed target-language
/// construction. It is not a sentence generator and is never used by the
/// production translation router.
/// </summary>
internal sealed record LegendShadowCompositionRequest(
    string SourceLanguageCode,
    string TargetLanguageCode,
    string ProposedTargetText,
    IReadOnlyList<LegendShadowCompositionComponent> Components,
    IReadOnlyList<LegendShadowCompositionRelationshipRequirement> RequiredRelationships);

/// <summary>
/// Read-only composition capability result. Supported and validated states are
/// shadow-only observations; they never grant production eligibility.
/// </summary>
internal sealed record LegendShadowCompositionCapability(
    string State,
    bool IsExactObserved,
    bool IsProductionEligible,
    IReadOnlyList<string> Reasons)
{
    internal const string ExactObserved = "ExactObserved";
    internal const string InsufficientEvidence = "InsufficientEvidence";
    internal const string Contradicted = "Contradicted";
    internal const string SupportedForShadowEvaluation = "SupportedForShadowEvaluation";
    internal const string ValidatedForShadowEvaluation = "ValidatedForShadowEvaluation";
}

/// <summary>
/// One semantic component independently recognized in an unseen source
/// sentence from active Founder-approved compositional evidence.
/// </summary>
internal sealed record LegendShadowSourceSemanticComponent(
    string Dimension,
    string Value,
    string SurfaceForm,
    int StartTokenIndex,
    int TokenLength,
    string SemanticSignature);

/// <summary>
/// Read-only source-understanding result. This cannot formulate a target,
/// persist learned state, authorize production serving, or consult Azure.
/// Ambiguous or incomplete semantic coverage fails closed.
/// </summary>
internal sealed record LegendShadowSourceUnderstanding(
    string State,
    bool IsProductionEligible,
    IReadOnlyList<LegendShadowSourceSemanticComponent> Components,
    IReadOnlyList<string> Reasons)
{
    internal const string SupportedForShadowEvaluation = "SupportedForShadowEvaluation";
    internal const string InsufficientEvidence = "InsufficientEvidence";
    internal const string Ambiguous = "Ambiguous";
}

/// <summary>
/// The single read-only result of governed semantic-transition evaluation.
/// It carries no model result, provider output, or response lookup identity.
/// </summary>
internal sealed record LegendSemanticTransitionInference(
    string State,
    string? RealizedText,
    int EvidenceCount,
    IReadOnlyList<string> Reasons)
{
    internal const string Supported = "Supported";
    internal const string InsufficientEvidence = "InsufficientEvidence";
    internal const string Ambiguous = "Ambiguous";
    internal const string Contradicted = "Contradicted";
}

/// <summary>
/// One target realization resolved from existing verified directional
/// evidence. Its observed target position comes from target-language evidence,
/// never from source-language component order.
/// </summary>
internal sealed record LegendShadowTargetRealization(
    string Dimension,
    string Value,
    string SemanticSignature,
    string SurfaceForm,
    int ObservedTargetStartTokenIndex,
    int ObservedTargetTokenLength);

/// <summary>
/// Read-only Phase 4B formulation result. Successful shadow formulation still
/// cannot be served by TryComposeAsync.
/// </summary>
internal sealed record LegendShadowTargetFormulation(
    string State,
    string? Text,
    bool IsProductionEligible,
    IReadOnlyList<LegendShadowTargetRealization> Realizations,
    IReadOnlyList<string> Reasons)
{
    internal const string InsufficientEvidence = "InsufficientEvidence";
    internal const string Ambiguous = "Ambiguous";
    internal const string Contradicted = "Contradicted";
    internal const string SupportedForShadowEvaluation =
        "SupportedForShadowEvaluation";
}

internal sealed class LegendConnectCurriculumService : ILegendConnectStructuralCompositionGate
{
    // One canonical evidence hierarchy is consumed at every native stage.
    // Broad means active, explicit, human-verified Founder evidence with no
    // contradiction. HigherStandard means that same evidence also meets the
    // existing independent three-source production-eligibility contract.
    // These are selection ranks, not alternate stores or serving paths.
    private const int BroadGovernedEvidenceStandard = 1;
    private const int HigherGovernedEvidenceStandard = 2;
    private const int MaximumExamplesPerBatch = 100;
    // This value participates in the durable relationship identity. It is
    // advanced only when the canonical grouping meaning changes, allowing the
    // existing bounded evaluator replay to supersede its prior derived row
    // without rewriting or conflating historical evidence.
    private const string ReusableStructuralRelationshipIdentityVersion = "controlled-anchor-order-v4";
    // Founder-controlled equivalent surface forms may prove that several
    // independently anchored semantic components are jointly realized by one
    // fused lexical span. This identity versions that derivation without
    // changing the meaning of historical token coordinates.
    private const string SourceCoRealizationDerivationVersion =
        "founder-source-corealization-v1";
    // Historical controlled families predate explicit @ground declarations.
    // This versioned identity records only the narrow projection authorized
    // by an already production-eligible source frame plus one exact,
    // full-example Founder control. It is deliberately distinct from the
    // newer explicit-grounding identity so a future evaluator revision can
    // supersede only this derived evidence without rewriting Founder rows.
    private const string HistoricalSourceFrameProjectionDerivationVersion =
        "founder-source-frame-projection-v2";
    // Candidate identities retain the evidence interpretation that produced
    // them. Advance only with the existing evaluator version when the
    // canonical contrast meaning materially changes.
    private const string TargetRealizationCandidateDerivationVersion = "target-contrast-contextual-v4";
    private readonly MasterAppDbContext _db;
    private readonly ILegendLanguageRegistry _languages;
    private readonly LegendConnectCorpusService _corpus;
    private readonly ILegendConnectOperationalEventWriter? _operations;

    public LegendConnectCurriculumService(
        MasterAppDbContext db,
        ILegendLanguageRegistry languages,
        LegendConnectCorpusService corpus,
        ILegendConnectOperationalEventWriter? operations = null)
    {
        _db = db;
        _languages = languages;
        _corpus = corpus;
        _operations = operations;
    }

    /// <summary>
    /// Phase-5 autonomous admission of one Phase-4 SystemValidatedMachine
    /// proposal. The existing proposal row owns the durable admission lease;
    /// this service remains the sole curriculum authority.
    /// </summary>
    internal async Task<bool>
        ProcessOneSystemValidatedMachineProposalAsync(
            CancellationToken cancellationToken = default)
    {
        const int maximumAttempts = 3;

        var proposal = await TryClaimSystemValidatedMachineProposalAsync(
            maximumAttempts,
            cancellationToken);

        if (proposal is null)
            return false;

        try
        {
            var failureCode =
                await AdmitSystemValidatedMachineProposalAsync(
                    proposal,
                    cancellationToken);

            if (failureCode is not null)
            {
                proposal.ValidationState =
                    "CurriculumAdmissionRejected";
                proposal.CurriculumAdmissionFailureCode =
                    failureCode;
                proposal.CurriculumAdmissionLeaseExpiresUtc =
                    null;
                proposal.CurriculumAdmittedUtc =
                    DateTime.UtcNow;
                proposal.UpdatedUtc =
                    DateTime.UtcNow;

                await _db.SaveChangesAsync(cancellationToken);
                return true;
            }

            proposal.ValidationState = "CurriculumAdmitted";
            proposal.CurriculumAdmissionFailureCode = null;
            proposal.CurriculumAdmissionLeaseExpiresUtc = null;
            proposal.CurriculumAdmittedUtc = DateTime.UtcNow;
            proposal.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            proposal.CurriculumAdmissionFailureCode =
                "machine_curriculum_admission_failed";

            if (proposal.CurriculumAdmissionAttemptCount >=
                maximumAttempts)
            {
                proposal.ValidationState =
                    "CurriculumAdmissionFailed";
                proposal.CurriculumAdmissionLeaseExpiresUtc =
                    null;
                proposal.CurriculumAdmittedUtc =
                    DateTime.UtcNow;
            }
            else
            {
                proposal.ValidationState =
                    "CurriculumAdmissionProcessing";
                proposal.CurriculumAdmissionLeaseExpiresUtc =
                    DateTime.UtcNow.AddMinutes(
                        Math.Min(
                            30,
                            Math.Max(
                                5,
                                proposal
                                    .CurriculumAdmissionAttemptCount *
                                5)));
            }

            proposal.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            return true;
        }
    }

    private async Task<LegendLanguageTeacherProposal?>
        TryClaimSystemValidatedMachineProposalAsync(
            int maximumAttempts,
            CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(10);

        var proposalId =
            await _db.Set<LegendLanguageTeacherProposal>()
                .AsNoTracking()
                .Where(item =>
                    item.Provenance ==
                        LegendConnectKnowledgeProvenance
                            .SystemValidatedMachine &&
                    item.CurriculumAdmissionAttemptCount <
                        maximumAttempts &&
                    (
                        item.ValidationState ==
                            "SystemValidated" ||
                        (
                            item.ValidationState ==
                                "CurriculumAdmissionProcessing" &&
                            item.CurriculumAdmissionLeaseExpiresUtc !=
                                null &&
                            item.CurriculumAdmissionLeaseExpiresUtc <
                                now
                        )
                    ))
                .OrderBy(item => item.CreatedUtc)
                .Select(item => (Guid?)item.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (proposalId is null)
            return null;

        if (!_db.Database.IsRelational())
        {
            var proposal =
                await _db.Set<LegendLanguageTeacherProposal>()
                    .SingleOrDefaultAsync(
                        item =>
                            item.Id == proposalId.Value &&
                            item.Provenance ==
                                LegendConnectKnowledgeProvenance
                                    .SystemValidatedMachine &&
                            item.CurriculumAdmissionAttemptCount <
                                maximumAttempts &&
                            (
                                item.ValidationState ==
                                    "SystemValidated" ||
                                (
                                    item.ValidationState ==
                                        "CurriculumAdmissionProcessing" &&
                                    item.CurriculumAdmissionLeaseExpiresUtc !=
                                        null &&
                                    item.CurriculumAdmissionLeaseExpiresUtc <
                                        now
                                )
                            ),
                        cancellationToken);

            if (proposal is null)
                return null;

            proposal.ValidationState =
                "CurriculumAdmissionProcessing";
            proposal.CurriculumAdmissionAttemptCount++;
            proposal.CurriculumAdmissionLeaseExpiresUtc =
                expires;
            proposal.UpdatedUtc = now;

            await _db.SaveChangesAsync(cancellationToken);
            return proposal;
        }

        var claimed =
            await _db.Set<LegendLanguageTeacherProposal>()
                .Where(item =>
                    item.Id == proposalId.Value &&
                    item.Provenance ==
                        LegendConnectKnowledgeProvenance
                            .SystemValidatedMachine &&
                    item.CurriculumAdmissionAttemptCount <
                        maximumAttempts &&
                    (
                        item.ValidationState ==
                            "SystemValidated" ||
                        (
                            item.ValidationState ==
                                "CurriculumAdmissionProcessing" &&
                            item.CurriculumAdmissionLeaseExpiresUtc !=
                                null &&
                            item.CurriculumAdmissionLeaseExpiresUtc <
                                now
                        )
                    ))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            item => item.ValidationState,
                            "CurriculumAdmissionProcessing")
                        .SetProperty(
                            item =>
                                item.CurriculumAdmissionAttemptCount,
                            item =>
                                item.CurriculumAdmissionAttemptCount +
                                1)
                        .SetProperty(
                            item =>
                                item.CurriculumAdmissionLeaseExpiresUtc,
                            expires)
                        .SetProperty(
                            item => item.UpdatedUtc,
                            now),
                    cancellationToken);

        return claimed == 1
            ? await _db.Set<LegendLanguageTeacherProposal>()
                .SingleAsync(
                    item => item.Id == proposalId.Value,
                    cancellationToken)
            : null;
    }

    private async Task<string?>
        AdmitSystemValidatedMachineProposalAsync(
            LegendLanguageTeacherProposal proposal,
            CancellationToken cancellationToken)
    {
        if (!string.Equals(
                proposal.Provenance,
                LegendConnectKnowledgeProvenance
                    .SystemValidatedMachine,
                StringComparison.Ordinal))
        {
            return "machine_proposal_provenance_invalid";
        }

        LegendLanguageTeacherFamilyProposal? machineFamily;

        try
        {
            machineFamily =
                System.Text.Json.JsonSerializer
                    .Deserialize<
                        LegendLanguageTeacherFamilyProposal>(
                        proposal.ProposalPayloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            machineFamily = null;
        }

        if (machineFamily is null ||
            machineFamily.Examples is null ||
            machineFamily.Examples.Count is < 2 or > 8)
        {
            return "machine_curriculum_payload_invalid";
        }

        var semanticTransitions = NormalizeSemanticTransitions(
            machineFamily.SemanticTransitions);
        if (semanticTransitions is null)
            return "machine_curriculum_semantic_transition_invalid";
        var machineCandidate = await _db.Set<LegendCorpusCandidate>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == proposal.CorpusCandidateId,
                cancellationToken);
        var isConversationMachineProposal = machineCandidate is not null &&
            string.Equals(machineCandidate.Provenance, "MachineConversation", StringComparison.Ordinal) &&
            string.Equals(machineCandidate.ProcessingState, "ConversationProposal", StringComparison.Ordinal);
        if (isConversationMachineProposal && semanticTransitions.Count == 0)
            return "machine_conversation_semantic_transition_required";

        var familyKey =
            NormalizeFamilyKey(machineFamily.FamilyKey);

        var semanticCategory =
            NormalizeOptional(
                machineFamily.SemanticCategory,
                120);

        if (familyKey is null ||
            !string.Equals(
                familyKey,
                NormalizeFamilyKey(proposal.FamilyKey),
                StringComparison.Ordinal))
        {
            return "machine_curriculum_family_invalid";
        }

        var sourceLanguage =
            await _languages
                .NormalizeEnabledTranslationLanguageAsync(
                    proposal.SourceLanguageCode,
                    cancellationToken);

        if (sourceLanguage is null)
            return "machine_curriculum_source_language_unavailable";

        var prepared =
            new List<(
                LegendLanguageTeacherExampleProposal Proposal,
                string SourceText,
                IReadOnlyDictionary<string, string> Variations,
                LegendShadowSourceUnderstanding Understanding)>(
                machineFamily.Examples.Count);

        var sourceHashes =
            new HashSet<string>(StringComparer.Ordinal);

        foreach (var example in machineFamily.Examples)
        {
            var sourceText =
                LegendLanguageIdentity.NormalizeText(
                    example.SourceText);

            if (string.IsNullOrWhiteSpace(sourceText) ||
                sourceText.Length > 10_000 ||
                !sourceHashes.Add(
                    LegendLanguageIdentity.TextHash(sourceText)) ||
                example.Components is null ||
                example.Components.Count is < 1 or > 16)
            {
                return "machine_curriculum_example_invalid";
            }

            var variations =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);

            foreach (var component in example.Components)
            {
                var dimension =
                    NormalizeDimension(component.Dimension);

                var value =
                    NormalizeOptional(component.Value, 160);

                var surface =
                    LegendLanguageIdentity.NormalizeText(
                        component.SurfaceForm);

                if (dimension is null ||
                    value is null ||
                    string.IsNullOrWhiteSpace(surface) ||
                    !variations.TryAdd(dimension, value))
                {
                    return "machine_curriculum_component_invalid";
                }
            }

            // Recheck the existing canonical semantic authority immediately
            // before persistence. Phase-4 validation cannot be silently
            // inherited after the evidence graph changes.
            var understanding =
                await AnalyzeShadowSourceSemanticsAsync(
                    sourceLanguage,
                    sourceText,
                    cancellationToken);

            if (!string.Equals(
                    understanding.State,
                    LegendShadowSourceUnderstanding
                        .SupportedForShadowEvaluation,
                    StringComparison.Ordinal))
            {
                return "machine_curriculum_semantics_no_longer_supported";
            }

            var proposed =
                example.Components
                    .Select(item =>
                        (
                            Dimension:
                                item.Dimension.Trim()
                                    .ToLowerInvariant(),
                            Value:
                                item.Value.Trim()
                                    .ToLowerInvariant(),
                            Surface:
                                LegendLanguageIdentity
                                    .NormalizeText(
                                        item.SurfaceForm)
                                    .ToLowerInvariant()))
                    .OrderBy(item => item.Dimension)
                    .ThenBy(item => item.Value)
                    .ThenBy(item => item.Surface)
                    .ToArray();

            var established =
                understanding.Components
                    .Select(item =>
                        (
                            Dimension:
                                item.Dimension.Trim()
                                    .ToLowerInvariant(),
                            Value:
                                item.Value.Trim()
                                    .ToLowerInvariant(),
                            Surface:
                                LegendLanguageIdentity
                                    .NormalizeText(
                                        item.SurfaceForm)
                                    .ToLowerInvariant()))
                    .OrderBy(item => item.Dimension)
                    .ThenBy(item => item.Value)
                    .ThenBy(item => item.Surface)
                    .ToArray();

            if (!proposed.SequenceEqual(established))
                return "machine_curriculum_semantic_drift";

            prepared.Add(
                (
                    example,
                    sourceText,
                    variations,
                    understanding));
        }

        foreach (var transition in semanticTransitions)
        {
            var sources = prepared
                .Where(item => TryBindSemanticFrame(transition.Source, item.Variations, out _))
                .ToArray();
            var results = prepared
                .Where(item => TryBindSemanticFrame(transition.Result, item.Variations, out _))
                .ToArray();
            var hasCompatibleDistinctEndpoints = sources.Any(source =>
                results.Any(result =>
                    !ReferenceEquals(source.Proposal, result.Proposal) &&
                    TryBindSemanticFrame(transition.Source, source.Variations, out var sourceBindings) &&
                    TryBindSemanticFrame(transition.Result, result.Variations, out var resultBindings) &&
                    BindingsAreCompatible(sourceBindings, resultBindings)));
            if (!hasCompatibleDistinctEndpoints)
                return "machine_curriculum_semantic_transition_unbound";
        }

        var family = await LegendConnectCanonicalCurriculumPersistence.AdmitFamilyAsync(
            _db,
            familyKey,
            semanticCategory,
            LegendConnectKnowledgeProvenance.SystemValidatedMachine,
            cancellationToken);
        var existingFamily = _db.Entry(family).State == EntityState.Added
            ? null
            : family;

        if (existingFamily is not null &&
            !string.IsNullOrWhiteSpace(
                existingFamily.SemanticCategory) &&
            !string.IsNullOrWhiteSpace(
                semanticCategory) &&
            !string.Equals(
                existingFamily.SemanticCategory,
                semanticCategory,
                StringComparison.OrdinalIgnoreCase))
        {
            // Existing family classification wins, particularly Founder.
            return "machine_curriculum_family_category_conflict";
        }

        if (existingFamily is not null &&
            existingFamily.Provenance !=
                LegendConnectKnowledgeProvenance
                    .FounderApproved &&
            existingFamily.Provenance !=
                LegendConnectKnowledgeProvenance
                    .SystemValidatedMachine)
        {
            return "machine_curriculum_family_provenance_conflict";
        }

        if (existingFamily is null)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        else if (
            family.Provenance ==
                LegendConnectKnowledgeProvenance
                    .SystemValidatedMachine &&
            family.SemanticCategory is null &&
            semanticCategory is not null)
        {
            family.SemanticCategory = semanticCategory;
            family.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var sourceExamples =
            new List<(
                LegendCurriculumExample Example,
                LegendLanguageTextUnit Unit,
                LegendLanguageTeacherExampleProposal Proposal,
                LegendShadowSourceUnderstanding Understanding)>();

        foreach (var item in prepared)
        {
            var submitted =
                await _corpus
                    .SubmitSystemValidatedMachineKnowledgeAsync(
                        sourceLanguage,
                        item.SourceText,
                        string.IsNullOrWhiteSpace(
                            item.Proposal.TargetText)
                            ? null
                            : proposal.TargetLanguageCode,
                        item.Proposal.TargetText,
                        family.SemanticCategory,
                        cancellationToken);

            if (!submitted.Succeeded ||
                submitted.SourceTextUnitId is null)
            {
                return submitted.ErrorCode ??
                    "machine_curriculum_corpus_admission_failed";
            }

            var sourceUnit =
                await _db.Set<LegendLanguageTextUnit>()
                    .SingleAsync(
                        unit =>
                            unit.Id ==
                                submitted.SourceTextUnitId.Value,
                        cancellationToken);

            var sourceExample =
                await GetOrCreateExampleAsync(
                    family,
                    sourceUnit,
                    sourceLanguage,
                    derivedFromCurriculumExampleId: null,
                    provenanceOverride:
                        LegendConnectKnowledgeProvenance
                            .SystemValidatedMachine,
                    cancellationToken);

            await EnsureVariationsAsync(
                sourceExample,
                item.Variations,
                cancellationToken);

            sourceExamples.Add(
                (
                    sourceExample,
                    sourceUnit,
                    item.Proposal,
                    item.Understanding));
        }

        await _db.SaveChangesAsync(cancellationToken);

        var lexicalInputs =
            sourceExamples
                .DistinctBy(item => item.Unit.Id)
                .Select(item =>
                    new AtomicInput(
                        item.Unit,
                        "StructuredExample",
                        null,
                        null,
                        null))
                .ToList();

        await EnsureLanguageLexicalObservationsAsync(
            lexicalInputs,
            sourceLanguage,
            cancellationToken);

        foreach (var item in sourceExamples)
        {
            await AttachSystemValidatedMachineSemanticAnchorsAsync(
                family,
                item.Example,
                item.Proposal,
                item.Understanding,
                sourceLanguage,
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Existing same-language structural evaluator now sees the machine
        // curriculum as auditable lower-tier evidence. It cannot become
        // HumanVerified merely by repetition.
        await AnalyzeFamilyLanguageAsync(
            family.Id,
            sourceLanguage,
            pairKey: null,
            cancellationToken);

        if (semanticTransitions.Count > 0)
        {
            var machineVariations = sourceExamples.ToDictionary(
                item => item.Example.Id,
                item => (IReadOnlyDictionary<string, string>)item.Proposal.Components
                    .ToDictionary(
                        component => component.Dimension.Trim().ToLowerInvariant(),
                        component => component.Value.Trim(),
                        StringComparer.Ordinal),
                EqualityComparer<Guid>.Default);
            await PersistGovernedSemanticTransitionEvidenceAsync(
                family,
                sourceExamples.Select(item => item.Example).ToArray(),
                semanticTransitions,
                sourceLanguage,
                machineVariations,
                knownNewCurriculumExampleIds: null,
                provenance: LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                isHumanVerifiedSupport: false,
                cancellationToken: cancellationToken);
        }

        foreach (var item in sourceExamples)
        {
            // Attach already-existing canonical alignments first.
            await AttachExistingExpansionsAsync(
                item.Example,
                item.Unit,
                cancellationToken);

            // Then queue only missing directions through the existing provider
            // worker/capacity/corpus authority.
            await _corpus
                .EnsureSystemValidatedMachineSeedCandidatesAsync(
                    item.Unit,
                    family.Id,
                    item.Example.Id,
                    cancellationToken);
        }

        return null;
    }

    private async Task
        AttachSystemValidatedMachineSemanticAnchorsAsync(
            LegendCurriculumFamily family,
            LegendCurriculumExample example,
            LegendLanguageTeacherExampleProposal proposal,
            LegendShadowSourceUnderstanding understanding,
            string languageCode,
            CancellationToken cancellationToken)
    {
        var occurrences =
            await _db.Set<LegendLanguageLexicalOccurrence>()
                .Where(item =>
                    item.TextUnitId == example.TextUnitId &&
                    item.SupersededUtc == null)
                .ToDictionaryAsync(
                    item => item.TokenIndex,
                    cancellationToken);

        foreach (var proposed in proposal.Components)
        {
            var match =
                understanding.Components.Single(item =>
                    string.Equals(
                        item.Dimension,
                        proposed.Dimension,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        item.Value,
                        proposed.Value,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        LegendLanguageIdentity.NormalizeText(
                            item.SurfaceForm),
                        LegendLanguageIdentity.NormalizeText(
                            proposed.SurfaceForm),
                        StringComparison.OrdinalIgnoreCase));

            occurrences.TryGetValue(
                match.StartTokenIndex,
                out var occurrence);

            var signature =
                AnchorSignature(
                    example.Id,
                    occurrence?.LexemeId,
                    match.Dimension,
                    match.Value);

            var candidate = new LegendLanguageCompositionalAnchor
            {
                Id = Guid.NewGuid(),
                LanguageCode = languageCode,
                PairKey = null,
                TextUnitId = example.TextUnitId,
                LexemeId = occurrence?.LexemeId,
                ComponentStartTokenIndex = match.StartTokenIndex,
                ComponentLength = match.TokenLength,
                CurriculumFamilyId = family.Id,
                CurriculumExampleId = example.Id,
                Dimension = match.Dimension,
                Value = match.Value,
                SemanticSignature = match.SemanticSignature,
                AnchorSignature = signature,
                Provenance = LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                CreatedUtc = DateTime.UtcNow
            };
            var admitted = await LegendConnectCanonicalCurriculumPersistence.AdmitCompositionalAnchorAsync(
                _db,
                candidate,
                cancellationToken);
            if (!ReferenceEquals(admitted, candidate))
            {
                // Never alter stronger Founder evidence.
                continue;
            }
        }
    }

    /// <summary>
    /// Preflights one family using the exact same authority and normalization
    /// rules used by persistence. This method performs no mutation. It exists
    /// so a multi-family Founder manifest can be validated completely before
    /// the first family is written.
    /// </summary>
    internal async Task<LegendConnectCurriculumSubmissionResult?> PreflightFounderEnglishBatchAsync(
        LegendConnectCurriculumBatchSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var familyKey = NormalizeFamilyKey(submission.FamilyKey);
        var examples = NormalizeExamples(submission.Examples);
        var semanticTransitions = NormalizeSemanticTransitions(submission.SemanticTransitions);
        var semanticSpanGroundings = NormalizeSemanticSpanGroundings(submission.SemanticSpanGroundings);
        var english = await _languages.NormalizeEnabledTranslationLanguageAsync("en", cancellationToken);
        if (english is null || !string.Equals(english, "en", StringComparison.OrdinalIgnoreCase))
            return Rejected("english_training_unavailable", "English must be an enabled direct Founder training language.", familyKey);
        if (familyKey is null)
            return Rejected("invalid_curriculum_family", "Use a concise semantic family key such as conversation.greeting.basic.", null);
        if (examples is null)
            return Rejected("invalid_curriculum_examples", "A structured curriculum family requires 2–100 distinct English examples with controlled variations.", familyKey);
        if (semanticTransitions is null)
            return Rejected(
                "invalid_semantic_transition",
                "A semantic transition requires distinct, controlled source and result frames with valid shared variables.",
                familyKey);
        if (semanticSpanGroundings is null)
            return Rejected(
                "invalid_semantic_span_grounding",
                "A semantic grounding must identify one unique source-frame dimension and one controlled surface dimension.",
                familyKey);

        foreach (var transition in semanticTransitions)
        {
            var grounded = examples
                .Select((example, index) => new { Example = example, Index = index })
                .Where(item => TryBindSemanticFrame(transition.Source, item.Example.Variations, out _))
                .Select(source => examples
                    .Select((example, index) => new { Example = example, Index = index })
                    .Where(result => result.Index != source.Index &&
                        TryBindSemanticFrame(transition.Result, result.Example.Variations, out var resultBindings) &&
                        TryBindSemanticFrame(transition.Source, source.Example.Variations, out var sourceBindings) &&
                        BindingsAreCompatible(sourceBindings, resultBindings))
                    .Any())
                .Any();
            if (!grounded)
            {
                return Rejected(
                    "semantic_transition_not_grounded",
                    "Each semantic transition needs distinct controlled source and result curriculum examples in its family.",
                    familyKey);
            }
        }

        foreach (var grounding in semanticSpanGroundings)
        {
            var applicableTransitions = semanticTransitions
                .Where(transition => transition.Source.Dimensions.ContainsKey(grounding.SemanticDimension))
                .ToList();
            if (applicableTransitions.Count == 0)
            {
                return Rejected(
                    "semantic_span_grounding_not_source",
                    $"Grounding '{grounding.SemanticDimension} -> {grounding.SurfaceDimension}' must name a dimension in a declared source frame.",
                    familyKey);
            }

            var sourceExamples = examples
                .Where(example => applicableTransitions.Any(transition =>
                    TryBindSemanticFrame(transition.Source, example.Variations, out _)))
                .ToList();
            if (sourceExamples.Count == 0 || sourceExamples.Any(example =>
                    !example.Variations.TryGetValue(grounding.SurfaceDimension, out var surfaceValue) ||
                    !HasOneExactSurfaceSpan(example.Text, surfaceValue)))
            {
                return Rejected(
                    "semantic_span_grounding_not_explicit",
                    $"Grounding '{grounding.SemanticDimension} -> {grounding.SurfaceDimension}' needs one exact controlled surface span in every matching source example.",
                    familyKey);
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a Founder manifest's cross-example declarations before the
    /// durable receipt is accepted. The declaration keys are opaque semantic
    /// example identities and must resolve to controlled meaning graphs in
    /// this same immutable manifest; neither surface text nor family order is
    /// used to create a response relationship.
    /// </summary>
    internal async Task<LegendConnectCurriculumSubmissionResult?> PreflightFounderEnglishManifestAsync(
        LegendConnectCurriculumManifestSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var families = submission.Families?.ToArray() ?? [];
        if (families.Length == 0)
            return Rejected("empty_curriculum_manifest", "The curriculum manifest must contain at least one explicit semantic family.", null);

        var declaredExamples = new Dictionary<string, (string FamilyKey, NormalizedCurriculumExample Example)>(
            StringComparer.Ordinal);
        foreach (var family in families)
        {
            var validation = await PreflightFounderEnglishBatchAsync(family, cancellationToken);
            if (validation is not null)
                return validation;

            var familyKey = NormalizeFamilyKey(family.FamilyKey)!;
            var examples = NormalizeExamples(family.Examples)!;
            foreach (var example in examples.Where(item => item.SemanticExampleKey is not null))
            {
                if (example.MeaningGraph is null ||
                    !declaredExamples.TryAdd(example.SemanticExampleKey!, (familyKey, example)))
                {
                    return Rejected(
                        "invalid_semantic_example_identity",
                        "Every cross-example semantic key must identify exactly one controlled example with a governed meaning graph.",
                        NormalizeFamilyKey(family.FamilyKey));
                }
            }
        }

        // A semantic example key is a global opaque canonical identity, not a
        // manifest-local label. Detect a collision before accepting durable
        // work so a malformed later submission cannot partially mutate valid
        // family evidence and fail only when its relation work is reached.
        var semanticExampleIdentities = declaredExamples.Keys
            .Select(FounderSemanticExampleIdentity)
            .ToArray();
        if (semanticExampleIdentities.Length > 0)
        {
            var existing = await (
                from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                join family in _db.Set<LegendCurriculumFamily>().AsNoTracking()
                    on example.CurriculumFamilyId equals family.Id
                join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on example.TextUnitId equals unit.Id
                where example.SemanticExampleIdentity != null &&
                    semanticExampleIdentities.Contains(example.SemanticExampleIdentity) &&
                    example.SupersededUtc == null &&
                    example.DerivedFromCurriculumExampleId == null &&
                    example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    family.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
                select new { example.SemanticExampleIdentity, family.FamilyKey, unit.NormalizedHash }
            ).ToListAsync(cancellationToken);
            foreach (var item in declaredExamples)
            {
                var identity = FounderSemanticExampleIdentity(item.Key);
                var textHash = LegendLanguageIdentity.TextHash(item.Value.Example.Text);
                if (existing.Any(row =>
                    string.Equals(row.SemanticExampleIdentity, identity, StringComparison.Ordinal) &&
                    (!string.Equals(row.FamilyKey, item.Value.FamilyKey, StringComparison.Ordinal) ||
                     !string.Equals(row.NormalizedHash, textHash, StringComparison.Ordinal))))
                {
                    return Rejected(
                        "semantic_example_identity_conflict",
                        "A Founder semantic-example key is already bound to different canonical curriculum evidence.",
                        item.Value.FamilyKey);
                }
            }
        }

        var relationships = submission.CrossExampleSemanticRelationships ?? [];
        if (relationships.Count > 200)
            return Rejected("invalid_cross_example_relationship", "A Founder curriculum manifest may declare at most 200 cross-example semantic relationships.", null);

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relationship in relationships)
        {
            var sourceKey = NormalizeDimension(relationship.SourceSemanticExampleKey);
            var resultKey = NormalizeDimension(relationship.ResultSemanticExampleKey);
            var semanticIdentity = NormalizeDimension(relationship.RelationshipSemanticIdentity);
            if (sourceKey is null || resultKey is null || semanticIdentity is null ||
                string.Equals(sourceKey, resultKey, StringComparison.Ordinal) ||
                !declaredExamples.ContainsKey(sourceKey) ||
                !declaredExamples.ContainsKey(resultKey) ||
                !identities.Add(sourceKey + "\u001f" + semanticIdentity + "\u001f" + resultKey))
            {
                return Rejected(
                    "invalid_cross_example_relationship",
                    "A cross-example relationship must connect two distinct Founder-declared meaning graphs with one unique semantic relationship identity.",
                    null);
            }
        }

        return null;
    }

    public async Task<LegendConnectCurriculumSubmissionResult> SubmitFounderEnglishBatchAsync(
        LegendConnectCurriculumBatchSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var validation = await PreflightFounderEnglishBatchAsync(submission, cancellationToken);
        if (validation is not null)
            return validation;

        var familyKey = NormalizeFamilyKey(submission.FamilyKey)!;
        var examples = NormalizeExamples(submission.Examples)!;
        var semanticTransitions = NormalizeSemanticTransitions(submission.SemanticTransitions)!;
        var semanticSpanGroundings = NormalizeSemanticSpanGroundings(submission.SemanticSpanGroundings)!;
        const string english = "en";
        var newlyCreatedSourceTextUnitIds = new HashSet<Guid>();
        var newlyCreatedSourceExampleIds = new HashSet<Guid>();
        var sourceTextUnitsById = new Dictionary<Guid, LegendLanguageTextUnit>();

        LegendCurriculumFamily family;
        var isNewFamily = false;
        try
        {
            family = await LegendConnectCanonicalCurriculumPersistence.AdmitFamilyAsync(
                _db,
                familyKey,
                NormalizeOptional(submission.SemanticCategory, 120),
                "FounderApproved",
                cancellationToken);
            isNewFamily = _db.Entry(family).State == EntityState.Added;
            if (!isNewFamily && !string.IsNullOrWhiteSpace(submission.SemanticCategory) && family.SemanticCategory is null)
            {
                family.SemanticCategory = NormalizeOptional(submission.SemanticCategory, 120);
                family.UpdatedUtc = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            family = await _db.Set<LegendCurriculumFamily>()
                .SingleAsync(item => item.FamilyKey == familyKey, cancellationToken);
        }

        var sourceExamples = new List<LegendCurriculumExample>(examples.Count);
        var createdSourceCount = 0;
        foreach (var example in examples)
        {
            var textHash = LegendLanguageIdentity.TextHash(example.Text);
            var textUnit = await _db.Set<LegendLanguageTextUnit>()
                .SingleOrDefaultAsync(item => item.LanguageCode == english && item.NormalizedHash == textHash, cancellationToken);
            if (textUnit is null)
            {
                var submitted = await _corpus.SubmitApprovedKnowledgeAsync(
                    new LegendConnectKnowledgeSubmission(
                        english,
                        example.Text,
                        null,
                        null,
                        family.SemanticCategory,
                        null,
                        null,
                    "FounderApproved"),
                    cancellationToken,
                    queueFounderExpansion: false);
                if (!submitted.Succeeded && !submitted.DuplicatePrevented)
                    return Rejected(submitted.ErrorCode ?? "curriculum_source_rejected", submitted.Message ?? "The English curriculum example could not be retained.", familyKey);

                textUnit = submitted.SourceTextUnitId is { } textUnitId
                    ? await _db.Set<LegendLanguageTextUnit>().SingleAsync(item => item.Id == textUnitId, cancellationToken)
                    : await _db.Set<LegendLanguageTextUnit>().SingleAsync(item =>
                        item.LanguageCode == english && item.NormalizedHash == textHash, cancellationToken);
                newlyCreatedSourceTextUnitIds.Add(textUnit.Id);
                createdSourceCount++;
            }
            else if (!textUnit.IsTrainingEligible &&
                textUnit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                await _db.Set<LegendCurriculumExample>().AnyAsync(item =>
                    item.CurriculumFamilyId == family.Id &&
                    item.TextUnitId == textUnit.Id &&
                    item.DerivedFromCurriculumExampleId == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved,
                    cancellationToken))
            {
                // A retained controlled Founder declaration is the canonical
                // authority to reactivate its own historical example. This
                // covers only a governed family re-declared by its normal
                // manifest path; it never revives unrelated legacy raw text.
                textUnit.IsTrainingEligible = true;
                textUnit.UpdatedUtc = DateTime.UtcNow;
            }

            var curriculumExample = await GetOrCreateExampleAsync(
                family,
                textUnit,
                english,
                derivedFromCurriculumExampleId: null,
                cancellationToken);
            if (_db.Entry(curriculumExample).State == EntityState.Added)
                newlyCreatedSourceExampleIds.Add(curriculumExample.Id);
            await EnsureFounderSemanticExampleIdentityAsync(
                curriculumExample,
                example.SemanticExampleKey,
                cancellationToken);
            await EnsureVariationsAsync(curriculumExample, example.Variations, cancellationToken);
            sourceExamples.Add(curriculumExample);
            sourceTextUnitsById[textUnit.Id] = textUnit;
        }
        await _db.SaveChangesAsync(cancellationToken);

        var knownVariationsByExample = sourceExamples
            .Zip(examples, (curriculumExample, normalized) =>
                new KeyValuePair<Guid, IReadOnlyDictionary<string, string>>(
                    curriculumExample.Id,
                    normalized.Variations))
            .ToDictionary(item => item.Key, item => item.Value);
        var structuredSourceInputs = sourceExamples
            .DistinctBy(item => item.TextUnitId)
            .Select(item => new AtomicInput(
                sourceTextUnitsById[item.TextUnitId],
                "StructuredExample",
                null,
                null,
                null,
                newlyCreatedSourceTextUnitIds.Contains(item.TextUnitId)))
            .ToList();
        await EnsureLanguageLexicalObservationsAsync(structuredSourceInputs, english, cancellationToken);
        var sourceTextUnitIds = sourceExamples.Select(item => item.TextUnitId).ToHashSet();
        var expectedLexicalOccurrenceCount = sourceTextUnitsById.Values
            .Where(item => sourceTextUnitIds.Contains(item.Id))
            .Sum(item => SurfaceComponents(item.Text).Count);
        var trackedLexemeSurfaceById = _db.ChangeTracker.Entries<LegendLanguageLexeme>()
            .Where(entry => entry.State != EntityState.Detached && entry.State != EntityState.Deleted)
            .ToDictionary(entry => entry.Entity.Id, entry => entry.Entity.SurfaceForm);
        var knownLexicalOccurrences = _db.ChangeTracker.Entries<LegendLanguageLexicalOccurrence>()
            .Where(entry => entry.State != EntityState.Detached && entry.State != EntityState.Deleted &&
                entry.Entity.SupersededUtc is null && sourceTextUnitIds.Contains(entry.Entity.TextUnitId) &&
                trackedLexemeSurfaceById.ContainsKey(entry.Entity.LexemeId))
            .Select(entry => new FounderLexicalOccurrence(
                entry.Entity.TextUnitId,
                entry.Entity.TokenIndex,
                entry.Entity.LexemeId,
                trackedLexemeSurfaceById[entry.Entity.LexemeId]))
            .ToArray();
        await AttachExplicitFounderSemanticAnchorsAsync(
            family,
            sourceExamples,
            english,
            semanticTransitions,
            semanticSpanGroundings,
            sourceTextUnitsById,
            sourceTextUnitsById.Values
                .Where(item => item.IsTrainingEligible &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
                .Select(item => item.Id)
                .ToHashSet(),
            knownVariationsByExample,
            knownLexicalOccurrences.Length == expectedLexicalOccurrenceCount
                ? knownLexicalOccurrences
                : null,
            newlyCreatedSourceExampleIds,
            cancellationToken);
        await AttachFounderMeaningGraphsAsync(
            family,
            sourceExamples,
            examples,
            english,
            cancellationToken);
        // Co-realization is a derived relation between two explicit meaning
        // graphs. It must run only after the canonical graphs have been
        // persisted; doing so earlier would make replay observe evidence that
        // ordinary ingestion could not yet see.
        await AttachFounderCoRealizedSemanticAnchorsAsync(
            family,
            sourceExamples,
            english,
            sourceTextUnitsById,
            knownVariationsByExample,
            cancellationToken);
        await PersistGovernedSemanticTransitionEvidenceAsync(
            family,
            sourceExamples,
            semanticTransitions,
            english,
            knownVariationsByExample,
            newlyCreatedSourceExampleIds,
            provenance: LegendConnectKnowledgeProvenance.FounderApproved,
            isHumanVerifiedSupport: true,
            cancellationToken: cancellationToken);

        // Ordinary Founder ingestion and historical replay must converge
        // through the same canonical source-evidence authority. Once retained
        // semantic-transition evidence becomes independently production
        // eligible, this reconciliation materializes any missing governed
        // source-frame projection from that existing evidence. It does not
        // infer semantic values from text, introduce phrase-specific logic, or
        // create a competing evidence path.
        await ReconcileFounderApprovedSourceEvidenceAsync(
            family.Id,
            english,
            cancellationToken);

        // This is the existing expansion authority. It is idempotent by source
        // asset and directional pair, and it carries curriculum lineage only as
        // metadata for the already-approved work.
        foreach (var sourceExample in sourceExamples.DistinctBy(item => item.Id))
        {
            var sourceUnit = sourceTextUnitsById[sourceExample.TextUnitId];
            await _corpus.EnsureFounderSeedCandidatesAsync(
                sourceUnit,
                family.Id,
                sourceExample.Id,
                cancellationToken,
                newlyCreatedSourceTextUnitIds.Contains(sourceUnit.Id));
            await AttachExistingExpansionsAsync(sourceExample, sourceUnit, cancellationToken);
        }

        // A new Founder family has just established its entire active English
        // source set in an owned durable evaluation transaction. Reusing that
        // exact canonical set avoids a second broad read of CurriculumExamples
        // while independent family workers still hold their own writes.
        // Ordinary direct submissions, existing, or partial families continue
        // through the persisted-query path so their full historical source set
        // remains authoritative.
        var knownNewFamilyExamples = _db.Database.CurrentTransaction is not null &&
            isNewFamily &&
            sourceExamples.Select(item => item.Id).Distinct().All(newlyCreatedSourceExampleIds.Contains)
                ? BuildFounderAnalysisExamples(sourceExamples, sourceTextUnitsById)
                : null;
        var knownAnalysisAnchors = knownNewFamilyExamples is not null
            ? BuildTrackedExplicitControlledAnchorsByExample(
                sourceExamples.Select(item => item.Id).ToArray(),
                english)
            : null;
        await AnalyzeFamilyLanguageAsync(
            family.Id,
            english,
            pairKey: null,
            cancellationToken,
            knownNewFamilyExamples,
            knownVariationsByExample,
            knownAnalysisAnchors,
            sourceIdentitiesKnownNew: knownNewFamilyExamples is not null);
        var targetExpansionCount = await _db.Set<LegendCurriculumExample>()
            .CountAsync(item => item.CurriculumFamilyId == family.Id && item.LanguageCode != english, cancellationToken);
        return new LegendConnectCurriculumSubmissionResult(
            true,
            createdSourceCount == 0,
            null,
            createdSourceCount == 0
                ? "The curriculum family already matched canonical English entries; no duplicate learning confidence was created."
                : "Founder English curriculum was saved and queued through the existing Azure expansion pipeline.",
            family.FamilyKey,
            family.Id,
            sourceExamples.Select(item => item.Id).Distinct().Count(),
            targetExpansionCount);
    }

    /// <summary>
    /// Establishes the existing canonical language-pair rows that monolingual
    /// Founder English evidence may need for ordinary expansion.  Manifest
    /// workers call this once before independent family claims begin: a
    /// language-pair row is a shared canonical dependency, whereas families
    /// are otherwise independent.  This does not interpret curriculum,
    /// create evidence, or queue a second processing path.
    /// </summary>
    internal async Task EnsureFounderEnglishExpansionPairsAsync(
        CancellationToken cancellationToken = default)
    {
        const string english = "en";
        var source = await _languages.NormalizeEnabledTranslationLanguageAsync(english, cancellationToken);
        if (source is null)
            return;

        var targets = await _languages.ListEnabledTranslationLanguagesAsync(cancellationToken);
        foreach (var target in targets
                     .Where(item => item.IsLearningEnabled && item.IsTranslationEnabled &&
                         !string.Equals(item.Code, source, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase))
        {
            _ = await _languages.GetOrCreateEnabledPairAsync(source, target.Code, cancellationToken);
        }
    }

    /// <summary>
    /// Establishes only globally shared lexical primitive identities from an
    /// already accepted Founder manifest before its independent family work is
    /// claimed. Family-local occurrences, relationships, graphs, anchors and
    /// evidence remain owned by their normal durable family evaluator.
    /// </summary>
    internal async Task EnsureFounderManifestLexicalPrerequisitesAsync(
        IReadOnlyCollection<LegendConnectCurriculumBatchSubmission> families,
        CancellationToken cancellationToken = default)
    {
        var components = families
            .SelectMany(family => family.Examples ?? [])
            .SelectMany(example => SurfaceComponents(example.Text))
            .GroupBy(component => component.NormalizedHash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (components.Length == 0)
            return;

        const string english = "en";
        var admitted = await LegendConnectCanonicalCurriculumPersistence.AdmitLexemesAsync(
            _db,
            english,
            components.Select(item => new LegendCanonicalLexemeAdmission(
                item.NormalizedHash,
                item.NormalizedText,
                LegendConnectKnowledgeProvenance.FounderApproved)).ToArray(),
            cancellationToken);
        var created = admitted.Values
            .Where(item => _db.Entry(item).State == EntityState.Added)
            .ToArray();
        if (created.Length == 0)
            return;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            foreach (var lexeme in created)
                _db.Entry(lexeme).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Extends the existing curriculum authority with direct English surface
    /// evidence after the canonical Founder ingestion boundary has produced
    /// atomic units. This records only observed lexical identities, ordered
    /// boundaries, and neighboring sentences; semantic or grammatical claims
    /// still require controlled curriculum variations below.
    /// </summary>
    internal async Task ObserveFounderEnglishAtomicUnitsAsync(
        Guid trainingSubmissionId,
        IReadOnlyList<LegendFounderTrainingAtomicUnit> units,
        IReadOnlyDictionary<string, LegendLanguageTextUnit> textUnitsByHash,
        CancellationToken cancellationToken = default)
    {
        var inputs = units
            .Select(unit => new AtomicInput(
                textUnitsByHash[LegendLanguageIdentity.TextHash(unit.Text)],
                unit.UnitType,
                trainingSubmissionId,
                unit.SequenceNumber,
                unit.ParagraphNumber))
            .Where(item => item.TextUnit.IsTrainingEligible &&
                string.Equals(item.TextUnit.LanguageCode, "en", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (inputs.Count == 0)
            return;

        await EnsureLanguageLexicalObservationsAsync(inputs, "en", cancellationToken);
        await EnsureParagraphNeighborRelationshipsAsync(inputs, cancellationToken);
    }

    /// <summary>
    /// Retires curriculum and structural observations that depend on canonical
    /// assets invalidated by legacy raw-submission reconciliation. New atomic
    /// examples are then analyzed normally by the same authority.
    /// </summary>
    internal async Task ReconcileSupersededExamplesAsync(
        IReadOnlyCollection<Guid> textUnitIds,
        CancellationToken cancellationToken = default)
    {
        if (textUnitIds.Count == 0)
            return;
        var now = DateTime.UtcNow;
        var lexicalOccurrences = await _db.Set<LegendLanguageLexicalOccurrence>()
            .Where(item => item.SupersededUtc == null && textUnitIds.Contains(item.TextUnitId))
            .ToListAsync(cancellationToken);
        foreach (var occurrence in lexicalOccurrences)
        {
            occurrence.SupersededUtc = now;
            occurrence.UpdatedUtc = now;
        }
        var lexicalRelationships = await _db.Set<LegendLanguageLexicalRelationship>()
            .Where(item => item.SupersededUtc == null && textUnitIds.Contains(item.TextUnitId))
            .ToListAsync(cancellationToken);
        foreach (var relationship in lexicalRelationships)
        {
            relationship.SupersededUtc = now;
            relationship.UpdatedUtc = now;
        }
        var anchors = await _db.Set<LegendLanguageCompositionalAnchor>()
            .Where(item => item.SupersededUtc == null && textUnitIds.Contains(item.TextUnitId))
            .ToListAsync(cancellationToken);
        foreach (var anchor in anchors)
            anchor.SupersededUtc = now;
        if (lexicalOccurrences.Count > 0 || lexicalRelationships.Count > 0 || anchors.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        var examples = await _db.Set<LegendCurriculumExample>()
            .Where(item => item.SupersededUtc == null && textUnitIds.Contains(item.TextUnitId))
            .ToListAsync(cancellationToken);
        if (examples.Count == 0)
            return;
        foreach (var example in examples)
        {
            example.SupersededUtc = now;
            example.UpdatedUtc = now;
        }
        var exampleIds = examples.Select(item => item.Id).ToArray();
        var evidence = await _db.Set<LegendLanguageStructuralEvidence>()
            .Where(item => item.SupersededUtc == null &&
                (exampleIds.Contains(item.BaselineCurriculumExampleId) ||
                 exampleIds.Contains(item.ComparedCurriculumExampleId)))
            .ToListAsync(cancellationToken);
        foreach (var item in evidence)
            item.SupersededUtc = now;
        var semanticTransitions = await _db.Set<LegendSemanticTransitionEvidence>()
            .Where(item => item.SupersededUtc == null &&
                (exampleIds.Contains(item.SourceCurriculumExampleId) ||
                 exampleIds.Contains(item.ResultCurriculumExampleId)))
            .ToListAsync(cancellationToken);
        foreach (var item in semanticTransitions)
        {
            item.SupersededUtc = now;
            item.UpdatedUtc = now;
        }
        await _db.SaveChangesAsync(cancellationToken);

        var affectedPatterns = evidence.Select(item => item.StructuralPatternId).Distinct().ToArray();
        var affectedRelationships = evidence
            .Where(item => item.StructuralRelationshipId is not null)
            .Select(item => item.StructuralRelationshipId!.Value)
            .Distinct()
            .ToArray();
        foreach (var patternId in affectedPatterns)
            await RefreshPatternMaturityAsync(patternId, cancellationToken);
        foreach (var relationshipId in affectedRelationships)
            await RefreshStructuralRelationshipMaturityAsync(relationshipId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Retires only the active curriculum projection that was derived from one
    /// superseded directional alignment. The alignment and its prior evidence
    /// remain auditable; no active composition or contextual decision may
    /// continue to use that exact source/target realization. This deliberately
    /// reuses the established structural-evidence reconciliation authority
    /// rather than introducing a correction-specific learning path.
    /// </summary>
    internal async Task ReconcileSupersededAlignmentAsync(
        string pairKey,
        Guid sourceTextUnitId,
        Guid targetTextUnitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pairKey) || sourceTextUnitId == Guid.Empty || targetTextUnitId == Guid.Empty)
            return;

        var now = DateTime.UtcNow;
        var exampleIds = await (
            from targetExample in _db.Set<LegendCurriculumExample>()
            join sourceExample in _db.Set<LegendCurriculumExample>()
                on targetExample.DerivedFromCurriculumExampleId equals sourceExample.Id
            where targetExample.SupersededUtc == null &&
                targetExample.TextUnitId == targetTextUnitId &&
                sourceExample.TextUnitId == sourceTextUnitId
            select targetExample.Id
        ).ToListAsync(cancellationToken);

        var evidence = exampleIds.Count == 0
            ? new List<StructuralEvidenceImpact>()
            : await _db.Set<LegendLanguageStructuralEvidence>()
                .AsNoTracking()
                .Where(item => item.SupersededUtc == null &&
                    (exampleIds.Contains(item.BaselineCurriculumExampleId) ||
                     exampleIds.Contains(item.ComparedCurriculumExampleId)))
                .Select(item => new StructuralEvidenceImpact(
                    item.StructuralPatternId,
                    item.StructuralRelationshipId))
                .ToListAsync(cancellationToken);

        // Contextual examples originate directly from the alignment. Controlled
        // variation contexts are pair-scoped projections of its target example.
        // Both must become historical when that realization is corrected, while
        // contexts for other pairs and other source/target identities remain live.
        var contextQuery = _db.Set<LegendLanguageContextRelationship>()
            .Where(item => item.SupersededUtc == null && item.PairKey == pairKey &&
                ((item.RelationshipKind == "ContextualExample" &&
                    item.SourceTextUnitId == sourceTextUnitId && item.RelatedTextUnitId == targetTextUnitId) ||
                 (item.RelationshipKind == "ControlledVariation" &&
                    (item.SourceTextUnitId == targetTextUnitId || item.RelatedTextUnitId == targetTextUnitId))));

        // This is the same reconciliation state transition as the tracked
        // implementation, expressed as bounded set-based writes. A correction
        // can retire many derived observations; materializing every one inside
        // the Founder request previously held the transaction long enough for
        // IIS to cancel the request before its authoritative redirect.
        var usesSetBasedUpdates = _db.Database.IsRelational();
        var exampleRetirementQuery = _db.Set<LegendCurriculumExample>()
            .Where(item => exampleIds.Contains(item.Id) && item.SupersededUtc == null);
        var retiredExampleCount = exampleIds.Count == 0
            ? 0
            : await ApplyRetirementAsync(
                exampleRetirementQuery,
                setters => setters
                    .SetProperty(item => item.SupersededUtc, (DateTime?)now)
                    .SetProperty(item => item.UpdatedUtc, now),
                item =>
                {
                    item.SupersededUtc = now;
                    item.UpdatedUtc = now;
                },
                cancellationToken);
        var evidenceRetirementQuery = _db.Set<LegendLanguageStructuralEvidence>()
            .Where(item => item.SupersededUtc == null &&
                (exampleIds.Contains(item.BaselineCurriculumExampleId) ||
                 exampleIds.Contains(item.ComparedCurriculumExampleId)));
        var retiredEvidenceCount = exampleIds.Count == 0
            ? 0
            : await ApplyRetirementAsync(
                evidenceRetirementQuery,
                setters => setters.SetProperty(item => item.SupersededUtc, (DateTime?)now),
                item => item.SupersededUtc = now,
                cancellationToken);
        var retiredContextCount = await ApplyRetirementAsync(
            contextQuery,
            setters => setters
                .SetProperty(item => item.SupersededUtc, (DateTime?)now)
                .SetProperty(item => item.UpdatedUtc, now),
            item =>
            {
                item.SupersededUtc = now;
                item.UpdatedUtc = now;
            },
            cancellationToken);

        if (!usesSetBasedUpdates)
            await _db.SaveChangesAsync(cancellationToken);

        // A corrected target can retain current lexical/component observations
        // only when another active canonical lineage still uses that text unit.
        // This avoids a global text reset while preventing an otherwise orphaned
        // corrected realization from continuing to supply active observations.
        var targetRemainsCurrent = await _db.Set<LegendCurriculumExample>()
            .AnyAsync(item => item.TextUnitId == targetTextUnitId && item.SupersededUtc == null, cancellationToken) ||
            await _db.Set<LegendTranslationAlignment>()
                .AnyAsync(item => item.SupersededUtc == null &&
                    (item.SourceTextUnitId == targetTextUnitId || item.TargetTextUnitId == targetTextUnitId), cancellationToken);
        if (!targetRemainsCurrent)
        {
            var occurrenceRetirementQuery = _db.Set<LegendLanguageLexicalOccurrence>()
                .Where(item => item.TextUnitId == targetTextUnitId && item.SupersededUtc == null);
            await ApplyRetirementAsync(
                occurrenceRetirementQuery,
                setters => setters
                    .SetProperty(item => item.SupersededUtc, (DateTime?)now)
                    .SetProperty(item => item.UpdatedUtc, now),
                item =>
                {
                    item.SupersededUtc = now;
                    item.UpdatedUtc = now;
                },
                cancellationToken);
            var relationshipRetirementQuery = _db.Set<LegendLanguageLexicalRelationship>()
                .Where(item => item.TextUnitId == targetTextUnitId && item.SupersededUtc == null);
            await ApplyRetirementAsync(
                relationshipRetirementQuery,
                setters => setters
                    .SetProperty(item => item.SupersededUtc, (DateTime?)now)
                    .SetProperty(item => item.UpdatedUtc, now),
                item =>
                {
                    item.SupersededUtc = now;
                    item.UpdatedUtc = now;
                },
                cancellationToken);
            var anchorRetirementQuery = _db.Set<LegendLanguageCompositionalAnchor>()
                .Where(item => item.TextUnitId == targetTextUnitId && item.SupersededUtc == null);
            await ApplyRetirementAsync(
                anchorRetirementQuery,
                setters => setters.SetProperty(item => item.SupersededUtc, (DateTime?)now),
                item => item.SupersededUtc = now,
                cancellationToken);
            if (!usesSetBasedUpdates)
                await _db.SaveChangesAsync(cancellationToken);
        }

        if (retiredExampleCount == 0 && retiredEvidenceCount == 0 && retiredContextCount == 0)
            return;

        foreach (var patternId in evidence.Select(item => item.StructuralPatternId).Distinct())
            await RefreshPatternMaturityAsync(patternId, cancellationToken);
        foreach (var relationshipId in evidence
            .Where(item => item.StructuralRelationshipId is not null)
            .Select(item => item.StructuralRelationshipId!.Value)
            .Distinct())
            await RefreshStructuralRelationshipMaturityAsync(relationshipId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// SQL Server performs correction retirement as one statement per affected
    /// projection. The in-memory provider used by the isolated proof suite has
    /// no ExecuteUpdate support, so it uses the same state transition through
    /// the tracked context and the caller persists that single unit of work.
    /// </summary>
    private async Task<int> ApplyRetirementAsync<TEntity>(
        IQueryable<TEntity> query,
        Action<UpdateSettersBuilder<TEntity>> relationalSetters,
        Action<TEntity> inMemorySetter,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (_db.Database.IsRelational())
            return await query.ExecuteUpdateAsync(relationalSetters, cancellationToken);

        var entities = await query.ToListAsync(cancellationToken);
        foreach (var entity in entities)
            inMemorySetter(entity);
        return entities.Count;
    }

    /// <summary>
    /// Called after the established candidate → Azure → corpus path has
    /// persisted an alignment. It attaches the new target asset to every
    /// relevant family and runs only same-language structural comparisons.
    /// </summary>
    public async Task AttachProcessedExpansionAsync(
        LegendCorpusCandidate candidate,
        LegendLanguagePairSnapshot pair,
        CancellationToken cancellationToken = default)
    {
        var source = await _db.Set<LegendLanguageTextUnit>()
            .SingleOrDefaultAsync(item => item.LanguageCode == pair.SourceLanguageCode &&
                item.NormalizedHash == candidate.SourceTextHash && item.IsTrainingEligible, cancellationToken);
        if (source is null)
            return;

        var sourceExamples = await _db.Set<LegendCurriculumExample>()
            .Where(item => item.TextUnitId == source.Id && item.LanguageCode == pair.SourceLanguageCode && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        if (sourceExamples.Count == 0)
            return;

        var alignment = await _db.Set<LegendTranslationAlignment>()
            .Where(item => item.PairKey == pair.PairKey && item.SourceTextUnitId == source.Id && item.SupersededUtc == null)
            .OrderByDescending(item => item.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (alignment is null)
            return;
        await AttachAlignmentToCurriculumAsync(alignment.Id, cancellationToken);
    }

    /// <summary>
    /// Reuses the one curriculum/structural authority when a Founder verifies
    /// an existing directional alignment. A verified correction therefore
    /// strengthens related pair evidence without copying target text into a
    /// second corpus or creating a separate correction engine.
    /// </summary>
    internal async Task AttachValidatedAlignmentAsync(
        Guid alignmentId,
        CancellationToken cancellationToken = default)
    {
        var alignment = await _db.Set<LegendTranslationAlignment>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == alignmentId && item.HumanVerified && item.SupersededUtc == null,
                cancellationToken);
        if (alignment is not null)
            await AttachAlignmentToCurriculumAsync(alignment.Id, cancellationToken);
    }

    /// <summary>
    /// Replays canonical active alignments through the existing curriculum
    /// authority. It preserves provider rows and target assets while ensuring
    /// historical ProviderDerived observations are only represented as
    /// insufficient candidate structure until independent human evidence
    /// supports them. The work is idempotent by the existing curriculum and
    /// structural-evidence uniqueness identities.
    /// </summary>
    internal async Task<int> ReevaluateHistoricalAlignmentsAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        // Preserve the original idempotent replay entry point used by tests
        // and direct callers. The worker uses the overload below so a future
        // evaluator revision can traverse every active historical identity.
        _ = await ReevaluateHistoricalAlignmentsAsync(
            take,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            afterId: null,
            cancellationToken);
        var alignments = await ReevaluateHistoricalAlignmentsAsync(
            take,
            LegendConnectLanguageIntelligenceReevaluationPhases.Alignments,
            afterId: null,
            cancellationToken);
        return alignments.ProcessedCount;
    }

    /// <summary>
    /// Processes one bounded page of the existing canonical curriculum
    /// reevaluator. The durable runtime-policy cursor selects a stable
    /// historical identity; all analysis still flows through the same
    /// curriculum, lexical, structural, maturity, and correction rules.
    /// </summary>
    internal async Task<LegendConnectHistoricalReevaluationProgress> ReevaluateHistoricalAlignmentsAsync(
        int take,
        string phase,
        Guid? afterId,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(take, 1, 250);
        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies)
        {
            // Rebuild monolingual controlled evidence first. Empty pair scope
            // remains a source-language observation and cannot lend target
            // maturity to a directional pair.
            var sourceFamilyIds = await (
                from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on example.TextUnitId equals unit.Id
                where example.DerivedFromCurriculumExampleId == null && example.SupersededUtc == null &&
                    unit.IsTrainingEligible && (!afterId.HasValue || example.CurriculumFamilyId.CompareTo(afterId.Value) > 0)
                select example.CurriculumFamilyId
            ).Distinct().OrderBy(item => item).Take(pageSize).ToListAsync(cancellationToken);

            foreach (var familyId in sourceFamilyIds)
            {
                var languages = await (
                    from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                    join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                        on example.TextUnitId equals unit.Id
                    where example.CurriculumFamilyId == familyId && example.DerivedFromCurriculumExampleId == null &&
                        example.SupersededUtc == null && unit.IsTrainingEligible
                    select example.LanguageCode
                ).Distinct().OrderBy(item => item).ToListAsync(cancellationToken);
                foreach (var languageCode in languages)
                    await ReevaluateHistoricalSourceFamilyLanguageAsync(
                        familyId,
                        languageCode,
                        cancellationToken);
            }

            return new LegendConnectHistoricalReevaluationProgress(
                sourceFamilyIds.Count,
                sourceFamilyIds.Count == 0 ? null : sourceFamilyIds[^1],
                sourceFamilyIds.Count < pageSize);
        }

        if (phase != LegendConnectLanguageIntelligenceReevaluationPhases.Alignments)
            throw new ArgumentOutOfRangeException(nameof(phase), "The curriculum evaluator handles source-family or alignment pages only.");

        var alignmentIds = await (
            from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where alignment.SupersededUtc == null && source.IsTrainingEligible && target.IsTrainingEligible &&
                (!afterId.HasValue || alignment.Id.CompareTo(afterId.Value) > 0) &&
                _db.Set<LegendCurriculumExample>().Any(example =>
                    example.TextUnitId == source.Id && example.SupersededUtc == null)
            orderby alignment.Id
            select alignment.Id
        ).Take(pageSize).ToListAsync(cancellationToken);

        foreach (var alignmentId in alignmentIds)
            await ReevaluateHistoricalAlignmentAsync(alignmentId, cancellationToken);

        return new LegendConnectHistoricalReevaluationProgress(
            alignmentIds.Count,
            alignmentIds.Count == 0 ? null : alignmentIds[^1],
            alignmentIds.Count < pageSize);
    }

    /// <summary>
    /// Executes one already-claimed historical identity through the same
    /// canonical curriculum evaluator used by cursor replay. It adds no
    /// semantic rules or evidence authority; the durable work layer supplies
    /// only scheduling and collision protection.
    /// </summary>
    internal async Task ReevaluateHistoricalWorkItemAsync(
        string phase,
        Guid subjectId,
        string subjectScope,
        CancellationToken cancellationToken = default)
    {
        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies)
        {
            await ReevaluateHistoricalSourceFamilyLanguageAsync(subjectId, subjectScope, cancellationToken);
            return;
        }

        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.Alignments)
        {
            await ReevaluateHistoricalAlignmentAsync(subjectId, cancellationToken);
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(phase), "The curriculum evaluator handles source-family or alignment work only.");
    }

    /// <summary>
    /// Records compact contract-to-artifact provenance for one already
    /// canonical Founder family. This is deliberately metadata-only: it does
    /// not analyse surface language, create an anchor/node/relation, alter a
    /// maturity projection, or touch production eligibility. The same
    /// durable work lease that owns this bounded family owns the inventory,
    /// making bootstrap resumable and idempotent without a second replay
    /// authority.
    /// </summary>
    internal async Task InventoryHistoricalDerivationDependenciesAsync(
        Guid familyId,
        int evaluatorVersion,
        CancellationToken cancellationToken = default)
    {
        if (!RequiresDependencyInventory(evaluatorVersion))
            return;

        var sourceContract = LegendConnectDerivationContracts.ContractIdentityFor(
            evaluatorVersion,
            LegendConnectDerivationContracts.SourceSemanticProjection);
        var transformationContract = LegendConnectDerivationContracts.ContractIdentityFor(
            evaluatorVersion,
            LegendConnectDerivationContracts.GovernedSemanticTransformation);
        var candidates = new List<LegendDerivationArtifactCandidate>();

        var anchors = await _db.Set<LegendLanguageCompositionalAnchor>()
            .AsNoTracking()
            .Where(item => item.CurriculumFamilyId == familyId && item.SupersededUtc == null)
            .Select(item => new { item.Id, item.CurriculumExampleId, item.AnchorSignature, item.SemanticSignature })
            .ToListAsync(cancellationToken);
        candidates.AddRange(anchors.Select(item => new LegendDerivationArtifactCandidate(
            "compositional-anchor",
            $"anchor:{item.CurriculumExampleId:D}:{item.AnchorSignature}",
            $"anchor-evidence:{item.Id:D}",
            item.SemanticSignature ?? item.AnchorSignature,
            sourceContract)));

        var nodes = await _db.Set<LegendLanguageMeaningNodeEvidence>()
            .AsNoTracking()
            .Where(item => item.CurriculumFamilyId == familyId && item.SupersededUtc == null)
            .Select(item => new { item.Id, item.CurriculumExampleId, item.NodeKey, item.SemanticSignature })
            .ToListAsync(cancellationToken);
        candidates.AddRange(nodes.Select(item => new LegendDerivationArtifactCandidate(
            "meaning-node",
            $"meaning-node:{item.CurriculumExampleId:D}:{item.NodeKey}",
            $"meaning-node-evidence:{item.Id:D}",
            item.SemanticSignature,
            sourceContract)));

        var primitiveEvidence = await _db.Set<LegendLanguageMeaningPrimitiveEvidence>()
            .AsNoTracking()
            .Where(item => item.CurriculumFamilyId == familyId && item.SupersededUtc == null)
            .Select(item => new
            {
                item.Id,
                item.MeaningPrimitiveId,
                item.MeaningNodeEvidenceId,
                item.EvidenceIdentity
            })
            .ToListAsync(cancellationToken);
        candidates.AddRange(primitiveEvidence.Select(item => new LegendDerivationArtifactCandidate(
            "meaning-primitive",
            $"meaning-primitive:{item.MeaningPrimitiveId:D}",
            $"meaning-node:{item.MeaningNodeEvidenceId:D}",
            item.EvidenceIdentity,
            sourceContract)));

        var relationEvidence = await _db.Set<LegendLanguageMeaningRelationEvidence>()
            .AsNoTracking()
            .Where(item => item.CurriculumFamilyId == familyId && item.SupersededUtc == null)
            .Select(item => new
            {
                item.Id,
                item.MeaningRelationId,
                item.EvidenceIdentity
            })
            .ToListAsync(cancellationToken);
        candidates.AddRange(relationEvidence.Select(item => new LegendDerivationArtifactCandidate(
            "meaning-relation",
            $"meaning-relation:{item.MeaningRelationId:D}",
            $"meaning-relation-evidence:{item.Id:D}",
            item.EvidenceIdentity,
            sourceContract)));

        var exampleIds = await _db.Set<LegendCurriculumExample>()
            .AsNoTracking()
            .Where(item => item.CurriculumFamilyId == familyId && item.SupersededUtc == null)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (exampleIds.Count > 0)
        {
            var transitions = await _db.Set<LegendSemanticTransitionEvidence>()
                .AsNoTracking()
                .Where(item => item.SupersededUtc == null &&
                    (exampleIds.Contains(item.SourceCurriculumExampleId) ||
                     exampleIds.Contains(item.ResultCurriculumExampleId)))
                .Select(item => new
                {
                    item.Id,
                    item.TransitionSignature,
                    item.FounderRelationshipSemanticSignature,
                    item.SourceSemanticFrameSignature,
                    item.ResultSemanticFrameSignature
                })
                .ToListAsync(cancellationToken);
            candidates.AddRange(transitions.Select(item => new LegendDerivationArtifactCandidate(
                "semantic-transformation",
                $"semantic-transition:{item.TransitionSignature}",
                $"semantic-transition-evidence:{item.Id:D}",
                item.FounderRelationshipSemanticSignature ?? LegendLanguageIdentity.TextHash(
                    $"{item.SourceSemanticFrameSignature}|{item.ResultSemanticFrameSignature}"),
                transformationContract)));
        }

        if (candidates.Count == 0)
            return;

        var distinct = candidates
            .DistinctBy(item => (item.ArtifactKind, item.ResultArtifactIdentity,
                item.SourceDependencyIdentity, item.DerivationContractIdentity))
            .ToArray();
        var resultIdentities = distinct.Select(item => item.ResultArtifactIdentity).Distinct().ToArray();
        var sourceIdentities = distinct.Select(item => item.SourceDependencyIdentity).Distinct().ToArray();
        var contractIdentities = distinct.Select(item => item.DerivationContractIdentity).Distinct().ToArray();
        var existing = await _db.Set<LegendLanguageDerivationArtifact>()
            .Where(item => resultIdentities.Contains(item.ResultArtifactIdentity) &&
                sourceIdentities.Contains(item.SourceDependencyIdentity) &&
                contractIdentities.Contains(item.DerivationContractIdentity))
            .ToListAsync(cancellationToken);
        var existingByKey = existing.ToDictionary(item => (
                item.ArtifactKind,
                item.ResultArtifactIdentity,
                item.SourceDependencyIdentity,
                item.DerivationContractIdentity));
        var now = DateTime.UtcNow;
        foreach (var candidate in distinct)
        {
            if (existingByKey.TryGetValue((candidate.ArtifactKind, candidate.ResultArtifactIdentity,
                    candidate.SourceDependencyIdentity, candidate.DerivationContractIdentity), out var retained))
            {
                // The canonical family/evidence query above is the authority
                // that this exact artifact still exists. A same-contract
                // replay may have marked its ledger row Stale before bounded
                // revalidation; restore that exact row rather than leaving a
                // missing Current projection or inserting a duplicate.
                if (!string.Equals(retained.State, "Current", StringComparison.Ordinal) ||
                    !string.Equals(
                        retained.SourceDependencySemanticVersion,
                        candidate.SourceDependencySemanticVersion,
                        StringComparison.Ordinal))
                {
                    retained.State = "Current";
                    retained.SourceDependencySemanticVersion = candidate.SourceDependencySemanticVersion;
                    retained.UpdatedUtc = now;
                }
                continue;
            }
            _db.Set<LegendLanguageDerivationArtifact>().Add(new LegendLanguageDerivationArtifact
            {
                Id = Guid.NewGuid(),
                ArtifactKind = candidate.ArtifactKind,
                ResultArtifactIdentity = candidate.ResultArtifactIdentity,
                SourceDependencyIdentity = candidate.SourceDependencyIdentity,
                SourceDependencySemanticVersion = candidate.SourceDependencySemanticVersion,
                DerivationContractIdentity = candidate.DerivationContractIdentity,
                State = "Current",
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Gives every intake and replay caller one contract-governed entry point
    /// for dependency inventory. The declaration decides whether any current
    /// derivation needs the ledger; callers never inspect a feature kind or
    /// evaluator number to make that decision.
    /// </summary>
    internal Task RefreshCurrentDerivationDependenciesForFamilyAsync(
        Guid familyId,
        int evaluatorVersion,
        CancellationToken cancellationToken = default) =>
        InventoryHistoricalDerivationDependenciesAsync(familyId, evaluatorVersion, cancellationToken);

    // Test/runtime helper for the cursor-compatible harness only. The actual
    // production inventory is seeded and leased by
    // LegendConnectHistoricalReevaluationWorkAuthority; this returns stable
    // IDs solely so the older in-memory cursor test can model the same
    // metadata-only phase without inventing a second evaluator.
    internal Task<List<Guid>> GetHistoricalDependencyInventoryFamilyIdsAsync(
        CancellationToken cancellationToken = default) => _db.Set<LegendCurriculumExample>()
        .AsNoTracking()
        .Where(item => item.SupersededUtc == null)
        .Select(item => item.CurriculumFamilyId)
        .Distinct()
        .OrderBy(item => item)
        .ToListAsync(cancellationToken);

    /// <summary>
    /// Bounded inventory page for the existing durable worker. The cursor is
    /// the stable family identity stored by the runtime policy; the returned
    /// continuation is persisted in the same owned execution transaction as
    /// the dependency rows. A large pre-contract corpus therefore receives a
    /// small number of metadata pages, not one new semantic replay item per
    /// historical family.
    /// </summary>
    internal async Task<LegendDependencyInventoryProgress>
        InventoryHistoricalDerivationDependenciesBatchAsync(
            Guid? afterFamilyId,
            int evaluatorVersion,
            int take,
            CancellationToken cancellationToken = default)
    {
        var familyIds = await _db.Set<LegendCurriculumExample>().AsNoTracking()
            .Where(item => item.SupersededUtc == null &&
                (!afterFamilyId.HasValue || item.CurriculumFamilyId.CompareTo(afterFamilyId.Value) > 0))
            .Select(item => item.CurriculumFamilyId)
            .Distinct()
            .OrderBy(item => item)
            .Take(Math.Clamp(take, 1, 64))
            .ToListAsync(cancellationToken);
        foreach (var familyId in familyIds)
            await InventoryHistoricalDerivationDependenciesAsync(familyId, evaluatorVersion, cancellationToken);
        return new LegendDependencyInventoryProgress(
            familyIds.Count,
            familyIds.Count == 0 ? null : familyIds[^1]);
    }

    internal async Task RefreshCurrentDerivationDependenciesForManifestAsync(
        IReadOnlyCollection<string> familyKeys,
        int evaluatorVersion,
        CancellationToken cancellationToken = default)
    {
        if (familyKeys.Count == 0)
            return;
        var normalized = familyKeys
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var familyIds = await _db.Set<LegendCurriculumFamily>().AsNoTracking()
            .Where(item => normalized.Contains(item.FamilyKey))
            .Select(item => item.Id)
            .OrderBy(item => item)
            .ToListAsync(cancellationToken);
        foreach (var familyId in familyIds)
            await RefreshCurrentDerivationDependenciesForFamilyAsync(familyId, evaluatorVersion, cancellationToken);
    }

    private static bool RequiresDependencyInventory(int evaluatorVersion) =>
        LegendConnectDerivationContracts.ForEvaluator(evaluatorVersion)
            .Any(item => item.RequiresDependencyInventory);

    private sealed record LegendDerivationArtifactCandidate(
        string ArtifactKind,
        string ResultArtifactIdentity,
        string SourceDependencyIdentity,
        string SourceDependencySemanticVersion,
        string DerivationContractIdentity);

    internal sealed record LegendDependencyInventoryProgress(
        int ProcessedFamilyCount,
        Guid? LastFamilyId);

    /// <summary>
    /// Replays retained same-family Founder semantic-transition declarations
    /// through the one canonical transition persistence authority.
    ///
    /// The retained transition evidence is the durable declaration projection:
    /// its source/result frames came directly from the Founder-approved
    /// submission. Historical replay may expand that already-governed
    /// declaration across the family's current compatible examples, but it
    /// must never infer a new transition, manufacture a frame, or consume
    /// cross-example relationship-derived transitions owned by their separate
    /// canonical reconciliation authority.
    /// </summary>
    private async Task ReconcileFounderSemanticTransitionEvidenceAsync(
        Guid familyId,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var family = await _db.Set<LegendCurriculumFamily>()
            .SingleOrDefaultAsync(
                item => item.Id == familyId &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved,
                cancellationToken);
        if (family is null)
            return;

        var examples = await _db.Set<LegendCurriculumExample>()
            .Where(item =>
                item.CurriculumFamilyId == familyId &&
                item.LanguageCode == languageCode &&
                item.DerivedFromCurriculumExampleId == null &&
                item.SupersededUtc == null &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);

        if (examples.Count < 2)
            return;

        var exampleIds = examples.Select(item => item.Id).ToArray();

        var retainedFrames = await _db.Set<LegendSemanticTransitionEvidence>()
            .AsNoTracking()
            .Where(item =>
                item.SupersededUtc == null &&
                item.FounderSemanticExampleRelationEvidenceId == null &&
                item.SourceLanguageCode == languageCode &&
                item.ResultLanguageCode == languageCode &&
                exampleIds.Contains(item.SourceCurriculumExampleId) &&
                exampleIds.Contains(item.ResultCurriculumExampleId) &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new
            {
                item.SourceSemanticFrame,
                item.ResultSemanticFrame
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (retainedFrames.Count == 0)
            return;

        var transitions = new List<NormalizedSemanticTransition>(
            retainedFrames.Count);

        foreach (var retained in retainedFrames)
        {
            if (!TryReadSemanticFrame(
                    retained.SourceSemanticFrame,
                    out var sourceFrame) ||
                !TryReadSemanticFrame(
                    retained.ResultSemanticFrame,
                    out var resultFrame))
            {
                throw new InvalidOperationException(
                    "Retained Founder semantic-transition evidence contains an invalid governed frame.");
            }

            if (string.Equals(
                    sourceFrame.Signature,
                    resultFrame.Signature,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Retained Founder semantic-transition evidence collapsed to an identical source/result frame.");
            }

            transitions.Add(
                new NormalizedSemanticTransition(
                    sourceFrame,
                    resultFrame));
        }

        await PersistGovernedSemanticTransitionEvidenceAsync(
            family,
            examples,
            transitions,
            languageCode,
            knownVariationsByExample: null,
            knownNewCurriculumExampleIds: null,
            provenance: LegendConnectKnowledgeProvenance.FounderApproved,
            isHumanVerifiedSupport: true,
            cancellationToken: cancellationToken);
    }

    private async Task ReevaluateHistoricalSourceFamilyLanguageAsync(
        Guid familyId,
        string languageCode,
        CancellationToken cancellationToken)
    {
        await ReconcileFounderApprovedSourceEvidenceAsync(
            familyId,
            languageCode,
            cancellationToken);

        // Same-family transition declarations are ordinarily materialized
        // during Founder ingestion. Historical SourceFamilies replay must pass
        // retained declarations through that exact authority as well so an
        // evaluator revision or newly reconciled source example cannot leave
        // old curriculum permanently under-materialized.
        await ReconcileFounderSemanticTransitionEvidenceAsync(
            familyId,
            languageCode,
            cancellationToken);

        await AnalyzeFamilyLanguageAsync(
            familyId,
            languageCode,
            pairKey: null,
            cancellationToken);

        await ReconcileFounderMeaningPrimitivesAsync(
            familyId,
            languageCode,
            cancellationToken);

        // Cross-example semantic relationships retain and replay their own
        // governed declaration evidence. They intentionally remain separate
        // from the same-family declaration reconciliation above.
        await ReconcileFounderCrossExampleSemanticRelationsAsync(
            familyId,
            languageCode,
            cancellationToken);
    }

    private async Task ReevaluateHistoricalAlignmentAsync(
        Guid alignmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            await AttachAlignmentToCurriculumAsync(alignmentId, cancellationToken);
        }
        catch (LegendControlledVariationConflictException exception)
        {
            // This is a historical data contradiction, not an instruction to
            // choose one controlled value. Discard the incomplete attachment,
            // retain canonical rows, and make the exact alignment reviewable.
            _db.ChangeTracker.Clear();
            await RecordHistoricalAlignmentConflictAsync(
                alignmentId,
                exception,
                cancellationToken);
        }
    }

    private async Task AttachAlignmentToCurriculumAsync(
        Guid alignmentId,
        CancellationToken cancellationToken)
    {
        var alignment = await _db.Set<LegendTranslationAlignment>()
            .SingleOrDefaultAsync(item => item.Id == alignmentId && item.SupersededUtc == null, cancellationToken);
        if (alignment is null)
            return;
        var pair = await _db.Set<LegendLanguagePair>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.PairKey == alignment.PairKey && item.IsEnabled, cancellationToken);
        if (pair is null)
            return;
        await ReconcileRetiredTargetRealizationCandidatesAsync(pair.PairKey, cancellationToken);
        var source = await _db.Set<LegendLanguageTextUnit>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == alignment.SourceTextUnitId && item.IsTrainingEligible, cancellationToken);
        var target = await _db.Set<LegendLanguageTextUnit>()
            .SingleOrDefaultAsync(item => item.Id == alignment.TargetTextUnitId &&
                item.LanguageCode == pair.TargetLanguageCode && item.IsTrainingEligible, cancellationToken);
        if (source is null || target is null)
            return;

        var sourceExamples = await _db.Set<LegendCurriculumExample>()
            .Where(item => item.TextUnitId == source.Id && item.LanguageCode == pair.SourceLanguageCode &&
                item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        if (sourceExamples.Count == 0)
            return;

        var affectedFamilies = new HashSet<Guid>();
        foreach (var sourceExample in sourceExamples)
        {
            var family = await _db.Set<LegendCurriculumFamily>()
                .SingleAsync(item => item.Id == sourceExample.CurriculumFamilyId, cancellationToken);
            var targetExample = await GetOrCreateExampleAsync(
                family,
                target,
                pair.TargetLanguageCode,
                sourceExample.Id,
                cancellationToken);
            await CopyVariationsAsync(sourceExample, targetExample, cancellationToken);
            affectedFamilies.Add(family.Id);
        }
        await _db.SaveChangesAsync(cancellationToken);
        await EnsureLanguageLexicalObservationsAsync(
            [new AtomicInput(target, "DirectionalExpansion", null, null, null)],
            pair.TargetLanguageCode,
            cancellationToken);

        foreach (var familyId in affectedFamilies)
            await AnalyzeFamilyLanguageAsync(familyId, pair.TargetLanguageCode, pair.PairKey, cancellationToken);
    }

    /// <summary>
    /// Explicit validation is deliberately separate from automatic evidence
    /// accumulation. Even a validated pattern remains production-ineligible
    /// until a future composition authority is independently approved.
    /// </summary>
    internal async Task<bool> TryValidatePatternAsync(
        Guid patternId,
        CancellationToken cancellationToken = default)
    {
        var pattern = await _db.Set<LegendLanguageStructuralPattern>()
            .SingleOrDefaultAsync(item => item.Id == patternId && item.SupersededUtc == null, cancellationToken);
        if (pattern is null || pattern.SupportCount < 3 || pattern.IndependentSourceCount < 3 ||
            pattern.ContradictionCount != 0 || pattern.HumanVerifiedSupportCount == 0)
            return false;

        pattern.MaturityState = "Validated";
        pattern.IsProductionEligible = IsPatternProductionEligible(pattern);
        pattern.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Stable router boundary for future evidence-backed composition. Phase 2
    /// records and validates observations but deliberately has no formulation
    /// engine, so Azure remains the fallback even if a pattern is supported or
    /// explicitly validated. This prevents structural evidence from being
    /// mistaken for executable grammar.
    /// </summary>
    /// <summary>
    /// Formulates an unseen target candidate in shadow mode from:
    /// Phase 4A source semantics
    /// + existing Founder-verified directional target realizations
    /// + their existing observed target positions.
    ///
    /// No provider is consulted, no row is persisted, and this result cannot
    /// cross the production TryComposeAsync boundary.
    /// </summary>
    internal async Task<LegendShadowTargetFormulation> FormulateShadowTargetAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string sourceText,
        CancellationToken cancellationToken = default)
    {
        var understanding = await AnalyzeShadowSourceSemanticsAsync(
            sourceLanguageCode,
            sourceText,
            cancellationToken);

        if (!string.Equals(
                understanding.State,
                LegendShadowSourceUnderstanding.SupportedForShadowEvaluation,
                StringComparison.Ordinal))
        {
            return new LegendShadowTargetFormulation(
                string.Equals(
                    understanding.State,
                    LegendShadowSourceUnderstanding.Ambiguous,
                    StringComparison.Ordinal)
                    ? LegendShadowTargetFormulation.Ambiguous
                    : LegendShadowTargetFormulation.InsufficientEvidence,
                null,
                false,
                [],
                understanding.Reasons);
        }

        var source = await _languages.NormalizeEnabledTranslationLanguageAsync(
            sourceLanguageCode,
            cancellationToken);
        var target = await _languages.NormalizeEnabledTranslationLanguageAsync(
            targetLanguageCode,
            cancellationToken);

        if (source is null || target is null)
        {
            return new LegendShadowTargetFormulation(
                LegendShadowTargetFormulation.InsufficientEvidence,
                null,
                false,
                [],
                ["source_or_target_language_unavailable"]);
        }

        var pairKey = LegendLanguageIdentity.PairKey(source, target);
        var resolved = new List<LegendShadowTargetRealization>();

        foreach (var component in understanding.Components)
        {
            var candidates = await _db
                .Set<LegendLanguageTargetRealizationCandidate>()
                .AsNoTracking()
                .Where(item =>
                    item.PairKey == pairKey &&
                    item.SemanticSignature == component.SemanticSignature &&
                    item.SupersededUtc == null &&
                    item.RejectedUtc == null &&
                    item.VerificationState == "FounderVerified" &&
                    item.MaturityState == "Supported" &&
                    item.HumanVerifiedSupportCount >= 3 &&
                    item.ProviderOnlySupportCount == 0 &&
                    item.ContradictionCount == 0 &&
                    item.IsProductionEligible &&
                    item.VerifiedAnchorId != null)
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0)
            {
                return new LegendShadowTargetFormulation(
                    LegendShadowTargetFormulation.InsufficientEvidence,
                    null,
                    false,
                    resolved,
                    [$"missing_target_realization:{component.Dimension}:{component.Value}"]);
            }

            var candidateIds = candidates
                .Select(item => item.Id)
                .ToArray();

            var evidence = await _db
                .Set<LegendLanguageTargetRealizationEvidence>()
                .AsNoTracking()
                .Where(item =>
                    candidateIds.Contains(item.CandidateId) &&
                    item.SupersededUtc == null &&
                    item.IsHumanVerifiedSupport &&
                    item.Provenance == "FounderApproved")
                .ToListAsync(cancellationToken);

            if (evidence.Count == 0)
            {
                return new LegendShadowTargetFormulation(
                    LegendShadowTargetFormulation.InsufficientEvidence,
                    null,
                    false,
                    resolved,
                    [$"missing_founder_target_evidence:{component.Dimension}:{component.Value}"]);
            }

            var supportedCandidateIds = evidence
                .Select(item => item.CandidateId)
                .Distinct()
                .ToHashSet();

            var supportedCandidates = candidates
                .Where(item => supportedCandidateIds.Contains(item.Id))
                .ToList();

            var realizedSurfaces = supportedCandidates
                .Select(item => item.TargetRealization.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (realizedSurfaces.Length != 1)
            {
                return new LegendShadowTargetFormulation(
                    LegendShadowTargetFormulation.Ambiguous,
                    null,
                    false,
                    resolved,
                    [$"ambiguous_target_realization:{component.Dimension}:{component.Value}"]);
            }

            var selectedCandidateIds = supportedCandidates
                .Where(item =>
                    string.Equals(
                        item.TargetRealization.Trim(),
                        realizedSurfaces[0],
                        StringComparison.Ordinal))
                .Select(item => item.Id)
                .ToHashSet();

            evidence = evidence
                .Where(item => selectedCandidateIds.Contains(item.CandidateId))
                .ToList();

            var tokenLengths = evidence
                .Select(item => item.TargetTokenLength)
                .Distinct()
                .ToArray();

            // Founder-backed realization boundaries must agree on span size.
            // Absolute sentence position is not semantic identity: legitimate
            // target syntax may place the same realization differently.
            if (tokenLengths.Length != 1 ||
                tokenLengths[0] < 1)
            {
                return new LegendShadowTargetFormulation(
                    LegendShadowTargetFormulation.Ambiguous,
                    null,
                    false,
                    resolved,
                    [$"ambiguous_target_boundary:{component.Dimension}:{component.Value}"]);
            }

            var observedStarts = evidence
                .Select(item => item.TargetStartTokenIndex)
                .Distinct()
                .OrderBy(item => item)
                .ToArray();

            if (observedStarts.Length == 0)
            {
                return new LegendShadowTargetFormulation(
                    LegendShadowTargetFormulation.InsufficientEvidence,
                    null,
                    false,
                    resolved,
                    [$"missing_target_position_evidence:{component.Dimension}:{component.Value}"]);
            }

            resolved.Add(
                new LegendShadowTargetRealization(
                    component.Dimension,
                    component.Value,
                    component.SemanticSignature,
                    realizedSurfaces[0],
                    observedStarts[0],
                    tokenLengths[0]));
        }

        if (resolved.Count != understanding.Components.Count)
        {
            return new LegendShadowTargetFormulation(
                LegendShadowTargetFormulation.InsufficientEvidence,
                null,
                false,
                resolved,
                ["incomplete_target_semantic_coverage"]);
        }

        var ordered = resolved
            .OrderBy(item => item.ObservedTargetStartTokenIndex)
            .ToArray();

        if (ordered
            .GroupBy(item => item.ObservedTargetStartTokenIndex)
            .Any(group => group.Count() != 1))
        {
            return new LegendShadowTargetFormulation(
                LegendShadowTargetFormulation.Ambiguous,
                null,
                false,
                resolved,
                ["overlapping_target_positions"]);
        }

        for (var index = 1; index < ordered.Length; index++)
        {
            var previousEnd =
                ordered[index - 1].ObservedTargetStartTokenIndex +
                ordered[index - 1].ObservedTargetTokenLength;

            if (ordered[index].ObservedTargetStartTokenIndex != previousEnd)
            {
                return new LegendShadowTargetFormulation(
                    LegendShadowTargetFormulation.InsufficientEvidence,
                    null,
                    false,
                    resolved,
                    ["target_arrangement_not_contiguously_proven"]);
            }
        }

        var formulated = string.Join(
            " ",
            ordered.Select(item => item.SurfaceForm));

        if (string.IsNullOrWhiteSpace(formulated))
        {
            return new LegendShadowTargetFormulation(
                LegendShadowTargetFormulation.InsufficientEvidence,
                null,
                false,
                resolved,
                ["empty_target_formulation"]);
        }

        return new LegendShadowTargetFormulation(
            LegendShadowTargetFormulation.SupportedForShadowEvaluation,
            formulated,
            false,
            resolved,
            ["target_formulated_from_verified_directional_evidence"]);
    }

    public async Task<LegendContextualTranslationSuggestion?> TryComposeAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default)
    {
        var source =
            await _languages.NormalizeEnabledTranslationLanguageAsync(
                sourceLanguageCode,
                cancellationToken);

        var target =
            await _languages.NormalizeEnabledTranslationLanguageAsync(
                targetLanguageCode,
                cancellationToken);

        if (source is null ||
            target is null ||
            string.Equals(
                source,
                target,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var units =
            LegendFounderTrainingSegmenter
                .SegmentForComposition(text);

        // Eight bounded units × the existing 24-component atomic
        // authority gives a maximum of 192 surface components without
        // weakening the atomic evidence gate.
        if (units.Count is < 1 or > 8)
            return null;

        var outputs = new List<string>(units.Count);

        foreach (var unit in units)
        {
            var surfaceCount =
                SurfaceComponents(unit.Text).Count;

            if (surfaceCount is < 1 or > 24)
                return null;

            var atomic =
                await TryComposeAtomicAsync(
                    source,
                    target,
                    unit.Text,
                    cancellationToken);

            if (atomic is null ||
                string.IsNullOrWhiteSpace(atomic.Text))
            {
                return null;
            }

            outputs.Add(atomic.Text.Trim());
        }

        var composed =
            string.Join(
                " ",
                outputs);

        return string.IsNullOrWhiteSpace(composed)
            ? null
            : new LegendContextualTranslationSuggestion(
                composed,
                1.0m);
    }

    private async Task<LegendContextualTranslationSuggestion?>
        TryComposeAtomicAsync(
            string source,
            string target,
            string text,
            CancellationToken cancellationToken)
    {
        // Reuse the existing Phase 4B formulation authority. Every unit
        // independently requires complete source semantics, verified
        // directional realizations, contradiction-free evidence, and the
        // existing production structural authority.
        var formulation =
            await FormulateShadowTargetAsync(
                source,
                target,
                text,
                cancellationToken);

        if (!string.Equals(
                formulation.State,
                LegendShadowTargetFormulation
                    .SupportedForShadowEvaluation,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(
                formulation.Text) ||
            formulation.Realizations.Count == 0)
        {
            return null;
        }

        var pairKey =
            LegendLanguageIdentity.PairKey(
                source,
                target);

        var hasProductionStructuralAuthority =
            await _db
                .Set<LegendLanguageStructuralRelationship>()
                .AsNoTracking()
                .AnyAsync(
                    item =>
                        item.PairKey == pairKey &&
                        item.LanguageCode == target &&
                        item.SupersededUtc == null &&
                        (item.MaturityState == "Supported" ||
                         item.MaturityState == "Validated") &&
                        item.SupportCount >= 3 &&
                        item.IndependentSourceCount >= 3 &&
                        item.HumanVerifiedSupportCount >= 3 &&
                        item.ProviderOnlySupportCount == 0 &&
                        item.ContradictionCount == 0 &&
                        item.IsProductionEligible,
                    cancellationToken);

        if (!hasProductionStructuralAuthority)
            return null;

        return new LegendContextualTranslationSuggestion(
            formulation.Text,
            1.0m);
    }

    /// <summary>
    /// Founder-safe read projection of the candidate aggregate created by the
    /// existing controlled cross-example evaluator. It exposes only retained
    /// curriculum and alignment assets, never private message content.
    /// </summary>
    internal async Task<LegendTargetRealizationReviewSnapshot> GetTargetRealizationReviewAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = await _db.Set<LegendLanguageTargetRealizationCandidate>()
            .AsNoTracking()
            .Where(item => item.SupersededUtc == null)
            .OrderBy(item => item.VerificationState == "Candidate" || item.VerificationState == "Contradicted" ? 0 : 1)
            .ThenByDescending(item => item.UpdatedUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
        var candidateIds = candidates.Select(item => item.Id).ToArray();
        var evidence = candidateIds.Length == 0
            ? []
            : await (
                from item in _db.Set<LegendLanguageTargetRealizationEvidence>().AsNoTracking()
                join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking() on item.SourceTextUnitId equals source.Id
                join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking() on item.TargetTextUnitId equals target.Id
                where candidateIds.Contains(item.CandidateId) && item.SupersededUtc == null
                orderby item.CreatedUtc
                select new { item, SourceText = source.Text, TargetText = target.Text }
            ).ToListAsync(cancellationToken);
        var evidenceByCandidate = evidence.GroupBy(item => item.item.CandidateId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<LegendTargetRealizationEvidenceSnapshot>)group
                .Take(12)
                .Select(item => new LegendTargetRealizationEvidenceSnapshot(
                    item.item.Id,
                    item.SourceText,
                    item.TargetText,
                    item.item.TargetStartTokenIndex,
                    item.item.TargetTokenLength,
                    item.item.IsHumanVerifiedSupport,
                    item.item.Provenance))
                .ToList());
        return new LegendTargetRealizationReviewSnapshot(
            await _db.Set<LegendLanguageTargetRealizationCandidate>().LongCountAsync(item => item.SupersededUtc == null, cancellationToken),
            await _db.Set<LegendLanguageTargetRealizationCandidate>().LongCountAsync(item => item.SupersededUtc == null && item.VerificationState == "FounderVerified", cancellationToken),
            await _db.Set<LegendLanguageTargetRealizationCandidate>().LongCountAsync(item => item.SupersededUtc == null && item.VerificationState == "Rejected", cancellationToken),
            await _db.Set<LegendLanguageTargetRealizationCandidate>().LongCountAsync(item => item.ContradictionCount > 0 && item.SupersededUtc == null, cancellationToken),
            candidates.Select(item =>
            {
                var candidateEvidence = evidenceByCandidate.GetValueOrDefault(item.Id, []);
                var representative = candidateEvidence.FirstOrDefault();
                return new LegendTargetRealizationCandidateSnapshot(
                    item.Id,
                    item.PairKey,
                    item.SourceLanguageCode,
                    item.TargetLanguageCode,
                    item.VariationDimension,
                    item.SemanticValue,
                    item.TargetRealization,
                    item.SlotSignature,
                    representative is null
                        ? "No active template evidence"
                        : TargetTemplatePreview(
                            representative.TargetText,
                            representative.TargetStartTokenIndex,
                            representative.TargetTokenLength,
                            item.VariationDimension),
                    item.VerificationState,
                    item.MaturityState,
                    item.SupportCount,
                    item.IndependentSourceCount,
                    item.HumanVerifiedSupportCount,
                    item.ProviderOnlySupportCount,
                    item.ContradictionCount,
                    item.Confidence,
                    item.IsProductionEligible,
                    candidateEvidence);
            })
                .ToList());
    }

    internal async Task<LegendTargetRealizationReviewActionResult> VerifyTargetRealizationCandidateAsync(
        string founderUserId,
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await _db.Set<LegendLanguageTargetRealizationCandidate>()
            .SingleOrDefaultAsync(item => item.Id == candidateId, cancellationToken);
        if (candidate is null || candidate.SupersededUtc is not null)
            return TargetRealizationActionFailure(candidateId, "candidate_not_found", "The target-realization candidate is unavailable.");
        if (candidate.VerificationState == "Rejected")
            return TargetRealizationActionFailure(candidateId, "candidate_rejected", "Rejected candidates remain historical and cannot be verified again.");

        await RefreshTargetRealizationCandidateAsync(candidateId, cancellationToken);
        if (candidate.ContradictionCount > 0)
            return TargetRealizationActionFailure(candidateId, "candidate_contradicted", "Conflicting target realizations remain unresolved; Founder verification fails closed.");

        var evidence = await _db.Set<LegendLanguageTargetRealizationEvidence>()
            .Where(item => item.CandidateId == candidateId && item.SupersededUtc == null)
            .OrderBy(item => item.CreatedUtc)
            .ToListAsync(cancellationToken);
        if (evidence.Count == 0)
            return TargetRealizationActionFailure(candidateId, "candidate_without_active_evidence", "The candidate no longer has active directional evidence.");

        // An anchor is scoped to its exact target curriculum example. Creating
        // the established anchor projection for every independently supported
        // instance makes the target realization available to the existing
        // reusable-relationship evaluator when complete, non-overlapping
        // component coverage is independently established.
        LegendLanguageCompositionalAnchor? representativeAnchor = null;
        var affectedFamilies = new HashSet<Guid>();
        foreach (var item in evidence)
        {
            var anchor = await GetOrCreateVerifiedTargetAnchorAsync(candidate, item, cancellationToken);
            representativeAnchor ??= anchor;
            affectedFamilies.Add(anchor.CurriculumFamilyId);
        }

        candidate.VerificationState = "FounderVerified";
        candidate.VerifiedAnchorId = representativeAnchor!.Id;
        candidate.VerifiedUtc = DateTime.UtcNow;
        candidate.VerifiedByFounderUserId = founderUserId;
        candidate.IsProductionEligible = false;
        candidate.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        foreach (var familyId in affectedFamilies)
        {
            await AnalyzeFamilyLanguageAsync(
                familyId,
                candidate.TargetLanguageCode,
                candidate.PairKey,
                cancellationToken);
        }
        await RefreshTargetRealizationCandidateAsync(candidateId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return new LegendTargetRealizationReviewActionResult(
            true,
            null,
            "Founder verification created one trusted canonical target anchor. Existing evidence still determines maturity; production composition remains closed.",
            candidate.Id,
            candidate.VerificationState,
            candidate.VerifiedAnchorId);
    }

    private async Task<LegendLanguageCompositionalAnchor> GetOrCreateVerifiedTargetAnchorAsync(
        LegendLanguageTargetRealizationCandidate candidate,
        LegendLanguageTargetRealizationEvidence evidence,
        CancellationToken cancellationToken)
    {
        var anchorSignature = LegendLanguageIdentity.TextHash(
            $"verified-target-realization|{candidate.Id:D}|{evidence.TargetCurriculumExampleId:D}|{evidence.TargetStartTokenIndex}:{evidence.TargetTokenLength}");
        var existing = _db.Set<LegendLanguageCompositionalAnchor>().Local
            .SingleOrDefault(item => item.CurriculumExampleId == evidence.TargetCurriculumExampleId &&
                item.AnchorSignature == anchorSignature)
            ?? await _db.Set<LegendLanguageCompositionalAnchor>()
                .SingleOrDefaultAsync(item => item.CurriculumExampleId == evidence.TargetCurriculumExampleId &&
                    item.AnchorSignature == anchorSignature, cancellationToken);
        if (existing is not null)
        {
            existing.SupersededUtc = null;
            return existing;
        }

        var equivalent = await _db.Set<LegendLanguageCompositionalAnchor>()
            .Where(item =>
                item.LanguageCode == candidate.TargetLanguageCode &&
                item.PairKey == candidate.PairKey &&
                item.TextUnitId == evidence.TargetTextUnitId &&
                item.CurriculumExampleId == evidence.TargetCurriculumExampleId &&
                item.Dimension == candidate.VariationDimension &&
                item.SemanticSignature == candidate.SemanticSignature &&
                item.ComponentStartTokenIndex == evidence.TargetStartTokenIndex &&
                item.ComponentLength == evidence.TargetTokenLength &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                item.SupersededUtc == null)
            .OrderBy(item => item.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (equivalent is not null)
            return equivalent;

        var occurrence = await _db.Set<LegendLanguageLexicalOccurrence>()
            .AsNoTracking()
            .Where(item => item.TextUnitId == evidence.TargetTextUnitId &&
                item.TokenIndex == evidence.TargetStartTokenIndex && item.SupersededUtc == null)
            .Select(item => item.LexemeId)
            .SingleOrDefaultAsync(cancellationToken);
        var familyId = await _db.Set<LegendCurriculumExample>()
            .Where(item => item.Id == evidence.TargetCurriculumExampleId)
            .Select(item => item.CurriculumFamilyId)
            .SingleAsync(cancellationToken);
        var anchor = new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(),
            LanguageCode = candidate.TargetLanguageCode,
            PairKey = candidate.PairKey,
            TextUnitId = evidence.TargetTextUnitId,
            LexemeId = occurrence == Guid.Empty ? null : occurrence,
            ComponentStartTokenIndex = evidence.TargetStartTokenIndex,
            ComponentLength = evidence.TargetTokenLength,
            CurriculumFamilyId = familyId,
            CurriculumExampleId = evidence.TargetCurriculumExampleId,
            Dimension = candidate.VariationDimension,
            Value = candidate.SemanticValue,
            SemanticSignature = candidate.SemanticSignature,
            AnchorSignature = anchorSignature,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
            CreatedUtc = DateTime.UtcNow
        };
        return await LegendConnectCanonicalCurriculumPersistence.AdmitCompositionalAnchorAsync(
            _db,
            anchor,
            cancellationToken);
    }

    internal async Task<LegendTargetRealizationReviewActionResult> RejectTargetRealizationCandidateAsync(
        string founderUserId,
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await _db.Set<LegendLanguageTargetRealizationCandidate>()
            .SingleOrDefaultAsync(item => item.Id == candidateId, cancellationToken);
        if (candidate is null || candidate.SupersededUtc is not null)
            return TargetRealizationActionFailure(candidateId, "candidate_not_found", "The target-realization candidate is unavailable.");
        if (candidate.VerificationState == "FounderVerified")
            return TargetRealizationActionFailure(candidateId, "candidate_already_verified", "A verified realization must be changed through the existing Founder correction path.");

        candidate.VerificationState = "Rejected";
        candidate.MaturityState = "Superseded";
        candidate.IsProductionEligible = false;
        candidate.RejectedUtc = DateTime.UtcNow;
        candidate.RejectedByFounderUserId = founderUserId;
        candidate.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return new LegendTargetRealizationReviewActionResult(
            true,
            null,
            "The candidate was rejected. Its evidence remains retained for audit and cannot become trusted.",
            candidate.Id,
            candidate.VerificationState,
            null);
    }

    private static LegendTargetRealizationReviewActionResult TargetRealizationActionFailure(
        Guid candidateId,
        string errorCode,
        string message) => new(false, errorCode, message, candidateId, "Unavailable", null);

    /// <summary>
    /// Independently recognizes semantic components in an unseen source
    /// sentence from the same Founder-approved compositional anchors already
    /// owned by this curriculum authority.
    ///
    /// Language identity controls which evidence is queried; there is no
    /// language-specific grammar branch. This method is deliberately read-only
    /// and cannot formulate or serve a translation.
    /// </summary>
    public Task<LegendShadowSourceUnderstanding> AnalyzeShadowSourceSemanticsAsync(
        string sourceLanguageCode,
        string text,
        CancellationToken cancellationToken = default) =>
        AnalyzeSourceSemanticsAsync(
            sourceLanguageCode,
            text,
            semanticTransitionSourceOnly: false,
            cancellationToken);

    internal Task<LegendShadowSourceUnderstanding> AnalyzeSemanticTransitionSourceSemanticsAsync(
        string sourceLanguageCode,
        string text,
        CancellationToken cancellationToken = default) =>
        AnalyzeSourceSemanticsAsync(
            sourceLanguageCode,
            text,
            semanticTransitionSourceOnly: true,
            cancellationToken);

    /// <summary>
    /// Uses the existing semantic analyser with the additional authority scope
    /// required by conversational serving: an anchor must originate from a
    /// currently governed transition source example. Result-side curriculum
    /// evidence cannot reinterpret a user utterance merely because it shares
    /// a literal surface form.
    /// </summary>
    private async Task<LegendShadowSourceUnderstanding> AnalyzeSourceSemanticsAsync(
        string sourceLanguageCode,
        string text,
        bool semanticTransitionSourceOnly,
        CancellationToken cancellationToken = default)
    {
        var sourceLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(
            sourceLanguageCode,
            cancellationToken);

        if (sourceLanguage is null)
        {
            return new LegendShadowSourceUnderstanding(
                LegendShadowSourceUnderstanding.InsufficientEvidence,
                false,
                [],
                ["invalid_source_language"]);
        }

        var normalizedText = LegendLanguageIdentity.NormalizeText(text);
        var sourceComponents = SurfaceComponents(normalizedText);

        if (string.IsNullOrWhiteSpace(normalizedText) ||
            sourceComponents.Count is < 1 or > 24)
        {
            return new LegendShadowSourceUnderstanding(
                LegendShadowSourceUnderstanding.InsufficientEvidence,
                false,
                [],
                ["invalid_source_text"]);
        }

        var proofs = await (
            from anchor in _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on anchor.TextUnitId equals unit.Id
            where anchor.LanguageCode == sourceLanguage &&
                unit.LanguageCode == sourceLanguage &&
                unit.IsTrainingEligible &&
                anchor.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                anchor.SupersededUtc == null &&
                anchor.LexemeId != null &&
                anchor.SemanticSignature != null &&
                anchor.SemanticSignature != string.Empty &&
                anchor.ComponentStartTokenIndex != null &&
                anchor.ComponentLength != null &&
                anchor.ComponentLength > 0 &&
                (!semanticTransitionSourceOnly ||
                 _db.Set<LegendSemanticTransitionEvidence>().Any(evidence =>
                     evidence.SourceCurriculumExampleId == anchor.CurriculumExampleId &&
                     evidence.SupersededUtc == null &&
                     evidence.ContributionState == "Supported" &&
                     evidence.IsHumanVerifiedSupport &&
                     evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                     evidence.SourceLanguageCode == sourceLanguage &&
                     evidence.ResultLanguageCode == sourceLanguage))
            select new
            {
                anchor.Dimension,
                anchor.Value,
                SemanticSignature = anchor.SemanticSignature!,
                StartTokenIndex = anchor.ComponentStartTokenIndex!.Value,
                TokenLength = anchor.ComponentLength!.Value,
                anchor.CurriculumExampleId,
                unit.Text
            }
        ).ToListAsync(cancellationToken);

        var inputTokens = sourceComponents
            .Select(item => item.NormalizedText)
            .ToArray();

        var candidates = new List<ShadowSourceSemanticCandidate>();

        foreach (var proof in proofs)
        {
            var proofComponents = SurfaceComponents(proof.Text);

            if (proof.StartTokenIndex < 0 ||
                proof.TokenLength < 1 ||
                proof.StartTokenIndex + proof.TokenLength > proofComponents.Count)
            {
                continue;
            }

            var proofTokens = proofComponents
                .Skip(proof.StartTokenIndex)
                .Take(proof.TokenLength)
                .Select(item => item.NormalizedText)
                .ToArray();

            if (proofTokens.Length == 0)
                continue;

            foreach (var start in FindTokenSequenceOccurrences(
                         inputTokens,
                         proofTokens))
            {
                candidates.Add(new ShadowSourceSemanticCandidate(
                    proof.Dimension,
                    proof.Value,
                    string.Join(
                        ' ',
                        inputTokens
                            .Skip(start)
                            .Take(proofTokens.Length)),
                    start,
                    proofTokens.Length,
                    proof.SemanticSignature,
                    [proof.CurriculumExampleId]));
            }
        }

        // Historical Founder curriculum may already have a production-eligible
        // controlled source frame while a later replay has not yet persisted
        // its lexical projection.  Consult that same direct Founder endpoint
        // authority here as read-only evidence. The reconciliation lifecycle
        // persists the identical projection for scalable future serving; this
        // bounded path prevents a completed earlier evaluator from making
        // otherwise governed canonical knowledge temporarily invisible.
        candidates.AddRange(
            await ReadHistoricalSourceFrameProjectionCandidatesAsync(
                sourceLanguage,
                normalizedText,
                inputTokens,
                cancellationToken));

        var distinctCandidates = candidates
            .GroupBy(item => (
                item.StartTokenIndex,
                item.TokenLength,
                item.SemanticSignature))
            .Select(group =>
            {
                var first = group.First();
                return first with
                {
                    CurriculumExampleIds = group
                        .SelectMany(item => item.CurriculumExampleIds)
                        .Distinct()
                        .OrderBy(item => item)
                        .ToArray(),
                    IsDirectFounderFrameProjection = group.Any(
                        item => item.IsDirectFounderFrameProjection)
                };
            })
            .ToList();

        if (distinctCandidates.Count == 0)
        {
            return new LegendShadowSourceUnderstanding(
                LegendShadowSourceUnderstanding.InsufficientEvidence,
                false,
                [],
                ["source_semantic_component_unknown"]);
        }

        var spanGroups = distinctCandidates
            .GroupBy(item => (
                item.StartTokenIndex,
                item.TokenLength))
            .Select(group => group
                .GroupBy(
                    item => item.SemanticSignature,
                    StringComparer.Ordinal)
                .Select(signatureGroup => signatureGroup.First())
                .ToList())
            .ToList();

        var resolvedCandidates =
            new List<ShadowSourceSemanticCandidate>();

        var unresolvedSpans =
            new List<List<ShadowSourceSemanticCandidate>>();

        foreach (var span in spanGroups)
        {
            if (span.Count == 1)
            {
                resolvedCandidates.Add(span[0]);
                continue;
            }

            if (await IsFounderCoRealizationAsync(
                    sourceLanguage,
                    span,
                    cancellationToken))
            {
                // These are simultaneous meanings realized by one fused
                // surface span, not competing interpretations.
                resolvedCandidates.AddRange(span);
                continue;
            }

            unresolvedSpans.Add(span);
        }

        while (unresolvedSpans.Count > 0)
        {
            var progressed = false;

            foreach (var span in unresolvedSpans.ToList())
            {
                // Two different Founder semantic identities explicitly attached
                // to the same training example remain a genuine contradiction.
                // Context from another example may not silently override it.
                var hasDirectFounderIdentityConflict = span
                    .SelectMany(candidate =>
                        candidate.CurriculumExampleIds.Select(exampleId =>
                            (
                                ExampleId: exampleId,
                                candidate.SemanticSignature)))
                    .GroupBy(item => item.ExampleId)
                    .Any(group => group
                        .Select(item => item.SemanticSignature)
                        .Distinct(StringComparer.Ordinal)
                        .Skip(1)
                        .Any());

                if (hasDirectFounderIdentityConflict)
                {
                    return new LegendShadowSourceUnderstanding(
                        LegendShadowSourceUnderstanding.Ambiguous,
                        false,
                        [],
                        ["ambiguous_source_semantic_identity"]);
                }

                var structurallySupported =
                    new List<ShadowSourceSemanticCandidate>();

                foreach (var candidate in span)
                {
                    var hypothesis = resolvedCandidates
                        .Append(candidate)
                        .OrderBy(item => item.StartTokenIndex)
                        .ThenBy(item => item.TokenLength)
                        .ThenBy(item => item.Dimension, StringComparer.Ordinal)
                        .ToList();

                    if (await HasSupportedSourceSemanticContextAsync(
                            sourceLanguage,
                            hypothesis,
                            cancellationToken))
                    {
                        structurallySupported.Add(candidate);
                    }
                }

                // Context is authoritative only when the existing Founder-backed
                // structural graph identifies exactly one remaining meaning.
                // Zero or multiple supported meanings continue to fail closed.
                if (structurallySupported.Count != 1)
                    continue;

                resolvedCandidates.Add(structurallySupported[0]);
                unresolvedSpans.Remove(span);
                progressed = true;
            }

            if (!progressed)
            {
                return new LegendShadowSourceUnderstanding(
                    LegendShadowSourceUnderstanding.Ambiguous,
                    false,
                    [],
                    ["ambiguous_source_semantic_identity"]);
            }
        }

        var segmentationOptions = resolvedCandidates
            .GroupBy(item => (
                item.StartTokenIndex,
                item.TokenLength))
            .Select(group => group
                .OrderBy(
                    item => item.Dimension,
                    StringComparer.Ordinal)
                .ThenBy(
                    item => item.SemanticSignature,
                    StringComparer.Ordinal)
                .ToList())
            .OrderBy(group =>
                group[0].StartTokenIndex)
            .ThenByDescending(group =>
                group[0].TokenLength)
            .ToList();

        var completeSegmentations =
            new List<IReadOnlyList<ShadowSourceSemanticCandidate>>();

        void FindCompleteSegmentations(
            int tokenIndex,
            List<ShadowSourceSemanticCandidate> selected)
        {
            // More than two complete interpretations already proves
            // structural ambiguity, so search remains bounded.
            if (completeSegmentations.Count > 1)
                return;

            if (tokenIndex == inputTokens.Length)
            {
                completeSegmentations.Add(selected.ToArray());
                return;
            }

            foreach (var option in segmentationOptions.Where(group =>
                         group[0].StartTokenIndex == tokenIndex))
            {
                selected.AddRange(option);

                FindCompleteSegmentations(
                    tokenIndex +
                        option[0].TokenLength,
                    selected);

                selected.RemoveRange(
                    selected.Count - option.Count,
                    option.Count);
            }
        }

        FindCompleteSegmentations(
            0,
            new List<ShadowSourceSemanticCandidate>());

        if (completeSegmentations.Count == 0)
        {
            return new LegendShadowSourceUnderstanding(
                LegendShadowSourceUnderstanding.InsufficientEvidence,
                false,
                [],
                ["source_semantic_component_unknown"]);
        }

        IReadOnlyList<ShadowSourceSemanticCandidate> uniqueSpans;

        if (completeSegmentations.Count == 1)
        {
            uniqueSpans = completeSegmentations[0];
        }
        else
        {
            // A Founder may explicitly ground a semantic frame both as a
            // fused span and as its constituent spans. The layouts are
            // alternate attestations, not alternate meanings, when every
            // complete controlled semantic frame is identical. Resolve that
            // exact equivalence before consulting structural layout support:
            // there is no semantic choice for that support to make.
            var completeSemanticFrames = completeSegmentations
                .Select(CanonicalSemanticFrameIdentity)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (completeSemanticFrames.Length == 1)
            {
                uniqueSpans = completeSegmentations
                    .OrderBy(CanonicalSourceSegmentationIdentity, StringComparer.Ordinal)
                    .First();
            }
            else
            {
                var structurallySupported =
                    new List<IReadOnlyList<ShadowSourceSemanticCandidate>>();

                foreach (var segmentation in completeSegmentations)
                {
                    if (await HasSupportedSourceSemanticContextAsync(
                            sourceLanguage,
                            segmentation,
                            cancellationToken))
                    {
                        structurallySupported.Add(segmentation);
                    }
                }

                if (structurallySupported.Count != 1)
                {
                    return new LegendShadowSourceUnderstanding(
                        LegendShadowSourceUnderstanding.Ambiguous,
                        false,
                        [],
                        ["ambiguous_source_semantic_structure"]);
                }

                uniqueSpans = structurallySupported[0];
            }
        }

        return new LegendShadowSourceUnderstanding(
            LegendShadowSourceUnderstanding.SupportedForShadowEvaluation,
            false,
            uniqueSpans
                .Select(item =>
                    new LegendShadowSourceSemanticComponent(
                        item.Dimension,
                        item.Value,
                        item.SurfaceForm,
                        item.StartTokenIndex,
                        item.TokenLength,
                        item.SemanticSignature))
                .ToList(),
            ["complete_founder_backed_source_semantic_coverage"]);
    }

    /// <summary>
    /// Evaluates a controlled source frame, selects exactly one sufficiently
    /// supported result frame, and realizes it only from active canonical
    /// anchors. This is a generic semantic-transition authority: it does not
    /// store conversations, match prompts to answers, call a provider, or use
    /// a language-specific response template.
    /// </summary>
    /// <summary>
    /// Observes reusable Founder-governed primitives in a novel surface form.
    /// It matches only independently supported exact component spans, never a
    /// complete stored sentence, adjacency, a synonym table, or a prompt map.
    /// This Stage-2 analysis cannot serve a response or promote an edge.
    /// </summary>
    internal async Task<LegendConnectUtteranceMeaningGraphSnapshot>
        AnalyzeReusableMeaningGraphAsync(
            string sourceLanguageCode,
            string input,
            CancellationToken cancellationToken = default)
    {
        var languageCode = await _languages.NormalizeEnabledTranslationLanguageAsync(
            sourceLanguageCode,
            cancellationToken);
        var normalizedInput = LegendLanguageIdentity.NormalizeText(input);
        var tokens = SurfaceComponents(normalizedInput);
        if (languageCode is null || tokens.Count == 0)
            return new(false, [], [], tokens.Select(item => item.NormalizedText).ToArray(), "meaning_graph_input_invalid");

        // Lexical anchors always retain the canonical lexeme at the first
        // token of their controlled span. Start from the exact lexemes present
        // in this request so inference never materializes every supported
        // anchor for an entire language before doing an in-memory comparison.
        var inputLexemeHashes = tokens
            .Select(item => item.NormalizedHash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var candidates = await (
            from node in _db.Set<LegendLanguageMeaningNodeEvidence>().AsNoTracking()
            join anchor in _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
                on node.CompositionalAnchorId equals anchor.Id
            join lexeme in _db.Set<LegendLanguageLexeme>().AsNoTracking()
                on anchor.LexemeId equals (Guid?)lexeme.Id
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on anchor.TextUnitId equals unit.Id
            join primitive in _db.Set<LegendLanguageMeaningPrimitive>().AsNoTracking()
                on new { node.LanguageCode, node.SemanticSignature }
                equals new { primitive.LanguageCode, primitive.SemanticSignature }
            where node.LanguageCode == languageCode && node.SupersededUtc == null &&
                anchor.SupersededUtc == null && unit.IsTrainingEligible &&
                anchor.ComponentStartTokenIndex != null && anchor.ComponentLength > 0 &&
                lexeme.LanguageCode == languageCode &&
                inputLexemeHashes.Contains(lexeme.NormalizedHash) &&
                primitive.SupersededUtc == null &&
                primitive.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                primitive.MaturityState != "Contradicted" &&
                primitive.ContradictionCount == 0 &&
                primitive.IndependentSourceCount >= 1 &&
                primitive.HumanVerifiedSupportCount >= 1
            select new ReusableMeaningAnchorCandidate(
                node.SemanticSignature,
                node.SemanticDimension,
                node.SemanticValue,
                primitive.IndependentSourceCount,
                primitive.HumanVerifiedSupportCount,
                primitive.MaturityState,
                unit.Text,
                anchor.ComponentStartTokenIndex,
                anchor.ComponentLength)
        ).ToListAsync(cancellationToken);

        // When the current surface is itself an active Founder endpoint, its
        // explicit graph is the strongest source-grounding authority. A dense
        // curriculum may also contain reusable anchors with identical token
        // spans but different meanings in other utterances; unioning those
        // unrelated observations manufactures an ambiguous supergraph.
        // Restrict only source-meaning candidates here. Response selection and
        // realization continue through their existing independent authorities.
        var exactSourceCandidates = candidates
            .Where(item => string.Equals(
                LegendLanguageIdentity.NormalizeText(item.Text),
                normalizedInput,
                StringComparison.Ordinal))
            .ToList();
        if (exactSourceCandidates.Count > 0)
            candidates = exactSourceCandidates;

        // If the same surface span has both mature and still-growing Founder
        // interpretations, retain only the highest evidence standard for that
        // span. Equal-standard disagreements remain visible and fail closed;
        // a broad interpretation can never dilute a higher-standard one.
        candidates = candidates
            .GroupBy(item => (item.StartTokenIndex, item.TokenLength))
            .SelectMany(group =>
            {
                var standard = group.Max(item => MeaningEvidenceStandard(
                    item.IndependentSupportCount,
                    item.HumanVerifiedSupportCount,
                    item.MaturityState));
                return group.Where(item => MeaningEvidenceStandard(
                    item.IndependentSupportCount,
                    item.HumanVerifiedSupportCount,
                    item.MaturityState) == standard);
            })
            .ToList();

        var nodes = new List<LegendConnectUtteranceMeaningNode>();
        foreach (var candidate in candidates)
        {
            if (candidate.StartTokenIndex is not int start || candidate.TokenLength is not int length || length <= 0)
                continue;
            var sourceTokens = SurfaceComponents(candidate.Text);
            if (start < 0 || start + length > sourceTokens.Count)
                continue;
            var surface = sourceTokens.Skip(start).Take(length).Select(item => item.NormalizedText).ToArray();
            for (var inputStart = 0; inputStart <= tokens.Count - surface.Length; inputStart++)
            {
                if (!surface.SequenceEqual(tokens.Skip(inputStart).Take(surface.Length).Select(item => item.NormalizedText), StringComparer.Ordinal))
                    continue;
                var node = new LegendConnectUtteranceMeaningNode(
                    candidate.SemanticSignature,
                    candidate.SemanticDimension,
                    candidate.SemanticValue,
                    inputStart,
                    surface.Length,
                    candidate.IndependentSupportCount);
                if (!nodes.Any(existing => existing.SemanticSignature == node.SemanticSignature &&
                    existing.StartTokenIndex == node.StartTokenIndex && existing.TokenLength == node.TokenLength))
                {
                    nodes.Add(node);
                }
            }
        }

        var relations = new List<LegendConnectUtteranceMeaningRelation>();
        if (nodes.Count > 1)
        {
            var signatures = nodes.Select(item => item.SemanticSignature).Distinct(StringComparer.Ordinal).ToArray();
            var learnedRelations = await _db.Set<LegendLanguageMeaningRelation>().AsNoTracking()
                .Where(item => item.LanguageCode == languageCode && item.SupersededUtc == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    item.MaturityState != "Contradicted" && item.ContradictionCount == 0 &&
                    item.IndependentSourceCount >= 1 && item.HumanVerifiedSupportCount >= 1 &&
                    signatures.Contains(item.SourceSemanticSignature) &&
                    signatures.Contains(item.TargetSemanticSignature))
                .ToListAsync(cancellationToken);
            learnedRelations = learnedRelations
                .GroupBy(item => new
                {
                    item.SourceSemanticSignature,
                    item.TargetSemanticSignature
                })
                .SelectMany(group =>
                {
                    var standard = group.Max(item => MeaningEvidenceStandard(
                        item.IndependentSourceCount,
                        item.HumanVerifiedSupportCount,
                        item.MaturityState));
                    return group.Where(item => MeaningEvidenceStandard(
                        item.IndependentSourceCount,
                        item.HumanVerifiedSupportCount,
                        item.MaturityState) == standard);
                })
                .ToList();
            foreach (var relation in learnedRelations)
            {
                for (var sourceIndex = 0; sourceIndex < nodes.Count; sourceIndex++)
                for (var targetIndex = 0; targetIndex < nodes.Count; targetIndex++)
                {
                    if (sourceIndex == targetIndex ||
                        nodes[sourceIndex].SemanticSignature != relation.SourceSemanticSignature ||
                        nodes[targetIndex].SemanticSignature != relation.TargetSemanticSignature)
                    {
                        continue;
                    }
                    relations.Add(new LegendConnectUtteranceMeaningRelation(
                        relation.RelationSignature,
                        relation.RelationKind,
                        sourceIndex,
                        targetIndex,
                        relation.IndependentSourceCount));
                }
            }
        }

        var covered = new bool[tokens.Count];
        foreach (var node in nodes)
            for (var index = node.StartTokenIndex; index < node.StartTokenIndex + node.TokenLength; index++)
                covered[index] = true;
        var unknown = tokens.Where((_, index) => !covered[index]).Select(item => item.NormalizedText).ToArray();
        // Relations were discovered against the mutable candidate-order list.
        // The snapshot exposes canonical surface/semantic node order, so every
        // endpoint must be remapped before returning it. Retaining pre-sort
        // indexes can invert a directed governed relation depending on SQL
        // enumeration order, which is unsafe for discourse, planning, and
        // semantic-transition selection alike.
        var orderedNodeIndexes = Enumerable.Range(0, nodes.Count)
            .OrderBy(index => nodes[index].StartTokenIndex)
            .ThenBy(index => nodes[index].SemanticSignature, StringComparer.Ordinal)
            .ThenBy(index => nodes[index].SemanticDimension, StringComparer.Ordinal)
            .ThenBy(index => nodes[index].SemanticValue, StringComparer.Ordinal)
            .ToArray();
        var canonicalIndexByOriginalIndex = orderedNodeIndexes
            .Select((originalIndex, canonicalIndex) => new { originalIndex, canonicalIndex })
            .ToDictionary(item => item.originalIndex, item => item.canonicalIndex);
        var orderedNodes = orderedNodeIndexes.Select(index => nodes[index]).ToArray();
        var orderedRelations = relations
            .Select(item => item with
            {
                SourceNodeIndex = canonicalIndexByOriginalIndex[item.SourceNodeIndex],
                TargetNodeIndex = canonicalIndexByOriginalIndex[item.TargetNodeIndex]
            })
            .OrderBy(item => item.SourceNodeIndex)
            .ThenBy(item => item.TargetNodeIndex)
            .ThenBy(item => item.RelationSignature, StringComparer.Ordinal)
            .ToArray();
        // A dense curriculum can recognize mature subspans inside one mature
        // whole-span primitive. Those overlapping observations are not
        // independent semantic components and do not require invented
        // relations. When no relation is present, retain one atomic primitive
        // only if its governed span contains every other matched span and all
        // equally dominant candidates agree on the same semantic value.
        // Conflicting coextensive meanings and genuinely disconnected spans
        // continue to fail closed as relation-unproven.
        if (orderedRelations.Length == 0 && orderedNodes.Length > 1)
        {
            var dominantNodes = orderedNodes
                .Where(candidate => orderedNodes.All(other =>
                    candidate.StartTokenIndex <= other.StartTokenIndex &&
                    candidate.StartTokenIndex + candidate.TokenLength >=
                        other.StartTokenIndex + other.TokenLength))
                .ToArray();
            var dominantMeanings = dominantNodes
                .GroupBy(item => new { item.SemanticDimension, item.SemanticValue })
                .Take(2)
                .ToArray();
            if (dominantMeanings.Length == 1)
            {
                orderedNodes =
                [dominantMeanings[0]
                    .OrderByDescending(item => item.IndependentSupportCount)
                    .ThenBy(item => item.SemanticSignature, StringComparer.Ordinal)
                    .First()];
            }
        }
        // V20.3: a reusable meaning graph does not require an artificial
        // second semantic node merely to prove that one independently mature
        // semantic primitive has meaning.
        //
        // Multi-node interpretations still require independently supported
        // relations. A one-node interpretation may stand alone because the
        // primitive itself already passed the existing independent-support and
        // contradiction gates above.
        //
        // Unknown surface material remains visible in UnknownTokens; no token
        // is assigned a semantic value by this rule.
        // V20.3: one independently mature primitive is a valid atomic
        // meaning. For multi-node input, one or more independently mature
        // relations establish the active connected semantic component.
        //
        // A mature primitive that is recognized elsewhere in the utterance
        // but does not participate in that connected component must not
        // invalidate the governed meaning. The existing semantic-value
        // projection below already consumes only relation-participating nodes
        // for relational meaning, so this preserves one authority rather than
        // introducing a second completeness rule.
        var isAtomicMeaning =
            orderedNodes.Length == 1;

        var isRelationalMeaning =
            orderedNodes.Length > 1 &&
            orderedRelations.Length > 0;

        var isComposed =
            isAtomicMeaning ||
            isRelationalMeaning;

        var reasonCode =
            orderedNodes.Length == 0
                ? "meaning_graph_component_unknown"
                : isAtomicMeaning
                    ? "meaning_graph_atomic_primitive_governed"
                    : orderedRelations.Length == 0
                        ? "meaning_graph_relation_unproven"
                        : "meaning_graph_observational_composed";

        return new(
            isComposed,
            orderedNodes,
            orderedRelations,
            unknown,
            reasonCode);
    }

    private sealed record ReusableMeaningAnchorCandidate(
        string SemanticSignature,
        string SemanticDimension,
        string SemanticValue,
        int IndependentSupportCount,
        int HumanVerifiedSupportCount,
        string MaturityState,
        string Text,
        int? StartTokenIndex,
        int? TokenLength);

    private static int MeaningEvidenceStandard(
        int independentSourceCount,
        int humanVerifiedSupportCount,
        string maturityState) =>
        independentSourceCount >= 3 &&
        humanVerifiedSupportCount >= 3 &&
        string.Equals(maturityState, "Supported", StringComparison.Ordinal)
            ? HigherGovernedEvidenceStandard
            : BroadGovernedEvidenceStandard;

    /// <summary>
    /// Stage-5 serving projection: the existing selector receives the Stage-1
    /// composed graph and Stage-3 durable discourse bindings before the same
    /// governed compositional realization routine runs. It is intentionally not
    /// a surface lookup or a second transition authority.
    /// </summary>
    internal async Task<LegendSemanticTransitionInference> TryInferComposedSemanticTransitionAsync(
        string sourceLanguageCode,
        string input,
        IReadOnlyList<LegendConnectConversationContextItem> context,
        LegendConnectDiscourseStateSnapshot? discourseState,
        CancellationToken cancellationToken = default)
    {
        var graph = await AnalyzeReusableMeaningGraphAsync(sourceLanguageCode, input, cancellationToken);
        var selection = await SelectSemanticTransitionAsync(
            sourceLanguageCode,
            input,
            context,
            discourseState,
            requireComposedGraph: true,
            composedGraph: graph,
            cancellationToken: cancellationToken);
        if (selection.Selected is null)
        {
            return selection.IsAmbiguous
                ? SemanticTransitionAmbiguous(selection.ReasonCode)
                : selection.IsContradicted
                    ? SemanticTransitionContradicted(selection.ReasonCode)
                    : SemanticTransitionInsufficient(selection.ReasonCode);
        }

        var content = await ResolveGovernedResponseContentAsync(
            selection.SourceLanguageCode,
            selection.Selected,
            cancellationToken);
        if (content.IsRequired && !content.Succeeded)
        {
            return SemanticTransitionInsufficient(content.ReasonCode);
        }

        var candidate = content.Succeeded
            ? selection.Selected with
            {
                Bindings = content.MergedBindings,
                EvidenceStandard = Math.Min(
                    selection.Selected.EvidenceStandard,
                    content.EvidenceStandard)
            }
            : selection.Selected;
        var realization = await TryRealizeSemanticTransitionResultAsync(
            candidate,
            selection.SourceLanguageCode,
            SourceComponentsFromMeaningGraph(input, graph),
            content.ContentVariableBindings,
            requireOriginalRealization: true,
            cancellationToken: cancellationToken);
        if (realization.Reason is not null)
        {
            return realization.IsAmbiguous
                ? SemanticTransitionAmbiguous(realization.Reason)
                : SemanticTransitionInsufficient(realization.Reason);
        }

        return new LegendSemanticTransitionInference(
            LegendSemanticTransitionInference.Supported,
            realization.Text,
            selection.Selected.IndependentEvidenceCount + selection.Selected.ReasoningEvidenceCount + content.EvidenceCount + realization.LayoutEvidenceCount,
            [
                "governed_composed_meaning_graph",
                Math.Min(candidate.EvidenceStandard, realization.EvidenceStandard) ==
                    HigherGovernedEvidenceStandard
                    ? "higher_standard_semantic_transition"
                    : "broad_governed_semantic_transition",
                "governed_content_binding",
                realization.IsOriginal
                    ? "original_compositional_anchor_realization"
                    : "canonical_governed_endpoint_articulation"
            ]);
    }

    internal async Task<LegendConnectResponseMeaningPlanResult> TryPlanResponseMeaningAsync(
        string sourceLanguageCode,
        string input,
        LegendConnectDiscourseStateSnapshot? discourseState,
        CancellationToken cancellationToken = default)
    {
        var selection = await SelectResponseMeaningPlanAsync(
            sourceLanguageCode, input, discourseState, cancellationToken);
        return selection.Plan is null
            ? new(false, selection.ReasonCode, null)
            : new(true, "response_meaning_plan_governed", selection.Plan);
    }

    internal async Task<LegendConnectContentBoundResponseMeaningPlanResult>
        TryBindResponseContentAsync(
            string sourceLanguageCode,
            string input,
            LegendConnectDiscourseStateSnapshot? discourseState,
            CancellationToken cancellationToken = default)
    {
        var selection = await SelectResponseMeaningPlanAsync(
            sourceLanguageCode, input, discourseState, cancellationToken);
        if (selection.Plan is null || selection.Selected is null)
            return new(false, selection.ReasonCode, null);

        var content = await ResolveGovernedResponseContentAsync(
            selection.SourceLanguageCode,
            selection.Selected,
            cancellationToken);
        if (!content.Succeeded)
            return new(false, content.ReasonCode, null);

        return new(true, "response_content_bound_governed", new(
            selection.Plan,
            content.ContentVariableBindings,
            content.Facts,
            content.EvidenceCount));
    }

    private async Task<ResponseMeaningPlanSelection> SelectResponseMeaningPlanAsync(
        string sourceLanguageCode,
        string input,
        LegendConnectDiscourseStateSnapshot? discourseState,
        CancellationToken cancellationToken)
    {
        var graph = await AnalyzeReusableMeaningGraphAsync(sourceLanguageCode, input, cancellationToken);
        var selection = await SelectSemanticTransitionAsync(
            sourceLanguageCode, input, [], discourseState, requireComposedGraph: true,
            composedGraph: graph, cancellationToken: cancellationToken);
        var selected = selection.Selected;
        if (selected is null ||
            !TryInstantiateResponsePlanFrame(
                selected.ResultFrame,
                selected.Bindings,
                out var dimensions,
                out var unboundResultVariables))
        {
            return new(selection.SourceLanguageCode, graph, null, null, selection.ReasonCode);
        }
        var activeBindings = ActiveDiscourseBindings(discourseState);
        var resolvedBindings = discourseState?.Turns.LastOrDefault()?.Bindings
            .Where(item => item.ResolutionState == "bound")
            .OrderBy(item => item.EntitySemanticDimension, StringComparer.Ordinal)
            .ThenBy(item => item.EntitySemanticSignature, StringComparer.Ordinal)
            .ToArray() ?? [];
        var identity = LegendLanguageIdentity.TextHash(string.Join("|",
            MeaningGraphIdentity(graph),
            selected.TransitionSignature,
            selected.ResultFrame.Signature,
            CanonicalBindings(selected.Bindings),
            string.Join(";", unboundResultVariables.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Key + "=" + item.Value)),
            string.Join(";", activeBindings.OrderBy(item => item.Key).Select(item => item.Key + "=" + item.Value)),
            string.Join(";", selected.ReasoningPath ?? Array.Empty<string>())));
        return new(selection.SourceLanguageCode, graph, selected, new(
            identity,
            MeaningGraphIdentity(graph),
            selected.TransitionSignature,
            selected.ResultFrame.Signature,
            dimensions,
            resolvedBindings,
            selected.IndependentEvidenceCount + selected.ReasoningEvidenceCount,
            discourseState is { Turns.Count: > 0 },
            selected.Bindings,
            unboundResultVariables,
            selected.ReasoningPath,
            selected.ReasoningEvidenceCount,
            selected.EvidenceStandard == HigherGovernedEvidenceStandard
                ? "HigherStandard"
                : "BroadGoverned"),
            "response_meaning_plan_governed");
    }

    private async Task<SemanticTransitionSelection> SelectSemanticTransitionAsync(
        string sourceLanguageCode, string input, IReadOnlyList<LegendConnectConversationContextItem> context,
        LegendConnectDiscourseStateSnapshot? discourseState, bool requireComposedGraph,
        LegendConnectUtteranceMeaningGraphSnapshot? composedGraph, CancellationToken cancellationToken)
    {
        var language = await _languages.NormalizeEnabledTranslationLanguageAsync(sourceLanguageCode, cancellationToken);
        if (language is null) return SemanticTransitionSelection.Insufficient("invalid_source_language");
        IReadOnlyDictionary<string, string> values;
        IReadOnlyList<LegendShadowSourceSemanticComponent> sourceComponents;
        if (requireComposedGraph)
        {
            var graph = composedGraph ?? await AnalyzeReusableMeaningGraphAsync(language, input, cancellationToken);
            if (!graph.IsComposed) return SemanticTransitionSelection.Insufficient(graph.ReasonCode);
            if (discourseState?.Turns.LastOrDefault()?.Bindings.Any(item => item.ResolutionState == "unresolved") == true)
                return SemanticTransitionSelection.Insufficient("discourse_reference_unresolved");
            if (!TryToUnambiguousSemanticValues(graph, out values))
                return SemanticTransitionSelection.Ambiguous("ambiguous_composed_meaning");
            // Stage 4 intentionally selects from the composed governed graph,
            // not from a second lexical interpretation. Surface analysis has
            // already been admitted by AnalyzeReusableMeaningGraphAsync.
            sourceComponents = [];
        }
        else
        {
            var understanding = await AnalyzeSemanticTransitionSourceSemanticsAsync(language, input, cancellationToken);
            if (understanding.State != LegendShadowSourceUnderstanding.SupportedForShadowEvaluation)
                return SemanticTransitionSelection.Insufficient(understanding.Reasons.FirstOrDefault() ?? "source_semantics_not_governed");
            if (!TryToUnambiguousSemanticValues(understanding.Components, out values))
                return SemanticTransitionSelection.Ambiguous("ambiguous_source_semantic_dimension", language, understanding.Components);
            sourceComponents = understanding.Components;
        }
        var observations = await LoadActiveSemanticTransitionObservationsAsync(language, null, cancellationToken);
        if (observations.Count == 0)
            return SemanticTransitionSelection.Insufficient("semantic_transition_evidence_unknown", language, sourceComponents);
        // Founder-declared reasoning.* relationships are internal semantic
        // operations, never direct conversational answer edges. They retain the
        // exact canonical transition provenance and governed-evidence gates.
        var reasoningOperators = await LoadActiveGovernedReasoningOperatorsAsync(
            language,
            cancellationToken);
        var responseObservations = reasoningOperators.Count == 0
            ? observations
            : observations.Where(item => !reasoningOperators.ContainsKey(item.TransitionSignature)).ToList();
        var candidates = BuildGovernedSemanticTransitionCandidates(
            responseObservations,
            values,
            allowMissingVariables: false);
        candidates = PreferHighestStandardCandidates(candidates);
        if (candidates.Count == 0)
        {
            if (HasContradictedSemanticTransition(responseObservations, values)) return SemanticTransitionSelection.Contradicted("semantic_transition_contradicted");

            // Founder curricula may carry controlled descriptive dimensions
            // which are not independently surfaced by the present-turn
            // meaning graph.  They remain canonical evidence, but they must
            // not make an otherwise unique governed response unreachable.
            //
            // This is a projection over the complete active transition
            // authority, not an override or a phrase-specific fallback.  A
            // static dimension may be omitted only when at least one other
            // source dimension matched this turn and every compatible,
            // independently production-eligible transition selects the same
            // result frame. Higher-standard evidence is mandatory whenever it
            // is present; broad governed evidence remains usable only when no
            // higher-standard compatible candidate exists. Observed conflicts,
            // semantic variables, genuine
            // result ambiguity, and contradictory evidence remain fail-closed.
            var projectedCandidates = BuildGovernedSemanticTransitionCandidates(
                    responseObservations,
                    values,
                    allowMissingVariables: false,
                    allowMissingStaticDimensions: true)
                .Where(item => item.DirectSourceMatchCount > 0)
                .ToList();
            projectedCandidates = PreferHighestStandardCandidates(projectedCandidates);
            if (projectedCandidates.Count > 0)
            {
                if (HasContradictedSemanticTransition(
                        responseObservations,
                        values,
                        allowMissingStaticDimensions: true))
                {
                    return SemanticTransitionSelection.Contradicted(
                        "semantic_transition_projected_contradicted");
                }

                if (projectedCandidates
                        .Select(item => item.ResultFrame.Signature)
                        .Distinct(StringComparer.Ordinal)
                        .Count() != 1)
                {
                    return SemanticTransitionSelection.Ambiguous(
                        "ambiguous_semantic_transition_projection");
                }

                return new(
                    language,
                    sourceComponents,
                    projectedCandidates
                        .OrderByDescending(item => item.DirectSourceMatchCount)
                        .ThenBy(item => item.TransitionSignature, StringComparer.Ordinal)
                        .First(),
                    "response_meaning_plan_governed_projected",
                    false,
                    false);
            }

            // The exact active curriculum endpoint is the strongest source-
            // frame projection when a dense knowledge graph contains a
            // different connected subgraph alongside that endpoint's governed
            // meaning. Resolve only its source frame here; result selection
            // still runs through the same transition evidence, contradiction,
            // evidence-standard, and ambiguity gates. This is not stored-answer
            // retrieval and cannot make an unseen paraphrase appear supported.
            var exactEndpoint = await SelectCurrentExactSourceEndpointAsync(
                language,
                input,
                responseObservations,
                cancellationToken);
            if (exactEndpoint is not null)
                return exactEndpoint;

            var failureReason = "semantic_transition_not_supported";
            var partialCandidates = BuildGovernedSemanticTransitionCandidates(
                    responseObservations, values, allowMissingVariables: true)
                .Where(item => item.MissingVariables.Count > 0 && item.DirectSourceMatchCount > 0)
                .ToList();
            partialCandidates = PreferHighestStandardCandidates(partialCandidates);
            if (partialCandidates.Count > 0)
            {
                // First complete missing non-lexical source metadata when the
                // current utterance is itself an exact active Founder endpoint.
                // This establishes source meaning only; it never retrieves the
                // endpoint's answer.
                candidates = await BindCandidatesFromCurrentSourceEndpointAsync(
                    language,
                    input,
                    partialCandidates,
                    cancellationToken);

                if (candidates.Count == 0)
                {
                    // A held-out utterance may explicitly contain the value of a
                    // missing semantic variable (for example a domain/content
                    // value) even when that value is intentionally not a
                    // cross-family primitive. Bind only exact Founder-approved
                    // lexical anchor surfaces for the candidate's missing
                    // dimensions, and only when the current surface resolves to
                    // one unambiguous semantic value.
                    candidates = await BindCandidatesFromCurrentLexicalVariableEvidenceAsync(
                        language,
                        input,
                        partialCandidates,
                        cancellationToken);
                }

                if (candidates.Count == 0)
                {
                    // Existing discourse/context grounding remains the one
                    // antecedent-binding authority after current-turn evidence.
                    var contextFrames = new List<GroundedContextFrame>();
                    if (discourseState is not null)
                    {
                        contextFrames.AddRange(ResolveGroundedContextFramesFromDiscourseState(
                            discourseState, responseObservations));
                    }
                    contextFrames.AddRange(await ResolveGroundedContextFramesAsync(
                        language, context, responseObservations, cancellationToken));
                    candidates = BindCandidatesFromGroundedContext(partialCandidates, contextFrames);
                }

                if (candidates.Count == 0)
                    failureReason = "semantic_context_not_governed";
            }

            if (candidates.Count == 0 && reasoningOperators.Count > 0)
            {
                var reasoned = await TrySelectGovernedReasonedResponseAsync(
                    language,
                    values,
                    discourseState,
                    observations,
                    responseObservations,
                    reasoningOperators,
                    cancellationToken);
                if (reasoned.IsAmbiguous)
                    return SemanticTransitionSelection.Ambiguous(reasoned.ReasonCode, language, sourceComponents);
                if (reasoned.IsContradicted)
                    return SemanticTransitionSelection.Contradicted(reasoned.ReasonCode, language, sourceComponents);
                if (reasoned.Selected is not null)
                {
                    return new(
                        language,
                        sourceComponents,
                        reasoned.Selected,
                        reasoned.ReasonCode,
                        false,
                        false);
                }
                if (!string.Equals(
                        reasoned.ReasonCode,
                        "governed_reasoning_not_applicable",
                        StringComparison.Ordinal))
                {
                    return SemanticTransitionSelection.Insufficient(
                        reasoned.ReasonCode,
                        language,
                        sourceComponents);
                }
            }

            if (candidates.Count == 0)
                return SemanticTransitionSelection.Insufficient(failureReason, language, sourceComponents);
        }
        candidates = PreferHighestStandardCandidates(candidates);
        if (candidates.Select(item => item.ResultFrame.Signature).Distinct().Count() != 1)
            return SemanticTransitionSelection.Ambiguous("ambiguous_semantic_transition");
        return new(language, sourceComponents, candidates
            .OrderByDescending(item => item.EvidenceStandard)
            .ThenByDescending(item => item.DirectSourceMatchCount)
            .ThenBy(item => item.TransitionSignature, StringComparer.Ordinal)
            .First(), "response_meaning_plan_governed", false, false);
    }

    private async Task<SemanticTransitionSelection?> SelectCurrentExactSourceEndpointAsync(
        string sourceLanguage,
        string input,
        IReadOnlyList<SemanticTransitionObservation> observations,
        CancellationToken cancellationToken)
    {
        if (observations.Count == 0)
            return null;

        var normalizedInput = LegendLanguageIdentity.NormalizeText(input);
        if (string.IsNullOrWhiteSpace(normalizedInput))
            return null;

        var sourceExampleIds = observations
            .Select(item => item.SourceCurriculumExampleId)
            .Distinct()
            .ToArray();
        var exactSourceExampleIds = await (
            from source in _db.Set<LegendCurriculumExample>().AsNoTracking()
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on source.TextUnitId equals unit.Id
            where sourceExampleIds.Contains(source.Id) &&
                source.SupersededUtc == null &&
                source.LanguageCode == sourceLanguage &&
                unit.LanguageCode == sourceLanguage &&
                unit.IsTrainingEligible &&
                unit.Text == normalizedInput
            select source.Id).Distinct().ToArrayAsync(cancellationToken);
        if (exactSourceExampleIds.Length == 0)
            return null;

        var exactIds = exactSourceExampleIds.ToHashSet();
        var exactObservations = observations
            .Where(item => exactIds.Contains(item.SourceCurriculumExampleId))
            .ToList();
        if (exactObservations.Any(item => string.Equals(
                item.ContributionState,
                "Contradictory",
                StringComparison.Ordinal)))
        {
            return SemanticTransitionSelection.Contradicted(
                "exact_source_semantic_transition_contradicted",
                sourceLanguage,
                []);
        }

        var endpointCandidates = BuildGovernedSemanticTransitionCandidates(
            exactObservations,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            allowMissingVariables: true,
            allowMissingStaticDimensions: true);
        var completeCandidates = endpointCandidates
            .Where(item => item.MissingVariables.Count == 0)
            .ToList();
        var partialCandidates = endpointCandidates
            .Where(item => item.MissingVariables.Count > 0)
            .ToList();
        if (partialCandidates.Count > 0)
        {
            completeCandidates.AddRange(await BindCandidatesFromCurrentSourceEndpointAsync(
                sourceLanguage,
                input,
                partialCandidates,
                cancellationToken));
        }

        completeCandidates = PreferHighestStandardCandidates(completeCandidates);
        if (completeCandidates.Count == 0)
            return null;
        if (completeCandidates
                .Select(item => item.ResultFrame.Signature)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1)
        {
            return SemanticTransitionSelection.Ambiguous(
                "ambiguous_exact_source_semantic_transition",
                sourceLanguage,
                []);
        }

        return new(
            sourceLanguage,
            [],
            completeCandidates
                .OrderByDescending(item => item.EvidenceStandard)
                .ThenBy(item => item.TransitionSignature, StringComparer.Ordinal)
                .First(),
            "response_meaning_plan_governed_exact_source",
            false,
            false);
    }

    private static IReadOnlyDictionary<string, string> ActiveDiscourseBindings(LegendConnectDiscourseStateSnapshot? state) =>
        state?.Turns.SelectMany(item => item.Bindings).Where(item => item.ResolutionState == "bound" && item.EntitySemanticValue is not null)
            .GroupBy(item => item.EntitySemanticDimension).ToDictionary(item => item.Key, item => item.Last().EntitySemanticValue!, StringComparer.Ordinal)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static string MeaningGraphIdentity(LegendConnectUtteranceMeaningGraphSnapshot graph) =>
        LegendLanguageIdentity.TextHash(string.Join("|",
            graph.Nodes.OrderBy(item => item.SemanticSignature, StringComparer.Ordinal)
                .ThenBy(item => item.SemanticDimension, StringComparer.Ordinal)
                .ThenBy(item => item.SemanticValue, StringComparer.Ordinal)
                .Select(item => item.SemanticSignature + ":" + item.SemanticDimension + "=" + item.SemanticValue),
            string.Join(";", graph.Relations.OrderBy(item => item.RelationSignature, StringComparer.Ordinal)
                .ThenBy(item => item.RelationKind, StringComparer.Ordinal)
                .Select(item => item.RelationSignature + ":" + item.RelationKind))));

    private static IReadOnlyList<GroundedContextFrame> ResolveGroundedContextFramesFromDiscourseState(
        LegendConnectDiscourseStateSnapshot state,
        IReadOnlyList<SemanticTransitionObservation> observations)
    {
        var frames = new List<GroundedContextFrame>();
        // The final persisted turn is the current input. It may supply a
        // reference selector but can never become its own antecedent.
        foreach (var turn in state.Turns.Take(Math.Max(0, state.Turns.Count - 1)))
        {
            if (!turn.IsComposed || !TryToUnambiguousSemanticValues(turn.Nodes, turn.Relations, out var values))
                continue;
            AddGroundedContextFrames(frames, observations, values);
        }
        return frames;
    }

    private async Task<IReadOnlyList<SemanticTransitionObservation>>
        LoadActiveSemanticTransitionObservationsAsync(
            string sourceLanguage,
            IReadOnlyCollection<string>? transitionSignatures,
            CancellationToken cancellationToken)
    {
        return await (
            from evidence in _db.Set<LegendSemanticTransitionEvidence>().AsNoTracking()
            join source in _db.Set<LegendCurriculumExample>().AsNoTracking()
                on evidence.SourceCurriculumExampleId equals source.Id
            join result in _db.Set<LegendCurriculumExample>().AsNoTracking()
                on evidence.ResultCurriculumExampleId equals result.Id
            join sourceUnit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on source.TextUnitId equals sourceUnit.Id
            join resultUnit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on result.TextUnitId equals resultUnit.Id
            where evidence.SupersededUtc == null &&
                evidence.SourceLanguageCode == sourceLanguage &&
                evidence.ResultLanguageCode == sourceLanguage &&
                (transitionSignatures == null || transitionSignatures.Contains(evidence.TransitionSignature)) &&
                (evidence.ContributionState == "Supported" ||
                 evidence.ContributionState == "Contradictory") &&
                source.SupersededUtc == null && result.SupersededUtc == null &&
                source.LanguageCode == sourceLanguage && result.LanguageCode == sourceLanguage &&
                sourceUnit.IsTrainingEligible && resultUnit.IsTrainingEligible &&
                ((evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                  evidence.IsHumanVerifiedSupport &&
                  source.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                  result.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                  sourceUnit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                  resultUnit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved) ||
                 (evidence.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine &&
                  !evidence.IsHumanVerifiedSupport &&
                  source.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine &&
                  result.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine &&
                  (sourceUnit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved ||
                   sourceUnit.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine) &&
                  (resultUnit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved ||
                   resultUnit.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine)))
            select new SemanticTransitionObservation(
                evidence.TransitionSignature,
                evidence.SourceSemanticFrame,
                evidence.ResultSemanticFrame,
                evidence.IndependentSourceIdentity,
                evidence.ContributionState,
                evidence.IsHumanVerifiedSupport,
                evidence.Provenance,
                evidence.SourceCurriculumExampleId,
                evidence.ResultCurriculumExampleId)
        ).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Read-only status support for a bounded Founder submission projection.
    /// It reuses the exact production transition admissibility checks while
    /// querying only signatures already referenced by that requested page.
    /// </summary>
    internal async Task<IReadOnlySet<string>> GetProductionEligibleSemanticTransitionSignaturesAsync(
        string sourceLanguage,
        IReadOnlyCollection<string> transitionSignatures,
        CancellationToken cancellationToken = default)
    {
        if (transitionSignatures.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var observations = await LoadActiveSemanticTransitionObservationsAsync(
            sourceLanguage,
            transitionSignatures,
            cancellationToken);
        return observations
            .GroupBy(item => item.TransitionSignature, StringComparer.Ordinal)
            .Where(group => TryGetProductionSemanticTransitionFrames(
                group,
                out _,
                out _,
                out _))
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyList<SemanticTransitionCandidate>
        BuildGovernedSemanticTransitionCandidates(
            IReadOnlyList<SemanticTransitionObservation> observations,
            IReadOnlyDictionary<string, string> inputValues,
            bool allowMissingVariables,
            bool allowMissingStaticDimensions = false)
    {
        var candidates = new List<SemanticTransitionCandidate>();
        foreach (var group in observations.GroupBy(item => item.TransitionSignature, StringComparer.Ordinal))
        {
            if (!TryGetGovernedSemanticTransitionFrames(
                    group,
                    out var independentEvidenceCount,
                    out var sourceFrame,
                    out var resultFrame,
                    out var evidenceStandard))
            {
                continue;
            }

            if (!TryBindInputSemanticFrame(
                    sourceFrame,
                    inputValues,
                    allowMissingVariables,
                    allowMissingStaticDimensions,
                    out var bindings,
                    out var missingVariables,
                    out var directSourceMatchCount))
            {
                continue;
            }

            candidates.Add(new SemanticTransitionCandidate(
                group.Key,
                sourceFrame,
                resultFrame,
                bindings,
                missingVariables,
                directSourceMatchCount,
                independentEvidenceCount,
                evidenceStandard));
        }
        return candidates;
    }

    private static List<SemanticTransitionCandidate> PreferHighestStandardCandidates(
        IReadOnlyList<SemanticTransitionCandidate> candidates)
    {
        if (candidates.Count == 0)
            return candidates.ToList();

        var highestStandard = candidates.Max(item => item.EvidenceStandard);
        var highestStandardCandidates = candidates
            .Where(item => item.EvidenceStandard == highestStandard)
            .ToList();
        var mostDirectMatches = highestStandardCandidates
            .Max(item => item.DirectSourceMatchCount);
        return highestStandardCandidates
            .Where(item => item.DirectSourceMatchCount == mostDirectMatches)
            .ToList();
    }

    private static bool TryGetGovernedSemanticTransitionFrames(
        IEnumerable<SemanticTransitionObservation> observations,
        out int independentEvidenceCount,
        out NormalizedSemanticFrame sourceFrame,
        out NormalizedSemanticFrame resultFrame,
        out int evidenceStandard)
    {
        var group = observations as IReadOnlyList<SemanticTransitionObservation> ?? observations.ToList();
        evidenceStandard = BroadGovernedEvidenceStandard;
        if (TryGetProductionSemanticTransitionFrames(
                group,
                out independentEvidenceCount,
                out sourceFrame,
                out resultFrame))
        {
            evidenceStandard = HigherGovernedEvidenceStandard;
            return true;
        }

        independentEvidenceCount = 0;
        sourceFrame = null!;
        resultFrame = null!;
        if (group.Count == 0 ||
            group.Any(item => string.Equals(
                item.ContributionState,
                "Contradictory",
                StringComparison.Ordinal)))
        {
            return false;
        }

        var supported = group
            .Where(item =>
                string.Equals(item.ContributionState, "Supported", StringComparison.Ordinal) &&
                ((item.IsHumanVerifiedSupport &&
                  string.Equals(item.Provenance, LegendConnectKnowledgeProvenance.FounderApproved, StringComparison.Ordinal)) ||
                 (!item.IsHumanVerifiedSupport &&
                  string.Equals(item.Provenance, LegendConnectKnowledgeProvenance.SystemValidatedMachine, StringComparison.Ordinal))))
            .ToList();
        independentEvidenceCount = supported
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (independentEvidenceCount == 0)
            return false;

        var representative = supported[0];
        if (group.Any(item =>
                !string.Equals(item.SourceFrame, representative.SourceFrame, StringComparison.Ordinal) ||
                !string.Equals(item.ResultFrame, representative.ResultFrame, StringComparison.Ordinal)) ||
            !TryReadSemanticFrame(representative.SourceFrame, out sourceFrame) ||
            !TryReadSemanticFrame(representative.ResultFrame, out resultFrame))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetProductionSemanticTransitionFrames(
        IEnumerable<SemanticTransitionObservation> observations,
        out int independentEvidenceCount,
        out NormalizedSemanticFrame sourceFrame,
        out NormalizedSemanticFrame resultFrame)
    {
        var group = observations as IReadOnlyList<SemanticTransitionObservation> ?? observations.ToList();
        independentEvidenceCount = 0;
        sourceFrame = null!;
        resultFrame = null!;
        if (!LegendSemanticTransitionProductionEligibility.IsEligible(group.Select(item =>
                new LegendSemanticTransitionEligibilityObservation(
                    item.SourceFrame,
                    item.ResultFrame,
                    item.IndependentSourceIdentity,
                    item.ContributionState,
                    item.IsHumanVerifiedSupport))))
        {
            return false;
        }

        independentEvidenceCount = group
            .Where(item => item.IsHumanVerifiedSupport &&
                string.Equals(item.ContributionState, "Supported", StringComparison.Ordinal))
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var representative = group[0];
        if (!TryReadSemanticFrame(representative.SourceFrame, out sourceFrame) ||
            !TryReadSemanticFrame(representative.ResultFrame, out resultFrame) ||
            group.Any(item => !string.Equals(item.SourceFrame, representative.SourceFrame, StringComparison.Ordinal) ||
                !string.Equals(item.ResultFrame, representative.ResultFrame, StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    private static bool HasContradictedSemanticTransition(
        IReadOnlyList<SemanticTransitionObservation> observations,
        IReadOnlyDictionary<string, string> inputValues,
        bool allowMissingStaticDimensions = false)
    {
        foreach (var group in observations
                     .Where(item => string.Equals(item.ContributionState, "Contradictory", StringComparison.Ordinal))
                     .GroupBy(item => item.TransitionSignature, StringComparer.Ordinal))
        {
            if (!TryReadSemanticFrame(group.First().SourceFrame, out var sourceFrame))
                continue;
            if (TryBindInputSemanticFrame(
                    sourceFrame,
                    inputValues,
                    allowMissingVariables: false,
                    allowMissingStaticDimensions: allowMissingStaticDimensions,
                    out _,
                    out _,
                    out var directSourceMatchCount) &&
                directSourceMatchCount > 0)
            {
                return true;
            }
        }
        return false;
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadActiveGovernedReasoningOperatorsAsync(
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from transition in _db.Set<LegendSemanticTransitionEvidence>().AsNoTracking()
            join relation in _db.Set<LegendFounderSemanticExampleRelationEvidence>().AsNoTracking()
                on transition.FounderSemanticExampleRelationEvidenceId equals (Guid?)relation.Id
            where transition.SupersededUtc == null &&
                transition.SourceLanguageCode == sourceLanguage &&
                transition.ResultLanguageCode == sourceLanguage &&
                transition.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                relation.SupersededUtc == null &&
                relation.LanguageCode == sourceLanguage &&
                relation.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                relation.RelationshipSemanticIdentity.StartsWith("reasoning.")
            select new
            {
                transition.TransitionSignature,
                relation.RelationshipSemanticIdentity
            }
        ).Distinct().ToListAsync(cancellationToken);

        // A reasoning-prefixed Founder relation is an internal operator only
        // when one unambiguous identity is executable by the governed executor.
        // All other relations remain ordinary canonical response evidence.
        return rows
            .GroupBy(item => item.TransitionSignature, StringComparer.Ordinal)
            .Where(group =>
            {
                var identities = group
                    .Select(item => item.RelationshipSemanticIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .ToArray();
                return identities.Length == 1 &&
                    LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(identities[0]);
            })
            .ToDictionary(
                group => group.Key,
                group => group.First().RelationshipSemanticIdentity,
                StringComparer.Ordinal);
    }

    private async Task<GovernedReasonedResponseSelection> TrySelectGovernedReasonedResponseAsync(
        string language,
        IReadOnlyDictionary<string, string> currentValues,
        LegendConnectDiscourseStateSnapshot? discourseState,
        IReadOnlyList<SemanticTransitionObservation> allObservations,
        IReadOnlyList<SemanticTransitionObservation> responseObservations,
        IReadOnlyDictionary<string, string> reasoningOperators,
        CancellationToken cancellationToken)
    {
        var rules = new List<LegendGovernedReasoningRule>();
        foreach (var group in allObservations
                     .Where(item => reasoningOperators.TryGetValue(item.TransitionSignature, out var operation) &&
                         LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(operation))
                     .GroupBy(item => item.TransitionSignature, StringComparer.Ordinal))
        {
            if (!TryGetGovernedSemanticTransitionFrames(
                    group,
                    out var independentEvidenceCount,
                    out var sourceFrame,
                    out var resultFrame,
                    out var evidenceStandard))
            {
                continue;
            }
            rules.Add(new LegendGovernedReasoningRule(
                group.Key,
                reasoningOperators[group.Key],
                sourceFrame.Dimensions,
                resultFrame.Dimensions,
                independentEvidenceCount,
                evidenceStandard));
        }
        if (rules.Count == 0)
            return GovernedReasonedResponseSelection.None;

        var initialValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in currentValues)
            initialValues[item.Key] = item.Value;
        foreach (var binding in ActiveDiscourseBindings(discourseState))
        {
            if (!initialValues.ContainsKey(binding.Key))
                initialValues[binding.Key] = binding.Value;
        }

        var execution = LegendConnectGovernedReasoningExecutor.Derive(initialValues, rules);
        if (execution.InitialContradiction)
            return GovernedReasonedResponseSelection.Contradicted("governed_reasoning_constraint_contradicted");
        if (execution.BudgetExceeded)
            return GovernedReasonedResponseSelection.Failure("governed_reasoning_budget_exceeded");
        if (execution.DerivedStates.Count == 0)
            return GovernedReasonedResponseSelection.None;

        var responses = new List<(SemanticTransitionCandidate Candidate, LegendGovernedReasoningProof Proof)>();
        foreach (var proof in execution.DerivedStates)
        {
            if (HasContradictedSemanticTransition(responseObservations, proof.Values))
                continue;
            var proofCandidates = BuildGovernedSemanticTransitionCandidates(
                responseObservations,
                proof.Values,
                allowMissingVariables: false);
            foreach (var candidate in proofCandidates)
            {
                responses.Add((candidate with
                {
                    EvidenceStandard = Math.Min(
                        candidate.EvidenceStandard,
                        proof.EvidenceStandard)
                }, proof));
            }
        }
        if (responses.Count == 0)
            return GovernedReasonedResponseSelection.None;

        var highestResponseStandard = responses.Max(item => item.Candidate.EvidenceStandard);
        responses = responses
            .Where(item => item.Candidate.EvidenceStandard == highestResponseStandard)
            .ToList();
        var highestDirectMatchCount = responses.Max(item => item.Candidate.DirectSourceMatchCount);
        responses = responses
            .Where(item => item.Candidate.DirectSourceMatchCount == highestDirectMatchCount)
            .ToList();

        var outcomes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var response in responses)
        {
            if (!TryInstantiateResponsePlanFrame(
                    response.Candidate.ResultFrame,
                    response.Candidate.Bindings,
                    out var dimensions,
                    out var unbound))
            {
                continue;
            }
            outcomes.Add(JsonSerializer.Serialize(new
            {
                Dimensions = dimensions.OrderBy(item => item.Key, StringComparer.Ordinal),
                Unbound = unbound.OrderBy(item => item.Key, StringComparer.Ordinal)
            }));
        }
        if (outcomes.Count != 1)
            return GovernedReasonedResponseSelection.Ambiguous("ambiguous_governed_reasoning_conclusion");

        var selected = responses
            .OrderByDescending(item => item.Candidate.EvidenceStandard)
            .ThenBy(item => item.Proof.Depth)
            .ThenByDescending(item => item.Proof.EvidenceCount)
            .ThenBy(item => item.Candidate.TransitionSignature, StringComparer.Ordinal)
            .First();
        return GovernedReasonedResponseSelection.Success(
            selected.Candidate with
            {
                ReasoningEvidenceCount = selected.Proof.EvidenceCount,
                ReasoningPath = selected.Proof.TransitionPath,
                EvidenceStandard = Math.Min(
                    selected.Candidate.EvidenceStandard,
                    selected.Proof.EvidenceStandard)
            });
    }

    private async Task<IReadOnlyList<SemanticTransitionCandidate>> BindCandidatesFromCurrentSourceEndpointAsync(
        string sourceLanguage,
        string input,
        IReadOnlyList<SemanticTransitionCandidate> partialCandidates,
        CancellationToken cancellationToken)
    {
        if (partialCandidates.Count == 0)
            return [];

        var normalizedInput = LegendLanguageIdentity.NormalizeText(input);
        if (string.IsNullOrWhiteSpace(normalizedInput))
            return [];

        var signatures = partialCandidates
            .Select(item => item.TransitionSignature)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceExampleIds = await (
            from evidence in _db.Set<LegendSemanticTransitionEvidence>().AsNoTracking()
            join source in _db.Set<LegendCurriculumExample>().AsNoTracking()
                on evidence.SourceCurriculumExampleId equals source.Id
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on source.TextUnitId equals unit.Id
            where signatures.Contains(evidence.TransitionSignature) &&
                evidence.SupersededUtc == null &&
                evidence.SourceLanguageCode == sourceLanguage &&
                evidence.ResultLanguageCode == sourceLanguage &&
                evidence.ContributionState == "Supported" &&
                evidence.IsHumanVerifiedSupport &&
                evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                source.SupersededUtc == null &&
                source.LanguageCode == sourceLanguage &&
                source.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                unit.LanguageCode == sourceLanguage &&
                unit.IsTrainingEligible &&
                unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                unit.Text == normalizedInput
            select new
            {
                evidence.TransitionSignature,
                evidence.SourceCurriculumExampleId
            }).Distinct().ToListAsync(cancellationToken);

        if (sourceExampleIds.Count == 0)
            return [];

        var ids = sourceExampleIds.Select(item => item.SourceCurriculumExampleId).Distinct().ToArray();
        var variations = await _db.Set<LegendCurriculumExampleVariation>().AsNoTracking()
            .Where(item => ids.Contains(item.CurriculumExampleId))
            .Select(item => new
            {
                item.CurriculumExampleId,
                item.Dimension,
                item.Value
            })
            .ToListAsync(cancellationToken);
        var signatureByExample = sourceExampleIds
            .GroupBy(item => item.SourceCurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.TransitionSignature).Distinct(StringComparer.Ordinal).ToArray());

        var completed = new List<SemanticTransitionCandidate>();
        foreach (var candidate in partialCandidates)
        {
            var candidateExampleIds = signatureByExample
                .Where(item => item.Value.Contains(candidate.TransitionSignature, StringComparer.Ordinal))
                .Select(item => item.Key)
                .ToHashSet();
            if (candidateExampleIds.Count == 0)
                continue;

            var bindings = new Dictionary<string, string>(candidate.Bindings, StringComparer.Ordinal);
            var complete = true;
            foreach (var missing in candidate.MissingVariables)
            {
                var values = variations
                    .Where(item => candidateExampleIds.Contains(item.CurriculumExampleId) &&
                        string.Equals(item.Dimension, missing.Dimension, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Value)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToArray();
                if (values.Length != 1)
                {
                    complete = false;
                    break;
                }
                if (bindings.TryGetValue(missing.Variable, out var existing) &&
                    !string.Equals(existing, values[0], StringComparison.OrdinalIgnoreCase))
                {
                    complete = false;
                    break;
                }
                bindings[missing.Variable] = values[0];
            }

            if (!complete)
                continue;
            completed.Add(candidate with
            {
                Bindings = bindings,
                MissingVariables = []
            });
        }

        return completed
            .GroupBy(item => item.TransitionSignature + "\u001f" + CanonicalBindings(item.Bindings), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<SemanticTransitionCandidate>> BindCandidatesFromCurrentLexicalVariableEvidenceAsync(
        string sourceLanguage,
        string input,
        IReadOnlyList<SemanticTransitionCandidate> partialCandidates,
        CancellationToken cancellationToken)
    {
        if (partialCandidates.Count == 0)
            return [];

        var dimensions = partialCandidates
            .SelectMany(item => item.MissingVariables)
            .Select(item => item.Dimension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (dimensions.Length == 0)
            return [];

        var normalizedInput = LegendLanguageIdentity.NormalizeText(input);
        var inputTokens = SurfaceComponents(normalizedInput)
            .Select(item => normalizedInput.Substring(item.CharacterOffset, item.CharacterLength))
            .ToArray();
        if (inputTokens.Length == 0)
            return [];

        var rows = await (
            from anchor in _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on anchor.TextUnitId equals unit.Id
            where anchor.LanguageCode == sourceLanguage &&
                anchor.SupersededUtc == null &&
                anchor.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                anchor.LexemeId != null &&
                anchor.ComponentStartTokenIndex != null &&
                anchor.ComponentLength != null &&
                anchor.ComponentLength > 0 &&
                dimensions.Contains(anchor.Dimension) &&
                unit.LanguageCode == sourceLanguage &&
                unit.IsTrainingEligible &&
                unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
            select new
            {
                anchor.Dimension,
                anchor.Value,
                Start = anchor.ComponentStartTokenIndex!.Value,
                Length = anchor.ComponentLength!.Value,
                Text = unit.Text
            }).ToListAsync(cancellationToken);

        var matched = new List<(string Dimension, string Value)>();
        foreach (var row in rows)
        {
            var sourceTokens = SurfaceComponents(row.Text);
            if (row.Start < 0 || row.Length < 1 || row.Start + row.Length > sourceTokens.Count)
                continue;
            var lexicalTokens = sourceTokens
                .Skip(row.Start)
                .Take(row.Length)
                .Select(item => row.Text.Substring(item.CharacterOffset, item.CharacterLength))
                .ToArray();
            if (lexicalTokens.Length == 0 || !ContainsTokenSequence(inputTokens, lexicalTokens))
                continue;
            matched.Add((row.Dimension, row.Value));
        }
        if (matched.Count == 0)
            return [];

        var completed = new List<SemanticTransitionCandidate>();
        foreach (var candidate in partialCandidates)
        {
            var bindings = new Dictionary<string, string>(candidate.Bindings, StringComparer.Ordinal);
            var complete = true;
            foreach (var missing in candidate.MissingVariables)
            {
                var values = matched
                    .Where(item => string.Equals(item.Dimension, missing.Dimension, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Value)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToArray();
                if (values.Length != 1)
                {
                    complete = false;
                    break;
                }
                if (bindings.TryGetValue(missing.Variable, out var existing) &&
                    !string.Equals(existing, values[0], StringComparison.OrdinalIgnoreCase))
                {
                    complete = false;
                    break;
                }
                bindings[missing.Variable] = values[0];
            }

            if (!complete)
                continue;
            completed.Add(candidate with
            {
                Bindings = bindings,
                MissingVariables = []
            });
        }

        return completed
            .GroupBy(item => item.TransitionSignature + "\u001f" + CanonicalBindings(item.Bindings), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool ContainsTokenSequence(
        IReadOnlyList<string> inputTokens,
        IReadOnlyList<string> lexicalTokens)
    {
        if (lexicalTokens.Count == 0 || lexicalTokens.Count > inputTokens.Count)
            return false;
        for (var start = 0; start <= inputTokens.Count - lexicalTokens.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < lexicalTokens.Count; offset++)
            {
                if (string.Equals(
                        inputTokens[start + offset],
                        lexicalTokens[offset],
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                matches = false;
                break;
            }
            if (matches)
                return true;
        }
        return false;
    }

    private async Task<IReadOnlyList<GroundedContextFrame>> ResolveGroundedContextFramesAsync(
        string sourceLanguage,
        IReadOnlyList<LegendConnectConversationContextItem> context,
        IReadOnlyList<SemanticTransitionObservation> observations,
        CancellationToken cancellationToken)
    {
        var frames = new List<GroundedContextFrame>();
        foreach (var turn in context.Reverse().Where(item =>
                     string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(item.Content)))
        {
            var understanding = await AnalyzeSemanticTransitionSourceSemanticsAsync(
                sourceLanguage,
                turn.Content,
                cancellationToken);
            if (!string.Equals(understanding.State, LegendShadowSourceUnderstanding.SupportedForShadowEvaluation,
                    StringComparison.Ordinal) ||
                !TryToUnambiguousSemanticValues(understanding.Components, out var values))
            {
                continue;
            }

            AddGroundedContextFrames(frames, observations, values);
        }
        return frames;
    }

    private static void AddGroundedContextFrames(
        ICollection<GroundedContextFrame> frames,
        IReadOnlyList<SemanticTransitionObservation> observations,
        IReadOnlyDictionary<string, string> values)
    {
        var candidates = BuildGovernedSemanticTransitionCandidates(
            observations,
            values,
            allowMissingVariables: false);
        candidates = PreferHighestStandardCandidates(candidates);
        if (candidates.Select(item => item.ResultFrame.Signature).Distinct(StringComparer.Ordinal).Count() != 1)
            return;
        foreach (var candidate in candidates)
        {
            if (TryInstantiateResponsePlanFrame(
                    candidate.ResultFrame,
                    candidate.Bindings,
                    out var frameValues,
                    out _))
                frames.Add(new GroundedContextFrame(candidate.ResultFrame.Signature, frameValues));
        }
    }

    private static IReadOnlyList<SemanticTransitionCandidate> BindCandidatesFromGroundedContext(
        IReadOnlyList<SemanticTransitionCandidate> partialCandidates,
        IReadOnlyList<GroundedContextFrame> contextFrames)
    {
        var completed = new List<SemanticTransitionCandidate>();
        foreach (var candidate in partialCandidates)
        {
            var bindings = new List<IReadOnlyDictionary<string, string>>();
            foreach (var frame in contextFrames)
            {
                var proposed = new Dictionary<string, string>(candidate.Bindings, StringComparer.Ordinal);
                var isComplete = true;
                foreach (var missing in candidate.MissingVariables)
                {
                    if (!frame.Values.TryGetValue(missing.Dimension, out var value) ||
                        string.IsNullOrWhiteSpace(value))
                    {
                        isComplete = false;
                        break;
                    }
                    if (proposed.TryGetValue(missing.Variable, out var existing) &&
                        !string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                    {
                        isComplete = false;
                        break;
                    }
                    proposed[missing.Variable] = value;
                }
                if (isComplete)
                    bindings.Add(proposed);
            }

            var distinctBindings = bindings
                .GroupBy(item => CanonicalBindings(item), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (distinctBindings.Count != 1)
                continue;
            completed.Add(candidate with
            {
                Bindings = distinctBindings[0],
                MissingVariables = []
            });
        }
        return completed;
    }

    /// <summary>
    /// Binds only result variables which the selected transition intentionally
    /// leaves unspecified.  The transition remains the authority for response
    /// shape; this reads the existing Founder meaning-node/relation evidence
    /// for substance.  It does not search retained text or select a response
    /// sentence.
    /// </summary>
    private async Task<GovernedContentResolution> ResolveGovernedResponseContentAsync(
        string languageCode,
        SemanticTransitionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var unbound = candidate.ResultFrame.Dimensions
            .Where(item => IsSemanticVariable(item.Value) && !candidate.Bindings.ContainsKey(item.Value))
            .GroupBy(item => item.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Key)
                .OrderBy(item => item, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        if (unbound.Count == 0)
        {
            return GovernedContentResolution.NotRequired(candidate.Bindings);
        }

        // One unbound semantic variable names one controlled result dimension.
        // Reusing it across incomparable fields would make content binding
        // under-specified, so it fails closed instead of inventing an
        // association between those fields.
        if (unbound.Any(item => item.Value.Length != 1))
            return GovernedContentResolution.Failure("result_content_variable_ambiguous");

        var boundSubjectSignatures = candidate.ResultFrame.Dimensions
            .Where(item => IsSemanticVariable(item.Value) && candidate.Bindings.TryGetValue(item.Value, out _))
            .Select(item => SemanticSignature(item.Key, candidate.Bindings[item.Value]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (boundSubjectSignatures.Length == 0)
            return GovernedContentResolution.Failure("result_content_subject_unknown");

        var contentDimensions = unbound.Values.Select(item => item[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var rows = await (
            from evidence in _db.Set<LegendLanguageMeaningRelationEvidence>().AsNoTracking()
            join relation in _db.Set<LegendLanguageMeaningRelation>().AsNoTracking()
                on evidence.MeaningRelationId equals relation.Id
            join source in _db.Set<LegendLanguageMeaningNodeEvidence>().AsNoTracking()
                on evidence.SourceMeaningNodeId equals source.Id
            join target in _db.Set<LegendLanguageMeaningNodeEvidence>().AsNoTracking()
                on evidence.TargetMeaningNodeId equals target.Id
            join sourcePrimitive in _db.Set<LegendLanguageMeaningPrimitive>().AsNoTracking()
                on new { source.LanguageCode, source.SemanticSignature }
                equals new { sourcePrimitive.LanguageCode, sourcePrimitive.SemanticSignature }
            join targetPrimitive in _db.Set<LegendLanguageMeaningPrimitive>().AsNoTracking()
                on new { target.LanguageCode, target.SemanticSignature }
                equals new { targetPrimitive.LanguageCode, targetPrimitive.SemanticSignature }
            where evidence.SupersededUtc == null &&
                evidence.ContributionState == "Supported" &&
                evidence.IsHumanVerifiedSupport &&
                evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                relation.LanguageCode == languageCode && relation.SupersededUtc == null &&
                relation.MaturityState != "Contradicted" &&
                relation.ContradictionCount == 0 && relation.IndependentSourceCount >= 1 &&
                relation.HumanVerifiedSupportCount >= 1 &&
                relation.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                source.LanguageCode == languageCode && source.SupersededUtc == null &&
                target.LanguageCode == languageCode && target.SupersededUtc == null &&
                source.SemanticSignature == relation.SourceSemanticSignature &&
                target.SemanticSignature == relation.TargetSemanticSignature &&
                boundSubjectSignatures.Contains(source.SemanticSignature) &&
                contentDimensions.Contains(target.SemanticDimension) &&
                sourcePrimitive.SupersededUtc == null && sourcePrimitive.MaturityState != "Contradicted" &&
                sourcePrimitive.ContradictionCount == 0 && sourcePrimitive.IndependentSourceCount >= 1 &&
                sourcePrimitive.HumanVerifiedSupportCount >= 1 &&
                sourcePrimitive.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                targetPrimitive.SupersededUtc == null && targetPrimitive.MaturityState != "Contradicted" &&
                targetPrimitive.ContradictionCount == 0 && targetPrimitive.IndependentSourceCount >= 1 &&
                targetPrimitive.HumanVerifiedSupportCount >= 1 &&
                targetPrimitive.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
            select new GovernedContentEvidenceRow(
                evidence.IndependentSourceIdentity,
                relation.RelationSignature,
                relation.SupportCount,
                relation.IndependentSourceCount,
                relation.ContradictionCount,
                relation.MaturityState,
                relation.MaturityState == "Supported" &&
                relation.IndependentSourceCount >= 3 &&
                relation.HumanVerifiedSupportCount >= 3 &&
                sourcePrimitive.MaturityState == "Supported" &&
                sourcePrimitive.IndependentSourceCount >= 3 &&
                sourcePrimitive.HumanVerifiedSupportCount >= 3 &&
                targetPrimitive.MaturityState == "Supported" &&
                targetPrimitive.IndependentSourceCount >= 3 &&
                targetPrimitive.HumanVerifiedSupportCount >= 3,
                source.SemanticSignature,
                source.SemanticDimension,
                source.SemanticValue,
                target.SemanticSignature,
                target.SemanticDimension,
                target.SemanticValue)
        ).ToListAsync(cancellationToken);

        var facts = rows
            .GroupBy(item => new
            {
                item.RelationSignature,
                item.SubjectSemanticSignature,
                item.ContentSemanticSignature
            })
            .Select(group =>
            {
                var representative = group.First();
                var independentSources = group.Select(item => item.IndependentSourceIdentity)
                    .Distinct(StringComparer.Ordinal).Count();
                return new
                {
                    Fact = new LegendConnectGovernedContentFactSnapshot(
                        LegendLanguageIdentity.TextHash(
                            "governed-content-fact|v1|" + representative.SubjectSemanticSignature + "|" +
                            representative.RelationSignature + "|" + representative.ContentSemanticSignature),
                        representative.SubjectSemanticSignature,
                        representative.ContentSemanticSignature,
                        representative.SubjectDimension,
                        representative.SubjectValue,
                        representative.ContentDimension,
                        representative.ContentValue,
                        representative.RelationSignature,
                        group.Count(),
                        independentSources,
                        representative.ContradictionCount,
                        representative.MaturityState,
                        // This flag records which tier won; it no longer hides
                        // active, non-contradicted Founder evidence while that
                        // evidence is accumulating independent support.
                        group.All(item => item.IsProductionEligible)),
                    IndependentSources = independentSources
                };
            })
            .Where(item => item.IndependentSources >= 1)
            .Select(item => item.Fact)
            .OrderBy(item => item.FactIdentity, StringComparer.Ordinal)
            .ToArray();

        var contentBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var selectedFacts = new List<LegendConnectGovernedContentFactSnapshot>();
        foreach (var item in unbound.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var candidates = facts
                .Where(fact => string.Equals(
                    fact.ContentDimension,
                    item.Value[0],
                    StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length == 0)
                return GovernedContentResolution.Failure("governed_content_fact_unknown");
            var useHigherStandard = candidates.Any(fact => fact.IsProductionEligible);
            var preferred = candidates
                .Where(fact => fact.IsProductionEligible == useHigherStandard)
                .ToArray();
            var distinctValues = preferred
                .Select(fact => fact.ContentSemanticSignature)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (distinctValues.Length != 1)
                return GovernedContentResolution.Failure("ambiguous_governed_content_fact");

            var selected = preferred
                .OrderByDescending(fact => fact.IndependentSourceCount)
                .ThenBy(fact => fact.FactIdentity, StringComparer.Ordinal)
                .First();
            contentBindings[item.Key] = selected.ContentValue;
            selectedFacts.Add(selected);
        }

        var mergedBindings = new Dictionary<string, string>(candidate.Bindings, StringComparer.Ordinal);
        foreach (var binding in contentBindings)
            mergedBindings.Add(binding.Key, binding.Value);

        return GovernedContentResolution.Success(
            mergedBindings,
            contentBindings,
            selectedFacts.OrderBy(item => item.FactIdentity, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<LegendShadowSourceSemanticComponent> SourceComponentsFromMeaningGraph(
        string input,
        LegendConnectUtteranceMeaningGraphSnapshot graph)
    {
        var tokens = SurfaceComponents(LegendLanguageIdentity.NormalizeText(input));
        return graph.Nodes
            .Where(node => node.StartTokenIndex >= 0 && node.TokenLength > 0 &&
                node.StartTokenIndex + node.TokenLength <= tokens.Count)
            .Select(node => new LegendShadowSourceSemanticComponent(
                node.SemanticDimension,
                node.SemanticValue,
                string.Join(' ', tokens.Skip(node.StartTokenIndex).Take(node.TokenLength)
                    .Select(item => item.NormalizedText)),
                node.StartTokenIndex,
                node.TokenLength,
                node.SemanticSignature))
            .GroupBy(item => new { item.SemanticSignature, item.StartTokenIndex, item.TokenLength })
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<SemanticTransitionRealization> TryRealizeSemanticTransitionResultAsync(
        SemanticTransitionCandidate candidate,
        string languageCode,
        IReadOnlyList<LegendShadowSourceSemanticComponent> sourceComponents,
        IReadOnlyDictionary<string, string>? contentVariableBindings,
        bool requireOriginalRealization,
        CancellationToken cancellationToken)
    {
        if (contentVariableBindings is { Count: > 0 })
        {
            var mergedBindings = new Dictionary<string, string>(candidate.Bindings, StringComparer.Ordinal);
            foreach (var binding in contentVariableBindings)
            {
                if (mergedBindings.TryGetValue(binding.Key, out var existing) &&
                    !string.Equals(existing, binding.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return SemanticTransitionRealization.Ambiguous("result_content_variable_conflicted");
                }
                mergedBindings[binding.Key] = binding.Value;
            }
            candidate = candidate with { Bindings = mergedBindings };
        }
        if (!TryInstantiateFrame(candidate.ResultFrame, candidate.Bindings, out var resultValues))
            return SemanticTransitionRealization.Insufficient("result_semantic_variable_unbound_frame");

        var allowSystemValidatedMachine =
            candidate.EvidenceStandard == BroadGovernedEvidenceStandard;
        var activeExamples =
            from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on example.TextUnitId equals unit.Id
            where example.SupersededUtc == null &&
                example.LanguageCode == languageCode &&
                (example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved ||
                 (allowSystemValidatedMachine &&
                  example.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine)) &&
                unit.LanguageCode == languageCode && unit.IsTrainingEligible &&
                (unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved ||
                 (allowSystemValidatedMachine &&
                  unit.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine))
            select new { Example = example, Unit = unit };

        // Frame relevance is applied in SQL before materialization. This uses
        // the existing variation rows as the semantic index and deliberately
        // does not turn the full English curriculum into an in-memory
        // candidate universe.
        var scopedQuery = activeExamples;
        var layoutQuery = activeExamples;
        // An exact static response may be served only from an example that is
        // itself an active, human-verified endpoint of the selected governed
        // transition. This keeps canonical realization tied to the same
        // transition provenance as selection; a merely similar curriculum
        // example cannot become a conversational answer.
        scopedQuery = scopedQuery.Where(item =>
            _db.Set<LegendSemanticTransitionEvidence>().Any(evidence =>
                evidence.TransitionSignature == candidate.TransitionSignature &&
                evidence.ResultCurriculumExampleId == item.Example.Id &&
                evidence.SourceLanguageCode == languageCode &&
                evidence.ResultLanguageCode == languageCode &&
                evidence.SupersededUtc == null &&
                evidence.ContributionState == "Supported" &&
                ((evidence.IsHumanVerifiedSupport &&
                  evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved) ||
                 (allowSystemValidatedMachine &&
                  !evidence.IsHumanVerifiedSupport &&
                  evidence.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine))));
        foreach (var dimension in candidate.ResultFrame.Dimensions)
        {
            var dimensionName = dimension.Key;
            if (IsStructuralRelationFrameDimension(dimensionName))
                continue;
            if (IsSemanticVariable(dimension.Value))
            {
                if (!candidate.Bindings.TryGetValue(dimension.Value, out var boundValue))
                    return SemanticTransitionRealization.Insufficient("result_semantic_variable_unbound");
                scopedQuery = scopedQuery.Where(item =>
                    _db.Set<LegendCurriculumExampleVariation>().Any(variation =>
                        variation.CurriculumExampleId == item.Example.Id &&
                        variation.Dimension == dimensionName && variation.Value == boundValue));
                layoutQuery = layoutQuery.Where(item =>
                    _db.Set<LegendCurriculumExampleVariation>().Any(variation =>
                        variation.CurriculumExampleId == item.Example.Id &&
                        variation.Dimension == dimensionName));
                continue;
            }

            var staticValue = dimension.Value;
            scopedQuery = scopedQuery.Where(item =>
                _db.Set<LegendCurriculumExampleVariation>().Any(variation =>
                    variation.CurriculumExampleId == item.Example.Id &&
                    variation.Dimension == dimensionName && variation.Value == staticValue));
            layoutQuery = layoutQuery.Where(item =>
                _db.Set<LegendCurriculumExampleVariation>().Any(variation =>
                    variation.CurriculumExampleId == item.Example.Id &&
                    variation.Dimension == dimensionName && variation.Value == staticValue));
        }

        var scopedDatabaseExamples = await scopedQuery
            .Select(item => new SemanticResultExample(
                item.Example.Id,
                item.Example.CurriculumFamilyId,
                item.Example.TextUnitId,
                item.Unit.Text))
            .ToListAsync(cancellationToken);
        var layoutDatabaseExamples = await layoutQuery
            .Select(item => new SemanticResultExample(
                item.Example.Id,
                item.Example.CurriculumFamilyId,
                item.Example.TextUnitId,
                item.Unit.Text))
            .ToListAsync(cancellationToken);
        var examples = scopedDatabaseExamples
            .Concat(layoutDatabaseExamples)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        if (examples.Count == 0)
            return SemanticTransitionRealization.Insufficient("result_canonical_evidence_unknown");

        var exampleIds = examples.Select(item => item.Id).ToArray();
        var variations = await _db.Set<LegendCurriculumExampleVariation>().AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .Select(item => new SemanticVariation(item.CurriculumExampleId, item.Dimension, item.Value))
            .ToListAsync(cancellationToken);
        var variationMaps = variations
            .GroupBy(item => item.ExampleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group.ToDictionary(
                    item => item.Dimension,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase));

        // Keep canonical static realization on the transition's exact
        // Founder-approved result endpoints.  The broader layout set below
        // is evidence for a bound-variable composition only; it must never
        // introduce an otherwise similar curriculum example as a static
        // conversational response.
        var scopedExamples = scopedDatabaseExamples
            .Where(item => variationMaps.TryGetValue(item.Id, out var values) &&
                MatchesInstantiatedSemanticFrame(candidate.ResultFrame, values, candidate.Bindings))
            .ToList();

        // The legacy non-discourse evaluator remains available for historical
        // regression diagnostics. Production conversation uses the composed
        // path below and is never allowed to present this endpoint retrieval
        // as original articulation.
        if (!requireOriginalRealization &&
            !candidate.ResultFrame.Dimensions.Values.Any(IsSemanticVariable) &&
            TryRealizeCanonicalStaticResult(
                scopedExamples,
                out var canonicalText,
                out var canonicalIndependentFamilies))
        {
            return new SemanticTransitionRealization(
                canonicalText,
                canonicalIndependentFamilies,
                null,
                false);
        }

        var anchors = await _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId) &&
                item.LanguageCode == languageCode &&
                (item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved ||
                 (allowSystemValidatedMachine &&
                  item.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine)) &&
                item.SupersededUtc == null && item.LexemeId != null &&
                item.ComponentStartTokenIndex != null && item.ComponentLength != null && item.ComponentLength > 0)
            .Select(item => new SemanticAnchor(
                item.CurriculumExampleId,
                item.Dimension,
                item.Value,
                item.ComponentStartTokenIndex!.Value,
                item.ComponentLength!.Value))
            .ToListAsync(cancellationToken);
        var anchorsByExample = anchors
            .GroupBy(item => item.ExampleId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var frameDimensions = candidate.ResultFrame.Dimensions.Keys
            .Where(item => !IsStructuralRelationFrameDimension(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasResultVariables = candidate.ResultFrame.Dimensions.Values.Any(IsSemanticVariable);
        // Static responses may use only the selected transition's exact
        // Founder-approved endpoints. Bound-variable responses retain the
        // broader same-frame layout evidence needed to realize a new value.
        var layoutExamples = hasResultVariables
            ? examples
            : scopedDatabaseExamples;
        var layouts = new List<SemanticRealizationLayout>();
        foreach (var example in layoutExamples.Where(item =>
                     variationMaps.TryGetValue(item.Id, out var values) &&
                     MatchesStaticSemanticFrame(candidate.ResultFrame, values)))
        {
            if (!anchorsByExample.TryGetValue(example.Id, out var exampleAnchors) ||
                !TryBuildSemanticRealizationLayout(example, exampleAnchors, frameDimensions, out var layout))
            {
                continue;
            }
            layouts.Add(layout);
        }

        var eligibleLayouts = layouts
            .GroupBy(
                item => item.Shape + "\u001f" + item.TerminalPunctuation,
                StringComparer.Ordinal)
            .Select(group => new
            {
                Layouts = group.ToList(),
                IndependentFamilies = group.Select(item => item.FamilyId).Distinct().Count(),
                EvidenceStandard = group.Select(item => item.FamilyId).Distinct().Count() >= 3
                    ? HigherGovernedEvidenceStandard
                    : BroadGovernedEvidenceStandard
            })
            .Where(group => group.IndependentFamilies >= 1)
            .ToList();
        if (eligibleLayouts.Count > 0)
        {
            var highestLayoutStandard = eligibleLayouts.Max(item => item.EvidenceStandard);
            eligibleLayouts = eligibleLayouts
                .Where(item => item.EvidenceStandard == highestLayoutStandard)
                .ToList();
        }
        if (scopedExamples.Count > 0)
        {
            // A native response must remain under this same governed
            // articulation authority. Multiple independently mature layouts are
            // retained as alternative evidence; the downstream original
            // composer may use them without converting layout diversity into a
            // false semantic contradiction.
            var realizationSeed = string.Join('|',
                sourceComponents
                    .OrderBy(item => item.StartTokenIndex)
                    .ThenBy(item => item.Dimension, StringComparer.Ordinal)
                    .Select(item => item.SemanticSignature + "=" + item.SurfaceForm));
            if (eligibleLayouts.Count > 0 && hasResultVariables &&
                TryRealizeGovernedCrossArticulation(
                    eligibleLayouts.SelectMany(item => item.Layouts).ToArray(),
                    scopedExamples,
                    candidate.TransitionSignature + "|" + realizationSeed,
                    out var crossArticulatedText,
                    out var crossArticulationEvidence))
            {
                if (!await IsActiveCurriculumSentenceAsync(
                        languageCode,
                        crossArticulatedText,
                        cancellationToken))
                {
                    return new SemanticTransitionRealization(
                        crossArticulatedText,
                        crossArticulationEvidence,
                        null,
                        false,
                        Math.Min(candidate.EvidenceStandard, HigherGovernedEvidenceStandard),
                        true);
                }
            }

            foreach (var eligibleLayout in eligibleLayouts
                         .OrderByDescending(item => item.IndependentFamilies)
                         .ThenBy(item => item.Layouts[0].Shape, StringComparer.Ordinal)
                         .ThenBy(item => item.Layouts[0].TerminalPunctuation, StringComparer.Ordinal))
            {
                if (!TryRealizeOriginalLearnedLayout(
                        eligibleLayout.Layouts,
                        scopedExamples,
                        candidate.TransitionSignature + "|" + realizationSeed + "|" +
                        eligibleLayout.Layouts[0].Shape,
                        out var text))
                {
                    continue;
                }
                if (await IsActiveCurriculumSentenceAsync(languageCode, text, cancellationToken))
                    continue;
                return new SemanticTransitionRealization(
                    text,
                    eligibleLayout.IndependentFamilies,
                    null,
                    false,
                    Math.Min(candidate.EvidenceStandard, eligibleLayout.EvidenceStandard),
                    true);
            }

            // Original composition remains preferred, but the exact endpoint
            // selected by the same governed transition is valid articulation
            // evidence. Refusing it made known Founder knowledge appear
            // unknown. This is not a phrase fallback: the selected transition
            // signature and instantiated result frame still scope the result.
            if (TryRealizeCanonicalGovernedResult(
                    scopedExamples,
                    out var governedText,
                    out var governedIndependentFamilies))
            {
                var endpointStandard = governedIndependentFamilies >= 3
                    ? HigherGovernedEvidenceStandard
                    : BroadGovernedEvidenceStandard;
                return new SemanticTransitionRealization(
                    governedText,
                    governedIndependentFamilies,
                    null,
                    false,
                    Math.Min(candidate.EvidenceStandard, endpointStandard),
                    false);
            }

            return SemanticTransitionRealization.Insufficient("result_canonical_evidence_unknown");
        }

        // A bound result value can be realized from the governed source only
        // when the result layout itself has three independent Founder families
        // and every non-bound component is invariant across that layout.  This
        // permits a learned compositional response to a new approved source
        // binding without manufacturing a target fact or using a template.
        var boundRealization = TryRealizeBoundSemanticTransitionResult(
            candidate,
            sourceComponents,
            contentVariableBindings,
            layoutDatabaseExamples,
            variationMaps,
            anchorsByExample,
            requireOriginalRealization);
        if (requireOriginalRealization &&
            boundRealization.Reason is null &&
            !string.IsNullOrWhiteSpace(boundRealization.Text) &&
            await IsActiveCurriculumSentenceAsync(
                languageCode,
                boundRealization.Text,
                cancellationToken))
        {
            return SemanticTransitionRealization.Insufficient(
                "result_original_realization_matched_curriculum_sentence");
        }
        return boundRealization;
    }

    private Task<bool> IsActiveCurriculumSentenceAsync(
        string languageCode,
        string text,
        CancellationToken cancellationToken)
    {
        var textHash = LegendLanguageIdentity.TextHash(text);
        return _db.Set<LegendCurriculumExample>().AsNoTracking().AnyAsync(example =>
            example.SupersededUtc == null &&
            example.LanguageCode == languageCode &&
            _db.Set<LegendLanguageTextUnit>().Any(unit =>
                unit.Id == example.TextUnitId &&
                unit.LanguageCode == languageCode &&
                unit.NormalizedHash == textHash),
            cancellationToken);
    }

    private static bool TryRealizeCanonicalStaticResult(
        IReadOnlyList<SemanticResultExample> examples,
        out string text,
        out int independentFamilies) =>
        TryRealizeCanonicalGovernedResult(
            examples,
            3,
            out text,
            out independentFamilies);

    private static bool TryRealizeCanonicalGovernedResult(
        IReadOnlyList<SemanticResultExample> examples,
        out string text,
        out int independentFamilies) =>
        TryRealizeCanonicalGovernedResult(
            examples,
            1,
            out text,
            out independentFamilies);

    private static bool TryRealizeCanonicalGovernedResult(
        IReadOnlyList<SemanticResultExample> examples,
        int minimumIndependentFamilies,
        out string text,
        out int independentFamilies)
    {
        text = string.Empty;
        independentFamilies = examples
            .Select(item => item.CurriculumFamilyId)
            .Distinct()
            .Count();
        if (independentFamilies < minimumIndependentFamilies)
            return false;

        var canonical = examples
            .Select(item => LegendLanguageIdentity.NormalizeText(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => LegendLanguageIdentity.TextHash(item), StringComparer.Ordinal)
            .ThenBy(item => item, StringComparer.Ordinal)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(canonical))
            return false;

        text = canonical;
        return true;
    }

    private static LegendSemanticTransitionInference SemanticTransitionInsufficient(string reason) =>
        new(LegendSemanticTransitionInference.InsufficientEvidence, null, 0, [reason]);

    private static LegendSemanticTransitionInference SemanticTransitionAmbiguous(string reason) =>
        new(LegendSemanticTransitionInference.Ambiguous, null, 0, [reason]);

    private static LegendSemanticTransitionInference SemanticTransitionContradicted(string reason) =>
        new(LegendSemanticTransitionInference.Contradicted, null, 0, [reason]);

    private static bool TryToUnambiguousSemanticValues(
        IReadOnlyList<LegendShadowSourceSemanticComponent> components,
        out IReadOnlyDictionary<string, string> values)
        => TryToUnambiguousSemanticValues(
            components.Select(item => new KeyValuePair<string, string>(item.Dimension, item.Value)),
            out values);

    private static bool TryToUnambiguousSemanticValues(
        IReadOnlyList<LegendConnectUtteranceMeaningNode> nodes,
        out IReadOnlyDictionary<string, string> values)
        => TryToUnambiguousSemanticValues(
            nodes.Select(item => new KeyValuePair<string, string>(item.SemanticDimension, item.SemanticValue)),
            out values);

    private static bool TryToUnambiguousSemanticValues(
        LegendConnectUtteranceMeaningGraphSnapshot graph,
        out IReadOnlyDictionary<string, string> values) =>
        TryToUnambiguousSemanticValues(graph.Nodes, graph.Relations, out values);

    private static bool TryToUnambiguousSemanticValues(
        IReadOnlyList<LegendConnectUtteranceMeaningNode> nodes,
        IReadOnlyList<LegendConnectUtteranceMeaningRelation> relations,
        out IReadOnlyDictionary<string, string> values)
    {
        var components = new List<KeyValuePair<string, string>>(nodes.Count + relations.Count);

        // V20.3: one independently mature primitive is itself a valid semantic
        // frame. Requiring an edge for a one-node meaning incorrectly made
        // atomic conversational acts invisible to transition selection.
        if (nodes.Count == 1 && relations.Count == 0)
        {
            components.Add(new KeyValuePair<string, string>(
                nodes[0].SemanticDimension,
                nodes[0].SemanticValue));

            return TryToUnambiguousSemanticValues(
                components,
                out values);
        }

        // A multi-node composed meaning graph is defined by governed relations,
        // not by every independently recognized surface primitive. A dangling
        // primitive can be legitimate lexical evidence for another meaning,
        // but must not make the active connected meaning ambiguous.
        var participatingNodeIndexes = new HashSet<int>();
        foreach (var relation in relations)
        {
            if (relation.SourceNodeIndex < 0 || relation.SourceNodeIndex >= nodes.Count ||
                relation.TargetNodeIndex < 0 || relation.TargetNodeIndex >= nodes.Count)
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return false;
            }
            participatingNodeIndexes.Add(relation.SourceNodeIndex);
            participatingNodeIndexes.Add(relation.TargetNodeIndex);
        }
        components.AddRange(participatingNodeIndexes.OrderBy(index => index)
            .Select(index => new KeyValuePair<string, string>(
                nodes[index].SemanticDimension,
                nodes[index].SemanticValue)));
        foreach (var relation in relations)
        {
            components.Add(new KeyValuePair<string, string>(
                StructuralRelationFrameDimension(
                    relation.RelationKind,
                    nodes[relation.SourceNodeIndex].SemanticDimension,
                    nodes[relation.TargetNodeIndex].SemanticDimension,
                    clauseKey: null),
                "present"));
        }
        return TryToUnambiguousSemanticValues(components, out values);
    }

    private static bool TryToUnambiguousSemanticValues(
        IEnumerable<KeyValuePair<string, string>> components,
        out IReadOnlyDictionary<string, string> values)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in components.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var alternatives = group.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (alternatives.Length != 1)
            {
                values = resolved;
                return false;
            }
            resolved[group.Key] = alternatives[0];
        }
        values = resolved;
        return resolved.Count > 0;
    }

    private static bool TryReadSemanticFrame(
        string serialized,
        out NormalizedSemanticFrame frame)
    {
        try
        {
            var dimensions = JsonSerializer.Deserialize<Dictionary<string, string>>(serialized);
            var normalized = NormalizeSemanticFrame(dimensions);
            if (normalized is null || !string.Equals(normalized.Serialized, serialized, StringComparison.Ordinal))
            {
                frame = null!;
                return false;
            }
            frame = normalized;
            return true;
        }
        catch (JsonException)
        {
            frame = null!;
            return false;
        }
    }

    private static bool TryBindInputSemanticFrame(
        NormalizedSemanticFrame frame,
        IReadOnlyDictionary<string, string> inputValues,
        bool allowMissingVariables,
        bool allowMissingStaticDimensions,
        out IReadOnlyDictionary<string, string> bindings,
        out IReadOnlyList<SemanticMissingVariable> missingVariables,
        out int directSourceMatchCount)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<SemanticMissingVariable>();
        directSourceMatchCount = 0;
        foreach (var item in frame.Dimensions)
        {
            if (!IsSemanticVariable(item.Value))
            {
                if (!inputValues.TryGetValue(item.Key, out var observed))
                {
                    if (allowMissingStaticDimensions)
                        continue;

                    bindings = resolved;
                    missingVariables = missing;
                    return false;
                }
                if (!string.Equals(observed, item.Value, StringComparison.OrdinalIgnoreCase))
                {
                    bindings = resolved;
                    missingVariables = missing;
                    return false;
                }
                // A complete match of one controlled static source dimension
                // is direct present-turn evidence. It authorizes only the
                // subsequent binding of genuinely missing variables from a
                // governed context frame; it never permits a context-only
                // transition whose current source frame supplied nothing.
                directSourceMatchCount++;
                continue;
            }

            if (!inputValues.TryGetValue(item.Key, out var value))
            {
                if (!allowMissingVariables)
                {
                    bindings = resolved;
                    missingVariables = missing;
                    return false;
                }
                missing.Add(new SemanticMissingVariable(item.Key, item.Value));
                continue;
            }
            if (resolved.TryGetValue(item.Value, out var existing) &&
                !string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                bindings = resolved;
                missingVariables = missing;
                return false;
            }
            resolved[item.Value] = value;
            directSourceMatchCount++;
        }
        bindings = resolved;
        missingVariables = missing;
        return true;
    }

    private static bool TryInstantiateFrame(
        NormalizedSemanticFrame frame,
        IReadOnlyDictionary<string, string> bindings,
        out IReadOnlyDictionary<string, string> values)
    {
        var instantiated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in frame.Dimensions)
        {
            if (IsSemanticVariable(item.Value))
            {
                if (!bindings.TryGetValue(item.Value, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    values = instantiated;
                    return false;
                }
                instantiated[item.Key] = value;
                continue;
            }
            instantiated[item.Key] = item.Value;
        }
        values = instantiated;
        return true;
    }

    private static bool TryInstantiateResponsePlanFrame(
        NormalizedSemanticFrame frame,
        IReadOnlyDictionary<string, string> bindings,
        out IReadOnlyDictionary<string, string> values,
        out IReadOnlyDictionary<string, string> unboundResultVariables)
    {
        var instantiated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unbound = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in frame.Dimensions)
        {
            if (!IsSemanticVariable(item.Value))
            {
                instantiated[item.Key] = item.Value;
                continue;
            }

            if (bindings.TryGetValue(item.Value, out var boundValue) &&
                !string.IsNullOrWhiteSpace(boundValue))
            {
                instantiated[item.Key] = boundValue;
                continue;
            }

            if (unbound.TryGetValue(item.Value, out var existingDimension) &&
                !string.Equals(existingDimension, item.Key, StringComparison.Ordinal))
            {
                values = instantiated;
                unboundResultVariables = unbound;
                return false;
            }
            unbound[item.Value] = item.Key;
            instantiated[item.Key] = item.Value;
        }
        values = instantiated;
        unboundResultVariables = unbound;
        return true;
    }

    private static bool MatchesInstantiatedSemanticFrame(
        NormalizedSemanticFrame frame,
        IReadOnlyDictionary<string, string> variations,
        IReadOnlyDictionary<string, string> bindings) =>
        TryInstantiateFrame(frame, bindings, out var values) &&
        values.All(item => IsStructuralRelationFrameDimension(item.Key) ||
            (variations.TryGetValue(item.Key, out var observed) &&
             string.Equals(item.Value, observed, StringComparison.OrdinalIgnoreCase)));

    private static bool MatchesStaticSemanticFrame(
        NormalizedSemanticFrame frame,
        IReadOnlyDictionary<string, string> variations) =>
        frame.Dimensions.All(item =>
            IsStructuralRelationFrameDimension(item.Key) ||
            (variations.TryGetValue(item.Key, out var observed) &&
             (IsSemanticVariable(item.Value) ||
              string.Equals(item.Value, observed, StringComparison.OrdinalIgnoreCase))));

    private static bool TryBuildSemanticRealizationLayout(
        SemanticResultExample example,
        IReadOnlyList<SemanticAnchor> anchors,
        IReadOnlySet<string> frameDimensions,
        out SemanticRealizationLayout layout)
    {
        var components = BuildSemanticLayoutComponents(example, anchors, frameDimensions);

        // Some valid Founder curricula intentionally govern an entire response
        // as one semantic span instead of assigning artificial semantics to
        // each clause. When that one exact lexical span covers the complete
        // response, derive only its observed punctuation-delimited surface
        // segments and feed them back into this same realization authority.
        // No segment receives invented meaning; the whole-span semantic frame
        // remains the authority and three independent families still have to
        // support one structural layout before any recombination can serve.
        if (components.Count == 1 &&
            TryExpandWholeSpanSurfaceRealizationLayout(
                example, components[0], out var surfaceComponents))
        {
            components = surfaceComponents;
        }

        if (components.Count < 2 ||
            components.Zip(components.Skip(1), (left, right) =>
                    left.StartTokenIndex + left.TokenLength > right.StartTokenIndex)
                .Any(item => item))
        {
            layout = null!;
            return false;
        }

        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            SemanticLayoutShape(components),
            components,
            TerminalPunctuation(example.Text),
            example.Text);
        return true;
    }

    /// <summary>
    /// Identifies the ordered semantic slots in a static result layout. A
    /// meaning graph may legitimately ground more than one non-overlapping
    /// surface span to the same semantic dimension; the occurrence ordinal
    /// preserves those distinct slots without making token length or offset
    /// part of the reusable semantic structure.
    /// </summary>
    private static string SemanticLayoutShape(
        IReadOnlyList<SemanticLayoutComponent> components)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var slots = new string[components.Count];
        for (var index = 0; index < components.Count; index++)
        {
            var dimension = components[index].Dimension;
            var occurrence = occurrences.GetValueOrDefault(dimension) + 1;
            occurrences[dimension] = occurrence;
            slots[index] = $"{dimension}:{occurrence}";
        }
        return string.Join("|", slots);
    }

    /// <summary>
    /// Refines one exact whole-response semantic anchor into observed surface
    /// segments only when internal punctuation supplies an explicit boundary.
    /// This is representation refinement inside the canonical realization
    /// authority: it does not create semantic nodes, templates, phrase rules,
    /// response caches, or another responder.
    /// </summary>
    private static bool TryExpandWholeSpanSurfaceRealizationLayout(
        SemanticResultExample example,
        SemanticLayoutComponent wholeSpan,
        out List<SemanticLayoutComponent> components)
    {
        components = [];
        var normalized = LegendLanguageIdentity.NormalizeText(example.Text);
        var tokens = SurfaceComponents(normalized);
        if (tokens.Count < 2 ||
            wholeSpan.StartTokenIndex != 0 ||
            wholeSpan.TokenLength != tokens.Count)
        {
            return false;
        }

        var terminalPunctuation = TerminalPunctuation(normalized);
        var bodyLength = normalized.Length - terminalPunctuation.Length;
        if (bodyLength <= 0)
            return false;

        var starts = new List<int> { 0 };
        var ends = new List<int>();
        for (var index = 0; index < bodyLength; index++)
        {
            var character = normalized[index];
            if (character is not ('!' or '?' or '.' or ';' or ':') ||
                index + 1 >= bodyLength)
            {
                continue;
            }

            var next = index + 1;
            while (next < bodyLength && char.IsWhiteSpace(normalized[next]))
                next++;
            if (next >= bodyLength)
                continue;

            ends.Add(index + 1);
            starts.Add(next);
            index = next - 1;
        }
        ends.Add(bodyLength);
        if (starts.Count < 2 || starts.Count != ends.Count)
            return false;

        var tokenCursor = 0;
        for (var index = 0; index < starts.Count; index++)
        {
            var surface = normalized[starts[index]..ends[index]].Trim();
            if (string.IsNullOrWhiteSpace(surface))
                return false;
            var segmentTokenCount = SurfaceComponents(surface).Count;
            if (segmentTokenCount <= 0)
                return false;

            components.Add(new SemanticLayoutComponent(
                wholeSpan.Dimension + "#surface-" + (index + 1),
                wholeSpan.Value,
                tokenCursor,
                segmentTokenCount,
                surface));
            tokenCursor += segmentTokenCount;
        }

        if (tokenCursor != wholeSpan.TokenLength)
        {
            components = [];
            return false;
        }
        return components.Count >= 2;
    }

    /// <summary>
    /// Synthesizes one response from two complete grammatical realization
    /// forms of the same already-selected semantic result. A form is eligible
    /// only when its abstract semantic-slot shape recurs in at least three
    /// independent Founder families. The current bound value must already have
    /// an exact scoped endpoint for that form. No words are spliced between
    /// forms; every sentence remains observed Founder language, while the
    /// multi-form response itself is newly composed.
    /// </summary>
    private static bool TryRealizeGovernedCrossArticulation(
        IReadOnlyList<SemanticRealizationLayout> matureLayouts,
        IReadOnlyList<SemanticResultExample> scopedEndpoints,
        string seed,
        out string text,
        out int evidenceCount)
    {
        text = string.Empty;
        evidenceCount = 0;
        if (matureLayouts.Count == 0 || scopedEndpoints.Count == 0)
            return false;

        var scopedTexts = scopedEndpoints
            .Select(item => LegendLanguageIdentity.NormalizeText(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);
        if (scopedTexts.Count < 2)
            return false;

        var forms = matureLayouts
            .GroupBy(
                item => item.Shape + "\u001f" + item.TerminalPunctuation,
                StringComparer.Ordinal)
            .Select(group => new
            {
                Layouts = group.ToArray(),
                IndependentFamilies = group.Select(item => item.FamilyId).Distinct().Count()
            })
            .Where(group => group.IndependentFamilies >= 3)
            .SelectMany(group => group.Layouts
                .Select(item => new
                {
                    Text = LegendLanguageIdentity.NormalizeText(item.ObservedText),
                    group.IndependentFamilies,
                    Shape = item.Shape
                })
                .Where(item => scopedTexts.Contains(item.Text)))
            .Where(item => !HasImmediateRepeatedWord(item.Text))
            .GroupBy(item => item.Shape + "\u001f" + item.Text, StringComparer.Ordinal)
            .Select(group => group.First())
            // Prefer informative complete forms; hash only resolves equal-size
            // ties deterministically and never carries topic logic.
            .OrderByDescending(item => SurfaceComponents(item.Text).Count)
            .ThenBy(item => LegendLanguageIdentity.TextHash(seed + "|" + item.Shape + "|" + item.Text), StringComparer.Ordinal)
            .ToArray();

        if (forms.Length < 2)
            return false;

        // Use two distinct mature forms. Keeping each sentence intact is what
        // prevents the malformed cross-scaffold wording caught by the live
        // conversation proof. The composition is deterministic and bounded.
        var selected = forms.Take(2).ToArray();
        if (string.Equals(selected[0].Text, selected[1].Text, StringComparison.Ordinal))
            return false;

        var combined = LegendLanguageIdentity.NormalizeText(
            selected[0].Text + " " + selected[1].Text);
        if (string.IsNullOrWhiteSpace(combined) || scopedTexts.Contains(combined))
            return false;

        text = combined;
        evidenceCount = selected.Min(item => item.IndependentFamilies);
        return evidenceCount >= 3;
    }

    private static bool HasImmediateRepeatedWord(string text)
    {
        var words = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim('.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\''))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        for (var index = 1; index < words.Length; index++)
        {
            if (string.Equals(words[index - 1], words[index], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryRealizeOriginalLearnedLayout(
        IReadOnlyList<SemanticRealizationLayout> layouts,
        IReadOnlyList<SemanticResultExample> storedEndpoints,
        string seed,
        out string text)
    {
        text = string.Empty;
        if (layouts.Count == 0 || layouts[0].Components.Count < 2)
            return false;

        var componentCount = layouts[0].Components.Count;
        if (layouts.Any(item => item.Components.Count != componentCount))
            return false;

        var alternativesByPosition = new List<string[]>(componentCount);
        for (var position = 0; position < componentCount; position++)
        {
            var reference = layouts[0].Components[position];
            var aligned = layouts.Select(item => item.Components[position]).ToArray();
            if (aligned.Any(item =>
                    !string.Equals(item.Dimension, reference.Dimension, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.Value, reference.Value, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var alternatives = aligned
                .Select(item => LegendLanguageIdentity.NormalizeText(item.SurfaceForm))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => LegendLanguageIdentity.TextHash(item), StringComparer.Ordinal)
                .ThenBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (alternatives.Length == 0)
                return false;
            alternativesByPosition.Add(alternatives);
        }

        var punctuation = layouts
            .Select(item => item.TerminalPunctuation)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (punctuation.Length != 1)
            return false;

        const long maximumCombinations = 4096;
        long combinationCount = 1;
        foreach (var alternatives in alternativesByPosition)
            combinationCount = Math.Min(maximumCombinations, combinationCount * alternatives.Length);
        if (combinationCount <= 1)
            return false;

        var stored = storedEndpoints
            .Select(item => LegendLanguageIdentity.NormalizeText(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);
        var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var startingOrdinal = (long)(BitConverter.ToUInt64(seedBytes, 0) % (ulong)combinationCount);

        for (long attempt = 0; attempt < combinationCount; attempt++)
        {
            var ordinal = (startingOrdinal + attempt) % combinationCount;
            var cursor = ordinal;
            var output = new List<string>(componentCount);
            foreach (var alternatives in alternativesByPosition)
            {
                output.Add(alternatives[(int)(cursor % alternatives.Length)]);
                cursor /= alternatives.Length;
            }

            var candidate = LegendLanguageIdentity.NormalizeText(string.Join(' ', output));
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (!string.IsNullOrWhiteSpace(punctuation[0]))
                candidate += punctuation[0];
            candidate = LegendLanguageIdentity.NormalizeText(candidate);
            if (stored.Contains(candidate))
                continue;

            text = candidate;
            return true;
        }

        return false;
    }

    private static SemanticTransitionRealization TryRealizeBoundSemanticTransitionResult(
        SemanticTransitionCandidate candidate,
        IReadOnlyList<LegendShadowSourceSemanticComponent> sourceComponents,
        IReadOnlyDictionary<string, string>? contentVariableBindings,
        IReadOnlyList<SemanticResultExample> layoutExamples,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> variationMaps,
        IReadOnlyDictionary<Guid, List<SemanticAnchor>> anchorsByExample,
        bool requireOriginalRealization)
    {
        var dynamicDimensions = candidate.ResultFrame.Dimensions
            .Where(item => IsSemanticVariable(item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (dynamicDimensions.Count == 0)
            return SemanticTransitionRealization.Insufficient("result_semantic_frame_unrealized");

        var resultFrameDimensions = candidate.ResultFrame.Dimensions.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var layouts = new List<SemanticRealizationLayout>();
        foreach (var example in layoutExamples.Where(item =>
                     variationMaps.TryGetValue(item.Id, out var values) &&
                     MatchesStaticSemanticFrame(candidate.ResultFrame, values)))
        {
            if (!anchorsByExample.TryGetValue(example.Id, out var anchors) ||
                !TryBuildBoundSemanticRealizationLayout(
                    example,
                    anchors,
                    resultFrameDimensions,
                    out var layout))
            {
                continue;
            }
            layouts.Add(layout);
        }

        var eligibleLayouts = layouts
            .GroupBy(
                item => item.Shape + "\u001f" + item.TerminalPunctuation,
                StringComparer.Ordinal)
            .Select(group => new
            {
                Layouts = group.ToList(),
                IndependentFamilies = group.Select(item => item.FamilyId).Distinct().Count(),
                EvidenceStandard = group.Select(item => item.FamilyId).Distinct().Count() >= 3
                    ? HigherGovernedEvidenceStandard
                    : BroadGovernedEvidenceStandard
            })
            .Where(group => group.IndependentFamilies >= 1)
            .ToList();
        if (eligibleLayouts.Count == 0)
            return SemanticTransitionRealization.Insufficient("result_bound_layout_insufficient");
        var highestLayoutStandard = eligibleLayouts.Max(item => item.EvidenceStandard);
        eligibleLayouts = eligibleLayouts
            .Where(item => item.EvidenceStandard == highestLayoutStandard)
            .ToList();
        if (eligibleLayouts.Count > 1)
            return SemanticTransitionRealization.Ambiguous("ambiguous_result_bound_layout");

        var learnedLayout = eligibleLayouts[0].Layouts;
        if (!TryResolveInvariantBoundLayoutComponents(
                learnedLayout,
                dynamicDimensions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                out var fixedComponents))
        {
            return SemanticTransitionRealization.Ambiguous("contradictory_result_bound_components");
        }

        var boundSurfaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dynamicDimension in dynamicDimensions)
        {
            if (!candidate.Bindings.TryGetValue(dynamicDimension.Value, out var boundValue) ||
                string.IsNullOrWhiteSpace(boundValue))
            {
                return SemanticTransitionRealization.Insufficient("result_semantic_variable_unbound_layout");
            }

            var contentBound = contentVariableBindings is not null &&
                contentVariableBindings.ContainsKey(dynamicDimension.Value);
            var sourceSurfaces = sourceComponents
                .Where(item =>
                    string.Equals(item.Dimension, dynamicDimension.Key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Value, boundValue, StringComparison.OrdinalIgnoreCase))
                .Select(item => LegendLanguageIdentity.NormalizeText(item.SurfaceForm))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var layoutSurfaces = learnedLayout
                .SelectMany(item => item.Components)
                .Where(item =>
                    string.Equals(item.Dimension, dynamicDimension.Key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Value, boundValue, StringComparison.OrdinalIgnoreCase))
                .Select(item => LegendLanguageIdentity.NormalizeText(item.SurfaceForm))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // A current utterance can omit a value that Stage 3 has already
            // bound from governed discourse.  Such a value cannot be copied
            // from the current source surface.  Its canonical result-layout
            // realization is admissible only when the Founder evidence has
            // one unambiguous surface for this exact semantic value.  This is
            // the same bounded realization authority used for Stage-6 content
            // values; it is never a transcript lookup or a free-text fallback.
            var surfaces = contentBound
                ? layoutSurfaces
                : sourceSurfaces.Length == 1
                    ? sourceSurfaces
                    : layoutSurfaces;
            if (surfaces.Length != 1)
                return SemanticTransitionRealization.Insufficient(
                    contentBound
                        ? "result_bound_content_surface_unknown"
                        : "result_bound_source_surface_unknown");
            boundSurfaces[dynamicDimension.Key] = surfaces[0];
        }

        var output = new List<string>();
        foreach (var position in learnedLayout[0].Components)
        {
            if (boundSurfaces.TryGetValue(position.Dimension, out var dynamicSurface))
            {
                output.Add(dynamicSurface);
                continue;
            }
            if (!fixedComponents.TryGetValue(position.Dimension, out var fixedComponent))
                return SemanticTransitionRealization.Insufficient("result_bound_layout_component_missing");
            output.Add(fixedComponent.SurfaceForm);
        }

        var punctuation = learnedLayout
            .Select(item => item.TerminalPunctuation)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (punctuation.Length != 1)
            return SemanticTransitionRealization.Ambiguous("ambiguous_result_bound_punctuation");

        var text = LegendLanguageIdentity.NormalizeText(string.Join(' ', output));
        if (string.IsNullOrWhiteSpace(text))
            return SemanticTransitionRealization.Insufficient("result_bound_layout_component_missing");
        if (!string.IsNullOrWhiteSpace(punctuation[0]))
            text += punctuation[0];
        text = LegendLanguageIdentity.NormalizeText(text);
        return new SemanticTransitionRealization(
            text,
            eligibleLayouts[0].IndependentFamilies,
            null,
            false,
            Math.Min(candidate.EvidenceStandard, eligibleLayouts[0].EvidenceStandard),
            !layoutExamples.Any(item => string.Equals(
                LegendLanguageIdentity.NormalizeText(item.Text),
                text,
                StringComparison.Ordinal)));
    }

    private static bool TryBuildBoundSemanticRealizationLayout(
        SemanticResultExample example,
        IReadOnlyList<SemanticAnchor> anchors,
        IReadOnlySet<string> resultFrameDimensions,
        out SemanticRealizationLayout layout)
    {
        var components = BuildSemanticLayoutComponents(example, anchors, resultFrameDimensions);
        if (components.Count < 2 ||
            components.Select(item => item.Dimension).Distinct(StringComparer.OrdinalIgnoreCase).Count() != components.Count ||
            components.Zip(components.Skip(1), (left, right) =>
                    left.StartTokenIndex + left.TokenLength > right.StartTokenIndex)
                .Any(item => item))
        {
            layout = null!;
            return false;
        }

        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            string.Join("|", components.Select(item => item.Dimension)),
            components,
            TerminalPunctuation(example.Text),
            example.Text);
        return true;
    }

    /// <summary>
    /// Selects output components only from exact lexical Founder anchors. If a
    /// lexical control and a semantic-frame annotation share a span, the
    /// lexical control realizes that span. A frame dimension remains eligible
    /// when it is the sole exact lexical control. No value is inferred.
    /// </summary>
    private static List<SemanticLayoutComponent> BuildSemanticLayoutComponents(
        SemanticResultExample example,
        IReadOnlyList<SemanticAnchor> anchors,
        IReadOnlySet<string> resultFrameDimensions)
    {
        return anchors
            .GroupBy(item => (item.StartTokenIndex, item.TokenLength))
            .SelectMany(group =>
            {
                var lexicalControls = group
                    .Where(item => !resultFrameDimensions.Contains(item.Dimension))
                    .ToList();
                return lexicalControls.Count > 0 ? lexicalControls : group.ToList();
            })
            .GroupBy(item => (item.Dimension, item.StartTokenIndex, item.TokenLength, item.Value))
            .Select(group => group.First())
            .Select(item => new SemanticLayoutComponent(
                item.Dimension,
                item.Value,
                item.StartTokenIndex,
                item.TokenLength,
                ExtractAnchorSurface(example.Text, item.StartTokenIndex, item.TokenLength)))
            .Where(item => !string.IsNullOrWhiteSpace(item.SurfaceForm))
            .OrderBy(item => item.StartTokenIndex)
            .ThenBy(item => item.TokenLength)
            .ToList();
    }

    private static bool TryResolveInvariantBoundLayoutComponents(
        IReadOnlyList<SemanticRealizationLayout> layouts,
        IReadOnlySet<string> dynamicDimensions,
        out IReadOnlyDictionary<string, SemanticLayoutComponent> components)
    {
        var resolved = new Dictionary<string, SemanticLayoutComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in layouts.SelectMany(item => item.Components)
                     .Where(item => !dynamicDimensions.Contains(item.Dimension))
                     .GroupBy(item => item.Dimension, StringComparer.OrdinalIgnoreCase))
        {
            var possibilities = group
                .GroupBy(item => item.Value + "\u001f" + item.SurfaceForm, StringComparer.Ordinal)
                .Select(item => item.First())
                .ToArray();
            if (possibilities.Length != 1)
            {
                components = resolved;
                return false;
            }
            resolved[group.Key] = possibilities[0];
        }

        components = resolved;
        return true;
    }

    private static string ExtractAnchorSurface(string text, int startTokenIndex, int tokenLength)
    {
        var normalized = LegendLanguageIdentity.NormalizeText(text);
        var components = SurfaceComponents(normalized);
        if (startTokenIndex < 0 || tokenLength < 1 || startTokenIndex + tokenLength > components.Count)
            return string.Empty;
        var first = components[startTokenIndex];
        var last = components[startTokenIndex + tokenLength - 1];
        return normalized[first.CharacterOffset..(last.CharacterOffset + last.CharacterLength)];
    }

    private static string TerminalPunctuation(string text)
    {
        var normalized = LegendLanguageIdentity.NormalizeText(text).Trim();
        var suffix = new string(normalized.Reverse()
            .TakeWhile(character => !char.IsLetterOrDigit(character))
            .Reverse()
            .ToArray());
        return suffix.Length <= 4 ? suffix : string.Empty;
    }

    private static string CanonicalBindings(IReadOnlyDictionary<string, string> bindings) =>
        JsonSerializer.Serialize(bindings.OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));

    /// <summary>
    /// Evaluates an explicitly bounded, previously unseen target construction
    /// against the active canonical evidence graph. It is deliberately
    /// read-only: no composition is persisted, no corpus asset is generated,
    /// and no production translation path can receive a formulation from it.
    /// </summary>
    public async Task<LegendShadowCompositionCapability> EvaluateShadowCompositionAsync(
        LegendShadowCompositionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(
            request.SourceLanguageCode, cancellationToken);
        var targetLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(
            request.TargetLanguageCode, cancellationToken);
        if (sourceLanguage is null || targetLanguage is null ||
            string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return Insufficient("invalid_language_pair");
        }

        var normalizedTarget = LegendLanguageIdentity.NormalizeText(request.ProposedTargetText);
        var components = NormalizeShadowComponents(request.Components, normalizedTarget);
        var relationships = NormalizeShadowRequirements(request.RequiredRelationships);
        if (string.IsNullOrWhiteSpace(normalizedTarget) || components is null || relationships is null)
            return Insufficient("invalid_shadow_composition_request");
        if (components.Count > 24 || relationships.Count is < 1 or > 8)
            return Insufficient("shadow_composition_request_limit");

        var pairKey = LegendLanguageIdentity.PairKey(sourceLanguage, targetLanguage);
        var exactObserved = await (
            from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where alignment.PairKey == pairKey && alignment.SupersededUtc == null && alignment.HumanVerified &&
                target.IsTrainingEligible && target.LanguageCode == targetLanguage &&
                target.NormalizedHash == LegendLanguageIdentity.TextHash(normalizedTarget)
            select alignment.Id
        ).AnyAsync(cancellationToken);
        if (exactObserved)
        {
            return new LegendShadowCompositionCapability(
                LegendShadowCompositionCapability.ExactObserved,
                true,
                false,
                ["exact_trusted_target_observed"]);
        }

        var semanticSignatures = components.Select(item => item.SemanticSignature).Distinct(StringComparer.Ordinal).ToArray();
        var sourceSignatures = await _db.Set<LegendLanguageCompositionalAnchor>()
            .AsNoTracking()
            .Where(item => item.LanguageCode == sourceLanguage &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                item.SupersededUtc == null && item.SemanticSignature != null &&
                semanticSignatures.Contains(item.SemanticSignature))
            .Select(item => item.SemanticSignature!)
            .Distinct()
            .ToListAsync(cancellationToken);
        var missingSourceSemantics = semanticSignatures.Except(sourceSignatures, StringComparer.Ordinal).ToList();
        if (missingSourceSemantics.Count > 0)
            return Insufficient("source_semantic_component_unknown");

        var targetAnchorProofs = await (
            from anchor in _db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
            join lexeme in _db.Set<LegendLanguageLexeme>().AsNoTracking()
                on anchor.LexemeId equals lexeme.Id
            where anchor.LanguageCode == targetLanguage &&
                anchor.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                anchor.SupersededUtc == null && anchor.SemanticSignature != null &&
                semanticSignatures.Contains(anchor.SemanticSignature)
            select new { SemanticSignature = anchor.SemanticSignature!, lexeme.SurfaceForm }
        ).ToListAsync(cancellationToken);
        var realizedComponents = targetAnchorProofs
            .Select(item => ComponentIdentity(item.SemanticSignature, item.SurfaceForm))
            .ToHashSet(StringComparer.Ordinal);
        if (components.Any(item => !realizedComponents.Contains(ComponentIdentity(item.SemanticSignature, item.SurfaceForm))))
            return Insufficient("known_semantic_component_not_realized");

        var propositionRequirements = relationships
            .Select(item => new ShadowPropositionRequirement(
                item.Dimension,
                ControlledPropositionSignature(item.Dimension, item.FirstValue, item.SecondValue)))
            .ToList();
        var patternStates = new Dictionary<string, LegendLanguageStructuralPattern>(StringComparer.Ordinal);
        foreach (var requirement in propositionRequirements)
        {
            var pattern = await _db.Set<LegendLanguageStructuralPattern>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.PairKey == pairKey && item.LanguageCode == targetLanguage &&
                    item.VariationDimension == requirement.Dimension &&
                    item.PropositionSignature == requirement.PropositionSignature && item.SupersededUtc == null,
                    cancellationToken);
            if (pattern is null || pattern.SupportCount < 3 || pattern.IndependentSourceCount < 3 ||
                pattern.HumanVerifiedSupportCount < 3)
            {
                return Insufficient("required_proposition_not_supported:" + requirement.Dimension);
            }
            if (pattern.ContradictionCount > 0)
                return Contradicted("required_proposition_contradicted:" + requirement.Dimension);
            if (pattern.MaturityState is not ("Supported" or "Validated"))
                return Insufficient("required_proposition_not_mature:" + requirement.Dimension);
            patternStates.Add(requirement.Dimension, pattern);
        }

        var requestedLayout = ShadowAnchorLayout(components);
        foreach (var requirement in relationships)
        {
            var relationResult = await EvaluateShadowRelationshipAsync(
                pairKey,
                targetLanguage,
                requirement.Dimension,
                requestedLayout,
                cancellationToken);
            if (relationResult == ShadowRelationshipState.Contradicted)
                return Contradicted("required_relationship_contradicted:" + requirement.Dimension);
            if (relationResult != ShadowRelationshipState.Supported)
                return Insufficient("required_relationship_not_supported:" + requirement.Dimension);
        }

        // Existing explicit validation belongs to a proposition. Relationship
        // maturity has no independent validation transition, so all required
        // propositions must be validated before exposing the narrower
        // validated-for-shadow distinction.
        var allValidated = patternStates.Values.All(item => item.MaturityState == "Validated");
        return new LegendShadowCompositionCapability(
            allValidated
                ? LegendShadowCompositionCapability.ValidatedForShadowEvaluation
                : LegendShadowCompositionCapability.SupportedForShadowEvaluation,
            false,
            false,
            ["active_founder_supported_semantics", "active_supported_structural_relationships"]);
    }

    private static string CanonicalSemanticFrameIdentity(
        IEnumerable<ShadowSourceSemanticCandidate> components) =>
        string.Join(
            "|",
            components
                .Select(item => item.SemanticSignature)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal));

    private static string CanonicalSourceSegmentationIdentity(
        IEnumerable<ShadowSourceSemanticCandidate> components) =>
        string.Join(
            "|",
            components
                .OrderBy(item => item.StartTokenIndex)
                .ThenBy(item => item.TokenLength)
                .ThenBy(item => item.SemanticSignature, StringComparer.Ordinal)
                .Select(item =>
                    $"{item.StartTokenIndex:D4}:{item.TokenLength:D4}:{item.SemanticSignature}"));

    /// <summary>
    /// Reads the narrow historical counterpart of a persisted source-frame
    /// projection. It is intentionally limited to an exact canonical source
    /// text identity and to semantic-transition signatures that already pass
    /// native production eligibility. No semantic value is inferred from the
    /// text itself: every value comes from the active Founder-controlled
    /// variation that satisfies the persisted transition source frame.
    /// </summary>
    private async Task<IReadOnlyList<ShadowSourceSemanticCandidate>>
        ReadHistoricalSourceFrameProjectionCandidatesAsync(
            string sourceLanguage,
            string normalizedText,
            IReadOnlyList<string> inputTokens,
            CancellationToken cancellationToken)
    {
        if (inputTokens.Count == 0)
            return [];

        var sourceHash = LegendLanguageIdentity.TextHash(normalizedText);
        var endpointEvidence = await (
            from evidence in _db.Set<LegendSemanticTransitionEvidence>().AsNoTracking()
            join example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                on evidence.SourceCurriculumExampleId equals example.Id
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on example.TextUnitId equals unit.Id
            where evidence.SourceLanguageCode == sourceLanguage &&
                evidence.ResultLanguageCode == sourceLanguage &&
                evidence.SupersededUtc == null &&
                evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                evidence.ContributionState == "Supported" &&
                evidence.IsHumanVerifiedSupport &&
                example.LanguageCode == sourceLanguage &&
                example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                example.DerivedFromCurriculumExampleId == null &&
                example.SupersededUtc == null &&
                unit.LanguageCode == sourceLanguage &&
                unit.NormalizedHash == sourceHash &&
                unit.IsTrainingEligible &&
                unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
            select new HistoricalSourceFrameEvidence(
                evidence.TransitionSignature,
                evidence.SourceCurriculumExampleId,
                evidence.SourceSemanticFrame)
        ).Take(64).ToListAsync(cancellationToken);
        if (endpointEvidence.Count == 0)
            return [];

        var eligibleSignatures =
            await GetProductionEligibleSemanticTransitionSignaturesAsync(
                sourceLanguage,
                endpointEvidence
                    .Select(item => item.TransitionSignature)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                cancellationToken);
        if (eligibleSignatures.Count == 0)
            return [];

        var exampleIds = endpointEvidence
            .Where(item => eligibleSignatures.Contains(item.TransitionSignature))
            .Select(item => item.SourceCurriculumExampleId)
            .Distinct()
            .ToArray();
        if (exampleIds.Length == 0)
            return [];

        var variations = await _db.Set<LegendCurriculumExampleVariation>()
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var variationsByExample = variations
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group.ToDictionary(
                    item => item.Dimension,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase));

        var candidates = new List<ShadowSourceSemanticCandidate>();
        foreach (var evidence in endpointEvidence.Where(item =>
                     eligibleSignatures.Contains(item.TransitionSignature)))
        {
            if (!variationsByExample.TryGetValue(
                    evidence.SourceCurriculumExampleId,
                    out var variationMap) ||
                !TryResolveControlledSourceFrame(
                    evidence.SourceSemanticFrame,
                    variationMap,
                    out var semanticValues))
            {
                continue;
            }

            foreach (var semanticValue in semanticValues)
            {
                candidates.Add(new ShadowSourceSemanticCandidate(
                    semanticValue.Key,
                    semanticValue.Value,
                    normalizedText,
                    0,
                    inputTokens.Count,
                    SemanticSignature(semanticValue.Key, semanticValue.Value),
                    [evidence.SourceCurriculumExampleId],
                    IsDirectFounderFrameProjection: true));
            }
        }

        return candidates;
    }

    private static bool TryResolveControlledSourceFrame(
        string sourceSemanticFrame,
        IReadOnlyDictionary<string, string> variationMap,
        out IReadOnlyDictionary<string, string> semanticValues)
    {
        semanticValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryReadSemanticFrame(sourceSemanticFrame, out var sourceFrame))
            return false;

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dimension in sourceFrame.Dimensions)
        {
            if (!variationMap.TryGetValue(dimension.Key, out var controlledValue) ||
                string.IsNullOrWhiteSpace(controlledValue) ||
                (!IsSemanticVariable(dimension.Value) &&
                 !string.Equals(
                     dimension.Value,
                     controlledValue,
                     StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            resolved[dimension.Key] = controlledValue;
        }

        if (resolved.Count == 0)
            return false;

        semanticValues = resolved;
        return true;
    }

    private async Task<bool> IsFounderCoRealizationAsync(
        string sourceLanguage,
        IReadOnlyList<ShadowSourceSemanticCandidate> span,
        CancellationToken cancellationToken)
    {
        if (span.Count < 2)
            return false;

        var signatures = span
            .Select(item => item.SemanticSignature)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (signatures.Length < 2)
            return false;

        var commonExampleIds =
            span[0].CurriculumExampleIds.ToHashSet();

        foreach (var candidate in span.Skip(1))
        {
            commonExampleIds.IntersectWith(
                candidate.CurriculumExampleIds);
        }

        if (commonExampleIds.Count == 0)
            return false;

        if (span.All(item => item.IsDirectFounderFrameProjection))
        {
            // These candidates were emitted only by the bounded historical
            // source-frame authority above: one exact canonical Founder source
            // endpoint, its persisted controlled variations, and an already
            // production-eligible transition. Multiple dimensions on that
            // endpoint are direct co-annotation. A conflicting controlled
            // value for one dimension remains ambiguous and cannot be
            // collapsed by this compatibility path.
            return span
                .GroupBy(item => item.Dimension, StringComparer.OrdinalIgnoreCase)
                .All(group => group
                    .Select(item => item.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .Count() == 1);
        }

        foreach (var exampleId in commonExampleIds)
        {
            var directAnchors =
                await _db.Set<LegendLanguageCompositionalAnchor>()
                    .AsNoTracking()
                    .Where(item =>
                        item.CurriculumExampleId == exampleId &&
                        item.LanguageCode == sourceLanguage &&
                        item.Provenance ==
                            LegendConnectKnowledgeProvenance
                                .FounderApproved &&
                        item.SupersededUtc == null &&
                        item.LexemeId != null &&
                        item.SemanticSignature != null &&
                        signatures.Contains(
                            item.SemanticSignature) &&
                        item.ComponentStartTokenIndex ==
                            span[0].StartTokenIndex &&
                        item.ComponentLength ==
                            span[0].TokenLength)
                    .ToListAsync(cancellationToken);

            if (directAnchors
                    .Select(item => item.SemanticSignature!)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != signatures.Length)
            {
                continue;
            }

            var variations =
                await _db.Set<LegendCurriculumExampleVariation>()
                    .AsNoTracking()
                    .Where(item =>
                        item.CurriculumExampleId == exampleId)
                    .ToListAsync(cancellationToken);

            var variationSignatures = variations
                .Select(item =>
                    SemanticSignature(
                        item.Dimension,
                        item.Value))
                .ToHashSet(StringComparer.Ordinal);

            if (signatures.Any(signature =>
                    !variationSignatures.Contains(signature)))
            {
                continue;
            }

            // Multiple controlled dimensions may deliberately describe the
            // same explicitly grounded Founder span (for example, lexical
            // form, speech act, and discourse role).  That is direct
            // co-annotation, not competing inference.  The exact source
            // example is the authority for this narrow case; no contextual
            // or language-specific rule is used to choose between meanings.
            return true;
        }

        return false;
    }

    private async Task<bool> HasSupportedSourceSemanticContextAsync(
        string sourceLanguage,
        IReadOnlyList<ShadowSourceSemanticCandidate> components,
        CancellationToken cancellationToken)
    {
        if (components.Count < 2)
            return false;

        var requestedLayout = RelativeShadowAnchorLayout(
            components.Select(item => (
                item.Dimension,
                item.StartTokenIndex,
                item.TokenLength)));

        var supported = false;

        foreach (var dimension in components
                     .Select(item => item.Dimension)
                     .Distinct(StringComparer.Ordinal))
        {
            var state = await EvaluateShadowRelationshipAsync(
                string.Empty,
                sourceLanguage,
                dimension,
                requestedLayout,
                cancellationToken,
                useRelativeLayout: true);

            // A contradiction anywhere in this proposed source layout keeps the
            // hypothesis closed even if another dimension has support.
            if (state == ShadowRelationshipState.Contradicted)
                return false;

            if (state == ShadowRelationshipState.Supported)
                supported = true;
        }

        return supported;
    }

    private async Task<ShadowRelationshipState> EvaluateShadowRelationshipAsync(
        string pairKey,
        string targetLanguage,
        string dimension,
        string requestedLayout,
        CancellationToken cancellationToken,
        bool useRelativeLayout = false)
    {
        // The request has a maximum of eight relationship dimensions and each
        // dimension reads at most sixteen existing supporting observations.
        // This is a bounded evidence lookup, never a corpus-wide combination.
        var relationships = await _db.Set<LegendLanguageStructuralRelationship>()
            .AsNoTracking()
            .Where(item => item.PairKey == pairKey && item.LanguageCode == targetLanguage &&
                item.VariationDimension == dimension && item.SupersededUtc == null)
            .OrderByDescending(item => item.UpdatedUtc)
            .Take(16)
            .ToListAsync(cancellationToken);
        if (relationships.Count == 0)
            return ShadowRelationshipState.Insufficient;

        var relationshipIds = relationships.Select(item => item.Id).ToArray();
        var evidence = await _db.Set<LegendLanguageStructuralEvidence>()
            .AsNoTracking()
            .Where(item => relationshipIds.Contains(item.StructuralRelationshipId ?? Guid.Empty) &&
                item.StructuralRelationshipContributionState == "Supported" && item.SupersededUtc == null)
            .OrderByDescending(item => item.CreatedUtc)
            .Take(128)
            .Select(item => new { item.StructuralRelationshipId, item.BaselineCurriculumExampleId, item.ComparedCurriculumExampleId })
            .ToListAsync(cancellationToken);
        if (evidence.Count == 0)
            return ShadowRelationshipState.Insufficient;

        var exampleIds = evidence.SelectMany(item => new[]
            { item.BaselineCurriculumExampleId, item.ComparedCurriculumExampleId }).Distinct().ToArray();
        var anchors = await _db.Set<LegendLanguageCompositionalAnchor>()
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId) && item.LanguageCode == targetLanguage &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved && item.SupersededUtc == null &&
                item.SemanticSignature != null && item.ComponentStartTokenIndex != null && item.ComponentLength != null &&
                item.ComponentLength > 0)
            .Select(item => new ShadowAnchor(
                item.CurriculumExampleId,
                item.Dimension,
                item.ComponentStartTokenIndex!.Value,
                item.ComponentLength!.Value))
            .ToListAsync(cancellationToken);
        var layouts = anchors
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => useRelativeLayout
                    ? RelativeShadowAnchorLayout(group.Select(item => (
                        item.Dimension,
                        item.StartTokenIndex,
                        item.TokenLength)))
                    : ShadowAnchorLayout(group),
                EqualityComparer<Guid>.Default);

        var compatibleRelationshipIds = evidence
            .Where(item => layouts.GetValueOrDefault(item.BaselineCurriculumExampleId) == requestedLayout ||
                layouts.GetValueOrDefault(item.ComparedCurriculumExampleId) == requestedLayout)
            .Select(item => item.StructuralRelationshipId!.Value)
            .Distinct()
            .ToHashSet();
        if (compatibleRelationshipIds.Count == 0)
            return ShadowRelationshipState.Insufficient;

        var compatible = relationships.Where(item => compatibleRelationshipIds.Contains(item.Id)).ToList();
        if (compatible.Any(item => item.ContradictionCount > 0))
            return ShadowRelationshipState.Contradicted;
        return compatible.Any(item => item.MaturityState is "Supported" or "Validated" &&
                item.SupportCount >= 3 && item.IndependentSourceCount >= 3 &&
                item.HumanVerifiedSupportCount >= 3 && item.ProviderOnlySupportCount == 0)
            ? ShadowRelationshipState.Supported
            : ShadowRelationshipState.Insufficient;
    }

    private static IReadOnlyList<ShadowCompositionComponent>? NormalizeShadowComponents(
        IReadOnlyList<LegendShadowCompositionComponent>? input,
        string normalizedTarget)
    {
        if (input is null || input.Count is < 1 or > 24)
            return null;
        var tokens = normalizedTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<ShadowCompositionComponent>(input.Count);
        foreach (var component in input)
        {
            var dimension = NormalizeOptional(component.Dimension, 80);
            var value = NormalizeOptional(component.Value, 160);
            var surface = NormalizeOptional(component.SurfaceForm, 256);
            if (dimension is null || value is null || surface is null || component.StartTokenIndex < 0 ||
                component.TokenLength is < 1 or > 8 || component.StartTokenIndex + component.TokenLength > tokens.Length)
            {
                return null;
            }
            var realizedSurface = string.Join(' ', tokens.Skip(component.StartTokenIndex).Take(component.TokenLength));
            if (!string.Equals(LegendLanguageIdentity.NormalizeText(surface), realizedSurface, StringComparison.OrdinalIgnoreCase))
                return null;
            normalized.Add(new ShadowCompositionComponent(
                dimension,
                value,
                realizedSurface.ToLowerInvariant(),
                component.StartTokenIndex,
                component.TokenLength,
                SemanticSignature(dimension, value)));
        }
        return normalized.Select(item => item.Dimension).Distinct(StringComparer.Ordinal).Count() == normalized.Count &&
            !HasOverlappingComponents(normalized)
            ? normalized
            : null;
    }

    private static IReadOnlyList<ShadowRelationshipRequirement>? NormalizeShadowRequirements(
        IReadOnlyList<LegendShadowCompositionRelationshipRequirement>? input)
    {
        if (input is null || input.Count is < 1 or > 8)
            return null;
        var normalized = new List<ShadowRelationshipRequirement>(input.Count);
        foreach (var requirement in input)
        {
            var dimension = NormalizeOptional(requirement.Dimension, 80);
            var first = NormalizeOptional(requirement.FirstValue, 160);
            var second = NormalizeOptional(requirement.SecondValue, 160);
            if (dimension is null || first is null || second is null ||
                string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            normalized.Add(new ShadowRelationshipRequirement(dimension, first, second));
        }
        return normalized.Select(item => item.Dimension).Distinct(StringComparer.Ordinal).Count() == normalized.Count
            ? normalized
            : null;
    }

    private static bool HasOverlappingComponents(IReadOnlyList<ShadowCompositionComponent> components) =>
        components.OrderBy(item => item.StartTokenIndex)
            .Zip(components.OrderBy(item => item.StartTokenIndex).Skip(1),
                (left, right) => left.StartTokenIndex + left.TokenLength > right.StartTokenIndex)
            .Any(item => item);

    private static string ComponentIdentity(string semanticSignature, string surfaceForm) =>
        semanticSignature + "|" + LegendLanguageIdentity.NormalizeText(surfaceForm).ToLowerInvariant();

    private static string ShadowAnchorLayout(IEnumerable<ShadowAnchor> anchors) =>
        string.Join('|', anchors
            .DistinctBy(item => (
                item.Dimension,
                item.StartTokenIndex,
                item.TokenLength))
            .OrderBy(item => item.StartTokenIndex)
            .ThenBy(item => item.TokenLength)
            .ThenBy(item => item.Dimension, StringComparer.Ordinal)
            .Select(item => $"{item.Dimension}:{item.StartTokenIndex}:{item.TokenLength}"));

    private static string ShadowAnchorLayout(IEnumerable<ShadowCompositionComponent> components) =>
        string.Join('|', components
            .OrderBy(item => item.StartTokenIndex)
            .ThenBy(item => item.TokenLength)
            .ThenBy(item => item.Dimension, StringComparer.Ordinal)
            .Select(item => $"{item.Dimension}:{item.StartTokenIndex}:{item.TokenLength}"));

    private static string RelativeShadowAnchorLayout(
        IEnumerable<(
            string Dimension,
            int StartTokenIndex,
            int TokenLength)> components) =>
        string.Join('|', components
            .DistinctBy(item => (
                item.Dimension,
                item.StartTokenIndex,
                item.TokenLength))
            .OrderBy(item => item.StartTokenIndex)
            .ThenBy(item => item.TokenLength)
            .ThenBy(item => item.Dimension, StringComparer.Ordinal)
            .Select(item => $"{item.Dimension}:{item.TokenLength}"));

    private static LegendShadowCompositionCapability Insufficient(string reason) => new(
        LegendShadowCompositionCapability.InsufficientEvidence,
        false,
        false,
        [reason]);

    private static LegendShadowCompositionCapability Contradicted(string reason) => new(
        LegendShadowCompositionCapability.Contradicted,
        false,
        false,
        [reason]);

    /// <summary>
    /// Extends the existing lexical-observation extractor to any registry
    /// language. It records only observed component boundaries and adjacency;
    /// no language-specific parser, lemma, or grammatical claim is created.
    /// </summary>
    private async Task EnsureLanguageLexicalObservationsAsync(
        IReadOnlyList<AtomicInput> inputs,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var activeInputs = inputs
            .Where(item => item.TextUnit.IsTrainingEligible &&
                string.Equals(item.TextUnit.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.TextUnit.Id)
            .Select(group => group.First())
            .ToList();
        if (activeInputs.Count == 0)
            return;

        var observationProvenance =
            activeInputs.All(item =>
                item.TextUnit.Provenance ==
                    LegendConnectKnowledgeProvenance.FounderApproved)
                ? LegendConnectKnowledgeProvenance.FounderApproved
                : activeInputs.All(item =>
                    item.TextUnit.Provenance ==
                        LegendConnectKnowledgeProvenance
                            .SystemValidatedMachine)
                    ? LegendConnectKnowledgeProvenance
                        .SystemValidatedMachine
                    : LegendConnectKnowledgeProvenance
                        .ProviderDerived;

        var componentsByTextUnit = activeInputs.ToDictionary(
            item => item.TextUnit.Id,
            item => SurfaceComponents(item.TextUnit.Text));
        var allComponents = componentsByTextUnit.Values.SelectMany(item => item)
            .DistinctBy(item => item.NormalizedHash, StringComparer.Ordinal)
            .ToList();
        var lexemesByHash = await LegendConnectCanonicalCurriculumPersistence.AdmitLexemesAsync(
            _db,
            languageCode,
            allComponents.Select(item => new LegendCanonicalLexemeAdmission(
                item.NormalizedHash,
                item.NormalizedText,
                observationProvenance)).ToArray(),
            cancellationToken);
        // A durable family keeps its ownership transaction through canonical
        // writes. Insert canonical lexical identities in their actual unique
        // identity order, not the random storage GUID order. Parallel family
        // evaluators can otherwise acquire the lexeme unique-index pages in
        // opposing orders even when their source units are independent.
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(cancellationToken);

        var textUnitIds = activeInputs.Select(item => item.TextUnit.Id).ToArray();
        // A fresh text-unit identity cannot already own lexical observations
        // or relationships.  Retaining that fact from the canonical source
        // admission prevents an impossible table scan from reading another
        // family's uncommitted rows while both owners hold their canonical
        // mutation transaction. Reused identities still take the existing
        // database lookup and normal reactivation path.
        var reusableTextUnitIds = activeInputs
            .Where(item => !item.CanonicalIdentityKnownNew)
            .Select(item => item.TextUnit.Id)
            .ToArray();
        var existingOccurrences = reusableTextUnitIds.Length == 0
            ? new Dictionary<(Guid TextUnitId, int TokenIndex), LegendLanguageLexicalOccurrence>()
            : await _db.Set<LegendLanguageLexicalOccurrence>()
                .Where(item => reusableTextUnitIds.Contains(item.TextUnitId))
                .ToDictionaryAsync(item => (item.TextUnitId, item.TokenIndex), cancellationToken);
        var observationsChanged = false;
        var createdOccurrences = new List<LegendLanguageLexicalOccurrence>();
        foreach (var input in activeInputs)
        {
            foreach (var component in componentsByTextUnit[input.TextUnit.Id])
            {
                var key = (input.TextUnit.Id, component.TokenIndex);
                if (existingOccurrences.TryGetValue(key, out var existing))
                {
                    if (existing.SupersededUtc is not null)
                    {
                        existing.SupersededUtc = null;
                        existing.UpdatedUtc = DateTime.UtcNow;
                        observationsChanged = true;
                    }
                    continue;
                }

                createdOccurrences.Add(new LegendLanguageLexicalOccurrence
                {
                    Id = Guid.NewGuid(),
                    TextUnitId = input.TextUnit.Id,
                    LexemeId = lexemesByHash[component.NormalizedHash].Id,
                    TokenIndex = component.TokenIndex,
                    CharacterOffset = component.CharacterOffset,
                    CharacterLength = component.CharacterLength,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
                observationsChanged = true;
            }
        }
        // The occurrence's canonical identity is (text unit, token index).
        // Its lexeme index is also maintained in ascending key order so the
        // same relationship never receives a different physical lock order
        // merely because its storage GUID was generated at a different time.
        _db.Set<LegendLanguageLexicalOccurrence>().AddRange(createdOccurrences
            .OrderBy(item => item.LexemeId)
            .ThenBy(item => item.TextUnitId)
            .ThenBy(item => item.TokenIndex));
        if (observationsChanged)
            await _db.SaveChangesAsync(cancellationToken);

        var existingRelationships = reusableTextUnitIds.Length == 0
            ? new Dictionary<(Guid TextUnitId, int SourceTokenIndex, int RelatedTokenIndex), LegendLanguageLexicalRelationship>()
            : await _db.Set<LegendLanguageLexicalRelationship>()
                .Where(item => reusableTextUnitIds.Contains(item.TextUnitId))
                .ToDictionaryAsync(item => (item.TextUnitId, item.SourceTokenIndex, item.RelatedTokenIndex), cancellationToken);
        var relationshipsChanged = false;
        var createdRelationships = new List<LegendLanguageLexicalRelationship>();
        foreach (var input in activeInputs)
        {
            var components = componentsByTextUnit[input.TextUnit.Id];
            for (var index = 0; index < components.Count - 1; index++)
            {
                var key = (input.TextUnit.Id, components[index].TokenIndex, components[index + 1].TokenIndex);
                if (existingRelationships.TryGetValue(key, out var existing))
                {
                    if (existing.SupersededUtc is not null)
                    {
                        existing.SupersededUtc = null;
                        existing.UpdatedUtc = DateTime.UtcNow;
                        relationshipsChanged = true;
                    }
                    continue;
                }

                createdRelationships.Add(new LegendLanguageLexicalRelationship
                {
                    Id = Guid.NewGuid(),
                    TextUnitId = input.TextUnit.Id,
                    SourceLexemeId = lexemesByHash[components[index].NormalizedHash].Id,
                    RelatedLexemeId = lexemesByHash[components[index + 1].NormalizedHash].Id,
                    RelationshipKind = "AdjacentToken",
                    SourceTokenIndex = components[index].TokenIndex,
                    RelatedTokenIndex = components[index + 1].TokenIndex,
                    ObservationCount = 1,
                    Provenance = observationProvenance,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
                relationshipsChanged = true;
            }
        }
        // The relationship table has indexes on both its source/related
        // lexical pair and its related lexeme. Preserve one canonical global
        // write order for those keys. This does not merge families or weaken
        // the durable lane; it prevents independent lanes from deadlocking on
        // the lexical indexes while their owned transactions are active.
        _db.Set<LegendLanguageLexicalRelationship>().AddRange(createdRelationships
            .OrderBy(item => item.SourceLexemeId)
            .ThenBy(item => item.RelatedLexemeId)
            .ThenBy(item => item.TextUnitId)
            .ThenBy(item => item.SourceTokenIndex)
            .ThenBy(item => item.RelatedTokenIndex));
        if (relationshipsChanged)
            await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Persists only the explicit Founder-authored utterance meaning graph
    /// attached to a controlled example. A node is never guessed from token
    /// order: it must name one exact Founder-declared span (and an explicit
    /// occurrence when that surface repeats). Its semantic identity remains
    /// graph-local evidence until a later governed lifecycle can derive a
    /// reusable abstraction.
    /// Relations are likewise direct evidence between those persisted nodes.
    /// The aggregate below is observational in Phase 1 and cannot authorize
    /// native serving until a later versioned lifecycle adds that gate.
    /// </summary>
    private async Task AttachFounderMeaningGraphsAsync(
        LegendCurriculumFamily family,
        IReadOnlyList<LegendCurriculumExample> founderExamples,
        IReadOnlyList<NormalizedCurriculumExample> submittedExamples,
        string languageCode,
        CancellationToken cancellationToken)
    {
        if (founderExamples.Count != submittedExamples.Count)
            throw new InvalidOperationException("The controlled meaning graph no longer matches its canonical curriculum examples.");

        var declared = founderExamples
            .Zip(submittedExamples, (example, submission) => new { Example = example, submission.MeaningGraph })
            .Where(item => item.MeaningGraph is not null)
            .ToList();
        if (declared.Count == 0)
            return;

        var exampleIds = declared.Select(item => item.Example.Id).ToArray();
        var textUnitIds = declared.Select(item => item.Example.TextUnitId).Distinct().ToArray();
        var unitsById = await _db.Set<LegendLanguageTextUnit>()
            .AsNoTracking()
            .Where(item => textUnitIds.Contains(item.Id) &&
                item.LanguageCode == languageCode &&
                item.IsTrainingEligible &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var occurrences = await _db.Set<LegendLanguageLexicalOccurrence>()
            .AsNoTracking()
            .Where(item => textUnitIds.Contains(item.TextUnitId) && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        var lexemeByOccurrence = occurrences.ToDictionary(
            item => (item.TextUnitId, item.TokenIndex),
            item => item.LexemeId);
        var anchorsBySignature = await _db.Set<LegendLanguageCompositionalAnchor>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToDictionaryAsync(item => item.AnchorSignature, cancellationToken);
        var nodesByExampleAndKey = await _db.Set<LegendLanguageMeaningNodeEvidence>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToDictionaryAsync(
                item => (item.CurriculumExampleId, item.NodeKey),
                cancellationToken);
        var existingEvidenceByIdentity = await _db.Set<LegendLanguageMeaningRelationEvidence>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToDictionaryAsync(item => item.EvidenceIdentity, cancellationToken);
        var existingReferenceEvidenceByIdentity = await _db.Set<LegendLanguageDiscourseReferenceRuleEvidence>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToDictionaryAsync(item => item.EvidenceIdentity, cancellationToken);
        var relationsBySignature = new Dictionary<string, LegendLanguageMeaningRelation>(StringComparer.Ordinal);
        var affectedRelations = new HashSet<Guid>();
        var referenceRulesBySignature = new Dictionary<string, LegendLanguageDiscourseReferenceRule>(StringComparer.Ordinal);
        var affectedReferenceRules = new HashSet<Guid>();

        foreach (var item in declared)
        {
            var graph = item.MeaningGraph!;
            var example = item.Example;
            if (!unitsById.TryGetValue(example.TextUnitId, out var unit))
                throw new InvalidOperationException("A Founder meaning graph cannot attach to an unavailable curriculum text unit.");

            var nodes = new Dictionary<string, LegendLanguageMeaningNodeEvidence>(StringComparer.Ordinal);
            foreach (var declaration in graph.Nodes)
            {
                if (!TryFindSurfaceSpan(
                        unit.Text,
                        declaration.SurfaceText,
                        declaration.SurfaceOccurrence,
                        out var startTokenIndex,
                        out var tokenLength) ||
                    !lexemeByOccurrence.TryGetValue((example.TextUnitId, startTokenIndex), out var lexemeId))
                {
                    throw new InvalidOperationException("A validated Founder meaning node lost its exact lexical evidence.");
                }

                var semanticSignature = SemanticSignature(
                    declaration.SemanticDimension,
                    declaration.SemanticValue);
                var anchorSignature = LegendLanguageIdentity.TextHash(
                    $"founder-meaning-node-anchor|v1|{example.Id:D}|{declaration.NodeKey}|" +
                    $"{startTokenIndex}|{tokenLength}|{semanticSignature}");
                if (!anchorsBySignature.TryGetValue(anchorSignature, out var anchor))
                {
                    anchor = new LegendLanguageCompositionalAnchor
                    {
                        Id = Guid.NewGuid(),
                        LanguageCode = languageCode,
                        TextUnitId = example.TextUnitId,
                        LexemeId = lexemeId,
                        ComponentStartTokenIndex = startTokenIndex,
                        ComponentLength = tokenLength,
                        CurriculumFamilyId = family.Id,
                        CurriculumExampleId = example.Id,
                        Dimension = declaration.SemanticDimension,
                        Value = declaration.SemanticValue,
                        SemanticSignature = semanticSignature,
                        AnchorSignature = anchorSignature,
                        Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                        CreatedUtc = DateTime.UtcNow
                    };
                    anchor = await LegendConnectCanonicalCurriculumPersistence.AdmitCompositionalAnchorAsync(
                        _db,
                        anchor,
                        cancellationToken);
                    anchorsBySignature.Add(anchorSignature, anchor);
                }

                var nodeIdentity = (example.Id, declaration.NodeKey);
                if (!nodesByExampleAndKey.TryGetValue(nodeIdentity, out var node))
                {
                    node = new LegendLanguageMeaningNodeEvidence
                    {
                        Id = Guid.NewGuid(),
                        LanguageCode = languageCode,
                        CurriculumFamilyId = family.Id,
                        CurriculumExampleId = example.Id,
                        CompositionalAnchorId = anchor.Id,
                        NodeKey = declaration.NodeKey,
                        SemanticSignature = semanticSignature,
                        SemanticDimension = declaration.SemanticDimension,
                        SemanticValue = declaration.SemanticValue,
                        ClauseKey = declaration.ClauseKey,
                        Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    _db.Set<LegendLanguageMeaningNodeEvidence>().Add(node);
                    nodesByExampleAndKey.Add(nodeIdentity, node);
                }
                else if (node.CompositionalAnchorId != anchor.Id ||
                    !string.Equals(node.SemanticSignature, semanticSignature, StringComparison.Ordinal) ||
                    !string.Equals(node.SemanticDimension, declaration.SemanticDimension, StringComparison.Ordinal) ||
                    !string.Equals(node.SemanticValue, declaration.SemanticValue, StringComparison.Ordinal) ||
                    !string.Equals(node.ClauseKey, declaration.ClauseKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A canonical Founder meaning node cannot silently change its governed identity.");
                }
                else if (node.SupersededUtc is not null)
                {
                    node.SupersededUtc = null;
                    node.UpdatedUtc = DateTime.UtcNow;
                }

                nodes.Add(declaration.NodeKey, node);
            }

            foreach (var declaration in graph.Relations)
            {
                var source = nodes[declaration.SourceNodeKey];
                var target = nodes[declaration.TargetNodeKey];
                var relationSignature = MeaningRelationSignature(
                    languageCode,
                    source.SemanticSignature,
                    declaration.RelationKind,
                    target.SemanticSignature,
                    declaration.ClauseKey);
                if (!relationsBySignature.TryGetValue(relationSignature, out var relation))
                {
                    relation = _db.Set<LegendLanguageMeaningRelation>().Local
                        .SingleOrDefault(item => item.LanguageCode == languageCode &&
                            item.RelationSignature == relationSignature)
                        ?? await _db.Set<LegendLanguageMeaningRelation>()
                            .SingleOrDefaultAsync(item => item.LanguageCode == languageCode &&
                                item.RelationSignature == relationSignature, cancellationToken);
                    if (relation is null)
                    {
                        relation = new LegendLanguageMeaningRelation
                        {
                            Id = Guid.NewGuid(),
                            LanguageCode = languageCode,
                            RelationSignature = relationSignature,
                            RelationKind = declaration.RelationKind,
                            SourceSemanticSignature = source.SemanticSignature,
                            TargetSemanticSignature = target.SemanticSignature,
                            ClauseKey = declaration.ClauseKey,
                            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                            CreatedUtc = DateTime.UtcNow,
                            UpdatedUtc = DateTime.UtcNow
                        };
                        _db.Set<LegendLanguageMeaningRelation>().Add(relation);
                    }
                    else if (relation.SupersededUtc is not null)
                    {
                        relation.SupersededUtc = null;
                        relation.UpdatedUtc = DateTime.UtcNow;
                    }
                    relationsBySignature.Add(relationSignature, relation);
                }

                var evidenceIdentity = LegendLanguageIdentity.TextHash(
                    $"founder-meaning-relation-evidence|v1|{example.Id:D}|{source.Id:D}|" +
                    $"{declaration.RelationKind}|{target.Id:D}|{declaration.ClauseKey ?? string.Empty}");
                if (!existingEvidenceByIdentity.TryGetValue(evidenceIdentity, out var evidence))
                {
                    evidence = new LegendLanguageMeaningRelationEvidence
                    {
                        Id = Guid.NewGuid(),
                        MeaningRelationId = relation.Id,
                        CurriculumFamilyId = family.Id,
                        CurriculumExampleId = example.Id,
                        SourceMeaningNodeId = source.Id,
                        TargetMeaningNodeId = target.Id,
                        EvidenceIdentity = evidenceIdentity,
                        IndependentSourceIdentity = family.Id.ToString("N"),
                        ContributionState = "Supported",
                        IsHumanVerifiedSupport = true,
                        Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    _db.Set<LegendLanguageMeaningRelationEvidence>().Add(evidence);
                    existingEvidenceByIdentity.Add(evidenceIdentity, evidence);
                }
                else if (evidence.MeaningRelationId != relation.Id ||
                    evidence.SourceMeaningNodeId != source.Id || evidence.TargetMeaningNodeId != target.Id ||
                    !string.Equals(evidence.Provenance, LegendConnectKnowledgeProvenance.FounderApproved, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A canonical Founder meaning relation cannot silently change its governed identity.");
                }
                else if (evidence.SupersededUtc is not null)
                {
                    evidence.SupersededUtc = null;
                    evidence.UpdatedUtc = DateTime.UtcNow;
                }

                affectedRelations.Add(relation.Id);
            }

            foreach (var declaration in graph.DiscourseReferences)
            {
                var selector = nodes[declaration.SelectorNodeKey];
                var ruleSignature = DiscourseReferenceRuleSignature(
                    languageCode,
                    selector.SemanticSignature,
                    declaration.EntitySemanticDimension,
                    declaration.ResolutionMode,
                    declaration.SelectionRank,
                    declaration.AllowedSourceRoles,
                    declaration.ReplacesActiveBinding);
                if (!referenceRulesBySignature.TryGetValue(ruleSignature, out var rule))
                {
                    rule = _db.Set<LegendLanguageDiscourseReferenceRule>().Local
                        .SingleOrDefault(item => item.LanguageCode == languageCode && item.RuleSignature == ruleSignature)
                        ?? await _db.Set<LegendLanguageDiscourseReferenceRule>()
                            .SingleOrDefaultAsync(item => item.LanguageCode == languageCode && item.RuleSignature == ruleSignature,
                                cancellationToken);
                    if (rule is null)
                    {
                        rule = new LegendLanguageDiscourseReferenceRule
                        {
                            Id = Guid.NewGuid(),
                            LanguageCode = languageCode,
                            RuleSignature = ruleSignature,
                            SelectorSemanticSignature = selector.SemanticSignature,
                            EntitySemanticDimension = declaration.EntitySemanticDimension,
                            ResolutionMode = declaration.ResolutionMode,
                            SelectionRank = declaration.SelectionRank,
                            AllowedSourceRoles = string.Join("|", declaration.AllowedSourceRoles),
                            ReplacesActiveBinding = declaration.ReplacesActiveBinding,
                            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                            CreatedUtc = DateTime.UtcNow,
                            UpdatedUtc = DateTime.UtcNow
                        };
                        _db.Set<LegendLanguageDiscourseReferenceRule>().Add(rule);
                    }
                    else if (rule.SupersededUtc is not null)
                    {
                        rule.SupersededUtc = null;
                        rule.UpdatedUtc = DateTime.UtcNow;
                    }
                    referenceRulesBySignature.Add(ruleSignature, rule);
                }

                var evidenceIdentity = LegendLanguageIdentity.TextHash(
                    $"founder-discourse-reference-evidence|v1|{example.Id:D}|{selector.Id:D}|{rule.Id:D}");
                if (!existingReferenceEvidenceByIdentity.TryGetValue(evidenceIdentity, out var evidence))
                {
                    evidence = new LegendLanguageDiscourseReferenceRuleEvidence
                    {
                        Id = Guid.NewGuid(),
                        DiscourseReferenceRuleId = rule.Id,
                        CurriculumFamilyId = family.Id,
                        CurriculumExampleId = example.Id,
                        SelectorMeaningNodeId = selector.Id,
                        EvidenceIdentity = evidenceIdentity,
                        IndependentSourceIdentity = family.Id.ToString("N"),
                        ContributionState = "Supported",
                        IsHumanVerifiedSupport = true,
                        Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    _db.Set<LegendLanguageDiscourseReferenceRuleEvidence>().Add(evidence);
                    existingReferenceEvidenceByIdentity.Add(evidenceIdentity, evidence);
                }
                else if (evidence.DiscourseReferenceRuleId != rule.Id ||
                    evidence.SelectorMeaningNodeId != selector.Id ||
                    !string.Equals(evidence.Provenance, LegendConnectKnowledgeProvenance.FounderApproved,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A canonical Founder discourse reference cannot silently change its governed identity.");
                }
                else if (evidence.SupersededUtc is not null)
                {
                    evidence.SupersededUtc = null;
                    evidence.UpdatedUtc = DateTime.UtcNow;
                }
                affectedReferenceRules.Add(rule.Id);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        foreach (var relationId in affectedRelations)
            await RefreshMeaningRelationMaturityAsync(relationId, cancellationToken);
        if (affectedRelations.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);
        foreach (var ruleId in affectedReferenceRules)
            await RefreshDiscourseReferenceRuleMaturityAsync(ruleId, cancellationToken);
        if (affectedReferenceRules.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);
        await ReconcileFounderMeaningPrimitivesAsync(family.Id, languageCode, cancellationToken);
    }

    /// <summary>
    /// Derives only a reusable index over explicit Founder graph nodes. The
    /// same routine runs for ordinary ingestion and SourceFamilies replay, so
    /// a later evaluator revision can rebuild derived evidence without
    /// resubmitting or duplicating canonical curriculum.
    /// </summary>
    private async Task ReconcileFounderMeaningPrimitivesAsync(
        Guid familyId,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var nodes = await _db.Set<LegendLanguageMeaningNodeEvidence>()
            .Where(item => item.CurriculumFamilyId == familyId &&
                item.LanguageCode == languageCode && item.SupersededUtc == null &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .ToListAsync(cancellationToken);
        if (nodes.Count == 0)
            return;

        var signatures = nodes.Select(item => item.SemanticSignature).Distinct(StringComparer.Ordinal).ToArray();
        var primitives = await _db.Set<LegendLanguageMeaningPrimitive>()
            .Where(item => item.LanguageCode == languageCode && signatures.Contains(item.SemanticSignature))
            .ToDictionaryAsync(item => item.SemanticSignature, cancellationToken);
        var nodeIds = nodes.Select(item => item.Id).ToArray();
        var evidenceByNode = await _db.Set<LegendLanguageMeaningPrimitiveEvidence>()
            .Where(item => nodeIds.Contains(item.MeaningNodeEvidenceId))
            .ToDictionaryAsync(item => item.MeaningNodeEvidenceId, cancellationToken);
        var affected = new HashSet<Guid>();

        foreach (var node in nodes)
        {
            if (!primitives.TryGetValue(node.SemanticSignature, out var primitive))
            {
                primitive = new LegendLanguageMeaningPrimitive
                {
                    Id = Guid.NewGuid(),
                    LanguageCode = languageCode,
                    SemanticSignature = node.SemanticSignature,
                    SemanticDimension = node.SemanticDimension,
                    SemanticValue = node.SemanticValue,
                    Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                _db.Set<LegendLanguageMeaningPrimitive>().Add(primitive);
                primitives.Add(primitive.SemanticSignature, primitive);
            }
            else if (!string.Equals(primitive.SemanticDimension, node.SemanticDimension, StringComparison.Ordinal) ||
                !string.Equals(primitive.SemanticValue, node.SemanticValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A reusable meaning primitive cannot silently change its governed semantic identity.");
            }
            else if (primitive.SupersededUtc is not null)
            {
                primitive.SupersededUtc = null;
                primitive.UpdatedUtc = DateTime.UtcNow;
            }

            var evidenceIdentity = LegendLanguageIdentity.TextHash(
                $"founder-meaning-primitive-evidence|v1|{node.Id:D}|{primitive.Id:D}");
            if (!evidenceByNode.TryGetValue(node.Id, out var evidence))
            {
                evidence = new LegendLanguageMeaningPrimitiveEvidence
                {
                    Id = Guid.NewGuid(),
                    MeaningPrimitiveId = primitive.Id,
                    MeaningNodeEvidenceId = node.Id,
                    CurriculumFamilyId = node.CurriculumFamilyId,
                    CurriculumExampleId = node.CurriculumExampleId,
                    EvidenceIdentity = evidenceIdentity,
                    IndependentSourceIdentity = node.CurriculumFamilyId.ToString("N"),
                    ContributionState = "Supported",
                    IsHumanVerifiedSupport = true,
                    Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                _db.Set<LegendLanguageMeaningPrimitiveEvidence>().Add(evidence);
                evidenceByNode.Add(node.Id, evidence);
            }
            else if (evidence.MeaningPrimitiveId != primitive.Id ||
                !string.Equals(evidence.EvidenceIdentity, evidenceIdentity, StringComparison.Ordinal) ||
                !string.Equals(evidence.Provenance, LegendConnectKnowledgeProvenance.FounderApproved, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A reusable meaning primitive cannot silently change its governed evidence identity.");
            }
            else if (evidence.SupersededUtc is not null)
            {
                evidence.SupersededUtc = null;
                evidence.UpdatedUtc = DateTime.UtcNow;
            }
            affected.Add(primitive.Id);
        }

        await _db.SaveChangesAsync(cancellationToken);
        foreach (var primitiveId in affected)
            await RefreshMeaningPrimitiveMaturityAsync(primitiveId, cancellationToken);
        if (affected.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RefreshMeaningPrimitiveMaturityAsync(
        Guid primitiveId,
        CancellationToken cancellationToken)
    {
        var primitive = await _db.Set<LegendLanguageMeaningPrimitive>()
            .SingleAsync(item => item.Id == primitiveId, cancellationToken);
        var evidence = await _db.Set<LegendLanguageMeaningPrimitiveEvidence>()
            .Where(item => item.MeaningPrimitiveId == primitiveId && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        var supported = evidence.Where(item => item.ContributionState == "Supported" && item.IsHumanVerifiedSupport).ToList();
        var contradictions = evidence.Count(item => item.ContributionState == "Contradictory");
        primitive.SupportCount = supported.Count;
        primitive.ContradictionCount = contradictions;
        primitive.IndependentSourceCount = supported.Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal).Count();
        primitive.HumanVerifiedSupportCount = supported.Count;
        primitive.Confidence = 0m;
        primitive.MaturityState = contradictions > 0 ? "Contradicted" :
            primitive.IndependentSourceCount >= 3 ? "Supported" : "Observation";
        primitive.IsProductionEligible = false;
        primitive.UpdatedUtc = DateTime.UtcNow;
    }

    private async Task RefreshMeaningRelationMaturityAsync(
        Guid relationId,
        CancellationToken cancellationToken)
    {
        var relation = await _db.Set<LegendLanguageMeaningRelation>()
            .SingleAsync(item => item.Id == relationId, cancellationToken);
        var evidence = await _db.Set<LegendLanguageMeaningRelationEvidence>()
            .Where(item => item.MeaningRelationId == relationId && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        var supported = evidence.Where(item => item.ContributionState == "Supported" &&
            item.IsHumanVerifiedSupport).ToList();
        var contradictions = evidence.Count(item => item.ContributionState == "Contradictory");
        relation.SupportCount = supported.Count;
        relation.ContradictionCount = contradictions;
        relation.IndependentSourceCount = supported
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        relation.HumanVerifiedSupportCount = supported.Count;
        relation.Confidence = 0m;
        relation.MaturityState = contradictions > 0
            ? "Contradicted"
            : relation.IndependentSourceCount >= 3
                ? "Supported"
                : "Observation";
        relation.IsProductionEligible = false;
        relation.UpdatedUtc = DateTime.UtcNow;
    }

    private static string MeaningRelationSignature(
        string languageCode,
        string sourceSemanticSignature,
        string relationKind,
        string targetSemanticSignature,
        string? clauseKey) =>
        LegendLanguageIdentity.TextHash(
            $"founder-meaning-relation|v1|{languageCode}|{sourceSemanticSignature}|" +
            $"{relationKind}|{targetSemanticSignature}|{clauseKey ?? string.Empty}");

    internal async Task<IReadOnlyList<LegendConnectDiscourseReferenceRuleSnapshot>>
        GetProductionDiscourseReferenceRulesAsync(
            string sourceLanguageCode,
            IReadOnlyList<string> selectorSemanticSignatures,
            CancellationToken cancellationToken = default)
    {
        var languageCode = await _languages.NormalizeEnabledTranslationLanguageAsync(
            sourceLanguageCode,
            cancellationToken);
        var selectors = selectorSemanticSignatures
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (languageCode is null || selectors.Length == 0)
            return [];

        var rules = await _db.Set<LegendLanguageDiscourseReferenceRule>().AsNoTracking()
            .Where(item => item.LanguageCode == languageCode && item.SupersededUtc == null &&
                item.MaturityState == "Supported" && item.IsProductionEligible &&
                item.ContradictionCount == 0 && item.IndependentSourceCount >= 3 &&
                selectors.Contains(item.SelectorSemanticSignature))
            .ToListAsync(cancellationToken);
        return rules.Select(item => new LegendConnectDiscourseReferenceRuleSnapshot(
                item.SelectorSemanticSignature,
                item.EntitySemanticDimension,
                item.ResolutionMode,
                item.SelectionRank,
                item.AllowedSourceRoles.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                item.ReplacesActiveBinding,
                item.IndependentSourceCount))
            .ToArray();
    }

    private async Task RefreshDiscourseReferenceRuleMaturityAsync(
        Guid ruleId,
        CancellationToken cancellationToken)
    {
        var rule = await _db.Set<LegendLanguageDiscourseReferenceRule>()
            .SingleAsync(item => item.Id == ruleId, cancellationToken);
        var evidence = await _db.Set<LegendLanguageDiscourseReferenceRuleEvidence>()
            .Where(item => item.DiscourseReferenceRuleId == ruleId && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        var supported = evidence.Where(item => item.ContributionState == "Supported" &&
            item.IsHumanVerifiedSupport).ToList();
        var contradictions = evidence.Count(item => item.ContributionState == "Contradictory");
        rule.SupportCount = supported.Count;
        rule.ContradictionCount = contradictions;
        rule.IndependentSourceCount = supported.Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal).Count();
        rule.HumanVerifiedSupportCount = supported.Count;
        rule.MaturityState = contradictions > 0 ? "Contradicted" :
            rule.IndependentSourceCount >= 3 ? "Supported" : "Observation";
        rule.IsProductionEligible = rule.MaturityState == "Supported" &&
            rule.ContradictionCount == 0 && rule.HumanVerifiedSupportCount >= 3;
        rule.UpdatedUtc = DateTime.UtcNow;
    }

    private static string DiscourseReferenceRuleSignature(
        string languageCode,
        string selectorSemanticSignature,
        string entitySemanticDimension,
        string resolutionMode,
        int? selectionRank,
        IReadOnlyList<string> allowedSourceRoles,
        bool replacesActiveBinding) =>
        LegendLanguageIdentity.TextHash(
            $"founder-discourse-reference-rule|v1|{languageCode}|{selectorSemanticSignature}|" +
            $"{entitySemanticDimension}|{resolutionMode}|{selectionRank?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}|" +
            $"{string.Join("|", allowedSourceRoles)}|{replacesActiveBinding}");

    private async Task AttachExplicitFounderSemanticAnchorsAsync(
        LegendCurriculumFamily family,
        IReadOnlyList<LegendCurriculumExample> examples,
        string languageCode,
        IReadOnlyList<NormalizedSemanticTransition>? sourceTransitions,
        IReadOnlyList<NormalizedSemanticSpanGrounding>? semanticSpanGroundings,
        IReadOnlyDictionary<Guid, LegendLanguageTextUnit>? knownFounderTextUnitsById,
        IReadOnlySet<Guid>? knownFounderApprovedTextUnitIds,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? knownVariationsByExample,
        IReadOnlyList<FounderLexicalOccurrence>? knownLexicalOccurrences,
        IReadOnlySet<Guid>? knownNewCurriculumExampleIds,
        CancellationToken cancellationToken)
    {
        var candidates = examples
            .Where(item => string.Equals(item.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase) && item.SupersededUtc is null)
            .DistinctBy(item => item.Id)
            .ToList();
        if (candidates.Count == 0)
            return;

        var candidateTextUnitIds = candidates.Select(example => example.TextUnitId).ToArray();
        var founderApprovedUnitIds = knownFounderApprovedTextUnitIds is not null
            ? candidateTextUnitIds
                .Where(knownFounderApprovedTextUnitIds.Contains)
                .ToHashSet()
            : await _db.Set<LegendLanguageTextUnit>()
                .Where(item => candidateTextUnitIds.Contains(item.Id) &&
                    item.LanguageCode == languageCode && item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
                .Select(item => item.Id)
                .ToHashSetAsync(cancellationToken);
        var founderExamples = candidates
            .Where(item =>
                founderApprovedUnitIds.Contains(item.TextUnitId) &&
                item.Provenance ==
                    LegendConnectKnowledgeProvenance
                        .FounderApproved)
            .ToList();
        if (founderExamples.Count == 0)
            return;

        var exampleIds = founderExamples.Select(item => item.Id).ToArray();
        var textUnitIds = founderExamples.Select(item => item.TextUnitId).ToArray();
        var variationsByExample = knownVariationsByExample is not null
            ? knownVariationsByExample
                .Where(item => exampleIds.Contains(item.Key))
                .ToDictionary(
                    item => item.Key,
                    item => (IReadOnlyList<FounderVariation>)item.Value
                        .Select(variation => new FounderVariation(variation.Key, variation.Value))
                        .ToArray())
            : (await _db.Set<LegendCurriculumExampleVariation>()
                    .Where(item => exampleIds.Contains(item.CurriculumExampleId))
                    .ToListAsync(cancellationToken))
                .GroupBy(item => item.CurriculumExampleId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<FounderVariation>)group
                        .Select(item => new FounderVariation(item.Dimension, item.Value))
                        .ToArray());
        var occurrencesByTextUnit = knownLexicalOccurrences is not null
            ? knownLexicalOccurrences
                .Where(item => textUnitIds.Contains(item.TextUnitId))
                .ToArray()
            : await (
                from occurrence in _db.Set<LegendLanguageLexicalOccurrence>()
                join lexeme in _db.Set<LegendLanguageLexeme>() on occurrence.LexemeId equals lexeme.Id
                where textUnitIds.Contains(occurrence.TextUnitId) && occurrence.SupersededUtc == null
                select new FounderLexicalOccurrence(
                    occurrence.TextUnitId,
                    occurrence.TokenIndex,
                    occurrence.LexemeId,
                    lexeme.SurfaceForm)
            ).ToArrayAsync(cancellationToken);
        // This canonical existence read must remain on the unique anchor
        // identity index.  Loading whole rows here forces clustered-key
        // lookups while independent durable family owners are inserting
        // different examples, which can form page-lock deadlocks despite the
        // example identities being disjoint.  The anchor signature is the
        // only value required for the normal idempotency decision, so keep
        // the common path index-covered.  The bounded legacy repair below
        // intentionally materializes only rows that actually need mutation.
        var reusableExampleIds = knownNewCurriculumExampleIds is null
            ? exampleIds
            : exampleIds.Where(id => !knownNewCurriculumExampleIds.Contains(id)).ToArray();
        var existingSignatures = reusableExampleIds.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : await _db.Set<LegendLanguageCompositionalAnchor>()
                .AsNoTracking()
                .Where(item => reusableExampleIds.Contains(item.CurriculumExampleId))
                .Select(item => item.AnchorSignature)
                .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);
        var anchorsNeedingSemanticSignature = reusableExampleIds.Length == 0
            ? []
            : await _db.Set<LegendLanguageCompositionalAnchor>()
                .Where(item => reusableExampleIds.Contains(item.CurriculumExampleId) &&
                    item.SemanticSignature == null)
                .ToListAsync(cancellationToken);
        var pending = false;
        foreach (var existing in anchorsNeedingSemanticSignature)
        {
            // This is the bounded historical signature repair. It operates
            // only on the source-family page currently being replayed, so a
            // whole-corpus repair can never hold its runtime-policy cursor.
            existing.SemanticSignature = SemanticSignature(existing.Dimension, existing.Value);
            pending = true;
        }
        foreach (var example in founderExamples)
        {
            if (!variationsByExample.TryGetValue(example.Id, out var variations))
                continue;
            var occurrences = occurrencesByTextUnit.Where(item => item.TextUnitId == example.TextUnitId).ToList();
            foreach (var variation in variations)
            {
                var sentenceSignature = AnchorSignature(example.Id, null, variation.Dimension, variation.Value);
                if (existingSignatures.Add(sentenceSignature))
                {
                    var candidate = new LegendLanguageCompositionalAnchor
                    {
                        Id = Guid.NewGuid(),
                        LanguageCode = languageCode,
                        TextUnitId = example.TextUnitId,
                        CurriculumFamilyId = family.Id,
                        CurriculumExampleId = example.Id,
                        Dimension = variation.Dimension,
                        Value = variation.Value,
                        SemanticSignature = SemanticSignature(variation.Dimension, variation.Value),
                        AnchorSignature = sentenceSignature,
                        Provenance = "FounderApproved",
                        CreatedUtc = DateTime.UtcNow
                    };
                    var admitted = await LegendConnectCanonicalCurriculumPersistence.AdmitCompositionalAnchorAsync(
                        _db,
                        candidate,
                        cancellationToken);
                    pending |= ReferenceEquals(admitted, candidate);
                }

                var controlledComponents = SurfaceComponents(variation.Value);
                if (controlledComponents.Count == 0)
                    continue;
                var orderedOccurrences = occurrences.OrderBy(item => item.TokenIndex).ToList();
                for (var start = 0; start <= orderedOccurrences.Count - controlledComponents.Count; start++)
                {
                    if (!controlledComponents.Select(item => item.NormalizedText).SequenceEqual(
                            orderedOccurrences.Skip(start).Take(controlledComponents.Count).Select(item => item.SurfaceForm),
                            StringComparer.Ordinal))
                        continue;
                    var occurrence = orderedOccurrences[start];
                    var lexicalSignature = AnchorSignature(example.Id, occurrence.LexemeId, variation.Dimension,
                        variation.Value + ":" + occurrence.TokenIndex + ":" + controlledComponents.Count);
                    if (!existingSignatures.Add(lexicalSignature))
                        continue;
                    var candidate = new LegendLanguageCompositionalAnchor
                    {
                        Id = Guid.NewGuid(),
                        LanguageCode = languageCode,
                        TextUnitId = example.TextUnitId,
                        LexemeId = occurrence.LexemeId,
                        ComponentStartTokenIndex = occurrence.TokenIndex,
                        ComponentLength = controlledComponents.Count,
                        CurriculumFamilyId = family.Id,
                        CurriculumExampleId = example.Id,
                        Dimension = variation.Dimension,
                        Value = variation.Value,
                        SemanticSignature = SemanticSignature(variation.Dimension, variation.Value),
                        AnchorSignature = lexicalSignature,
                        Provenance = "FounderApproved",
                        CreatedUtc = DateTime.UtcNow
                    };
                    var admitted = await LegendConnectCanonicalCurriculumPersistence.AdmitCompositionalAnchorAsync(
                        _db,
                        candidate,
                        cancellationToken);
                    pending |= ReferenceEquals(admitted, candidate);
                }
            }
        }
        if (pending)
            await _db.SaveChangesAsync(cancellationToken);

        // A Founder may describe the semantic role of an explicitly grounded
        // span with a value that is not itself surface text (for example,
        // conversation_function=greeting on "Hello").  The normal parser has
        // already made both facts durable here.  Project that direct
        // co-annotation onto the already grounded span so native source
        // analysis can use the Founder-authored semantic value without a
        // language-specific word list or a second authoring format.
        await AttachFounderSemanticProjectionAnchorsAsync(
            family,
            founderExamples,
            languageCode,
            sourceTransitions,
            semanticSpanGroundings,
            knownFounderTextUnitsById,
            knownVariationsByExample,
            knownNewCurriculumExampleIds,
            cancellationToken);

    }

    /// <summary>
    /// Projects a non-literal Founder source-frame annotation only to the
    /// exact controlled surface span explicitly named by @ground. This never
    /// guesses from adjacency, syntax, a keyword table, or another family.
    /// </summary>
    private async Task AttachFounderSemanticProjectionAnchorsAsync(
        LegendCurriculumFamily family,
        IReadOnlyList<LegendCurriculumExample> founderExamples,
        string languageCode,
        IReadOnlyList<NormalizedSemanticTransition>? sourceTransitions,
        IReadOnlyList<NormalizedSemanticSpanGrounding>? semanticSpanGroundings,
        IReadOnlyDictionary<Guid, LegendLanguageTextUnit>? knownFounderTextUnitsById,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? knownVariationsByExample,
        IReadOnlySet<Guid>? knownNewCurriculumExampleIds,
        CancellationToken cancellationToken)
    {
        if (founderExamples.Count == 0 || sourceTransitions is null || sourceTransitions.Count == 0 ||
            semanticSpanGroundings is null || semanticSpanGroundings.Count == 0)
            return;

        var exampleIds = founderExamples.Select(item => item.Id).Distinct().ToArray();
        var textUnitIds = founderExamples.Select(item => item.TextUnitId).Distinct().ToArray();
        var textUnits = knownFounderTextUnitsById is not null
            ? knownFounderTextUnitsById
                .Where(item => textUnitIds.Contains(item.Key) &&
                    item.Value.LanguageCode == languageCode && item.Value.IsTrainingEligible &&
                    item.Value.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
                .ToDictionary(item => item.Key, item => item.Value)
            : await _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                .Where(item => textUnitIds.Contains(item.Id) &&
                    item.LanguageCode == languageCode &&
                    item.IsTrainingEligible &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
                .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (textUnits.Count == 0)
            return;

        var variationsByExample = knownVariationsByExample is not null
            ? knownVariationsByExample
                .Where(item => exampleIds.Contains(item.Key))
                .ToDictionary(
                    item => item.Key,
                    item => (IReadOnlyList<FounderVariation>)item.Value
                        .Select(variation => new FounderVariation(variation.Key, variation.Value))
                        .ToArray())
            : (await _db.Set<LegendCurriculumExampleVariation>()
                    .AsNoTracking()
                    .Where(item => exampleIds.Contains(item.CurriculumExampleId))
                    .ToListAsync(cancellationToken))
                .GroupBy(item => item.CurriculumExampleId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<FounderVariation>)group
                        .Select(item => new FounderVariation(item.Dimension, item.Value))
                        .ToArray());
        var reusableExampleIds = knownNewCurriculumExampleIds is null
            ? exampleIds
            : exampleIds.Where(id => !knownNewCurriculumExampleIds.Contains(id)).ToArray();
        var trackedAnchors = _db.ChangeTracker.Entries<LegendLanguageCompositionalAnchor>()
            .Where(entry => entry.State != EntityState.Detached && entry.State != EntityState.Deleted &&
                knownNewCurriculumExampleIds is not null &&
                knownNewCurriculumExampleIds.Contains(entry.Entity.CurriculumExampleId) &&
                entry.Entity.LanguageCode == languageCode &&
                entry.Entity.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                entry.Entity.SupersededUtc is null && entry.Entity.LexemeId != null &&
                entry.Entity.ComponentStartTokenIndex != null && entry.Entity.ComponentLength is > 0)
            .Select(entry => new FounderLexicalAnchor(
                entry.Entity.CurriculumExampleId,
                entry.Entity.LexemeId!.Value,
                entry.Entity.Dimension,
                entry.Entity.Value,
                entry.Entity.ComponentStartTokenIndex!.Value,
                entry.Entity.ComponentLength!.Value))
            .ToArray();
        var persistedAnchors = reusableExampleIds.Length == 0
            ? Array.Empty<FounderLexicalAnchor>()
            : await _db.Set<LegendLanguageCompositionalAnchor>()
                .AsNoTracking()
                .Where(item => reusableExampleIds.Contains(item.CurriculumExampleId) &&
                    item.LanguageCode == languageCode &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    item.SupersededUtc == null && item.LexemeId != null &&
                    item.ComponentStartTokenIndex != null && item.ComponentLength != null &&
                    item.ComponentLength > 0)
                .Select(item => new FounderLexicalAnchor(
                    item.CurriculumExampleId,
                    item.LexemeId!.Value,
                    item.Dimension,
                    item.Value,
                    item.ComponentStartTokenIndex!.Value,
                    item.ComponentLength!.Value))
                .ToArrayAsync(cancellationToken);
        var anchors = trackedAnchors.Concat(persistedAnchors).ToArray();
        var existingSignatures = knownNewCurriculumExampleIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : _db.ChangeTracker.Entries<LegendLanguageCompositionalAnchor>()
                .Where(entry => entry.State != EntityState.Detached && entry.State != EntityState.Deleted &&
                    knownNewCurriculumExampleIds is not null &&
                    knownNewCurriculumExampleIds.Contains(entry.Entity.CurriculumExampleId))
                .Select(entry => entry.Entity.AnchorSignature)
                .ToHashSet(StringComparer.Ordinal);
        if (reusableExampleIds.Length > 0)
            existingSignatures.UnionWith(await _db.Set<LegendLanguageCompositionalAnchor>()
                .Where(item => reusableExampleIds.Contains(item.CurriculumExampleId))
                .Select(item => item.AnchorSignature)
                .ToListAsync(cancellationToken));

        var pending = false;
        foreach (var example in founderExamples)
        {
            if (!textUnits.TryGetValue(example.TextUnitId, out var textUnit) ||
                !variationsByExample.TryGetValue(example.Id, out var exampleVariations))
            {
                continue;
            }

            var variationMap = exampleVariations.ToDictionary(
                item => item.Dimension,
                item => item.Value,
                StringComparer.OrdinalIgnoreCase);
            if (!sourceTransitions.Any(transition =>
                    TryBindSemanticFrame(transition.Source, variationMap, out _)))
            {
                // Result examples need sentence-level semantic evidence for
                // canonical matching, but projecting their discourse role
                // onto source lexemes would fabricate a competing source
                // interpretation.  Only a declared source frame authorizes
                // lexical semantic projection.
                continue;
            }

            var tokens = SurfaceComponents(textUnit.Text);
            foreach (var grounding in semanticSpanGroundings)
            {
                if (!variationMap.TryGetValue(grounding.SemanticDimension, out var semanticValue))
                    continue;

                var directAnchors = anchors
                    .Where(item => item.ExampleId == example.Id &&
                        string.Equals(item.Dimension, grounding.SurfaceDimension, StringComparison.OrdinalIgnoreCase) &&
                        IsExactAnchorSurface(tokens, item.StartTokenIndex, item.TokenLength, item.Value))
                    .GroupBy(item => (item.LexemeId, item.StartTokenIndex, item.TokenLength))
                    .Select(group => group.First())
                    .ToList();
                if (directAnchors.Count != 1)
                    continue;

                var directAnchor = directAnchors[0];
                var identity = LegendLanguageIdentity.TextHash(
                    $"founder-semantic-projection|v2|{example.Id:D}|{directAnchor.LexemeId:D}|" +
                    $"{directAnchor.StartTokenIndex}|{directAnchor.TokenLength}|" +
                    $"{grounding.SemanticDimension}|{semanticValue}|{grounding.SurfaceDimension}");
                if (!existingSignatures.Add(identity))
                    continue;

                var candidate = new LegendLanguageCompositionalAnchor
                {
                    Id = Guid.NewGuid(),
                    LanguageCode = languageCode,
                    TextUnitId = example.TextUnitId,
                    LexemeId = directAnchor.LexemeId,
                    ComponentStartTokenIndex = directAnchor.StartTokenIndex,
                    ComponentLength = directAnchor.TokenLength,
                    CurriculumFamilyId = family.Id,
                    CurriculumExampleId = example.Id,
                    Dimension = grounding.SemanticDimension,
                    Value = semanticValue,
                    SemanticSignature = SemanticSignature(grounding.SemanticDimension, semanticValue),
                    AnchorSignature = identity,
                    Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                    CreatedUtc = DateTime.UtcNow
                };
                var admitted = await LegendConnectCanonicalCurriculumPersistence.AdmitCompositionalAnchorAsync(
                    _db,
                    candidate,
                    cancellationToken);
                pending |= ReferenceEquals(admitted, candidate);
            }
        }

        if (pending)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsExactAnchorSurface(
        IReadOnlyList<SurfaceComponent> tokens,
        int startTokenIndex,
        int tokenLength,
        string value)
    {
        var valueComponents = SurfaceComponents(value);
        return valueComponents.Count == tokenLength && startTokenIndex >= 0 &&
            startTokenIndex + tokenLength <= tokens.Count &&
            valueComponents.Select(item => item.NormalizedText).SequenceEqual(
                tokens.Skip(startTokenIndex).Take(tokenLength).Select(item => item.NormalizedText),
            StringComparer.Ordinal);
    }

    private static bool TryFindUniqueSurfaceSpan(
        string text,
        string value,
        out int startTokenIndex,
        out int tokenLength)
    {
        startTokenIndex = -1;
        tokenLength = 0;
        var tokens = SurfaceComponents(text);
        var components = SurfaceComponents(value);
        if (components.Count == 0 || tokens.Count < components.Count)
            return false;

        for (var start = 0; start <= tokens.Count - components.Count; start++)
        {
            if (!components.Select(item => item.NormalizedText).SequenceEqual(
                    tokens.Skip(start).Take(components.Count).Select(item => item.NormalizedText),
                    StringComparer.Ordinal))
            {
                continue;
            }
            if (startTokenIndex >= 0)
                return false;
            startTokenIndex = start;
            tokenLength = components.Count;
        }

        return startTokenIndex >= 0;
    }

    /// <summary>
    /// Resolves exactly the occurrence that a Founder named in a meaning-node
    /// declaration. The ordinal is explicit evidence; this method never
    /// chooses a repeated surface span by position or adjacency.
    /// </summary>
    private static bool TryFindSurfaceSpan(
        string text,
        string value,
        int occurrence,
        out int startTokenIndex,
        out int tokenLength)
    {
        startTokenIndex = -1;
        tokenLength = 0;
        if (occurrence < 1)
            return false;

        var tokens = SurfaceComponents(text);
        var components = SurfaceComponents(value);
        if (components.Count == 0 || tokens.Count < components.Count)
            return false;

        var matched = 0;
        for (var start = 0; start <= tokens.Count - components.Count; start++)
        {
            if (!components.Select(item => item.NormalizedText).SequenceEqual(
                    tokens.Skip(start).Take(components.Count).Select(item => item.NormalizedText),
                    StringComparer.Ordinal))
            {
                continue;
            }

            matched++;
            if (matched != occurrence)
                continue;

            startTokenIndex = start;
            tokenLength = components.Count;
            return true;
        }

        return false;
    }

    private static bool HasOneExactSurfaceSpan(string text, string value)
    {
        var tokens = SurfaceComponents(text);
        var components = SurfaceComponents(value);
        if (components.Count == 0 || tokens.Count < components.Count)
            return false;

        var matches = 0;
        for (var start = 0; start <= tokens.Count - components.Count; start++)
        {
            if (!components.Select(item => item.NormalizedText).SequenceEqual(
                    tokens.Skip(start).Take(components.Count).Select(item => item.NormalizedText),
                    StringComparer.Ordinal))
            {
                continue;
            }

            matches++;
            if (matches > 1)
                return false;
        }

        return matches == 1;
    }

    private static bool HasPotentialFounderCoRealization(
        IReadOnlyList<LegendCurriculumExample> founderExamples,
        IReadOnlyDictionary<Guid, LegendLanguageTextUnit> textUnits,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> variationsByExample)
    {
        for (var leftIndex = 0; leftIndex < founderExamples.Count - 1; leftIndex++)
        {
            var left = founderExamples[leftIndex];
            if (!textUnits.TryGetValue(left.TextUnitId, out var leftText) ||
                !variationsByExample.TryGetValue(left.Id, out var leftVariations))
            {
                continue;
            }

            for (var rightIndex = leftIndex + 1; rightIndex < founderExamples.Count; rightIndex++)
            {
                var right = founderExamples[rightIndex];
                if (!textUnits.TryGetValue(right.TextUnitId, out var rightText) ||
                    !variationsByExample.TryGetValue(right.Id, out var rightVariations) ||
                    !ControlledVariationMapsEqual(leftVariations, rightVariations))
                {
                    continue;
                }

                var contrast = TryGetSafeTargetContrast(
                    leftText.Text,
                    rightText.Text,
                    "source-corealization");
                if (contrast is null || contrast.Left.TokenLength == contrast.Right.TokenLength)
                    continue;

                var expanded = contrast.Left.TokenLength > contrast.Right.TokenLength
                    ? contrast.Left
                    : contrast.Right;
                var fused = contrast.Left.TokenLength > contrast.Right.TokenLength
                    ? contrast.Right
                    : contrast.Left;
                if (expanded.TokenLength >= 2 && fused.TokenLength >= 1 &&
                    fused.TokenLength < expanded.TokenLength)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ControlledVariationMapsEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count &&
        left.All(item => right.TryGetValue(item.Key, out var value) &&
            string.Equals(item.Value, value, StringComparison.Ordinal));

    /// <summary>
    /// Derives a fused/co-realized lexical span only from two Founder-approved
    /// examples in the same family whose complete controlled semantic values
    /// are identical and whose only safe surface difference compresses two or
    /// more independently anchored components into fewer tokens.
    ///
    /// This is language-neutral evidence. No contraction spelling, suffix,
    /// apostrophe, lemma, or language-specific grammar rule is encoded.
    /// </summary>
    private async Task AttachFounderCoRealizedSemanticAnchorsAsync(
        LegendCurriculumFamily family,
        IReadOnlyList<LegendCurriculumExample> founderExamples,
        string languageCode,
        IReadOnlyDictionary<Guid, LegendLanguageTextUnit>? knownFounderTextUnitsById,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? knownVariationsByExample,
        CancellationToken cancellationToken)
    {
        if (founderExamples.Count < 2)
            return;

        var exampleIds = founderExamples
            .Select(item => item.Id)
            .ToArray();

        // The normal durable ingestion path already has both the bounded
        // text-unit map and the normalized controlled variations for this
        // family. Before touching any shared evidence table, prove that this
        // family can actually contain a co-realized span. Most ordinary
        // curriculum families cannot: their controlled variation maps differ
        // or their surfaces have no structural compression. Returning here is
        // semantically neutral (there is no eligible derivation to perform),
        // while avoiding avoidable read/write lock cycles between independent
        // family owners. Historical reconciliation intentionally does not use
        // this fast path; it retains its authoritative persisted reads.
        if (knownFounderTextUnitsById is not null && knownVariationsByExample is not null &&
            !HasPotentialFounderCoRealization(
                founderExamples,
                knownFounderTextUnitsById,
                knownVariationsByExample))
        {
            return;
        }

        // An explicit Founder meaning graph is the strongest co-realization
        // authority. A legacy flat variation declaration can still prove a
        // fused form, but only through two or more direct literal Founder
        // anchors below; projection- or replay-derived anchors can never
        // bootstrap a new fused span from an unordered variation map.
        var declaredNodes = await _db.Set<LegendLanguageMeaningNodeEvidence>()
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId) &&
                item.LanguageCode == languageCode && item.SupersededUtc == null &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new FounderGraphNode(
                item.Id,
                item.CurriculumExampleId,
                item.SemanticDimension,
                item.SemanticValue,
                item.SemanticSignature,
                item.ClauseKey))
            .ToListAsync(cancellationToken);
        var textUnitIds = founderExamples
            .Select(item => item.TextUnitId)
            .Distinct()
            .ToArray();
        // New durable family work has already admitted these exact controlled
        // source identities through the canonical text-unit boundary. Reuse
        // that bounded, evaluation-scoped map rather than issue a second
        // filtered TextUnits read while other independent family owners are
        // committing their own inserts. SQL Server can otherwise choose an
        // index range for this redundant read that conflicts with those
        // writes, even though the requested text-unit identities are
        // disjoint. Historical replay has no in-scope admission map, so it
        // retains the persisted canonical read below.
        var textUnits = knownFounderTextUnitsById is not null
            ? knownFounderTextUnitsById
                .Where(item =>
                    textUnitIds.Contains(item.Key) &&
                    string.Equals(item.Value.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase) &&
                    item.Value.IsTrainingEligible &&
                    item.Value.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
                .ToDictionary(item => item.Key, item => item.Value)
            : await _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                .Where(item =>
                    textUnitIds.Contains(item.Id) &&
                    item.LanguageCode == languageCode &&
                    item.IsTrainingEligible &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
                .ToDictionaryAsync(item => item.Id, cancellationToken);
        var variationsByExample = knownVariationsByExample is not null
            ? knownVariationsByExample
                .Where(item => exampleIds.Contains(item.Key))
                .ToDictionary(
                    item => item.Key,
                    item => item.Value
                        .OrderBy(variation => variation.Key, StringComparer.Ordinal)
                        .ThenBy(variation => variation.Value, StringComparer.Ordinal)
                        .Select(variation => (variation.Key, variation.Value))
                        .ToArray())
            : (await _db.Set<LegendCurriculumExampleVariation>()
                    .AsNoTracking()
                    .Where(item => exampleIds.Contains(item.CurriculumExampleId))
                    .ToListAsync(cancellationToken))
                .GroupBy(item => item.CurriculumExampleId)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(item => item.Dimension, StringComparer.Ordinal)
                        .ThenBy(item => item.Value, StringComparer.Ordinal)
                        .Select(item => (item.Dimension, item.Value))
                        .ToArray());
        var declaredNodeIds = declaredNodes.Select(item => item.Id).ToArray();
        var declaredRelations = declaredNodeIds.Length == 0
            ? []
            : await (
                from evidence in _db.Set<LegendLanguageMeaningRelationEvidence>().AsNoTracking()
                join relation in _db.Set<LegendLanguageMeaningRelation>().AsNoTracking()
                    on evidence.MeaningRelationId equals relation.Id
                where exampleIds.Contains(evidence.CurriculumExampleId) &&
                    evidence.SupersededUtc == null && relation.SupersededUtc == null &&
                    evidence.ContributionState == "Supported" &&
                    evidence.IsHumanVerifiedSupport &&
                    evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    relation.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    declaredNodeIds.Contains(evidence.SourceMeaningNodeId) &&
                    declaredNodeIds.Contains(evidence.TargetMeaningNodeId)
                select new FounderGraphRelation(
                    evidence.CurriculumExampleId,
                    evidence.SourceMeaningNodeId,
                    evidence.TargetMeaningNodeId,
                    relation.RelationKind,
                    relation.ClauseKey))
                .ToListAsync(cancellationToken);
        var declaredNodesByExample = declaredNodes
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FounderGraphNode>)group.ToArray());
        var declaredRelationsByExample = declaredRelations
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FounderGraphRelation>)group.ToArray());
        var explicitGraphIdentities = new Dictionary<Guid, string>();
        foreach (var (exampleId, nodes) in declaredNodesByExample)
        {
            var relations = declaredRelationsByExample.GetValueOrDefault(exampleId, []);
            if (nodes.Count >= 2 && relations.Count > 0)
                explicitGraphIdentities[exampleId] = FounderMeaningGraphSignature(nodes, relations);
        }

        var lexicalAnchors =
            await _db.Set<LegendLanguageCompositionalAnchor>()
                .AsNoTracking()
                .Where(item =>
                    item.CurriculumFamilyId == family.Id &&
                    exampleIds.Contains(item.CurriculumExampleId) &&
                    item.LanguageCode == languageCode &&
                    item.Provenance ==
                        LegendConnectKnowledgeProvenance.FounderApproved &&
                    item.SupersededUtc == null &&
                    item.LexemeId != null &&
                    item.SemanticSignature != null &&
                    item.SemanticSignature != string.Empty &&
                    item.ComponentStartTokenIndex != null &&
                    item.ComponentLength != null &&
                    item.ComponentLength > 0)
                .ToListAsync(cancellationToken);

        var occurrences =
            await _db.Set<LegendLanguageLexicalOccurrence>()
                .AsNoTracking()
                .Where(item =>
                    textUnitIds.Contains(item.TextUnitId) &&
                    item.SupersededUtc == null)
                .ToListAsync(cancellationToken);

        var existingSignatures =
            await _db.Set<LegendLanguageCompositionalAnchor>()
                .Where(item =>
                    item.CurriculumFamilyId == family.Id &&
                    exampleIds.Contains(item.CurriculumExampleId))
                .Select(item => item.AnchorSignature)
                .ToHashSetAsync(cancellationToken);

        var pending = false;

        for (var leftIndex = 0;
             leftIndex < founderExamples.Count - 1;
             leftIndex++)
        {
            for (var rightIndex = leftIndex + 1;
                 rightIndex < founderExamples.Count;
                 rightIndex++)
            {
                var leftExample = founderExamples[leftIndex];
                var rightExample = founderExamples[rightIndex];

                if (!textUnits.TryGetValue(
                        leftExample.TextUnitId,
                        out var leftTextUnit) ||
                    !textUnits.TryGetValue(
                        rightExample.TextUnitId,
                        out var rightTextUnit) ||
                    !variationsByExample.TryGetValue(
                        leftExample.Id,
                        out var leftVariations) ||
                    !variationsByExample.TryGetValue(
                        rightExample.Id,
                        out var rightVariations) ||
                    !leftVariations.SequenceEqual(rightVariations))
                {
                    continue;
                }
                var graphAuthorized =
                    explicitGraphIdentities.TryGetValue(leftExample.Id, out var leftGraphIdentity) &&
                    explicitGraphIdentities.TryGetValue(rightExample.Id, out var rightGraphIdentity) &&
                    string.Equals(leftGraphIdentity, rightGraphIdentity, StringComparison.Ordinal);

                // Reuse the existing bounded LCS contrast authority. Here its
                // output is source-local evidence rather than a target claim.
                var contrast = TryGetSafeTargetContrast(
                    leftTextUnit.Text,
                    rightTextUnit.Text,
                    "source-corealization");

                if (contrast is null ||
                    contrast.Left.TokenLength ==
                        contrast.Right.TokenLength)
                {
                    continue;
                }

                var leftIsExpanded =
                    contrast.Left.TokenLength >
                    contrast.Right.TokenLength;

                var expandedExample =
                    leftIsExpanded ? leftExample : rightExample;

                var fusedExample =
                    leftIsExpanded ? rightExample : leftExample;

                var expandedSpan =
                    leftIsExpanded ? contrast.Left : contrast.Right;

                var fusedSpan =
                    leftIsExpanded ? contrast.Right : contrast.Left;

                // Co-realization must actually compress structure. A generic
                // paraphrase of equal or greater width is not morphology.
                if (expandedSpan.TokenLength < 2 ||
                    fusedSpan.TokenLength < 1 ||
                    fusedSpan.TokenLength >=
                        expandedSpan.TokenLength)
                {
                    continue;
                }

                var expandedEnd =
                    expandedSpan.StartTokenIndex +
                    expandedSpan.TokenLength;

                var expandedAnchors = lexicalAnchors
                    .Where(item =>
                        item.CurriculumExampleId ==
                            expandedExample.Id &&
                        item.ComponentStartTokenIndex >=
                            expandedSpan.StartTokenIndex &&
                        item.ComponentStartTokenIndex +
                            item.ComponentLength <=
                            expandedEnd)
                    .GroupBy(
                        item => item.SemanticSignature!,
                        StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(item =>
                        item.ComponentStartTokenIndex)
                    .ThenBy(item =>
                        item.ComponentLength)
                    .ToList();

                if (!graphAuthorized)
                {
                    // Flat variations remain evidence only where their own
                    // literal anchor identity proves the semantic component
                    // was directly observed in the expanded surface. This
                    // blocks a replay-created projection from becoming the
                    // source of more derived anchors, while preserving an
                    // ordinary Founder-approved equivalent realization such
                    // as two explicit components compressed into one token.
                    var declaredVariations = variationsByExample[expandedExample.Id]
                        .ToHashSet();
                    expandedAnchors = expandedAnchors
                        .Where(item =>
                            declaredVariations.Contains((item.Dimension, item.Value)) &&
                            IsDirectFounderVariationLexicalAnchor(item))
                        .ToList();
                }

                // A one-component substitution is ordinary lexical variation,
                // not evidence that one form realizes multiple components.
                if (expandedAnchors
                        .Select(item => item.SemanticSignature)
                        .Distinct(StringComparer.Ordinal)
                        .Count() < 2)
                {
                    continue;
                }

                var fusedOccurrence = occurrences
                    .SingleOrDefault(item =>
                        item.TextUnitId ==
                            fusedExample.TextUnitId &&
                        item.TokenIndex ==
                            fusedSpan.StartTokenIndex);

                if (fusedOccurrence is null)
                    continue;

                foreach (var expandedAnchor in expandedAnchors)
                {
                    var identity =
                        LegendLanguageIdentity.TextHash(
                            $"source-corealization|" +
                            $"{SourceCoRealizationDerivationVersion}|" +
                            $"{family.Id:D}|" +
                            $"{expandedExample.Id:D}|" +
                            $"{fusedExample.Id:D}|" +
                            $"{expandedAnchor.SemanticSignature}|" +
                            $"{fusedSpan.StartTokenIndex}|" +
                            $"{fusedSpan.TokenLength}");

                    if (!existingSignatures.Add(identity))
                        continue;

                    var candidate = new LegendLanguageCompositionalAnchor
                    {
                        Id = Guid.NewGuid(),
                        LanguageCode = languageCode,
                        TextUnitId = fusedExample.TextUnitId,
                        LexemeId = fusedOccurrence.LexemeId,
                        ComponentStartTokenIndex = fusedSpan.StartTokenIndex,
                        ComponentLength = fusedSpan.TokenLength,
                        CurriculumFamilyId = family.Id,
                        CurriculumExampleId = fusedExample.Id,
                        Dimension = expandedAnchor.Dimension,
                        Value = expandedAnchor.Value,
                        SemanticSignature = expandedAnchor.SemanticSignature,
                        AnchorSignature = identity,
                        Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                        CreatedUtc = DateTime.UtcNow
                    };
                    var admitted = await LegendConnectCanonicalCurriculumPersistence.AdmitCompositionalAnchorAsync(
                        _db,
                        candidate,
                        cancellationToken);

                    pending |= ReferenceEquals(admitted, candidate);
                }
            }
        }

        if (pending)
            await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Replays existing Founder-approved source curriculum through the same
    /// lexical and semantic-anchor authority used for a new curriculum batch.
    /// It creates no raw submission, corpus asset, or second parser; historic
    /// examples simply receive the missing reusable evidence projection.
    /// </summary>
    private async Task ReconcileFounderApprovedSourceEvidenceAsync(
        Guid familyId,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var family = await _db.Set<LegendCurriculumFamily>()
            .SingleOrDefaultAsync(item => item.Id == familyId, cancellationToken);
        if (family is null || family.Provenance != LegendConnectKnowledgeProvenance.FounderApproved)
            return;

        var examples = await _db.Set<LegendCurriculumExample>()
            .Where(item => item.CurriculumFamilyId == familyId && item.LanguageCode == languageCode &&
                item.DerivedFromCurriculumExampleId == null && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        if (examples.Count == 0)
            return;

        var textUnits = await _db.Set<LegendLanguageTextUnit>()
            .Where(item => examples.Select(example => example.TextUnitId).Contains(item.Id) &&
                item.IsTrainingEligible && item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var inputs = examples
            .Where(item =>
                item.Provenance ==
                    LegendConnectKnowledgeProvenance
                        .FounderApproved &&
                textUnits.ContainsKey(item.TextUnitId))
            .Select(item => new AtomicInput(textUnits[item.TextUnitId], "StructuredExample", null, null, null))
            .ToList();
        if (inputs.Count == 0)
            return;

        await EnsureLanguageLexicalObservationsAsync(inputs, languageCode, cancellationToken);
        await AttachExplicitFounderSemanticAnchorsAsync(
            family,
            examples,
            languageCode,
            sourceTransitions: null,
            semanticSpanGroundings: null,
            knownFounderTextUnitsById: null,
            knownFounderApprovedTextUnitIds: null,
            knownVariationsByExample: null,
            knownLexicalOccurrences: null,
            knownNewCurriculumExampleIds: null,
            cancellationToken);
        await AttachFounderCoRealizedSemanticAnchorsAsync(
            family,
            examples,
            languageCode,
            knownFounderTextUnitsById: null,
            knownVariationsByExample: null,
            cancellationToken);
        await AttachHistoricalSourceFrameProjectionAnchorsAsync(
            family,
            examples,
            languageCode,
            cancellationToken);
    }

    /// <summary>
    /// Reconstructs a missing lexical semantic projection only for historical
    /// Founder curriculum that already has a production-eligible controlled
    /// source frame. The historical authoring format had no <c>@ground</c>
    /// declaration, but the exact canonical source endpoint and the
    /// transition's persisted source frame are jointly sufficient evidence.
    ///
    /// This does not infer from word adjacency, a dictionary, a synonym, or
    /// one nearby example. It requires the same independent-transition,
    /// contradiction, provenance, language, and training-eligibility gates
    /// used by native serving, then projects only the declared source-frame
    /// dimension/value onto that exact full source span. A preexisting exact
    /// full-span control is reused when present; otherwise the source
    /// endpoint's own canonical lexical occurrence supplies only its span
    /// coordinates. It never supplies a semantic value by itself.
    /// </summary>
    private async Task AttachHistoricalSourceFrameProjectionAnchorsAsync(
        LegendCurriculumFamily family,
        IReadOnlyList<LegendCurriculumExample> examples,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var founderExamples = examples
            .Where(item => item.CurriculumFamilyId == family.Id &&
                item.LanguageCode == languageCode &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                item.DerivedFromCurriculumExampleId == null &&
                item.SupersededUtc == null)
            .DistinctBy(item => item.Id)
            .ToList();
        if (founderExamples.Count == 0)
            return;

        var exampleIds = founderExamples.Select(item => item.Id).ToArray();
        var evidence = await _db.Set<LegendSemanticTransitionEvidence>()
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.SourceCurriculumExampleId) &&
                item.SourceLanguageCode == languageCode &&
                item.ResultLanguageCode == languageCode &&
                item.SupersededUtc == null &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                item.ContributionState == "Supported" &&
                item.IsHumanVerifiedSupport)
            .Select(item => new HistoricalSourceFrameEvidence(
                item.TransitionSignature,
                item.SourceCurriculumExampleId,
                item.SourceSemanticFrame))
            .ToListAsync(cancellationToken);
        if (evidence.Count == 0)
            return;

        var eligibleSignatures =
            await GetProductionEligibleSemanticTransitionSignaturesAsync(
                languageCode,
                evidence
                    .Select(item => item.TransitionSignature)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                cancellationToken);
        if (eligibleSignatures.Count == 0)
            return;

        var textUnits = await _db.Set<LegendLanguageTextUnit>()
            .AsNoTracking()
            .Where(item => founderExamples.Select(example => example.TextUnitId).Contains(item.Id) &&
                item.LanguageCode == languageCode &&
                item.IsTrainingEligible &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var variations = await _db.Set<LegendCurriculumExampleVariation>()
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var variationsByExample = variations
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group.ToDictionary(
                    item => item.Dimension,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase));
        // A current Founder manifest can already contain the exact governed
        // meaning-node identity for a source-frame value. Historical
        // whole-source projection exists only to recover the missing evidence
        // in earlier curricula; adding a second whole-span anchor for a value
        // that has a retained explicit node is neither a new capability nor a
        // new canonical fact. Keep the historical path for values genuinely
        // absent from the explicit graph, but make current and replayed
        // processing converge on the same anchor identities.
        var declaredMeaningNodes = await _db
            .Set<LegendLanguageMeaningNodeEvidence>()
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId) &&
                item.LanguageCode == languageCode && item.SupersededUtc == null &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new
            {
                item.CurriculumExampleId,
                item.SemanticSignature
            })
            .ToListAsync(cancellationToken);
        var declaredMeaningSignaturesByExample = declaredMeaningNodes
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.SemanticSignature)
                    .ToHashSet(StringComparer.Ordinal));
        var anchors = await _db.Set<LegendLanguageCompositionalAnchor>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId) &&
                item.LanguageCode == languageCode &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                item.SupersededUtc == null &&
                item.LexemeId != null &&
                item.ComponentStartTokenIndex != null &&
                item.ComponentLength != null &&
                item.ComponentLength > 0)
            .ToListAsync(cancellationToken);
        var anchorsByExample = anchors
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var firstOccurrencesByTextUnit = await _db.Set<LegendLanguageLexicalOccurrence>()
            .AsNoTracking()
            .Where(item => textUnits.Keys.Contains(item.TextUnitId) &&
                item.TokenIndex == 0 && item.SupersededUtc == null)
            .ToDictionaryAsync(item => item.TextUnitId, cancellationToken);
        var pending = false;

        foreach (var sourceEvidence in evidence.Where(item =>
                     eligibleSignatures.Contains(item.TransitionSignature)))
        {
            if (!TryReadSemanticFrame(sourceEvidence.SourceSemanticFrame, out var sourceFrame) ||
                !variationsByExample.TryGetValue(
                    sourceEvidence.SourceCurriculumExampleId,
                    out var variationMap) ||
                !TryResolveControlledSourceFrame(
                    sourceEvidence.SourceSemanticFrame,
                    variationMap,
                    out var semanticValues) ||
                !textUnits.TryGetValue(
                    founderExamples.Single(item =>
                        item.Id == sourceEvidence.SourceCurriculumExampleId).TextUnitId,
                    out var textUnit))
            {
                continue;
            }
            // The persisted query above cannot see anchors staged by an
            // earlier governed evidence row in this same evaluation.  Keep
            // one evaluation-scoped identity set per example so repeated
            // independent support for the same transition cannot enqueue the
            // same canonical anchor twice before SaveChanges reaches the SQL
            // uniqueness constraint.
            if (!anchorsByExample.TryGetValue(
                    sourceEvidence.SourceCurriculumExampleId,
                    out var exampleAnchors))
            {
                exampleAnchors = [];
                anchorsByExample[sourceEvidence.SourceCurriculumExampleId] = exampleAnchors;
            }

            var tokenCount = SurfaceComponents(textUnit.Text).Count;
            if (tokenCount == 0)
                continue;

            // Reuse an existing Founder-controlled exact whole-example span
            // when one exists. Multiple variation names may describe that
            // same span; its lexical identity, not an arbitrary dimension
            // name, is what keeps the projection unambiguous.
            var fullSpanGroups = exampleAnchors
                .Where(item =>
                    !sourceFrame.Dimensions.ContainsKey(item.Dimension) &&
                    item.ComponentStartTokenIndex == 0 &&
                    item.ComponentLength == tokenCount &&
                    variationMap.TryGetValue(item.Dimension, out var value) &&
                    string.Equals(value, textUnit.Text, StringComparison.OrdinalIgnoreCase))
                .GroupBy(item => (
                    item.LexemeId!.Value,
                    item.ComponentStartTokenIndex!.Value,
                    item.ComponentLength!.Value))
                .ToList();
            if (fullSpanGroups.Count > 1)
                continue;

            var span = fullSpanGroups.Count == 1
                ? fullSpanGroups[0].First()
                : null;
            var spanLexemeId = span?.LexemeId;
            var spanStartTokenIndex = span?.ComponentStartTokenIndex;
            var spanLength = span?.ComponentLength;
            if (span is null)
            {
                // Pre-@ground curriculum may have only the canonical source
                // endpoint plus its explicit transition source frame. That
                // direct endpoint relationship is sufficient to use the
                // entire endpoint as the source span, but only after the
                // same transition has passed every production gate above.
                if (!firstOccurrencesByTextUnit.TryGetValue(textUnit.Id, out var firstOccurrence))
                    continue;
                spanLexemeId = firstOccurrence.LexemeId;
                spanStartTokenIndex = 0;
                spanLength = tokenCount;
            }
            if (spanLexemeId is null || spanStartTokenIndex is null || spanLength is null)
                continue;
            var declaredSignatures = declaredMeaningSignaturesByExample
                .GetValueOrDefault(sourceEvidence.SourceCurriculumExampleId);
            foreach (var semanticValue in semanticValues)
            {
                if (declaredSignatures?.Contains(
                        SemanticSignature(semanticValue.Key, semanticValue.Value)) == true)
                {
                    continue;
                }
                if (exampleAnchors.Any(item =>
                        item.Dimension == semanticValue.Key &&
                        item.Value == semanticValue.Value &&
                        item.LexemeId == spanLexemeId &&
                        item.ComponentStartTokenIndex == spanStartTokenIndex &&
                        item.ComponentLength == spanLength))
                {
                    continue;
                }

                var identity = LegendLanguageIdentity.TextHash(
                    $"historical-source-frame-projection|" +
                    $"{HistoricalSourceFrameProjectionDerivationVersion}|" +
                    $"{sourceEvidence.SourceCurriculumExampleId:D}|" +
                    $"{spanLexemeId!.Value:D}|" +
                    $"{spanStartTokenIndex!.Value}|" +
                    $"{spanLength!.Value}|" +
                    $"{sourceEvidence.TransitionSignature}|" +
                    $"{semanticValue.Key}|{semanticValue.Value}");
                if (exampleAnchors.Any(item => item.AnchorSignature == identity))
                    continue;

                var projected = new LegendLanguageCompositionalAnchor
                {
                    Id = Guid.NewGuid(),
                    LanguageCode = languageCode,
                    TextUnitId = textUnit.Id,
                    LexemeId = spanLexemeId,
                    ComponentStartTokenIndex = spanStartTokenIndex,
                    ComponentLength = spanLength,
                    CurriculumFamilyId = family.Id,
                    CurriculumExampleId = sourceEvidence.SourceCurriculumExampleId,
                    Dimension = semanticValue.Key,
                    Value = semanticValue.Value,
                    SemanticSignature = SemanticSignature(
                        semanticValue.Key,
                        semanticValue.Value),
                    AnchorSignature = identity,
                    Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                    CreatedUtc = DateTime.UtcNow
                };
                var admitted = await LegendConnectCanonicalCurriculumPersistence.AdmitCompositionalAnchorAsync(
                    _db,
                    projected,
                    cancellationToken);
                exampleAnchors.Add(admitted);
                pending |= ReferenceEquals(admitted, projected);
            }
        }

        if (pending)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureParagraphNeighborRelationshipsAsync(
        IReadOnlyList<AtomicInput> inputs,
        CancellationToken cancellationToken)
    {
        var sequenced = inputs.Where(item => item.TrainingSubmissionId is not null && item.SequenceNumber is not null && item.ParagraphNumber is not null)
            .GroupBy(item => (item.TrainingSubmissionId!.Value, item.ParagraphNumber!.Value));
        var pending = false;
        foreach (var paragraph in sequenced)
        {
            var ordered = paragraph.OrderBy(item => item.SequenceNumber).ToList();
            for (var index = 0; index < ordered.Count - 1; index++)
            {
                var source = ordered[index];
                var related = ordered[index + 1];
                var signature = $"paragraph-sequence:{paragraph.Key.Item1:D}:{paragraph.Key.Item2}";
                var candidate = new LegendLanguageContextRelationship
                {
                    Id = Guid.NewGuid(),
                    SourceTextUnitId = source.TextUnit.Id,
                    RelatedTextUnitId = related.TextUnit.Id,
                    RelationshipKind = "AdjacentSentence",
                    ContextSignature = signature,
                    SourcePatternSignature = $"sequence:{source.SequenceNumber}>{related.SequenceNumber}",
                    ContextCategory = "ParagraphSequence",
                    Confidence = 1m,
                    QualityState = "Observation",
                    Provenance = "FounderApproved",
                    ObservationCount = 1,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                var existing = await LegendConnectCanonicalCurriculumPersistence.AdmitContextRelationshipAsync(
                    _db,
                    candidate,
                    cancellationToken);
                if (!ReferenceEquals(existing, candidate))
                {
                    if (existing.SupersededUtc is not null)
                    {
                        existing.SupersededUtc = null;
                        existing.UpdatedUtc = DateTime.UtcNow;
                        pending = true;
                    }
                    continue;
                }
                pending = true;
            }
        }
        if (pending)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task AttachExistingExpansionsAsync(
        LegendCurriculumExample sourceExample,
        LegendLanguageTextUnit source,
        CancellationToken cancellationToken)
    {
        var alignments = await _db.Set<LegendTranslationAlignment>()
            .Where(item => item.SourceTextUnitId == source.Id && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        if (alignments.Count == 0)
            return;

        var family = await _db.Set<LegendCurriculumFamily>()
            .SingleAsync(item => item.Id == sourceExample.CurriculumFamilyId, cancellationToken);
        foreach (var alignment in alignments)
        {
            var target = await _db.Set<LegendLanguageTextUnit>()
                .SingleOrDefaultAsync(item => item.Id == alignment.TargetTextUnitId && item.IsTrainingEligible, cancellationToken);
            if (target is null)
                continue;
            var targetExample = await GetOrCreateExampleAsync(
                family,
                target,
                target.LanguageCode,
                sourceExample.Id,
                cancellationToken);
            await CopyVariationsAsync(sourceExample, targetExample, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await AnalyzeFamilyLanguageAsync(family.Id, target.LanguageCode, alignment.PairKey, cancellationToken);
        }
    }

    private Task<LegendCurriculumExample> GetOrCreateExampleAsync(
        LegendCurriculumFamily family,
        LegendLanguageTextUnit textUnit,
        string languageCode,
        Guid? derivedFromCurriculumExampleId,
        CancellationToken cancellationToken) =>
        GetOrCreateExampleAsync(
            family,
            textUnit,
            languageCode,
            derivedFromCurriculumExampleId,
            provenanceOverride: null,
            cancellationToken);

    private async Task<LegendCurriculumExample> GetOrCreateExampleAsync(
        LegendCurriculumFamily family,
        LegendLanguageTextUnit textUnit,
        string languageCode,
        Guid? derivedFromCurriculumExampleId,
        string? provenanceOverride,
        CancellationToken cancellationToken)
    {
        var existing = await _db.Set<LegendCurriculumExample>()
            .SingleOrDefaultAsync(item => item.CurriculumFamilyId == family.Id && item.TextUnitId == textUnit.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.SupersededUtc is not null)
            {
                existing.SupersededUtc = null;
                existing.UpdatedUtc = DateTime.UtcNow;
            }
            if (existing.DerivedFromCurriculumExampleId is null && derivedFromCurriculumExampleId is not null)
            {
                existing.DerivedFromCurriculumExampleId = derivedFromCurriculumExampleId;
                existing.UpdatedUtc = DateTime.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(provenanceOverride) &&
                existing.Provenance !=
                    LegendConnectKnowledgeProvenance.FounderApproved &&
                existing.Provenance != provenanceOverride)
            {
                existing.Provenance = provenanceOverride;
                existing.UpdatedUtc = DateTime.UtcNow;
            }

            return existing;
        }

        var created = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = textUnit.Id,
            LanguageCode = languageCode,
            DerivedFromCurriculumExampleId = derivedFromCurriculumExampleId,
            // A target curriculum example retains the provenance of its own
            // language asset. Founder approval of an English source does not
            // verify a provider-derived target realization.
            Provenance =
                string.IsNullOrWhiteSpace(provenanceOverride)
                    ? textUnit.Provenance
                    : provenanceOverride,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        _db.Set<LegendCurriculumExample>().Add(created);
        return created;
    }

    private async Task EnsureVariationsAsync(
        LegendCurriculumExample curriculumExample,
        IReadOnlyDictionary<string, string> variations,
        CancellationToken cancellationToken)
    {
        foreach (var variation in variations)
        {
            var existing = await _db.Set<LegendCurriculumExampleVariation>()
                .SingleOrDefaultAsync(item => item.CurriculumExampleId == curriculumExample.Id &&
                    item.Dimension == variation.Key, cancellationToken);
            if (existing is null)
            {
                _db.Set<LegendCurriculumExampleVariation>().Add(new LegendCurriculumExampleVariation
                {
                    Id = Guid.NewGuid(),
                    CurriculumExampleId = curriculumExample.Id,
                    Dimension = variation.Key,
                    Value = variation.Value,
                    CreatedUtc = DateTime.UtcNow
                });
                continue;
            }

            if (!string.Equals(existing.Value, variation.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A curriculum example cannot silently change a controlled variation after evidence has been recorded.");
            }
        }
    }

    /// <summary>
    /// Binds an opaque Founder semantic-example key to its canonical example.
    /// The database stores only a stable hash, never a response/prompt
    /// surface.  Once assigned it cannot be silently reassigned, which keeps
    /// future cross-example evidence tied to the same governed graph.
    /// </summary>
    private async Task EnsureFounderSemanticExampleIdentityAsync(
        LegendCurriculumExample curriculumExample,
        string? semanticExampleKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(semanticExampleKey))
            return;

        var identity = LegendLanguageIdentity.TextHash(
            "founder-semantic-example|v1|" + semanticExampleKey);
        if (string.IsNullOrWhiteSpace(curriculumExample.SemanticExampleIdentity))
        {
            var existing = await _db.Set<LegendCurriculumExample>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.SemanticExampleIdentity == identity &&
                    item.Id != curriculumExample.Id, cancellationToken);
            if (existing is not null)
            {
                throw new InvalidOperationException(
                    "A Founder semantic-example key cannot identify more than one canonical curriculum example.");
            }
            curriculumExample.SemanticExampleIdentity = identity;
            curriculumExample.UpdatedUtc = DateTime.UtcNow;
            return;
        }

        if (!string.Equals(curriculumExample.SemanticExampleIdentity, identity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A canonical Founder semantic-example identity cannot silently change after evidence has been recorded.");
        }
    }

    private async Task CopyVariationsAsync(
        LegendCurriculumExample source,
        LegendCurriculumExample target,
        CancellationToken cancellationToken)
    {
        var sourceVariations = await _db.Set<LegendCurriculumExampleVariation>()
            .Where(item => item.CurriculumExampleId == source.Id)
            .ToListAsync(cancellationToken);
        foreach (var variation in sourceVariations)
        {
            var existing = await _db.Set<LegendCurriculumExampleVariation>()
                .SingleOrDefaultAsync(item => item.CurriculumExampleId == target.Id &&
                    item.Dimension == variation.Dimension, cancellationToken);
            if (existing is null)
            {
                _db.Set<LegendCurriculumExampleVariation>().Add(new LegendCurriculumExampleVariation
                {
                    Id = Guid.NewGuid(),
                    CurriculumExampleId = target.Id,
                    Dimension = variation.Dimension,
                    Value = variation.Value,
                    CreatedUtc = DateTime.UtcNow
                });
            }
            else if (!string.Equals(existing.Value, variation.Value, StringComparison.Ordinal))
            {
                throw new LegendControlledVariationConflictException(
                    target.Id,
                    variation.Dimension,
                    existing.Value,
                    variation.Value);
            }
        }
    }

    private async Task RecordHistoricalAlignmentConflictAsync(
        Guid alignmentId,
        LegendControlledVariationConflictException exception,
        CancellationToken cancellationToken)
    {
        const string category = "HistoricalCurriculumReplay";
        const string errorCode = "conflicting_controlled_variation";
        var correlationId = alignmentId.ToString("D");
        var alreadyRecorded = await _db.Set<LegendConnectOperationalEvent>()
            .AsNoTracking()
            .AnyAsync(item => item.Category == category && item.ErrorCode == errorCode &&
                item.CorrelationId == correlationId && !item.IsResolved, cancellationToken);
        if (alreadyRecorded || _operations is null)
            return;

        await _operations.TryRecordAsync(
            category,
            "Warning",
            "Quarantined",
            errorCode: errorCode,
            correlationId: correlationId,
            summary: $"Alignment replay was quarantined because target example {exception.TargetExampleId:D} has conflicting controlled values for '{exception.Dimension}'.",
            cancellationToken: cancellationToken);
    }

    private async Task AnalyzeFamilyLanguageAsync(
        Guid familyId,
        string languageCode,
        string? pairKey,
        CancellationToken cancellationToken,
        IReadOnlyList<AnalysisExample>? knownSourceExamples = null,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? knownVariationsByExample = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<ExplicitControlledAnchor>>? knownExplicitAnchorsByExample = null,
        bool sourceIdentitiesKnownNew = false)
    {
        var examples = pairKey is null && knownSourceExamples is not null
            ? knownSourceExamples
            : await LoadAnalysisExamplesAsync(familyId, languageCode, pairKey, cancellationToken);
        if (examples.Count < 2)
            return;
        var pairScope = pairKey ?? string.Empty;

        var exampleIds = examples.Select(item => item.Example.Id).ToArray();
        // New Founder family work already owns this bounded normalized
        // variation map. Reusing it prevents an unnecessary shared-table read
        // while other independent durable owners are inserting their own
        // controlled variations. Historical and target-language analysis keep
        // the persisted read because no in-scope Founder map exists there.
        var variationsByExample = knownVariationsByExample is not null
            ? knownVariationsByExample
                .Where(item => exampleIds.Contains(item.Key))
                .ToDictionary(
                    item => item.Key,
                    item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                        item.Value,
                        StringComparer.Ordinal))
            : (await _db.Set<LegendCurriculumExampleVariation>()
                    .Where(item => exampleIds.Contains(item.CurriculumExampleId))
                    .ToListAsync(cancellationToken))
                .GroupBy(item => item.CurriculumExampleId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<string, string>)group.ToDictionary(
                        item => item.Dimension,
                        item => item.Value,
                        StringComparer.Ordinal));
        var anchorsByExample = knownExplicitAnchorsByExample is not null
            ? knownExplicitAnchorsByExample
                .Where(item => exampleIds.Contains(item.Key))
                .ToDictionary(item => item.Key, item => item.Value)
            : await LoadExplicitControlledAnchorsByExampleAsync(
                exampleIds,
                languageCode,
                cancellationToken);
        var affectedPatternIds = new HashSet<Guid>();
        var affectedRelationshipIds = new HashSet<Guid>();
        var newlyCreatedPatternIds = new HashSet<Guid>();
        var newlyCreatedRelationshipIds = new HashSet<Guid>();
        var controlledVariationContexts = new List<(
            AnalysisExample Left,
            AnalysisExample Right,
            StructuralComparison Comparison,
            string Dimension,
            string PropositionSignature)>();

        // A bounded family can contribute more than one reusable structural
        // identity.  Acquire every exact identity it can mutate before the
        // first pattern, relationship, or evidence row is staged.  Acquiring
        // one identity lazily inside the comparison loop lets two otherwise
        // valid family owners hold different shared identities and request
        // them in opposite orders.  This plan keeps the existing narrow
        // transaction-owned identity fence, but gives every owner the same
        // global order.  Families with no overlapping canonical identity do
        // not wait for one another.
        var structuralIdentityPlan = new List<(
            AnalysisExample Left,
            AnalysisExample Right,
            StructuralComparison Comparison,
            string Dimension,
            string PropositionSignature)>();
        for (var leftIndex = 0; leftIndex < examples.Count - 1; leftIndex++)
        {
            var left = examples[leftIndex];
            if (!variationsByExample.TryGetValue(left.Example.Id, out var leftVariations))
                continue;
            for (var rightIndex = leftIndex + 1; rightIndex < examples.Count; rightIndex++)
            {
                var right = examples[rightIndex];
                if (!variationsByExample.TryGetValue(right.Example.Id, out var rightVariations))
                    continue;
                foreach (var dimension in leftVariations.Keys
                             .Intersect(rightVariations.Keys, StringComparer.Ordinal))
                {
                    var leftValue = leftVariations[dimension];
                    var rightValue = rightVariations[dimension];
                    if (string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                        continue;
                    var comparison = CanonicalComparison(
                        dimension, left.Text, leftValue, left.Example.Id,
                        right.Text, rightValue, right.Example.Id);
                    structuralIdentityPlan.Add((
                        left,
                        right,
                        comparison,
                        dimension,
                        comparison.PropositionSignature));
                }
            }
        }

        foreach (var identity in structuralIdentityPlan
                     .Select(item => (item.Dimension, item.PropositionSignature))
                     .Distinct()
                     .OrderBy(item => item.Dimension, StringComparer.Ordinal)
                     .ThenBy(item => item.PropositionSignature, StringComparer.Ordinal))
        {
            await AcquireStructuralPatternIdentityLockAsync(
                pairScope,
                languageCode,
                identity.Dimension,
                identity.PropositionSignature,
                cancellationToken);
        }

        foreach (var identity in structuralIdentityPlan
                     .Select(item => new
                     {
                         item.Dimension,
                         Candidate = TryCreateReusableStructuralRelationship(
                             item.Dimension,
                             anchorsByExample.GetValueOrDefault(item.Comparison.BaselineExampleId, []),
                             anchorsByExample.GetValueOrDefault(item.Comparison.ComparedExampleId, []))
                     })
                     .Where(item => item.Candidate is not null)
                     .Select(item => (item.Dimension, item.Candidate!.RelationshipSignature))
                     .Distinct()
                     .OrderBy(item => item.Dimension, StringComparer.Ordinal)
                     .ThenBy(item => item.RelationshipSignature, StringComparer.Ordinal))
        {
            await AcquireStructuralRelationshipIdentityLockAsync(
                pairScope,
                languageCode,
                identity.Dimension,
                identity.RelationshipSignature,
                cancellationToken);
        }

        foreach (var planned in structuralIdentityPlan
                     // SQL Server clusters this entity by its Guid primary
                     // key.  Every family stages its complete bounded set in
                     // that same physical key order; distinct identities can
                     // remain parallel without taking clustered pages in
                     // opposite orders.
                     .OrderBy(item => new SqlGuid(StructuralPatternCanonicalId(
                         pairScope,
                         languageCode,
                         item.Dimension,
                         item.PropositionSignature)))
                     .ThenBy(item => new SqlGuid(StructuralEvidenceCanonicalId(
                         familyId,
                         pairScope,
                         languageCode,
                         item.Dimension,
                         item.Comparison.BaselineExampleId,
                         item.Comparison.ComparedExampleId))))
        {
            var left = planned.Left;
            var right = planned.Right;
            var comparison = planned.Comparison;
            var dimension = planned.Dimension;
            var propositionSignature = planned.PropositionSignature;
                    var pattern = _db.Set<LegendLanguageStructuralPattern>().Local
                        .SingleOrDefault(item => item.PairKey == pairScope &&
                            item.LanguageCode == languageCode && item.VariationDimension == dimension &&
                            item.PropositionSignature == propositionSignature)
                        ?? await _db.Set<LegendLanguageStructuralPattern>()
                            .SingleOrDefaultAsync(item => item.PairKey == pairScope &&
                                item.LanguageCode == languageCode && item.VariationDimension == dimension &&
                                item.PropositionSignature == propositionSignature, cancellationToken);
                    var bothHumanVerified =
                        left.IsHumanVerifiedSupport &&
                        right.IsHumanVerifiedSupport;

                    var bothSystemValidatedMachine =
                        !bothHumanVerified &&
                        string.Equals(
                            left.AlignmentProvenance,
                            LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            right.AlignmentProvenance,
                            LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                            StringComparison.Ordinal);

                    var governedStructuralSupport =
                        bothHumanVerified ||
                        bothSystemValidatedMachine;

                    var evidenceProvenance =
                        bothHumanVerified
                            ? LegendConnectKnowledgeProvenance.FounderApproved
                            : bothSystemValidatedMachine
                                ? LegendConnectKnowledgeProvenance.SystemValidatedMachine
                                : LegendConnectKnowledgeProvenance.ProviderDerived;

                    var contributionState =
                        governedStructuralSupport
                            ? "Supported"
                            : await HasTrustedDirectionalConflictAsync(
                                left,
                                right,
                                pairKey,
                                cancellationToken)
                                ? "Contradictory"
                                : "Insufficient";
                    if (pattern is null)
                    {
                        pattern = new LegendLanguageStructuralPattern
                        {
                            Id = StructuralPatternCanonicalId(
                                pairScope,
                                languageCode,
                                dimension,
                                propositionSignature),
                            CurriculumFamilyId = familyId,
                            PropositionSignature = propositionSignature,
                            PairKey = pairScope,
                            LanguageCode = languageCode,
                            VariationDimension = dimension,
                            // The proposition is shared through
                            // PropositionSignature. This retains the first
                            // observed language-local realization solely for
                            // audit and never for cross-family identity.
                            RealizationSignature = comparison.Signature,
                            MaturityState = "Observation",
                            Provenance = evidenceProvenance,
                            CreatedUtc = DateTime.UtcNow,
                            UpdatedUtc = DateTime.UtcNow
                        };
                        _db.Set<LegendLanguageStructuralPattern>().Add(pattern);
                        newlyCreatedPatternIds.Add(pattern.Id);
                    }
                    else
                    {
                        if (pattern.SupersededUtc is not null)
                            pattern.SupersededUtc = null;
                        if (bothHumanVerified)
                        {
                            pattern.Provenance =
                                LegendConnectKnowledgeProvenance.FounderApproved;
                        }
                        else if (
                            bothSystemValidatedMachine &&
                            !string.Equals(
                                pattern.Provenance,
                                LegendConnectKnowledgeProvenance.FounderApproved,
                                StringComparison.Ordinal))
                        {
                            pattern.Provenance =
                                LegendConnectKnowledgeProvenance.SystemValidatedMachine;
                        }

                        pattern.UpdatedUtc = DateTime.UtcNow;
                    }

                    var sourceIdentity = IndependentSourceIdentity(left.SourceTextUnitId, right.SourceTextUnitId);
                    var existingEvidence = await _db.Set<LegendLanguageStructuralEvidence>().SingleOrDefaultAsync(item =>
                        item.CurriculumFamilyId == familyId && item.PairKey == pairScope && item.LanguageCode == languageCode &&
                        item.VariationDimension == dimension &&
                        item.BaselineCurriculumExampleId == comparison.BaselineExampleId &&
                        item.ComparedCurriculumExampleId == comparison.ComparedExampleId, cancellationToken);
                    var priorPatternId = existingEvidence?.StructuralPatternId;
                    var priorRelationshipId = existingEvidence?.StructuralRelationshipId;
                    LegendLanguageStructuralEvidence structuralEvidence;
                    if (existingEvidence is null)
                    {
                        structuralEvidence = new LegendLanguageStructuralEvidence
                        {
                            Id = StructuralEvidenceCanonicalId(
                                familyId,
                                pairScope,
                                languageCode,
                                dimension,
                                comparison.BaselineExampleId,
                                comparison.ComparedExampleId),
                            StructuralPatternId = pattern.Id,
                            CurriculumFamilyId = familyId,
                            PairKey = pairScope,
                            LanguageCode = languageCode,
                            VariationDimension = dimension,
                            BaselineCurriculumExampleId = comparison.BaselineExampleId,
                            ComparedCurriculumExampleId = comparison.ComparedExampleId,
                            BaselineVariationValue = comparison.BaselineValue,
                            ComparedVariationValue = comparison.ComparedValue,
                            EvidenceSignature = propositionSignature,
                            BaselineComponentSignature = comparison.BaselineComponentSignature,
                            ComparedComponentSignature = comparison.ComparedComponentSignature,
                            IndependentSourceIdentity = sourceIdentity,
                            ContributionState = contributionState,
                            IsHumanVerifiedSupport = bothHumanVerified,
                            Provenance = evidenceProvenance,
                            CreatedUtc = DateTime.UtcNow
                        };
                        _db.Set<LegendLanguageStructuralEvidence>().Add(structuralEvidence);
                    }
                    else
                    {
                        structuralEvidence = existingEvidence;
                        structuralEvidence.StructuralPatternId = pattern.Id;
                        structuralEvidence.IndependentSourceIdentity = sourceIdentity;
                        structuralEvidence.ContributionState =
                            contributionState;

                        structuralEvidence.IsHumanVerifiedSupport =
                            bothHumanVerified;

                        if (bothHumanVerified)
                        {
                            structuralEvidence.Provenance =
                                LegendConnectKnowledgeProvenance.FounderApproved;
                        }
                        else if (
                            bothSystemValidatedMachine &&
                            !string.Equals(
                                structuralEvidence.Provenance,
                                LegendConnectKnowledgeProvenance.FounderApproved,
                                StringComparison.Ordinal))
                        {
                            structuralEvidence.Provenance =
                                LegendConnectKnowledgeProvenance.SystemValidatedMachine;
                        }
                        else if (
                            !bothSystemValidatedMachine &&
                            !string.Equals(
                                structuralEvidence.Provenance,
                                LegendConnectKnowledgeProvenance.FounderApproved,
                                StringComparison.Ordinal) &&
                            !string.Equals(
                                structuralEvidence.Provenance,
                                LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                                StringComparison.Ordinal))
                        {
                            structuralEvidence.Provenance =
                                LegendConnectKnowledgeProvenance.ProviderDerived;
                        }

                        structuralEvidence.SupersededUtc =
                            null;
                    }

                    // Canonically governed Founder or SystemValidatedMachine
                    // evidence may establish a reusable structural candidate.
                    // Provider-derived observations remain insufficient.
                    var relationshipCandidate =
                        governedStructuralSupport
                            ? TryCreateReusableStructuralRelationship(
                                dimension,
                                anchorsByExample.GetValueOrDefault(
                                    comparison.BaselineExampleId,
                                    []),
                                anchorsByExample.GetValueOrDefault(
                                    comparison.ComparedExampleId,
                                    []))
                            : null;
                    if (relationshipCandidate is null)
                    {
                        structuralEvidence.StructuralRelationshipId = null;
                        structuralEvidence.StructuralRelationshipContributionState = null;
                    }
                    else
                    {
                        var relationship = await GetOrCreateStructuralRelationshipAsync(
                            pairScope,
                            languageCode,
                            dimension,
                            relationshipCandidate,
                            evidenceProvenance,
                            cancellationToken);
                        structuralEvidence.StructuralRelationshipId = relationship.Id;
                        structuralEvidence.StructuralRelationshipContributionState =
                            !governedStructuralSupport
                                ? "Insufficient"
                                : string.Equals(
                                    relationship.AnchorLayoutSignature,
                                    relationshipCandidate.AnchorLayoutSignature,
                                    StringComparison.Ordinal)
                                    ? "Supported"
                                    : "Contradictory";
                        affectedRelationshipIds.Add(relationship.Id);
                        if (_db.Entry(relationship).State == EntityState.Added)
                            newlyCreatedRelationshipIds.Add(relationship.Id);
                    }
                    affectedPatternIds.Add(pattern.Id);
                    if (priorPatternId is { } prior && prior != pattern.Id)
                        affectedPatternIds.Add(prior);
                    if (priorRelationshipId is { } priorRelationship &&
                        priorRelationship != structuralEvidence.StructuralRelationshipId)
                        affectedRelationshipIds.Add(priorRelationship);
                    if (bothHumanVerified)
                    {
                        // Context rows are canonical evidence too. Collect
                        // them here and write their unique identities in one
                        // SQL index order below so independent durable owners
                        // cannot form a page-lock cycle merely because their
                        // in-memory example enumeration differs.
                        controlledVariationContexts.Add((
                            left,
                            right,
                            comparison,
                            dimension,
                            propositionSignature));
                    }
        }

        foreach (var context in controlledVariationContexts
                     .OrderBy(item => pairKey ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(item => new SqlGuid(
                         item.Left.Example.Id == item.Comparison.BaselineExampleId
                             ? item.Left.SourceTextUnitId
                             : item.Right.SourceTextUnitId))
                     .ThenBy(item => new SqlGuid(
                         item.Left.Example.Id == item.Comparison.ComparedExampleId
                             ? item.Left.SourceTextUnitId
                             : item.Right.SourceTextUnitId))
                     .ThenBy(item => item.PropositionSignature, StringComparer.Ordinal))
        {
            await EnsureControlledVariationContextAsync(
                context.Left,
                context.Right,
                context.Comparison,
                pairKey,
                context.Dimension,
                context.PropositionSignature,
                cancellationToken);
        }

        if (affectedPatternIds.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            foreach (var patternId in affectedPatternIds)
                await RefreshPatternMaturityAsync(
                    patternId,
                    cancellationToken,
                    useTrackedEvidenceOnly: sourceIdentitiesKnownNew && newlyCreatedPatternIds.Contains(patternId));
            foreach (var relationshipId in affectedRelationshipIds)
                await RefreshStructuralRelationshipMaturityAsync(
                    relationshipId,
                    cancellationToken,
                    useTrackedEvidenceOnly: sourceIdentitiesKnownNew && newlyCreatedRelationshipIds.Contains(relationshipId));
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Target realization candidates are derived from this same controlled
        // directional comparison. They retain a review-only state and do not
        // participate in the existing trusted-anchor queries until a Founder
        // explicitly verifies one.
        if (!string.IsNullOrWhiteSpace(pairKey))
            await DeriveTargetRealizationCandidatesAsync(
                pairKey,
                languageCode,
                cancellationToken);
    }

    private async Task DeriveTargetRealizationCandidatesAsync(
        string pairKey,
        string targetLanguageCode,
        CancellationToken cancellationToken)
    {
        var separator = pairKey.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == pairKey.Length - 1)
            return;
        var sourceLanguageCode = pairKey[..separator];

        // A material change in candidate semantics must retire only the
        // unverified v1 projection. The retained candidate/evidence rows are
        // historical provenance; v2 derives its active replacement through
        // this same evaluator and never creates a parallel review authority.
        await RetireObsoleteTargetRealizationCandidatesAsync(pairKey, cancellationToken);

        var examples = await LoadPairScopedAnalysisExamplesAsync(
            pairKey,
            targetLanguageCode,
            cancellationToken);
        if (examples.Count < 2)
        {
            await RetireNoLongerExclusiveTargetRealizationCandidatesAsync(
                pairKey,
                new HashSet<string>(StringComparer.Ordinal),
                cancellationToken);
            if (_db.ChangeTracker.HasChanges())
                await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var exampleIds = examples.Select(item => item.Example.Id).ToArray();
        var variationsByExample = await _db.Set<LegendCurriculumExampleVariation>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var variationMaps = variationsByExample
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(item => item.Dimension, item => item.Value, StringComparer.Ordinal));
        var observations = BuildTargetContrastObservations(examples, variationMaps);
        var affectedCandidateIds = new HashSet<Guid>();
        var activeCandidateIdentities = new HashSet<string>(StringComparer.Ordinal);

        // A proposal is eligible only when the smallest recurring target span
        // is uniquely attributable to this semantic value across independent
        // human-controlled contrasts. Provider observations can be retained as
        // supplemental evidence after that proof, but can never establish it.
        foreach (var realization in DeriveExclusiveTargetRealizations(observations))
        {
            foreach (var evidenceGroup in realization.Evidence
                         .GroupBy(item => item.Span.SlotSignature, StringComparer.Ordinal))
            {
                var evidence = evidenceGroup.ToList();
                if (evidence.Where(item => item.Observation.IsHumanVerifiedContrast)
                        .Select(item => item.Observation.Example.SourceTextUnitId)
                        .Distinct()
                        .Count() < 2)
                {
                    continue;
                }

                var representative = evidence[0];
                activeCandidateIdentities.Add(TargetRealizationCandidateIdentity(
                    pairKey,
                    SemanticSignature(realization.Dimension, realization.SemanticValue),
                    representative.Span.Realization,
                    representative.Span.SlotSignature,
                    realization.ContextSignature));
                var candidate = await GetOrCreateTargetRealizationCandidateAsync(
                    pairKey,
                    sourceLanguageCode,
                    targetLanguageCode,
                    realization.Dimension,
                    realization.SemanticValue,
                    representative.Span,
                    realization.ContextSignature,
                    cancellationToken);
                foreach (var item in evidence)
                {
                    await AddTargetRealizationEvidenceIfAbsentAsync(
                        candidate,
                        item.Observation.Example,
                        item.Span,
                        item.Observation.IsHumanVerifiedContrast,
                        cancellationToken);
                }
                affectedCandidateIds.Add(candidate.Id);
            }
        }

        await RetireNoLongerExclusiveTargetRealizationCandidatesAsync(
            pairKey,
            activeCandidateIdentities,
            cancellationToken);

        // Persist the candidate/evidence identities before computing their
        // aggregate counts. Relational queries intentionally do not rely on
        // unsaved tracked additions, which also keeps InMemory and SQL Server
        // reconciliation semantics aligned.
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(cancellationToken);

        affectedCandidateIds.UnionWith(await ReconcileRetiredTargetRealizationEvidenceAsync(pairKey, cancellationToken));
        // Retired alignment evidence must be durable before the aggregate
        // queries below. This is the same correction/reconciliation boundary
        // used by current and historical alignment processing.
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(cancellationToken);
        if (affectedCandidateIds.Count > 0)
        {
            var affectedContexts = await _db.Set<LegendLanguageTargetRealizationCandidate>().AsNoTracking()
                .Where(item => affectedCandidateIds.Contains(item.Id))
                .Select(item => new { item.PairKey, item.SemanticSignature, item.ContextSignature })
                .ToListAsync(cancellationToken);
            if (affectedContexts.Count > 0)
            {
                var allConflictingIds = await _db.Set<LegendLanguageTargetRealizationCandidate>().AsNoTracking()
                    .Where(item => item.PairKey == pairKey && item.SupersededUtc == null)
                    .Select(item => new { item.Id, item.PairKey, item.SemanticSignature, item.ContextSignature })
                    .ToListAsync(cancellationToken);
                affectedCandidateIds.UnionWith(allConflictingIds
                    .Where(item => affectedContexts.Any(context =>
                        context.PairKey == item.PairKey && context.SemanticSignature == item.SemanticSignature &&
                        context.ContextSignature == item.ContextSignature))
                    .Select(item => item.Id));
            }
        }

        foreach (var candidateId in affectedCandidateIds)
            await RefreshTargetRealizationCandidateAsync(candidateId, cancellationToken);
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RetireObsoleteTargetRealizationCandidatesAsync(
        string pairKey,
        CancellationToken cancellationToken)
    {
        var candidates = await _db.Set<LegendLanguageTargetRealizationCandidate>()
            .Where(item => item.PairKey == pairKey && item.SupersededUtc == null &&
                item.VerificationState != "FounderVerified" && item.VerificationState != "Rejected")
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var candidate in candidates.Where(item => !HasCurrentTargetRealizationCandidateIdentity(item)))
        {
            candidate.SupersededUtc = now;
            candidate.MaturityState = "Superseded";
            candidate.IsProductionEligible = false;
            candidate.UpdatedUtc = now;
        }
    }

    private async Task RetireNoLongerExclusiveTargetRealizationCandidatesAsync(
        string pairKey,
        IReadOnlySet<string> activeCandidateIdentities,
        CancellationToken cancellationToken)
    {
        var candidates = await _db.Set<LegendLanguageTargetRealizationCandidate>()
            .Where(item => item.PairKey == pairKey && item.SupersededUtc == null &&
                item.VerificationState != "FounderVerified" && item.VerificationState != "Rejected")
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var candidate in candidates.Where(item =>
                     HasCurrentTargetRealizationCandidateIdentity(item) &&
                     !activeCandidateIdentities.Contains(item.CandidateIdentity)))
        {
            candidate.SupersededUtc = now;
            candidate.MaturityState = "Superseded";
            candidate.IsProductionEligible = false;
            candidate.UpdatedUtc = now;
        }
    }

    private static List<TargetContrastObservation> BuildTargetContrastObservations(
        IReadOnlyList<AnalysisExample> examples,
        IReadOnlyDictionary<Guid, Dictionary<string, string>> variationsByExample)
    {
        var observations = new List<TargetContrastObservation>();
        foreach (var family in examples.GroupBy(item => item.Example.CurriculumFamilyId))
        {
            var members = family.OrderBy(item => item.Example.Id).ToList();
            for (var leftIndex = 0; leftIndex < members.Count - 1; leftIndex++)
            {
                var left = members[leftIndex];
                if (!variationsByExample.TryGetValue(left.Example.Id, out var leftVariations) ||
                    left.Example.DerivedFromCurriculumExampleId is null || left.AlignmentId is null)
                {
                    continue;
                }

                for (var rightIndex = leftIndex + 1; rightIndex < members.Count; rightIndex++)
                {
                    var right = members[rightIndex];
                    if (!variationsByExample.TryGetValue(right.Example.Id, out var rightVariations) ||
                        right.Example.DerivedFromCurriculumExampleId is null || right.AlignmentId is null ||
                        !TryGetIsolatedVariationDimension(leftVariations, rightVariations, out var dimension))
                    {
                        continue;
                    }

                    var contrast = TryGetSafeTargetContrast(left.Text, right.Text, dimension);
                    if (contrast is null)
                        continue;

                    var contextSignature = CandidateContextSignature(leftVariations, dimension);
                    var isHumanVerifiedContrast = left.IsHumanVerifiedSupport && right.IsHumanVerifiedSupport;
                    observations.Add(new TargetContrastObservation(
                        left,
                        dimension,
                        leftVariations[dimension],
                        contextSignature,
                        contrast.Left,
                        isHumanVerifiedContrast));
                    observations.Add(new TargetContrastObservation(
                        right,
                        dimension,
                        rightVariations[dimension],
                        contextSignature,
                        contrast.Right,
                        isHumanVerifiedContrast));
                }
            }
        }

        return observations;
    }

    private static bool TryGetIsolatedVariationDimension(
        IReadOnlyDictionary<string, string> leftVariations,
        IReadOnlyDictionary<string, string> rightVariations,
        out string dimension)
    {
        dimension = string.Empty;
        if (leftVariations.Count != rightVariations.Count ||
            leftVariations.Keys.Except(rightVariations.Keys, StringComparer.Ordinal).Any())
        {
            return false;
        }

        var changedDimensions = leftVariations.Keys
            .Where(key => !string.Equals(leftVariations[key], rightVariations[key], StringComparison.Ordinal))
            .ToArray();
        if (changedDimensions.Length != 1)
            return false;

        dimension = changedDimensions[0];
        return true;
    }

    private static IReadOnlyList<ExclusiveTargetRealization> DeriveExclusiveTargetRealizations(
        IReadOnlyList<TargetContrastObservation> observations)
    {
        var derived = new List<ExclusiveTargetRealization>();
        foreach (var group in observations.GroupBy(item => new
                 {
                     item.Dimension,
                     item.SemanticValue,
                     item.ContextSignature
                 }))
        {
            var human = group.Where(item => item.IsHumanVerifiedContrast).ToList();
            if (human.Select(item => item.Example.SourceTextUnitId).Distinct().Count() < 2)
                continue;

            var competitors = observations.Where(item =>
                    item.IsHumanVerifiedContrast &&
                    string.Equals(item.Dimension, group.Key.Dimension, StringComparison.Ordinal) &&
                    string.Equals(item.ContextSignature, group.Key.ContextSignature, StringComparison.Ordinal) &&
                    !string.Equals(item.SemanticValue, group.Key.SemanticValue, StringComparison.Ordinal))
                .ToList();
            var eligible = EnumerateCandidateTokenSequences(human[0].Span)
                .Where(tokens => IsExclusiveTargetRealization(tokens, human, competitors))
                .ToList();
            if (eligible.Count == 0)
                continue;

            var minimumLength = eligible.Min(tokens => tokens.Count);
            var smallest = eligible
                .Where(tokens => tokens.Count == minimumLength)
                .Distinct(TokenSequenceComparer.Instance)
                .ToList();
            // More than one equally-small target span means the retained
            // evidence cannot prove which material realizes this dimension.
            if (smallest.Count != 1)
                continue;

            var tokens = smallest[0];
            var refined = observations
                .Where(item =>
                    string.Equals(item.Dimension, group.Key.Dimension, StringComparison.Ordinal) &&
                    string.Equals(item.SemanticValue, group.Key.SemanticValue, StringComparison.Ordinal) &&
                    string.Equals(item.ContextSignature, group.Key.ContextSignature, StringComparison.Ordinal))
                .Select(item => TryRefineTargetContrastSpan(item.Span, tokens, item.Dimension, out var span)
                    ? new RefinedTargetContrastObservation(item, span)
                    : null)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList();
            if (refined.Where(item => item.Observation.IsHumanVerifiedContrast)
                    .Select(item => item.Observation.Example.SourceTextUnitId)
                    .Distinct()
                    .Count() < 2)
            {
                continue;
            }

            derived.Add(new ExclusiveTargetRealization(
                group.Key.Dimension,
                group.Key.SemanticValue,
                group.Key.ContextSignature,
                string.Join(' ', tokens),
                refined));
        }

        return derived;
    }

    private static IReadOnlyList<IReadOnlyList<string>> EnumerateCandidateTokenSequences(
        TargetContrastSpan span)
    {
        var candidates = new List<IReadOnlyList<string>>();
        var end = span.StartTokenIndex + span.TokenLength;
        for (var start = span.StartTokenIndex; start < end; start++)
        for (var length = 1; start + length <= end; length++)
            candidates.Add(span.TargetTokens.Skip(start).Take(length).ToArray());
        return candidates;
    }

    private static bool IsExclusiveTargetRealization(
        IReadOnlyList<string> tokens,
        IReadOnlyList<TargetContrastObservation> humanSupport,
        IReadOnlyList<TargetContrastObservation> competitors)
    {
        if (tokens.Count == 0)
            return false;

        // The proposed material must occur once, inside the observed target
        // difference, for every independently human-verified contrast.
        if (humanSupport.Any(item => !TryRefineTargetContrastSpan(item.Span, tokens, item.Dimension, out _)))
            return false;

        // If the same surface appears anywhere in a competing semantic value,
        // it is not exclusively attributable to this controlled dimension.
        return competitors.All(item => CountTokenSequenceOccurrences(item.Span.TargetTokens, tokens) == 0);
    }

    private static bool TryRefineTargetContrastSpan(
        TargetContrastSpan source,
        IReadOnlyList<string> realizationTokens,
        string dimension,
        out TargetContrastSpan refined)
    {
        refined = default!;
        var starts = FindTokenSequenceOccurrences(source.TargetTokens, realizationTokens);
        if (starts.Count != 1)
            return false;

        var start = starts[0];
        var sourceEnd = source.StartTokenIndex + source.TokenLength;
        if (start < source.StartTokenIndex || start + realizationTokens.Count > sourceEnd)
            return false;

        refined = BuildTargetContrastSpan(
            source.TargetTokens,
            Enumerable.Range(start, realizationTokens.Count).ToArray(),
            dimension)!;
        return true;
    }

    private static int CountTokenSequenceOccurrences(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate) =>
        FindTokenSequenceOccurrences(source, candidate).Count;

    private static IReadOnlyList<int> FindTokenSequenceOccurrences(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate)
    {
        if (candidate.Count == 0 || candidate.Count > source.Count)
            return [];

        var starts = new List<int>();
        for (var start = 0; start <= source.Count - candidate.Count; start++)
        {
            if (source.Skip(start).Take(candidate.Count)
                    .SequenceEqual(candidate, StringComparer.Ordinal))
            {
                starts.Add(start);
            }
        }
        return starts;
    }

    private async Task<LegendLanguageTargetRealizationCandidate> GetOrCreateTargetRealizationCandidateAsync(
        string pairKey,
        string sourceLanguageCode,
        string targetLanguageCode,
        string dimension,
        string semanticValue,
        TargetContrastSpan span,
        string contextSignature,
        CancellationToken cancellationToken)
    {
        var semanticSignature = SemanticSignature(dimension, semanticValue);
        var identity = TargetRealizationCandidateIdentity(
            pairKey,
            semanticSignature,
            span.Realization,
            span.SlotSignature,
            contextSignature);
        var candidate = _db.Set<LegendLanguageTargetRealizationCandidate>().Local
            .SingleOrDefault(item => item.CandidateIdentity == identity)
            ?? await _db.Set<LegendLanguageTargetRealizationCandidate>()
                .SingleOrDefaultAsync(item => item.CandidateIdentity == identity, cancellationToken);
        if (candidate is not null)
        {
            if (candidate.SupersededUtc is not null)
                candidate.SupersededUtc = null;
            candidate.UpdatedUtc = DateTime.UtcNow;
            return candidate;
        }

        candidate = new LegendLanguageTargetRealizationCandidate
        {
            Id = Guid.NewGuid(),
            PairKey = pairKey,
            SourceLanguageCode = sourceLanguageCode,
            TargetLanguageCode = targetLanguageCode,
            SemanticSignature = semanticSignature,
            VariationDimension = dimension,
            SemanticValue = semanticValue,
            TargetRealization = span.Realization,
            ContextSignature = contextSignature,
            TemplateSignature = span.TemplateSignature,
            SlotSignature = span.SlotSignature,
            CandidateIdentity = identity,
            VerificationState = "Candidate",
            MaturityState = "Observation",
            IsProductionEligible = false,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        _db.Set<LegendLanguageTargetRealizationCandidate>().Add(candidate);
        return candidate;
    }

    private static string TargetRealizationCandidateIdentity(
        string pairKey,
        string semanticSignature,
        string targetRealization,
        string slotSignature,
        string contextSignature) =>
        LegendLanguageIdentity.TextHash(string.Join('|',
            "target-realization",
            TargetRealizationCandidateDerivationVersion,
            pairKey,
            semanticSignature,
            targetRealization,
            slotSignature,
            contextSignature));

    private static bool HasCurrentTargetRealizationCandidateIdentity(
        LegendLanguageTargetRealizationCandidate candidate) =>
        string.Equals(
            candidate.CandidateIdentity,
            TargetRealizationCandidateIdentity(
                candidate.PairKey,
                candidate.SemanticSignature,
                candidate.TargetRealization,
                candidate.SlotSignature,
                candidate.ContextSignature),
            StringComparison.Ordinal);

    private async Task AddTargetRealizationEvidenceIfAbsentAsync(
        LegendLanguageTargetRealizationCandidate candidate,
        AnalysisExample example,
        TargetContrastSpan span,
        bool isHumanVerifiedSupport,
        CancellationToken cancellationToken)
    {
        var sourceExampleId = example.Example.DerivedFromCurriculumExampleId;
        if (sourceExampleId is null || example.AlignmentId is null)
            return;
        var identity = LegendLanguageIdentity.TextHash(string.Join('|',
            candidate.Id.ToString("D"), sourceExampleId.Value.ToString("D"), example.Example.Id.ToString("D"),
            example.AlignmentId.Value.ToString("D"), span.StartTokenIndex, span.TokenLength));
        if (_db.Set<LegendLanguageTargetRealizationEvidence>().Local.Any(item =>
                item.CandidateId == candidate.Id && item.EvidenceIdentity == identity) ||
            await _db.Set<LegendLanguageTargetRealizationEvidence>().AnyAsync(item =>
                item.CandidateId == candidate.Id && item.EvidenceIdentity == identity, cancellationToken))
        {
            return;
        }

        _db.Set<LegendLanguageTargetRealizationEvidence>().Add(new LegendLanguageTargetRealizationEvidence
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            SourceCurriculumExampleId = sourceExampleId.Value,
            TargetCurriculumExampleId = example.Example.Id,
            SourceTextUnitId = example.SourceTextUnitId,
            TargetTextUnitId = example.TargetTextUnitId,
            SourceAlignmentId = example.AlignmentId,
            TargetStartTokenIndex = span.StartTokenIndex,
            TargetTokenLength = span.TokenLength,
            EvidenceIdentity = identity,
            IsHumanVerifiedSupport = isHumanVerifiedSupport,
            // Trust and origin are deliberately separate. A Founder can
            // verify a provider observation through the established review
            // path, but its retained provenance must remain ProviderDerived.
            Provenance = example.AlignmentProvenance,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
    }

    private async Task<IReadOnlyCollection<Guid>> ReconcileRetiredTargetRealizationEvidenceAsync(
        string pairKey,
        CancellationToken cancellationToken)
    {
        var stale = await (
            from evidence in _db.Set<LegendLanguageTargetRealizationEvidence>()
            join candidate in _db.Set<LegendLanguageTargetRealizationCandidate>() on evidence.CandidateId equals candidate.Id
            join alignment in _db.Set<LegendTranslationAlignment>() on evidence.SourceAlignmentId equals alignment.Id
            where candidate.PairKey == pairKey && evidence.SupersededUtc == null && alignment.SupersededUtc != null
            select evidence
        ).ToListAsync(cancellationToken);
        if (stale.Count == 0)
            return [];
        var now = DateTime.UtcNow;
        foreach (var evidence in stale)
        {
            evidence.SupersededUtc = now;
            evidence.UpdatedUtc = now;
        }
        return stale.Select(item => item.CandidateId).Distinct().ToArray();
    }

    /// <summary>
    /// Keeps target-realization support attached to the active directional
    /// alignment lineage. It is invoked from the existing alignment attach
    /// path, so a current correction and a versioned historical replay use
    /// the exact same reconciliation behavior.
    /// </summary>
    private async Task ReconcileRetiredTargetRealizationCandidatesAsync(
        string pairKey,
        CancellationToken cancellationToken)
    {
        var affectedCandidateIds = await ReconcileRetiredTargetRealizationEvidenceAsync(pairKey, cancellationToken);
        if (affectedCandidateIds.Count == 0)
            return;

        await _db.SaveChangesAsync(cancellationToken);
        foreach (var candidateId in affectedCandidateIds)
            await RefreshTargetRealizationCandidateAsync(candidateId, cancellationToken);
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RefreshTargetRealizationCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        var candidate = await _db.Set<LegendLanguageTargetRealizationCandidate>()
            .SingleOrDefaultAsync(item => item.Id == candidateId, cancellationToken);
        if (candidate is null)
            return;

        // A v1 review candidate remains retained historical provenance after
        // v2 supersedes it. Do not let an unrelated alignment replay reopen
        // that over-broad proposal merely because its old evidence still
        // exists.
        if (candidate.SupersededUtc is not null && !HasCurrentTargetRealizationCandidateIdentity(candidate))
        {
            candidate.MaturityState = "Superseded";
            candidate.IsProductionEligible = false;
            return;
        }

        var evidence = await _db.Set<LegendLanguageTargetRealizationEvidence>()
            .Where(item => item.CandidateId == candidateId && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        if (evidence.Count == 0)
        {
            var supersededUtc = candidate.SupersededUtc ?? DateTime.UtcNow;
            candidate.SupersededUtc = supersededUtc;
            candidate.MaturityState = "Superseded";
            candidate.IsProductionEligible = false;
            if (candidate.VerifiedAnchorId is Guid verifiedAnchorId)
            {
                var anchor = await _db.Set<LegendLanguageCompositionalAnchor>()
                    .SingleOrDefaultAsync(item => item.Id == verifiedAnchorId, cancellationToken);
                if (anchor is not null && anchor.SupersededUtc is null)
                    anchor.SupersededUtc = supersededUtc;
            }
            candidate.UpdatedUtc = DateTime.UtcNow;
            return;
        }

        candidate.SupportCount = evidence.Select(item => item.SourceTextUnitId).Distinct().Count();
        candidate.IndependentSourceCount = evidence.Select(item => item.SourceCurriculumExampleId).Distinct().Count();
        candidate.HumanVerifiedSupportCount = evidence.Where(item => item.IsHumanVerifiedSupport)
            .Select(item => item.SourceTextUnitId).Distinct().Count();
        candidate.ProviderOnlySupportCount = evidence.Where(item => !item.IsHumanVerifiedSupport)
            .Select(item => item.SourceTextUnitId).Distinct().Count();
        var conflicting = await _db.Set<LegendLanguageTargetRealizationCandidate>().AsNoTracking()
            .Where(item => item.Id != candidate.Id && item.PairKey == candidate.PairKey &&
                item.SemanticSignature == candidate.SemanticSignature && item.ContextSignature == candidate.ContextSignature &&
                item.SlotSignature == candidate.SlotSignature && item.TargetRealization != candidate.TargetRealization &&
                item.SupersededUtc == null &&
                item.VerificationState != "Rejected")
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        candidate.ContradictionCount = conflicting.Count;
        candidate.Confidence = candidate.SupportCount == 0
            ? 0m
            : decimal.Round((decimal)candidate.HumanVerifiedSupportCount /
                Math.Max(1, candidate.SupportCount + candidate.ContradictionCount), 4);
        candidate.SupersededUtc = null;
        if (candidate.VerificationState is not "FounderVerified" and not "Rejected")
        {
            candidate.VerificationState = candidate.ContradictionCount > 0
                ? "Contradicted"
                : "Candidate";
        }
        candidate.MaturityState = candidate.VerificationState switch
        {
            "Rejected" => "Superseded",
            _ when candidate.ContradictionCount > 0 => "Observation",
            "FounderVerified" when candidate.HumanVerifiedSupportCount >= 3 && candidate.IndependentSourceCount >= 3 => "Supported",
            "FounderVerified" when candidate.HumanVerifiedSupportCount >= 2 => "Candidate",
            "FounderVerified" => "Observation",
            _ => "Observation"
        };
        candidate.IsProductionEligible = IsTargetRealizationProductionEligible(candidate);
        candidate.UpdatedUtc = DateTime.UtcNow;
    }

    private static TargetContrast? TryGetSafeTargetContrast(string leftText, string rightText, string dimension)
    {
        var leftTokens = SurfaceComponents(leftText).Select(item => item.NormalizedText).ToArray();
        var rightTokens = SurfaceComponents(rightText).Select(item => item.NormalizedText).ToArray();
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
            return null;

        var lcs = new int[leftTokens.Length + 1, rightTokens.Length + 1];
        for (var leftIndex = leftTokens.Length - 1; leftIndex >= 0; leftIndex--)
        for (var rightIndex = rightTokens.Length - 1; rightIndex >= 0; rightIndex--)
            lcs[leftIndex, rightIndex] = string.Equals(leftTokens[leftIndex], rightTokens[rightIndex], StringComparison.Ordinal)
                ? 1 + lcs[leftIndex + 1, rightIndex + 1]
                : Math.Max(lcs[leftIndex + 1, rightIndex], lcs[leftIndex, rightIndex + 1]);

        var leftMatched = new HashSet<int>();
        var rightMatched = new HashSet<int>();
        var i = 0;
        var j = 0;
        while (i < leftTokens.Length && j < rightTokens.Length)
        {
            if (string.Equals(leftTokens[i], rightTokens[j], StringComparison.Ordinal))
            {
                leftMatched.Add(i++);
                rightMatched.Add(j++);
            }
            else if (lcs[i + 1, j] >= lcs[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        var leftUnmatched = Enumerable.Range(0, leftTokens.Length).Where(index => !leftMatched.Contains(index)).ToArray();
        var rightUnmatched = Enumerable.Range(0, rightTokens.Length).Where(index => !rightMatched.Contains(index)).ToArray();
        if (!IsOneContiguousSpan(leftUnmatched) || !IsOneContiguousSpan(rightUnmatched))
            return null;

        var left = BuildTargetContrastSpan(leftTokens, leftUnmatched, dimension);
        var right = BuildTargetContrastSpan(rightTokens, rightUnmatched, dimension);
        return left is null || right is null ? null : new TargetContrast(left, right);
    }

    private static string TargetTemplatePreview(
        string targetText,
        int startTokenIndex,
        int tokenLength,
        string dimension)
    {
        var tokens = LegendLanguageIdentity.NormalizeText(targetText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (startTokenIndex < 0 || tokenLength <= 0 || startTokenIndex + tokenLength > tokens.Length)
            return "Observed target template unavailable";
        for (var index = startTokenIndex; index < startTokenIndex + tokenLength; index++)
            tokens[index] = $"<{dimension}>";
        return string.Join(' ', tokens);
    }

    private static bool IsOneContiguousSpan(IReadOnlyList<int> indexes) =>
        indexes.Count > 0 && indexes.Skip(1).Select((index, offset) => index == indexes[offset] + 1).All(item => item);

    private static TargetContrastSpan? BuildTargetContrastSpan(
        IReadOnlyList<string> tokens,
        IReadOnlyList<int> indexes,
        string dimension)
    {
        if (!IsOneContiguousSpan(indexes))
            return null;
        var start = indexes[0];
        var length = indexes.Count;
        var realization = string.Join(' ', indexes.Select(index => tokens[index]));
        if (string.IsNullOrWhiteSpace(realization))
            return null;
        var template = tokens.Select((token, index) => index >= start && index < start + length
            ? $"<{dimension}>"
            : token);
        return new TargetContrastSpan(
            tokens.ToArray(),
            realization,
            start,
            length,
            LegendLanguageIdentity.TextHash("target-template|" + string.Join(' ', template)),
            $"{dimension}:{start}:{length}:of:{tokens.Count}");
    }

    private static string CandidateContextSignature(
        IReadOnlyDictionary<string, string> variations,
        string changedDimension) =>
        LegendLanguageIdentity.TextHash("target-realization-context|" + string.Join('|', variations
            .Where(item => !string.Equals(item.Key, changedDimension, StringComparison.Ordinal))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            // Values are intentionally omitted: independently controlled
            // examples may differ in lexical content while preserving the
            // same declared structural slots. Distinct source senses remain
            // separate through the changed component's SemanticSignature.
            .Select(item => item.Key)));

    private async Task<List<AnalysisExample>> LoadAnalysisExamplesAsync(
        Guid familyId,
        string languageCode,
        string? pairKey,
        CancellationToken cancellationToken)
    {
        if (pairKey is null)
        {
            var sourceExamples = await (
                from example in _db.Set<LegendCurriculumExample>()
                join textUnit in _db.Set<LegendLanguageTextUnit>() on example.TextUnitId equals textUnit.Id
                where example.CurriculumFamilyId == familyId && example.LanguageCode == languageCode &&
                    example.DerivedFromCurriculumExampleId == null && example.SupersededUtc == null &&
                    textUnit.IsTrainingEligible
                orderby example.Id
                select new { Example = example, Text = textUnit.Text, TextUnitId = textUnit.Id, textUnit.Provenance }
            ).ToListAsync(cancellationToken);
            return sourceExamples.Select(item =>
            {
                var founderControlled =
                    string.Equals(
                        item.Provenance,
                        LegendConnectKnowledgeProvenance.FounderApproved,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        item.Example.Provenance,
                        LegendConnectKnowledgeProvenance.FounderApproved,
                        StringComparison.Ordinal);

                var machineControlled =
                    string.Equals(
                        item.Example.Provenance,
                        LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                        StringComparison.Ordinal);

                return new AnalysisExample(
                    item.Example,
                    item.Text,
                    item.TextUnitId,
                    item.TextUnitId,
                    founderControlled,
                    founderControlled
                        ? LegendConnectKnowledgeProvenance.FounderApproved
                        : machineControlled
                            ? LegendConnectKnowledgeProvenance.SystemValidatedMachine
                            : LegendConnectKnowledgeProvenance.ProviderDerived,
                    null);
            }).ToList();
        }

        var targetExamples = await (
            from targetExample in _db.Set<LegendCurriculumExample>()
            join target in _db.Set<LegendLanguageTextUnit>() on targetExample.TextUnitId equals target.Id
            join sourceExample in _db.Set<LegendCurriculumExample>()
                on targetExample.DerivedFromCurriculumExampleId equals sourceExample.Id
            join alignment in _db.Set<LegendTranslationAlignment>()
                on new { SourceTextUnitId = sourceExample.TextUnitId, TargetTextUnitId = targetExample.TextUnitId }
                equals new { alignment.SourceTextUnitId, alignment.TargetTextUnitId }
            where targetExample.CurriculumFamilyId == familyId && targetExample.LanguageCode == languageCode &&
                targetExample.SupersededUtc == null && sourceExample.SupersededUtc == null &&
                target.IsTrainingEligible && alignment.PairKey == pairKey && alignment.SupersededUtc == null
            orderby targetExample.Id
            select new
            {
                Example = targetExample,
                Text = target.Text,
                TargetTextUnitId = target.Id,
                SourceTextUnitId = sourceExample.TextUnitId,
                alignment.HumanVerified,
                alignment.Provenance,
                alignment.Id
            }
        ).ToListAsync(cancellationToken);
        return targetExamples.Select(item => new AnalysisExample(
            item.Example,
            item.Text,
            item.TargetTextUnitId,
            item.SourceTextUnitId,
            item.HumanVerified,
            item.Provenance,
            item.Id)).ToList();
    }

    private static List<AnalysisExample> BuildFounderAnalysisExamples(
        IReadOnlyList<LegendCurriculumExample> sourceExamples,
        IReadOnlyDictionary<Guid, LegendLanguageTextUnit> sourceTextUnitsById)
    {
        return sourceExamples
            .DistinctBy(item => item.Id)
            .Where(item => sourceTextUnitsById.TryGetValue(item.TextUnitId, out var textUnit) &&
                textUnit.IsTrainingEligible)
            .Select(item =>
            {
                var textUnit = sourceTextUnitsById[item.TextUnitId];
                var founderControlled =
                    string.Equals(
                        textUnit.Provenance,
                        LegendConnectKnowledgeProvenance.FounderApproved,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        item.Provenance,
                        LegendConnectKnowledgeProvenance.FounderApproved,
                        StringComparison.Ordinal);
                var machineControlled =
                    string.Equals(
                        item.Provenance,
                        LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                        StringComparison.Ordinal);
                return new AnalysisExample(
                    item,
                    textUnit.Text,
                    textUnit.Id,
                    textUnit.Id,
                    founderControlled,
                    founderControlled
                        ? LegendConnectKnowledgeProvenance.FounderApproved
                        : machineControlled
                            ? LegendConnectKnowledgeProvenance.SystemValidatedMachine
                            : LegendConnectKnowledgeProvenance.ProviderDerived,
                    null);
            })
            .OrderBy(item => item.Example.Id)
            .ToList();
    }

    private async Task<List<AnalysisExample>> LoadPairScopedAnalysisExamplesAsync(
        string pairKey,
        string targetLanguageCode,
        CancellationToken cancellationToken)
    {
        var targetExamples = await (
            from targetExample in _db.Set<LegendCurriculumExample>()
            join target in _db.Set<LegendLanguageTextUnit>() on targetExample.TextUnitId equals target.Id
            join sourceExample in _db.Set<LegendCurriculumExample>()
                on targetExample.DerivedFromCurriculumExampleId equals sourceExample.Id
            join alignment in _db.Set<LegendTranslationAlignment>()
                on new { SourceTextUnitId = sourceExample.TextUnitId, TargetTextUnitId = targetExample.TextUnitId }
                equals new { alignment.SourceTextUnitId, alignment.TargetTextUnitId }
            where targetExample.LanguageCode == targetLanguageCode && targetExample.SupersededUtc == null &&
                sourceExample.SupersededUtc == null && target.IsTrainingEligible &&
                alignment.PairKey == pairKey && alignment.SupersededUtc == null
            orderby targetExample.CurriculumFamilyId, targetExample.Id, alignment.HumanVerified descending, alignment.Id
            select new
            {
                Example = targetExample,
                Text = target.Text,
                TargetTextUnitId = target.Id,
                SourceTextUnitId = sourceExample.TextUnitId,
                alignment.HumanVerified,
                alignment.Provenance,
                alignment.Id
            }
        ).ToListAsync(cancellationToken);

        // One active target example may be retained by more than one
        // alignment lineage. Prefer the human-verified lineage when its text
        // is identical; otherwise each target example remains a distinct
        // canonical observation.
        return targetExamples
            .GroupBy(item => item.Example.Id)
            .Select(group => group.First())
            .Select(item => new AnalysisExample(
                item.Example,
                item.Text,
                item.TargetTextUnitId,
                item.SourceTextUnitId,
                item.HumanVerified,
                item.Provenance,
                item.Id))
            .ToList();
    }

    private async Task<bool> HasTrustedDirectionalConflictAsync(
        AnalysisExample left,
        AnalysisExample right,
        string? pairKey,
        CancellationToken cancellationToken)
    {
        if (pairKey is null)
            return false;
        var providerAlignmentIds = new[] { left.AlignmentId, right.AlignmentId }
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToArray();
        if (providerAlignmentIds.Length == 0)
            return false;
        return await (
            from observed in _db.Set<LegendTranslationAlignment>()
            where providerAlignmentIds.Contains(observed.Id) && !observed.HumanVerified &&
                observed.SupersededUtc == null && observed.PairKey == pairKey
            where _db.Set<LegendTranslationAlignment>().Any(trusted =>
                trusted.PairKey == observed.PairKey && trusted.SourceTextUnitId == observed.SourceTextUnitId &&
                trusted.TargetTextUnitId != observed.TargetTextUnitId && trusted.HumanVerified &&
                trusted.SupersededUtc == null)
            select observed.Id
        ).AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Records the existing context-relationship projection of an explicitly
    /// controlled Founder variation. It is a classification of supplied
    /// evidence, not a classifier for unlabeled text or a second context
    /// graph. Provider-derived comparisons deliberately never enter here.
    /// </summary>
    private async Task EnsureControlledVariationContextAsync(
        AnalysisExample left,
        AnalysisExample right,
        StructuralComparison comparison,
        string? pairKey,
        string dimension,
        string propositionSignature,
        CancellationToken cancellationToken)
    {
        var baseline = left.Example.Id == comparison.BaselineExampleId ? left : right;
        var compared = left.Example.Id == comparison.ComparedExampleId ? left : right;
        var contextCategory = "ControlledVariation:" + dimension;
        // Evaluation-local additions must participate in canonical identity
        // lookup before any persisted query. A normal Founder family may
        // stage the same controlled context through more than one governed
        // comparison before SaveChanges; the tracked identity is authoritative
        // for that bounded evaluation. A new curriculum example can still
        // reuse a pre-existing text-unit identity, so every cache miss must
        // continue through the persisted canonical lookup as well.
        var candidate = new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(),
            PairKey = pairKey,
            SourceTextUnitId = baseline.SourceTextUnitId,
            RelatedTextUnitId = compared.SourceTextUnitId,
            RelationshipKind = "ControlledVariation",
            ContextSignature = propositionSignature,
            SourcePatternSignature = propositionSignature,
            ContextCategory = contextCategory,
            Confidence = 1m,
            QualityState = "Verified",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
            ObservationCount = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        var existing = await LegendConnectCanonicalCurriculumPersistence.AdmitContextRelationshipAsync(
            _db,
            candidate,
            cancellationToken);
        if (ReferenceEquals(existing, candidate))
            return;

        var changed = existing.SupersededUtc is not null ||
            existing.ContextCategory != contextCategory ||
            existing.QualityState != "Verified" ||
            existing.Provenance != LegendConnectKnowledgeProvenance.FounderApproved ||
            existing.Confidence != 1m;
        existing.SupersededUtc = null;
        existing.ContextCategory = contextCategory;
        existing.QualityState = "Verified";
        existing.Provenance = LegendConnectKnowledgeProvenance.FounderApproved;
        existing.Confidence = 1m;
        if (changed)
            existing.UpdatedUtc = DateTime.UtcNow;
    }

    private Dictionary<Guid, IReadOnlyList<ExplicitControlledAnchor>>
        BuildTrackedExplicitControlledAnchorsByExample(
            IReadOnlyCollection<Guid> exampleIds,
            string languageCode)
    {
        // The normal new-family path has just persisted these exact anchors
        // through AttachExplicitFounderSemanticAnchorsAsync in the same owned
        // DbContext. Their tracked canonical identities are sufficient for
        // the immediately following structural analysis; a second shared
        // table read only introduces an avoidable SQL Server lock cycle while
        // unrelated durable owners insert their own anchors. Replays and
        // reused families deliberately take LoadExplicit... below instead.
        var anchors = _db.ChangeTracker.Entries<LegendLanguageCompositionalAnchor>()
            .Where(entry => entry.State != EntityState.Detached &&
                entry.State != EntityState.Deleted &&
                exampleIds.Contains(entry.Entity.CurriculumExampleId) &&
                string.Equals(entry.Entity.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase) &&
                (entry.Entity.Provenance == LegendConnectKnowledgeProvenance.FounderApproved ||
                 entry.Entity.Provenance == LegendConnectKnowledgeProvenance.SystemValidatedMachine) &&
                entry.Entity.SupersededUtc is null &&
                !string.IsNullOrWhiteSpace(entry.Entity.SemanticSignature) &&
                ((entry.Entity.ComponentStartTokenIndex is null && entry.Entity.ComponentLength is null) ||
                 (entry.Entity.ComponentStartTokenIndex is not null && entry.Entity.ComponentLength is not null &&
                  entry.Entity.ComponentStartTokenIndex >= 0 && entry.Entity.ComponentLength > 0)))
            .Select(entry => new ExplicitControlledAnchor(
                entry.Entity.CurriculumExampleId,
                entry.Entity.Dimension,
                entry.Entity.SemanticSignature!,
                entry.Entity.ComponentStartTokenIndex ?? -1,
                entry.Entity.ComponentLength ?? 0))
            .ToList();

        return anchors
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ExplicitControlledAnchor>)group
                    .OrderBy(item => item.ComponentStartTokenIndex)
                    .ThenBy(item => item.ComponentLength)
                    .ThenBy(item => item.Dimension, StringComparer.Ordinal)
                    .ThenBy(item => item.SemanticSignature, StringComparer.Ordinal)
                    .ToList());
    }

    /// <summary>
    /// Loads explicit, Founder-approved controlled anchors. A sentence-level
    /// semantic anchor preserves a supplied variation even when that value is
    /// not itself a literal surface span; reusable relationships still require
    /// an independently stable, observed surface component and never infer an
    /// unanchored role from text.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<ExplicitControlledAnchor>>> LoadExplicitControlledAnchorsByExampleAsync(
        IReadOnlyCollection<Guid> exampleIds,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var anchors = await _db.Set<LegendLanguageCompositionalAnchor>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId) &&
                item.LanguageCode == languageCode &&
                (
                    item.Provenance ==
                        LegendConnectKnowledgeProvenance.FounderApproved ||
                    item.Provenance ==
                        LegendConnectKnowledgeProvenance.SystemValidatedMachine
                ) &&
                item.SupersededUtc == null &&
                item.SemanticSignature != null && item.SemanticSignature != string.Empty &&
                ((item.ComponentStartTokenIndex == null && item.ComponentLength == null) ||
                 (item.ComponentStartTokenIndex != null && item.ComponentLength != null &&
                    item.ComponentStartTokenIndex >= 0 && item.ComponentLength > 0)))
            .Select(item => new ExplicitControlledAnchor(
                item.CurriculumExampleId,
                item.Dimension,
                item.SemanticSignature!,
                item.ComponentStartTokenIndex ?? -1,
                item.ComponentLength ?? 0))
            .ToListAsync(cancellationToken);

        return anchors
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ExplicitControlledAnchor>)group
                    .OrderBy(item => item.ComponentStartTokenIndex)
                    .ThenBy(item => item.ComponentLength)
                    .ThenBy(item => item.Dimension, StringComparer.Ordinal)
                    .ThenBy(item => item.SemanticSignature, StringComparer.Ordinal)
                    .ToList());
    }

    /// <summary>
    /// Builds a candidate relationship only where controlled component
    /// anchors establish one changed dimension and at least one separately
    /// stable, observed surface component. The changed dimension may be an
    /// explicit sentence-level semantic anchor when its supplied value does
    /// not name a literal span; no span is guessed. The signature intentionally
    /// contains no words or proposition values, enabling explicitly anchored
    /// lexical substitutions to contribute without guessing their semantics.
    /// </summary>
    private static ReusableStructuralRelationshipCandidate? TryCreateReusableStructuralRelationship(
        string changedDimension,
        IReadOnlyList<ExplicitControlledAnchor> baselineAnchors,
        IReadOnlyList<ExplicitControlledAnchor> comparedAnchors)
    {
        if (baselineAnchors.Count == 0 || comparedAnchors.Count == 0)
            return null;

        var baselineByDimension = baselineAnchors
            .GroupBy(item => item.Dimension, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.ComponentStartTokenIndex)
                    .ThenBy(item => item.ComponentLength)
                    .ThenBy(item => item.SemanticSignature, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);
        var comparedByDimension = comparedAnchors
            .GroupBy(item => item.Dimension, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.ComponentStartTokenIndex)
                    .ThenBy(item => item.ComponentLength)
                    .ThenBy(item => item.SemanticSignature, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);
        if (!baselineByDimension.ContainsKey(changedDimension) ||
            !comparedByDimension.ContainsKey(changedDimension) ||
            !baselineByDimension.Keys.Order(StringComparer.Ordinal)
                .SequenceEqual(comparedByDimension.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return null;

        var invariantDimensions = baselineByDimension.Keys
            .Where(item => !string.Equals(item, changedDimension, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (invariantDimensions.Count == 0)
            return null;

        // Sentence-level semantic anchors preserve Founder-supplied controlled
        // variation meaning, but cannot by themselves assert a reusable
        // structural layout. At least one invariant must be an observed
        // component span in both examples.
        if (!invariantDimensions.Any(dimension =>
                baselineByDimension[dimension].Any(item => item.ComponentLength > 0) &&
                comparedByDimension[dimension].Any(item => item.ComponentLength > 0)))
        {
            return null;
        }

        // The evidence establishes that precisely one explicitly controlled
        // dimension changed. Any other anchored component must retain the
        // same supplied semantic value in this comparison; otherwise this is
        // not a controlled structural observation.
        foreach (var invariantDimension in invariantDimensions)
        {
            var baselineInvariant = baselineByDimension[invariantDimension]
                .Select(item => item.SemanticSignature)
                .Order(StringComparer.Ordinal);
            var comparedInvariant = comparedByDimension[invariantDimension]
                .Select(item => item.SemanticSignature)
                .Order(StringComparer.Ordinal);
            if (!baselineInvariant.SequenceEqual(comparedInvariant, StringComparer.Ordinal))
                return null;
        }

        // Structural identity counts independently observed component
        // positions, not duplicate semantic assertions attached to the same
        // position. Meaning can disagree at one structural slot; that
        // disagreement must become contradictory evidence against the same
        // relationship rather than creating a different relationship.
        var componentDimensions = string.Join('|', baselineByDimension
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item =>
                $"{item.Key}:{item.Value
                    .Select(anchor => (
                        anchor.ComponentStartTokenIndex,
                        anchor.ComponentLength))
                    .Distinct()
                    .Count()}"));
        var relationshipSignature = LegendLanguageIdentity.TextHash(
            $"controlled-anchor-relationship|{ReusableStructuralRelationshipIdentityVersion}|{changedDimension.Trim().ToLowerInvariant()}|{componentDimensions}");
        var layout = $"baseline:{AnchorLayout(baselineAnchors)}|compared:{AnchorLayout(comparedAnchors)}";
        return new ReusableStructuralRelationshipCandidate(
            relationshipSignature,
            LegendLanguageIdentity.TextHash(layout));
    }

    private async Task<LegendLanguageStructuralRelationship> GetOrCreateStructuralRelationshipAsync(
        string pairKey,
        string languageCode,
        string variationDimension,
        ReusableStructuralRelationshipCandidate candidate,
        string evidenceProvenance,
        CancellationToken cancellationToken)
    {
        // Independent family work may legitimately contribute support to one
        // reusable relationship. The SQL unique index remains the canonical
        // identity invariant; this transaction-owned lock serializes only
        // writers for that exact identity before either can stage a row. It
        // is deliberately narrower than a language or family lock so truly
        // independent governed relationships still execute in parallel.
        await AcquireStructuralRelationshipIdentityLockAsync(
            pairKey,
            languageCode,
            variationDimension,
            candidate.RelationshipSignature,
            cancellationToken);
        var relationship = _db.Set<LegendLanguageStructuralRelationship>().Local
            .SingleOrDefault(item => item.PairKey == pairKey && item.LanguageCode == languageCode &&
                item.VariationDimension == variationDimension &&
                item.RelationshipSignature == candidate.RelationshipSignature)
            ?? await _db.Set<LegendLanguageStructuralRelationship>().SingleOrDefaultAsync(item =>
                item.PairKey == pairKey && item.LanguageCode == languageCode &&
                item.VariationDimension == variationDimension &&
                item.RelationshipSignature == candidate.RelationshipSignature,
                cancellationToken);
        if (relationship is null)
        {
            relationship = new LegendLanguageStructuralRelationship
            {
                Id = StructuralRelationshipCanonicalId(
                    pairKey,
                    languageCode,
                    variationDimension,
                    candidate.RelationshipSignature),
                PairKey = pairKey,
                LanguageCode = languageCode,
                VariationDimension = variationDimension,
                RelationshipSignature = candidate.RelationshipSignature,
                AnchorLayoutSignature = candidate.AnchorLayoutSignature,
                MaturityState = "Observation",
                Provenance = evidenceProvenance,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.Set<LegendLanguageStructuralRelationship>().Add(relationship);
            return relationship;
        }

        relationship.SupersededUtc = null;

        if (string.Equals(
                evidenceProvenance,
                LegendConnectKnowledgeProvenance.FounderApproved,
                StringComparison.Ordinal))
        {
            relationship.Provenance =
                LegendConnectKnowledgeProvenance.FounderApproved;
        }
        else if (
            string.Equals(
                evidenceProvenance,
                LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                StringComparison.Ordinal) &&
            !string.Equals(
                relationship.Provenance,
                LegendConnectKnowledgeProvenance.FounderApproved,
                StringComparison.Ordinal))
        {
            relationship.Provenance =
                LegendConnectKnowledgeProvenance.SystemValidatedMachine;
        }

        relationship.UpdatedUtc =
            DateTime.UtcNow;

        return relationship;
    }

    private async Task AcquireStructuralRelationshipIdentityLockAsync(
        string pairKey,
        string languageCode,
        string variationDimension,
        string relationshipSignature,
        CancellationToken cancellationToken)
    {
        await AcquireCanonicalIdentityLockAsync(
            "structural-relationship",
            pairKey,
            languageCode,
            variationDimension,
            relationshipSignature,
            cancellationToken);
    }

    private async Task AcquireStructuralPatternIdentityLockAsync(
        string pairKey,
        string languageCode,
        string variationDimension,
        string propositionSignature,
        CancellationToken cancellationToken)
    {
        await AcquireCanonicalIdentityLockAsync(
            "structural-pattern",
            pairKey,
            languageCode,
            variationDimension,
            propositionSignature,
            cancellationToken);
    }

    private async Task AcquireCanonicalIdentityLockAsync(
        string identityKind,
        string pairKey,
        string languageCode,
        string variationDimension,
        string canonicalSignature,
        CancellationToken cancellationToken)
    {
        // The durable owned-execution authority supplies one transaction for
        // the entire canonical evaluator. Outside that authority ordinary
        // sequential ingestion retains its existing query/unique-constraint
        // behavior; a session-scoped applock would be unsafe because the
        // relationship write is intentionally deferred to SaveChanges.
        if (!_db.Database.IsSqlServer() || _db.Database.CurrentTransaction is null)
            return;

        var resource = "legend:" + identityKind + ":" + LegendLanguageIdentity.TextHash(
            pairKey + "|" + languageCode + "|" + variationDimension + "|" + canonicalSignature);
        var connection = (SqlConnection)_db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)_db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 60000,
                @DbPrincipal = 'public';
            SELECT @result;
            """;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255)
        {
            Value = resource
        });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not int status || status < 0)
        {
            throw new InvalidOperationException(
                "The canonical structural relationship identity could not acquire its database ownership lock.");
        }
    }

    private async Task RefreshStructuralRelationshipMaturityAsync(
        Guid relationshipId,
        CancellationToken cancellationToken,
        bool useTrackedEvidenceOnly = false)
    {
        var relationship = _db.Set<LegendLanguageStructuralRelationship>().Local
            .SingleOrDefault(item => item.Id == relationshipId)
            ?? await _db.Set<LegendLanguageStructuralRelationship>()
                .SingleAsync(item => item.Id == relationshipId, cancellationToken);
        var evidence = useTrackedEvidenceOnly
            ? _db.ChangeTracker.Entries<LegendLanguageStructuralEvidence>()
                .Where(entry => entry.State != EntityState.Detached && entry.State != EntityState.Deleted &&
                    entry.Entity.StructuralRelationshipId == relationship.Id && entry.Entity.SupersededUtc is null)
                .Select(entry => entry.Entity)
                .ToList()
            : await _db.Set<LegendLanguageStructuralEvidence>()
                .Where(item => item.StructuralRelationshipId == relationship.Id && item.SupersededUtc == null)
                .ToListAsync(cancellationToken);
        var supported = evidence.Where(item => item.StructuralRelationshipContributionState == "Supported").ToList();
        var contradictory = evidence.Where(item => item.StructuralRelationshipContributionState == "Contradictory").ToList();
        relationship.SupportCount = supported.Select(item => item.IndependentSourceIdentity).Distinct(StringComparer.Ordinal).Count();
        relationship.ContradictionCount = contradictory.Select(item => item.IndependentSourceIdentity).Distinct(StringComparer.Ordinal).Count();
        relationship.IndependentSourceCount = supported
            .SelectMany(item => item.IndependentSourceIdentity.Split('|', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .Count();
        relationship.HumanVerifiedSupportCount = supported
            .Where(item => item.IsHumanVerifiedSupport)
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        relationship.ProviderOnlySupportCount = evidence
            .Where(item =>
                string.Equals(
                    item.Provenance,
                    LegendConnectKnowledgeProvenance.ProviderDerived,
                    StringComparison.Ordinal) &&
                item.StructuralRelationshipContributionState != "Contradictory")
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        relationship.Confidence = relationship.SupportCount == 0
            ? 0m
            : decimal.Round((decimal)relationship.HumanVerifiedSupportCount /
                Math.Max(1, relationship.SupportCount + relationship.ContradictionCount), 4);
        relationship.SupersededUtc = evidence.Count == 0 ? DateTime.UtcNow : null;
        relationship.MaturityState = evidence.Count == 0
            ? "Superseded"
            : relationship.ContradictionCount > 0
            ? "Observation"
            : relationship.SupportCount >= 3 && relationship.IndependentSourceCount >= 3
            ? "Supported"
            : relationship.SupportCount == 2
            ? "Candidate"
            : "Observation";
        relationship.IsProductionEligible =
            IsStructuralRelationshipProductionEligible(relationship);
        relationship.UpdatedUtc = DateTime.UtcNow;
    }

    // A reusable relationship preserves the order and span of explicitly
    // controlled components. Absolute positions are intentionally excluded:
    // unrelated, unanchored words between those components do not change the
    // controlled relationship. Reordered controlled components still produce
    // a different layout and remain visible as a contradiction.
    private static string AnchorLayout(IReadOnlyList<ExplicitControlledAnchor> anchors) =>
        string.Join('|', anchors
            .DistinctBy(item => (
                item.Dimension,
                item.ComponentStartTokenIndex,
                item.ComponentLength))
            .OrderBy(item => item.ComponentStartTokenIndex)
            .ThenBy(item => item.ComponentLength)
            .ThenBy(item => item.Dimension, StringComparer.Ordinal)
            .Select(item => $"{item.Dimension}:{item.ComponentLength}"));

    private async Task RefreshPatternMaturityAsync(
        Guid patternId,
        CancellationToken cancellationToken,
        bool useTrackedEvidenceOnly = false)
    {
        var pattern = _db.Set<LegendLanguageStructuralPattern>().Local
            .SingleOrDefault(item => item.Id == patternId)
            ?? await _db.Set<LegendLanguageStructuralPattern>()
                .SingleAsync(item => item.Id == patternId, cancellationToken);
        var evidence = useTrackedEvidenceOnly
            ? _db.ChangeTracker.Entries<LegendLanguageStructuralEvidence>()
                .Where(entry => entry.State != EntityState.Detached && entry.State != EntityState.Deleted &&
                    entry.Entity.StructuralPatternId == pattern.Id && entry.Entity.SupersededUtc is null)
                .Select(entry => entry.Entity)
                .ToList()
            : await _db.Set<LegendLanguageStructuralEvidence>()
                .Where(item => item.StructuralPatternId == pattern.Id && item.SupersededUtc == null)
                .ToListAsync(cancellationToken);
        var supported = evidence.Where(item => item.ContributionState == "Supported").ToList();
        var contradictory = evidence.Where(item => item.ContributionState == "Contradictory").ToList();
        pattern.SupportCount = supported.Select(item => item.IndependentSourceIdentity).Distinct(StringComparer.Ordinal).Count();
        pattern.ContradictionCount = contradictory.Select(item => item.IndependentSourceIdentity).Distinct(StringComparer.Ordinal).Count();
        pattern.IndependentSourceCount = supported
            .SelectMany(item => item.IndependentSourceIdentity.Split('|', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .Count();
        pattern.HumanVerifiedSupportCount = supported
            .Where(item => item.IsHumanVerifiedSupport)
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        pattern.ProviderOnlySupportCount = evidence
            .Where(item =>
                string.Equals(
                    item.Provenance,
                    LegendConnectKnowledgeProvenance.ProviderDerived,
                    StringComparison.Ordinal) &&
                item.ContributionState != "Contradictory")
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        pattern.Confidence = pattern.SupportCount == 0
            ? 0m
            : decimal.Round((decimal)pattern.HumanVerifiedSupportCount /
                Math.Max(1, pattern.SupportCount + pattern.ContradictionCount), 4);
        pattern.SupersededUtc = evidence.Count == 0 ? DateTime.UtcNow : null;
        var wasValidated = pattern.MaturityState == "Validated";
        pattern.MaturityState = evidence.Count == 0
            ? "Superseded"
            : pattern.ContradictionCount > 0
            ? "Observation"
            : pattern.SupportCount >= 3 && pattern.IndependentSourceCount >= 3
            ? wasValidated ? "Validated" : "Supported"
            : pattern.SupportCount == 2
            ? "Candidate"
            : "Observation";
        pattern.IsProductionEligible = IsPatternProductionEligible(pattern);
        pattern.UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// One deterministic production-eligibility policy for already-learned
    /// structural facts. Eligibility does NOT formulate output; it only marks
    /// evidence that has earned the right to participate in a future bounded
    /// formulation authority.
    ///
    /// Provider-derived repetition can never satisfy these gates.
    /// Monolingual/source-only structure can never authorize a directional
    /// translation.
    /// </summary>
    private static bool IsPatternProductionEligible(
        LegendLanguageStructuralPattern pattern) =>
        !string.IsNullOrWhiteSpace(pattern.PairKey) &&
        pattern.SupersededUtc is null &&
        pattern.MaturityState == "Validated" &&
        pattern.SupportCount >= 3 &&
        pattern.IndependentSourceCount >= 3 &&
        pattern.HumanVerifiedSupportCount >= 3 &&
        pattern.ProviderOnlySupportCount == 0 &&
        pattern.ContradictionCount == 0;

    private static bool IsStructuralRelationshipProductionEligible(
        LegendLanguageStructuralRelationship relationship) =>
        !string.IsNullOrWhiteSpace(relationship.PairKey) &&
        relationship.SupersededUtc is null &&
        relationship.MaturityState is "Supported" or "Validated" &&
        relationship.SupportCount >= 3 &&
        relationship.IndependentSourceCount >= 3 &&
        relationship.HumanVerifiedSupportCount >= 3 &&
        relationship.ProviderOnlySupportCount == 0 &&
        relationship.ContradictionCount == 0;

    private static bool IsTargetRealizationProductionEligible(
        LegendLanguageTargetRealizationCandidate candidate) =>
        candidate.SupersededUtc is null &&
        candidate.VerificationState == "FounderVerified" &&
        candidate.MaturityState == "Supported" &&
        candidate.SupportCount >= 3 &&
        candidate.IndependentSourceCount >= 3 &&
        candidate.HumanVerifiedSupportCount >= 3 &&
        candidate.ProviderOnlySupportCount == 0 &&
        candidate.ContradictionCount == 0;

    private static string RealizationSignature(string left, string right) =>
        $"{TextShape(left)}>{TextShape(right)}";

    private static string IndependentSourceIdentity(Guid leftSourceTextUnitId, Guid rightSourceTextUnitId) =>
        string.Compare(leftSourceTextUnitId.ToString("D"), rightSourceTextUnitId.ToString("D"), StringComparison.Ordinal) <= 0
            ? $"{leftSourceTextUnitId:D}|{rightSourceTextUnitId:D}"
            : $"{rightSourceTextUnitId:D}|{leftSourceTextUnitId:D}";

    private static StructuralComparison CanonicalComparison(
        string dimension,
        string leftText,
        string leftValue,
        Guid leftExampleId,
        string rightText,
        string rightValue,
        Guid rightExampleId)
    {
        var forward = string.Compare(leftValue, rightValue, StringComparison.Ordinal) <= 0;
        var baselineText = forward ? leftText : rightText;
        var comparedText = forward ? rightText : leftText;
        return new StructuralComparison(
            RealizationSignature(baselineText, comparedText),
            ControlledPropositionSignature(
                dimension,
                forward ? leftValue : rightValue,
                forward ? rightValue : leftValue),
            forward ? leftExampleId : rightExampleId,
            forward ? rightExampleId : leftExampleId,
            forward ? leftValue : rightValue,
            forward ? rightValue : leftValue,
            ComponentSignature(baselineText),
            ComponentSignature(comparedText));
    }

    private static string ComponentSignature(string text)
    {
        var components = SurfaceComponents(text);
        var composition = string.Join('|', components.Select(item =>
            $"{item.TokenIndex}:{item.CharacterOffset}:{item.CharacterLength}:{item.NormalizedHash}"));
        return $"components:{components.Count}:{LegendLanguageIdentity.TextHash(composition)}";
    }

    private static IReadOnlyList<SurfaceComponent> SurfaceComponents(string text)
    {
        var normalized = LegendLanguageIdentity.NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var components = new List<SurfaceComponent>();
        var cursor = 0;
        foreach (var rawToken in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var rawOffset = normalized.IndexOf(rawToken, cursor, StringComparison.Ordinal);
            cursor = rawOffset + rawToken.Length;
            var surface = rawToken.Trim(SurfaceBoundaryCharacters);
            if (string.IsNullOrWhiteSpace(surface) || !surface.Any(character => char.IsLetterOrDigit(character)))
                continue;
            var surfaceOffset = rawOffset + rawToken.IndexOf(surface, StringComparison.Ordinal);
            var normalizedSurface = surface.Normalize().ToLowerInvariant();
            components.Add(new SurfaceComponent(
                components.Count,
                surfaceOffset,
                surface.Length,
                normalizedSurface,
                LegendLanguageIdentity.TextHash(normalizedSurface)));
        }
        return components;
    }

    private static string AnchorSignature(
        Guid curriculumExampleId,
        Guid? lexemeId,
        string dimension,
        string value) => LegendLanguageIdentity.TextHash(
            $"{curriculumExampleId:D}|{lexemeId?.ToString("D") ?? "sentence"}|{dimension}|{value}");

    private static bool IsDirectFounderVariationLexicalAnchor(
        LegendLanguageCompositionalAnchor anchor) =>
        anchor.LexemeId is { } lexemeId &&
        anchor.ComponentStartTokenIndex is { } startTokenIndex &&
        anchor.ComponentLength is { } componentLength &&
        componentLength > 0 &&
        string.Equals(
            anchor.AnchorSignature,
            AnchorSignature(
                anchor.CurriculumExampleId,
                lexemeId,
                anchor.Dimension,
                anchor.Value + ":" + startTokenIndex + ":" + componentLength),
            StringComparison.Ordinal);

    private static string SemanticSignature(
        string dimension,
        string value) => LegendLanguageIdentity.TextHash(
            $"semantic|{dimension.Trim().ToLowerInvariant()}|{value.Trim().ToLowerInvariant()}");

    private static string ControlledPropositionSignature(
        string dimension,
        string baselineValue,
        string comparedValue)
    {
        var first = baselineValue.Trim().ToLowerInvariant();
        var second = comparedValue.Trim().ToLowerInvariant();
        if (string.Compare(first, second, StringComparison.Ordinal) > 0)
            (first, second) = (second, first);
        return LegendLanguageIdentity.TextHash(
            $"controlled-proposition|{dimension.Trim().ToLowerInvariant()}|{first}|{second}");
    }

    // These are primary-key allocation functions, not alternate semantic
    // identities.  Each is derived from the entity's existing SQL-enforced
    // canonical identity so independently evaluated evidence stages in one
    // stable physical-key order.  Historical rows retain their IDs unchanged.
    private static Guid StructuralPatternCanonicalId(
        string pairKey,
        string languageCode,
        string variationDimension,
        string propositionSignature) =>
        CanonicalPrimaryKey("structural-pattern", string.Join("|",
            pairKey,
            languageCode,
            variationDimension,
            propositionSignature));

    private static Guid StructuralEvidenceCanonicalId(
        Guid curriculumFamilyId,
        string pairKey,
        string languageCode,
        string variationDimension,
        Guid baselineCurriculumExampleId,
        Guid comparedCurriculumExampleId) =>
        CanonicalPrimaryKey("structural-evidence", string.Join("|",
            curriculumFamilyId.ToString("D"),
            pairKey,
            languageCode,
            variationDimension,
            baselineCurriculumExampleId.ToString("D"),
            comparedCurriculumExampleId.ToString("D")));

    private static Guid StructuralRelationshipCanonicalId(
        string pairKey,
        string languageCode,
        string variationDimension,
        string relationshipSignature) =>
        CanonicalPrimaryKey("structural-relationship", string.Join("|",
            pairKey,
            languageCode,
            variationDimension,
            relationshipSignature));

    private static Guid CanonicalPrimaryKey(string kind, string identity) =>
        Guid.ParseExact(LegendLanguageIdentity.TextHash(
            "legend-canonical-primary-key|" + kind + "|" + identity)[..32], "N");

    private static string TextShape(string text)
    {
        var tokens = LegendLanguageIdentity.NormalizeText(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokenShapes = tokens.Select(token =>
        {
            var letters = token.Count(char.IsLetter);
            var digits = token.Count(char.IsDigit);
            var punctuation = token.Length - letters - digits;
            return $"{letters}l{digits}d{punctuation}p";
        });
        return $"{tokens.Length}t[{string.Join(',', tokenShapes)}]";
    }

    private static readonly char[] SurfaceBoundaryCharacters =
        [' ', '.', ',', ';', ':', '!', '?', '"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', '{', '}', '—', '–'];

    private sealed record AtomicInput(
        LegendLanguageTextUnit TextUnit,
        string UnitType,
        Guid? TrainingSubmissionId,
        int? SequenceNumber,
        int? ParagraphNumber,
        bool CanonicalIdentityKnownNew = false);

    private sealed record AnalysisExample(
        LegendCurriculumExample Example,
        string Text,
        Guid TargetTextUnitId,
        Guid SourceTextUnitId,
        bool IsHumanVerifiedSupport,
        string AlignmentProvenance,
        Guid? AlignmentId);

    private sealed record SurfaceComponent(
        int TokenIndex,
        int CharacterOffset,
        int CharacterLength,
        string NormalizedText,
        string NormalizedHash);

    private sealed record StructuralComparison(
        string Signature,
        string PropositionSignature,
        Guid BaselineExampleId,
        Guid ComparedExampleId,
        string BaselineValue,
        string ComparedValue,
        string BaselineComponentSignature,
        string ComparedComponentSignature);

    private sealed record ExplicitControlledAnchor(
        Guid CurriculumExampleId,
        string Dimension,
        string SemanticSignature,
        int ComponentStartTokenIndex,
        int ComponentLength);

    private sealed record ShadowSourceSemanticCandidate(
        string Dimension,
        string Value,
        string SurfaceForm,
        int StartTokenIndex,
        int TokenLength,
        string SemanticSignature,
        IReadOnlyList<Guid> CurriculumExampleIds,
        bool IsDirectFounderFrameProjection = false);

    private sealed record ShadowCompositionComponent(
        string Dimension,
        string Value,
        string SurfaceForm,
        int StartTokenIndex,
        int TokenLength,
        string SemanticSignature);

    private sealed record ShadowRelationshipRequirement(
        string Dimension,
        string FirstValue,
        string SecondValue);

    private sealed record ShadowPropositionRequirement(
        string Dimension,
        string PropositionSignature);

    private sealed record ShadowAnchor(
        Guid CurriculumExampleId,
        string Dimension,
        int StartTokenIndex,
        int TokenLength);

    private enum ShadowRelationshipState
    {
        Insufficient,
        Contradicted,
        Supported
    }

    private sealed record ReusableStructuralRelationshipCandidate(
        string RelationshipSignature,
        string AnchorLayoutSignature);

    private sealed record TargetContrast(
        TargetContrastSpan Left,
        TargetContrastSpan Right);

    private sealed record TargetContrastSpan(
        IReadOnlyList<string> TargetTokens,
        string Realization,
        int StartTokenIndex,
        int TokenLength,
        string TemplateSignature,
        string SlotSignature);

    private sealed record TargetContrastObservation(
        AnalysisExample Example,
        string Dimension,
        string SemanticValue,
        string ContextSignature,
        TargetContrastSpan Span,
        bool IsHumanVerifiedContrast);

    private sealed record RefinedTargetContrastObservation(
        TargetContrastObservation Observation,
        TargetContrastSpan Span);

    private sealed record ExclusiveTargetRealization(
        string Dimension,
        string SemanticValue,
        string ContextSignature,
        string TargetRealization,
        IReadOnlyList<RefinedTargetContrastObservation> Evidence);

    private sealed class TokenSequenceComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        internal static readonly TokenSequenceComparer Instance = new();

        public bool Equals(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
            ReferenceEquals(left, right) ||
            (left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal));

        public int GetHashCode(IReadOnlyList<string> value)
        {
            var hash = new HashCode();
            foreach (var token in value)
                hash.Add(token, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private sealed record StructuralEvidenceImpact(
        Guid StructuralPatternId,
        Guid? StructuralRelationshipId);

    private sealed record SemanticTransitionObservation(
        string TransitionSignature,
        string SourceFrame,
        string ResultFrame,
        string IndependentSourceIdentity,
        string ContributionState,
        bool IsHumanVerifiedSupport,
        string Provenance,
        Guid SourceExampleId,
        Guid ResultExampleId);

    private sealed record FounderSemanticExampleProjection(
        Guid Id,
        Guid CurriculumFamilyId,
        string SemanticExampleIdentity,
        string LanguageCode);

    private sealed record FounderGraphNode(
        Guid Id,
        Guid CurriculumExampleId,
        string SemanticDimension,
        string SemanticValue,
        string SemanticSignature,
        string? ClauseKey);

    private sealed record FounderGraphRelation(
        Guid CurriculumExampleId,
        Guid SourceMeaningNodeId,
        Guid TargetMeaningNodeId,
        string RelationKind,
        string? ClauseKey);

    private sealed record HistoricalSourceFrameEvidence(
        string TransitionSignature,
        Guid SourceCurriculumExampleId,
        string SourceSemanticFrame);

    private sealed record SemanticMissingVariable(
        string Dimension,
        string Variable);

    private sealed record SemanticTransitionCandidate(
        string TransitionSignature,
        NormalizedSemanticFrame SourceFrame,
        NormalizedSemanticFrame ResultFrame,
        IReadOnlyDictionary<string, string> Bindings,
        IReadOnlyList<SemanticMissingVariable> MissingVariables,
        int DirectSourceMatchCount,
        int IndependentEvidenceCount,
        int EvidenceStandard,
        int ReasoningEvidenceCount = 0,
        IReadOnlyList<string>? ReasoningPath = null);

    private sealed record ResponseMeaningPlanSelection(
        string SourceLanguageCode,
        LegendConnectUtteranceMeaningGraphSnapshot Graph,
        SemanticTransitionCandidate? Selected,
        LegendConnectResponseMeaningPlanSnapshot? Plan,
        string ReasonCode);

    private sealed record GovernedContentEvidenceRow(
        string IndependentSourceIdentity,
        string RelationSignature,
        int SupportCount,
        int IndependentSourceCount,
        int ContradictionCount,
        string MaturityState,
        bool IsProductionEligible,
        string SubjectSemanticSignature,
        string SubjectDimension,
        string SubjectValue,
        string ContentSemanticSignature,
        string ContentDimension,
        string ContentValue);

    private sealed record GovernedContentResolution(
        bool IsRequired,
        bool Succeeded,
        string ReasonCode,
        IReadOnlyDictionary<string, string> MergedBindings,
        IReadOnlyDictionary<string, string> ContentVariableBindings,
        IReadOnlyList<LegendConnectGovernedContentFactSnapshot> Facts,
        int EvidenceCount,
        int EvidenceStandard)
    {
        internal static GovernedContentResolution NotRequired(
            IReadOnlyDictionary<string, string> bindings) =>
            new(false, true, "response_content_not_required", bindings,
                new Dictionary<string, string>(StringComparer.Ordinal), [], 0,
                HigherGovernedEvidenceStandard);

        internal static GovernedContentResolution Failure(string reasonCode) =>
            new(true, false, reasonCode,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal), [], 0,
                BroadGovernedEvidenceStandard);

        internal static GovernedContentResolution Success(
            IReadOnlyDictionary<string, string> mergedBindings,
            IReadOnlyDictionary<string, string> contentBindings,
            IReadOnlyList<LegendConnectGovernedContentFactSnapshot> facts) =>
            new(true, true, "response_content_bound_governed", mergedBindings,
                contentBindings, facts, facts.Sum(item => item.IndependentSourceCount),
                facts.All(item => item.IsProductionEligible)
                    ? HigherGovernedEvidenceStandard
                    : BroadGovernedEvidenceStandard);
    }

    /// <summary>
    /// The sole selection result shared by legacy native serving and the
    /// non-serving response-plan observation.  It contains only governed
    /// semantic state; realization remains outside this selection boundary.
    /// </summary>
    private sealed record SemanticTransitionSelection(
        string SourceLanguageCode,
        IReadOnlyList<LegendShadowSourceSemanticComponent> SourceComponents,
        SemanticTransitionCandidate? Selected,
        string ReasonCode,
        bool IsAmbiguous,
        bool IsContradicted)
    {
        public static SemanticTransitionSelection Insufficient(
            string reasonCode,
            string sourceLanguageCode = "",
            IReadOnlyList<LegendShadowSourceSemanticComponent>? sourceComponents = null) =>
            new(sourceLanguageCode, sourceComponents ?? [], null, reasonCode, false, false);

        public static SemanticTransitionSelection Ambiguous(
            string reasonCode,
            string sourceLanguageCode = "",
            IReadOnlyList<LegendShadowSourceSemanticComponent>? sourceComponents = null) =>
            new(sourceLanguageCode, sourceComponents ?? [], null, reasonCode, true, false);

        public static SemanticTransitionSelection Contradicted(
            string reasonCode,
            string sourceLanguageCode = "",
            IReadOnlyList<LegendShadowSourceSemanticComponent>? sourceComponents = null) =>
            new(sourceLanguageCode, sourceComponents ?? [], null, reasonCode, false, true);
    }

    private sealed record GovernedReasonedResponseSelection(
        SemanticTransitionCandidate? Selected,
        string ReasonCode,
        bool IsAmbiguous,
        bool IsContradicted)
    {
        internal static GovernedReasonedResponseSelection None =>
            new(null, "governed_reasoning_not_applicable", false, false);

        internal static GovernedReasonedResponseSelection Success(SemanticTransitionCandidate selected) =>
            new(selected, "response_meaning_plan_governed_reasoned", false, false);

        internal static GovernedReasonedResponseSelection Failure(string reasonCode) =>
            new(null, reasonCode, false, false);

        internal static GovernedReasonedResponseSelection Ambiguous(string reasonCode) =>
            new(null, reasonCode, true, false);

        internal static GovernedReasonedResponseSelection Contradicted(string reasonCode) =>
            new(null, reasonCode, false, true);
    }

    private sealed record GroundedContextFrame(
        string ResultFrameSignature,
        IReadOnlyDictionary<string, string> Values);

    private sealed record SemanticResultExample(
        Guid Id,
        Guid CurriculumFamilyId,
        Guid TextUnitId,
        string Text);

    private sealed record SemanticVariation(
        Guid ExampleId,
        string Dimension,
        string Value);

    private sealed record SemanticAnchor(
        Guid ExampleId,
        string Dimension,
        string Value,
        int StartTokenIndex,
        int TokenLength);

    private sealed record FounderLexicalAnchor(
        Guid ExampleId,
        Guid LexemeId,
        string Dimension,
        string Value,
        int StartTokenIndex,
        int TokenLength);

    private sealed record FounderLexicalOccurrence(
        Guid TextUnitId,
        int TokenIndex,
        Guid LexemeId,
        string SurfaceForm);

    private sealed record FounderVariation(
        string Dimension,
        string Value);

    private sealed record SemanticLayoutComponent(
        string Dimension,
        string Value,
        int StartTokenIndex,
        int TokenLength,
        string SurfaceForm);

    private sealed record SemanticRealizationLayout(
        Guid FamilyId,
        string Shape,
        IReadOnlyList<SemanticLayoutComponent> Components,
        string TerminalPunctuation,
        string ObservedText);

    private sealed record SemanticTransitionRealization(
        string? Text,
        int LayoutEvidenceCount,
        string? Reason,
        bool IsAmbiguous,
        int EvidenceStandard = BroadGovernedEvidenceStandard,
        bool IsOriginal = false)
    {
        internal static SemanticTransitionRealization Insufficient(string reason) =>
            new(null, 0, reason, false);

        internal static SemanticTransitionRealization Ambiguous(string reason) =>
            new(null, 0, reason, true);
    }

    private sealed record NormalizedSemanticFrame(
        IReadOnlyDictionary<string, string> Dimensions,
        string Serialized,
        string Signature);

    private sealed record NormalizedSemanticTransition(
        NormalizedSemanticFrame Source,
        NormalizedSemanticFrame Result);

    private sealed record NormalizedSemanticSpanGrounding(
        string SemanticDimension,
        string SurfaceDimension);

    private static IReadOnlyList<NormalizedCurriculumExample>? NormalizeExamples(
        IReadOnlyList<LegendConnectCurriculumExampleSubmission>? examples)
    {
        if (examples is null || examples.Count is < 2 or > MaximumExamplesPerBatch)
            return null;

        var normalized = new List<NormalizedCurriculumExample>(examples.Count);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var example in examples)
        {
            var text = LegendLanguageIdentity.NormalizeText(example.Text);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 10_000 || !hashes.Add(LegendLanguageIdentity.TextHash(text)))
                return null;
            if (example.Variations is null || example.Variations.Count == 0 || example.Variations.Count > 12)
                return null;

            var variations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var variation in example.Variations)
            {
                var dimension = NormalizeDimension(variation.Key);
                var value = NormalizeOptional(variation.Value, 160);
                if (dimension is null || value is null || !variations.TryAdd(dimension, value))
                    return null;
            }
            var semanticExampleKey = string.IsNullOrWhiteSpace(example.SemanticExampleKey)
                ? null
                : NormalizeDimension(example.SemanticExampleKey);
            if ((example.SemanticExampleKey is not null && semanticExampleKey is null) ||
                !TryNormalizeMeaningGraph(example.MeaningGraph, text, out var meaningGraph))
                return null;
            normalized.Add(new NormalizedCurriculumExample(text, variations, meaningGraph, semanticExampleKey));
        }
        return normalized;
    }

    private static bool TryNormalizeMeaningGraph(
        LegendConnectMeaningGraphSubmission? graph,
        string exampleText,
        out NormalizedMeaningGraph? normalized)
    {
        normalized = null;
        if (graph is null)
            return true;
        if (graph.Nodes is null || graph.Nodes.Count is < 1 or > 24 ||
            graph.Relations is null || graph.Relations.Count > 64)
        {
            return false;
        }

        var nodes = new List<NormalizedMeaningNode>(graph.Nodes.Count);
        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            var nodeKey = NormalizeDimension(node.NodeKey);
            var dimension = NormalizeDimension(node.SemanticDimension);
            var value = NormalizeOptional(node.SemanticValue, 160);
            var surface = LegendLanguageIdentity.NormalizeText(node.SurfaceText);
            var clauseKey = string.IsNullOrWhiteSpace(node.ClauseKey)
                ? null
                : NormalizeDimension(node.ClauseKey);
            if (nodeKey is null || dimension is null || value is null ||
                string.IsNullOrWhiteSpace(surface) || surface.Length > 1_000 ||
                (node.ClauseKey is not null && clauseKey is null) ||
                !nodeKeys.Add(nodeKey) ||
                node.SurfaceOccurrence is < 1 or > 32 ||
                !TryFindSurfaceSpan(exampleText, surface, node.SurfaceOccurrence, out _, out _))
            {
                return false;
            }
            nodes.Add(new NormalizedMeaningNode(
                nodeKey,
                dimension,
                value,
                surface,
                clauseKey,
                node.SurfaceOccurrence));
        }

        var relations = new List<NormalizedMeaningRelation>(graph.Relations.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in graph.Relations)
        {
            var sourceNodeKey = NormalizeDimension(relation.SourceNodeKey);
            var targetNodeKey = NormalizeDimension(relation.TargetNodeKey);
            var relationKind = NormalizeDimension(relation.RelationKind);
            var clauseKey = string.IsNullOrWhiteSpace(relation.ClauseKey)
                ? null
                : NormalizeDimension(relation.ClauseKey);
            if (sourceNodeKey is null || targetNodeKey is null || relationKind is null ||
                (relation.ClauseKey is not null && clauseKey is null) ||
                string.Equals(sourceNodeKey, targetNodeKey, StringComparison.Ordinal) ||
                !nodeKeys.Contains(sourceNodeKey) || !nodeKeys.Contains(targetNodeKey) ||
                !identities.Add(sourceNodeKey + "\u001f" + relationKind + "\u001f" + targetNodeKey + "\u001f" + (clauseKey ?? string.Empty)))
            {
                return false;
            }
            relations.Add(new NormalizedMeaningRelation(
                sourceNodeKey,
                relationKind,
                targetNodeKey,
                clauseKey));
        }

        var references = new List<NormalizedDiscourseReference>(graph.DiscourseReferences?.Count ?? 0);
        var referenceIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in graph.DiscourseReferences ?? [])
        {
            var selectorNodeKey = NormalizeDimension(reference.SelectorNodeKey);
            var entityDimension = NormalizeDimension(reference.EntitySemanticDimension);
            var resolutionMode = NormalizeDimension(reference.ResolutionMode);
            var allowedRoles = (reference.AllowedSourceRoles ?? ["user", "assistant"])
                .Select(item => item?.Trim().ToLowerInvariant())
                .Where(item => item is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (selectorNodeKey is null || entityDimension is null ||
                resolutionMode is not ("ordinal" or "unique") ||
                !nodeKeys.Contains(selectorNodeKey) ||
                allowedRoles.Length is < 1 or > 2 ||
                allowedRoles.Any(item => item is not ("user" or "assistant")) ||
                (resolutionMode == "ordinal" && reference.SelectionRank is not (>= 1 and <= 16)) ||
                (resolutionMode == "unique" && reference.SelectionRank is not null))
            {
                return false;
            }
            var identity = string.Join("\u001f", selectorNodeKey, entityDimension, resolutionMode,
                reference.SelectionRank?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                string.Join("|", allowedRoles), reference.ReplacesActiveBinding ? "replace" : "set");
            if (!referenceIdentities.Add(identity))
                return false;
            references.Add(new NormalizedDiscourseReference(
                selectorNodeKey, entityDimension, resolutionMode, reference.SelectionRank,
                allowedRoles, reference.ReplacesActiveBinding));
        }

        normalized = new NormalizedMeaningGraph(nodes, relations, references);
        return true;
    }

    private static IReadOnlyList<NormalizedSemanticTransition>? NormalizeSemanticTransitions(
        IReadOnlyList<LegendConnectSemanticTransitionSubmission>? transitions)
    {
        if (transitions is null || transitions.Count == 0)
            return [];
        if (transitions.Count > 12)
            return null;

        var normalized = new List<NormalizedSemanticTransition>(transitions.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transition in transitions)
        {
            var source = NormalizeSemanticFrame(transition.Source?.Dimensions);
            var result = NormalizeSemanticFrame(transition.Result?.Dimensions);
            if (source is null || result is null)
                return null;

            // A result variable shared with the source carries an ordinary
            // conversational binding. A result-only variable remains
            // deliberately unresolved here: Stage 6 may bind it only from a
            // mature, contradiction-free governed content fact. It never
            // authorizes a free-form value or a response lookup.

            var identity = source.Serialized + "\n→\n" + result.Serialized;
            if (!identities.Add(identity))
                return null;
            normalized.Add(new NormalizedSemanticTransition(source, result));
        }
        return normalized;
    }

    private static IReadOnlyList<NormalizedSemanticSpanGrounding>? NormalizeSemanticSpanGroundings(
        IReadOnlyList<LegendConnectSemanticSpanGroundingSubmission>? groundings)
    {
        if (groundings is null || groundings.Count == 0)
            return [];
        if (groundings.Count > 12)
            return null;

        var normalized = new List<NormalizedSemanticSpanGrounding>(groundings.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grounding in groundings)
        {
            var semanticDimension = NormalizeDimension(grounding.SemanticDimension);
            var surfaceDimension = NormalizeDimension(grounding.SurfaceDimension);
            if (semanticDimension is null || surfaceDimension is null ||
                !identities.Add(semanticDimension + "→" + surfaceDimension))
            {
                return null;
            }
            normalized.Add(new NormalizedSemanticSpanGrounding(semanticDimension, surfaceDimension));
        }
        return normalized;
    }

    private static NormalizedSemanticFrame? NormalizeSemanticFrame(
        IReadOnlyDictionary<string, string>? dimensions)
    {
        if (dimensions is null || dimensions.Count is < 1 or > 12)
            return null;

        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in dimensions)
        {
            var dimension = NormalizeDimension(item.Key);
            var value = NormalizeSemanticFrameValue(item.Value);
            if (dimension is null || value is null || !normalized.TryAdd(dimension, value))
                return null;
        }

        var serialized = JsonSerializer.Serialize(normalized);
        return serialized.Length > 4000
            ? null
            : new NormalizedSemanticFrame(normalized, serialized, FrameSignature(serialized));
    }

    private static string? NormalizeSemanticFrameValue(string? value)
    {
        var normalized = NormalizeOptional(value, 160);
        if (normalized is null)
            return null;
        if (!normalized.StartsWith('$'))
            return normalized;
        if (normalized.Length is < 2 or > 81 ||
            !char.IsLetter(normalized[1]) ||
            normalized[2..].Any(character =>
                !(char.IsLetterOrDigit(character) || character is '_' or '-')))
        {
            return null;
        }
        return "$" + normalized[1..].ToLowerInvariant();
    }

    private static bool IsSemanticVariable(string value) =>
        value.Length > 1 && value[0] == '$';

    private static bool TryBindSemanticFrame(
        NormalizedSemanticFrame frame,
        IReadOnlyDictionary<string, string> variations,
        out IReadOnlyDictionary<string, string> bindings)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in frame.Dimensions)
        {
            if (!variations.TryGetValue(item.Key, out var observed) ||
                string.IsNullOrWhiteSpace(observed))
            {
                bindings = resolved;
                return false;
            }

            if (!IsSemanticVariable(item.Value))
            {
                if (!string.Equals(item.Value, observed, StringComparison.OrdinalIgnoreCase))
                {
                    bindings = resolved;
                    return false;
                }
                continue;
            }

            if (resolved.TryGetValue(item.Value, out var existing) &&
                !string.Equals(existing, observed, StringComparison.OrdinalIgnoreCase))
            {
                bindings = resolved;
                return false;
            }
            resolved[item.Value] = observed;
        }
        bindings = resolved;
        return true;
    }

    private static bool BindingsAreCompatible(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        foreach (var item in first)
        {
            if (second.TryGetValue(item.Key, out var value) &&
                !string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private async Task PersistGovernedSemanticTransitionEvidenceAsync(
        LegendCurriculumFamily family,
        IReadOnlyList<LegendCurriculumExample> examples,
        IReadOnlyList<NormalizedSemanticTransition> transitions,
        string languageCode,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? knownVariationsByExample,
        IReadOnlySet<Guid>? knownNewCurriculumExampleIds,
        string provenance,
        bool isHumanVerifiedSupport,
        CancellationToken cancellationToken)
    {
        if (transitions.Count == 0)
            return;

        var exampleIds = examples.Select(item => item.Id).Distinct().ToArray();
        var currentTransitionSignatures = transitions
            .Select(item => FrameSignature(
                item.Source.Serialized + "\n→\n" + item.Result.Serialized))
            .ToHashSet(StringComparer.Ordinal);
        // A Founder manifest can replace a transition declaration while
        // retaining the same canonical examples.  The previous transition is
        // historical evidence, not simultaneous authority.  Retire it through
        // the normal lifecycle before persisting the new declaration so the
        // evaluator never sees competing active frames from one family.
        var retiredAt = DateTime.UtcNow;
        var allExamplesAreNew = knownNewCurriculumExampleIds is not null &&
            exampleIds.All(knownNewCurriculumExampleIds.Contains);
        var replacedTransitions = allExamplesAreNew
            ? []
            : await _db.Set<LegendSemanticTransitionEvidence>()
                .Where(item => item.SupersededUtc == null &&
                    item.FounderSemanticExampleRelationEvidenceId == null &&
                    exampleIds.Contains(item.SourceCurriculumExampleId) &&
                    exampleIds.Contains(item.ResultCurriculumExampleId) &&
                    !currentTransitionSignatures.Contains(item.TransitionSignature))
                .ToListAsync(cancellationToken);
        foreach (var replaced in replacedTransitions)
        {
            replaced.SupersededUtc = retiredAt;
            replaced.UpdatedUtc = retiredAt;
        }
        if (replacedTransitions.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        var variationMaps = knownVariationsByExample is not null
            ? knownVariationsByExample
                .Where(item => exampleIds.Contains(item.Key))
                .ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    EqualityComparer<Guid>.Default)
            : (await _db.Set<LegendCurriculumExampleVariation>()
                    .Where(item => exampleIds.Contains(item.CurriculumExampleId))
                    .ToListAsync(cancellationToken))
                .GroupBy(item => item.CurriculumExampleId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<string, string>)group.ToDictionary(
                        item => item.Dimension,
                        item => item.Value,
                        StringComparer.OrdinalIgnoreCase));
        var now = DateTime.UtcNow;
        var independentSourceIdentity = "family:" + family.Id.ToString("N");

        foreach (var transition in transitions)
        {
            var transitionSignature = FrameSignature(
                transition.Source.Serialized + "\n→\n" + transition.Result.Serialized);
            var sources = examples
                .Where(example => variationMaps.TryGetValue(example.Id, out var map) &&
                    TryBindSemanticFrame(transition.Source, map, out _))
                .ToList();
            var results = examples
                .Where(example => variationMaps.TryGetValue(example.Id, out var map) &&
                    TryBindSemanticFrame(transition.Result, map, out _))
                .ToList();

            foreach (var source in sources)
            {
                var sourceBindings = variationMaps.TryGetValue(source.Id, out var sourceMap) &&
                    TryBindSemanticFrame(transition.Source, sourceMap, out var boundSource)
                    ? boundSource
                    : null;
                if (sourceBindings is null)
                    continue;
                foreach (var result in results.Where(item => item.Id != source.Id))
                {
                    if (!variationMaps.TryGetValue(result.Id, out var resultMap) ||
                        !TryBindSemanticFrame(transition.Result, resultMap, out var resultBindings) ||
                        !BindingsAreCompatible(sourceBindings, resultBindings))
                    {
                        continue;
                    }

                    var exists = _db.Set<LegendSemanticTransitionEvidence>().Local.Any(item =>
                        item.TransitionSignature == transitionSignature &&
                        item.SourceCurriculumExampleId == source.Id &&
                        item.ResultCurriculumExampleId == result.Id &&
                        item.SupersededUtc == null) ||
                        (!(knownNewCurriculumExampleIds?.Contains(source.Id) ?? false) ||
                         !(knownNewCurriculumExampleIds?.Contains(result.Id) ?? false)) &&
                        await _db.Set<LegendSemanticTransitionEvidence>().AnyAsync(item =>
                            item.TransitionSignature == transitionSignature &&
                            item.SourceCurriculumExampleId == source.Id &&
                            item.ResultCurriculumExampleId == result.Id &&
                            item.SupersededUtc == null,
                            cancellationToken);
                    if (exists)
                        continue;

                    _db.Set<LegendSemanticTransitionEvidence>().Add(new LegendSemanticTransitionEvidence
                    {
                        Id = Guid.NewGuid(),
                        TransitionSignature = transitionSignature,
                        SourceSemanticFrameSignature = transition.Source.Signature,
                        ResultSemanticFrameSignature = transition.Result.Signature,
                        SourceSemanticFrame = transition.Source.Serialized,
                        ResultSemanticFrame = transition.Result.Serialized,
                        SourceLanguageCode = languageCode,
                        ResultLanguageCode = languageCode,
                        SourceCurriculumExampleId = source.Id,
                        ResultCurriculumExampleId = result.Id,
                        IndependentSourceIdentity = independentSourceIdentity,
                        ContributionState = "Supported",
                        IsHumanVerifiedSupport = isHumanVerifiedSupport,
                        Provenance = provenance,
                        CreatedUtc = now,
                        UpdatedUtc = now
                    });
                }
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Projects one Founder-declared cross-example meaning relation into the
    /// existing semantic-transition evidence authority.  The declaration is
    /// resolved through opaque semantic-example identities and persisted graph
    /// structure only.  A transition identity is derived from the structural
    /// source/result delta and the declared relationship semantic identity;
    /// it cannot depend on text, example IDs, family keys, or insertion order.
    /// </summary>
    internal async Task PersistFounderCrossExampleSemanticRelationAsync(
        LegendConnectCrossExampleSemanticRelationshipSubmission declaration,
        int evaluatorVersion,
        CancellationToken cancellationToken = default)
    {
        var sourceKey = NormalizeDimension(declaration.SourceSemanticExampleKey)
            ?? throw new InvalidOperationException("The Founder cross-example source identity is invalid.");
        var resultKey = NormalizeDimension(declaration.ResultSemanticExampleKey)
            ?? throw new InvalidOperationException("The Founder cross-example result identity is invalid.");
        var relationshipIdentity = NormalizeDimension(declaration.RelationshipSemanticIdentity)
            ?? throw new InvalidOperationException("The Founder cross-example relationship identity is invalid.");
        if (string.Equals(sourceKey, resultKey, StringComparison.Ordinal))
            throw new InvalidOperationException("A Founder cross-example relationship requires distinct semantic examples.");

        var sourceIdentity = FounderSemanticExampleIdentity(sourceKey);
        var resultIdentity = FounderSemanticExampleIdentity(resultKey);
        var examples = await (
            from example in _db.Set<LegendCurriculumExample>()
            join family in _db.Set<LegendCurriculumFamily>() on example.CurriculumFamilyId equals family.Id
            join unit in _db.Set<LegendLanguageTextUnit>() on example.TextUnitId equals unit.Id
            where (example.SemanticExampleIdentity == sourceIdentity ||
                   example.SemanticExampleIdentity == resultIdentity) &&
                example.LanguageCode == "en" && example.DerivedFromCurriculumExampleId == null &&
                example.SupersededUtc == null &&
                example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                family.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                unit.IsTrainingEligible && unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
            select new FounderSemanticExampleProjection(
                example.Id,
                example.CurriculumFamilyId,
                example.SemanticExampleIdentity!,
                example.LanguageCode)
        ).ToListAsync(cancellationToken);
        var source = examples.SingleOrDefault(item => item.SemanticExampleIdentity == sourceIdentity);
        var result = examples.SingleOrDefault(item => item.SemanticExampleIdentity == resultIdentity);
        if (source is null || result is null ||
            !string.Equals(source.LanguageCode, result.LanguageCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A Founder cross-example relationship must resolve to two active Founder-approved meaning graphs in one language.");
        }

        var exampleIds = new[] { source.Id, result.Id };
        var nodes = await _db.Set<LegendLanguageMeaningNodeEvidence>().AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId) && item.SupersededUtc == null &&
                item.LanguageCode == source.LanguageCode &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new FounderGraphNode(
                item.Id,
                item.CurriculumExampleId,
                item.SemanticDimension,
                item.SemanticValue,
                item.SemanticSignature,
                item.ClauseKey))
            .ToListAsync(cancellationToken);
        var nodesByExample = nodes.GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FounderGraphNode>)group.ToArray());
        if (!nodesByExample.TryGetValue(source.Id, out var sourceNodes) || sourceNodes.Count == 0 ||
            !nodesByExample.TryGetValue(result.Id, out var resultNodes) || resultNodes.Count == 0)
        {
            throw new InvalidOperationException(
                "A Founder cross-example relationship requires persisted governed meaning nodes at both semantic endpoints.");
        }

        var nodeIds = nodes.Select(item => item.Id).ToArray();
        var relations = await (
            from evidence in _db.Set<LegendLanguageMeaningRelationEvidence>().AsNoTracking()
            join relation in _db.Set<LegendLanguageMeaningRelation>().AsNoTracking()
                on evidence.MeaningRelationId equals relation.Id
            where exampleIds.Contains(evidence.CurriculumExampleId) && evidence.SupersededUtc == null &&
                relation.SupersededUtc == null && evidence.ContributionState == "Supported" &&
                evidence.IsHumanVerifiedSupport &&
                evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                relation.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                nodeIds.Contains(evidence.SourceMeaningNodeId) && nodeIds.Contains(evidence.TargetMeaningNodeId)
            select new FounderGraphRelation(
                evidence.CurriculumExampleId,
                evidence.SourceMeaningNodeId,
                evidence.TargetMeaningNodeId,
                relation.RelationKind,
                relation.ClauseKey)
        ).ToListAsync(cancellationToken);
        var relationsByExample = relations.GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FounderGraphRelation>)group.ToArray());
        if (!TryBuildStructuralTransitionFrames(
                sourceNodes,
                relationsByExample.GetValueOrDefault(source.Id, []),
                resultNodes,
                relationsByExample.GetValueOrDefault(result.Id, []),
                out var sourceFrame,
                out var resultFrame))
        {
            throw new InvalidOperationException(
                "The Founder cross-example relationship has ambiguous governed graph structure and cannot derive a reusable transition.");
        }

        var relationshipSignature = FrameSignature(
            "founder-cross-example-relationship|v1|" + relationshipIdentity);
        var sourceGraphSignature = FounderMeaningGraphSignature(
            sourceNodes,
            relationsByExample.GetValueOrDefault(source.Id, []));
        var resultGraphSignature = FounderMeaningGraphSignature(
            resultNodes,
            relationsByExample.GetValueOrDefault(result.Id, []));
        var relationIdentity = FrameSignature(string.Join("|",
            "founder-cross-example-evidence|v1",
            source.Id.ToString("N"),
            relationshipSignature,
            result.Id.ToString("N")));
        var independentSourceIdentity = "family-pair:" +
            source.CurriculumFamilyId.ToString("N") + "|" + result.CurriculumFamilyId.ToString("N");
        var now = DateTime.UtcNow;

        var relationEvidence = _db.Set<LegendFounderSemanticExampleRelationEvidence>().Local
            .SingleOrDefault(item => item.RelationIdentity == relationIdentity)
            ?? await _db.Set<LegendFounderSemanticExampleRelationEvidence>()
                .SingleOrDefaultAsync(item => item.RelationIdentity == relationIdentity, cancellationToken);
        if (relationEvidence is null)
        {
            relationEvidence = new LegendFounderSemanticExampleRelationEvidence
            {
                Id = Guid.NewGuid(),
                RelationIdentity = relationIdentity,
                RelationshipSemanticIdentity = relationshipIdentity,
                RelationshipSemanticSignature = relationshipSignature,
                SourceCurriculumFamilyId = source.CurriculumFamilyId,
                SourceCurriculumExampleId = source.Id,
                ResultCurriculumFamilyId = result.CurriculumFamilyId,
                ResultCurriculumExampleId = result.Id,
                SourceMeaningGraphSignature = sourceGraphSignature,
                ResultMeaningGraphSignature = resultGraphSignature,
                LanguageCode = source.LanguageCode,
                IndependentSourceIdentity = independentSourceIdentity,
                ContributionState = "Supported",
                IsHumanVerifiedSupport = true,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                EvaluatorVersion = evaluatorVersion,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            _db.Set<LegendFounderSemanticExampleRelationEvidence>().Add(relationEvidence);
        }
        else if (relationEvidence.SourceCurriculumExampleId != source.Id ||
            relationEvidence.ResultCurriculumExampleId != result.Id ||
            !string.Equals(relationEvidence.RelationshipSemanticSignature, relationshipSignature, StringComparison.Ordinal) ||
            !string.Equals(relationEvidence.SourceMeaningGraphSignature, sourceGraphSignature, StringComparison.Ordinal) ||
            !string.Equals(relationEvidence.ResultMeaningGraphSignature, resultGraphSignature, StringComparison.Ordinal) ||
            !string.Equals(relationEvidence.Provenance, LegendConnectKnowledgeProvenance.FounderApproved, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A canonical Founder cross-example relationship cannot silently change its governed identity.");
        }
        else
        {
            relationEvidence.SupersededUtc = null;
            relationEvidence.EvaluatorVersion = Math.Max(relationEvidence.EvaluatorVersion, evaluatorVersion);
            relationEvidence.UpdatedUtc = now;
        }

        await EnsureFounderDerivedTransitionProjectionAsync(
            relationEvidence,
            source,
            result,
            sourceFrame,
            resultFrame,
            relationshipSignature,
            independentSourceIdentity,
            evaluatorVersion,
            cancellationToken);
    }

    /// <summary>
    /// Replays retained cross-example Founder declarations through the same
    /// projection used by normal manifest processing. Historical replay adds
    /// no relations and never changes canonical example/graph evidence.
    /// </summary>
    private async Task ReconcileFounderCrossExampleSemanticRelationsAsync(
        Guid familyId,
        string languageCode,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(languageCode, "en", StringComparison.Ordinal))
            return;

        var relationRows = await _db.Set<LegendFounderSemanticExampleRelationEvidence>().AsNoTracking()
            .Where(item => item.SupersededUtc == null && item.LanguageCode == languageCode &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                (item.SourceCurriculumFamilyId == familyId || item.ResultCurriculumFamilyId == familyId))
            .OrderBy(item => item.RelationIdentity)
            .Select(item => new
            {
                item.SourceCurriculumExampleId,
                item.ResultCurriculumExampleId,
                item.RelationshipSemanticIdentity
            })
            .ToListAsync(cancellationToken);
        if (relationRows.Count == 0)
            return;

        foreach (var row in relationRows)
        {
            await ReconcileFounderCrossExampleSemanticRelationByEvidenceAsync(
                row.SourceCurriculumExampleId,
                row.ResultCurriculumExampleId,
                row.RelationshipSemanticIdentity,
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                cancellationToken);
        }
    }

    private async Task EnsureFounderDerivedTransitionProjectionAsync(
        LegendFounderSemanticExampleRelationEvidence relationEvidence,
        FounderSemanticExampleProjection source,
        FounderSemanticExampleProjection result,
        NormalizedSemanticFrame sourceFrame,
        NormalizedSemanticFrame resultFrame,
        string relationshipSignature,
        string independentSourceIdentity,
        int evaluatorVersion,
        CancellationToken cancellationToken)
    {
        var transitionSignature = FrameSignature(
            "founder-cross-example-transition|v1|" + relationshipSignature + "\n" +
            sourceFrame.Serialized + "\n→\n" + resultFrame.Serialized);
        var transition = _db.Set<LegendSemanticTransitionEvidence>().Local
            .SingleOrDefault(item => item.TransitionSignature == transitionSignature &&
                item.SourceCurriculumExampleId == source.Id && item.ResultCurriculumExampleId == result.Id)
            ?? await _db.Set<LegendSemanticTransitionEvidence>()
                .SingleOrDefaultAsync(item => item.TransitionSignature == transitionSignature &&
                    item.SourceCurriculumExampleId == source.Id && item.ResultCurriculumExampleId == result.Id,
                    cancellationToken);
        var now = DateTime.UtcNow;
        if (transition is null)
        {
            transition = new LegendSemanticTransitionEvidence
            {
                Id = Guid.NewGuid(),
                TransitionSignature = transitionSignature,
                SourceSemanticFrameSignature = sourceFrame.Signature,
                ResultSemanticFrameSignature = resultFrame.Signature,
                SourceSemanticFrame = sourceFrame.Serialized,
                ResultSemanticFrame = resultFrame.Serialized,
                SourceLanguageCode = source.LanguageCode,
                ResultLanguageCode = result.LanguageCode,
                SourceCurriculumExampleId = source.Id,
                ResultCurriculumExampleId = result.Id,
                FounderSemanticExampleRelationEvidenceId = relationEvidence.Id,
                FounderRelationshipSemanticSignature = relationshipSignature,
                DerivationEvaluatorVersion = evaluatorVersion,
                IndependentSourceIdentity = independentSourceIdentity,
                ContributionState = "Supported",
                IsHumanVerifiedSupport = true,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            _db.Set<LegendSemanticTransitionEvidence>().Add(transition);
        }
        else if (!string.Equals(transition.SourceSemanticFrame, sourceFrame.Serialized, StringComparison.Ordinal) ||
            !string.Equals(transition.ResultSemanticFrame, resultFrame.Serialized, StringComparison.Ordinal) ||
            transition.FounderSemanticExampleRelationEvidenceId != relationEvidence.Id ||
            !string.Equals(transition.FounderRelationshipSemanticSignature, relationshipSignature, StringComparison.Ordinal) ||
            !string.Equals(transition.Provenance, LegendConnectKnowledgeProvenance.FounderApproved, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A canonical semantic transition cannot silently change its derived Founder relationship provenance.");
        }
        else
        {
            transition.SupersededUtc = null;
            transition.DerivationEvaluatorVersion = Math.Max(transition.DerivationEvaluatorVersion ?? 0, evaluatorVersion);
            transition.UpdatedUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await RefreshFounderDerivedTransitionContributionStateAsync(
            relationshipSignature,
            sourceFrame.Signature,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileFounderCrossExampleSemanticRelationByEvidenceAsync(
        Guid sourceExampleId,
        Guid resultExampleId,
        string relationshipSemanticIdentity,
        int evaluatorVersion,
        CancellationToken cancellationToken)
    {
        var relationshipSignature = FrameSignature(
            "founder-cross-example-relationship|v1|" + relationshipSemanticIdentity);
        var relationEvidence = await _db.Set<LegendFounderSemanticExampleRelationEvidence>()
            .SingleOrDefaultAsync(item => item.SourceCurriculumExampleId == sourceExampleId &&
                item.ResultCurriculumExampleId == resultExampleId &&
                item.RelationshipSemanticSignature == relationshipSignature &&
                item.SupersededUtc == null &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved,
                cancellationToken);
        if (relationEvidence is null)
            return;

        var exampleIds = new[] { sourceExampleId, resultExampleId };
        var examples = await (
            from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking() on example.TextUnitId equals unit.Id
            where exampleIds.Contains(example.Id) && example.SupersededUtc == null &&
                example.DerivedFromCurriculumExampleId == null &&
                example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                unit.IsTrainingEligible && unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
            select new FounderSemanticExampleProjection(
                example.Id,
                example.CurriculumFamilyId,
                example.SemanticExampleIdentity ?? string.Empty,
                example.LanguageCode)
        ).ToListAsync(cancellationToken);
        var source = examples.SingleOrDefault(item => item.Id == sourceExampleId);
        var result = examples.SingleOrDefault(item => item.Id == resultExampleId);
        if (source is null || result is null ||
            !string.Equals(source.LanguageCode, result.LanguageCode, StringComparison.Ordinal))
        {
            return;
        }

        var nodes = await _db.Set<LegendLanguageMeaningNodeEvidence>().AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId) && item.SupersededUtc == null &&
                item.LanguageCode == source.LanguageCode &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new FounderGraphNode(
                item.Id,
                item.CurriculumExampleId,
                item.SemanticDimension,
                item.SemanticValue,
                item.SemanticSignature,
                item.ClauseKey))
            .ToListAsync(cancellationToken);
        var nodesByExample = nodes.GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FounderGraphNode>)group.ToArray());
        if (!nodesByExample.TryGetValue(source.Id, out var sourceNodes) || sourceNodes.Count == 0 ||
            !nodesByExample.TryGetValue(result.Id, out var resultNodes) || resultNodes.Count == 0)
        {
            return;
        }

        var nodeIds = nodes.Select(item => item.Id).ToArray();
        var relations = await (
            from evidence in _db.Set<LegendLanguageMeaningRelationEvidence>().AsNoTracking()
            join relation in _db.Set<LegendLanguageMeaningRelation>().AsNoTracking()
                on evidence.MeaningRelationId equals relation.Id
            where exampleIds.Contains(evidence.CurriculumExampleId) && evidence.SupersededUtc == null &&
                relation.SupersededUtc == null && evidence.ContributionState == "Supported" &&
                evidence.IsHumanVerifiedSupport &&
                evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                relation.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                nodeIds.Contains(evidence.SourceMeaningNodeId) && nodeIds.Contains(evidence.TargetMeaningNodeId)
            select new FounderGraphRelation(
                evidence.CurriculumExampleId,
                evidence.SourceMeaningNodeId,
                evidence.TargetMeaningNodeId,
                relation.RelationKind,
                relation.ClauseKey)
        ).ToListAsync(cancellationToken);
        var relationsByExample = relations.GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FounderGraphRelation>)group.ToArray());
        if (!TryBuildStructuralTransitionFrames(
                sourceNodes,
                relationsByExample.GetValueOrDefault(source.Id, []),
                resultNodes,
                relationsByExample.GetValueOrDefault(result.Id, []),
                out var sourceFrame,
                out var resultFrame))
        {
            // Ambiguous structure remains the retained relation's governed
            // state; it never becomes a broad transition on replay.
            return;
        }

        relationEvidence.EvaluatorVersion = Math.Max(relationEvidence.EvaluatorVersion, evaluatorVersion);
        relationEvidence.UpdatedUtc = DateTime.UtcNow;
        await EnsureFounderDerivedTransitionProjectionAsync(
            relationEvidence,
            source,
            result,
            sourceFrame,
            resultFrame,
            relationshipSignature,
            relationEvidence.IndependentSourceIdentity,
            evaluatorVersion,
            cancellationToken);
    }

    private async Task RefreshFounderDerivedTransitionContributionStateAsync(
        string relationshipSignature,
        string sourceFrameSignature,
        CancellationToken cancellationToken)
    {
        var transitions = await _db.Set<LegendSemanticTransitionEvidence>()
            .Where(item => item.SupersededUtc == null &&
                item.FounderRelationshipSemanticSignature == relationshipSignature &&
                item.SourceSemanticFrameSignature == sourceFrameSignature &&
                item.FounderSemanticExampleRelationEvidenceId != null &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .ToListAsync(cancellationToken);
        if (transitions.Count == 0)
            return;

        var desiredState = transitions.Select(item => item.ResultSemanticFrameSignature)
            .Distinct(StringComparer.Ordinal).Skip(1).Any()
            ? "Contradictory"
            : "Supported";
        var relationIds = new HashSet<Guid>(transitions
            .Where(item => item.FounderSemanticExampleRelationEvidenceId.HasValue)
            .Select(item => item.FounderSemanticExampleRelationEvidenceId!.Value));
        foreach (var transition in transitions)
        {
            if (!string.Equals(transition.ContributionState, desiredState, StringComparison.Ordinal))
            {
                transition.ContributionState = desiredState;
                transition.UpdatedUtc = DateTime.UtcNow;
            }
        }

        var relationEvidence = await _db.Set<LegendFounderSemanticExampleRelationEvidence>()
            .Where(item => relationIds.Contains(item.Id) && item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var evidence in relationEvidence)
        {
            if (!string.Equals(evidence.ContributionState, desiredState, StringComparison.Ordinal))
            {
                evidence.ContributionState = desiredState;
                evidence.UpdatedUtc = DateTime.UtcNow;
            }
        }
    }

    private static bool TryBuildStructuralTransitionFrames(
        IReadOnlyList<FounderGraphNode> sourceNodes,
        IReadOnlyList<FounderGraphRelation> sourceRelations,
        IReadOnlyList<FounderGraphNode> resultNodes,
        IReadOnlyList<FounderGraphRelation> resultRelations,
        out NormalizedSemanticFrame sourceFrame,
        out NormalizedSemanticFrame resultFrame)
    {
        sourceFrame = null!;
        resultFrame = null!;
        if (!TryGraphSemanticValues(sourceNodes, sourceRelations, out var sourceValues) ||
            !TryGraphSemanticValues(resultNodes, resultRelations, out var resultValues))
        {
            return false;
        }

        var sourceDimensions = sourceNodes.Select(item => item.SemanticDimension)
            .ToHashSet(StringComparer.Ordinal);
        var resultDimensions = resultNodes.Select(item => item.SemanticDimension)
            .ToHashSet(StringComparer.Ordinal);
        var shared = sourceDimensions.Intersect(resultDimensions, StringComparer.Ordinal)
            .Where(dimension => string.Equals(
                sourceValues[dimension],
                resultValues[dimension],
                StringComparison.Ordinal))
            .OrderBy(item => item, StringComparer.Ordinal)
            .Select((item, index) => new KeyValuePair<string, string>(
                item,
                "$v" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

        var sourceFrameValues = new Dictionary<string, string>(sourceValues, StringComparer.Ordinal);
        var resultFrameValues = new Dictionary<string, string>(resultValues, StringComparer.Ordinal);
        foreach (var sharedValue in shared)
        {
            sourceFrameValues[sharedValue.Key] = sharedValue.Value;
            resultFrameValues[sharedValue.Key] = sharedValue.Value;
        }

        var normalizedSource = NormalizeSemanticFrame(sourceFrameValues);
        var normalizedResult = NormalizeSemanticFrame(resultFrameValues);
        if (normalizedSource is null || normalizedResult is null)
            return false;
        sourceFrame = normalizedSource;
        resultFrame = normalizedResult;
        return true;
    }

    private static bool TryGraphSemanticValues(
        IReadOnlyList<FounderGraphNode> nodes,
        IReadOnlyList<FounderGraphRelation> relations,
        out IReadOnlyDictionary<string, string> values)
    {
        var mappedNodes = nodes.GroupBy(item => item.SemanticDimension, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.SemanticValue).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        if (mappedNodes.Any(item => item.Value.Length != 1))
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            return false;
        }

        var result = mappedNodes.ToDictionary(item => item.Key, item => item.Value[0], StringComparer.Ordinal);
        var nodeById = nodes.ToDictionary(item => item.Id);
        foreach (var relation in relations)
        {
            if (!nodeById.TryGetValue(relation.SourceMeaningNodeId, out var source) ||
                !nodeById.TryGetValue(relation.TargetMeaningNodeId, out var target))
            {
                values = result;
                return false;
            }
            result[StructuralRelationFrameDimension(
                relation.RelationKind,
                source.SemanticDimension,
                target.SemanticDimension,
                relation.ClauseKey)] = "present";
        }
        values = result;
        return result.Count > 0;
    }

    private static string FounderMeaningGraphSignature(
        IReadOnlyList<FounderGraphNode> nodes,
        IReadOnlyList<FounderGraphRelation> relations)
    {
        var nodeById = nodes.ToDictionary(item => item.Id);
        return FrameSignature(string.Join("|",
            nodes.OrderBy(item => item.SemanticSignature, StringComparer.Ordinal)
                .ThenBy(item => item.ClauseKey, StringComparer.Ordinal)
                .Select(item => "node:" + item.SemanticSignature + ":" + (item.ClauseKey ?? string.Empty)),
            string.Join(";", relations.OrderBy(item => item.RelationKind, StringComparer.Ordinal)
                .ThenBy(item => item.SourceMeaningNodeId)
                .ThenBy(item => item.TargetMeaningNodeId)
                .Select(item =>
                {
                    var source = nodeById[item.SourceMeaningNodeId];
                    var target = nodeById[item.TargetMeaningNodeId];
                    return "relation:" + source.SemanticSignature + ":" + item.RelationKind + ":" +
                        target.SemanticSignature + ":" + (item.ClauseKey ?? string.Empty);
                }))));
    }

    private static string FounderSemanticExampleIdentity(string semanticExampleKey) =>
        LegendLanguageIdentity.TextHash("founder-semantic-example|v1|" + semanticExampleKey);

    private static string StructuralRelationFrameDimension(
        string relationKind,
        string sourceDimension,
        string targetDimension,
        string? clauseKey) =>
        "rel_" + FrameSignature(string.Join("|",
            "meaning-graph-structure|v1",
            relationKind,
            sourceDimension,
            targetDimension,
            clauseKey ?? string.Empty))[..32];

    private static bool IsStructuralRelationFrameDimension(string dimension) =>
        dimension.StartsWith("rel_", StringComparison.Ordinal);

    private static string FrameSignature(string canonicalFrame) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalFrame))).ToLowerInvariant();

    private static string? NormalizeFamilyKey(string? value)
    {
        var key = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(key) || key.Length > 160 ||
            key.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_'))
            ? null
            : key;
    }

    private static string? NormalizeDimension(string? value)
    {
        var dimension = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(dimension) || dimension.Length > 80 ||
            dimension.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_'))
            ? null
            : dimension;
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private static LegendConnectCurriculumSubmissionResult Rejected(
        string errorCode,
        string message,
        string? familyKey) => new(false, false, errorCode, message, familyKey, null, 0, 0);

    private sealed record NormalizedCurriculumExample(
        string Text,
        IReadOnlyDictionary<string, string> Variations,
        NormalizedMeaningGraph? MeaningGraph,
        string? SemanticExampleKey);

    private sealed record NormalizedMeaningGraph(
        IReadOnlyList<NormalizedMeaningNode> Nodes,
        IReadOnlyList<NormalizedMeaningRelation> Relations,
        IReadOnlyList<NormalizedDiscourseReference> DiscourseReferences);

    private sealed record NormalizedMeaningNode(
        string NodeKey,
        string SemanticDimension,
        string SemanticValue,
        string SurfaceText,
        string? ClauseKey,
        int SurfaceOccurrence);

    private sealed record NormalizedMeaningRelation(
        string SourceNodeKey,
        string RelationKind,
        string TargetNodeKey,
        string? ClauseKey);

    private sealed record NormalizedDiscourseReference(
        string SelectorNodeKey,
        string EntitySemanticDimension,
        string ResolutionMode,
        int? SelectionRank,
        IReadOnlyList<string> AllowedSourceRoles,
        bool ReplacesActiveBinding);

    /// <summary>
    /// A canonical target example has two incompatible Founder-controlled
    /// values for the same semantic dimension. Historical replay may record
    /// and quarantine this contradiction, but it must never select a value or
    /// manufacture a derived target projection.
    /// </summary>
    private sealed class LegendControlledVariationConflictException : InvalidOperationException
    {
        public LegendControlledVariationConflictException(
            Guid targetExampleId,
            string dimension,
            string existingValue,
            string proposedValue)
            : base("One target example cannot carry conflicting controlled semantic variation values.")
        {
            TargetExampleId = targetExampleId;
            Dimension = dimension;
            ExistingValue = existingValue;
            ProposedValue = proposedValue;
        }

        public Guid TargetExampleId { get; }
        public string Dimension { get; }
        public string ExistingValue { get; }
        public string ProposedValue { get; }
    }
}
