from pathlib import Path

P = Path("Infrastructure/Messaging/LegendConnectCurriculum.cs")
text = P.read_text()


def once(old: str, new: str, label: str):
    global text
    n = text.count(old)
    if n != 1:
        raise SystemExit(f"{label}: expected 1 anchor, found {n}")
    text = text.replace(old, new, 1)

# The realization layout is an ephemeral projection of existing Founder
# evidence. Carrying its observed text lets this SAME articulation authority
# derive reusable surface structure. Nothing is persisted as a template and no
# second responder or response store is introduced.
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

# The live-gap patch already removed sentence-specific geometry from bound
# layout identity. Preserve that correction and carry observed surface only.
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

# Variable-bearing results need cross-articulation, not the static composer's
# requirement that every semantic value be identical across families. Route
# them through a lattice built exclusively from independently repeated Founder
# surface evidence. Static semantics and layout shape remain unchanged.
once(
'''                if (!TryRealizeOriginalLearnedLayout(
                        eligibleLayout.Layouts,
                        scopedExamples,
                        candidate.TransitionSignature + "|" + realizationSeed + "|" +
                        eligibleLayout.Layouts[0].Shape,
                        out var text))
                {
                    continue;
                }''',
'''                var realized = hasResultVariables
                    ? TryRealizeOriginalGovernedSurfaceLattice(
                        eligibleLayout.Layouts,
                        layouts,
                        candidate.ResultFrame,
                        candidate.Bindings,
                        scopedExamples,
                        candidate.TransitionSignature + "|" + realizationSeed + "|" +
                        eligibleLayout.Layouts[0].Shape,
                        out var text)
                    : TryRealizeOriginalLearnedLayout(
                        eligibleLayout.Layouts,
                        scopedExamples,
                        candidate.TransitionSignature + "|" + realizationSeed + "|" +
                        eligibleLayout.Layouts[0].Shape,
                        out text);
                if (!realized)
                    continue;''',
"variable-aware original articulation")

