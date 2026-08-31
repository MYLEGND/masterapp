using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Messaging;

/// <summary>
/// Deterministically projects the existing governed LEGEND evidence graph into
/// training and held-out manifests.
///
/// This compiler owns no language knowledge and persists no second corpus.
/// Historical rows participate because the compiler reads the complete active
/// canonical evidence graph after the existing evaluator replay has converged.
/// Partitioning is by connected governed semantic lineage, never by row or
/// evidence identity, so semantic siblings cannot leak across evaluation.
/// </summary>
internal sealed class LegendConnectTrainingDatasetCompiler
{
    private const int HeldOutDivisor = 5;

    private readonly MasterAppDbContext _db;

    internal LegendConnectTrainingDatasetCompiler(MasterAppDbContext db)
    {
        _db = db;
    }

    internal async Task<LegendConnectTrainingDatasetManifest> CompileAsync(
        string scopeKey = "Global",
        CancellationToken cancellationToken = default)
    {
        var policy = await _db.Set<LegendConnectRuntimePolicy>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ScopeKey == "Global",
                cancellationToken);

        if (policy is null)
            throw new InvalidOperationException(
                "training_dataset_runtime_policy_missing");

        if (policy.CompletedLanguageIntelligenceEvaluatorVersion <
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current ||
            !string.Equals(
                policy.LanguageIntelligenceReevaluationPhase,
                "Complete",
                StringComparison.Ordinal) ||
            (policy.TargetLanguageIntelligenceEvaluatorVersion > 0 &&
             policy.TargetLanguageIntelligenceEvaluatorVersion !=
             policy.CompletedLanguageIntelligenceEvaluatorVersion))
        {
            throw new InvalidOperationException(
                "training_dataset_historical_replay_incomplete");
        }

        var evaluatorVersion =
            policy.CompletedLanguageIntelligenceEvaluatorVersion;

        var rows = new Dictionary<string, CandidateRow>(
            StringComparer.Ordinal);
        var splitGroups = await LoadSplitGroupIndexAsync(
            cancellationToken);

        await AddGovernedAlignmentsAsync(
            rows,
            splitGroups,
            scopeKey,
            cancellationToken);

        await AddGovernedCurriculumPairsAsync(
            rows,
            splitGroups,
            scopeKey,
            cancellationToken);

        await AddGovernedSemanticTransitionsAsync(
            rows,
            splitGroups,
            scopeKey,
            cancellationToken);

        var ordered = AssignSplitGroupIdentities(rows.Values)
            .OrderBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
            .ThenBy(item => item.SourceLanguageCode, StringComparer.Ordinal)
            .ThenBy(item => item.TargetLanguageCode, StringComparer.Ordinal)
            .ThenBy(item => item.SourceTextHash, StringComparer.Ordinal)
            .ThenBy(item => item.TargetTextHash, StringComparer.Ordinal)
            .ToArray();
        var heldOutGroups = SelectHeldOutGroups(ordered);

        var training = new List<LegendConnectTrainingDatasetExample>();
        var heldOut = new List<LegendConnectTrainingDatasetExample>();

        foreach (var row in ordered)
        {
            var example = new LegendConnectTrainingDatasetExample(
                row.EvidenceIdentity,
                row.PairKey,
                row.SourceLanguageCode,
                row.TargetLanguageCode,
                row.SourceText,
                row.TargetText,
                row.Provenance,
                row.Weight,
                row.SourceTextHash,
                row.TargetTextHash,
                row.CapabilityKey,
                row.Instructions,
                row.OutputContract,
                row.SplitGroupIdentity);

            if (heldOutGroups.Contains(row.SplitGroupIdentity))
                heldOut.Add(example);
            else
                training.Add(example);
        }

        EnsureNoSplitGroupLeakage(training, heldOut);

        var datasetIdentity = ComputeDatasetIdentity(
            evaluatorVersion,
            scopeKey,
            training,
            heldOut);

