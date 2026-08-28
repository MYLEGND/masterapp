from pathlib import Path

P = Path("Infrastructure/Messaging/LegendConnectCurriculum.cs")
text = P.read_text()


def once(old: str, new: str, label: str):
    global text
    n = text.count(old)
    if n != 1:
        raise SystemExit(f"{label}: expected 1 anchor, found {n}")
    text = text.replace(old, new, 1)

# Keep the observed Founder sentence inside the ephemeral realization layout so
# the existing articulation authority can prove cross-family realization forms.
# This is read-only request-time evidence, not a new template store.
once(
'''    private sealed record SemanticRealizationLayout(
        Guid FamilyId,
        string Shape,
        IReadOnlyList<SemanticLayoutComponent> Components,
        string TerminalPunctuation);''',
'''    private sealed record SemanticRealizationLayout(
        Guid FamilyId,
        string Shape,
        IReadOnlyList<SemanticLayoutComponent> Components,
        string TerminalPunctuation,
        string ObservedText);''',
"surface layout observed text contract")

once(
'''        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            SemanticLayoutShape(components),
            components,
            TerminalPunctuation(example.Text));''',
'''        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            SemanticLayoutShape(components),
            components,
            TerminalPunctuation(example.Text),
            example.Text);''',
"static surface layout observed text")

once(
'''        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            string.Join("|", components.Select(item => item.Dimension)),
            components,
            TerminalPunctuation(example.Text));''',
'''        layout = new SemanticRealizationLayout(
            example.CurriculumFamilyId,
            string.Join("|", components.Select(item => item.Dimension)),
            components,
            TerminalPunctuation(example.Text),
            example.Text);''',
"bound surface layout observed text")

# The word-level lattice experiment proved that independently supported semantic
# phrases can still be grammatically incompatible when spliced into a different
# sentence scaffold. Remove that unsafe Cartesian product. For variable-bearing
# results, synthesize the response only from COMPLETE grammatical realization
# forms whose structural shape is independently repeated across >=3 Founder
# families. Combining multiple mature forms yields a new response while never
# inventing connective language or corrupting an observed sentence.
old = '''            foreach (var eligibleLayout in eligibleLayouts
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
                "result_original_realization_unavailable");'''
new = '''            if (hasResultVariables &&
                TryRealizeGovernedCrossArticulation(
                    eligibleLayouts.SelectMany(item => item.Layouts).ToArray(),
                    scopedExamples,
                    candidate.TransitionSignature + "|" + realizationSeed,
                    out var crossArticulatedText,
                    out var crossArticulationEvidence))
            {
                if (!await IsActiveCurriculumSentenceAsync(
                        languageCode,
                        crossArticulatedText,
                        cancellationToken))
                {
                    return new SemanticTransitionRealization(
                        crossArticulatedText,
                        crossArticulationEvidence,
                        null,
                        false);
                }
            }

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
                "result_original_realization_unavailable");'''
once(old, new, "cross-articulation insertion")

marker = '''    private static bool TryRealizeOriginalLearnedLayout(
'''
helper = r'''    /// <summary>
    /// Synthesizes one response from two complete grammatical realization
    /// forms of the same already-selected semantic result. A form is eligible
    /// only when its abstract semantic-slot shape recurs in at least three
    /// independent Founder families. The current bound value must already have
    /// an exact scoped endpoint for that form. No words are spliced between
    /// forms; every sentence remains observed Founder language, while the
    /// multi-form response itself is newly composed.
    /// </summary>
    private static bool TryRealizeGovernedCrossArticulation(
        IReadOnlyList<SemanticRealizationLayout> matureLayouts,
        IReadOnlyList<SemanticResultExample> scopedEndpoints,
        string seed,
        out string text,
        out int evidenceCount)
    {
        text = string.Empty;
        evidenceCount = 0;
        if (matureLayouts.Count == 0 || scopedEndpoints.Count == 0)
            return false;

        var scopedTexts = scopedEndpoints
            .Select(item => LegendLanguageIdentity.NormalizeText(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);
        if (scopedTexts.Count < 2)
            return false;

        var forms = matureLayouts
            .GroupBy(
                item => item.Shape + "\u001f" + item.TerminalPunctuation,
                StringComparer.Ordinal)
            .Select(group => new
            {
                Layouts = group.ToArray(),
                IndependentFamilies = group.Select(item => item.FamilyId).Distinct().Count()
            })
            .Where(group => group.IndependentFamilies >= 3)
            .SelectMany(group => group.Layouts
                .Select(item => new
                {
                    Text = LegendLanguageIdentity.NormalizeText(item.ObservedText),
                    group.IndependentFamilies,
                    Shape = item.Shape
                })
                .Where(item => scopedTexts.Contains(item.Text)))
            .Where(item => !HasImmediateRepeatedWord(item.Text))
            .GroupBy(item => item.Shape + "\u001f" + item.Text, StringComparer.Ordinal)
            .Select(group => group.First())
            // Prefer informative complete forms; hash only resolves equal-size
            // ties deterministically and never carries topic logic.
            .OrderByDescending(item => SurfaceComponents(item.Text).Count)
            .ThenBy(item => LegendLanguageIdentity.TextHash(seed + "|" + item.Shape + "|" + item.Text), StringComparer.Ordinal)
            .ToArray();

        if (forms.Length < 2)
            return false;

        // Use two distinct mature forms. Keeping each sentence intact is what
        // prevents the malformed cross-scaffold wording caught by the live
        // conversation proof. The composition is deterministic and bounded.
        var selected = forms.Take(2).ToArray();
        if (string.Equals(selected[0].Text, selected[1].Text, StringComparison.Ordinal))
            return false;

        var combined = LegendLanguageIdentity.NormalizeText(
            selected[0].Text + " " + selected[1].Text);
        if (string.IsNullOrWhiteSpace(combined) || scopedTexts.Contains(combined))
            return false;

        text = combined;
        evidenceCount = selected.Min(item => item.IndependentFamilies);
        return evidenceCount >= 3;
    }

    private static bool HasImmediateRepeatedWord(string text)
    {
        var words = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim('.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\''))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        for (var index = 1; index < words.Length; index++)
        {
            if (string.Equals(words[index - 1], words[index], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

'''
if text.count(marker) != 1:
    raise SystemExit(f"cross-articulation helper marker: found {text.count(marker)}")
text = text.replace(marker, helper + marker, 1)

P.write_text(text)
