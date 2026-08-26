using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPortal.Models;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// Canonical presentation/evaluation authority for the versioned, three-lens
/// LEGEND intelligence contract. It never counts curriculum rows as a score,
/// never manufactures a self or external score, and never changes curriculum,
/// evaluator, model-promotion, or remediation authority.
/// </summary>
public interface ILegendIntelligenceEvaluationService
{
    Task<LegendIntelligenceEvaluationDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken);
    Task<LegendIntelligenceEvaluationDashboardSnapshot> CreateEvidenceSnapshotAsync(string founderUserId, CancellationToken cancellationToken);
}

public sealed class LegendIntelligenceEvaluationService : ILegendIntelligenceEvaluationService
{
    public const string ContractKey = "LEGEND Intelligence Evaluation Contract V1";
    public const string ContractVersion = "V1";
    private const string ContractIdentity = "legend-intelligence-evaluation-contract-v1";
    private const string CanonicalProjectionAuthority = "legend-canonical-evidence-projection-v1";
    private const string LanguageDomain = "language_linguistic";

    private static readonly IReadOnlyDictionary<string, decimal> ScoreWeights =
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["coverage"] = 0.10m,
            ["quality"] = 0.10m,
            ["diversity"] = 0.10m,
            ["validation_maturity"] = 0.15m,
            ["held_out"] = 0.20m,
            ["transfer"] = 0.20m,
            ["native_execution"] = 0.10m,
            ["calibration"] = 0.05m
        };

    private readonly MasterAppDbContext _db;

    public LegendIntelligenceEvaluationService(MasterAppDbContext db) => _db = db;

    public async Task<LegendIntelligenceEvaluationDashboardSnapshot> GetDashboardAsync(
        CancellationToken cancellationToken)
    {
        var contract = await CurrentContractAsync(cancellationToken);
        if (contract is null)
            return LegendIntelligenceEvaluationDashboardSnapshot.NotEvaluated();

        var snapshot = await _db.LegendIntelligenceEvaluationSnapshots
            .AsNoTracking()
            .Where(item => item.ContractId == contract.Id)
            .OrderByDescending(item => item.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null)
            return LegendIntelligenceEvaluationDashboardSnapshot.NotEvaluated();

        return await BuildDashboardAsync(contract, snapshot, cancellationToken);
    }

    public async Task<LegendIntelligenceEvaluationDashboardSnapshot> CreateEvidenceSnapshotAsync(
        string founderUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(founderUserId))
            throw new ArgumentException("A Founder identity is required.", nameof(founderUserId));

        var contract = await GetOrCreateContractAsync(cancellationToken);
        var productionEligibleEvidence = await RefreshCanonicalSignalsAsync(contract, cancellationToken);
        var signals = await _db.LegendIntelligenceEvaluationSignals
            .Where(item => item.ContractId == contract.Id && item.State == "Current")
            .OrderBy(item => item.DomainKey)
            .ThenBy(item => item.MetricKey)
            .ThenBy(item => item.EvidenceAuthority)
            .ThenBy(item => item.EvidenceReference)
            .ThenBy(item => item.MeasuredUtc)
            .ToListAsync(cancellationToken);
        var evidenceIdentity = BuildEvidenceIdentity(contract.ContractIdentity, signals);

        var snapshot = await _db.LegendIntelligenceEvaluationSnapshots
            .SingleOrDefaultAsync(item => item.ContractId == contract.Id && item.EvidenceSetIdentity == evidenceIdentity, cancellationToken);
        if (snapshot is null)
        {
            var previous = await _db.LegendIntelligenceEvaluationSnapshots
                .Where(item => item.ContractId == contract.Id)
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken);
            snapshot = new LegendIntelligenceEvaluationSnapshot
            {
                ContractId = contract.Id,
                PreviousSnapshotId = previous?.Id,
                EvidenceSetIdentity = evidenceIdentity,
                State = signals.Count == 0 ? "InsufficientEvidence" : "EvidenceOnly",
                CreatedUtc = DateTime.UtcNow
            };
            _db.LegendIntelligenceEvaluationSnapshots.Add(snapshot);

            foreach (var domain in LegendIntelligenceEvaluationDomainCatalog.All)
            {
                var domainSignals = signals.Where(item => item.DomainKey == domain.Key).ToArray();
                var evaluation = EvaluateDomain(domainSignals);
                _db.LegendIntelligenceEvaluationDomainSnapshots.Add(new LegendIntelligenceEvaluationDomainSnapshot
                {
                    SnapshotId = snapshot.Id,
                    DomainKey = domain.Key,
                    EvidenceScore = evaluation.Score,
                    EvidenceVolume = domainSignals.LongLength,
                    ProductionEligibleEvidenceCount = productionEligibleEvidence.GetValueOrDefault(domain.Key),
                    NativeSuccessRate = evaluation.TryMetric("native_execution"),
                    HeldOutResult = evaluation.TryMetric("held_out"),
                    TransferResult = evaluation.TryMetric("transfer"),
                    ContradictionRate = evaluation.TryMetric("contradiction_rate"),
                    EvidenceReferencesJson = JsonSerializer.Serialize(domainSignals
                        .Select(item => new { item.EvidenceAuthority, item.EvidenceReference })
                        .Distinct()
                        .ToArray()),
                    KnownWeaknessesJson = JsonSerializer.Serialize(evaluation.Weaknesses),
                    OpenGapsJson = JsonSerializer.Serialize(evaluation.Gaps)
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        return await BuildDashboardAsync(contract, snapshot, cancellationToken);
    }

    private async Task<LegendIntelligenceEvaluationDashboardSnapshot> BuildDashboardAsync(
        LegendIntelligenceEvaluationContract contract,
        LegendIntelligenceEvaluationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var domains = await _db.LegendIntelligenceEvaluationDomainSnapshots
            .AsNoTracking()
            .Where(item => item.SnapshotId == snapshot.Id)
            .ToListAsync(cancellationToken);
        var previous = snapshot.PreviousSnapshotId is null
            ? Array.Empty<LegendIntelligenceEvaluationDomainSnapshot>()
            : await _db.LegendIntelligenceEvaluationDomainSnapshots
                .AsNoTracking()
                .Where(item => item.SnapshotId == snapshot.PreviousSnapshotId.Value)
                .ToArrayAsync(cancellationToken);
        var previousByDomain = previous.ToDictionary(item => item.DomainKey, StringComparer.Ordinal);

        var displayDomains = LegendIntelligenceEvaluationDomainCatalog.All.Select(definition =>
        {
            var current = domains.SingleOrDefault(item => item.DomainKey == definition.Key);
            previousByDomain.TryGetValue(definition.Key, out var prior);
            return new LegendIntelligenceEvaluationDomainDisplaySnapshot(
                definition.Key,
                definition.Name,
                current?.EvidenceScore,
                current?.LegendSelfAssessment,
                current?.OpenAiExternalAssessment,
                prior?.EvidenceScore,
                current?.EvidenceVolume ?? 0,
                current?.ProductionEligibleEvidenceCount ?? 0,
                current?.NativeSuccessRate,
                current?.HeldOutResult,
                current?.TransferResult,
                current?.ContradictionRate,
                DeserializeStrings(current?.KnownWeaknessesJson),
                DeserializeStrings(current?.OpenGapsJson),
                current is null
                    ? "No evaluation result is retained for this domain."
                    : current.EvidenceScore is null
                        ? "Insufficient cited evidence for a score; the evaluator remains fail-closed."
                        : "Measured from the declared V1 evidence rubric.");
        }).ToArray();

        var scored = displayDomains.Where(item => item.EvidenceScore is not null).ToArray();
        var self = displayDomains.Where(item => item.LegendSelfAssessment is not null).ToArray();
        var external = displayDomains.Where(item => item.OpenAiExternalAssessment is not null).ToArray();
        var evidenceScore = Average(scored.Select(item => item.EvidenceScore));
        var selfScore = Average(self.Select(item => item.LegendSelfAssessment));
        var externalScore = Average(external.Select(item => item.OpenAiExternalAssessment));
        var calibration = selfScore is not null && evidenceScore is not null ? selfScore - evidenceScore : null;
        var growth = Average(displayDomains
            .Where(item => item.EvidenceScore is not null && item.PreviousEvidenceScore is not null)
            .Select(item => item.EvidenceScore - item.PreviousEvidenceScore));

        return new LegendIntelligenceEvaluationDashboardSnapshot(
            ContractKey,
            ContractVersion,
            snapshot.State,
            evidenceScore,
            selfScore,
            externalScore,
            calibration,
            growth,
            snapshot.CreatedUtc,
            displayDomains,
            scored.Length == 0
                ? "No score is shown until every required rubric factor has cited governed evidence. Missing assessment data is not treated as zero intelligence."
                : "Each lens remains separate. OpenAI external assessment must be collected without revealing LEGEND's self-assessment.");
    }

    private async Task<LegendIntelligenceEvaluationContract?> CurrentContractAsync(CancellationToken cancellationToken) =>
        await _db.LegendIntelligenceEvaluationContracts
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ContractIdentity == ContractIdentity && item.State == "Current", cancellationToken);

    private async Task<LegendIntelligenceEvaluationContract> GetOrCreateContractAsync(CancellationToken cancellationToken)
    {
        var current = await _db.LegendIntelligenceEvaluationContracts
            .SingleOrDefaultAsync(item => item.ContractIdentity == ContractIdentity, cancellationToken);
        if (current is not null)
            return current;

        current = new LegendIntelligenceEvaluationContract
        {
            ContractKey = ContractKey,
            Version = ContractVersion,
            ContractIdentity = ContractIdentity,
            State = "Current",
            CreatedUtc = DateTime.UtcNow
        };
        _db.LegendIntelligenceEvaluationContracts.Add(current);
        return current;
    }

    /// <summary>
    /// Projects already-governed evidence into the existing evaluation
    /// contract. It creates no curriculum, model result, or runtime outcome;
    /// absent authorities remain absent metrics and therefore fail closed.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, long>> RefreshCanonicalSignalsAsync(
        LegendIntelligenceEvaluationContract contract,
        CancellationToken cancellationToken)
    {
        var observations = await (
            from evidence in _db.LegendSemanticTransitionEvidence.AsNoTracking()
            join source in _db.LegendCurriculumExamples.AsNoTracking()
                on evidence.SourceCurriculumExampleId equals source.Id
            join result in _db.LegendCurriculumExamples.AsNoTracking()
                on evidence.ResultCurriculumExampleId equals result.Id
            join sourceUnit in _db.LegendLanguageTextUnits.AsNoTracking()
                on source.TextUnitId equals sourceUnit.Id
            join resultUnit in _db.LegendLanguageTextUnits.AsNoTracking()
                on result.TextUnitId equals resultUnit.Id
            join family in _db.LegendCurriculumFamilies.AsNoTracking()
                on source.CurriculumFamilyId equals family.Id
            where evidence.SupersededUtc == null &&
                  evidence.SourceLanguageCode == "en" && evidence.ResultLanguageCode == "en" &&
                  (evidence.ContributionState == "Supported" || evidence.ContributionState == "Contradictory") &&
                  evidence.Provenance == "FounderApproved" &&
                  source.SupersededUtc == null && result.SupersededUtc == null &&
                  source.LanguageCode == "en" && result.LanguageCode == "en" &&
                  source.Provenance == "FounderApproved" && result.Provenance == "FounderApproved" &&
                  sourceUnit.IsTrainingEligible && resultUnit.IsTrainingEligible &&
                  sourceUnit.Provenance == "FounderApproved" && resultUnit.Provenance == "FounderApproved" &&
                  family.Provenance == "FounderApproved"
            select new CanonicalTransitionObservation(
                evidence.Id,
                evidence.TransitionSignature,
                evidence.SourceSemanticFrame,
                evidence.ResultSemanticFrame,
                evidence.IndependentSourceIdentity,
                evidence.ContributionState,
                evidence.IsHumanVerifiedSupport,
                source.CurriculumFamilyId,
                family.SemanticCategory,
                evidence.UpdatedUtc)
        ).ToListAsync(cancellationToken);

        var groups = observations.GroupBy(item => item.TransitionSignature, StringComparer.Ordinal).ToArray();
        var eligible = groups.Where(IsProductionEligible).ToArray();
        var eligibleSignatures = eligible.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var eligibleObservations = observations.Where(item => eligibleSignatures.Contains(item.TransitionSignature)).ToArray();
        var measuredUtc = observations.Count == 0 ? (DateTime?)null : observations.Max(item => item.UpdatedUtc);
        var projected = new List<ProjectedSignal>();

        AddRatio(projected, "coverage",
            eligibleObservations.Select(item => item.FamilyId).Distinct().LongCount(),
            observations.Select(item => item.FamilyId).Distinct().LongCount(), measuredUtc, observations);
        AddRatio(projected, "quality",
            observations.Where(item => item.ContributionState == "Supported" && item.IsHumanVerifiedSupport)
                .Select(item => item.IndependentSourceIdentity).Distinct(StringComparer.Ordinal).LongCount(),
            observations.Select(item => item.IndependentSourceIdentity).Distinct(StringComparer.Ordinal).LongCount(), measuredUtc, observations);

        var categorized = observations.Where(item => !string.IsNullOrWhiteSpace(item.SemanticCategory)).ToArray();
        AddRatio(projected, "diversity",
            eligibleObservations.Where(item => !string.IsNullOrWhiteSpace(item.SemanticCategory))
                .Select(item => item.SemanticCategory!).Distinct(StringComparer.Ordinal).LongCount(),
            categorized.Select(item => item.SemanticCategory!).Distinct(StringComparer.Ordinal).LongCount(), measuredUtc, categorized);
        AddRatio(projected, "validation_maturity", eligible.LongLength, groups.LongLength, measuredUtc, observations);
        AddRatio(projected, "contradiction_rate",
            observations.Where(item => item.ContributionState == "Contradictory")
                .Select(item => item.IndependentSourceIdentity).Distinct(StringComparer.Ordinal).LongCount(),
            observations.Select(item => item.IndependentSourceIdentity).Distinct(StringComparer.Ordinal).LongCount(), measuredUtc, observations);

        var modelRun = await _db.LegendConnectModelTrainingRuns.AsNoTracking()
            .Where(item => item.EvaluationState == "Passed" && item.HeldOutScore != null && item.RegressionScore != null)
            .OrderByDescending(item => item.CompletedUtc).ThenByDescending(item => item.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (modelRun is not null)
        {
            projected.Add(Project("held_out", modelRun.HeldOutScore!.Value * 100m, modelRun.UpdatedUtc,
                $"model-run:{modelRun.Id:N}:held-out:{modelRun.HeldOutScore:0.000000}"));
            projected.Add(Project("transfer", modelRun.RegressionScore!.Value * 100m, modelRun.UpdatedUtc,
                $"model-run:{modelRun.Id:N}:regression:{modelRun.RegressionScore:0.000000}"));
        }

        var demands = await _db.LegendTranslationPairDemands.AsNoTracking().ToListAsync(cancellationToken);
        var requests = demands.Sum(item => item.TranslationRequestCount);
        decimal? nativeExecution = null;
        if (requests > 0)
        {
            var internalServes = demands.Sum(item => item.TranslationMemoryHitCount +
                item.StructuralInternalServeCount + item.ContextualInternalServeCount + item.NeuralModelServeCount);
            nativeExecution = Percent(internalServes, requests);
            var demandMaterial = string.Join("|", demands.OrderBy(item => item.PairKey, StringComparer.Ordinal)
                .Select(item => $"{item.PairKey}:{item.TranslationRequestCount}:{item.TranslationMemoryHitCount}:{item.StructuralInternalServeCount}:{item.ContextualInternalServeCount}:{item.NeuralModelServeCount}:{item.LastRequestedUtc:O}"));
            projected.Add(Project("native_execution", nativeExecution.Value,
                demands.Max(item => item.LastRequestedUtc), "translation-demand:" + Hash(demandMaterial)));
        }

        if (modelRun?.HeldOutScore is not null && nativeExecution is not null)
        {
            var heldOut = Math.Clamp(modelRun.HeldOutScore.Value * 100m, 0m, 100m);
            projected.Add(Project("calibration", 100m - Math.Abs(heldOut - nativeExecution.Value),
                new[] { modelRun.UpdatedUtc, demands.Max(item => item.LastRequestedUtc) }.Max(),
                $"held-out-native-agreement:{modelRun.Id:N}:{Hash(string.Join('|', demands.Select(item => item.Id).OrderBy(item => item)))}"));
        }

        await UpsertProjectedSignalsAsync(contract.Id, projected, cancellationToken);
        return new Dictionary<string, long>(StringComparer.Ordinal) { [LanguageDomain] = eligible.LongLength };
    }

    private async Task UpsertProjectedSignalsAsync(Guid contractId, IReadOnlyList<ProjectedSignal> projected, CancellationToken cancellationToken)
    {
        var existing = await _db.LegendIntelligenceEvaluationSignals
            .Where(item => item.ContractId == contractId && item.EvidenceAuthority == CanonicalProjectionAuthority)
            .ToListAsync(cancellationToken);
        var currentKeys = projected.Select(item => item.Reference).ToHashSet(StringComparer.Ordinal);
        foreach (var signal in existing.Where(item => item.State == "Current" && !currentKeys.Contains(item.EvidenceReference)))
            signal.State = "Superseded";

        foreach (var item in projected)
        {
            var signal = existing.SingleOrDefault(candidate => candidate.EvidenceReference == item.Reference);
            if (signal is not null)
            {
                signal.State = "Current";
                continue;
            }
            _db.LegendIntelligenceEvaluationSignals.Add(new LegendIntelligenceEvaluationSignal
            {
                ContractId = contractId,
                DomainKey = LanguageDomain,
                MetricKey = item.Metric,
                Value = item.Value,
                EvidenceAuthority = CanonicalProjectionAuthority,
                EvidenceReference = item.Reference,
                State = "Current",
                MeasuredUtc = item.MeasuredUtc,
                CreatedUtc = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsProductionEligible(IGrouping<string, CanonicalTransitionObservation> group) =>
        LegendSemanticTransitionProductionEligibility.IsEligible(group.Select(item =>
            new LegendSemanticTransitionEligibilityObservation(
                item.SourceFrame,
                item.ResultFrame,
                item.IndependentSourceIdentity,
                item.ContributionState,
                item.IsHumanVerifiedSupport)));

    private static void AddRatio(List<ProjectedSignal> target, string metric, long numerator, long denominator,
        DateTime? measuredUtc, IReadOnlyCollection<CanonicalTransitionObservation> evidence)
    {
        if (denominator <= 0 || measuredUtc is null)
            return;
        var material = string.Join("|", evidence.OrderBy(item => item.Id).Select(item =>
            $"{item.Id:N}:{item.UpdatedUtc:O}:{item.ContributionState}:{item.IsHumanVerifiedSupport}"));
        target.Add(Project(metric, Percent(numerator, denominator), measuredUtc.Value,
            $"semantic-transition:{metric}:{Hash(material)}"));
    }

    private static ProjectedSignal Project(string metric, decimal value, DateTime measuredUtc, string reference) =>
        new(metric, Math.Clamp(Math.Round(value, 2, MidpointRounding.AwayFromZero), 0m, 100m), measuredUtc, reference);

    private static decimal Percent(long numerator, long denominator) => denominator == 0 ? 0m : (decimal)numerator * 100m / denominator;
    private static string Hash(string material) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();

    private static DomainEvaluation EvaluateDomain(IEnumerable<LegendIntelligenceEvaluationSignal> source)
    {
        var current = source
            .GroupBy(item => item.MetricKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.MeasuredUtc).First().Value,
                StringComparer.Ordinal);
        var gaps = ScoreWeights.Keys.Where(key => !current.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (!current.TryGetValue("contradiction_rate", out var contradictionRate))
            gaps = gaps.Append("contradiction_rate").Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (gaps.Length > 0)
            return new DomainEvaluation(null, current, Array.Empty<string>(), gaps);

        var weighted = ScoreWeights.Sum(item => current[item.Key] * item.Value);
        var score = Math.Clamp(weighted - (contradictionRate * 0.10m), 0m, 100m);
        var weaknesses = current.Where(item => item.Value < 60m)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key} is below the declared proficiency threshold.")
            .ToArray();
        return new DomainEvaluation(score, current, weaknesses, Array.Empty<string>());
    }

    private static string BuildEvidenceIdentity(string contractIdentity, IEnumerable<LegendIntelligenceEvaluationSignal> signals)
    {
        var material = contractIdentity + "\n" + string.Join("\n", signals.Select(item =>
            $"{item.DomainKey}|{item.MetricKey}|{item.Value:0.00}|{item.EvidenceAuthority}|{item.EvidenceReference}|{item.MeasuredUtc:O}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static IReadOnlyList<string> DeserializeStrings(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? Array.Empty<string>() : JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return known.Length == 0 ? null : Math.Round(known.Average(), 1, MidpointRounding.AwayFromZero);
    }

    private sealed record DomainEvaluation(decimal? Score, IReadOnlyDictionary<string, decimal> Metrics, IReadOnlyList<string> Weaknesses, IReadOnlyList<string> Gaps)
    {
        public decimal? TryMetric(string key) => Metrics.TryGetValue(key, out var value) ? value : null;
    }

    private sealed record CanonicalTransitionObservation(Guid Id, string TransitionSignature, string SourceFrame,
        string ResultFrame, string IndependentSourceIdentity, string ContributionState, bool IsHumanVerifiedSupport,
        Guid FamilyId, string? SemanticCategory, DateTime UpdatedUtc);
    private sealed record ProjectedSignal(string Metric, decimal Value, DateTime MeasuredUtc, string Reference);
}
