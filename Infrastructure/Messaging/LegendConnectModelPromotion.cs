using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Messaging;

/// <summary>
/// Canonical Phase-9 promotion authority.
///
/// This service is the only Phase-9 code allowed to mutate
/// LegendLanguagePair.ActiveModelVersion.
///
/// It owns no translation routing. Phase 10 will decide when and how an active
/// model participates in inference.
/// </summary>
internal sealed class LegendConnectModelPromotionService
{
    private const string Prefix =
        "LegendConnect:ModelPromotion:";

    private const decimal DefaultMinimumHeldOutScore =
        0.95m;

    private const decimal DefaultMinimumRegressionScore =
        1m;

    private readonly MasterAppDbContext _db;
    private readonly LegendConnectTrainingDatasetCompiler _compiler;
    private readonly IConfiguration _configuration;

    internal LegendConnectModelPromotionService(
        MasterAppDbContext db,
        LegendConnectTrainingDatasetCompiler compiler,
        IConfiguration configuration)
    {
        _db = db;
        _compiler = compiler;
        _configuration = configuration;
    }

    internal async Task ProcessOneAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Enabled())
            return;

        var run =
            await _db
                .Set<LegendConnectModelTrainingRun>()
                .Where(item =>
                    item.State == "TrainingCompleted" &&
                    item.EvaluationState == "Passed" &&
                    item.PromotionState == "NotEvaluated" &&
                    item.ChallengerModelVersion != null &&
                    item.ChallengerModelVersion != "" &&
                    item.FailureDetail != null &&
                    item.FailureDetail.Contains(
                        "runtime_mode=LockedHeldOutEvaluation") &&
                    item.FailureDetail.Contains(
                        "response_authority=LegendConnectActiveModelInference"))
                .OrderBy(item => item.Generation)
                .FirstOrDefaultAsync(
                    cancellationToken);

        if (run is null)
            return;

        await PromoteAsync(
            run.Id,
            cancellationToken);
    }

    internal async Task<bool> PromoteAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var run =
            await _db
                .Set<LegendConnectModelTrainingRun>()
                .SingleOrDefaultAsync(
                    item => item.Id == runId,
                    cancellationToken);

        if (run is null)
            return false;

        if (run.State == "TrainingCompleted" &&
            run.EvaluationState == "Passed" &&
            run.PromotionState == "NotEvaluated" &&
            !LegendConnectModelRuntimeProofSummary.IsValid(
                run.FailureDetail))
        {
            await FailPromotionAsync(
                run,
                "model_promotion_runtime_proof_missing",
                cancellationToken);
            return false;
        }

        if (!CanEnterPromotion(run))
            return false;

        LegendConnectTrainingDatasetManifest manifest;

        try
        {
            manifest =
                await _compiler.CompileAsync(
                    run.ScopeKey,
                    cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await FailPromotionAsync(
                run,
                exception.Message,
                cancellationToken);
            return false;
        }

        if (!string.Equals(
                manifest.DatasetIdentity,
                run.DatasetIdentity,
                StringComparison.Ordinal) ||
            manifest.EvaluatorVersion !=
                run.DatasetEvaluatorVersion)
        {
            await FailPromotionAsync(
                run,
                "model_promotion_dataset_identity_changed",
                cancellationToken);
            return false;
        }

        if (!MeetsEvaluationThresholds(
                run))
        {
            await FailPromotionAsync(
                run,
                "model_promotion_evaluation_threshold_not_met",
                cancellationToken);
            return false;
        }

        var pairKeys =
            manifest.Training
                .Concat(
                    manifest.HeldOut)
                .Select(item =>
                    item.PairKey)
                .Where(item =>
                    !string.IsNullOrWhiteSpace(
                        item))
                .Distinct(
                    StringComparer.Ordinal)
                .OrderBy(item =>
                    item,
                    StringComparer.Ordinal)
                .ToArray();

        if (pairKeys.Length == 0)
        {
            await FailPromotionAsync(
                run,
                "model_promotion_no_directional_pairs",
                cancellationToken);
            return false;
        }

        var pairs =
            await _db
                .Set<LegendLanguagePair>()
                .Where(item =>
                    pairKeys.Contains(
                        item.PairKey))
                .OrderBy(item =>
                    item.PairKey)
                .ToListAsync(
                    cancellationToken);

        if (pairs.Count !=
                pairKeys.Length ||
            pairs.Any(item =>
                !item.IsEnabled))
        {
            await FailPromotionAsync(
                run,
                "model_promotion_pair_unavailable",
                cancellationToken);
            return false;
        }

        if (await HasBlockingContradictionAsync(
                pairKeys,
                cancellationToken))
        {
            await FailPromotionAsync(
                run,
                "model_promotion_blocking_contradiction",
                cancellationToken);
            return false;
        }

        var now =
            DateTime.UtcNow;

        if (!await LegendConnectModelLifecycleLease.TryClaimAsync(
                _db,
                run.Id,
                now,
                item =>
                    item.State == "TrainingCompleted" &&
                    item.EvaluationState == "Passed" &&
                    item.PromotionState == "NotEvaluated",
                cancellationToken))
        {
            return false;
        }

        run =
            await _db
                .Set<LegendConnectModelTrainingRun>()
                .SingleAsync(
                    item => item.Id == run.Id,
                    cancellationToken);

        if (!LegendConnectModelRuntimeProofSummary.IsValid(
                run.FailureDetail))
        {
            await FailPromotionAsync(
                run,
                "model_promotion_runtime_proof_missing",
                cancellationToken);
            return false;
        }

        pairs =
            await _db
                .Set<LegendLanguagePair>()
                .Where(item =>
                    pairKeys.Contains(
                        item.PairKey))
                .OrderBy(item =>
                    item.PairKey)
                .ToListAsync(
                    cancellationToken);

        if (await HasBlockingContradictionAsync(
                pairKeys,
                cancellationToken))
        {
            await FailPromotionAsync(
                run,
                "model_promotion_blocking_contradiction",
                cancellationToken);
            return false;
        }

        var challenger =
            run.ChallengerModelVersion!;

        var existingLineage =
            await _db
                .Set<LegendConnectModelPromotionPair>()
                .Where(item =>
                    item.ModelTrainingRunId ==
                        run.Id)
                .ToListAsync(
                    cancellationToken);

        if (existingLineage.Count > 0)
        {
            await FailPromotionAsync(
                run,
                "model_promotion_lineage_already_exists",
                cancellationToken);
            return false;
        }

        foreach (var pair in pairs)
        {
            _db.Set<LegendConnectModelPromotionPair>()
                .Add(
                    new LegendConnectModelPromotionPair
                    {
                        Id = Guid.NewGuid(),
                        ModelTrainingRunId =
                            run.Id,
                        PairKey =
                            pair.PairKey,
                        PreviousActiveModelVersion =
                            pair.ActiveModelVersion,
                        PromotedModelVersion =
                            challenger,
                        PromotedUtc =
                            now
                    });

            pair.ActiveModelVersion =
                challenger;

            pair.UpdatedUtc =
                now;
        }

        run.PromotionState =
            "Promoted";

        run.PromotedUtc =
            now;

        run.LeaseExpiresUtc =
            null;

        run.FailureCode =
            null;

        run.FailureDetail =
            PreserveEvaluationProof(
                run.FailureDetail!,
                pairs.Count,
                run.DatasetIdentity,
                run.DatasetEvaluatorVersion);

        run.UpdatedUtc =
            now;

        await _db.SaveChangesAsync(
            cancellationToken);

        return true;
    }

    internal async Task<bool> RollbackAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var run =
            await _db
                .Set<LegendConnectModelTrainingRun>()
                .SingleOrDefaultAsync(
                    item => item.Id == runId,
                    cancellationToken);

        if (run is null ||
            run.PromotionState !=
                "Promoted" ||
            string.IsNullOrWhiteSpace(
                run.ChallengerModelVersion))
        {
            return false;
        }

        var lineage =
            await _db
                .Set<LegendConnectModelPromotionPair>()
                .Where(item =>
                    item.ModelTrainingRunId ==
                        run.Id &&
                    item.RolledBackUtc == null)
                .OrderBy(item =>
                    item.PairKey)
                .ToListAsync(
                    cancellationToken);

        if (lineage.Count == 0)
            return false;

        var pairKeys =
            lineage
                .Select(item =>
                    item.PairKey)
                .ToArray();

        var pairs =
            await _db
                .Set<LegendLanguagePair>()
                .Where(item =>
                    pairKeys.Contains(
                        item.PairKey))
                .ToListAsync(
                    cancellationToken);

        if (pairs.Count !=
                lineage.Count)
        {
            return false;
        }

        var challenger =
            run.ChallengerModelVersion;

        if (pairs.Any(pair =>
                !string.Equals(
                    pair.ActiveModelVersion,
                    challenger,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        var now =
            DateTime.UtcNow;

        foreach (var row in lineage)
        {
            var pair =
                pairs.Single(item =>
                    item.PairKey ==
                        row.PairKey);

            pair.ActiveModelVersion =
                row.PreviousActiveModelVersion;

            pair.UpdatedUtc =
                now;

            row.RolledBackUtc =
                now;
        }

        run.PromotionState =
            "RolledBack";

        run.LeaseExpiresUtc =
            null;

        run.FailureCode =
            null;

        run.FailureDetail =
            AppendBounded(
                run.FailureDetail,
                $"rollback_pairs={lineage.Count}");

        run.UpdatedUtc =
            now;

        await _db.SaveChangesAsync(
            cancellationToken);

        return true;
    }

    private bool CanEnterPromotion(
        LegendConnectModelTrainingRun run) =>
        run.State ==
            "TrainingCompleted" &&
        run.EvaluationState ==
            "Passed" &&
        run.PromotionState ==
            "NotEvaluated" &&
        LegendConnectModelRuntimeProofSummary.IsValid(
            run.FailureDetail) &&
        !string.IsNullOrWhiteSpace(
            run.ChallengerModelVersion);

    private static string PreserveEvaluationProof(
        string evaluationProof,
        int pairCount,
        string datasetIdentity,
        int evaluatorVersion)
    {
        var detail =
            $"{evaluationProof};promotion_pairs={pairCount};promotion_dataset={datasetIdentity};promotion_evaluator={evaluatorVersion}";
        return detail[..Math.Min(
            detail.Length,
            1000)];
    }

    private bool MeetsEvaluationThresholds(
        LegendConnectModelTrainingRun run)
    {
        if (run.HeldOutScore is null ||
            run.RegressionScore is null)
        {
            return false;
        }

        return run.HeldOutScore >=
                   MinimumHeldOutScore() &&
               run.RegressionScore >=
                   MinimumRegressionScore();
    }

    private async Task<bool> HasBlockingContradictionAsync(
        IReadOnlyCollection<string> pairKeys,
        CancellationToken cancellationToken)
    {
        return await _db
            .Set<LegendTranslationQualityEvidence>()
            .AsNoTracking()
            .AnyAsync(
                item =>
                    pairKeys.Contains(
                        item.PairKey) &&
                    item.SupersededUtc == null &&
                    item.Signal ==
                        "Contradictory" &&
                    item.ResolutionState ==
                        "Open",
                cancellationToken);
    }

    private async Task FailPromotionAsync(
        LegendConnectModelTrainingRun run,
        string failureCode,
        CancellationToken cancellationToken)
    {
        run.PromotionState =
            "Rejected";

        run.LeaseExpiresUtc =
            null;

        run.FailureCode =
            failureCode[
                ..Math.Min(
                    failureCode.Length,
                    120)];

        run.FailureDetail =
            AppendBounded(
                run.FailureDetail,
                $"promotion_failure={failureCode}");

        run.UpdatedUtc =
            DateTime.UtcNow;

        await _db.SaveChangesAsync(
            cancellationToken);
    }

    private bool Enabled() =>
        bool.TryParse(
            _configuration[
                Prefix + "Enabled"],
            out var enabled) &&
        enabled;

    private static string AppendBounded(
        string? existing,
        string value)
    {
        var detail =
            string.IsNullOrWhiteSpace(existing)
                ? value
                : $"{existing};{value}";
        return detail[..Math.Min(
            detail.Length,
            1000)];
    }

    private decimal MinimumHeldOutScore() =>
        Score(
            _configuration[
                Prefix +
                "MinimumHeldOutScore"],
            DefaultMinimumHeldOutScore);

    private decimal MinimumRegressionScore() =>
        Score(
            _configuration[
                Prefix +
                "MinimumRegressionScore"],
            DefaultMinimumRegressionScore);

    private static decimal Score(
        string? value,
        decimal fallback) =>
        decimal.TryParse(
            value,
            System.Globalization
                .NumberStyles.Number,
            System.Globalization
                .CultureInfo.InvariantCulture,
            out var parsed)
            ? Math.Clamp(
                parsed,
                0m,
                1m)
            : fallback;
}
