using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Integrates the existing rate-times-quantity-plus-fee executor control through
/// admission and source grounding. Separate from the frozen capability matrix.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendConnectCompositionalArithmeticAdmissionTests
{
    [Fact]
    public async Task AdmittedMeasurements_BindThreeInputsAndExecuteTwoArithmeticStepsNatively()
    {
        var priorFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderId = Guid.NewGuid().ToString("D");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(async (services, db, externalCounts) =>
            {
                ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
                db.AgentProfiles.Add(new AgentProfile
                {
                    Id = Guid.NewGuid(), AgentUserId = founderId, AgentUpn = "arithmetic-chain@legend.test",
                    NormalizedEmail = "arithmetic-chain@legend.test", IsActive = true
                });
                await db.SaveChangesAsync();
                var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
                foreach (var family in new[] { "amber", "copper", "silver" })
                {
                    var admission = await curriculum.SubmitFounderBatchAsync(Teaching(family));
                    Assert.True(admission.Succeeded, "admission: " + admission.Message);
                    foreach (var sample in new[] { "first", "second" })
                    {
                        await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                            new("chain-input-" + family + "-" + sample, "reasoning.arithmetic.multiply.measurement-chain",
                                "chain-subtotal-" + family + "-" + sample), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
                        await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                            new("chain-subtotal-" + family + "-" + sample, "reasoning.arithmetic.add.measurement-chain",
                                "chain-total-" + family + "-" + sample), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
                    }
                }

                // Original typed executor control values; never passed to Teaching.
                var request = Source("1.25", "14", "2/3");
                const string expected = "The measured total is 109/6.";
                var corpus = await db.LegendLanguageTextUnits.Select(item => item.Text).ToArrayAsync();
                Assert.DoesNotContain(request, corpus);
                Assert.DoesNotContain(expected, corpus);
                foreach (var value in new[] { "1.25", "14", "2/3", "109/6" })
                    Assert.False(await db.LegendLanguageMeaningNodeEvidence.AnyAsync(item => item.SemanticValue == value));

                var signatures = await db.LegendSemanticTransitionEvidence
                    .Where(item => item.SupersededUtc == null && item.FounderSemanticExampleRelationEvidenceId != null)
                    .Select(item => item.TransitionSignature).Distinct().ToArrayAsync();
                Assert.Equal(2, signatures.Length);
                var eligible = await curriculum.GetProductionEligibleSemanticTransitionSignaturesAsync("en", signatures);
                Assert.Equal(2, eligible.Count);

                var operations = services.GetRequiredService<ILegendConnectOperations>();
                var graph = await operations.AnalyzeReusableMeaningGraphAsync(request);
                Assert.True(graph.IsComposed, "source grounding: " + graph.ReasonCode);
                foreach (var (dimension, value) in new[] { ("rate", "5/4"), ("quantity", "14"), ("fee", "2/3") })
                {
                    var node = Assert.Single(graph.Nodes.Where(item => item.SemanticDimension == dimension));
                    Assert.Equal(value, node.SemanticValue);
                    Assert.Equal("CurrentTurnAssertion", node.Provenance);
                    var receipt = Assert.IsType<LegendConnectSourceSlotBinding>(node.SourceSlotBinding);
                    Assert.Equal(LegendLanguageIdentity.TextHash(request), receipt.NormalizedInputHash);
                    Assert.True(receipt.Evidence.Select(item => item.SourceFamilyId).Distinct().Count() >= 2);
                }
                Assert.DoesNotContain(graph.Nodes, item => item.SemanticDimension is "subtotal" or "total");
                var planned = await operations.TryPlanConversationAsync(request, new LegendConnectDiscourseStateSnapshot([]));
                Assert.True(planned.Supported, "plan: " + planned.ReasonCode);
                var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
                Assert.Equal("109/6", plan.ResultDimensions["total"]);
                Assert.Equal(2, (plan.ReasoningTransitionPath ?? []).Count);
                Assert.All(plan.ReasoningTransitionPath ?? [], signature => Assert.Contains(signature, eligible));
                Assert.True(plan.ReasoningEvidenceCount >= 3);

                var reply = await services.GetRequiredService<LegendFounderAiConversationService>().ReplyAsync(
                    ControllerTestHelpers.BuildUser(founderId), new LegendFounderAiChatRequest
                    {
                        Mode = "legend", NativeOnly = true, SourceLanguageCode = "en",
                        Messages = [new("user", request)]
                    });
                Assert.True(reply.Succeeded, "reply: " + reply.Reason + "; " + reply.Error);
                Assert.Equal(expected, reply.Message);
                Assert.Equal("LegendAi", reply.ResponseAuthority);
                Assert.Equal("native_response", reply.Stage);
                Assert.Equal(LegendConnectResearchEvidenceOrigin.InternalKnowledge, reply.EvidenceOrigin);
                Assert.Equal(plan.ReasoningTransitionPath, reply.ReasoningTransitionPath);
                Assert.Equal((0, 0), externalCounts());
            });
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", priorFounder); }
    }

    private static LegendConnectCurriculumBatchSubmission Teaching(string family)
    {
        var samples = family switch
        {
            "amber" => new[] { ("first", 2, 3, 1), ("second", 3, 5, 2) },
            "copper" => new[] { ("first", 4, 5, 3), ("second", 5, 6, 4) },
            _ => new[] { ("first", 6, 7, 5), ("second", 7, 8, 6) }
        };
        var examples = new List<LegendConnectCurriculumExampleSubmission>();
        foreach (var (sample, rate, quantity, fee) in samples)
        {
            var r = rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var q = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var f = fee.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var subtotal = (rate * quantity).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var total = (rate * quantity + fee).ToString(System.Globalization.CultureInfo.InvariantCulture);
            examples.Add(new(Source(r, q, f), Values(("rate", r), ("quantity", q), ("fee", f)),
                new([new("rate", "rate", r, r), new("quantity", "quantity", q, q), new("fee", "fee", f, f)],
                    [new("rate", "multiplied-by", "quantity"), new("rate", "with-fee", "fee")]), "chain-input-" + family + "-" + sample));
            examples.Add(new("Subtotal is " + subtotal + "; fee is " + f + ".", Values(("subtotal", subtotal), ("fee", f)),
                new([new("subtotal", "subtotal", subtotal, subtotal), new("fee", "fee", f, f)],
                    [new("subtotal", "plus-fee", "fee")]), "chain-subtotal-" + family + "-" + sample));
            examples.Add(new("Computed total: " + total + ".", Values(("total", total)),
                new([new("total", "total", total, total)], []), "chain-total-" + family + "-" + sample));
            examples.Add(new("The measured total is " + total + ".", Values(("total", total), ("conversation_function", "measurement_answer")),
                new([new("total", "total", total, total), new("function", "conversation_function", "measurement_answer", "The measured total is")], [])));
        }
        return new("arithmetic.measurement-chain." + family, "Measured rate, quantity and fee with two computed operations", examples,
            [new(new(Values(("rate", "$numeric_left"), ("quantity", "$numeric_right"), ("fee", "$fee"))), new(Values(("subtotal", "$numeric_result"), ("fee", "$fee")))),
             new(new(Values(("subtotal", "$numeric_left"), ("fee", "$numeric_right"))), new(Values(("total", "$numeric_result")))),
             new(new(Values(("total", "$value"))), new(Values(("total", "$value"), ("conversation_function", "measurement_answer"))))]);
    }
    private static string Source(string rate, string quantity, string fee) => "Rate is " + rate + "; quantity is " + quantity + "; fee is " + fee + ".";
    private static Dictionary<string, string> Values(params (string Dimension, string Value)[] values) =>
        values.ToDictionary(item => item.Dimension, item => item.Value, StringComparer.Ordinal);
}
