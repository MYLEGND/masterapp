namespace Infrastructure.Messaging;

/// <summary>
/// Conservative local evaluator for exact, independently approved references.
/// It executes no answering API. Semantically equivalent but non-identical
/// prose cannot pass this evaluator and must receive independent review.
/// </summary>
internal sealed class LocalLegendConnectModelEvaluationBackend : ILegendConnectModelEvaluationBackend
{
    public Task<LegendModelEvaluationJudgement> JudgeAsync(
        LegendModelEvaluationJudgeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.BaselineModelText is null ||
            request.Example.OutputContract is not ("target_language_text_only" or "governed_surface_candidate_text_only" or "executable_oracle_exact_v1" or "foundation_control_exact_v1") ||
            string.IsNullOrWhiteSpace(request.Example.TargetText) ||
            request.GovernedReferenceText != request.Example.TargetText)
        {
            return Task.FromResult(new LegendModelEvaluationJudgement(false, 0, 0, 0, 0, 0, 0, 0, 0,
                false, false, true, ["local_evaluation_independent_reference_required"],
                "local_evaluation_independent_reference_required"));
        }
        var candidate = LegendFoundationConversationControl.MatchesVerifiedTarget(request.Example.SourceText, request.Example.TargetText, request.ChallengerText) ? 1m : 0m;
        var baseline = LegendFoundationConversationControl.MatchesVerifiedTarget(request.Example.SourceText, request.Example.TargetText, request.BaselineModelText) ? 1m : 0m;
        return Task.FromResult(new LegendModelEvaluationJudgement(true, candidate, baseline,
            candidate, candidate, candidate, candidate, candidate, candidate,
            false, string.IsNullOrWhiteSpace(request.ChallengerText), candidate < baseline,
            [candidate == 1 ? "independent_reference_exact_match" : "independent_reference_mismatch"]));
    }
}