        return new LegendConnectTrainingDatasetManifest(
            datasetIdentity,
            evaluatorVersion,
            scopeKey,
            training,
            heldOut);
    }

    private async Task AddGovernedAlignmentsAsync(
        IDictionary<string, CandidateRow> rows,
        DatasetSplitGroupIndex splitGroups,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        var alignments = await (
            from alignment in _db.Set<LegendTranslationAlignment>()
                .AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where alignment.SupersededUtc == null &&
                  source.IsTrainingEligible &&
                  target.IsTrainingEligible
            select new
            {
                Alignment = alignment,
                Source = source,
                Target = target
            })
            .ToListAsync(cancellationToken);

        if (!string.Equals(scopeKey, "Global", StringComparison.Ordinal))
        {
            alignments = alignments
                .Where(item =>
                    string.Equals(
                        item.Alignment.PairKey,
                        scopeKey,
                        StringComparison.Ordinal))
                .ToList();
        }

        var alignmentIds = alignments
            .Select(item => item.Alignment.Id)
            .ToArray();

        var blocking = alignmentIds.Length == 0
            ? new HashSet<Guid>()
            : (await _db.Set<LegendTranslationQualityEvidence>()
                .AsNoTracking()
                .Where(item =>
                    alignmentIds.Contains(item.ObservedAlignmentId) &&
                    item.SupersededUtc == null &&
                    item.Signal == "Contradictory" &&
                    item.ResolutionState == "Open")
                .Select(item => item.ObservedAlignmentId)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();

        foreach (var item in alignments)
        {
            if (blocking.Contains(item.Alignment.Id))
                continue;

            var provenance = EffectiveAlignmentProvenance(
                item.Alignment,
                item.Source,
                item.Target);

            var weight = WeightFor(
                provenance,
                item.Alignment.HumanVerified);

            if (weight == 0)
                continue;

            var evidenceIdentity = StableHash(
                string.Join('|',
                    "alignment",
                    item.Alignment.Id.ToString("D"),
                    item.Alignment.PairKey,
                    item.Source.NormalizedHash,
                    item.Target.NormalizedHash,
                    provenance));

            AddOrStrengthen(
                rows,
                new CandidateRow(
                    evidenceIdentity,
                    item.Alignment.PairKey,
                    item.Source.LanguageCode,
                    item.Target.LanguageCode,
                    item.Source.Text,
                    item.Target.Text,
                    item.Source.NormalizedHash,
                    item.Target.NormalizedHash,
                    provenance,
                    weight,
                    SplitGroupIdentities: splitGroups.ForAlignment(
                        item.Source,
                        item.Target))));
        }
    }

    private async Task AddGovernedCurriculumPairsAsync(
        IDictionary<string, CandidateRow> rows,
        DatasetSplitGroupIndex splitGroups,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        var examples = await (
            from targetExample in _db.Set<LegendCurriculumExample>()
                .AsNoTracking()
            join targetUnit in _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                on targetExample.TextUnitId equals targetUnit.Id
            join sourceExample in _db.Set<LegendCurriculumExample>()
                .AsNoTracking()
                on targetExample.DerivedFromCurriculumExampleId
                equals sourceExample.Id
            join sourceUnit in _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                on sourceExample.TextUnitId equals sourceUnit.Id
            where targetExample.SupersededUtc == null &&
                  sourceExample.SupersededUtc == null &&
                  sourceUnit.IsTrainingEligible &&
                  targetUnit.IsTrainingEligible
            select new
            {
                TargetExample = targetExample,
                SourceExample = sourceExample,
                SourceUnit = sourceUnit,
                TargetUnit = targetUnit
            })
            .ToListAsync(cancellationToken);

        foreach (var item in examples)
        {
            var pairKey =
                $"{item.SourceUnit.LanguageCode}:{item.TargetUnit.LanguageCode}";

            if (!string.Equals(scopeKey, "Global", StringComparison.Ordinal) &&
                !string.Equals(scopeKey, pairKey, StringComparison.Ordinal))
            {
                continue;
            }

            var provenance = StrongerProvenance(
                item.SourceExample.Provenance,
                item.TargetExample.Provenance,
                item.SourceUnit.Provenance,
                item.TargetUnit.Provenance);

            var weight = WeightFor(
                provenance,
                humanVerified: false);

            if (weight == 0)
                continue;

            var evidenceIdentity = StableHash(
                string.Join('|',
                    "curriculum",
                    item.SourceExample.Id.ToString("D"),
                    item.TargetExample.Id.ToString("D"),
                    pairKey,
                    item.SourceUnit.NormalizedHash,
                    item.TargetUnit.NormalizedHash,
                    provenance));

            AddOrStrengthen(
                rows,
                new CandidateRow(
                    evidenceIdentity,
                    pairKey,
                    item.SourceUnit.LanguageCode,
                    item.TargetUnit.LanguageCode,
                    item.SourceUnit.Text,
                    item.TargetUnit.Text,
                    item.SourceUnit.NormalizedHash,
                    item.TargetUnit.NormalizedHash,
                    provenance,
                    weight,
                    SplitGroupIdentities: splitGroups.ForCurriculumPair(
                        item.SourceExample,
                        item.SourceUnit,
                        item.TargetExample,
                        item.TargetUnit))));
        }
    }

    private async Task AddGovernedSemanticTransitionsAsync(
        IDictionary<string, CandidateRow> rows,
        DatasetSplitGroupIndex splitGroups,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        var includeSemanticTransitions =
            string.Equals(scopeKey, "Global", StringComparison.Ordinal) ||
            string.Equals(
                scopeKey,
                $"capability:{LegendModelCapabilityKeys.SemanticTransition}",
                StringComparison.Ordinal);
        var includeGovernedReasoning =
            string.Equals(scopeKey, "Global", StringComparison.Ordinal) ||
            string.Equals(
                scopeKey,
                $"capability:{LegendModelCapabilityKeys.GovernedReasoning}",
                StringComparison.Ordinal);

        if (!includeSemanticTransitions && !includeGovernedReasoning)
            return;

        var observations = await (
            from transition in _db.Set<LegendSemanticTransitionEvidence>()
                .AsNoTracking()
            join sourceExample in _db.Set<LegendCurriculumExample>()
                .AsNoTracking()
                on transition.SourceCurriculumExampleId equals sourceExample.Id
            join resultExample in _db.Set<LegendCurriculumExample>()
                .AsNoTracking()
                on transition.ResultCurriculumExampleId equals resultExample.Id
            join sourceUnit in _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                on sourceExample.TextUnitId equals sourceUnit.Id
            join resultUnit in _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                on resultExample.TextUnitId equals resultUnit.Id
            where transition.SupersededUtc == null &&
                  sourceExample.SupersededUtc == null &&
                  resultExample.SupersededUtc == null &&
                  sourceUnit.IsTrainingEligible &&
                  resultUnit.IsTrainingEligible &&
                  transition.ContributionState == "Supported"
            select new
            {
                Transition = transition,
                SourceExample = sourceExample,
                ResultExample = resultExample,
                SourceUnit = sourceUnit,
                ResultUnit = resultUnit
            })
            .ToListAsync(cancellationToken);

        var eligibleTransitionIds = observations
            .GroupBy(
                item => item.Transition.TransitionSignature,
                StringComparer.Ordinal)
            .Where(group =>
                LegendSemanticTransitionProductionEligibility.IsEligible(
                    group.Select(item =>
                        new LegendSemanticTransitionEligibilityObservation(
                            item.Transition.SourceSemanticFrame,
                            item.Transition.ResultSemanticFrame,
                            item.Transition.IndependentSourceIdentity,
                            item.Transition.ContributionState,
                            item.Transition.IsHumanVerifiedSupport))))
            .SelectMany(group => group.Select(item => item.Transition.Id))
            .ToHashSet();

        if (includeSemanticTransitions)
        foreach (var item in observations)
        {
            if (!eligibleTransitionIds.Contains(item.Transition.Id))
                continue;

            var provenance = StrongerProvenance(
                item.Transition.Provenance,
                item.SourceExample.Provenance,
                item.ResultExample.Provenance,
                item.SourceUnit.Provenance,
                item.ResultUnit.Provenance);
            var weight = WeightFor(
                provenance,
                item.Transition.IsHumanVerifiedSupport);
            if (weight == 0)
                continue;

            var input = JsonSerializer.Serialize(new
            {
                observed_text = item.SourceUnit.Text,
                source_semantic_frame = item.Transition.SourceSemanticFrame,
                transition_signature = item.Transition.TransitionSignature
            });
            var evidenceIdentity = StableHash(
                string.Join('|',
                    "semantic-transition",
                    item.Transition.Id.ToString("D"),
                    item.Transition.TransitionSignature,
                    item.SourceUnit.NormalizedHash,
                    item.ResultUnit.NormalizedHash,
                    provenance));

            AddOrStrengthen(
                rows,
                new CandidateRow(
                    evidenceIdentity,
                    $"capability:{LegendModelCapabilityKeys.SemanticTransition}",
                    item.Transition.SourceLanguageCode,
                    item.Transition.ResultLanguageCode,
                    input,
                    item.ResultUnit.Text,
                    StableHash(input),
                    item.ResultUnit.NormalizedHash,
                    provenance,
                    weight,
                    LegendModelCapabilityKeys.SemanticTransition,
                    "Apply only the supplied governed semantic transition. Return the resolved governed state only.",
                    "governed_state_only",
                    splitGroups.ForTransition(
                        item.Transition,
                        item.SourceExample,
                        item.SourceUnit,
                        item.ResultExample,
                        item.ResultUnit))));
        }

        if (!includeGovernedReasoning)
            return;

        var eligibleGroups = observations
            .Where(item => eligibleTransitionIds.Contains(item.Transition.Id))
            .GroupBy(
                item => item.Transition.TransitionSignature,
                StringComparer.Ordinal)
            .Select(group => new
            {
                Signature = group.Key,
                SourceFrame = group.First().Transition.SourceSemanticFrame,
                ResultFrame = group.First().Transition.ResultSemanticFrame,
                SourceLanguageCode = group.First().Transition.SourceLanguageCode,
                ResultLanguageCode = group.First().Transition.ResultLanguageCode,
                Items = group
                    .OrderBy(
                        item => item.Transition.IndependentSourceIdentity,
                        StringComparer.Ordinal)
                    .ThenBy(item => item.Transition.Id)
                    .ToArray()
            })
            .OrderBy(item => item.Signature, StringComparer.Ordinal)
            .ToArray();

        foreach (var firstGroup in eligibleGroups)
        foreach (var secondGroup in eligibleGroups)
        {
            if (string.Equals(
                    firstGroup.Signature,
                    secondGroup.Signature,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    firstGroup.ResultFrame,
                    secondGroup.SourceFrame,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    firstGroup.ResultLanguageCode,
                    secondGroup.SourceLanguageCode,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var exampleCount = Math.Min(
                firstGroup.Items.Length,
                secondGroup.Items.Length);
            for (var index = 0; index < exampleCount; index++)
            {
                var first = firstGroup.Items[index];
                var second = secondGroup.Items[index];
                var provenance = StrongerProvenance(
                    first.Transition.Provenance,
                    second.Transition.Provenance,
                    first.SourceExample.Provenance,
                    first.ResultExample.Provenance,
                    second.SourceExample.Provenance,
                    second.ResultExample.Provenance,
                    first.SourceUnit.Provenance,
                    first.ResultUnit.Provenance,
                    second.SourceUnit.Provenance,
                    second.ResultUnit.Provenance);
                var weight = WeightFor(
                    provenance,
                    first.Transition.IsHumanVerifiedSupport &&
                    second.Transition.IsHumanVerifiedSupport);
                if (weight == 0)
                    continue;

                var input = JsonSerializer.Serialize(new
                {
                    observed_text = first.SourceUnit.Text,
                    source_semantic_frame = first.Transition.SourceSemanticFrame,
                    transition_path = new[]
                    {
                        first.Transition.TransitionSignature,
                        second.Transition.TransitionSignature
                    }
                });
                var evidenceIdentity = StableHash(
                    string.Join('|',
                        "governed-reasoning",
                        first.Transition.Id.ToString("D"),
                        second.Transition.Id.ToString("D"),
                        first.SourceUnit.NormalizedHash,
                        second.ResultUnit.NormalizedHash,
                        provenance));

                AddOrStrengthen(
                    rows,
                    new CandidateRow(
                        evidenceIdentity,
                        $"capability:{LegendModelCapabilityKeys.GovernedReasoning}",
                        first.Transition.SourceLanguageCode,
                        second.Transition.ResultLanguageCode,
                        input,
                        second.ResultUnit.Text,
                        StableHash(input),
                        second.ResultUnit.NormalizedHash,
                        provenance,
                        weight,
                        LegendModelCapabilityKeys.GovernedReasoning,
                        "Apply the supplied governed transition path in order. Return only the final governed state.",
                        "governed_final_state_only",
                        splitGroups.ForTransitionPath(
                            first.Transition,
                            first.SourceExample,
                            first.SourceUnit,
                            first.ResultExample,
                            first.ResultUnit,
                            second.Transition,
                            second.SourceExample,
                            second.SourceUnit,
                            second.ResultExample,
                            second.ResultUnit))));
            }
        }
    }

    /// <summary>
    /// Builds only partition metadata from existing canonical identities. The
    /// compiler neither infers semantic relatedness from surface text nor
    /// writes a grouping artifact back into curriculum.
    /// </summary>
    private async Task<DatasetSplitGroupIndex> LoadSplitGroupIndexAsync(
        CancellationToken cancellationToken)
    {
        var index = new DatasetSplitGroupIndex();
        var memberships = await (
            from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
            join family in _db.Set<LegendCurriculumFamily>().AsNoTracking()
                on example.CurriculumFamilyId equals family.Id
            join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on example.TextUnitId equals unit.Id
            where example.SupersededUtc == null
            select new DatasetSplitMembership(
                example.Id,
                unit.Id,
                family.Id,
                family.FamilyKey,
                example.SemanticExampleIdentity,
                unit.LanguageCode,
                unit.NormalizedHash,
                unit.GlobalConceptId))
            .ToListAsync(cancellationToken);

        foreach (var membership in memberships)
            index.AddMembership(membership);

        var anchors = await (
            from anchor in _db.Set<LegendLanguageCompositionalAnchor>()
                .AsNoTracking()
            join example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                on anchor.CurriculumExampleId equals example.Id
            where anchor.SupersededUtc == null &&
                  example.SupersededUtc == null
            select new DatasetSplitAnchor(
                anchor.CurriculumExampleId,
                anchor.TextUnitId,
                anchor.CurriculumFamilyId,
                anchor.SemanticSignature,
                anchor.Dimension))
            .ToListAsync(cancellationToken);
        foreach (var anchor in anchors)
            index.AddAnchor(anchor);

        var variations = await (
            from variation in _db.Set<LegendCurriculumExampleVariation>()
                .AsNoTracking()
            join example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                on variation.CurriculumExampleId equals example.Id
            where example.SupersededUtc == null
            select new DatasetSplitVariation(
                variation.CurriculumExampleId,
                variation.Dimension))
            .ToListAsync(cancellationToken);
        foreach (var variation in variations)
            index.AddVariation(variation);

        var patterns = await _db.Set<LegendLanguageStructuralPattern>()
            .AsNoTracking()
            .Where(item => item.SupersededUtc == null)
            .Select(item => new DatasetSplitStructuralPattern(
                item.Id,
                item.CurriculumFamilyId,
                item.PairKey,
                item.LanguageCode,
                item.VariationDimension,
                item.PropositionSignature))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var relationships = await _db.Set<LegendLanguageStructuralRelationship>()
            .AsNoTracking()
            .Where(item => item.SupersededUtc == null)
            .Select(item => new DatasetSplitStructuralRelationship(
                item.Id,
                item.PairKey,
                item.LanguageCode,
                item.VariationDimension,
                item.RelationshipSignature))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var structuralEvidence = await _db.Set<LegendLanguageStructuralEvidence>()
            .AsNoTracking()
            .Where(item => item.SupersededUtc == null)
            .Select(item => new DatasetSplitStructuralEvidence(
                item.StructuralPatternId,
                item.StructuralRelationshipId,
                item.CurriculumFamilyId,
                item.BaselineCurriculumExampleId,
                item.ComparedCurriculumExampleId,
                item.VariationDimension))
            .ToListAsync(cancellationToken);
        foreach (var evidence in structuralEvidence)
        {
            index.AddStructuralEvidence(
                evidence,
                patterns,
                relationships);
        }

        return index;
    }

    private static string EffectiveAlignmentProvenance(
        LegendTranslationAlignment alignment,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target)
    {
        if (alignment.HumanVerified)
            return "HumanVerified";

        return StrongerProvenance(
            alignment.Provenance,
            source.Provenance,
            target.Provenance);
    }

    private static string StrongerProvenance(
        params string?[] provenances)
    {
        // A directional example is only as trusted as its least-authoritative
        // contributing asset. Founder source material must never launder a
        // machine-generated or provider-derived target into FounderApproved.
        if (provenances.Any(item =>
                string.Equals(
                    item,
                    LegendConnectKnowledgeProvenance.ProviderDerived,
                    StringComparison.Ordinal) ||
                string.Equals(
                    item,
                    LegendConnectKnowledgeProvenance.ConsentedLiveTranslation,
                    StringComparison.Ordinal)))
        {
            return LegendConnectKnowledgeProvenance.ProviderDerived;
        }

        if (provenances.Any(item =>
                string.Equals(
                    item,
                    LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                    StringComparison.Ordinal)))
        {
            return LegendConnectKnowledgeProvenance.SystemValidatedMachine;
        }

        if (provenances.Length > 0 &&
            provenances.All(item =>
                string.Equals(
                    item,
                    LegendConnectKnowledgeProvenance.FounderApproved,
                    StringComparison.Ordinal)))
        {
            return LegendConnectKnowledgeProvenance.FounderApproved;
        }

        return string.Empty;
    }

    private static int WeightFor(
        string provenance,
        bool humanVerified)
    {
        if (humanVerified ||
            string.Equals(
                provenance,
                "HumanVerified",
                StringComparison.Ordinal) ||
            string.Equals(
                provenance,
                LegendConnectKnowledgeProvenance.FounderApproved,
                StringComparison.Ordinal))
        {
            return 4;
        }

        if (string.Equals(
                provenance,
                LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                StringComparison.Ordinal))
        {
            return 3;
        }

        // Provider-derived or consented operational observations are not
        // admitted merely because they exist. They enter only after the
        // existing canonical human-verification authority has approved the
        // alignment, which is handled by the branch above.
        return 0;
    }

    private static void AddOrStrengthen(
        IDictionary<string, CandidateRow> rows,
        CandidateRow candidate)
    {
        var contentIdentity = StableHash(
            string.Join('|',
                candidate.CapabilityKey,
                candidate.PairKey,
                candidate.SourceTextHash,
                candidate.TargetTextHash));

        if (!rows.TryGetValue(contentIdentity, out var existing))
        {
            rows[contentIdentity] = candidate with
            {
                SplitGroupIdentities = NormalizeSplitGroupIdentities(
                    candidate.SplitGroupIdentities)
            };
            return;
        }

        var stronger = candidate.Weight > existing.Weight ||
            (candidate.Weight == existing.Weight &&
             string.CompareOrdinal(
                 candidate.EvidenceIdentity,
                 existing.EvidenceIdentity) < 0)
                ? candidate
                : existing;
        rows[contentIdentity] = stronger with
        {
            // Duplicate surfaces can connect independently governed families
            // or principles. Retaining every grouping identity prevents the
            // de-duplicated row from severing that lineage before splitting.
            SplitGroupIdentities = NormalizeSplitGroupIdentities(
                (existing.SplitGroupIdentities ?? [])
                    .Concat(candidate.SplitGroupIdentities ?? []))
        };
    }

    private static string StableHash(string value)
    {
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static IReadOnlyList<CandidateRow> AssignSplitGroupIdentities(
        IEnumerable<CandidateRow> source)
    {
        var rows = source.ToArray();
        var components = new DatasetSplitDisjointSet(rows.Length);
        var groupOwners = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < rows.Length; index++)
        {
            var identities = NormalizeSplitGroupIdentities(
                rows[index].SplitGroupIdentities);
            if (identities.Count == 0)
            {
                identities =
                [
                    SplitIdentity(
                        "content",
                        rows[index].CapabilityKey,
                        rows[index].PairKey,
                        rows[index].SourceTextHash,
                        rows[index].TargetTextHash)
                ];
            }

            rows[index] = rows[index] with
            {
                SplitGroupIdentities = identities
            };
            foreach (var identity in identities)
            {
                if (groupOwners.TryGetValue(identity, out var owner))
                    components.Union(index, owner);
                else
                    groupOwners[identity] = index;
            }
        }

        foreach (var component in Enumerable.Range(0, rows.Length)
                     .GroupBy(components.Find))
        {
            var componentIdentity = StableHash(
                string.Join(
                    "\n",
                    component
                        .SelectMany(index =>
                            rows[index].SplitGroupIdentities ?? [])
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .Prepend("legend-dataset-split-group-v1")));
            foreach (var index in component)
            {
                rows[index] = rows[index] with
                {
                    SplitGroupIdentity = componentIdentity
                };
            }
        }

        return rows;
    }

    private static HashSet<string> SelectHeldOutGroups(
        IReadOnlyCollection<CandidateRow> rows)
    {
        var groups = rows
            .GroupBy(item => item.SplitGroupIdentity, StringComparer.Ordinal)
            .Select(group => new DatasetSplitGroup(
                group.Key,
                group.Count(),
                StableHash("held-out-order-v1|" + group.Key)))
            .OrderBy(item => item.OrderIdentity, StringComparer.Ordinal)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();
        if (groups.Length <= 1)
            return new HashSet<string>(StringComparer.Ordinal);

        var targetRows = Math.Max(
            1,
            (int)Math.Round(
                rows.Count / (decimal)HeldOutDivisor,
                MidpointRounding.AwayFromZero));
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var selectedRows = 0;

        foreach (var group in groups)
        {
            if (selected.Count >= groups.Length - 1)
                break;

            var currentDistance = Math.Abs(targetRows - selectedRows);
            var proposedDistance = Math.Abs(
                targetRows - (selectedRows + group.RowCount));
            if (proposedDistance >= currentDistance)
                continue;

            selected.Add(group.Identity);
            selectedRows += group.RowCount;
        }

        // Two or more independent groups always preserve at least one bounded
        // held-out group. Choose the smallest deterministic group when every
        // candidate individually overshoots the target.
        if (selected.Count == 0)
        {
            selected.Add(groups
                .OrderBy(item => item.RowCount)
                .ThenBy(item => item.OrderIdentity, StringComparer.Ordinal)
                .ThenBy(item => item.Identity, StringComparer.Ordinal)
                .First()
                .Identity);
        }

        return selected;
    }

    private static void EnsureNoSplitGroupLeakage(
        IReadOnlyCollection<LegendConnectTrainingDatasetExample> training,
        IReadOnlyCollection<LegendConnectTrainingDatasetExample> heldOut)
    {
        var trainingGroups = training
            .Select(item => item.SplitGroupIdentity)
            .ToHashSet(StringComparer.Ordinal);
        if (heldOut.Any(item => trainingGroups.Contains(
                item.SplitGroupIdentity)))
        {
            throw new InvalidOperationException(
                "training_dataset_split_group_leakage");
        }
    }

    private static IReadOnlyList<string> NormalizeSplitGroupIdentities(
        IEnumerable<string>? identities) =>
        (identities ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private static string SplitIdentity(
        string kind,
        params string?[] parts) =>
        kind + ":" + StableHash(string.Join(
            "\n",
            parts.Select(item => item?.Trim() ?? string.Empty)));

    private static string ComputeDatasetIdentity(
        int evaluatorVersion,
        string scopeKey,
        IReadOnlyCollection<LegendConnectTrainingDatasetExample> training,
        IReadOnlyCollection<LegendConnectTrainingDatasetExample> heldOut)
    {
        var canonical = new
        {
            Schema = "legend-training-dataset-v4",
            EvaluatorVersion = evaluatorVersion,
            ScopeKey = scopeKey,
            Training = training
                .OrderBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
                .Select(CanonicalIdentityRow)
                .ToArray(),
            HeldOut = heldOut
                .OrderBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
                .Select(CanonicalIdentityRow)
                .ToArray()
        };

        var json = JsonSerializer.Serialize(canonical);

        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static object CanonicalIdentityRow(
        LegendConnectTrainingDatasetExample item) =>
        new
        {
            item.EvidenceIdentity,
            item.PairKey,
            item.SourceLanguageCode,
            item.TargetLanguageCode,
            item.SourceTextHash,
            item.TargetTextHash,
            item.Provenance,
            item.Weight,
            item.CapabilityKey,
            item.Instructions,
            item.OutputContract,
            item.SplitGroupIdentity
        };

    private sealed record CandidateRow(
        string EvidenceIdentity,
        string PairKey,
        string SourceLanguageCode,
        string TargetLanguageCode,
        string SourceText,
        string TargetText,
        string SourceTextHash,
        string TargetTextHash,
        string Provenance,
        int Weight,
        string CapabilityKey = LegendModelCapabilityKeys.Translation,
        string? Instructions = null,
        string OutputContract = "target_language_text_only",
        IReadOnlyList<string>? SplitGroupIdentities = null)
    {
        internal string SplitGroupIdentity { get; init; } = string.Empty;
    }

    private sealed class DatasetSplitGroupIndex
    {
        private readonly Dictionary<Guid, HashSet<string>> _exampleGroups = [];
        private readonly Dictionary<Guid, HashSet<string>> _textGroups = [];
        private readonly Dictionary<Guid, Guid> _exampleTextUnits = [];
        private readonly Dictionary<Guid, string> _exampleFamilyKeys = [];
        private readonly Dictionary<Guid, string> _familyKeys = [];

        internal void AddMembership(DatasetSplitMembership membership)
        {
            _familyKeys[membership.CurriculumFamilyId] = membership.FamilyKey;
            _exampleFamilyKeys[membership.CurriculumExampleId] = membership.FamilyKey;
            _exampleTextUnits[membership.CurriculumExampleId] = membership.TextUnitId;

            AddToExampleAndText(
                membership.CurriculumExampleId,
                membership.TextUnitId,
                SplitIdentity("family", membership.FamilyKey));
            AddToExampleAndText(
                membership.CurriculumExampleId,
                membership.TextUnitId,
                SplitIdentity(
                    "canonical-text",
                    membership.LanguageCode,
                    membership.NormalizedHash));

            if (membership.GlobalConceptId is Guid globalConceptId)
            {
                AddToExampleAndText(
                    membership.CurriculumExampleId,
                    membership.TextUnitId,
                    SplitIdentity(
                        "semantic-principle-global-concept",
                        globalConceptId.ToString("D")));
            }

            if (!string.IsNullOrWhiteSpace(
                    membership.SemanticExampleIdentity))
            {
                AddToExampleAndText(
                    membership.CurriculumExampleId,
                    membership.TextUnitId,
                    SplitIdentity(
                        "semantic-principle-example",
                        membership.SemanticExampleIdentity));
            }
        }

        internal void AddAnchor(DatasetSplitAnchor anchor)
        {
            if (!string.IsNullOrWhiteSpace(anchor.SemanticSignature))
            {
                AddToExampleAndText(
                    anchor.CurriculumExampleId,
                    anchor.TextUnitId,
                    SplitIdentity(
                        "semantic-principle-anchor",
                        anchor.SemanticSignature));
            }

            if (!string.IsNullOrWhiteSpace(anchor.Dimension))
            {
                AddToExampleAndText(
                    anchor.CurriculumExampleId,
                    anchor.TextUnitId,
                    SplitIdentity(
                        "controlled-variation",
                        FamilyKey(anchor.CurriculumFamilyId),
                        anchor.Dimension));
            }
        }

        internal void AddVariation(DatasetSplitVariation variation)
        {
            if (string.IsNullOrWhiteSpace(variation.Dimension) ||
                !_exampleTextUnits.TryGetValue(
                    variation.CurriculumExampleId,
                    out var textUnitId) ||
                !_exampleFamilyKeys.TryGetValue(
                    variation.CurriculumExampleId,
                    out var familyKey))
            {
                return;
            }

            AddToExampleAndText(
                variation.CurriculumExampleId,
                textUnitId,
                SplitIdentity(
                    "controlled-variation",
                    familyKey,
                    variation.Dimension));
        }

        internal void AddStructuralEvidence(
            DatasetSplitStructuralEvidence evidence,
            IReadOnlyDictionary<Guid, DatasetSplitStructuralPattern> patterns,
            IReadOnlyDictionary<Guid, DatasetSplitStructuralRelationship> relationships)
        {
            string? controlledGroup = null;
            if (evidence.StructuralRelationshipId is Guid relationshipId &&
                relationships.TryGetValue(relationshipId, out var relationship))
            {
                controlledGroup = SplitIdentity(
                    "controlled-variation-relationship",
                    relationship.PairKey,
                    relationship.LanguageCode,
                    relationship.VariationDimension,
                    relationship.RelationshipSignature);
            }
            else if (patterns.TryGetValue(
                         evidence.StructuralPatternId,
                         out var pattern))
            {
                controlledGroup = SplitIdentity(
                    "controlled-variation-pattern",
                    FamilyKey(pattern.CurriculumFamilyId),
                    pattern.PairKey,
                    pattern.LanguageCode,
                    pattern.VariationDimension,
                    pattern.PropositionSignature);
            }

            if (controlledGroup is null)
                return;

            AddToExample(
                evidence.BaselineCurriculumExampleId,
                controlledGroup);
            AddToExample(
                evidence.ComparedCurriculumExampleId,
                controlledGroup);
            var dimensionGroup = SplitIdentity(
                "controlled-variation",
                FamilyKey(evidence.CurriculumFamilyId),
                evidence.VariationDimension);
            AddToExample(
                evidence.BaselineCurriculumExampleId,
                dimensionGroup);
            AddToExample(
                evidence.ComparedCurriculumExampleId,
                dimensionGroup);
        }

        internal IReadOnlyList<string> ForAlignment(
            LegendLanguageTextUnit source,
            LegendLanguageTextUnit target) =>
            Merge(
                ForUnit(source),
                ForUnit(target));

        internal IReadOnlyList<string> ForCurriculumPair(
            LegendCurriculumExample sourceExample,
            LegendLanguageTextUnit sourceUnit,
            LegendCurriculumExample targetExample,
            LegendLanguageTextUnit targetUnit) =>
            Merge(
                ForExample(sourceExample, sourceUnit),
                ForExample(targetExample, targetUnit));

        internal IReadOnlyList<string> ForTransition(
            LegendSemanticTransitionEvidence transition,
            LegendCurriculumExample sourceExample,
            LegendLanguageTextUnit sourceUnit,
            LegendCurriculumExample resultExample,
            LegendLanguageTextUnit resultUnit)
        {
            var lineage = new List<string>();
            if (!string.IsNullOrWhiteSpace(transition.TransitionSignature))
            {
                lineage.Add(SplitIdentity(
                    "transition-lineage",
                    transition.TransitionSignature));
            }
            if (!string.IsNullOrWhiteSpace(
                    transition.SourceSemanticFrameSignature))
            {
                lineage.Add(SplitIdentity(
                    "transition-frame",
                    transition.SourceSemanticFrameSignature));
            }
            if (!string.IsNullOrWhiteSpace(
                    transition.ResultSemanticFrameSignature))
            {
                lineage.Add(SplitIdentity(
                    "transition-frame",
                    transition.ResultSemanticFrameSignature));
            }
            if (!string.IsNullOrWhiteSpace(
                    transition.FounderRelationshipSemanticSignature))
            {
                lineage.Add(SplitIdentity(
                    "transition-relationship",
                    transition.FounderRelationshipSemanticSignature));
            }

            return Merge(
                ForExample(sourceExample, sourceUnit),
                ForExample(resultExample, resultUnit),
                lineage);
        }

        internal IReadOnlyList<string> ForTransitionPath(
            LegendSemanticTransitionEvidence firstTransition,
            LegendCurriculumExample firstSourceExample,
            LegendLanguageTextUnit firstSourceUnit,
            LegendCurriculumExample firstResultExample,
            LegendLanguageTextUnit firstResultUnit,
            LegendSemanticTransitionEvidence secondTransition,
            LegendCurriculumExample secondSourceExample,
            LegendLanguageTextUnit secondSourceUnit,
            LegendCurriculumExample secondResultExample,
            LegendLanguageTextUnit secondResultUnit) =>
            Merge(
                ForTransition(
                    firstTransition,
                    firstSourceExample,
                    firstSourceUnit,
                    firstResultExample,
                    firstResultUnit),
                ForTransition(
                    secondTransition,
                    secondSourceExample,
                    secondSourceUnit,
                    secondResultExample,
                    secondResultUnit));

        private IReadOnlyList<string> ForExample(
            LegendCurriculumExample example,
            LegendLanguageTextUnit unit) =>
            Merge(
                ForUnit(unit),
                _exampleGroups.TryGetValue(example.Id, out var groups)
                    ? groups
                    : []);

        private IReadOnlyList<string> ForUnit(
            LegendLanguageTextUnit unit)
        {
            var identities = new List<string>
            {
                SplitIdentity(
                    "canonical-text",
                    unit.LanguageCode,
                    unit.NormalizedHash)
            };
            if (unit.GlobalConceptId is Guid globalConceptId)
            {
                identities.Add(SplitIdentity(
                    "semantic-principle-global-concept",
                    globalConceptId.ToString("D")));
            }
            if (_textGroups.TryGetValue(unit.Id, out var groups))
                identities.AddRange(groups);
            return NormalizeSplitGroupIdentities(identities);
        }

        private void AddToExampleAndText(
            Guid exampleId,
            Guid textUnitId,
            string identity)
        {
            Add(_exampleGroups, exampleId, identity);
            Add(_textGroups, textUnitId, identity);
        }

        private void AddToExample(Guid exampleId, string identity)
        {
            Add(_exampleGroups, exampleId, identity);
            if (_exampleTextUnits.TryGetValue(exampleId, out var textUnitId))
                Add(_textGroups, textUnitId, identity);
        }

        private string FamilyKey(Guid familyId) =>
            _familyKeys.TryGetValue(familyId, out var familyKey)
                ? familyKey
                : familyId.ToString("D");

        private static void Add(
            IDictionary<Guid, HashSet<string>> values,
            Guid key,
            string identity)
        {
            if (!values.TryGetValue(key, out var groups))
            {
                groups = new HashSet<string>(StringComparer.Ordinal);
                values[key] = groups;
            }
            groups.Add(identity);
        }

        private static IReadOnlyList<string> Merge(
            params IEnumerable<string>[] groups) =>
            NormalizeSplitGroupIdentities(groups.SelectMany(item => item));
    }

    private sealed class DatasetSplitDisjointSet
    {
        private readonly int[] _parents;

        internal DatasetSplitDisjointSet(int count)
        {
            _parents = Enumerable.Range(0, count).ToArray();
        }

        internal int Find(int value)
        {
            var root = value;
            while (_parents[root] != root)
                root = _parents[root];
            while (_parents[value] != value)
            {
                var parent = _parents[value];
                _parents[value] = root;
                value = parent;
            }
            return root;
        }

        internal void Union(int first, int second)
        {
            var firstRoot = Find(first);
            var secondRoot = Find(second);
            if (firstRoot == secondRoot)
                return;
            _parents[Math.Max(firstRoot, secondRoot)] =
                Math.Min(firstRoot, secondRoot);
        }
    }

    private sealed record DatasetSplitGroup(
        string Identity,
        int RowCount,
        string OrderIdentity);

    private sealed record DatasetSplitMembership(
        Guid CurriculumExampleId,
        Guid TextUnitId,
        Guid CurriculumFamilyId,
        string FamilyKey,
        string? SemanticExampleIdentity,
        string LanguageCode,
        string NormalizedHash,
        Guid? GlobalConceptId);

    private sealed record DatasetSplitAnchor(
        Guid CurriculumExampleId,
        Guid TextUnitId,
        Guid CurriculumFamilyId,
        string? SemanticSignature,
        string Dimension);

    private sealed record DatasetSplitVariation(
        Guid CurriculumExampleId,
        string Dimension);

    private sealed record DatasetSplitStructuralPattern(
        Guid Id,
        Guid CurriculumFamilyId,
        string PairKey,
        string LanguageCode,
        string VariationDimension,
        string PropositionSignature);

    private sealed record DatasetSplitStructuralRelationship(
        Guid Id,
        string PairKey,
        string LanguageCode,
        string VariationDimension,
        string RelationshipSignature);

    private sealed record DatasetSplitStructuralEvidence(
        Guid StructuralPatternId,
        Guid? StructuralRelationshipId,
        Guid CurriculumFamilyId,
        Guid BaselineCurriculumExampleId,
        Guid ComparedCurriculumExampleId,
        string VariationDimension);
}

internal sealed record LegendConnectTrainingDatasetManifest(
    string DatasetIdentity,
    int EvaluatorVersion,
    string ScopeKey,
    IReadOnlyList<LegendConnectTrainingDatasetExample> Training,
    IReadOnlyList<LegendConnectTrainingDatasetExample> HeldOut)
{
    internal int TrainingExampleCount => Training.Count;
    internal int ValidationExampleCount => HeldOut.Count;
}

internal sealed record LegendConnectTrainingDatasetExample(
    string EvidenceIdentity,
    string PairKey,
    string SourceLanguageCode,
    string TargetLanguageCode,
    string SourceText,
    string TargetText,
    string Provenance,
    int Weight,
    string SourceTextHash,
    string TargetTextHash,
    string CapabilityKey = LegendModelCapabilityKeys.Translation,
    string? Instructions = null,
    string OutputContract = "target_language_text_only",
    string SplitGroupIdentity = "")
{
    internal LegendModelTaskRequest ToTaskRequest() =>
        string.Equals(
            CapabilityKey,
            LegendModelCapabilityKeys.Translation,
            StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(Instructions)
            ? LegendModelTaskRequest.Translation(
                SourceLanguageCode,
                TargetLanguageCode,
                SourceText)
            : new LegendModelTaskRequest(
                CapabilityKey,
                Instructions ?? string.Empty,
                SourceText,
                OutputContract,
                SourceLanguageCode,
                TargetLanguageCode);
}
