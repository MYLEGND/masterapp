using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Messaging;
using Domain.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectFoundationControlCompilerTests
{
    [Theory]
    [InlineData("mark", 3, 1, "1/3")]
    [InlineData("remove", 2, 0, "0")]
    public void StateLabelsAndMembershipHaveDistinctComputedEffects(string operation, int active, int marked, string share)
    {
        var oracle = StateOracle(3, [1, 2, 3], [], (operation, 2));
        Assert.True(LegendFoundationConversationControl.TryEvaluateSetStateOracle(oracle, out var instruction, out var target, out _));
        Assert.Equal(JsonSerializer.Serialize(new { active_count = active, marked_active_count = marked, marked_share = share }), target);
        var source = StateSource(oracle, "independent-state-case");
        Assert.True(LegendFoundationConversationControl.TryValidateOracle(source, target, "en"));
        Assert.False(LegendFoundationConversationControl.TryValidateOracle(source, target, "ht"));
        Assert.False(LegendFoundationConversationControl.TryValidateOracle(source, target.Replace("active_count", "invented_count"), "en"));
        Assert.Contains("never changes membership", instruction);
    }

    [Fact]
    public void StateRemovalAndReactivationPreserveIndependentLabelsAndHandleEmptyDenominator()
    {
        string Target(JsonElement oracle)
        {
            Assert.True(LegendFoundationConversationControl.TryEvaluateSetStateOracle(oracle, out _, out var target, out _));
            return target;
        }
        Assert.Equal("{\"active_count\":0,\"marked_active_count\":0,\"marked_share\":null}",
            Target(StateOracle(1, [1], [1], ("remove", 1))));
        Assert.Equal("{\"active_count\":1,\"marked_active_count\":1,\"marked_share\":\"1\"}",
            Target(StateOracle(1, [1], [1], ("remove", 1), ("add", 1), ("add", 1), ("mark", 1))));
        Assert.Equal("{\"active_count\":1,\"marked_active_count\":0,\"marked_share\":\"0\"}",
            Target(StateOracle(1, [], [1], ("unmark", 1), ("add", 1))));
    }

    [Fact]
    public void StateScenarioIdentityIgnoresNamesAndSetOrderButPreservesCausalEventOrder()
    {
        var first = StateSource(StateOracle(3, [1, 2], [2, 3], ("remove", 2), ("add", 3), ("mark", 1)), "first");
        // Rename 1->3, 2->1, 3->2 and reorder only the initial sets.
        var renamed = StateSource(StateOracle(3, [1, 3], [2, 1], ("remove", 1), ("add", 2), ("mark", 3)), "cosmetic-name");
        Assert.Equal(LegendFoundationConversationControl.ProblemIdentity(first), LegendFoundationConversationControl.ProblemIdentity(renamed));
        var reactivated = StateSource(StateOracle(2, [1, 2], [1], ("remove", 1), ("add", 1)), "order-a");
        var removed = StateSource(StateOracle(2, [1, 2], [1], ("add", 1), ("remove", 1)), "order-b");
        Assert.NotEqual(LegendFoundationConversationControl.ProblemIdentity(reactivated), LegendFoundationConversationControl.ProblemIdentity(removed));
        using var firstDocument = JsonDocument.Parse(reactivated);
        using var secondDocument = JsonDocument.Parse(removed);
        Assert.True(LegendFoundationConversationControl.TryEvaluateSetStateOracle(firstDocument.RootElement.GetProperty("oracle"), out _, out var firstTarget, out _));
        Assert.True(LegendFoundationConversationControl.TryEvaluateSetStateOracle(secondDocument.RootElement.GetProperty("oracle"), out _, out var secondTarget, out _));
        Assert.NotEqual(firstTarget, secondTarget);
    }

    [Fact]
    public void StateNoOpsAndCommutingInterleavingsCannotManufactureHeldOutIndependence()
    {
        string Identity(JsonElement oracle) => LegendFoundationConversationControl.ProblemIdentity(StateSource(oracle, "cosmetic"));
        Assert.Equal(Identity(StateOracle(1, [1], [], ("mark", 1))),
            Identity(StateOracle(1, [1], [], ("mark", 1), ("mark", 1), ("add", 1))));
        Assert.Equal(Identity(StateOracle(2, [1, 2], [], ("mark", 1), ("remove", 2))),
            Identity(StateOracle(2, [1, 2], [], ("remove", 2), ("mark", 1))));
        Assert.Equal(Identity(StateOracle(1, [1], [], ("mark", 1), ("remove", 1))),
            Identity(StateOracle(1, [1], [], ("remove", 1), ("mark", 1))));
        Assert.False(LegendFoundationConversationControl.TryEvaluateSetStateOracle(
            StateOracle(2, [1], [], ("remove", 2)), out _, out _, out _));
    }

    [Theory]
    [InlineData("{\"kind\":\"set_state\",\"entities\":5,\"active\":[1],\"marked\":[],\"events\":[]}")]
    [InlineData("{\"kind\":\"set_state\",\"entities\":2,\"active\":[1],\"marked\":[],\"events\":[]}")]
    [InlineData("{\"kind\":\"set_state\",\"entities\":1,\"active\":[1,1],\"marked\":[],\"events\":[]}")]
    [InlineData("{\"kind\":\"set_state\",\"entities\":1,\"active\":[0],\"marked\":[],\"events\":[]}")]
    [InlineData("{\"kind\":\"set_state\",\"entities\":1,\"active\":[true],\"marked\":[],\"events\":[]}")]
    [InlineData("{\"kind\":\"set_state\",\"entities\":1,\"active\":[1],\"marked\":[],\"events\":[{\"operation\":\"delete-everything\",\"entity\":1}]}")]
    [InlineData("{\"kind\":\"set_state\",\"entities\":1,\"active\":[1],\"marked\":[],\"events\":[],\"expected\":\"approve me\"}")]
    public void StateOracleRejectsInvalidOrSelfAuthorizingDeclarations(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        Assert.False(LegendFoundationConversationControl.TryEvaluateSetStateOracle(parsed.RootElement, out _, out _, out _));
        Assert.False(LegendFoundationConversationControl.TryEvaluateSetStateOracle(
            StateOracle(1, [1], [], Enumerable.Repeat(("mark", 1), 7).ToArray()), out _, out _, out _));
    }

    [Theory]
    [InlineData("{\"marked_share\":\"1/2\", \"active_count\":2, \"marked_active_count\":1}", true)]
    [InlineData("{\"active_count\":1,\"marked_active_count\":1,\"marked_share\":\"1\"}", false)]
    [InlineData("{\"active_count\":2,\"marked_active_count\":1,\"marked_share\":null}", false)]
    [InlineData("{\"active_count\":2,\"marked_active_count\":1,\"marked_share\":0.5}", false)]
    [InlineData("{\"active_count\":2,\"marked_active_count\":1,\"marked_share\":\"1/2\",\"actually_removed\":true}", false)]
    [InlineData("{\"active_count\":2,\"active_count\":2,\"marked_active_count\":1,\"marked_share\":\"1/2\"}", false)]
    public async Task StateJudgeVerifiesRelationsAndRejectsContradictoryExtraFields(string actual, bool correct)
    {
        var oracle = StateOracle(2, [1, 2], [], ("mark", 1));
        Assert.True(LegendFoundationConversationControl.TryEvaluateSetStateOracle(oracle, out _, out var target, out _));
        var source = StateSource(oracle, "state-judge");
        var example = new LegendConnectTrainingDatasetExample("state-evidence", "en:en", "en", "en", source, target,
            "SystemValidatedMachine", 3, "source-hash", "target-hash", "foundation.conversation", OutputContract: LegendFoundationConversationControl.OutputContract);
        var judged = await new LocalLegendConnectModelEvaluationBackend().JudgeAsync(new(example, actual, target, BaselineModelText: target));
        Assert.True(judged.Succeeded);
        Assert.Equal(correct ? 1m : 0m, judged.ChallengerScore);
        Assert.Equal(1m, judged.BaselineScore);
    }

    [Fact]
    public async Task CompiledStateRenamingsCannotCrossTrainingAndHeldOutPartitions()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.Add(new LegendConnectRuntimePolicy { ScopeKey = "Global", LanguageIntelligenceReevaluationPhase = "Complete",
            CompletedLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            TargetLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current });
        var targets = new Dictionary<string, LegendLanguageTextUnit>(StringComparer.Ordinal);
        var declarations = new[]
        {
            StateOracle(2, [1, 2], [], ("mark", 1)),
            StateOracle(2, [2, 1], [], ("mark", 2)),
            StateOracle(2, [1, 2], [], ("mark", 1), ("mark", 1), ("add", 1)),
            StateOracle(2, [1, 2], [], ("remove", 1))
        };
        for (var index = 0; index < declarations.Length; index++)
        {
            var declaration = declarations[index];
            var sourceText = StateSource(declaration, "state-partition-" + index);
            Assert.True(LegendFoundationConversationControl.TryEvaluateSetStateOracle(declaration, out _, out var targetText, out _));
            var family = new LegendCurriculumFamily { FamilyKey = "state-partition-" + index,
                SemanticCategory = "foundation.conversation", Provenance = "FounderApproved" };
            var source = new LegendLanguageTextUnit { Text = sourceText, NormalizedHash = LegendLanguageIdentity.TextHash(sourceText),
                LanguageCode = "en", Provenance = "FounderApproved", IsTrainingEligible = true };
            if (!targets.TryGetValue(targetText, out var target))
            {
                target = new LegendLanguageTextUnit { Text = targetText, NormalizedHash = LegendLanguageIdentity.TextHash(targetText),
                    LanguageCode = "en", Provenance = "SystemValidatedMachine", IsTrainingEligible = true };
                targets.Add(targetText, target);
                db.Add(target);
            }
            var original = new LegendCurriculumExample { CurriculumFamilyId = family.Id, TextUnitId = source.Id,
                LanguageCode = "en", Provenance = "FounderApproved" };
            db.AddRange(family, source, original, new LegendCurriculumExample { CurriculumFamilyId = family.Id,
                TextUnitId = target.Id, DerivedFromCurriculumExampleId = original.Id, LanguageCode = "en", Provenance = "SystemValidatedMachine" });
        }
        await db.SaveChangesAsync();
        var manifest = await new LegendConnectTrainingDatasetCompiler(db, LegendModelTrainingTestConfiguration.Hosted).CompileAsync();
        var examples = manifest.Training.Concat(manifest.HeldOut).ToArray();
        Assert.Equal(4, examples.Length);
        var marked = examples.Where(example =>
        {
            using var source = JsonDocument.Parse(example.SourceText);
            return source.RootElement.GetProperty("oracle").GetProperty("events")[0].GetProperty("operation").GetString() == "mark";
        }).ToArray();
        Assert.Equal(3, marked.Length);
        var sharedGroup = Assert.Single(marked.Select(example => example.SplitGroupIdentity).Distinct());
        Assert.NotEqual(sharedGroup, Assert.Single(examples.Except(marked)).SplitGroupIdentity);
        Assert.False(manifest.Training.Any(example => example.SplitGroupIdentity == sharedGroup) &&
            manifest.HeldOut.Any(example => example.SplitGroupIdentity == sharedGroup));
        Assert.Empty(manifest.Training.Select(example => example.SplitGroupIdentity).Intersect(
            manifest.HeldOut.Select(example => example.SplitGroupIdentity)));
    }

    [Fact]
    public void StateHeldOutTargetDoesNotEnterTrainingMessagesOrRuntimeInput()
    {
        LegendConnectTrainingDatasetExample Example(JsonElement oracle, string identity)
        {
            Assert.True(LegendFoundationConversationControl.TryEvaluateSetStateOracle(oracle, out _, out var target, out _));
            return new(identity, "en:en", "en", "en", StateSource(oracle, identity), target, "SystemValidatedMachine", 1,
                identity + "-source", identity + "-target", "foundation.conversation", OutputContract: LegendFoundationConversationControl.OutputContract,
                SplitGroupIdentity: identity);
        }
        var training = Example(StateOracle(2, [1, 2], [], ("mark", 1)), "training");
        var heldOut = Example(StateOracle(3, [1, 2, 3], [], ("mark", 2)), "locked");
        var jsonl = Encoding.UTF8.GetString(LegendConnectModelTrainingService.BuildTrainingJsonl(new("dataset", 1, "Global", [training], [heldOut])));
        using var row = JsonDocument.Parse(jsonl);
        var messages = row.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(training.TargetText, messages[^1].GetProperty("content").GetString());
        Assert.DoesNotContain(messages, message => message.GetProperty("content").GetString() == heldOut.TargetText);
        Assert.DoesNotContain(heldOut.TargetText, heldOut.ToTaskRequest().ConversationInput!.Value.GetRawText(), StringComparison.Ordinal);
    }

    private static JsonElement StateOracle(int entities, int[] active, int[] marked, params (string Operation, int Entity)[] events) =>
        JsonSerializer.SerializeToElement(new { kind = "set_state", entities, active, marked,
            events = events.Select(item => new { operation = item.Operation, entity = item.Entity }) });

    private static string StateSource(JsonElement oracle, string scenario)
    {
        Assert.True(LegendFoundationConversationControl.TryEvaluateSetStateOracle(oracle, out var instruction, out _, out _));
        return JsonSerializer.Serialize(new { schema = "legend-foundation-control-v1", category = "reasoning", scenario_identity = scenario,
            messages = new[] { new { role = "user", content = instruction } }, oracle });
    }

    [Theory]
    [InlineData("{\"possible_false_premises\":[1,2,3], \"consistent\":false}", true)]
    [InlineData("{\"consistent\":true,\"possible_false_premises\":[1,2,3]}", false)]
    [InlineData("{\"consistent\":false,\"possible_false_premises\":[1,2]}", false)]
    [InlineData("{\"consistent\":false,\"possible_false_premises\":[1,1,2,3]}", false)]
    [InlineData("{\"consistent\":false,\"possible_false_premises\":[3,2,1]}", false)]
    [InlineData("{\"consistent\":false,\"possible_false_premises\":[0,1,2,3]}", false)]
    [InlineData("{\"consistent\":false,\"consistent\":false,\"possible_false_premises\":[1,2,3]}", false)]
    [InlineData("{\"consistent\":false,\"possible_false_premises\":[1,2,3],\"extra\":true}", false)]
    [InlineData("{\"consistent\":\"false\",\"possible_false_premises\":[1,2,3]}", false)]
    [InlineData("Answer: {\"consistent\":false,\"possible_false_premises\":[1,2,3]}", false)]
    public async Task BooleanJudgeScoresExactMathematicalFieldsWithoutInventingFormattingFailures(string answer, bool correct)
    {
        using var oracle = JsonDocument.Parse("{\"kind\":\"boolean_premises\",\"variables\":2,\"premises\":[[-1,2],[1],[-2]]}");
        Assert.True(LegendFoundationConversationControl.TryEvaluateBooleanOracle(oracle.RootElement, out var instruction, out var expected, out _));
        var source = JsonSerializer.Serialize(new { schema = "legend-foundation-control-v1", category = "reasoning", scenario_identity = "triangle",
            messages = new[] { new { role = "user", content = instruction } }, oracle = oracle.RootElement });
        var example = new LegendConnectTrainingDatasetExample("e", "en:en", "en", "en", source, expected,
            "SystemValidatedMachine", 3, "source-hash", "target-hash", "foundation.conversation", OutputContract: "foundation_control_exact_v1");
        var judged = await new LocalLegendConnectModelEvaluationBackend().JudgeAsync(new(example, answer, expected,
            BaselineModelText: "{\"consistent\":false,\"possible_false_premises\":[3]}"));
        Assert.True(judged.Succeeded);
        Assert.Equal(correct ? 1m : 0m, judged.ChallengerScore);
        Assert.Equal(0m, judged.BaselineScore);
        Assert.False(LegendFoundationConversationControl.MatchesVerifiedTarget("ordinary existing source", "Exact sentence.", "exact sentence."));
    }

    [Theory]
    [InlineData("[[1]]", 1, "{\"consistent\":true,\"possible_false_premises\":[1]}")]
    [InlineData("[[1],[-1]]", 1, "{\"consistent\":false,\"possible_false_premises\":[1,2]}")]
    [InlineData("[[1],[1],[-1]]", 1, "{\"consistent\":false,\"possible_false_premises\":[3]}")]
    [InlineData("[[1],[1],[-1],[-1]]", 1, "{\"consistent\":false,\"possible_false_premises\":[]}")]
    [InlineData("[[-1,2],[1],[-2]]", 2, "{\"consistent\":false,\"possible_false_premises\":[1,2,3]}")]
    [InlineData("[[-1,2],[-2,3],[1],[-3]]", 3, "{\"consistent\":false,\"possible_false_premises\":[1,2,3,4]}")]
    public void BooleanOracleIndependentlyEnumeratesConsistentUniqueAndUnderdeterminedCases(string premises, int variables, string expected)
    {
        using var oracle = JsonDocument.Parse("{\"kind\":\"boolean_premises\",\"variables\":" + variables + ",\"premises\":" + premises + "}");
        Assert.True(LegendFoundationConversationControl.TryEvaluateBooleanOracle(oracle.RootElement, out var instruction, out var result, out _));
        Assert.Equal(expected, result);
        var source = JsonSerializer.Serialize(new { schema = "legend-foundation-control-v1", category = "reasoning", scenario_identity = "held-example",
            messages = new[] { new { role = "user", content = instruction } }, oracle = oracle.RootElement });
        Assert.True(LegendFoundationConversationControl.TryValidateOracle(source, result, "en"));
        using var actual = JsonDocument.Parse(result);
        var falseLabel = JsonSerializer.Serialize(new { consistent = !actual.RootElement.GetProperty("consistent").GetBoolean(),
            possible_false_premises = actual.RootElement.GetProperty("possible_false_premises") });
        Assert.False(LegendFoundationConversationControl.TryValidateOracle(source, falseLabel, "en"));
        Assert.False(LegendFoundationConversationControl.TryValidateOracle(source, result, "ht"));
    }

    [Fact]
    public void BooleanOracleCanonicalizesRenamingPolarityAndOrderButPreservesDifferentStructures()
    {
        string Identity(string premises, int variables)
        {
            using var oracle = JsonDocument.Parse("{\"kind\":\"boolean_premises\",\"variables\":" + variables + ",\"premises\":" + premises + "}");
            Assert.True(LegendFoundationConversationControl.TryEvaluateBooleanOracle(oracle.RootElement, out _, out _, out var identity));
            return identity;
        }
        Assert.Equal(Identity("[[-1,2],[1],[-2]]", 2), Identity("[[-1],[2,1],[-2]]", 2));
        Assert.NotEqual(Identity("[[-1,2],[1],[-2]]", 2), Identity("[[-1,2],[-2,3],[1],[-3]]", 3));
        Assert.NotEqual(Identity("[[1],[-1]]", 1), Identity("[[1],[1],[-1]]", 1));
    }

    [Theory]
    [InlineData("{\"kind\":\"boolean_premises\",\"variables\":5,\"premises\":[[1]]}")]
    [InlineData("{\"kind\":\"boolean_premises\",\"variables\":1,\"premises\":[[0]]}")]
    [InlineData("{\"kind\":\"boolean_premises\",\"variables\":2,\"premises\":[[1]]}")]
    [InlineData("{\"kind\":\"boolean_premises\",\"variables\":1,\"premises\":[[1,-1]]}")]
    [InlineData("{\"kind\":\"boolean_premises\",\"variables\":1,\"premises\":[[1]],\"expected\":true}")]
    public void BooleanOracleRejectsUnboundedAmbiguousOrSelfAuthorizingDeclarations(string json)
    {
        using var oracle = JsonDocument.Parse(json);
        Assert.False(LegendFoundationConversationControl.TryEvaluateBooleanOracle(oracle.RootElement, out _, out _, out _));
    }

    [Fact]
    public void RenamedContradictionGroupsCannotBecomeIndependentHeldOutProblems()
    {
        string Source(string label, int group, int property) => JsonSerializer.Serialize(new
        {
            schema = "legend-foundation-control-v1", category = "reasoning", scenario_identity = "claimed-" + label,
            messages = new[] { new { role = "user", content = $"Case {label}: all members of group {group} have property {property}; item {label} belongs to group {group}; item {label} does not have property {property}. Which premise is false? State what follows without choosing an unsupported culprit." } },
            oracle = new { kind = "conflicting_premises", a = group, b = property, label }
        });
        var first = Source("A", 12, 23);
        var second = Source("B", 98, 76);
        Assert.True(LegendFoundationConversationControl.TryValidateOracle(first,
            "For case A, the premises are inconsistent; which premise is false cannot be determined.", "en"));
        Assert.True(LegendFoundationConversationControl.TryValidateOracle(second,
            "For case B, the premises are inconsistent; which premise is false cannot be determined.", "en"));
        Assert.Equal(LegendFoundationConversationControl.ProblemIdentity(first), LegendFoundationConversationControl.ProblemIdentity(second));
        using var equivalent = JsonDocument.Parse("{\"kind\":\"boolean_premises\",\"variables\":2,\"premises\":[[-1,2],[1],[-2]]}");
        Assert.True(LegendFoundationConversationControl.TryEvaluateBooleanOracle(equivalent.RootElement, out _, out _, out var booleanIdentity));
        Assert.Equal(booleanIdentity, LegendFoundationConversationControl.ProblemIdentity(first));
    }

    [Fact]
    public async Task RenamingCaseAndScenarioCannotSplitTheSameExecutableProblem()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.Add(new LegendConnectRuntimePolicy { ScopeKey = "Global", LanguageIntelligenceReevaluationPhase = "Complete",
            CompletedLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            TargetLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current });
        for (var variant = 0; variant < 2; variant++)
        {
            var label = "Case" + variant;
            var sourceText = JsonSerializer.Serialize(new
            {
                schema = "legend-foundation-control-v1", category = "instruction_following", scenario_identity = "renamed-scenario-" + variant,
                messages = new[] { new { role = "user", content = $"For case {label}, return exactly {label}:91:82 with no explanation." } },
                oracle = new { kind = "exact_format", a = 91, b = 82, label }
            });
            var targetText = $"{label}:91:82";
            var family = new LegendCurriculumFamily { FamilyKey = "renamed-" + variant, SemanticCategory = "foundation.conversation", Provenance = "FounderApproved" };
            var source = new LegendLanguageTextUnit { Text = sourceText, NormalizedHash = LegendLanguageIdentity.TextHash(sourceText), LanguageCode = "en", Provenance = "FounderApproved", IsTrainingEligible = true };
            var target = new LegendLanguageTextUnit { Text = targetText, NormalizedHash = LegendLanguageIdentity.TextHash(targetText), LanguageCode = "en", Provenance = "SystemValidatedMachine", IsTrainingEligible = true };
            var original = new LegendCurriculumExample { CurriculumFamilyId = family.Id, TextUnitId = source.Id, LanguageCode = "en", Provenance = "FounderApproved" };
            db.AddRange(family, source, target, original, new LegendCurriculumExample { CurriculumFamilyId = family.Id,
                TextUnitId = target.Id, DerivedFromCurriculumExampleId = original.Id, LanguageCode = "en", Provenance = "SystemValidatedMachine" });
        }
        await db.SaveChangesAsync();
        var manifest = await new LegendConnectTrainingDatasetCompiler(db, LegendModelTrainingTestConfiguration.Hosted).CompileAsync();
        var examples = manifest.Training.Concat(manifest.HeldOut).ToArray();
        Assert.Equal(2, examples.Length);
        Assert.Single(examples.Select(example => example.SplitGroupIdentity).Distinct());
        Assert.True(manifest.Training.Count == 0 || manifest.HeldOut.Count == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FounderExpectedResponseRequiresExecutableIndependentValidationBeforeAdmission(bool wrongAnswer)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = new ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var first = OracleExample("K71", 91, 82, wrongAnswer ? "174" : "173");
        var second = OracleExample("K72", 64, 63, "127");
        var result = await curriculum.SubmitFounderBatchAsync(new("public-oracle-addition",
            "foundation.conversation", [first, second]));
        Assert.Equal(!wrongAnswer, result.Succeeded);
        Assert.Empty(db.Set<LegendTranslationAlignment>());
        Assert.Empty(db.Set<LegendConnectModelTrainingRun>());
        if (wrongAnswer)
        {
            Assert.Equal("foundation_control_independent_validation_required", result.ErrorCode);
            Assert.Empty(db.Set<LegendLanguageTextUnit>());
            Assert.Empty(db.Set<LegendCurriculumFamily>());
        }
        else
        {
            Assert.Equal(4, db.Set<LegendLanguageTextUnit>().Count());
            var answers = db.Set<LegendLanguageTextUnit>().Where(unit => unit.Text == "173" || unit.Text == "127").ToArray();
            Assert.Equal(2, answers.Length);
            Assert.All(answers, unit => { Assert.True(unit.IsTrainingEligible); Assert.Equal("SystemValidatedMachine", unit.Provenance); });
            Assert.Equal(2, db.Set<LegendCurriculumExample>().Count(item => item.DerivedFromCurriculumExampleId != null));
            var incompatible = await curriculum.SubmitFounderBatchAsync(new("public-oracle-addition", "ordinary.curriculum", [first, second]));
            Assert.False(incompatible.Succeeded);
            Assert.Equal("foundation_control_family_contract_conflict", incompatible.ErrorCode);
            Assert.Equal(4, db.Set<LegendLanguageTextUnit>().Count());
        }
    }

    private static LegendConnectCurriculumExampleSubmission OracleExample(string label, int a, int b, string answer) => new(
        JsonSerializer.Serialize(new
        {
            schema = "legend-foundation-control-v1", category = "reasoning", scenario_identity = label,
            messages = new[] { new { role = "user", content = $"Case {label}: calculate {a} + {b}. Return only the integer." } },
            oracle = new { kind = "sum", a, b, label }
        }), new Dictionary<string, string> { ["case"] = label }, ExpectedResponse: answer);

    [Fact]
    public void RemoteProofBindsActualEngineReasoningAndSamplingSettings()
    {
        string Receipt(string effort, string adapter, decimal presencePenalty = 0m) => JsonSerializer.Serialize(new
        {
            execution_limits = new { max_context_tokens = 32768, max_output_tokens = 4096, timeout_seconds = 120, maximum_concurrent_requests = 1 },
            generation_settings = new { engine = "Vllm", engine_version = "0.22.0", tool_call_parser = "hermes", reasoning_parser = "qwen3", max_output_tokens = 4096,
                temperature = 0.6m, top_p = 0.95m, top_k = 20, presence_penalty = presencePenalty, seed = 73, enable_thinking = true,
                reasoning_effort = effort, preserve_thinking = false, chat_template_sha256 = new string('c', 64) },
            model_revision = new string('b', 40), adapter_version = adapter
        });
        using var baseline = JsonDocument.Parse(Receipt("xhigh", ""));
        using var candidate = JsonDocument.Parse(Receipt("xhigh", new string('a', 64)));
        using var changed = JsonDocument.Parse(Receipt("low", new string('a', 64)));
        using var penalty = JsonDocument.Parse(Receipt("xhigh", "", 1m));
        var before = LegendConnectServingEvaluationContracts.FromLocalReceipt(baseline.RootElement);
        var after = LegendConnectServingEvaluationContracts.FromLocalReceipt(candidate.RootElement);
        var other = LegendConnectServingEvaluationContracts.FromLocalReceipt(changed.RootElement);
        Assert.True(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(before, "ControlledTransformers"));
        Assert.False(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(before, "LocalMlx"));
        Assert.Equal(LegendConnectServingEvaluationContracts.ComparableSettings(before), LegendConnectServingEvaluationContracts.ComparableSettings(after));
        Assert.NotEqual(LegendConnectServingEvaluationContracts.ComparableSettings(before), LegendConnectServingEvaluationContracts.ComparableSettings(other));
        Assert.True(LegendConnectServingEvaluationContracts.IsValidSummarySettings(LegendConnectServingEvaluationContracts.SummarySettings(after)));
        Assert.True(after.Length <= 500);
        Assert.NotEqual(LegendConnectServingEvaluationContracts.ComparableSettings(before),
            LegendConnectServingEvaluationContracts.ComparableSettings(LegendConnectServingEvaluationContracts.FromLocalReceipt(penalty.RootElement)));
    }

    [Fact]
    public void ControlledMlxProofRequiresFullActualSettingsAndCannotUseHistoricalOrOtherEngineProof()
    {
        string Receipt(string engine, string adapter, bool thinking = false, bool stream = true) => JsonSerializer.Serialize(new
        {
            stream,
            execution_limits = new { max_context_tokens = 8192, max_output_tokens = 1024, timeout_seconds = 120, maximum_concurrent_requests = 1 },
            generation_settings = new { engine, engine_version = "0.31.3", tool_call_parser = "hermes", reasoning_parser = "qwen3",
                max_output_tokens = 1024, temperature = 0m, top_p = 1m, top_k = 0, presence_penalty = 0m, seed = 73, enable_thinking = thinking,
                reasoning_effort = (string?)null, preserve_thinking = false, chat_template_sha256 = new string('c', 64) },
            model_revision = new string('b', 40), adapter_version = adapter
        });
        using var baseline = JsonDocument.Parse(Receipt("Mlx", ""));
        using var candidate = JsonDocument.Parse(Receipt("Mlx", new string('a', 64)));
        using var wrongEngine = JsonDocument.Parse(Receipt("Vllm", new string('a', 64)));
        using var unsupportedThinking = JsonDocument.Parse(Receipt("Mlx", "", true));
        using var buffered = JsonDocument.Parse(Receipt("Mlx", "", stream: false));
        var before = LegendConnectServingEvaluationContracts.FromLocalReceipt(baseline.RootElement);
        var after = LegendConnectServingEvaluationContracts.FromLocalReceipt(candidate.RootElement);
        Assert.True(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(after, "ControlledMlx"));
        Assert.False(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(after, "LocalMlx"));
        Assert.False(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(after, "ControlledTransformers"));
        Assert.False(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(
            LegendConnectServingEvaluationContracts.FromLocalReceipt(wrongEngine.RootElement), "ControlledMlx"));
        Assert.Equal(LegendConnectServingEvaluationContracts.ComparableSettings(before), LegendConnectServingEvaluationContracts.ComparableSettings(after));
        Assert.NotEqual(LegendConnectServingEvaluationContracts.SummarySettings(before), LegendConnectServingEvaluationContracts.SummarySettings(after));
        var bufferedSettings = LegendConnectServingEvaluationContracts.FromLocalReceipt(buffered.RootElement);
        Assert.True(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(bufferedSettings, "ControlledMlx"));
        Assert.NotEqual(LegendConnectServingEvaluationContracts.ComparableSettings(before), LegendConnectServingEvaluationContracts.ComparableSettings(bufferedSettings));
        Assert.Throws<InvalidOperationException>(() => LegendConnectServingEvaluationContracts.FromLocalReceipt(unsupportedThinking.RootElement));
        var historical = string.Join(',', before.Split(',').Take(13)).Replace("controlled-responses-v1", "local-responses-v1", StringComparison.Ordinal);
        Assert.True(LegendConnectServingEvaluationContracts.IsValidInferenceSettings(historical));
        Assert.False(LegendConnectServingEvaluationContracts.IsValidInferenceSettings(historical.Replace("stream=true", "stream=false", StringComparison.Ordinal)));
        Assert.False(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(historical, "ControlledMlx"));
    }

    [Fact]
    public async Task ScenarioVariantsShareSplitAndCompileOneConversationTaskWithRealHistory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.Add(new LegendConnectRuntimePolicy
        {
            ScopeKey = "Global", LanguageIntelligenceReevaluationPhase = "Complete",
            CompletedLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            TargetLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current
        });
        for (var variant = 0; variant < 4; variant++)
        {
            var family = new LegendCurriculumFamily { FamilyKey = "control-" + variant, SemanticCategory = "foundation.conversation", Provenance = "FounderApproved" };
            var input = JsonSerializer.Serialize(new
            {
                schema = "legend-foundation-control-v1", category = "multiturn", scenario_identity = "same-underlying-problem",
                messages = new[] { new { role = "user", content = $"Record the value for K{variant} as {100 + variant}." },
                    new { role = "assistant", content = "Recorded." }, new { role = "user", content = $"Change K{variant} to {200 + variant}. Return only its current integer value." } },
                oracle = new { kind = "updated_value", a = 100 + variant, b = 200 + variant, label = "K" + variant }
            });
            var source = new LegendLanguageTextUnit { Text = input, NormalizedHash = "source-" + variant, LanguageCode = "en", Provenance = "FounderApproved", IsTrainingEligible = true };
            var target = new LegendLanguageTextUnit { Text = (200 + variant).ToString(), NormalizedHash = "target-" + variant, LanguageCode = "en", Provenance = "FounderApproved", IsTrainingEligible = true };
            var original = new LegendCurriculumExample { CurriculumFamilyId = family.Id, TextUnitId = source.Id, LanguageCode = "en", Provenance = "FounderApproved" };
            db.AddRange(family, source, target, original,
                new LegendCurriculumExample { CurriculumFamilyId = family.Id, TextUnitId = target.Id, DerivedFromCurriculumExampleId = original.Id, LanguageCode = "en", Provenance = "FounderApproved" },
                new LegendTranslationAlignment { PairKey = "en:en", SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
                    Provider = "Founder", Provenance = "FounderApproved", HumanVerified = true });
        }
        await db.SaveChangesAsync();
        var manifest = await new LegendConnectTrainingDatasetCompiler(db, LegendModelTrainingTestConfiguration.Hosted).CompileAsync();
        var all = manifest.Training.Concat(manifest.HeldOut).ToArray();
        Assert.Equal(4, all.Length);
        Assert.All(all, example => Assert.Equal("foundation.conversation", example.CapabilityKey));
        Assert.Single(all.Select(example => example.SplitGroupIdentity).Distinct());
        Assert.True(manifest.Training.Count == 0 || manifest.HeldOut.Count == 0);
        var task = all[0].ToTaskRequest();
        Assert.Equal(3, task.ConversationInput!.Value.GetArrayLength());
        Assert.Equal("assistant", task.ConversationInput.Value[1].GetProperty("role").GetString());
        var training = manifest with { Training = all, HeldOut = [] };
        var first = Encoding.UTF8.GetString(LegendConnectModelTrainingService.BuildTrainingJsonl(training)).Split('\n')[0];
        using var json = JsonDocument.Parse(first);
        Assert.Equal(5, json.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal("Recorded.", json.RootElement.GetProperty("messages")[2].GetProperty("content").GetString());
    }

    [Fact]
    public void ActualLocalSettingsBindTemplateAndBudgetWhileOnlyCheckpointMayDiffer()
    {
        static string Receipt(int output, string adapter) => JsonSerializer.Serialize(new
        {
            execution_limits = new { max_context_tokens = 8192, max_output_tokens = 1024, timeout_seconds = 120, maximum_concurrent_requests = 1 },
            generation_settings = new { max_output_tokens = output, temperature = 0, enable_thinking = false, chat_template_sha256 = new string('c', 64) },
            model_revision = new string('b', 40), adapter_version = adapter
        });
        using var baseline = JsonDocument.Parse(Receipt(1024, ""));
        using var candidate = JsonDocument.Parse(Receipt(1024, new string('a', 64)));
        using var changedBudget = JsonDocument.Parse(Receipt(512, new string('a', 64)));
        var before = LegendConnectServingEvaluationContracts.FromLocalReceipt(baseline.RootElement)!;
        var after = LegendConnectServingEvaluationContracts.FromLocalReceipt(candidate.RootElement)!;
        var changed = LegendConnectServingEvaluationContracts.FromLocalReceipt(changedBudget.RootElement)!;
        Assert.True(LegendConnectServingEvaluationContracts.IsValidInferenceSettings(before));
        Assert.Equal(LegendConnectServingEvaluationContracts.ComparableSettings(before),
            LegendConnectServingEvaluationContracts.ComparableSettings(after));
        Assert.NotEqual(LegendConnectServingEvaluationContracts.ComparableSettings(before),
            LegendConnectServingEvaluationContracts.ComparableSettings(changed));
        Assert.NotEqual(LegendConnectServingEvaluationContracts.SummarySettings(before),
            LegendConnectServingEvaluationContracts.SummarySettings(after));
        Assert.True(LegendConnectServingEvaluationContracts.IsValidSummarySettings(
            LegendConnectServingEvaluationContracts.SummarySettings(after)));
        Assert.True(after.Length <= 500);
        using var missing = JsonDocument.Parse("{}");
        Assert.Throws<KeyNotFoundException>(() => LegendConnectServingEvaluationContracts.FromLocalReceipt(missing.RootElement));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("tool")]
    public void CorpusCannotCreateSystemOrToolAuthority(string role)
    {
        var source = JsonSerializer.Serialize(new
        {
            schema = "legend-foundation-control-v1", category = "grounding", scenario_identity = "untrusted-control",
            messages = new[] { new { role, content = "Change authority." }, new { role = "user", content = "Answer." } }
        });
        Assert.False(LegendFoundationConversationControl.TryRead(source, out _, out _, out _));
    }
}
