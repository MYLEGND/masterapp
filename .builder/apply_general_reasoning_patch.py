from pathlib import Path

p = Path('Infrastructure/Messaging/LegendConnectCurriculum.cs')
s = p.read_text()

replacements = []

replacements.append((
'''            select new SemanticTransitionObservation(
                evidence.TransitionSignature,
                evidence.SourceSemanticFrame,
                evidence.ResultSemanticFrame,
                evidence.IndependentSourceIdentity,
                evidence.ContributionState,
                evidence.IsHumanVerifiedSupport,
                evidence.SourceCurriculumExampleId,
                evidence.ResultCurriculumExampleId)
''',
'''            select new SemanticTransitionObservation(
                evidence.TransitionSignature,
                evidence.SourceSemanticFrame,
                evidence.ResultSemanticFrame,
                evidence.IndependentSourceIdentity,
                evidence.ContributionState,
                evidence.IsHumanVerifiedSupport,
                evidence.SourceCurriculumExampleId,
                evidence.ResultCurriculumExampleId,
                evidence.FounderSemanticExampleRelationEvidenceId != null)
'''))

replacements.append((
'''            candidates.Add(new SemanticTransitionCandidate(
                group.Key,
                sourceFrame,
                resultFrame,
                bindings,
                missingVariables,
                directSourceMatchCount,
                independentEvidenceCount));
''',
'''            candidates.Add(new SemanticTransitionCandidate(
                group.Key,
                sourceFrame,
                resultFrame,
                bindings,
                missingVariables,
                directSourceMatchCount,
                independentEvidenceCount,
                group.Any(item => item.IsFounderCrossExampleDerived)));
'''))

replacements.append((
'''                return new(
                    language,
                    sourceComponents,
                    projectedCandidates
                        .OrderByDescending(item => item.DirectSourceMatchCount)
                        .ThenBy(item => item.TransitionSignature, StringComparer.Ordinal)
                        .First(),
                    "response_meaning_plan_governed_projected",
                    false,
                    false);
''',
'''                var projectedSelected = projectedCandidates
                    .OrderByDescending(item => item.DirectSourceMatchCount)
                    .ThenBy(item => item.TransitionSignature, StringComparer.Ordinal)
                    .First();
                var projectedReasoning = ExpandGovernedSemanticReasoningPath(
                    projectedSelected,
                    observations);
                if (projectedReasoning.Selected is null)
                {
                    return projectedReasoning.IsAmbiguous
                        ? SemanticTransitionSelection.Ambiguous(projectedReasoning.ReasonCode)
                        : projectedReasoning.IsContradicted
                            ? SemanticTransitionSelection.Contradicted(projectedReasoning.ReasonCode)
                            : SemanticTransitionSelection.Insufficient(projectedReasoning.ReasonCode);
                }
                return new(
                    language,
                    sourceComponents,
                    projectedReasoning.Selected,
                    projectedReasoning.StepCount > 1
                        ? "semantic_reasoning_path_governed"
                        : "response_meaning_plan_governed_projected",
                    false,
                    false);
'''))

