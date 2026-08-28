from pathlib import Path

P = Path("Infrastructure/Messaging/LegendConnectCurriculum.cs")
text = P.read_text()


def once(old: str, new: str, label: str):
    global text
    n = text.count(old)
    if n != 1:
        raise SystemExit(f"{label}: expected 1 anchor, found {n}")
    text = text.replace(old, new, 1)

# The realization layout remains an ephemeral view over the existing Founder
# endpoints. Retaining its observed text in-memory lets the same authority
# prove inter-slot surface grammar; it is not a persisted template store.
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

# The previous live-gap patch has already removed token geometry from the bound
# layout shape. Preserve that correction while adding the observed surface.
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

# Variable-bearing result frames need a different original-articulation proof
# than static frames. A cross-family layout may legitimately carry different
# values for the variable slot; requiring identical values made all such
# independently supported layouts unusable. Keep the existing static composer
# unchanged and route only variable-bearing layouts through the governed
# surface-pattern proof below.
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
                    ? TryRealizeOriginalGovernedSurfacePattern(
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
    /// Realizes one variable-bearing response from a surface grammar pattern
    /// that recurs across at least three independent Founder families. Literal
    /// inter-slot material is admitted only as part of that independently
    /// repeated observed pattern; semantic slots still come exclusively from
    /// exact Founder anchors. The method is read-only, persists no template,
    /// and rejects every supplied stored endpoint.
    /// </summary>
    private static bool TryRealizeOriginalGovernedSurfacePattern(
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

        var dynamicDimensions = resultFrame.Dimensions
            .Where(item => IsSemanticVariable(item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (dynamicDimensions.Count == 0)
            return false;

        var patterns = new List<(SemanticRealizationLayout Layout, ObservedSurfacePattern Pattern)>();
        foreach (var layout in supportLayouts)
        {
            if (TryBuildObservedSurfacePattern(layout, out var pattern))
                patterns.Add((layout, pattern));
        }
        if (patterns.Count == 0)
            return false;

        var stored = storedEndpoints
            .Select(item => LegendLanguageIdentity.NormalizeText(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var patternGroup in patterns
                     .GroupBy(item => item.Pattern.Signature, StringComparer.Ordinal)
                     .Select(group => new
                     {
                         Items = group.ToList(),
                         IndependentFamilies = group.Select(item => item.Layout.FamilyId).Distinct().Count()
                     })
                     .Where(group => group.IndependentFamilies >= 3)
                     .OrderBy(group => LegendLanguageIdentity.TextHash(seed + "|" + group.Items[0].Pattern.Signature), StringComparer.Ordinal))
        {
            var reference = patternGroup.Items[0];
            var componentCount = reference.Layout.Components.Count;
            if (patternGroup.Items.Any(item => item.Layout.Components.Count != componentCount))
                continue;

            var alternativesByPosition = new List<string[]>(componentCount);
            var valid = true;
            for (var position = 0; position < componentCount; position++)
            {
                var referenceComponent = reference.Layout.Components[position];
                if (patternGroup.Items.Any(item => !string.Equals(
                        item.Layout.Components[position].Dimension,
                        referenceComponent.Dimension,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    valid = false;
                    break;
                }

                string[] alternatives;
                if (dynamicDimensions.TryGetValue(referenceComponent.Dimension, out var variable))
                {
                    if (!bindings.TryGetValue(variable, out var boundValue) || string.IsNullOrWhiteSpace(boundValue))
                    {
                        valid = false;
                        break;
                    }

                    var occurrence = reference.Layout.Components
                        .Take(position + 1)
                        .Count(item => string.Equals(item.Dimension, referenceComponent.Dimension, StringComparison.OrdinalIgnoreCase)) - 1;
                    alternatives = allLayouts
                        .Select(layout => layout.Components
                            .Where(item => string.Equals(item.Dimension, referenceComponent.Dimension, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(item.Value, boundValue, StringComparison.OrdinalIgnoreCase))
                            .Skip(occurrence)
                            .FirstOrDefault())
                        .Where(item => item is not null)
                        .Select(item => LegendLanguageIdentity.NormalizeText(item!.SurfaceForm))
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => LegendLanguageIdentity.TextHash(item), StringComparer.Ordinal)
                        .ThenBy(item => item, StringComparer.Ordinal)
                        .ToArray();
                }
                else
                {
                    var semanticValues = patternGroup.Items
                        .Select(item => item.Layout.Components[position].Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (semanticValues.Length != 1)
                    {
                        valid = false;
                        break;
                    }
                    alternatives = patternGroup.Items
                        .Select(item => LegendLanguageIdentity.NormalizeText(item.Layout.Components[position].SurfaceForm))
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.Ordinal)
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
                combinationCount = Math.Min(maximumCombinations, combinationCount * alternatives.Length);
            if (combinationCount <= 0)
                continue;

            var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed + "|" + reference.Pattern.Signature));
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
                    builder.Append(reference.Pattern.Gaps[position]);
                    builder.Append(selected[position]);
                }
                builder.Append(reference.Pattern.Gaps[componentCount]);
                if (!string.IsNullOrWhiteSpace(reference.Layout.TerminalPunctuation))
                    builder.Append(reference.Layout.TerminalPunctuation);

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
        if (!string.IsNullOrWhiteSpace(punctuation) && normalized.EndsWith(punctuation, StringComparison.Ordinal))
            bodyLength -= punctuation.Length;
        if (bodyLength <= 0)
            return false;

        var tokens = SurfaceComponents(normalized);
        if (tokens.Count == 0)
            return false;

        var ordered = layout.Components.OrderBy(item => item.StartTokenIndex).ToArray();
        var gaps = new string[ordered.Length + 1];
        var cursor = 0;
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var signature = new StringBuilder();
        for (var index = 0; index < ordered.Length; index++)
        {
            var component = ordered[index];
            if (component.StartTokenIndex < 0 || component.TokenLength < 1 ||
                component.StartTokenIndex + component.TokenLength > tokens.Count)
                return false;
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
            signature.Append("{").Append(component.Dimension).Append(":").Append(occurrence).Append("}");
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
    raise SystemExit(f"surface pattern helper marker: found {text.count(marker)}")
text = text.replace(marker, helper + marker, 1)

P.write_text(text)
