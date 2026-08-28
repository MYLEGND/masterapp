from pathlib import Path

P = Path("Infrastructure/Messaging/LegendConnectCurriculum.cs")
text = P.read_text()


def once(old: str, new: str, label: str):
    global text
    n = text.count(old)
    if n != 1:
        raise SystemExit(f"{label}: expected 1 anchor, found {n}")
    text = text.replace(old, new, 1)

# A partially matched response transition may be missing content variables that
# are not mature meaning primitives themselves. Bind them from the strongest
# available canonical evidence in this order: exact current Founder endpoint,
# exact lexical semantic evidence in the current utterance, then governed prior
# conversation context. All three routes feed the SAME candidate binding
# authority; none can select a response on its own.
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
'''
once(old, new, "current evidence binding insertion")

marker = '''    private async Task<IReadOnlyList<GroundedContextFrame>> ResolveGroundedContextFramesAsync(
'''
methods = r'''    private async Task<IReadOnlyList<SemanticTransitionCandidate>> BindCandidatesFromCurrentSourceEndpointAsync(
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

'''
if text.count(marker) != 1:
    raise SystemExit(f"source evidence methods marker: found {text.count(marker)}")
text = text.replace(marker, methods + marker, 1)

# Bound realization layouts are semantic slot layouts, not token-coordinate
# templates. Sentence-specific offsets/lengths made independently equivalent
# Founder realizations appear contradictory. Keep semantic slot order and
# punctuation while removing sentence geometry from reusable identity.
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

# Multiple independently mature layouts for one already-selected semantic
# result are alternative articulation evidence, not semantic disagreement. The
# final articulation authority will decide which mature forms can safely
# compose; do not fail merely because more than one mature layout exists.
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
                    false);
            }

            return SemanticTransitionRealization.Insufficient(
                "result_original_realization_unavailable");
        }
'''
once(old_realize, new_realize, "static articulation alternatives")

P.write_text(text)
