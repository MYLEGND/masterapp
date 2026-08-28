from pathlib import Path

P = Path("Infrastructure/Messaging/LegendConnectCurriculum.cs")
text = P.read_text()


def once(old: str, new: str, label: str):
    global text
    n = text.count(old)
    if n != 1:
        raise SystemExit(f"{label}: expected 1 anchor, found {n}")
    text = text.replace(old, new, 1)

# Current-turn Founder source evidence is stronger than antecedent context for
# variables that the source frame explicitly carries but that are not lexical
# meaning nodes. This does NOT retrieve an answer. It only completes the
# already-selected production-eligible source frame when the current input is
# itself an exact active Founder-controlled source endpoint and every matching
# source contribution agrees on the missing controlled value.
old = '''            if (partialCandidates.Count > 0)
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
'''
new = '''            if (partialCandidates.Count > 0)
            {
                // The current utterance may itself be a Founder-controlled source
                // endpoint whose non-lexical frame values are intentionally carried
                // as controlled metadata. Complete only those missing variables from
                // that exact source identity when all matching active endpoint
                // evidence agrees. This is source understanding, never answer lookup.
                candidates = await BindCandidatesFromCurrentSourceEndpointAsync(
                    language,
                    input,
                    partialCandidates,
                    cancellationToken);

                if (candidates.Count == 0)
                {
                    // Existing conversation grounding remains the sole antecedent
                    // context-binding authority. Reasoning is considered only if
                    // neither current-source evidence nor context yields a response.
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
'''
once(old, new, "current source endpoint binding insertion")

marker = '''    private async Task<IReadOnlyList<GroundedContextFrame>> ResolveGroundedContextFramesAsync(
'''
method = r'''    private async Task<IReadOnlyList<SemanticTransitionCandidate>> BindCandidatesFromCurrentSourceEndpointAsync(
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

'''
if text.count(marker) != 1:
    raise SystemExit(f"source endpoint method marker: found {text.count(marker)}")
text = text.replace(marker, method + marker, 1)

# Bound realization layouts are semantic slot layouts, not token-coordinate
# templates. Sentence-specific offsets/lengths made independently equivalent
# Founder realizations appear contradictory. Keep unique semantic dimensions,
# ordering, overlap checks and punctuation gates; remove geometry from shape.
old_shape = '''        var firstStart = components[0].StartTokenIndex;
        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            string.Join("|", components.Select(item =>
                $"{item.Dimension}:{item.StartTokenIndex - firstStart}:{item.TokenLength}")),
            components,
            TerminalPunctuation(example.Text));
'''
new_shape = '''        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            string.Join("|", components.Select(item => item.Dimension)),
            components,
            TerminalPunctuation(example.Text));
'''
once(old_shape, new_shape, "bound semantic layout shape")

# Several independently mature articulation layouts for one already-selected
# semantic result are alternative realizations, not competing semantic answers.
# Each layout group has already passed the same >=3 independent Founder-family
# gate and every endpoint matches the selected result frame. Try those groups
# deterministically inside this same realization authority and accept only a
# non-verbatim composition. Semantic transition ambiguity remains upstream and
# continues to fail closed.
old_realize = '''        if (scopedExamples.Count > 0)
        {
            if (eligibleLayouts.Count == 0)
                return SemanticTransitionRealization.Insufficient("result_realization_layout_insufficient");
            if (eligibleLayouts.Count > 1)
                return SemanticTransitionRealization.Ambiguous("ambiguous_result_realization_layout");

            // A native conversational response must be composed from governed
            // semantic components. Returning an entire stored curriculum
            // sentence is retrieval, not articulation. The learned layout is
            // independently supported by three Founder families; this method
            // recombines only position-, dimension-, and value-compatible
            // exact anchors and rejects every verbatim endpoint.
            var realizationSeed = string.Join('|',
                sourceComponents
                    .OrderBy(item => item.StartTokenIndex)
                    .ThenBy(item => item.Dimension, StringComparer.Ordinal)
                    .Select(item => item.SemanticSignature + "=" + item.SurfaceForm));
            if (!TryRealizeOriginalLearnedLayout(
                    eligibleLayouts[0].Layouts,
                    scopedExamples,
                    candidate.TransitionSignature + "|" + realizationSeed,
                    out var text))
            {
                return SemanticTransitionRealization.Insufficient(
                    "result_original_realization_unavailable");
            }
            if (await IsActiveCurriculumSentenceAsync(languageCode, text, cancellationToken))
            {
                return SemanticTransitionRealization.Insufficient(
                    "result_original_realization_matched_curriculum_sentence");
            }
            return new SemanticTransitionRealization(text, eligibleLayouts[0].IndependentFamilies, null, false);
        }
'''
new_realize = '''        if (scopedExamples.Count > 0)
        {
            if (eligibleLayouts.Count == 0)
                return SemanticTransitionRealization.Insufficient("result_realization_layout_insufficient");

            // A native conversational response must be composed from governed
            // semantic components. Returning an entire stored curriculum
            // sentence is retrieval, not articulation. Multiple independently
            // supported layouts under this one selected result frame represent
            // articulation diversity, not a second semantic decision. Evaluate
            // them deterministically here and retain the existing endpoint ban.
            var realizationSeed = string.Join('|',
                sourceComponents
                    .OrderBy(item => item.StartTokenIndex)
                    .ThenBy(item => item.Dimension, StringComparer.Ordinal)
                    .Select(item => item.SemanticSignature + "=" + item.SurfaceForm));
            foreach (var eligibleLayout in eligibleLayouts
                         .OrderBy(item => LegendLanguageIdentity.TextHash(
                             candidate.TransitionSignature + "|" + realizationSeed + "|" +
                             item.Layouts[0].Shape + "|" + item.Layouts[0].TerminalPunctuation),
                             StringComparer.Ordinal))
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
                    false);
            }

            return SemanticTransitionRealization.Insufficient(
                "result_original_realization_unavailable");
        }
'''
once(old_realize, new_realize, "static articulation alternatives")

P.write_text(text)
