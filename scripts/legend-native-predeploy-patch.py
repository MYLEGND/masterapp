from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one replacement target, found {count}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


curriculum = "Infrastructure/Messaging/LegendConnectCurriculum.cs"
replace_once(
    curriculum,
    """    internal async Task<LegendSemanticTransitionInference> TryInferComposedSemanticTransitionAsync(\n        string sourceLanguageCode,\n        string input,\n        LegendConnectDiscourseStateSnapshot? discourseState,\n        CancellationToken cancellationToken = default)\n""",
    """    internal async Task<LegendSemanticTransitionInference> TryInferComposedSemanticTransitionAsync(\n        string sourceLanguageCode,\n        string input,\n        IReadOnlyList<LegendConnectConversationContextItem> context,\n        LegendConnectDiscourseStateSnapshot? discourseState,\n        CancellationToken cancellationToken = default)\n""")
replace_once(
    curriculum,
    """            sourceLanguageCode,\n            input,\n            [],\n            discourseState,\n            requireComposedGraph: true,\n""",
    """            sourceLanguageCode,\n            input,\n            context,\n            discourseState,\n            requireComposedGraph: true,\n""")
replace_once(
    curriculum,
    """            IReadOnlyList<GroundedContextFrame> contextFrames;\n            if (discourseState is not null)\n            {\n                contextFrames = ResolveGroundedContextFramesFromDiscourseState(\n                    discourseState, observations);\n            }\n            else\n            {\n                contextFrames = await ResolveGroundedContextFramesAsync(\n                    language, context, observations, cancellationToken);\n            }\n            candidates = BindCandidatesFromGroundedContext(partialCandidates, contextFrames);\n""",
    """            // Persisted discourse and caller-supplied context are two\n            // evidence views of the same conversation. Feed both through the\n            // one grounded-context authority; do not let persistence timing\n            // make the native planner forget context already present in the\n            // serving request. BindCandidatesFromGroundedContext canonicalizes\n            // duplicate bindings before transition selection.\n            var contextFrames = new List<GroundedContextFrame>();\n            if (discourseState is not null)\n            {\n                contextFrames.AddRange(ResolveGroundedContextFramesFromDiscourseState(\n                    discourseState, observations));\n            }\n            contextFrames.AddRange(await ResolveGroundedContextFramesAsync(\n                language, context, observations, cancellationToken));\n            candidates = BindCandidatesFromGroundedContext(partialCandidates, contextFrames);\n""")

replace_once(
    curriculum,
    """        var components = BuildSemanticLayoutComponents(example, anchors, frameDimensions);\n        if (components.Count < 2 ||\n            components.Zip(components.Skip(1), (left, right) =>\n                    left.StartTokenIndex + left.TokenLength > right.StartTokenIndex)\n                .Any(item => item))\n""",
    """        var components = BuildSemanticLayoutComponents(example, anchors, frameDimensions);\n\n        // Some valid Founder curricula intentionally govern an entire response\n        // as one semantic span instead of assigning artificial semantics to\n        // each clause. When that one exact lexical span covers the complete\n        // response, derive only its observed punctuation-delimited surface\n        // segments and feed them back into this same realization authority.\n        // No segment receives invented meaning; the whole-span semantic frame\n        // remains the authority and three independent families still have to\n        // support one structural layout before any recombination can serve.\n        if (components.Count == 1 &&\n            TryExpandWholeSpanSurfaceRealizationLayout(\n                example, components[0], out var surfaceComponents))\n        {\n            components = surfaceComponents;\n        }\n\n        if (components.Count < 2 ||\n            components.Zip(components.Skip(1), (left, right) =>\n                    left.StartTokenIndex + left.TokenLength > right.StartTokenIndex)\n                .Any(item => item))\n""")

