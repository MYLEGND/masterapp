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
/// </summary>
internal sealed class LegendConnectTrainingDatasetCompiler
{
    private const int HeldOutModulo = 5;

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

        await AddGovernedAlignmentsAsync(
            rows,
            scopeKey,
            cancellationToken);

        await AddGovernedCurriculumPairsAsync(
            rows,
            scopeKey,
            cancellationToken);

        var ordered = rows.Values
            .OrderBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
            .ThenBy(item => item.SourceLanguageCode, StringComparer.Ordinal)
            .ThenBy(item => item.TargetLanguageCode, StringComparer.Ordinal)
            .ThenBy(item => item.SourceTextHash, StringComparer.Ordinal)
            .ThenBy(item => item.TargetTextHash, StringComparer.Ordinal)
            .ToArray();

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
                LegendModelCapabilityKeys.Translation);

            if (IsHeldOut(row.EvidenceIdentity))
                heldOut.Add(example);
            else
                training.Add(example);
        }

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
                    weight));
        }
    }

    private async Task AddGovernedCurriculumPairsAsync(
        IDictionary<string, CandidateRow> rows,
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
                    weight));
        }
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
                candidate.PairKey,
                candidate.SourceTextHash,
                candidate.TargetTextHash));

        if (!rows.TryGetValue(contentIdentity, out var existing) ||
            candidate.Weight > existing.Weight ||
            (candidate.Weight == existing.Weight &&
             string.CompareOrdinal(
                 candidate.EvidenceIdentity,
                 existing.EvidenceIdentity) < 0))
        {
            rows[contentIdentity] = candidate;
        }
    }

    private static string StableHash(string value)
    {
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static bool IsHeldOut(string evidenceIdentity)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(evidenceIdentity));

        return bytes[0] % HeldOutModulo == 0;
    }

    private static string ComputeDatasetIdentity(
        int evaluatorVersion,
        string scopeKey,
        IReadOnlyCollection<LegendConnectTrainingDatasetExample> training,
        IReadOnlyCollection<LegendConnectTrainingDatasetExample> heldOut)
    {
        var canonical = new
        {
            Schema = "legend-training-dataset-v2",
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
            item.Instructions
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
        int Weight);
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
    string OutputContract = "target_language_text_only")
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
