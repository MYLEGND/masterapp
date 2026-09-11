using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Real language admission and conversation-boundary contracts for homogeneous
/// batch allocation. An unseen workload must be bound through the governed
/// numeric template; no source graph or expected schedule is injected.
/// Named-site priorities and unique contacts are deliberately outside this
/// operator's authority and cannot inherit its feasibility certificate.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendConnectBatchLanguageEndToEndContractTests
{
    [Fact]
    public async Task UnseenWorkload_ProducesTheExactAllocationCertificateThroughReplyAsync()
    {
        await WithTeachingAsync(async (services, db) =>
        {
            var request = Source(19, 4, 7, 6, 3, 40);
            var corpus = await db.LegendLanguageTextUnits.Select(unit => unit.Text).ToArrayAsync();
            Assert.DoesNotContain(request, corpus);
            Assert.DoesNotContain(corpus, text => text.Contains("19", StringComparison.Ordinal));
            Assert.DoesNotContain(corpus, text => text.Contains("$schedule_signature", StringComparison.Ordinal));
            Assert.False(await db.LegendLanguageMeaningNodeEvidence.AnyAsync(node => node.SemanticDimension == "digest"));

            var operations = services.GetRequiredService<ILegendConnectOperations>();
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(request);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            Assert.Contains(graph.Nodes, node => node.SemanticDimension == "workload" && node.SemanticValue == "19");
            var workloadNode = Assert.Single(graph.Nodes.Where(node => node.SemanticDimension == "workload"));
            var binding = Assert.IsType<LegendConnectSourceSlotBinding>(workloadNode.SourceSlotBinding);
            Assert.Equal("en", binding.SourceLanguageCode);
            Assert.Equal(LegendLanguageIdentity.TextHash(request), binding.NormalizedInputHash);
            Assert.Equal(6, binding.Captures.Count);
            Assert.Contains(binding.Captures, item => item.SemanticDimension == "workload" &&
                item.SemanticVariable == "$schedule_workload" && item.SemanticValue == "19");
            Assert.All(graph.Nodes, node =>
            {
                Assert.NotNull(node.SourceSlotBinding);
                Assert.Equal(1, node.IndependentSupportCount);
                Assert.NotEqual(LegendConnectKnowledgeProvenance.FounderApproved, node.Provenance);
                Assert.Null(node.SourceMeaningNodeEvidenceId);
            });
            Assert.DoesNotContain(graph.Nodes, node => node.SemanticDimension is "batch_count" or "elapsed" or "digest");
            var planned = await operations.TryPlanConversationAsync(request, new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(planned.Supported, planned.ReasonCode);
            var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
            Assert.NotEmpty(plan.ReasoningTransitionPath ?? []);
            Assert.True(plan.ReasoningEvidenceCount >= 3);
            var plannedCertificate = Assert.Single(plan.ScheduleCertificates ?? []);

            var response = await ReplyAsync(services, request);
            Assert.True(response.Succeeded, response.Error);
            Assert.Equal("LegendAi", response.ResponseAuthority);
            Assert.Equal("native_response", response.Stage);
            Assert.Equal(LegendConnectResearchEvidenceOrigin.InternalKnowledge, response.EvidenceOrigin);
            Assert.Equal("Use 5 batches over 21 minutes.", response.Message);
            Assert.DoesNotContain(response.Message!, corpus);
            Assert.NotEmpty(response.ReasoningTransitionPath ?? []);
            var certificate = Assert.Single(response.ScheduleCertificates ?? []);
            Assert.Equal(plannedCertificate.Signature, certificate.Signature);
            Assert.Equal("digest", certificate.SignatureDimension);
            Assert.Matches("^[0-9a-f]{64}$", certificate.Signature);
            Assert.Equal("reasoning.constrained-planning.batch.language-proof", certificate.OperatorIdentity);
            Assert.Contains(certificate.TransitionSignature, response.ReasoningTransitionPath ?? []);
            Assert.True(certificate.IndependentEvidenceCount >= 3);
            Assert.Equal(certificate.IndependentEvidenceCount, certificate.IndependentEvidenceIdentities.Distinct().Count());
            Assert.Equal("19", certificate.Premises["workload"]);
            Assert.Equal("4", certificate.Premises["capacity"]);
            Assert.Equal("7", certificate.Premises["duration"]);
            Assert.Equal("6", certificate.Premises["available"]);
            Assert.Equal("3", certificate.Premises["required"]);
            Assert.Equal("40", certificate.Premises["time_limit"]);
            Assert.Equal("5", certificate.Conclusions["batch_count"]);
            Assert.Equal("21", certificate.Conclusions["elapsed"]);
            Assert.Equal("3", certificate.Conclusions["final_size"]);
            Assert.Equal("feasible", certificate.Conclusions["status"]);
            Assert.Equal(certificate.Signature, certificate.Conclusions["digest"]);
            Assert.Equal(5, certificate.Steps.Count);
            Assert.Equal(19, certificate.Steps.Sum(step => step.WorkUnits));
            for (var index = 0; index < certificate.Steps.Count; index++)
            {
                var step = certificate.Steps[index];
                Assert.Equal(index + 1, step.BatchNumber);
                Assert.Equal(index == 4 ? 3 : 4, step.WorkUnits);
                Assert.Equal(index / 2 * 7, step.StartMinute);
                Assert.Equal(index / 2 * 7 + 7, step.EndMinute);
                Assert.Equal(index % 2 * 3 + 1, step.FirstResourceUnit);
                Assert.Equal(3, step.ResourceUnitCount);
                Assert.InRange(step.FirstResourceUnit + step.ResourceUnitCount - 1, 1, 6);
                Assert.InRange(step.EndMinute, 1, 40);
            }
        });
    }

    [Fact]
    public async Task UnadmittedPriorityAndUniqueContactConstraints_CannotInheritHomogeneousFeasibility()
    {
        await WithTeachingAsync(async (services, _) =>
        {
            var request = Source(19, 4, 7, 6, 3, 40) +
                " Prioritize hazardous sites and never contact a site twice.";
            var native = await services.GetRequiredService<ILegendConnectOperations>().TryInferConversationWithDiscourseAsync(
                request, [], new LegendConnectDiscourseStateSnapshot([]), sourceLanguageCode: "en",
                providerPolicy: LegendConnectExternalProviderPolicy.NativeOnly);
            Assert.False(native.Supported,
                "A homogeneous work certificate cannot prove hazard priority or unique named-site contacts.");
            Assert.Null(native.Answer);
            Assert.Empty(native.ScheduleCertificates ?? []);
            Assert.Empty(native.ReasoningTransitionPath ?? []);
            var response = await ReplyAsync(services, request);
            Assert.True(response.Succeeded, response.Error);
            Assert.Equal("SystemDiagnostic", response.ResponseAuthority);
            Assert.Equal("native_only_blocked", response.Stage);
            Assert.Empty(response.ScheduleCertificates ?? []);
            Assert.Empty(response.ReasoningTransitionPath ?? []);
        });
    }

    private static async Task WithTeachingAsync(Func<IServiceProvider, MasterAppDbContext, Task> verify)
    {
        using var founderScope = new FounderScope();
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(async (services, db, externalCounts) =>
        {
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(), AgentUserId = FounderScope.FounderId,
                AgentUpn = "batch-proof@legend.test", NormalizedEmail = "batch-proof@legend.test", IsActive = true
            });
            await db.SaveChangesAsync();
            var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
            foreach (var family in new[] { "amber", "copper", "silver" })
            {
                var admitted = await curriculum.SubmitFounderBatchAsync(Teaching(family));
                Assert.True(admitted.Succeeded, admitted.Message);
                foreach (var sample in new[] { "first", "second" })
                    await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                        new("source-" + family + "-" + sample, "reasoning.constrained-planning.batch.language-proof",
                            "result-" + family + "-" + sample), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            }
            await verify(services, db);
            Assert.Equal((0, 0), externalCounts());
        });
    }

    private static LegendConnectCurriculumBatchSubmission Teaching(string family)
    {
        var samples = family switch
        {
            "amber" => new[] { ("first", 10, 4, 7, 6, 3, 40, 3, 14, 2), ("second", 18, 5, 9, 8, 2, 45, 4, 9, 3) },
            "copper" => new[] { ("first", 23, 6, 11, 12, 4, 50, 4, 22, 5), ("second", 17, 3, 8, 10, 5, 60, 6, 24, 2) },
            _ => new[] { ("first", 29, 7, 13, 9, 3, 70, 5, 26, 1), ("second", 31, 8, 17, 16, 4, 80, 4, 17, 7) }
        };
        var examples = new List<LegendConnectCurriculumExampleSubmission>();
        foreach (var (sample, work, capacity, duration, available, required, limit, batches, elapsed, final) in samples)
        {
            var inputs = Values(("workload", work.ToString()), ("capacity", capacity.ToString()), ("duration", duration.ToString()),
                ("available", available.ToString()), ("required", required.ToString()), ("time_limit", limit.ToString()));
            examples.Add(new(Source(work, capacity, duration, available, required, limit), inputs,
                new(inputs.Select(value => new LegendConnectMeaningNodeSubmission(value.Key, value.Key, value.Value, value.Value)).ToArray(),
                    inputs.Keys.Where(key => key != "workload").Select(key =>
                        new LegendConnectMeaningRelationSubmission("workload", "constrained-by", key)).ToArray()),
                "source-" + family + "-" + sample));
            var outputs = Values(("batch_count", batches.ToString()), ("elapsed", elapsed.ToString()),
                ("final_size", final.ToString()), ("status", "feasible"), ("digest", "$schedule_signature"));
            examples.Add(new($"Allocation: {batches} batches; duration {elapsed} minutes; final size {final}; status feasible.", outputs,
                new(outputs.Where(value => value.Key != "digest").Select(value =>
                    new LegendConnectMeaningNodeSubmission(value.Key, value.Key, value.Value, value.Value)).ToArray(), []),
                "result-" + family + "-" + sample));
            examples.Add(new($"Use {batches} batches over {elapsed} minutes.",
                Values(("batch_count", batches.ToString()), ("elapsed", elapsed.ToString()), ("conversation_function", "schedule_answer"),
                    ("batch_connector", "batch_duration_connector"), ("time_unit", "minutes")),
                new([new("count", "batch_count", batches.ToString(), batches.ToString()),
                     new("elapsed", "elapsed", elapsed.ToString(), elapsed.ToString()),
                     new("function", "conversation_function", "schedule_answer", "Use"),
                     new("connector", "batch_connector", "batch_duration_connector", "batches over"),
                     new("unit", "time_unit", "minutes", "minutes")], [])));
        }
        return new("computed.batch.language." + family, "Homogeneous batch work with explicit resource and time roles", examples,
            [new(new(Values(("workload", "$schedule_workload"), ("capacity", "$schedule_batch_capacity"),
                    ("duration", "$schedule_batch_duration"), ("available", "$schedule_available_resources"),
                    ("required", "$schedule_required_resources"), ("time_limit", "$schedule_time_limit"))),
                 new(Values(("batch_count", "$schedule_batch_count"), ("elapsed", "$schedule_elapsed"),
                    ("final_size", "$schedule_final_batch_size"), ("status", "$schedule_status"), ("digest", "$schedule_signature")))),
             new(new(Values(("batch_count", "$count"), ("elapsed", "$elapsed"), ("status", "feasible"))),
                 new(Values(("batch_count", "$count"), ("elapsed", "$elapsed"), ("conversation_function", "schedule_answer"))))]);
    }

    private static string Source(int work, int capacity, int duration, int available, int required, int limit) =>
        $"Schedule {work} units in batches of {capacity}, each taking {duration} minutes with {required} resources from {available} available, within {limit} minutes.";
    private static Dictionary<string, string> Values(params (string Dimension, string Value)[] values) =>
        values.ToDictionary(item => item.Dimension, item => item.Value, StringComparer.Ordinal);
    private static Task<LegendFounderAiChatResponse> ReplyAsync(IServiceProvider services, string request) =>
        services.GetRequiredService<LegendFounderAiConversationService>().ReplyAsync(ControllerTestHelpers.BuildUser(FounderScope.FounderId),
            new LegendFounderAiChatRequest { Mode = "legend", NativeOnly = true, SourceLanguageCode = "en", Messages = [new("user", request)] });
    private sealed class FounderScope : IDisposable
    {
        public const string FounderId = "a1558f73-9e8d-4486-a290-4a33fe44b58e";
        private readonly string? _previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        public FounderScope() => Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
        public void Dispose() => Environment.SetEnvironmentVariable("FOUNDER_OID", _previous);
    }
}
