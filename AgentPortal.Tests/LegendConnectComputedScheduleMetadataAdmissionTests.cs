using System;
using System.Collections.Generic;
using System.Linq;
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
/// Canonical admission contracts for generated schedule metadata. These use
/// exact taught numeric samples; fresh language grounding is tested separately.
/// </summary>
public sealed class LegendConnectComputedScheduleMetadataAdmissionTests
{
    private const string SourceText = "Allocate work 53 capacity 8 duration 17 resources 3 required 1 deadline 100.";
    private const string ResultText = "Schedule count 7 elapsed 51 final 5 status feasible.";
    private const string SignatureVariable = "$schedule_signature";

    [Fact]
    public async Task SignatureDeclarationWithoutLiteralOrMeaningNode_ProducesTheExecutableCertificate()
    {
        await WithAuthorityAsync(async (curriculum, operations, db) =>
        {
            await SubmitAsync(curriculum, Batch());
            await AdmitOperatorAsync(curriculum);
            Assert.False(await db.Set<LegendLanguageMeaningNodeEvidence>().AnyAsync(node =>
                node.SemanticDimension == "digest" || node.SemanticValue == SignatureVariable));
            Assert.False(await db.LegendLanguageTextUnits.AnyAsync(unit => unit.Text.Contains(SignatureVariable)));
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(ResultText);
            Assert.DoesNotContain(graph.Nodes, node => node.SemanticDimension == "digest");

            var planned = await operations.TryPlanConversationAsync(SourceText, new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(planned.Supported, planned.ReasonCode);
            var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
            Assert.Equal("51", plan.ResultDimensions["elapsed"]);
            Assert.NotEmpty(plan.ReasoningTransitionPath ?? []);
            var certificate = Assert.Single(plan.ScheduleCertificates!);
            Assert.Equal("digest", certificate.SignatureDimension);
            Assert.Matches("^[0-9a-f]{64}$", certificate.Signature);
            Assert.Equal(7, certificate.Steps.Count);
            Assert.Equal(53L, certificate.Steps.Sum(step => step.WorkUnits));
            Assert.Equal(5L, certificate.Steps[^1].WorkUnits);
            Assert.Equal(51, certificate.Steps.Max(step => step.EndMinute));
            Assert.Equal(certificate.Signature, certificate.Conclusions["digest"]);
        });
    }

    [Fact]
    public async Task ExactAdmittedEndpoint_MetadataCannotSupplyAnOrdinaryResponseVariable()
    {
        await WithAuthorityAsync(async (curriculum, operations, db) =>
        {
            await SubmitAsync(curriculum, Batch(signatureResponseOnly: true));
            // This endpoint provides one observed value and signature
            // declaration metadata. It has no computed operator certificate;
            // the ordinary copy transition must not manufacture that fact.
            Assert.False(await db.Set<LegendLanguageMeaningNodeEvidence>().AnyAsync(node => node.SemanticDimension == "digest"));
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(ResultText);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            Assert.DoesNotContain(graph.Nodes, node => node.SemanticDimension == "digest");

            // The ordinary transition explicitly asks for digest=$copy. Even
            // this exact endpoint's metadata cannot become its missing fact.
            var planned = await operations.TryPlanConversationAsync(ResultText, new LegendConnectDiscourseStateSnapshot([]));
            Assert.False(planned.Supported);
            Assert.Null(planned.Plan);
            var native = await operations.TryInferConversationWithDiscourseAsync(ResultText, [],
                new LegendConnectDiscourseStateSnapshot([]), sourceLanguageCode: "en",
                providerPolicy: LegendConnectExternalProviderPolicy.NativeOnly);
            Assert.False(native.Supported);
            Assert.Null(native.Answer);
            Assert.Empty(native.ScheduleCertificates ?? []);
        });
    }

    [Theory]
    [InlineData("elapsed")]
    [InlineData("status")]
    public async Task SignatureMetadataException_CannotWaiveAnActualOutputClaim(string omittedDimension)
    {
        await WithAuthorityAsync(async (curriculum, _, db) =>
        {
            await SubmitAsync(curriculum, Batch(omittedGraphDimension: omittedDimension));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AdmitOperatorAsync(curriculum));
            Assert.Equal("computed_operator_sample_graph_unproven", error.Message);
            Assert.Empty(await db.Set<LegendFounderSemanticExampleRelationEvidence>().ToArrayAsync());
        });
    }

