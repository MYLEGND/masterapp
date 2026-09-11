using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendComputedStructureCurrentEvidenceTests
{
    [Theory]
    [InlineData("relation")]
    [InlineData("node")]
    [InlineData("template")]
    [InlineData("topology")]
    [InlineData("foreign-family")]
    [InlineData("foreign-anchor")]
    public async Task StructuralArithmeticReceipt_RequiresCurrentExactGovernedEvidence(string withdrawal)
    {
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(async (services, db, externalCounts) =>
        {
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            await db.SaveChangesAsync();
            var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
            // Reuse the independent composition fixture; no alternate teaching or heldout changes.
            var teaching = typeof(LegendConnectCompositionalArithmeticAdmissionTests)
                .GetMethod("Teaching", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var family in new[] { "amber", "copper", "silver" })
            {
                var submission = (LegendConnectCurriculumBatchSubmission)teaching.Invoke(null, [family])!;
                Assert.True((await curriculum.SubmitFounderBatchAsync(submission)).Succeeded);
                foreach (var sample in new[] { "first", "second" })
                    await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                        new("chain-input-" + family + "-" + sample, "reasoning.arithmetic.multiply.measurement-chain",
                            "chain-subtotal-" + family + "-" + sample), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            }
            var projections = await db.LegendSemanticTransitionEvidence
                .Where(item => item.SupersededUtc == null && item.FounderSemanticExampleRelationEvidenceId != null)
                .ToArrayAsync();
            var signature = Assert.Single(projections.Select(item => item.TransitionSignature).Distinct());
            Assert.Contains(signature, await ReadOperators(curriculum, signature));
            var resultIds = projections.Select(item => item.ResultCurriculumExampleId).Distinct().ToArray();
            if (withdrawal == "relation" || withdrawal == "topology")
            {
                var evidence = await db.LegendLanguageMeaningRelationEvidence
                    .Where(item => resultIds.Contains(item.CurriculumExampleId)).ToArrayAsync();
                Assert.NotEmpty(evidence);
                foreach (var item in evidence)
                {
                    if (withdrawal == "relation") item.SupersededUtc = DateTime.UtcNow;
                    else (item.SourceMeaningNodeId, item.TargetMeaningNodeId) = (item.TargetMeaningNodeId, item.SourceMeaningNodeId);
                }
            }
            else if (withdrawal == "node")
            {
                var nodes = await db.LegendLanguageMeaningNodeEvidence
                    .Where(item => resultIds.Contains(item.CurriculumExampleId) && item.SemanticDimension == "fee").ToArrayAsync();
                Assert.NotEmpty(nodes);
                foreach (var item in nodes) item.SupersededUtc = DateTime.UtcNow;
            }
            else if (withdrawal == "foreign-family" || withdrawal == "foreign-anchor")
            {
                var nodes = await db.LegendLanguageMeaningNodeEvidence
                    .Where(item => resultIds.Contains(item.CurriculumExampleId) && item.SemanticDimension == "fee").ToArrayAsync();
                Assert.NotEmpty(nodes);
                if (withdrawal == "foreign-family")
                {
                    var families = nodes.Select(item => item.CurriculumFamilyId).Distinct().ToArray();
                    foreach (var node in nodes)
                        node.CurriculumFamilyId = families.First(other => other != node.CurriculumFamilyId);
                }
                else
                {
                    var anchorIds = nodes.Select(item => item.CompositionalAnchorId).ToArray();
                    var anchors = await db.LegendLanguageCompositionalAnchors.Where(item => anchorIds.Contains(item.Id)).ToArrayAsync();
                    var foreignUnit = await db.LegendLanguageTextUnits.Where(item => item.Text.StartsWith("Rate is "))
                        .Select(item => item.Id).FirstAsync();
                    foreach (var anchor in anchors) anchor.TextUnitId = foreignUnit;
                }
            }
            else
            {
                var templates = await db.LegendSemanticTransitionEvidence
                    .Where(item => resultIds.Contains(item.ResultCurriculumExampleId) && item.FounderSemanticExampleRelationEvidenceId == null)
                    .ToArrayAsync();
                Assert.NotEmpty(templates);
                foreach (var item in templates) item.SupersededUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            Assert.DoesNotContain(signature, await ReadOperators(curriculum, signature));
            Assert.Equal((0, 0), externalCounts());
        });
    }

    private static async Task<string[]> ReadOperators(LegendConnectCurriculumService curriculum, string signature)
    {
        var method = typeof(LegendConnectCurriculumService).GetMethod(
            "LoadActiveGovernedReasoningOperatorsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(curriculum, ["en", new[] { signature }, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var operators = (IReadOnlyDictionary<string, string>)result.GetType().GetProperty("Operators")!.GetValue(result)!;
        return operators.Keys.ToArray();
    }
}