marker = "    private static bool TryRealizeOriginalLearnedLayout(\n"
helper = r'''    /// <summary>
    /// Refines one exact whole-response semantic anchor into observed surface
    /// segments only when internal punctuation supplies an explicit boundary.
    /// This is representation refinement inside the canonical realization
    /// authority: it does not create semantic nodes, templates, phrase rules,
    /// response caches, or another responder.
    /// </summary>
    private static bool TryExpandWholeSpanSurfaceRealizationLayout(
        SemanticResultExample example,
        SemanticLayoutComponent wholeSpan,
        out List<SemanticLayoutComponent> components)
    {
        components = [];
        var normalized = LegendLanguageIdentity.NormalizeText(example.Text);
        var tokens = SurfaceComponents(normalized);
        if (tokens.Count < 2 ||
            wholeSpan.StartTokenIndex != 0 ||
            wholeSpan.TokenLength != tokens.Count)
        {
            return false;
        }

        var terminalPunctuation = TerminalPunctuation(normalized);
        var bodyLength = normalized.Length - terminalPunctuation.Length;
        if (bodyLength <= 0)
            return false;

        var starts = new List<int> { 0 };
        var ends = new List<int>();
        for (var index = 0; index < bodyLength; index++)
        {
            var character = normalized[index];
            if (character is not ('!' or '?' or '.' or ';' or ':') ||
                index + 1 >= bodyLength)
            {
                continue;
            }

            var next = index + 1;
            while (next < bodyLength && char.IsWhiteSpace(normalized[next]))
                next++;
            if (next >= bodyLength)
                continue;

            ends.Add(index + 1);
            starts.Add(next);
            index = next - 1;
        }
        ends.Add(bodyLength);
        if (starts.Count < 2 || starts.Count != ends.Count)
            return false;

        var tokenCursor = 0;
        for (var index = 0; index < starts.Count; index++)
        {
            var surface = normalized[starts[index]..ends[index]].Trim();
            if (string.IsNullOrWhiteSpace(surface))
                return false;
            var segmentTokenCount = SurfaceComponents(surface).Count;
            if (segmentTokenCount <= 0)
                return false;

            components.Add(new SemanticLayoutComponent(
                wholeSpan.Dimension + "#surface-" + (index + 1),
                wholeSpan.Value,
                tokenCursor,
                segmentTokenCount,
                surface));
            tokenCursor += segmentTokenCount;
        }

        if (tokenCursor != wholeSpan.TokenLength)
        {
            components = [];
            return false;
        }
        return components.Count >= 2;
    }

'''
p = Path(curriculum)
text = p.read_text(encoding="utf-8")
if text.count(marker) != 1:
    raise SystemExit("realization helper insertion marker changed")
text = text.replace(marker, helper + marker, 1)
group_old = "            .GroupBy(item => item.Shape, StringComparer.Ordinal)\n"
if text.count(group_old) != 2:
    raise SystemExit(f"expected two realization shape groupings, found {text.count(group_old)}")
text = text.replace(
    group_old,
    """            .GroupBy(\n                item => item.Shape + "\\u001f" + item.TerminalPunctuation,\n                StringComparer.Ordinal)\n""")
p.write_text(text, encoding="utf-8")

operations = "Infrastructure/Messaging/LegendConnectOperations.cs"
replace_once(
    operations,
    """        var composed = await Curriculum.TryInferComposedSemanticTransitionAsync(\n            "en", input ?? string.Empty, discourseState, cancellationToken);\n""",
    """        var composed = await Curriculum.TryInferComposedSemanticTransitionAsync(\n            "en", input ?? string.Empty, context, discourseState, cancellationToken);\n""")

sql_tests = "AgentPortal.Tests/LegendConnectFounderSemanticTransformationSqlTests.cs"
test_text = Path(sql_tests).read_text(encoding="utf-8")
old = """            var composedInitial = await proofServices.Curriculum.TryInferComposedSemanticTransitionAsync(\n                "en",\n                establishingRequest,\n                new LegendConnectDiscourseStateSnapshot([]));\n"""
new = """            var composedInitial = await proofServices.Curriculum.TryInferComposedSemanticTransitionAsync(\n                "en",\n                establishingRequest,\n                [],\n                new LegendConnectDiscourseStateSnapshot([]));\n"""
if test_text.count(old) != 1:
    raise SystemExit("initial direct composed test call target changed")