    [Fact]
    public async Task IncorrectNumericalSample_RemainsRejectedWhenSignatureIsOnlyMetadata()
    {
        await WithAuthorityAsync(async (curriculum, _, db) =>
        {
            await SubmitAsync(curriculum, Batch(claimedElapsed: "52"));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AdmitOperatorAsync(curriculum));
            Assert.Equal("computed_operator_sample_result_mismatch", error.Message);
            Assert.Empty(await db.Set<LegendFounderSemanticExampleRelationEvidence>().ToArrayAsync());
        });
    }

    [Fact]
    public async Task SignaturePlaceholderMeaningNode_IsNotAnObservedComputedFact()
    {
        await WithAuthorityAsync(async (curriculum, _, db) =>
        {
            await SubmitAsync(curriculum, Batch(includePlaceholderNode: true));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AdmitOperatorAsync(curriculum));
            Assert.Equal("computed_operator_signature_placeholder_not_factual", error.Message);
            Assert.Empty(await db.Set<LegendFounderSemanticExampleRelationEvidence>().ToArrayAsync());
        });
    }

    private static LegendConnectCurriculumBatchSubmission Batch(
        bool signatureResponseOnly = false, string? omittedGraphDimension = null,
        string claimedElapsed = "51", bool includePlaceholderNode = false)
    {
        var sourceValues = Values(("work", "53"), ("capacity", "8"), ("duration", "17"),
            ("resources", "3"), ("required", "1"), ("deadline", "100"));
        var resultValues = Values(("batches", "7"), ("elapsed", claimedElapsed),
            ("last_size", "5"), ("status", "feasible"), ("digest", SignatureVariable));
        var resultNodes = resultValues.Where(item => item.Key != "digest" && item.Key != omittedGraphDimension)
            .Where(item => !signatureResponseOnly || item.Key == "elapsed")
            .Select(item => new LegendConnectMeaningNodeSubmission(item.Key, item.Key, item.Value, item.Value)).ToList();
        if (includePlaceholderNode)
            resultNodes.Add(new("digest", "digest", SignatureVariable, "pending"));
        var sourceFrame = new LegendConnectSemanticFrameSubmission(Values(
            ("work", "$schedule_workload"), ("capacity", "$schedule_batch_capacity"),
            ("duration", "$schedule_batch_duration"), ("resources", "$schedule_available_resources"),
            ("required", "$schedule_required_resources"), ("deadline", "$schedule_time_limit")));
        var resultFrame = new LegendConnectSemanticFrameSubmission(Values(
            ("batches", "$schedule_batch_count"), ("elapsed", "$schedule_elapsed"),
            ("last_size", "$schedule_final_batch_size"), ("status", "$schedule_status"),
            ("digest", SignatureVariable)));
        var resultText = ResultText.Replace("51", claimedElapsed, StringComparison.Ordinal) +
            (includePlaceholderNode ? " Certificate pending." : string.Empty);
        var examples = new List<LegendConnectCurriculumExampleSubmission>
        {
            new(SourceText, sourceValues,
                new(sourceValues.Select(item => new LegendConnectMeaningNodeSubmission(
                    item.Key, item.Key, item.Value, item.Value)).ToArray(),
                    sourceValues.Keys.Zip(sourceValues.Keys.Skip(1), (first, second) =>
                        new LegendConnectMeaningRelationSubmission(first, "paired-with", second)).ToArray()), "schedule-source"),
            new(resultText, resultValues, new(resultNodes, []), "schedule-result")
        };
        var transitions = new List<LegendConnectSemanticTransitionSubmission> { new(sourceFrame, resultFrame) };
        if (signatureResponseOnly)
        {
            examples.Add(new("Signature summary.", Values(("digest", SignatureVariable), ("conversation_function", "signature_answer")),
                new([new("function", "conversation_function", "signature_answer", "Signature summary")], [])));
            transitions.Add(new(new(Values(("elapsed", "$elapsed"), ("digest", "$copy"))),
                new(Values(("digest", "$copy"), ("conversation_function", "signature_answer")))));
        }
        else
        {
            examples.Add(new("Scheduled duration: " + claimedElapsed + ".",
                Values(("elapsed", claimedElapsed), ("conversation_function", "schedule_answer")),
                new([new("elapsed", "elapsed", claimedElapsed, claimedElapsed),
                    new("function", "conversation_function", "schedule_answer", "Scheduled duration")], [])));
            transitions.Add(new(new(Values(("elapsed", "$elapsed"), ("status", "feasible"))),
                new(Values(("elapsed", "$elapsed"), ("conversation_function", "schedule_answer")))));
        }
        return new("computed.schedule.metadata", "Generated schedule signature metadata", examples, transitions);
    }

    private static async Task SubmitAsync(LegendConnectCurriculumService curriculum, LegendConnectCurriculumBatchSubmission batch)
    {
        var submitted = await curriculum.SubmitFounderBatchAsync(batch);
        Assert.True(submitted.Succeeded, submitted.Message);
    }

    private static Task AdmitOperatorAsync(LegendConnectCurriculumService curriculum) =>
        curriculum.PersistFounderCrossExampleSemanticRelationAsync(
            new("schedule-source", "reasoning.constrained-planning.batch.metadata-contract", "schedule-result"),
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

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
