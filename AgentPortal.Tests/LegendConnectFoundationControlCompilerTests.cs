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
        string Receipt(string effort, string adapter) => JsonSerializer.Serialize(new
        {
            execution_limits = new { max_context_tokens = 32768, max_output_tokens = 4096, timeout_seconds = 120, maximum_concurrent_requests = 1 },
            generation_settings = new { engine = "Vllm", engine_version = "0.22.0", tool_call_parser = "hermes", reasoning_parser = "qwen3", max_output_tokens = 4096,
                temperature = 0.6m, top_p = 0.95m, top_k = 20, seed = 73, enable_thinking = true,
                reasoning_effort = effort, preserve_thinking = false, chat_template_sha256 = new string('c', 64) },
            model_revision = new string('b', 40), adapter_version = adapter
        });
        using var baseline = JsonDocument.Parse(Receipt("xhigh", ""));
        using var candidate = JsonDocument.Parse(Receipt("xhigh", new string('a', 64)));
        using var changed = JsonDocument.Parse(Receipt("low", new string('a', 64)));
        var before = LegendConnectServingEvaluationContracts.FromLocalReceipt(baseline.RootElement);
        var after = LegendConnectServingEvaluationContracts.FromLocalReceipt(candidate.RootElement);
        var other = LegendConnectServingEvaluationContracts.FromLocalReceipt(changed.RootElement);
        Assert.True(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(before, "ControlledTransformers"));
        Assert.False(LegendConnectServingEvaluationContracts.IsCompatibleWithBackend(before, "LocalMlx"));
        Assert.Equal(LegendConnectServingEvaluationContracts.ComparableSettings(before), LegendConnectServingEvaluationContracts.ComparableSettings(after));
        Assert.NotEqual(LegendConnectServingEvaluationContracts.ComparableSettings(before), LegendConnectServingEvaluationContracts.ComparableSettings(other));
        Assert.True(LegendConnectServingEvaluationContracts.IsValidSummarySettings(LegendConnectServingEvaluationContracts.SummarySettings(after)));
        Assert.True(after.Length <= 500);
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