marker = '''    private static bool TryRealizeOriginalLearnedLayout(
'''
helper = r'''    /// <summary>
    /// Cross-family original articulation for variable-bearing result frames.
    ///
    /// The caller has already established one semantic answer and a layout
    /// shape supported by at least three independent Founder families. This
    /// method adds three additional fail-closed requirements before composing:
    ///  1. the literal language scaffold (all text between semantic slots) must
    ///     recur in at least three independent Founder families;
    ///  2. every STATIC semantic-slot surface used in the new sentence must
    ///     itself recur at that structural position in at least three families;
    ///  3. every VARIABLE slot is filled only with an exact Founder-approved
    ///     surface for the exact bound semantic value.
    ///
    /// The result is a deterministic recombination of independently governed
    /// language evidence. It is not a prompt map, canned answer, provider
    /// fallback, persisted template, or second response authority.
    /// </summary>
    private static bool TryRealizeOriginalGovernedSurfaceLattice(
        IReadOnlyList<SemanticRealizationLayout> supportLayouts,
        IReadOnlyList<SemanticRealizationLayout> allLayouts,
        NormalizedSemanticFrame resultFrame,
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlyList<SemanticResultExample> storedEndpoints,
        string seed,
        out string text)
    {
        text = string.Empty;
        if (supportLayouts.Count == 0 || supportLayouts[0].Components.Count < 2)
            return false;

        var independentFamilies = supportLayouts
            .Select(item => item.FamilyId)
            .Distinct()
            .Count();
        if (independentFamilies < 3)
            return false;

        var referenceShape = supportLayouts[0].Shape;
        var componentCount = supportLayouts[0].Components.Count;
        if (supportLayouts.Any(item =>
                !string.Equals(item.Shape, referenceShape, StringComparison.Ordinal) ||
                item.Components.Count != componentCount))
        {
            return false;
        }

        var dynamicDimensions = resultFrame.Dimensions
            .Where(item => IsSemanticVariable(item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (dynamicDimensions.Count == 0)
            return false;

        // Prove that every structural position carries exactly the dimension
        // expected by the selected result frame. Static semantic values may not
        // drift. Variable values are intentionally allowed to differ by family.
        for (var position = 0; position < componentCount; position++)
        {
            var dimension = supportLayouts[0].Components[position].Dimension;
            if (supportLayouts.Any(item => !string.Equals(
                    item.Components[position].Dimension,
                    dimension,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (dynamicDimensions.ContainsKey(dimension))
                continue;

            if (!resultFrame.Dimensions.TryGetValue(dimension, out var expected) ||
                IsSemanticVariable(expected) ||
                supportLayouts.Any(item => !string.Equals(
                    item.Components[position].Value,
                    expected,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        var observedPatterns = new List<(SemanticRealizationLayout Layout, ObservedSurfacePattern Pattern)>();
        foreach (var layout in supportLayouts)
        {
            if (TryBuildObservedSurfacePattern(layout, out var pattern))
                observedPatterns.Add((layout, pattern));
        }
        if (observedPatterns.Count == 0)
            return false;

        // Only literal scaffolds independently repeated across three Founder
        // families are eligible. A one-off phrase cannot become syntax merely
        // because it appears in a single response example.
        var patternGroups = observedPatterns
            .GroupBy(item => item.Pattern.Signature, StringComparer.Ordinal)
            .Select(group => new
            {
                Items = group.ToList(),
                IndependentFamilies = group.Select(item => item.Layout.FamilyId).Distinct().Count()
            })
            .Where(group => group.IndependentFamilies >= 3)
            .OrderBy(group => LegendLanguageIdentity.TextHash(seed + "|" + group.Items[0].Pattern.Signature), StringComparer.Ordinal)
            .ToArray();
        if (patternGroups.Length == 0)
            return false;

        var stored = storedEndpoints
            .Select(item => LegendLanguageIdentity.NormalizeText(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var patternGroup in patternGroups)
        {
            var pattern = patternGroup.Items[0].Pattern;
            if (pattern.Gaps.Count != componentCount + 1)
                continue;

            var alternativesByPosition = new List<string[]>(componentCount);
            var valid = true;
            for (var position = 0; position < componentCount; position++)
            {
                var dimension = supportLayouts[0].Components[position].Dimension;
                string[] alternatives;

                if (dynamicDimensions.TryGetValue(dimension, out var variable))
                {
                    if (!bindings.TryGetValue(variable, out var boundValue) ||
                        string.IsNullOrWhiteSpace(boundValue))
                    {
                        valid = false;
                        break;
                    }

                    // Variable content does not need three duplicate copies of
                    // the same domain/fact. Its exact semantic value has already
                    // been governed by the selected transition. We require an
                    // exact Founder-approved lexical surface for that value at
                    // this same structural slot.
                    alternatives = allLayouts
                        .Where(item => item.Components.Count > position &&
                            string.Equals(item.Components[position].Dimension, dimension, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.Components[position].Value, boundValue, StringComparison.OrdinalIgnoreCase))
                        .Select(item => LegendLanguageIdentity.NormalizeText(item.Components[position].SurfaceForm))
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => LegendLanguageIdentity.TextHash(item), StringComparer.Ordinal)
                        .ThenBy(item => item, StringComparer.Ordinal)
                        .ToArray();
                }
                else
                {
                    // A static lexical realization becomes reusable only when
                    // the exact surface is observed at this slot in at least
                    // three independent Founder families.
                    alternatives = supportLayouts
                        .GroupBy(
                            item => LegendLanguageIdentity.NormalizeText(item.Components[position].SurfaceForm),
                            StringComparer.Ordinal)
                        .Where(group =>
                            !string.IsNullOrWhiteSpace(group.Key) &&
                            group.Select(item => item.FamilyId).Distinct().Count() >= 3)
                        .Select(group => group.Key)
                        .OrderBy(item => LegendLanguageIdentity.TextHash(item), StringComparer.Ordinal)
                        .ThenBy(item => item, StringComparer.Ordinal)
                        .ToArray();
                }

                if (alternatives.Length == 0)
                {
                    valid = false;
                    break;
                }
                alternativesByPosition.Add(alternatives);
            }
            if (!valid)
                continue;

            const long maximumCombinations = 4096;
            long combinationCount = 1;
            foreach (var alternatives in alternativesByPosition)
            {
                if (combinationCount >= maximumCombinations)
                    break;
                combinationCount = Math.Min(maximumCombinations, combinationCount * alternatives.Length);
            }
            if (combinationCount <= 0)
                continue;

            var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed + "|" + pattern.Signature));
            var startingOrdinal = (long)(BitConverter.ToUInt64(seedBytes, 0) % (ulong)combinationCount);
            for (long attempt = 0; attempt < combinationCount; attempt++)
            {
                var ordinal = (startingOrdinal + attempt) % combinationCount;
                var cursor = ordinal;
                var selected = new string[componentCount];
                for (var position = 0; position < componentCount; position++)
                {
                    var alternatives = alternativesByPosition[position];
                    selected[position] = alternatives[(int)(cursor % alternatives.Length)];
                    cursor /= alternatives.Length;
                }

                var builder = new StringBuilder();
                for (var position = 0; position < componentCount; position++)
                {
                    builder.Append(pattern.Gaps[position]);
                    builder.Append(selected[position]);
                }
                builder.Append(pattern.Gaps[componentCount]);
                if (!string.IsNullOrWhiteSpace(patternGroup.Items[0].Layout.TerminalPunctuation))
                    builder.Append(patternGroup.Items[0].Layout.TerminalPunctuation);

                var candidate = LegendLanguageIdentity.NormalizeText(builder.ToString());
                if (string.IsNullOrWhiteSpace(candidate) || stored.Contains(candidate))
                    continue;

                text = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildObservedSurfacePattern(
        SemanticRealizationLayout layout,
        out ObservedSurfacePattern pattern)
    {
        pattern = null!;
        if (layout.Components.Count < 2)
            return false;

        var normalized = LegendLanguageIdentity.NormalizeText(layout.ObservedText);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var punctuation = layout.TerminalPunctuation ?? string.Empty;
        var bodyLength = normalized.Length;
        if (!string.IsNullOrWhiteSpace(punctuation) &&
            normalized.EndsWith(punctuation, StringComparison.Ordinal))
        {
            bodyLength -= punctuation.Length;
        }
        if (bodyLength <= 0)
            return false;

        var tokens = SurfaceComponents(normalized);
        if (tokens.Count == 0)
            return false;

        var ordered = layout.Components
            .OrderBy(item => item.StartTokenIndex)
            .ThenBy(item => item.Dimension, StringComparer.Ordinal)
            .ToArray();
        var gaps = new string[ordered.Length + 1];
        var cursor = 0;
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var signature = new StringBuilder();
        for (var index = 0; index < ordered.Length; index++)
        {
            var component = ordered[index];
            if (component.StartTokenIndex < 0 || component.TokenLength < 1 ||
                component.StartTokenIndex + component.TokenLength > tokens.Count)
            {
                return false;
            }

            var first = tokens[component.StartTokenIndex];
            var last = tokens[component.StartTokenIndex + component.TokenLength - 1];
            var start = first.CharacterOffset;
            var end = last.CharacterOffset + last.CharacterLength;
            if (start < cursor || end > bodyLength)
                return false;

            gaps[index] = normalized[cursor..start];
            occurrences.TryGetValue(component.Dimension, out var prior);
            var occurrence = prior + 1;
            occurrences[component.Dimension] = occurrence;
            signature.Append(gaps[index]);
            signature.Append('{').Append(component.Dimension).Append(':').Append(occurrence).Append('}');
            cursor = end;
        }

        if (cursor > bodyLength)
            return false;
        gaps[^1] = normalized[cursor..bodyLength];
        signature.Append(gaps[^1]);
        pattern = new ObservedSurfacePattern(signature.ToString(), gaps);
        return true;
    }

    private sealed record ObservedSurfacePattern(
        string Signature,
        IReadOnlyList<string> Gaps);

'''
if text.count(marker) != 1:
    raise SystemExit(f"surface lattice helper marker: found {text.count(marker)}")
text = text.replace(marker, helper + marker, 1)

P.write_text(text)
