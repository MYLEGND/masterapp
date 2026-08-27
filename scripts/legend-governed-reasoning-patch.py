from pathlib import Path

CONTRACTS = Path("Domain/Messaging/LegendConnectContracts.cs")
CURRICULUM = Path("Infrastructure/Messaging/LegendConnectCurriculum.cs")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one anchor, found {count}")
    return text.replace(old, new, 1)


# Extend the existing text-free response plan with optional proof metadata.
text = CONTRACTS.read_text()
text = replace_once(
    text,
    """    IReadOnlyDictionary<string, string>? BoundSemanticVariables = null,
    IReadOnlyDictionary<string, string>? UnboundResultVariables = null);""",
    """    IReadOnlyDictionary<string, string>? BoundSemanticVariables = null,
    IReadOnlyDictionary<string, string>? UnboundResultVariables = null,
    IReadOnlyList<string>? ReasoningTransitionPath = null,
    int ReasoningEvidenceCount = 0);""",
    "response plan contract",
)
CONTRACTS.write_text(text)

text = CURRICULUM.read_text()

# Preserve the existing response/content/articulation evidence accounting and add
# only the weakest-link support carried by an internal reasoning proof.
text = replace_once(
    text,
    "            selected.IndependentEvidenceCount + realization.LayoutEvidenceCount,",
    "            selected.IndependentEvidenceCount + selected.ReasoningEvidenceCount + realization.LayoutEvidenceCount,",
    "legacy inference evidence accounting",
)
text = replace_once(
    text,
    "            selection.Selected.IndependentEvidenceCount + content.EvidenceCount + realization.LayoutEvidenceCount,",
    "            selection.Selected.IndependentEvidenceCount + selection.Selected.ReasoningEvidenceCount + content.EvidenceCount + realization.LayoutEvidenceCount,",
    "composed inference evidence accounting",
)
text = replace_once(
    text,
    """            string.Join(";", activeBindings.OrderBy(item => item.Key).Select(item => item.Key + "=" + item.Value))));""",
    """            string.Join(";", activeBindings.OrderBy(item => item.Key).Select(item => item.Key + "=" + item.Value)),
            string.Join(";", selected.ReasoningPath ?? Array.Empty<string>())));""",
    "response plan proof identity",
)
text = replace_once(
    text,
    """            selected.IndependentEvidenceCount,
            discourseState is { Turns.Count: > 0 },
            selected.Bindings,
            unboundResultVariables),""",
    """            selected.IndependentEvidenceCount + selected.ReasoningEvidenceCount,
            discourseState is { Turns.Count: > 0 },
            selected.Bindings,
            unboundResultVariables,
            selected.ReasoningPath,
            selected.ReasoningEvidenceCount),""",
    "response plan proof projection",
)

selector_start = text.index(
    "    private async Task<SemanticTransitionSelection> SelectSemanticTransitionAsync("
)
selector_end = text.index(
    "    private static IReadOnlyDictionary<string, string> ActiveDiscourseBindings",
    selector_start,
)
selector = text[selector_start:selector_end]

selector = replace_once(
    selector,
    "        var candidates = BuildProductionSemanticTransitionCandidates(observations, values, allowMissingVariables: false);",
    """        // Founder-declared reasoning.* relationships are internal semantic
        // operations, never direct conversational answer edges. They retain the
        // exact canonical transition provenance and production-eligibility gates.
        var reasoningOperators = await LoadActiveGovernedReasoningOperatorsAsync(
            language,
            cancellationToken);
        var responseObservations = reasoningOperators.Count == 0
            ? observations
            : observations.Where(item => !reasoningOperators.ContainsKey(item.TransitionSignature)).ToList();
        var candidates = BuildProductionSemanticTransitionCandidates(
            responseObservations,
            values,
            allowMissingVariables: false);""",
    "direct candidate classification",
)
selector = replace_once(
    selector,
    "HasContradictedSemanticTransition(observations, values)",
    "HasContradictedSemanticTransition(responseObservations, values)",
    "direct contradiction classification",
)
selector = replace_once(
    selector,
    """BuildProductionSemanticTransitionCandidates(
                    observations,
                    values,
                    allowMissingVariables: false,
                    allowMissingStaticDimensions: true)""",
    """BuildProductionSemanticTransitionCandidates(
                    responseObservations,
                    values,
                    allowMissingVariables: false,
                    allowMissingStaticDimensions: true)""",
    "projected candidate classification",
)
selector = replace_once(
    selector,
    """HasContradictedSemanticTransition(
                        observations,
                        values,
                        allowMissingStaticDimensions: true)""",
    """HasContradictedSemanticTransition(
                        responseObservations,
                        values,
                        allowMissingStaticDimensions: true)""",
    "projected contradiction classification",
)

