using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
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

        var existingFamily =
            await _db.Set<LegendCurriculumFamily>()
                .SingleOrDefaultAsync(
                    item =>
                        item.FamilyKey == familyKey,
                    cancellationToken);

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

        var family =
            existingFamily ??
            new LegendCurriculumFamily
            {
                Id = Guid.NewGuid(),
                FamilyKey = familyKey,
                SemanticCategory = semanticCategory,
                Provenance =
                    LegendConnectKnowledgeProvenance
                        .SystemValidatedMachine,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

        if (existingFamily is null)
        {
            _db.Set<LegendCurriculumFamily>().Add(family);
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

            var existing =
                await _db.Set<LegendLanguageCompositionalAnchor>()
                    .SingleOrDefaultAsync(
                        item =>
                            item.CurriculumExampleId ==
                                example.Id &&
                            item.AnchorSignature ==
                                signature &&
                            item.SupersededUtc == null,
                        cancellationToken);

            if (existing is not null)
            {
                // Never alter stronger Founder evidence.
                continue;
            }

            _db.Set<LegendLanguageCompositionalAnchor>().Add(
                new LegendLanguageCompositionalAnchor
                {
                    Id = Guid.NewGuid(),
                    LanguageCode = languageCode,
                    PairKey = null,
                    TextUnitId = example.TextUnitId,
                    LexemeId = occurrence?.LexemeId,
                    ComponentStartTokenIndex =
                        match.StartTokenIndex,
                    ComponentLength =
                        match.TokenLength,
                    CurriculumFamilyId = family.Id,
                    CurriculumExampleId = example.Id,
                    Dimension = match.Dimension,
                    Value = match.Value,
                    SemanticSignature =
                        match.SemanticSignature,
                    AnchorSignature = signature,
                    Provenance =
                        LegendConnectKnowledgeProvenance
                            .SystemValidatedMachine,
                    CreatedUtc = DateTime.UtcNow
                });
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

        var semanticCategory = NormalizeOptional(submission.SemanticCategory, 120);
        var existingFamily = await _db.Set<LegendCurriculumFamily>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.FamilyKey == familyKey, cancellationToken);
        if (existingFamily is not null &&
            !string.IsNullOrWhiteSpace(existingFamily.SemanticCategory) &&
            !string.IsNullOrWhiteSpace(semanticCategory) &&
            !string.Equals(existingFamily.SemanticCategory, semanticCategory, StringComparison.OrdinalIgnoreCase))
        {
            return Rejected(
                "curriculum_family_category_conflict",
                $"Family '{familyKey}' is already classified as '{existingFamily.SemanticCategory}'. Use the existing semantic category or a different deliberate family key.",
                familyKey);
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

        LegendCurriculumFamily family;
        try
        {
            family = await _db.Set<LegendCurriculumFamily>()
                .SingleOrDefaultAsync(item => item.FamilyKey == familyKey, cancellationToken)
                ?? new LegendCurriculumFamily
                {
                    Id = Guid.NewGuid(),
                    FamilyKey = familyKey,
                    SemanticCategory = NormalizeOptional(submission.SemanticCategory, 120),
                    Provenance = "FounderApproved",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
            if (_db.Entry(family).State == EntityState.Detached)
                _db.Set<LegendCurriculumFamily>().Add(family);
            else if (!string.IsNullOrWhiteSpace(submission.SemanticCategory) && family.SemanticCategory is null)
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
                    cancellationToken);
                if (!submitted.Succeeded && !submitted.DuplicatePrevented)
                    return Rejected(submitted.ErrorCode ?? "curriculum_source_rejected", submitted.Message ?? "The English curriculum example could not be retained.", familyKey);

                textUnit = submitted.SourceTextUnitId is { } textUnitId
                    ? await _db.Set<LegendLanguageTextUnit>().SingleAsync(item => item.Id == textUnitId, cancellationToken)
                    : await _db.Set<LegendLanguageTextUnit>().SingleAsync(item =>
                        item.LanguageCode == english && item.NormalizedHash == textHash, cancellationToken);
                createdSourceCount++;
            }

            var curriculumExample = await GetOrCreateExampleAsync(
                family,
                textUnit,
                english,
                derivedFromCurriculumExampleId: null,
                cancellationToken);
            await EnsureVariationsAsync(curriculumExample, example.Variations, cancellationToken);
            sourceExamples.Add(curriculumExample);
        }
        await _db.SaveChangesAsync(cancellationToken);

        var structuredSourceUnitIds = sourceExamples.Select(item => item.TextUnitId).Distinct().ToArray();
        var structuredSourceUnits = await _db.Set<LegendLanguageTextUnit>()
            .Where(item => structuredSourceUnitIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var structuredSourceInputs = sourceExamples
            .DistinctBy(item => item.TextUnitId)
            .Select(item => new AtomicInput(
                structuredSourceUnits[item.TextUnitId],
                "StructuredExample",
                null,
                null,
                null))
            .ToList();
        await EnsureLanguageLexicalObservationsAsync(structuredSourceInputs, english, cancellationToken);
        await AttachExplicitFounderSemanticAnchorsAsync(
            family,
            sourceExamples,
            english,
            semanticTransitions,
            semanticSpanGroundings,
            cancellationToken);
        await PersistFounderSemanticTransitionEvidenceAsync(
            family,
            sourceExamples,
            semanticTransitions,
            english,
            cancellationToken);

        // This is the existing expansion authority. It is idempotent by source
        // asset and directional pair, and it carries curriculum lineage only as
        // metadata for the already-approved work.
        foreach (var sourceExample in sourceExamples.DistinctBy(item => item.Id))
        {
            var sourceUnit = await _db.Set<LegendLanguageTextUnit>()
                .SingleAsync(item => item.Id == sourceExample.TextUnitId, cancellationToken);
            await _corpus.EnsureFounderSeedCandidatesAsync(
                sourceUnit,
                family.Id,
                sourceExample.Id,
                cancellationToken);
            await AttachExistingExpansionsAsync(sourceExample, sourceUnit, cancellationToken);
        }

        await AnalyzeFamilyLanguageAsync(family.Id, english, pairKey: null, cancellationToken);
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

    private async Task ReevaluateHistoricalSourceFamilyLanguageAsync(
        Guid familyId,
        string languageCode,
        CancellationToken cancellationToken)
    {
        await ReconcileFounderApprovedSourceEvidenceAsync(familyId, languageCode, cancellationToken);
        await AnalyzeFamilyLanguageAsync(familyId, languageCode, pairKey: null, cancellationToken);
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
        _db.Set<LegendLanguageCompositionalAnchor>().Add(anchor);
        return anchor;
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
    internal async Task<LegendSemanticTransitionInference> TryInferSemanticTransitionAsync(
        string sourceLanguageCode,
        string input,
        IReadOnlyList<LegendConnectConversationContextItem> context,
        CancellationToken cancellationToken = default)
    {
        var sourceLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(
            sourceLanguageCode,
            cancellationToken);
        if (sourceLanguage is null)
            return SemanticTransitionInsufficient("invalid_source_language");

        var understanding = await AnalyzeSemanticTransitionSourceSemanticsAsync(
            sourceLanguage,
            input,
            cancellationToken);
        if (!string.Equals(
                understanding.State,
                LegendShadowSourceUnderstanding.SupportedForShadowEvaluation,
                StringComparison.Ordinal))
        {
            return SemanticTransitionInsufficient(
                understanding.Reasons.FirstOrDefault() ?? "source_semantics_not_governed");
        }

        if (!TryToUnambiguousSemanticValues(understanding.Components, out var inputValues))
            return SemanticTransitionAmbiguous("ambiguous_source_semantic_dimension");

        var observations = await LoadActiveSemanticTransitionObservationsAsync(
            sourceLanguage,
            transitionSignatures: null,
            cancellationToken);
        if (observations.Count == 0)
            return SemanticTransitionInsufficient("semantic_transition_evidence_unknown");

        var directCandidates = BuildProductionSemanticTransitionCandidates(
            observations,
            inputValues,
            allowMissingVariables: false);
        var candidates = directCandidates;

        if (candidates.Count == 0)
        {
            if (HasContradictedSemanticTransition(observations, inputValues))
                return SemanticTransitionContradicted("semantic_transition_contradicted");

            var partialCandidates = BuildProductionSemanticTransitionCandidates(
                observations,
                inputValues,
                allowMissingVariables: true)
                .Where(item => item.MissingVariables.Count > 0 && item.DirectBindingCount > 0)
                .ToList();
            if (partialCandidates.Count == 0)
                return SemanticTransitionInsufficient("semantic_transition_not_supported");

            var contextFrames = await ResolveGroundedContextFramesAsync(
                sourceLanguage,
                context,
                observations,
                cancellationToken);
            candidates = BindCandidatesFromGroundedContext(partialCandidates, contextFrames);
            if (candidates.Count == 0)
                return SemanticTransitionInsufficient("semantic_context_not_governed");
        }

        var resultSignatures = candidates
            .Select(item => item.ResultFrame.Signature)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (resultSignatures.Length != 1)
            return SemanticTransitionAmbiguous("ambiguous_semantic_transition");

        var selected = candidates
            .OrderBy(item => item.TransitionSignature, StringComparer.Ordinal)
            .First();
        var realization = await TryRealizeSemanticTransitionResultAsync(
            selected,
            sourceLanguage,
            understanding.Components,
            cancellationToken);
        if (realization.Reason is not null)
        {
            return realization.IsAmbiguous
                ? SemanticTransitionAmbiguous(realization.Reason)
                : SemanticTransitionInsufficient(realization.Reason);
        }

        return new LegendSemanticTransitionInference(
            LegendSemanticTransitionInference.Supported,
            realization.Text,
            selected.IndependentEvidenceCount + realization.LayoutEvidenceCount,
            ["independently_supported_semantic_transition", "canonical_anchor_realization"]);
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
                evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                source.SupersededUtc == null && result.SupersededUtc == null &&
                source.LanguageCode == sourceLanguage && result.LanguageCode == sourceLanguage &&
                source.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                result.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                sourceUnit.IsTrainingEligible && resultUnit.IsTrainingEligible &&
                sourceUnit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                resultUnit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
            select new SemanticTransitionObservation(
                evidence.TransitionSignature,
                evidence.SourceSemanticFrame,
                evidence.ResultSemanticFrame,
                evidence.IndependentSourceIdentity,
                evidence.ContributionState,
                evidence.IsHumanVerifiedSupport,
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
        BuildProductionSemanticTransitionCandidates(
            IReadOnlyList<SemanticTransitionObservation> observations,
            IReadOnlyDictionary<string, string> inputValues,
            bool allowMissingVariables)
    {
        var candidates = new List<SemanticTransitionCandidate>();
        foreach (var group in observations.GroupBy(item => item.TransitionSignature, StringComparer.Ordinal))
        {
            if (!TryGetProductionSemanticTransitionFrames(
                    group,
                    out var independentEvidenceCount,
                    out var sourceFrame,
                    out var resultFrame))
            {
                continue;
            }

            if (!TryBindInputSemanticFrame(
                    sourceFrame,
                    inputValues,
                    allowMissingVariables,
                    out var bindings,
                    out var missingVariables,
                    out var directBindingCount))
            {
                continue;
            }

            candidates.Add(new SemanticTransitionCandidate(
                group.Key,
                sourceFrame,
                resultFrame,
                bindings,
                missingVariables,
                directBindingCount,
                independentEvidenceCount));
        }
        return candidates;
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
        if (group.Count == 0 || group.Any(item =>
                string.Equals(item.ContributionState, "Contradictory", StringComparison.Ordinal)))
        {
            return false;
        }

        independentEvidenceCount = group
            .Where(item => item.IsHumanVerifiedSupport &&
                string.Equals(item.ContributionState, "Supported", StringComparison.Ordinal))
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (independentEvidenceCount < 3)
            return false;

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
        IReadOnlyDictionary<string, string> inputValues)
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
                    out _,
                    out _,
                    out _))
            {
                return true;
            }
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

            var candidates = BuildProductionSemanticTransitionCandidates(
                observations,
                values,
                allowMissingVariables: false);
            if (candidates.Select(item => item.ResultFrame.Signature).Distinct(StringComparer.Ordinal).Count() != 1)
                continue;
            foreach (var candidate in candidates)
            {
                if (!TryInstantiateFrame(candidate.ResultFrame, candidate.Bindings, out var frameValues))
                    continue;
                frames.Add(new GroundedContextFrame(candidate.ResultFrame.Signature, frameValues));
            }
        }
        return frames;
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

    private async Task<SemanticTransitionRealization> TryRealizeSemanticTransitionResultAsync(
        SemanticTransitionCandidate candidate,
        string languageCode,
        IReadOnlyList<LegendShadowSourceSemanticComponent> sourceComponents,
        CancellationToken cancellationToken)
    {
        if (!TryInstantiateFrame(candidate.ResultFrame, candidate.Bindings, out var resultValues))
            return SemanticTransitionRealization.Insufficient("result_semantic_variable_unbound");

        var activeExamples =
            from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on example.TextUnitId equals unit.Id
            where example.SupersededUtc == null &&
                example.LanguageCode == languageCode &&
                example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                unit.LanguageCode == languageCode && unit.IsTrainingEligible &&
                unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
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
                evidence.IsHumanVerifiedSupport &&
                evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved));
        foreach (var dimension in candidate.ResultFrame.Dimensions)
        {
            var dimensionName = dimension.Key;
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

        var scopedExamples = examples
            .Where(item => variationMaps.TryGetValue(item.Id, out var values) &&
                MatchesInstantiatedSemanticFrame(candidate.ResultFrame, values, candidate.Bindings))
            .ToList();

        // A fully static target frame needs no synthetic composition: the
        // governed endpoint is already one exact canonical Founder response.
        // The result class itself still requires independent realization
        // support from three Founder families. This is intentionally not a
        // prompt/answer lookup: selection is by the reusable semantic frame
        // and one independently supported transition, and the returned text
        // is an existing canonical result endpoint rather than a template.
        // Frames with a bound variable continue below through the stricter
        // structural-layout realization path.
        if (!candidate.ResultFrame.Dimensions.Values.Any(IsSemanticVariable) &&
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
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
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
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var layouts = new List<SemanticRealizationLayout>();
        foreach (var example in examples.Where(item =>
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
            .GroupBy(item => item.Shape, StringComparer.Ordinal)
            .Select(group => new
            {
                Layouts = group.ToList(),
                IndependentFamilies = group.Select(item => item.FamilyId).Distinct().Count()
            })
            .Where(group => group.IndependentFamilies >= 3)
            .ToList();
        if (scopedExamples.Count > 0)
        {
            if (eligibleLayouts.Count == 0)
                return SemanticTransitionRealization.Insufficient("result_realization_layout_insufficient");
            if (eligibleLayouts.Count > 1)
                return SemanticTransitionRealization.Ambiguous("ambiguous_result_realization_layout");

            var learnedLayout = eligibleLayouts[0].Layouts;
            if (!TryResolveScopedSemanticComponents(
                    scopedExamples,
                    anchorsByExample,
                    frameDimensions,
                    out var scopedComponents))
            {
                return SemanticTransitionRealization.Ambiguous("contradictory_result_semantic_components");
            }

            if (!TryRealizeLearnedLayout(learnedLayout, scopedComponents, out var text))
                return SemanticTransitionRealization.Insufficient("result_realization_component_missing");
            return new SemanticTransitionRealization(text, eligibleLayouts[0].IndependentFamilies, null, false);
        }

        // A bound result value can be realized from the governed source only
        // when the result layout itself has three independent Founder families
        // and every non-bound component is invariant across that layout.  This
        // permits a learned compositional response to a new approved source
        // binding without manufacturing a target fact or using a template.
        return TryRealizeBoundSemanticTransitionResult(
            candidate,
            sourceComponents,
            layoutDatabaseExamples,
            variationMaps,
            anchorsByExample);
    }

    private static bool TryRealizeCanonicalStaticResult(
        IReadOnlyList<SemanticResultExample> examples,
        out string text,
        out int independentFamilies)
    {
        text = string.Empty;
        independentFamilies = examples
            .Select(item => item.CurriculumFamilyId)
            .Distinct()
            .Count();
        if (independentFamilies < 3)
            return false;

        // Stable canonical ordering makes a result reproducible across
        // requests and instances without encoding any phrase preference.
        // Every eligible candidate is already a Founder-approved endpoint of
        // the selected semantic transition.
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
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in components.GroupBy(item => item.Dimension, StringComparer.OrdinalIgnoreCase))
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
        out IReadOnlyDictionary<string, string> bindings,
        out IReadOnlyList<SemanticMissingVariable> missingVariables,
        out int directBindingCount)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<SemanticMissingVariable>();
        directBindingCount = 0;
        foreach (var item in frame.Dimensions)
        {
            if (!IsSemanticVariable(item.Value))
            {
                if (!inputValues.TryGetValue(item.Key, out var observed) ||
                    !string.Equals(observed, item.Value, StringComparison.OrdinalIgnoreCase))
                {
                    bindings = resolved;
                    missingVariables = missing;
                    return false;
                }
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
            directBindingCount++;
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

    private static bool MatchesInstantiatedSemanticFrame(
        NormalizedSemanticFrame frame,
        IReadOnlyDictionary<string, string> variations,
        IReadOnlyDictionary<string, string> bindings) =>
        TryInstantiateFrame(frame, bindings, out var values) &&
        values.All(item => variations.TryGetValue(item.Key, out var observed) &&
            string.Equals(item.Value, observed, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesStaticSemanticFrame(
        NormalizedSemanticFrame frame,
        IReadOnlyDictionary<string, string> variations) =>
        frame.Dimensions.All(item =>
            variations.TryGetValue(item.Key, out var observed) &&
            (IsSemanticVariable(item.Value) ||
             string.Equals(item.Value, observed, StringComparison.OrdinalIgnoreCase)));

    private static bool TryBuildSemanticRealizationLayout(
        SemanticResultExample example,
        IReadOnlyList<SemanticAnchor> anchors,
        IReadOnlySet<string> frameDimensions,
        out SemanticRealizationLayout layout)
    {
        var components = BuildSemanticLayoutComponents(example, anchors, frameDimensions);
        if (components.Count < 2 ||
            components.Select(item => item.Dimension).Distinct(StringComparer.OrdinalIgnoreCase).Count() != components.Count ||
            components.Zip(components.Skip(1), (left, right) =>
                    left.StartTokenIndex + left.TokenLength > right.StartTokenIndex)
                .Any(item => item))
        {
            layout = null!;
            return false;
        }

        var firstStart = components[0].StartTokenIndex;
        var shape = string.Join("|", components.Select(item =>
            $"{item.Dimension}:{item.StartTokenIndex - firstStart}:{item.TokenLength}"));
        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            shape,
            components,
            TerminalPunctuation(example.Text));
        return true;
    }

    private static bool TryResolveScopedSemanticComponents(
        IReadOnlyList<SemanticResultExample> examples,
        IReadOnlyDictionary<Guid, List<SemanticAnchor>> anchorsByExample,
        IReadOnlySet<string> frameDimensions,
        out IReadOnlyDictionary<string, SemanticLayoutComponent> components)
    {
        var candidates = new List<SemanticLayoutComponent>();
        foreach (var example in examples)
        {
            if (!anchorsByExample.TryGetValue(example.Id, out var anchors))
                continue;
            candidates.AddRange(anchors
                .GroupBy(item => (item.Dimension, item.StartTokenIndex, item.TokenLength, item.Value))
                .Select(group => group.First())
                .Select(item => new SemanticLayoutComponent(
                    item.Dimension,
                    item.Value,
                    item.StartTokenIndex,
                    item.TokenLength,
                    ExtractAnchorSurface(example.Text, item.StartTokenIndex, item.TokenLength)))
                .Where(item => !string.IsNullOrWhiteSpace(item.SurfaceForm)));
        }

        var resolved = new Dictionary<string, SemanticLayoutComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(item => item.Dimension, StringComparer.OrdinalIgnoreCase))
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

    private static bool TryRealizeLearnedLayout(
        IReadOnlyList<SemanticRealizationLayout> layouts,
        IReadOnlyDictionary<string, SemanticLayoutComponent> scopedComponents,
        out string text)
    {
        text = string.Empty;
        if (layouts.Count == 0)
            return false;

        var output = new List<string>();
        foreach (var position in layouts[0].Components)
        {
            if (scopedComponents.TryGetValue(position.Dimension, out var scoped))
            {
                output.Add(scoped.SurfaceForm);
                continue;
            }

            var fixedAlternatives = layouts
                .SelectMany(item => item.Components)
                .Where(item => string.Equals(item.Dimension, position.Dimension, StringComparison.OrdinalIgnoreCase))
                .GroupBy(item => item.Value + "\u001f" + item.SurfaceForm, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (fixedAlternatives.Length != 1)
                return false;
            output.Add(fixedAlternatives[0].SurfaceForm);
        }

        var punctuation = layouts
            .Select(item => item.TerminalPunctuation)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        text = LegendLanguageIdentity.NormalizeText(string.Join(' ', output));
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (punctuation.Length == 1 && !string.IsNullOrWhiteSpace(punctuation[0]))
            text += punctuation[0];
        return true;
    }

    private static SemanticTransitionRealization TryRealizeBoundSemanticTransitionResult(
        SemanticTransitionCandidate candidate,
        IReadOnlyList<LegendShadowSourceSemanticComponent> sourceComponents,
        IReadOnlyList<SemanticResultExample> layoutExamples,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> variationMaps,
        IReadOnlyDictionary<Guid, List<SemanticAnchor>> anchorsByExample)
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
            .GroupBy(item => item.Shape, StringComparer.Ordinal)
            .Select(group => new
            {
                Layouts = group.ToList(),
                IndependentFamilies = group.Select(item => item.FamilyId).Distinct().Count()
            })
            .Where(group => group.IndependentFamilies >= 3)
            .ToList();
        if (eligibleLayouts.Count == 0)
            return SemanticTransitionRealization.Insufficient("result_bound_layout_insufficient");
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
                return SemanticTransitionRealization.Insufficient("result_semantic_variable_unbound");
            }

            var surfaces = sourceComponents
                .Where(item =>
                    string.Equals(item.Dimension, dynamicDimension.Key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Value, boundValue, StringComparison.OrdinalIgnoreCase))
                .Select(item => LegendLanguageIdentity.NormalizeText(item.SurfaceForm))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (surfaces.Length != 1)
                return SemanticTransitionRealization.Insufficient("result_bound_source_surface_unknown");
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
        return new SemanticTransitionRealization(
            text,
            eligibleLayouts[0].IndependentFamilies,
            null,
            false);
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

        var firstStart = components[0].StartTokenIndex;
        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            string.Join("|", components.Select(item =>
                $"{item.Dimension}:{item.StartTokenIndex - firstStart}:{item.TokenLength}")),
            components,
            TerminalPunctuation(example.Text));
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
        var hashes = allComponents.Select(item => item.NormalizedHash).ToArray();
        var lexemesByHash = await _db.Set<LegendLanguageLexeme>()
            .Where(item => item.LanguageCode == languageCode && hashes.Contains(item.NormalizedHash))
            .ToDictionaryAsync(item => item.NormalizedHash, StringComparer.Ordinal, cancellationToken);
        foreach (var component in allComponents.Where(item => !lexemesByHash.ContainsKey(item.NormalizedHash)))
        {
            var lexeme = new LegendLanguageLexeme
            {
                Id = Guid.NewGuid(),
                LanguageCode = languageCode,
                NormalizedHash = component.NormalizedHash,
                SurfaceForm = component.NormalizedText,
                Provenance = observationProvenance,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.Set<LegendLanguageLexeme>().Add(lexeme);
            lexemesByHash.Add(lexeme.NormalizedHash, lexeme);
        }
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(cancellationToken);

        var textUnitIds = activeInputs.Select(item => item.TextUnit.Id).ToArray();
        var existingOccurrences = await _db.Set<LegendLanguageLexicalOccurrence>()
            .Where(item => textUnitIds.Contains(item.TextUnitId))
            .ToDictionaryAsync(item => (item.TextUnitId, item.TokenIndex), cancellationToken);
        var observationsChanged = false;
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

                _db.Set<LegendLanguageLexicalOccurrence>().Add(new LegendLanguageLexicalOccurrence
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
        if (observationsChanged)
            await _db.SaveChangesAsync(cancellationToken);

        var existingRelationships = await _db.Set<LegendLanguageLexicalRelationship>()
            .Where(item => textUnitIds.Contains(item.TextUnitId))
            .ToDictionaryAsync(item => (item.TextUnitId, item.SourceTokenIndex, item.RelatedTokenIndex), cancellationToken);
        var relationshipsChanged = false;
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

                _db.Set<LegendLanguageLexicalRelationship>().Add(new LegendLanguageLexicalRelationship
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
        if (relationshipsChanged)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task AttachExplicitFounderSemanticAnchorsAsync(
        LegendCurriculumFamily family,
        IReadOnlyList<LegendCurriculumExample> examples,
        string languageCode,
        IReadOnlyList<NormalizedSemanticTransition>? sourceTransitions,
        IReadOnlyList<NormalizedSemanticSpanGrounding>? semanticSpanGroundings,
        CancellationToken cancellationToken)
    {
        var candidates = examples
            .Where(item => string.Equals(item.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase) && item.SupersededUtc is null)
            .DistinctBy(item => item.Id)
            .ToList();
        if (candidates.Count == 0)
            return;

        var founderApprovedUnitIds = await _db.Set<LegendLanguageTextUnit>()
            .Where(item => candidates.Select(example => example.TextUnitId).Contains(item.Id) &&
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
        var exampleVariations = await _db.Set<LegendCurriculumExampleVariation>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var variationsByExample = exampleVariations
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var occurrencesByTextUnit = await (
            from occurrence in _db.Set<LegendLanguageLexicalOccurrence>()
            join lexeme in _db.Set<LegendLanguageLexeme>() on occurrence.LexemeId equals lexeme.Id
            where textUnitIds.Contains(occurrence.TextUnitId) && occurrence.SupersededUtc == null
            select new { occurrence.TextUnitId, occurrence.TokenIndex, occurrence.LexemeId, lexeme.SurfaceForm }
        ).ToListAsync(cancellationToken);
        var existingAnchors = await _db.Set<LegendLanguageCompositionalAnchor>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var existingSignatures = existingAnchors
            .Select(item => item.AnchorSignature)
            .ToHashSet(StringComparer.Ordinal);
        var pending = false;
        foreach (var existing in existingAnchors)
        {
            if (!string.IsNullOrWhiteSpace(existing.SemanticSignature))
                continue;

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
                    _db.Set<LegendLanguageCompositionalAnchor>().Add(new LegendLanguageCompositionalAnchor
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
                    });
                    pending = true;
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
                    _db.Set<LegendLanguageCompositionalAnchor>().Add(new LegendLanguageCompositionalAnchor
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
                    });
                    pending = true;
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
            cancellationToken);

        await AttachFounderCoRealizedSemanticAnchorsAsync(
            family,
            founderExamples,
            languageCode,
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
        CancellationToken cancellationToken)
    {
        if (founderExamples.Count == 0 || sourceTransitions is null || sourceTransitions.Count == 0 ||
            semanticSpanGroundings is null || semanticSpanGroundings.Count == 0)
            return;

        var exampleIds = founderExamples.Select(item => item.Id).Distinct().ToArray();
        var textUnits = await _db.Set<LegendLanguageTextUnit>()
            .AsNoTracking()
            .Where(item => founderExamples.Select(example => example.TextUnitId).Contains(item.Id) &&
                item.LanguageCode == languageCode &&
                item.IsTrainingEligible &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (textUnits.Count == 0)
            return;

        var variations = await _db.Set<LegendCurriculumExampleVariation>()
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var variationsByExample = variations
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var anchors = await _db.Set<LegendLanguageCompositionalAnchor>()
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId) &&
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
            .ToListAsync(cancellationToken);
        var existingSignatures = await _db.Set<LegendLanguageCompositionalAnchor>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .Select(item => item.AnchorSignature)
            .ToHashSetAsync(cancellationToken);

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

                _db.Set<LegendLanguageCompositionalAnchor>().Add(
                    new LegendLanguageCompositionalAnchor
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
                    });
                pending = true;
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
        CancellationToken cancellationToken)
    {
        if (founderExamples.Count < 2)
            return;

        var exampleIds = founderExamples
            .Select(item => item.Id)
            .ToArray();

        var textUnitIds = founderExamples
            .Select(item => item.TextUnitId)
            .Distinct()
            .ToArray();

        var textUnits = await _db.Set<LegendLanguageTextUnit>()
            .AsNoTracking()
            .Where(item =>
                textUnitIds.Contains(item.Id) &&
                item.LanguageCode == languageCode &&
                item.IsTrainingEligible &&
                item.Provenance ==
                    LegendConnectKnowledgeProvenance.FounderApproved)
            .ToDictionaryAsync(
                item => item.Id,
                cancellationToken);

        var variationRows =
            await _db.Set<LegendCurriculumExampleVariation>()
                .AsNoTracking()
                .Where(item =>
                    exampleIds.Contains(item.CurriculumExampleId))
                .ToListAsync(cancellationToken);

        var variationsByExample = variationRows
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.Dimension, StringComparer.Ordinal)
                    .ThenBy(item => item.Value, StringComparer.Ordinal)
                    .Select(item => (
                        item.Dimension,
                        item.Value))
                    .ToArray());

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

                    _db.Set<LegendLanguageCompositionalAnchor>()
                        .Add(
                            new LegendLanguageCompositionalAnchor
                            {
                                Id = Guid.NewGuid(),
                                LanguageCode = languageCode,
                                TextUnitId =
                                    fusedExample.TextUnitId,
                                LexemeId =
                                    fusedOccurrence.LexemeId,
                                ComponentStartTokenIndex =
                                    fusedSpan.StartTokenIndex,
                                ComponentLength =
                                    fusedSpan.TokenLength,
                                CurriculumFamilyId = family.Id,
                                CurriculumExampleId =
                                    fusedExample.Id,
                                Dimension =
                                    expandedAnchor.Dimension,
                                Value =
                                    expandedAnchor.Value,
                                SemanticSignature =
                                    expandedAnchor.SemanticSignature,
                                AnchorSignature = identity,
                                Provenance =
                                    LegendConnectKnowledgeProvenance
                                        .FounderApproved,
                                CreatedUtc = DateTime.UtcNow
                            });

                    pending = true;
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
            foreach (var semanticValue in semanticValues)
            {
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
                _db.Set<LegendLanguageCompositionalAnchor>().Add(projected);
                exampleAnchors.Add(projected);
                pending = true;
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
                var existing = await _db.Set<LegendLanguageContextRelationship>().SingleOrDefaultAsync(item =>
                    item.PairKey == null && item.SourceTextUnitId == source.TextUnit.Id &&
                    item.RelatedTextUnitId == related.TextUnit.Id && item.RelationshipKind == "AdjacentSentence" &&
                    item.ContextSignature == signature, cancellationToken);
                if (existing is not null)
                {
                    if (existing.SupersededUtc is not null)
                    {
                        existing.SupersededUtc = null;
                        existing.UpdatedUtc = DateTime.UtcNow;
                        pending = true;
                    }
                    continue;
                }
                _db.Set<LegendLanguageContextRelationship>().Add(new LegendLanguageContextRelationship
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
                });
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
        CancellationToken cancellationToken)
    {
        var examples = await LoadAnalysisExamplesAsync(familyId, languageCode, pairKey, cancellationToken);
        if (examples.Count < 2)
            return;
        var pairScope = pairKey ?? string.Empty;

        var exampleIds = examples.Select(item => item.Example.Id).ToArray();
        var variations = await _db.Set<LegendCurriculumExampleVariation>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var variationsByExample = variations
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(item => item.Dimension, item => item.Value, StringComparer.Ordinal));
        var anchorsByExample = await LoadExplicitControlledAnchorsByExampleAsync(
            exampleIds,
            languageCode,
            cancellationToken);
        var affectedPatternIds = new HashSet<Guid>();
        var affectedRelationshipIds = new HashSet<Guid>();

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
                foreach (var dimension in leftVariations.Keys.Intersect(rightVariations.Keys, StringComparer.Ordinal))
                {
                    var leftValue = leftVariations[dimension];
                    var rightValue = rightVariations[dimension];
                    if (string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                        continue;

                    // This signature is derived solely from the observed
                    // realization in this language/pair. Founder-controlled
                    // variation values provide the semantic comparison; no
                    // English transformation or target-language rule exists.
                    var comparison = CanonicalComparison(
                        dimension, left.Text, leftValue, left.Example.Id,
                        right.Text, rightValue, right.Example.Id);
                    var propositionSignature = comparison.PropositionSignature;
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
                            Id = Guid.NewGuid(),
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
                            Id = Guid.NewGuid(),
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
                    }
                    affectedPatternIds.Add(pattern.Id);
                    if (priorPatternId is { } prior && prior != pattern.Id)
                        affectedPatternIds.Add(prior);
                    if (priorRelationshipId is { } priorRelationship &&
                        priorRelationship != structuralEvidence.StructuralRelationshipId)
                        affectedRelationshipIds.Add(priorRelationship);
                    if (bothHumanVerified)
                        await EnsureControlledVariationContextAsync(
                            left,
                            right,
                            comparison,
                            pairKey,
                            dimension,
                            propositionSignature,
                            cancellationToken);
                }
            }
        }

        if (affectedPatternIds.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            foreach (var patternId in affectedPatternIds)
                await RefreshPatternMaturityAsync(patternId, cancellationToken);
            foreach (var relationshipId in affectedRelationshipIds)
                await RefreshStructuralRelationshipMaturityAsync(relationshipId, cancellationToken);
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
        var existing = await _db.Set<LegendLanguageContextRelationship>()
            .SingleOrDefaultAsync(item => item.PairKey == pairKey &&
                item.SourceTextUnitId == baseline.SourceTextUnitId &&
                item.RelatedTextUnitId == compared.SourceTextUnitId &&
                item.RelationshipKind == "ControlledVariation" &&
                item.ContextSignature == propositionSignature,
                cancellationToken);
        if (existing is null)
        {
            _db.Set<LegendLanguageContextRelationship>().Add(new LegendLanguageContextRelationship
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
            });
            return;
        }

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
                Id = Guid.NewGuid(),
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

    private async Task RefreshStructuralRelationshipMaturityAsync(
        Guid relationshipId,
        CancellationToken cancellationToken)
    {
        var relationship = await _db.Set<LegendLanguageStructuralRelationship>()
            .SingleAsync(item => item.Id == relationshipId, cancellationToken);
        var evidence = await _db.Set<LegendLanguageStructuralEvidence>()
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
        CancellationToken cancellationToken)
    {
        var pattern = await _db.Set<LegendLanguageStructuralPattern>()
            .SingleAsync(item => item.Id == patternId, cancellationToken);
        var evidence = await _db.Set<LegendLanguageStructuralEvidence>()
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
        int? ParagraphNumber);

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
        Guid SourceExampleId,
        Guid ResultExampleId);

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
        int DirectBindingCount,
        int IndependentEvidenceCount);

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
        string TerminalPunctuation);

    private sealed record SemanticTransitionRealization(
        string? Text,
        int LayoutEvidenceCount,
        string? Reason,
        bool IsAmbiguous)
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
            normalized.Add(new NormalizedCurriculumExample(text, variations));
        }
        return normalized;
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

            var sourceVariables = source.Dimensions
                .Where(item => IsSemanticVariable(item.Value))
                .Select(item => item.Value)
                .ToHashSet(StringComparer.Ordinal);
            if (result.Dimensions.Any(item => IsSemanticVariable(item.Value) && !sourceVariables.Contains(item.Value)))
                return null;

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

    private async Task PersistFounderSemanticTransitionEvidenceAsync(
        LegendCurriculumFamily family,
        IReadOnlyList<LegendCurriculumExample> examples,
        IReadOnlyList<NormalizedSemanticTransition> transitions,
        string languageCode,
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
        var replacedTransitions = await _db.Set<LegendSemanticTransitionEvidence>()
            .Where(item => item.SupersededUtc == null &&
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

        var variations = await _db.Set<LegendCurriculumExampleVariation>()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync(cancellationToken);
        var variationMaps = variations
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
                        item.ResultCurriculumExampleId == result.Id) ||
                        await _db.Set<LegendSemanticTransitionEvidence>().AnyAsync(item =>
                            item.TransitionSignature == transitionSignature &&
                            item.SourceCurriculumExampleId == source.Id &&
                            item.ResultCurriculumExampleId == result.Id,
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
                        IsHumanVerifiedSupport = true,
                        Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                        CreatedUtc = now,
                        UpdatedUtc = now
                    });
                }
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

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
        IReadOnlyDictionary<string, string> Variations);

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
