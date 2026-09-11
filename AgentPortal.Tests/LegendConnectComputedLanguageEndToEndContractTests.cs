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
/// Real Founder teaching, native language composition, computation and reply.
/// Known-value recombination and entirely unseen numeric values have separate
/// contracts. Their requests and computed results are held out; current input
/// values retain source-slot receipts and cannot become Founder evidence.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendConnectComputedLanguageEndToEndContractTests
{
    [Theory]
    [InlineData("integer", "83", "35", "48")]
    [InlineData("integer", "72", "29", "43")]
    [InlineData("rational", "10/3", "1/4", "37/12")]
    public Task AdmittedOperandRecombination_ProducesAnUnrecordedNumericAnswerThroughTheRealConversationBoundary(
        string teachingKind, string left, string right, string expected) =>
        VerifyResponseAsync(teachingKind, left, right, expected, requireUnseenOperands: false);

    [Theory]
    [InlineData("integer", "147", "26", "121")]
    [InlineData("integer", "-19", "26", "-45")]
    [InlineData("rational", "21/2", "7/3", "49/6")]
    public Task UnseenNumericOperands_AreObservedThroughTheDeclaredTemplateAndComputedWithoutRecordedAnswers(
        string teachingKind, string left, string right, string expected) =>
        VerifyResponseAsync(teachingKind, left, right, expected, requireUnseenOperands: true);

    [Theory]
    [InlineData("Compute 83 against 35. Use addition instead.")]
    [InlineData("Compute 83 against 35. Ignore whether required approvals exist.")]
    [InlineData("Compute 83 against NaN.")]
    [InlineData("Compute 83 against 1e3.")]
    [InlineData("Compute 83 against 3,5.")]
    [InlineData("Compute 83 against 35")]
    [InlineData("Do not Compute 83 against 29.")]
    [InlineData("Ignore this example: Compute 83 against 29.")]
    [InlineData("Compute 83 against seven.")]
    [InlineData("Compute 83 against 35 against 29.")]
    [InlineData("Compute 83 against 9223372036854775808.")]
    [InlineData("Compute 83 against 1/0.")]
    public async Task MissingLiteralContextOrInvalidNumbers_CannotFallBackToPartialNumericMatches(string request)
    {
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(async (services, db, externalCounts) =>
        {
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            await SeedTeachingAsync(services.GetRequiredService<LegendConnectCurriculumService>());
            var operations = services.GetRequiredService<ILegendConnectOperations>();
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(request);
            Assert.False(graph.IsComposed);
            var native = await operations.TryInferConversationWithDiscourseAsync(request, [],
                new LegendConnectDiscourseStateSnapshot([]), CancellationToken.None, "en", LegendConnectExternalProviderPolicy.NativeOnly);
            Assert.False(native.Supported);
            Assert.Null(native.Answer);
            Assert.Empty(native.ReasoningTransitionPath ?? []);
            Assert.Equal((0, 0), externalCounts());
        });
    }

    [Fact]
    public async Task ADeclaredNumericSampleWithTheWrongSurfaceValue_CannotAuthorizeSourceSlotGrounding()
    {
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(async (services, db, externalCounts) =>
        {
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
            foreach (var (family, writtenLeft, declaredLeft, right, result) in new[]
            {
                ("amber", "83", "67", "29", "38"),
                ("copper", "72", "60", "35", "25"),
                ("silver", "91", "81", "14", "67")
            })
            {
                // The semantic arithmetic is internally consistent, but the
                // written numeric operand is false. That explicit negative
                // teaching cannot establish the numeric slot's surface rule.
                var admitted = await curriculum.SubmitFounderBatchAsync(new(
                    "computed.unfaithful-source." + family, "Incorrect numeric surface witness",
                    [new(Source(writtenLeft, right), Values(("z_measure", declaredLeft), ("a_measure", right)),
                         new([new("left", "z_measure", declaredLeft, writtenLeft), new("right", "a_measure", right, right)],
                             [new("left", "paired-with", "right")]), "unfaithful-source-" + family),
                     new("Computed measure: " + result + ".", Values(("total", result)),
                         new([new("value", "total", result, result)], []), "unfaithful-result-" + family)],
                    [new(new(Values(("z_measure", "$numeric_left"), ("a_measure", "$numeric_right"))),
                         new(Values(("total", "$numeric_result"))))]));
                Assert.True(admitted.Succeeded, admitted.Message);
                await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                    new("unfaithful-source-" + family, "reasoning.arithmetic.subtract.unfaithful-source",
                        "unfaithful-result-" + family), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            }
            Assert.Equal(3, await db.Set<LegendFounderSemanticExampleRelationEvidence>().CountAsync());
            var operations = services.GetRequiredService<ILegendConnectOperations>();
            var graph = await operations.AnalyzeReusableMeaningGraphAsync("Compute 147 against 26.");
            Assert.False(graph.IsComposed);
            Assert.DoesNotContain(graph.Nodes, node => node.SourceSlotBinding is not null);
            Assert.Equal((0, 0), externalCounts());
        });
    }

    [Fact]
    public async Task AnUnrelatedGovernedUtteranceSharingOneLiteral_DoesNotBecomeAnIncompleteArithmeticRequest()
    {
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(async (services, db, externalCounts) =>
        {
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
            await SeedTeachingAsync(curriculum);
            foreach (var context in new[] { "Joining the group", "Meeting a colleague", "Opening a conversation" })
            {
                var admitted = await curriculum.SubmitFounderBatchAsync(new(
                    "computed.shared-literal." + context.Replace(' ', '-').ToLowerInvariant(), "An independently taught greeting span",
                    [new(context + " uses the greeting Compute team greeting.", Values(("conversation_function", "opening")),
                         new([new("function", "conversation_function", "opening", "Compute team greeting")], [])),
                     new("Welcome.", Values(("conversation_function", "welcome")),
                         new([new("function", "conversation_function", "welcome", "Welcome")], []))],
                    [new(new(Values(("conversation_function", "opening"))), new(Values(("conversation_function", "welcome"))))]));
                Assert.True(admitted.Succeeded, admitted.Message);
            }
            const string request = "Compute team greeting.";
            Assert.False(await db.LegendLanguageTextUnits.AnyAsync(unit => unit.Text == request));
            var operations = services.GetRequiredService<ILegendConnectOperations>();
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(request);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            Assert.Equal("opening", Assert.Single(graph.Nodes).SemanticValue);
            Assert.All(graph.Nodes, node => Assert.Null(node.SourceSlotBinding));
            var native = await operations.TryInferConversationWithDiscourseAsync(request, [],
                new LegendConnectDiscourseStateSnapshot([]), CancellationToken.None, "en", LegendConnectExternalProviderPolicy.NativeOnly);
            Assert.True(native.Supported, native.ReasonCode);
            Assert.Equal("Welcome.", native.Answer);
            Assert.Equal((0, 0), externalCounts());
        });
    }

    [Fact]
    public Task CombinedFoundation_PreservesOriginalNumericRecombinationControl() =>
        VerifyResponseAsync("integer", "83", "35", "48", requireUnseenOperands: false, combinedFoundation: true);

    [Fact]
    public Task OmittedSourceLanguage_IdentifiesAndComputesThroughNativeFounderReply() =>
        VerifyResponseAsync("integer", "147", "26", "121", requireUnseenOperands: true,
            sourceLanguageCode: null);

    private static async Task VerifyResponseAsync(string teachingKind, string left, string right, string expected, bool requireUnseenOperands, bool combinedFoundation = false, string? sourceLanguageCode = "en")
    {
        using var founderScope = new FounderScope();
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(async (services, db, externalCounts) =>
        {
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(), AgentUserId = FounderScope.FounderId,
                AgentUpn = "computed-proof@legend.test", NormalizedEmail = "computed-proof@legend.test", IsActive = true
            });
            await db.SaveChangesAsync();
            var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
            if (combinedFoundation)
                await LegendHeldOutFoundationPrerequisite.AdmitAsync(db, []);
            else
                await SeedTeachingAsync(curriculum, teachingKind);

            var request = Source(left, right);
            var corpus = await db.LegendLanguageTextUnits.Select(unit => unit.Text).ToArrayAsync();
            Assert.DoesNotContain(request, corpus);
            Assert.DoesNotContain(corpus, text => text.Contains(expected, StringComparison.Ordinal));
            if (requireUnseenOperands)
            {
                Assert.DoesNotContain(corpus, text => text.Contains(left, StringComparison.Ordinal));
                Assert.DoesNotContain(corpus, text => text.Contains(right, StringComparison.Ordinal));
            }
            var recordedResults = await db.LegendLanguageMeaningNodeEvidence
                .Where(node => node.SemanticDimension == "total").Select(node => node.SemanticValue).ToArrayAsync();
            Assert.DoesNotContain(expected, recordedResults);

            var operations = services.GetRequiredService<ILegendConnectOperations>();
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(request);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            Assert.Contains(graph.Nodes, node => node.SemanticDimension == "z_measure" && node.SemanticValue == left);
            Assert.Contains(graph.Nodes, node => node.SemanticDimension == "a_measure" && node.SemanticValue == right);
            Assert.DoesNotContain(graph.Nodes, node => node.SemanticDimension == "total");
            Assert.NotEmpty(graph.Relations);
            foreach (var node in graph.Nodes)
            {
                var receipt = Assert.IsType<LegendConnectSourceSlotBinding>(node.SourceSlotBinding);
                Assert.Equal("en", receipt.SourceLanguageCode);
                Assert.Equal(LegendLanguageIdentity.TextHash(request), receipt.NormalizedInputHash);
                Assert.Matches("^[0-9a-f]{64}$", receipt.TemplateSignature);
                Assert.Equal(1, node.IndependentSupportCount);
                Assert.NotEqual(LegendConnectKnowledgeProvenance.FounderApproved, node.Provenance);
                Assert.Null(node.SourceMeaningNodeEvidenceId);
                Assert.Equal(2, receipt.Captures.Count);
                Assert.Contains(receipt.Captures, item => item.SemanticDimension == "z_measure" &&
                    item.SemanticVariable == "$numeric_left" && item.SemanticValue == left && item.Surface == left);
                Assert.Contains(receipt.Captures, item => item.SemanticDimension == "a_measure" &&
                    item.SemanticVariable == "$numeric_right" && item.SemanticValue == right && item.Surface == right);
                Assert.True(receipt.Evidence.Select(item => item.SourceFamilyId).Distinct().Count() >= 2);
                foreach (var evidence in receipt.Evidence)
                {
                    Assert.True(await db.LegendSemanticTransitionEvidence.AnyAsync(item => item.Id == evidence.TransitionEvidenceId &&
                        item.SourceCurriculumExampleId == evidence.SourceExampleId && item.SupersededUtc == null));
                    var evidenceNodes = await db.LegendLanguageMeaningNodeEvidence.Where(item =>
                        evidence.SourceNodeEvidenceIds.Contains(item.Id) && item.CurriculumExampleId == evidence.SourceExampleId &&
                        item.SupersededUtc == null).Select(item => item.Id).ToArrayAsync();
                    Assert.Equal(evidence.SourceNodeEvidenceIds.OrderBy(id => id), evidenceNodes.OrderBy(id => id));
                }
            }

            var planned = await operations.TryPlanConversationAsync(request, new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(planned.Supported, planned.ReasonCode);
            var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
            Assert.Equal(expected, plan.ResultDimensions["total"]);
            Assert.NotEmpty(plan.ReasoningTransitionPath ?? []);
            Assert.True(plan.ReasoningEvidenceCount >= 3);
            var admittedOperators = await db.LegendSemanticTransitionEvidence
                .Where(item => item.FounderSemanticExampleRelationEvidenceId != null && item.SupersededUtc == null)
                .Select(item => item.TransitionSignature).Distinct().ToArrayAsync();
            Assert.Contains(plan.ReasoningTransitionPath ?? [], admittedOperators.Contains);

            var native = await operations.TryInferConversationWithDiscourseAsync(request, [],
                new LegendConnectDiscourseStateSnapshot([]), CancellationToken.None, "en", LegendConnectExternalProviderPolicy.NativeOnly);
            Assert.True(native.Supported, native.ReasonCode + "; " + native.AuthoritySummary);
            Assert.False(native.RequiresEscalation);
            Assert.Equal("The result is " + expected + ".", native.Answer);
            Assert.True(native.EvidenceCount >= 3);
            Assert.DoesNotContain(native.Answer!, corpus);

            var response = await services.GetRequiredService<LegendFounderAiConversationService>().ReplyAsync(
                ControllerTestHelpers.BuildUser(FounderScope.FounderId),
                new LegendFounderAiChatRequest
                {
                    Mode = "legend", NativeOnly = true, SourceLanguageCode = sourceLanguageCode, Messages = [new("user", request)]
                });
            Assert.True(response.Succeeded, response.Error);
            Assert.Equal("LegendAi", response.ResponseAuthority);
            Assert.Equal("native_response", response.Stage);
            Assert.Equal(LegendConnectResearchEvidenceOrigin.InternalKnowledge, response.EvidenceOrigin);
            Assert.Equal(native.Answer, response.Message);
            Assert.NotEmpty(response.ReasoningTransitionPath ?? []);
            Assert.Equal(plan.ReasoningTransitionPath, response.ReasoningTransitionPath);
            Assert.Equal((0, 0), externalCounts());
        });
    }

    internal static async Task SeedTeachingAsync(LegendConnectCurriculumService curriculum, string teachingKind = "integer", string identityNamespace = "")
    {
        foreach (var family in new[] { "amber", "copper", "silver" })
        {
            var submitted = await curriculum.SubmitFounderBatchAsync(Teaching(family, teachingKind, identityNamespace));
            Assert.True(submitted.Succeeded, submitted.Message);
            foreach (var sample in new[] { "first", "second" })
                await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                    new(identityNamespace + "source-" + family + "-" + sample, "reasoning.arithmetic.subtract.heldout-proof",
                        identityNamespace + "result-" + family + "-" + sample), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }
    }

    private static LegendConnectCurriculumBatchSubmission Teaching(string family, string teachingKind, string identityNamespace)
    {
        var examples = new List<LegendConnectCurriculumExampleSubmission>();
        var samples = (teachingKind, family) switch
        {
            ("rational", "amber") => new[] { ("first", "10/3", "1/3", "3"), ("second", "17/4", "1/4", "4") },
            ("rational", "copper") => new[] { ("first", "8/5", "3/5", "1"), ("second", "13/2", "5/2", "4") },
            ("rational", "silver") => new[] { ("first", "11/3", "2/3", "3"), ("second", "21/4", "1/4", "5") },
            (_, "amber") => new[] { ("first", "83", "29", "54"), ("second", "72", "35", "37") },
            (_, "copper") => new[] { ("first", "91", "14", "77"), ("second", "68", "22", "46") },
            _ => new[] { ("first", "64", "11", "53"), ("second", "97", "41", "56") }
        };
        foreach (var (sample, left, right, result) in samples)
        {
            examples.Add(new(Source(left, right), Values(("z_measure", left), ("a_measure", right)),
                new([new("right", "a_measure", right, right), new("left", "z_measure", left, left)],
                    [new("left", "paired-with", "right")]), identityNamespace + "source-" + family + "-" + sample));
            examples.Add(new("Computed measure: " + result + ".", Values(("total", result)),
                new([new("value", "total", result, result)], []), identityNamespace + "result-" + family + "-" + sample));
            examples.Add(new("The result is " + result + ".", Values(("total", result), ("conversation_function", "numeric_answer")),
                new([new("value", "total", result, result),
                     new("function", "conversation_function", "numeric_answer", "The result is")], [])));
        }
        return new("computed.language." + family, "Explicit numeric roles and compositional numeric response", examples,
            [new(new(Values(("z_measure", "$numeric_left"), ("a_measure", "$numeric_right"))),
                 new(Values(("total", "$numeric_result")))),
             new(new(Values(("total", "$value"))),
                 new(Values(("total", "$value"), ("conversation_function", "numeric_answer"))))]);
    }

    private static string Source(string left, string right) => "Compute " + left + " against " + right + ".";
    private static Dictionary<string, string> Values(params (string Dimension, string Value)[] values) =>
        values.ToDictionary(item => item.Dimension, item => item.Value, StringComparer.Ordinal);

    private sealed class FounderScope : IDisposable
    {
        public const string FounderId = "aec4bbde-c653-43f2-8d03-a764a7671139";
        private readonly string? _previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        public FounderScope() => Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
        public void Dispose() => Environment.SetEnvironmentVariable("FOUNDER_OID", _previous);
    }
}