partial_start = selector.index(
    "            var partialCandidates = BuildProductionSemanticTransitionCandidates("
)
partial_end = selector.index(
    "        }\n        if (candidates.Select",
    partial_start,
)
new_partial = """            var failureReason = "semantic_transition_not_supported";
            var partialCandidates = BuildProductionSemanticTransitionCandidates(
                    responseObservations, values, allowMissingVariables: true)
                .Where(item => item.MissingVariables.Count > 0 && item.DirectSourceMatchCount > 0)
                .ToList();
            if (partialCandidates.Count > 0)
            {
                // Existing conversation grounding remains the sole context-binding
                // authority. Reasoning is considered only if that authority yields
                // no executable response candidate.
                var contextFrames = new List<GroundedContextFrame>();
                if (discourseState is not null)
                {
                    contextFrames.AddRange(ResolveGroundedContextFramesFromDiscourseState(
                        discourseState, responseObservations));
                }
                contextFrames.AddRange(await ResolveGroundedContextFramesAsync(
                    language, context, responseObservations, cancellationToken));
                candidates = BindCandidatesFromGroundedContext(partialCandidates, contextFrames);
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
"""
selector = selector[:partial_start] + new_partial + selector[partial_end:]
text = text[:selector_start] + selector + text[selector_end:]

# The relation semantic identity classifies internal reasoning. All transitions
# bearing a reasoning.* identity are excluded from direct reply selection, even
# if malformed; only one unambiguous supported operator can execute.
method_marker = (
    "    private async Task<IReadOnlyList<GroundedContextFrame>> "
    "ResolveGroundedContextFramesAsync("
)
method_at = text.index(method_marker)
methods = r'''    private async Task<IReadOnlyDictionary<string, string>> LoadActiveGovernedReasoningOperatorsAsync(
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

        return rows
            .GroupBy(item => item.TransitionSignature, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var identities = group
                        .Select(item => item.RelationshipSemanticIdentity)
                        .Distinct(StringComparer.Ordinal)
                        .Take(2)
                        .ToArray();
                    return identities.Length == 1 &&
                        LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(identities[0])
                            ? identities[0]
                            : string.Empty;
                },
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
            if (!TryGetProductionSemanticTransitionFrames(
                    group,
                    out var independentEvidenceCount,
                    out var sourceFrame,
                    out var resultFrame))
            {
                continue;
            }
            rules.Add(new LegendGovernedReasoningRule(
                group.Key,
                reasoningOperators[group.Key],
                sourceFrame.Dimensions,
                resultFrame.Dimensions,
                independentEvidenceCount));
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
            var proofCandidates = BuildProductionSemanticTransitionCandidates(
                responseObservations,
                proof.Values,
                allowMissingVariables: false);
            foreach (var candidate in proofCandidates)
                responses.Add((candidate, proof));
        }
        if (responses.Count == 0)
            return GovernedReasonedResponseSelection.None;

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
            .OrderBy(item => item.Proof.Depth)
            .ThenByDescending(item => item.Proof.EvidenceCount)
            .ThenBy(item => item.Candidate.TransitionSignature, StringComparer.Ordinal)
            .First();
        return GovernedReasonedResponseSelection.Success(
            selected.Candidate with
            {
                ReasoningEvidenceCount = selected.Proof.EvidenceCount,
                ReasoningPath = selected.Proof.TransitionPath
            });
    }

'''
text = text[:method_at] + methods + text[method_at:]

candidate_marker = "    private sealed record SemanticTransitionCandidate("
candidate_start = text.index(candidate_marker)
candidate_end = text.index(");", candidate_start) + 2
candidate = text[candidate_start:candidate_end]
candidate = replace_once(
    candidate,
    "    int IndependentEvidenceCount);",
    """    int IndependentEvidenceCount,
    int ReasoningEvidenceCount = 0,
    IReadOnlyList<string>? ReasoningPath = null);""",
    "semantic candidate proof fields",
)
text = text[:candidate_start] + candidate + text[candidate_end:]

grounded_marker = "    private sealed record GroundedContextFrame("
grounded_at = text.index(grounded_marker)
reasoned_record = r'''    private sealed record GovernedReasonedResponseSelection(
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

'''
text = text[:grounded_at] + reasoned_record + text[grounded_at:]

CURRICULUM.write_text(text)
