using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Actual Founder curriculum admission and operator-selection contracts.
/// Exact taught source utterances test admission, not unseen-language ability.
/// Natural-language generalization needs separate fresh-operand serving proof.
/// </summary>
public sealed class LegendConnectComputedOperatorAdmissionContractTests
{
    [Fact]
    public async Task DeclaredOperandRoles_DetermineSubtractionIndependentlyOfNodeAndDimensionOrder()
    {
        await WithAuthorityAsync(async (curriculum, operations, db) =>
        {
            await AdmitAsync(curriculum, "ordered", "83", "29", "54");
            var projection = Assert.Single(await db.LegendSemanticTransitionEvidence
                .Where(item => item.FounderSemanticExampleRelationEvidenceId != null && item.SupersededUtc == null).ToArrayAsync());
            using var source = JsonDocument.Parse(projection.SourceSemanticFrame);
            using var result = JsonDocument.Parse(projection.ResultSemanticFrame);
            Assert.Equal("$numeric_left", source.RootElement.GetProperty("z_measure").GetString());
            Assert.Equal("$numeric_right", source.RootElement.GetProperty("a_measure").GetString());
            Assert.Equal("$numeric_result", result.RootElement.GetProperty("total").GetString());

            var planned = await operations.TryPlanConversationAsync(Source("83", "29"), new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(planned.Supported, planned.ReasonCode);
            var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
            Assert.Equal("54", plan.ResultDimensions["total"]);
            Assert.NotEmpty(plan.ReasoningTransitionPath ?? []);
            Assert.Contains(projection.TransitionSignature, plan.ReasoningTransitionPath ?? []);
        });
    }

    [Theory]
    [InlineData("999")]
    [InlineData("-54")]
    public async Task IncorrectFounderSample_CannotAuthorizeAComputedRuleOrAnOrdinaryAnswer(string claimedResult)
    {
        await WithAuthorityAsync(async (curriculum, operations, db) =>
        {
            var submitted = await curriculum.SubmitFounderBatchAsync(Batch("incorrect", "83", "29", claimedResult));
            Assert.True(submitted.Succeeded, submitted.Message);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                curriculum.PersistFounderCrossExampleSemanticRelationAsync(Relation("incorrect"),
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current));
            Assert.Equal("computed_operator_sample_result_mismatch", error.Message);
            Assert.Empty(await db.Set<LegendFounderSemanticExampleRelationEvidence>().ToArrayAsync());
            Assert.Empty(await db.LegendSemanticTransitionEvidence.Where(item =>
                item.FounderSemanticExampleRelationEvidenceId != null && item.SupersededUtc == null).ToArrayAsync());
            var planned = await operations.TryPlanConversationAsync(Source("83", "29"), new LegendConnectDiscourseStateSnapshot([]));
            Assert.False(planned.Supported);
            Assert.Null(planned.Plan);
        });
    }