test_text = test_text.replace(old, new, 1)
old = """            var composedHeldOut = await proofServices.Curriculum.TryInferComposedSemanticTransitionAsync(\n                "en",\n                heldOutRequest,\n                state);\n"""
new = """            var composedHeldOut = await proofServices.Curriculum.TryInferComposedSemanticTransitionAsync(\n                "en",\n                heldOutRequest,\n                [],\n                state);\n"""
if test_text.count(old) != 1:
    raise SystemExit("held-out direct composed test call target changed")
Path(sql_tests).write_text(test_text.replace(old, new, 1), encoding="utf-8")

tests = "AgentPortal.Tests/LegendConnectSemanticSpanGroundingTests.cs"
p = Path(tests)
text = p.read_text(encoding="utf-8")
test_marker = "    [Fact]\n    public async Task GovernedExecutableProjection_FailsClosedWhenOmittedMetadataCouldChangeTheResult()\n"
test_method = '''    [Fact]
    public async Task WholeSpanFounderResponses_DecomposeObservedSurfaceStructure_WithoutStoredResponseRetrieval()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                WholeSpanArticulationFamily(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var storedResults = await db.LegendCurriculumExamples
            .Where(item => item.SupersededUtc == null &&
                db.LegendCurriculumExampleVariations.Any(variation =>
                    variation.CurriculumExampleId == item.Id &&
                    variation.Dimension == "conversation_function" &&
                    variation.Value == "conversation_acknowledgement"))
            .Join(db.LegendLanguageTextUnits, example => example.TextUnitId, unit => unit.Id, (_, unit) => unit.Text)
            .ToArrayAsync();

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Good morning.", [], new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("semantic_transition_governed_composed", native.ReasonCode);
        Assert.True(native.EvidenceCount >= 3);
        Assert.False(string.IsNullOrWhiteSpace(native.Answer));
        Assert.DoesNotContain(storedResults, stored => string.Equals(
            LegendLanguageIdentity.NormalizeText(stored),
            LegendLanguageIdentity.NormalizeText(native.Answer!),
            StringComparison.Ordinal));
        Assert.Contains("!", native.Answer!, StringComparison.Ordinal);
        Assert.EndsWith("?", native.Answer, StringComparison.Ordinal);
        Assert.False(native.RequiresEscalation);
    }

'''
if text.count(test_marker) != 1:
    raise SystemExit("whole-span test insertion marker changed")
text = text.replace(test_marker, test_method + test_marker, 1)
helper_marker = "    private static LegendConnectCurriculumBatchSubmission\n        RepeatedSemanticSlotArticulationFamily(int family)\n"
family_helper = '''    private static LegendConnectCurriculumBatchSubmission
        WholeSpanArticulationFamily(int family)
    {
        var resultText = family switch
        {
            1 => "Hello! What can I help you with?",
            2 => "Welcome! How may I support you?",
            _ => "Greetings! What should we work on?"
        };

        return new LegendConnectCurriculumBatchSubmission(
            $"response.whole-span-articulation.{family}",
            "Founder-controlled whole-span response articulation evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder opening evidence {family}: Good morning.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "conversation_opening"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "opening", "conversation_function", "conversation_opening", "Good morning")
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    resultText,
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "conversation_acknowledgement"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "response", "conversation_function", "conversation_acknowledgement", resultText)
                    ],
                    []))
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "conversation_opening"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "conversation_acknowledgement"
                }))]);
    }

'''
if text.count(helper_marker) != 1:
    raise SystemExit("whole-span family helper insertion marker changed")
p.write_text(text.replace(helper_marker, family_helper + helper_marker, 1), encoding="utf-8")