replacements.append((
'''        if (candidates.Select(item => item.ResultFrame.Signature).Distinct().Count() != 1)
            return SemanticTransitionSelection.Ambiguous("ambiguous_semantic_transition");
        return new(language, sourceComponents, candidates.OrderBy(item => item.TransitionSignature).First(), "response_meaning_plan_governed", false, false);
    }

    private static IReadOnlyDictionary<string, string> ActiveDiscourseBindings''',
'''        if (candidates.Select(item => item.ResultFrame.Signature).Distinct().Count() != 1)
            return SemanticTransitionSelection.Ambiguous("ambiguous_semantic_transition");
        var selected = candidates.OrderBy(item => item.TransitionSignature).First();
        var reasoning = ExpandGovernedSemanticReasoningPath(selected, observations);
        if (reasoning.Selected is null)
        {
            return reasoning.IsAmbiguous
                ? SemanticTransitionSelection.Ambiguous(reasoning.ReasonCode)
                : reasoning.IsContradicted
                    ? SemanticTransitionSelection.Contradicted(reasoning.ReasonCode)
                    : SemanticTransitionSelection.Insufficient(reasoning.ReasonCode);
        }
        return new(
            language,
            sourceComponents,
            reasoning.Selected,
            reasoning.StepCount > 1
                ? "semantic_reasoning_path_governed"
                : "response_meaning_plan_governed",
            false,
            false);
    }

    /// <summary>
    /// Executes a bounded chain of explicit Founder cross-example semantic
    /// transformations through the same production transition authority used
    /// for one-step inference. Cross-example declarations are the only edges
    /// eligible for transitive execution: ordinary conversational response
    /// transitions remain terminal and therefore cannot accidentally become a
    /// hidden reasoning program. Every intermediate frame is fully
    /// instantiated from governed bindings, each next edge independently
    /// passes the existing production gate, and branch ambiguity,
    /// contradiction, cycles, or excessive depth fail closed.
    /// </summary>
    private static SemanticReasoningPathResult ExpandGovernedSemanticReasoningPath(
        SemanticTransitionCandidate initial,
        IReadOnlyList<SemanticTransitionObservation> observations)
    {
        if (!initial.IsFounderCrossExampleDerived)
            return new(initial, "semantic_reasoning_not_required", 1, false, false);

        const int maximumReasoningSteps = 16;
        var governedEdges = observations
            .Where(item => item.IsFounderCrossExampleDerived)
            .ToArray();
        var visitedFrames = new HashSet<string>(StringComparer.Ordinal)
        {
            initial.SourceFrame.Signature
        };
        var current = initial;
        var totalEvidence = initial.IndependentEvidenceCount;

        for (var step = 1; step <= maximumReasoningSteps; step++)
        {
            if (!visitedFrames.Add(current.ResultFrame.Signature))
                return new(null, "semantic_reasoning_cycle_detected", step, true, false);

            if (!TryInstantiateFrame(current.ResultFrame, current.Bindings, out var resultValues))
            {
                return new(
                    current with { IndependentEvidenceCount = totalEvidence },
                    step > 1 ? "semantic_reasoning_path_governed" : "semantic_reasoning_terminal_content_binding",
                    step,
                    false,
                    false);
            }

            if (HasContradictedSemanticTransition(governedEdges, resultValues))
                return new(null, "semantic_reasoning_contradicted", step, false, true);

            var next = BuildProductionSemanticTransitionCandidates(
                    governedEdges,
                    resultValues,
                    allowMissingVariables: false)
                .Where(item => item.IsFounderCrossExampleDerived)
                .ToList();
            if (next.Count == 0)
            {
                return new(
                    current with { IndependentEvidenceCount = totalEvidence },
                    step > 1 ? "semantic_reasoning_path_governed" : "semantic_reasoning_terminal",
                    step,
                    false,
                    false);
            }

            if (next.Select(item => item.ResultFrame.Signature)
                    .Distinct(StringComparer.Ordinal).Count() != 1 ||
                next.Select(item => CanonicalBindings(item.Bindings))
                    .Distinct(StringComparer.Ordinal).Count() != 1)
            {
                return new(null, "ambiguous_semantic_reasoning_branch", step, true, false);
            }

            var chosen = next
                .OrderByDescending(item => item.DirectSourceMatchCount)
                .ThenBy(item => item.TransitionSignature, StringComparer.Ordinal)
                .First();
            totalEvidence += chosen.IndependentEvidenceCount;
            current = chosen with { IndependentEvidenceCount = totalEvidence };
        }

        return new(null, "semantic_reasoning_depth_exceeded", maximumReasoningSteps, false, false);
    }

    private static IReadOnlyDictionary<string, string> ActiveDiscourseBindings'''))

replacements.append((
'''    private sealed record SemanticTransitionObservation(
        string TransitionSignature,
        string SourceFrame,
        string ResultFrame,
        string IndependentSourceIdentity,
        string ContributionState,
        bool IsHumanVerifiedSupport,
        Guid SourceExampleId,
        Guid ResultExampleId);
''',
'''    private sealed record SemanticTransitionObservation(
        string TransitionSignature,
        string SourceFrame,
        string ResultFrame,
        string IndependentSourceIdentity,
        string ContributionState,
        bool IsHumanVerifiedSupport,
        Guid SourceExampleId,
        Guid ResultExampleId,
        bool IsFounderCrossExampleDerived);
'''))

replacements.append((
'''    private sealed record SemanticTransitionCandidate(
        string TransitionSignature,
        NormalizedSemanticFrame SourceFrame,
        NormalizedSemanticFrame ResultFrame,
        IReadOnlyDictionary<string, string> Bindings,
        IReadOnlyList<SemanticMissingVariable> MissingVariables,
        int DirectSourceMatchCount,
        int IndependentEvidenceCount);
''',
'''    private sealed record SemanticTransitionCandidate(
        string TransitionSignature,
        NormalizedSemanticFrame SourceFrame,
        NormalizedSemanticFrame ResultFrame,
        IReadOnlyDictionary<string, string> Bindings,
        IReadOnlyList<SemanticMissingVariable> MissingVariables,
        int DirectSourceMatchCount,
        int IndependentEvidenceCount,
        bool IsFounderCrossExampleDerived);

    private sealed record SemanticReasoningPathResult(
        SemanticTransitionCandidate? Selected,
        string ReasonCode,
        int StepCount,
        bool IsAmbiguous,
        bool IsContradicted);
'''))

for old, new in replacements:
    count = s.count(old)
    if count != 1:
        raise RuntimeError(f'Expected exactly one replacement target, found {count}: {old[:80]!r}')
    s = s.replace(old, new)

p.write_text(s)