    [Theory]
    [InlineData("missing", "computed_operator_roles_unproven")]
    [InlineData("ambiguous", "computed_operator_roles_ambiguous")]
    public async Task ArithmeticRoles_CannotBeInferredFromNumbersOrAlphabeticalDimensionOrder(string declaration, string expectedReason)
    {
        await WithAuthorityAsync(async (curriculum, _, db) =>
        {
            var submitted = await curriculum.SubmitFounderBatchAsync(Batch("roles", "83", "29", "54", declaration));
            Assert.True(submitted.Succeeded, submitted.Message);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                curriculum.PersistFounderCrossExampleSemanticRelationAsync(Relation("roles"),
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current));
            Assert.Equal(expectedReason, error.Message);
            Assert.Empty(await db.Set<LegendFounderSemanticExampleRelationEvidence>().ToArrayAsync());
        });
    }

    [Fact]
    public async Task ComputedRoleDeclarationWithoutItsFounderOperator_CannotProjectTheRecordedResult()
    {
        await WithAuthorityAsync(async (curriculum, operations, _) =>
        {
            var submitted = await curriculum.SubmitFounderBatchAsync(Batch("unlinked", "83", "29", "54"));
            Assert.True(submitted.Succeeded, submitted.Message);
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(Source("83", "29"));
            Assert.True(graph.IsComposed, graph.ReasonCode);
            Assert.DoesNotContain(graph.Nodes, node => node.SemanticDimension == "total");
            var planned = await operations.TryPlanConversationAsync(Source("83", "29"), new LegendConnectDiscourseStateSnapshot([]));
            Assert.False(planned.Supported);
            Assert.Null(planned.Plan);
        });
    }

    [Theory]
    [InlineData("withdraw_parent")]
    [InlineData("replace_role_template_with_legacy_constants")]
    public async Task StaleComputedAuthority_CannotFallBackToAnOrdinarySourceResultMapping(string corruption)
    {
        await WithAuthorityAsync(async (curriculum, operations, db) =>
        {
            await AdmitAsync(curriculum, "withdrawn", "83", "29", "54");
            var before = await operations.TryPlanConversationAsync(Source("83", "29"), new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(before.Supported, before.ReasonCode);
            Assert.NotEmpty(before.Plan!.ReasoningTransitionPath ?? []);
            var parent = Assert.Single(await db.Set<LegendFounderSemanticExampleRelationEvidence>().ToArrayAsync());
            if (corruption == "withdraw_parent")
            {
                parent.SupersededUtc = DateTime.UtcNow;
            }
            else
            {
                var declaration = await db.LegendSemanticTransitionEvidence.SingleAsync(item =>
                    item.FounderSemanticExampleRelationEvidenceId == null && item.SupersededUtc == null &&
                    item.SourceCurriculumExampleId == parent.SourceCurriculumExampleId &&
                    item.ResultCurriculumExampleId == parent.ResultCurriculumExampleId &&
                    item.SourceSemanticFrame.Contains("$numeric_left"));
                // Adversarial persisted legacy-shaped drift after genuine
                // admission. The old computed projection must not inherit it.
                declaration.SourceSemanticFrame = JsonSerializer.Serialize(Values(("z_measure", "83"), ("a_measure", "29")));
                declaration.ResultSemanticFrame = JsonSerializer.Serialize(Values(("total", "54")));
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var after = await operations.TryPlanConversationAsync(Source("83", "29"), new LegendConnectDiscourseStateSnapshot([]));
            Assert.False(after.Supported);
            Assert.Null(after.Plan);
        });
    }

    [Fact]
    public async Task ReplayingTheSameFounderOperator_DoesNotMultiplyItsEvidence()
    {
        await WithAuthorityAsync(async (curriculum, _, db) =>
        {
            await AdmitAsync(curriculum, "replayed", "83", "29", "54");
            var parents = await db.Set<LegendFounderSemanticExampleRelationEvidence>().CountAsync();
            var transitions = await db.LegendSemanticTransitionEvidence.CountAsync();
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(Relation("replayed"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            Assert.Equal(parents, await db.Set<LegendFounderSemanticExampleRelationEvidence>().CountAsync());
            Assert.Equal(transitions, await db.LegendSemanticTransitionEvidence.CountAsync());
        });
    }

    private static async Task AdmitAsync(LegendConnectCurriculumService curriculum, string identity, string left, string right, string result)
    {
        var submitted = await curriculum.SubmitFounderBatchAsync(Batch(identity, left, right, result));
        Assert.True(submitted.Succeeded, submitted.Message);
        await curriculum.PersistFounderCrossExampleSemanticRelationAsync(Relation(identity),
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
    }

    private static LegendConnectCurriculumBatchSubmission Batch(string identity, string left, string right, string result, string declaration = "valid")
    {
        var transitions = new List<LegendConnectSemanticTransitionSubmission>();
        if (declaration != "missing")
            transitions.Add(new(new(Values(("z_measure", "$numeric_left"), ("a_measure", "$numeric_right"))),
                new(Values(("total", "$numeric_result")))));
        if (declaration == "ambiguous")
            transitions.Add(new(new(Values(("z_measure", "$numeric_right"), ("a_measure", "$numeric_left"))),
                new(Values(("total", "$numeric_result")))));
        transitions.Add(new(new(Values(("total", "$value"))),
            new(Values(("total", "$value"), ("conversation_function", "numeric_answer")))));
        return new LegendConnectCurriculumBatchSubmission("computed.admission." + identity, "Controlled numeric operator roles",
            [
                new(Source(left, right), Values(("z_measure", left), ("a_measure", right)),
                    new LegendConnectMeaningGraphSubmission(
                        [new("right", "a_measure", right, right), new("left", "z_measure", left, left)],
                        [new("left", "paired-with", "right")]), "source-" + identity),
                new("Computed measure: " + result + ".", Values(("total", result)),
                    new LegendConnectMeaningGraphSubmission([new("value", "total", result, result)], []), "result-" + identity),
                new("The result is " + result + ".", Values(("total", result), ("conversation_function", "numeric_answer")),
                    new LegendConnectMeaningGraphSubmission([new("value", "total", result, result)], []))
            ], transitions);
    }

    private static LegendConnectCrossExampleSemanticRelationshipSubmission Relation(string identity) =>
        new("source-" + identity, "reasoning.arithmetic.subtract.admission-contract", "result-" + identity);

    private static string Source(string left, string right) => "Compute " + left + " against " + right + ".";
    private static Dictionary<string, string> Values(params (string Dimension, string Value)[] values) =>
        values.ToDictionary(item => item.Dimension, item => item.Value, StringComparer.Ordinal);

    private static Task WithAuthorityAsync(Func<LegendConnectCurriculumService, ILegendConnectOperations, MasterAppDbContext, Task> verify) =>
        LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(async (services, db, externalCounts) =>
        {
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            await verify(services.GetRequiredService<LegendConnectCurriculumService>(),
                services.GetRequiredService<ILegendConnectOperations>(), db);
            Assert.Equal((0, 0), externalCounts());
        });
}
