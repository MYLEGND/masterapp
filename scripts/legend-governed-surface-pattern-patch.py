from pathlib import Path

P = Path("Infrastructure/Messaging/LegendConnectCurriculum.cs")
text = P.read_text()


def once(old: str, new: str, label: str):
    global text
    n = text.count(old)
    if n != 1:
        raise SystemExit(f"{label}: expected 1 anchor, found {n}")
    text = text.replace(old, new, 1)

# Keep the observed Founder sentence only inside the ephemeral realization
# layout. This gives the existing articulation authority a language-native
# scaffold to recombine; it creates no persisted template, response cache, or
# second responder.
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

# The live-gap patch has already removed sentence-specific token geometry from
# bound layout identity. Preserve that correction while carrying observed text.
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

# The selected result frame is already unique and governed. The layout group is
# already required to recur across >=3 independent Founder families. The old
# original composer incorrectly required a semantic VARIABLE VALUE to be the
# same across those families, which defeats the purpose of a variable. Route
# only variable-bearing results through a variable-aware recomposition that
# preserves the same cross-family shape gate and substitutes only exact governed
# values into an observed language-native scaffold.
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
                    ? TryRealizeOriginalVariableAwareLayout(
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
    /// Produces an original response for a variable-bearing result without
    /// weakening the existing cross-family articulation gate. The semantic
    /// slot order/scaffold must already be supported by at least three Founder
    /// families (enforced by the caller's eligible layout group). Static result
    /// semantics remain fixed. Only declared variable slots may change, and
    /// each replacement surface must be an exact Founder-approved lexical
    /// anchor for the exact bound semantic value. Every active curriculum
    /// sentence is still rejected by the caller.
    /// </summary>
    private static bool TryRealizeOriginalVariableAwareLayout(
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

        // Re-prove the invariant that makes the scaffold reusable. The caller
        // already groups by shape, but this method is intentionally defensive.
        if (supportLayouts.Select(item => item.FamilyId).Distinct().Count() < 3)
            return false;
        var shape = supportLayouts[0].Shape;
        if (supportLayouts.Any(item =>
                !string.Equals(item.Shape, shape, StringComparison.Ordinal) ||
                item.Components.Count != supportLayouts[0].Components.Count))
        {
            return false;
        }

        var dynamicDimensions = resultFrame.Dimensions
            .Where(item => IsSemanticVariable(item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (dynamicDimensions.Count == 0)
            return false;

        // Static slots must carry exactly the selected static semantic value in
        // every independently supporting family. Variable slots may vary by
        // design but may not change dimension or occurrence position.
        for (var position = 0; position < supportLayouts[0].Components.Count; position++)
        {
            var reference = supportLayouts[0].Components[position];
            var aligned = supportLayouts.Select(item => item.Components[position]).ToArray();
            if (aligned.Any(item => !string.Equals(
                    item.Dimension,
                    reference.Dimension,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (dynamicDimensions.ContainsKey(reference.Dimension))
                continue;

            if (!resultFrame.Dimensions.TryGetValue(reference.Dimension, out var expected) ||
                IsSemanticVariable(expected) ||
                aligned.Any(item => !string.Equals(item.Value, expected, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // Resolve every variable slot to an exact observed surface for its exact
        // bound value. A value may be domain-specific and occur in only one
        // curriculum family; independence is required for the reusable
        // articulation STRUCTURE, not for duplicating every possible content
        // value three times.
        var boundSurfaces = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var dynamic in dynamicDimensions)
        {
            if (!bindings.TryGetValue(dynamic.Value, out var boundValue) ||
                string.IsNullOrWhiteSpace(boundValue))
            {
                return false;
            }

            var surfaces = allLayouts
                .SelectMany(item => item.Components)
                .Where(item =>
                    string.Equals(item.Dimension, dynamic.Key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Value, boundValue, StringComparison.OrdinalIgnoreCase))
                .Select(item => LegendLanguageIdentity.NormalizeText(item.SurfaceForm))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => LegendLanguageIdentity.TextHash(item), StringComparer.Ordinal)
                .ThenBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (surfaces.Length == 0)
                return false;
            boundSurfaces[dynamic.Key] = surfaces;
        }

        var stored = storedEndpoints
            .Select(item => LegendLanguageIdentity.NormalizeText(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);

        // Prefer independently supported scaffolds deterministically. Each
        // scaffold is an actual Founder-authored sentence whose semantic slot
        // order belongs to the >=3-family group. Replacing only declared
        // variable spans preserves native grammar without inventing a template.
        var scaffolds = supportLayouts
            .OrderBy(item => LegendLanguageIdentity.TextHash(seed + "|" + item.ObservedText), StringComparer.Ordinal)
            .ThenBy(item => item.ObservedText, StringComparer.Ordinal)
            .ToArray();

        foreach (var scaffold in scaffolds)
        {
            var normalized = LegendLanguageIdentity.NormalizeText(scaffold.ObservedText);
            var tokens = SurfaceComponents(normalized);
            if (tokens.Count == 0)
                continue;

            var dynamicComponents = scaffold.Components
                .Where(item => dynamicDimensions.ContainsKey(item.Dimension))
                .OrderBy(item => item.StartTokenIndex)
                .ThenBy(item => item.Dimension, StringComparer.Ordinal)
                .ToArray();
            if (dynamicComponents.Length == 0)
                continue;

            // Compute one deterministic choice per bound semantic value, then
            // splice from right to left so earlier character offsets remain
            // stable. Multiple occurrences of the same variable share the same
            // governed value surface.
            var replacements = new List<(int Start, int End, string Surface)>();
            var valid = true;
            foreach (var component in dynamicComponents)
            {
                if (component.StartTokenIndex < 0 || component.TokenLength < 1 ||
                    component.StartTokenIndex + component.TokenLength > tokens.Count ||
                    !boundSurfaces.TryGetValue(component.Dimension, out var alternatives) ||
                    alternatives.Length == 0)
                {
                    valid = false;
                    break;
                }

                var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(
                    seed + "|" + scaffold.Shape + "|" + component.Dimension));
                var selected = alternatives[(int)(BitConverter.ToUInt64(seedBytes, 0) % (ulong)alternatives.Length)];
                var first = tokens[component.StartTokenIndex];
                var last = tokens[component.StartTokenIndex + component.TokenLength - 1];
                replacements.Add((
                    first.CharacterOffset,
                    last.CharacterOffset + last.CharacterLength,
                    selected));
            }
            if (!valid)
                continue;

            var builder = new StringBuilder(normalized);
            foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            {
                if (replacement.Start < 0 || replacement.End < replacement.Start || replacement.End > builder.Length)
                {
                    valid = false;
                    break;
                }
                builder.Remove(replacement.Start, replacement.End - replacement.Start);
                builder.Insert(replacement.Start, replacement.Surface);
            }
            if (!valid)
                continue;

            var candidate = LegendLanguageIdentity.NormalizeText(builder.ToString());
            if (string.IsNullOrWhiteSpace(candidate) || stored.Contains(candidate))
                continue;

            text = candidate;
            return true;
        }

        return false;
    }

'''
if text.count(marker) != 1:
    raise SystemExit(f"variable-aware articulation helper marker: found {text.count(marker)}")
text = text.replace(marker, helper + marker, 1)

P.write_text(text)
