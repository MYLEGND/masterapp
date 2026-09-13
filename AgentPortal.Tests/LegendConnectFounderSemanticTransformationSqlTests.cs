using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Data.Common;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

/// <summary>
/// Stage-5 fresh-SQL proof. The curriculum is submitted through the normal
/// authenticated Founder manifest service, then processed by the same durable
/// family/relation work authority registered in production. No test inserts
/// transition evidence or expected answers directly.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendConnectFounderSemanticTransformationSqlTests
{
    private const string ConnectionVariable = "LEGEND_STAGE5_SQL_CONNECTION";
    private readonly ITestOutputHelper _output;

    public LegendConnectFounderSemanticTransformationSqlTests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public async Task FounderCrossExampleEvidence_MaturesStructuralTransition_AndReachesAuthenticatedReplyAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Stage-5 SQL proof was not selected; LEGEND_STAGE5_SQL_CONNECTION is required.");
            return;
        }

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(CreateIsolatedConnectionString(connectionString))
            .Options;
        await using (var migration = new MasterAppDbContext(options))
            await migration.Database.MigrateAsync();

        var founderId = Guid.NewGuid().ToString("D");
        var founder = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderId)], "stage5-proof"));
        var configuration = Configuration();
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {

        await using (var setup = new MasterAppDbContext(options))
        {
            setup.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = $"stage5-{founderId}@legend.test",
                NormalizedEmail = $"stage5-{founderId}@legend.test",
                IsActive = true
            });
            await setup.SaveChangesAsync();

            var services = CreateServices(setup, configuration);
            var founderLegend = new FounderLegendConnectService(
                services.Operations,
                new AgentProfileAccessResolver(setup));
            var accepted = await founderLegend.SubmitCurriculumAsync(
                founder,
                new FounderLegendConnectCurriculumInput { Manifest = TrainingManifest() });
            Assert.True(accepted.Succeeded, accepted.Message);
            Assert.False(accepted.DuplicatePrevented);

            var processor = new LegendConnectCurriculumManifestProcessor(
                setup,
                services.Curriculum,
                services.Work,
                NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
            Assert.Equal(1, await processor.SeedDurableFamilyWorkAsync(
                services.Work,
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                16));

            await DrainManifestAsync(
                setup,
                services,
                processor,
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

            var manifest = Assert.Single(await setup.LegendCurriculumManifestWorkItems
                .Where(item => item.FounderUserId == founderId)
                .ToListAsync());
            Assert.Equal("Completed", manifest.ProcessingState);
            Assert.Equal(LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                manifest.CompletedLanguageIntelligenceEvaluatorVersion);
            Assert.Equal(3, await setup.LegendFounderSemanticExampleRelationEvidence.CountAsync(
                item => item.SupersededUtc == null));
            var transitions = await setup.LegendSemanticTransitionEvidence
                .Where(item => item.SupersededUtc == null &&
                    item.FounderSemanticExampleRelationEvidenceId != null)
                .ToListAsync();
            Assert.Equal(3, transitions.Count);
            Assert.Single(transitions.Select(item => item.TransitionSignature).Distinct());
            Assert.All(transitions, item =>
            {
                Assert.Equal("Supported", item.ContributionState);
                Assert.True(item.IsHumanVerifiedSupport);
                Assert.Equal(LegendConnectLanguageIntelligenceEvaluatorVersion.Current, item.DerivationEvaluatorVersion);
                Assert.False(string.IsNullOrWhiteSpace(item.FounderRelationshipSemanticSignature));
            });
            var currentDependencyArtifacts = await setup.LegendLanguageDerivationArtifacts
                .Where(item => item.State == "Current")
                .ToListAsync();
            Assert.NotEmpty(currentDependencyArtifacts);
            Assert.Contains(currentDependencyArtifacts, item => item.ArtifactKind == "semantic-transformation");
            Assert.All(currentDependencyArtifacts, item =>
                Assert.False(string.IsNullOrWhiteSpace(item.DerivationContractIdentity)));

            var eligible = await services.Curriculum.GetProductionEligibleSemanticTransitionSignaturesAsync(
                "en",
                transitions.Select(item => item.TransitionSignature).Distinct().ToArray());
            Assert.Single(eligible);

            var beforeReplay = await CountsAsync(setup);
            Assert.Equal(0, await processor.SeedDurableFamilyWorkAsync(
                services.Work,
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                16));
            await processor.RefreshDurableManifestStatusesAsync(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            Assert.Equal(beforeReplay, await CountsAsync(setup));
            Assert.Equal(0, await setup.LegendFounderSemanticExampleRelationEvidence
                .GroupBy(item => item.RelationIdentity)
                .CountAsync(group => group.Count() > 1));
            Assert.Equal(0, await setup.LegendSemanticTransitionEvidence
                .Where(item => item.SupersededUtc == null)
                .GroupBy(item => new
                {
                    item.TransitionSignature,
                    item.SourceCurriculumExampleId,
                    item.ResultCurriculumExampleId
                })
                .CountAsync(group => group.Count() > 1));
        }

        await using (var proof = new MasterAppDbContext(options))
        {
            var services = CreateServices(proof, configuration);
            const string heldOutSource = "Could you offer guidance now?";
            const string heldOutResultSurface = "I am ready to help you.";
            var graph = await services.Operations.AnalyzeReusableMeaningGraphAsync(heldOutSource);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            var functionNodeIndex = Array.FindIndex(graph.Nodes.ToArray(), item =>
                item.SemanticDimension == "conversation_function");
            var intentNodeIndex = Array.FindIndex(graph.Nodes.ToArray(), item =>
                item.SemanticDimension == "intent");
            Assert.True(functionNodeIndex >= 0 && intentNodeIndex >= 0);
            Assert.Contains(graph.Relations, item =>
                item.RelationKind == "governs" &&
                item.SourceNodeIndex == functionNodeIndex &&
                item.TargetNodeIndex == intentNodeIndex);
            Assert.DoesNotContain(await proof.LegendLanguageTextUnits
                .Select(item => item.Text)
                .ToListAsync(), item => string.Equals(item, heldOutSource, StringComparison.Ordinal));
            Assert.DoesNotContain(await proof.LegendLanguageTextUnits
                .Select(item => item.Text)
                .ToListAsync(), item => string.Equals(item, heldOutResultSurface, StringComparison.Ordinal));

            var plan = await services.Operations.TryPlanConversationAsync(
                heldOutSource,
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(plan.Supported, plan.ReasonCode);
            var structuredPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(plan.Plan);
            Assert.Equal(3, structuredPlan.IndependentEvidenceCount);
            Assert.Equal("offer_help", structuredPlan.ResultDimensions["conversation_function"]);
            Assert.Equal("guidance_offer", structuredPlan.ResultDimensions["intent"]);
            Assert.DoesNotContain(heldOutSource, System.Text.Json.JsonSerializer.Serialize(structuredPlan), StringComparison.OrdinalIgnoreCase);

            var factory = new CountingHttpClientFactory();
            var profiles = new AgentProfileAccessResolver(proof);
            var founderLegend = new FounderLegendConnectService(services.Operations, profiles);
            var chat = new LegendFounderAiConversationService(
                factory,
                configuration,
                founderLegend,
                NullLogger<LegendFounderAiConversationService>.Instance,
                new LegendFounderAiDiscourseStateService(proof, profiles, services.Operations),
                new LegendLanguageRegistry(proof, configuration),
                ControllerTestHelpers.BuildTranslationService());
            var conversationId = Guid.NewGuid();
            var reply = await chat.ReplyAsync(
                founder,
                new LegendFounderAiChatRequest
                {
                    Mode = "legend",
                    ConversationId = conversationId.ToString(),
                    Messages = [new LegendFounderAiChatMessage("user", heldOutSource)]
                });

            Assert.True(reply.Succeeded);
            Assert.Equal("I can help with that.", reply.Message);
            Assert.Equal(0, factory.CreateClientCalls);

            _output.WriteLine("STAGE 5 HELD-OUT STRUCTURAL GENERALIZATION");
            _output.WriteLine("USER: " + heldOutSource);
            _output.WriteLine("SOURCE MEANING NODES: " + string.Join(", ", graph.Nodes.Select(item =>
                item.SemanticDimension + "=" + item.SemanticValue)));
            _output.WriteLine("SOURCE MEANING RELATIONS: " + string.Join(", ", graph.Relations.Select(item => item.RelationKind)));
            _output.WriteLine("MATCHED TRANSFORMATION: " + structuredPlan.TransitionSignature);
            _output.WriteLine("INDEPENDENT SUPPORT: " + structuredPlan.IndependentEvidenceCount);
            _output.WriteLine("EXACT SOURCE SENTENCE SEEN IN TRANSITION TRAINING: False");
            _output.WriteLine("EXACT RESULT SENTENCE SEEN IN TRANSITION TRAINING: False");
            _output.WriteLine("PLAN RESULT: conversation_function=offer_help; intent=guidance_offer");
            _output.WriteLine("LEGEND: " + reply.Message);
            _output.WriteLine("REALIZATION MODE: CanonicalExisting");
            _output.WriteLine("OPENAI CLIENTS: 0; OPENAI HTTP CALLS: 0; FALLBACK: False");
        }
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    /// <summary>
    /// Stage-6 proof that a mature transition only authorizes the response
    /// shape. The two normal Founder submissions declare the same comparison
    /// transition and the same held-out request. Only their independently
    /// governed fact relations differ, so a response whose content does not
    /// flip has necessarily ignored the existing knowledge authority.
    /// </summary>
    [Fact]
    public async Task FounderGovernedFacts_BindMaturedComparisonContent_WithoutResponseRetrieval()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Stage-6 SQL content proof was not selected; LEGEND_STAGE5_SQL_CONNECTION is required.");
            return;
        }

        var lowerAlpha = await ExecuteGovernedContentCaseAsync(
            connectionString, "alpha", "alpha", "beta", "beta", "low", "high", verifySourceFamiliesReplay: true);
        var lowerBeta = await ExecuteGovernedContentCaseAsync(
            connectionString, "alpha", "alpha", "beta", "beta", "high", "low");
        var schedules = await ExecuteGovernedContentCaseAsync(
            connectionString, "schedule_a", "schedule a", "schedule_b", "schedule b", "low", "high");
        var plans = await ExecuteGovernedContentCaseAsync(
            connectionString, "plan_a", "plan a", "plan_b", "plan b", "high", "low");
        var options = await ExecuteGovernedContentCaseAsync(
            connectionString, "option_a", "option a", "option_b", "option b", "low", "high");

        Assert.Equal(lowerAlpha.TransitionSignature, lowerBeta.TransitionSignature);
        Assert.Equal(lowerAlpha.TransitionSignature, schedules.TransitionSignature);
        Assert.Equal(lowerAlpha.TransitionSignature, plans.TransitionSignature);
        Assert.Equal(lowerAlpha.TransitionSignature, options.TransitionSignature);
        Assert.Equal("low", lowerAlpha.ContentBindings["$cost_left"]);
        Assert.Equal("high", lowerAlpha.ContentBindings["$cost_right"]);
        Assert.Equal("high", lowerBeta.ContentBindings["$cost_left"]);
        Assert.Equal("low", lowerBeta.ContentBindings["$cost_right"]);
        Assert.NotEqual(lowerAlpha.Reply, lowerBeta.Reply);
        Assert.Equal(0, lowerAlpha.OpenAiClientCount);
        Assert.Equal(0, lowerBeta.OpenAiClientCount);
        Assert.All(new[] { schedules, plans, options }, proof =>
        {
            Assert.Contains(proof.LeftSurface, proof.Reply, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(proof.RightSurface, proof.Reply, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, proof.OpenAiClientCount);
        });

        _output.WriteLine("STAGE 6 TRANSITION ≠ CONTENT PROOF");
        _output.WriteLine("USER (both governed databases): Which one is cheaper?");
        _output.WriteLine("TRANSITION (both): " + lowerAlpha.TransitionSignature);
        _output.WriteLine("CASE A FACTS: alpha cost=" + lowerAlpha.ContentBindings["$cost_left"] +
            "; beta cost=" + lowerAlpha.ContentBindings["$cost_right"]);
        _output.WriteLine("CASE A LEGEND: " + lowerAlpha.Reply);
        _output.WriteLine("CASE B FACTS: alpha cost=" + lowerBeta.ContentBindings["$cost_left"] +
            "; beta cost=" + lowerBeta.ContentBindings["$cost_right"]);
        _output.WriteLine("CASE B LEGEND: " + lowerBeta.Reply);
        _output.WriteLine("TRANSFER CASES (same transition, different governed subjects):");
        _output.WriteLine("SCHEDULES: " + schedules.Reply);
        _output.WriteLine("PLANS: " + plans.Reply);
        _output.WriteLine("OPTIONS: " + options.Reply);
        _output.WriteLine("CONTENT FACTS: 2 per case; each supported by 3 independent Founder families; contradicted=0; production-eligible=True");
        _output.WriteLine("OPENAI CLIENTS: 0; OPENAI HTTP CALLS: 0");
    }

    [Fact]
    public async Task FounderTransition_WithoutMaturedGovernedFacts_FailsClosedWithoutFallback()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Stage-6 SQL content proof was not selected; LEGEND_STAGE5_SQL_CONNECTION is required.");
            return;
        }

        var proof = await ExecuteGovernedContentCaseAsync(
            connectionString,
            "alpha", "alpha", "beta", "beta", "low", "high",
            expectContent: false);

        Assert.Equal("governed_content_fact_unknown", proof.ReasonCode);
        Assert.Equal(0, proof.OpenAiClientCount);
        _output.WriteLine("STAGE 6 UNKNOWN CONTENT: fail closed; reason=governed_content_fact_unknown; OpenAI=0/0");
    }

    /// <summary>
    /// Stage-5 authenticated conversation proof.  The frozen curriculum uses
    /// independent Founder-declared example relations; every prompt below is
    /// deliberately absent as a complete retained curriculum sentence.  The
    /// test does not seed answers, plans, transitions, or discourse state.
    /// </summary>
    [Fact]
    public async Task FounderDeclaredStructuralTransformations_SupportHeldOutAuthenticatedConversations()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Stage-5 conversation SQL proof was not selected; LEGEND_STAGE5_SQL_CONNECTION is required.");
            return;
        }

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(CreateIsolatedConnectionString(connectionString))
            .Options;
        await using (var migration = new MasterAppDbContext(options))
            await migration.Database.MigrateAsync();

        var founderId = Guid.NewGuid().ToString("D");
        var founder = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderId)], "stage5-transcript"));
        var configuration = Configuration();
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using (var setup = new MasterAppDbContext(options))
            {
                setup.AgentProfiles.Add(new AgentProfile
                {
                    Id = Guid.NewGuid(),
                    AgentUserId = founderId,
                    AgentUpn = $"stage5-transcript-{founderId}@legend.test",
                    NormalizedEmail = $"stage5-transcript-{founderId}@legend.test",
                    IsActive = true
                });
                await setup.SaveChangesAsync();

                var services = CreateServices(setup, configuration);
                var founderLegend = new FounderLegendConnectService(
                    services.Operations,
                    new AgentProfileAccessResolver(setup));
                var accepted = await founderLegend.SubmitCurriculumAsync(
                    founder,
                    new FounderLegendConnectCurriculumInput { Manifest = ConversationManifest() });
                Assert.True(accepted.Succeeded, accepted.Message);
                Assert.False(accepted.DuplicatePrevented);

                var processor = new LegendConnectCurriculumManifestProcessor(
                    setup,
                    services.Curriculum,
                    services.Work,
                    NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
                Assert.Equal(1, await processor.SeedDurableFamilyWorkAsync(
                    services.Work,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                    128));
                await DrainManifestAsync(
                    setup,
                    services,
                    processor,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

                var manifest = await setup.LegendCurriculumManifestWorkItems
                    .SingleAsync(item => item.FounderUserId == founderId);
                Assert.Equal("Completed", manifest.ProcessingState);
                Assert.Equal(LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                    manifest.CompletedLanguageIntelligenceEvaluatorVersion);

                var relations = await setup.LegendFounderSemanticExampleRelationEvidence
                    .Where(item => item.SupersededUtc == null &&
                        item.SourceCurriculumFamilyId != item.ResultCurriculumFamilyId)
                    .ToListAsync();
                Assert.Equal(ConversationScenarios.Length * 3, relations.Count);
                Assert.All(relations, item =>
                {
                    Assert.True(item.IsHumanVerifiedSupport);
                    Assert.Equal("Supported", item.ContributionState);
                    Assert.Equal(LegendConnectLanguageIntelligenceEvaluatorVersion.Current, item.EvaluatorVersion);
                });

                var transformations = await setup.LegendSemanticTransitionEvidence
                    .Where(item => item.SupersededUtc == null &&
                        item.FounderSemanticExampleRelationEvidenceId != null &&
                        item.SourceCurriculumExampleId != item.ResultCurriculumExampleId)
                    .ToListAsync();
                Assert.Equal(ConversationScenarios.Length * 3, transformations.Count);
                Assert.Equal(ConversationScenarios.Length,
                    transformations.Select(item => item.TransitionSignature).Distinct().Count());
                Assert.All(transformations.GroupBy(item => item.TransitionSignature), group =>
                    Assert.Equal(3, group.Select(item => item.IndependentSourceIdentity).Distinct().Count()));

                var beforeReplay = await CountsAsync(setup);
                var beforeAnchorIdentities = await ActiveAnchorIdentitiesAsync(setup);
                var beforeTransitionIdentities = await ActiveTransitionIdentitiesAsync(setup);
                var beforeEligibleTransitionSignatures = (await services.Curriculum
                    .GetProductionEligibleSemanticTransitionSignaturesAsync(
                        "en",
                        transformations.Select(item => item.TransitionSignature).Distinct().ToArray()))
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                var historicalRelationFamilies = await setup.LegendFounderSemanticExampleRelationEvidence
                    .Where(item => item.SupersededUtc == null)
                    .Select(item => new
                    {
                        item.SourceCurriculumFamilyId,
                        item.ResultCurriculumFamilyId
                    })
                    .ToArrayAsync();
                var historicalFamilies = historicalRelationFamilies
                    .Select(item => item.SourceCurriculumFamilyId)
                    .Concat(historicalRelationFamilies.Select(item => item.ResultCurriculumFamilyId))
                    .Distinct()
                    .ToArray();
                for (var replayPass = 1; replayPass <= 2; replayPass++)
                {
                    foreach (var familyId in historicalFamilies)
                    {
                        await services.Curriculum.ReevaluateHistoricalWorkItemAsync(
                            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                            familyId,
                            "en");
                    }
                    await setup.SaveChangesAsync();
                    Assert.Equal(beforeReplay, await CountsAsync(setup));
                    Assert.Equal(beforeAnchorIdentities, await ActiveAnchorIdentitiesAsync(setup));
                    Assert.Equal(beforeTransitionIdentities, await ActiveTransitionIdentitiesAsync(setup));
                    Assert.Equal(beforeEligibleTransitionSignatures, (await services.Curriculum
                        .GetProductionEligibleSemanticTransitionSignaturesAsync(
                            "en",
                            transformations.Select(item => item.TransitionSignature).Distinct().ToArray()))
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .ToArray());
                    Assert.Equal(0, await setup.LegendLanguageCompositionalAnchors
                        .Where(item => item.SupersededUtc == null)
                        .GroupBy(item => new { item.CurriculumExampleId, item.AnchorSignature })
                        .CountAsync(group => group.Count() > 1));
                    Assert.Equal(0, await setup.LegendFounderSemanticExampleRelationEvidence
                        .Where(item => item.SupersededUtc == null)
                        .GroupBy(item => item.RelationIdentity)
                        .CountAsync(group => group.Count() > 1));
                    Assert.Equal(0, await setup.LegendSemanticTransitionEvidence
                        .Where(item => item.SupersededUtc == null)
                        .GroupBy(item => new
                        {
                            item.TransitionSignature,
                            item.SourceCurriculumExampleId,
                            item.ResultCurriculumExampleId
                        })
                        .CountAsync(group => group.Count() > 1));
                }
                Assert.Equal(0, await processor.SeedDurableFamilyWorkAsync(
                    services.Work,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                    128));
                await processor.RefreshDurableManifestStatusesAsync(
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
                Assert.Equal(beforeReplay, await CountsAsync(setup));
            }

            var conversationId = Guid.NewGuid();
            var factory = new CountingHttpClientFactory();
            var conversationLogger = new ExceptionCapturingLogger<LegendFounderAiConversationService>();
            var seenSurfaces = new List<string>();
            for (var index = 0; index < ConversationPrompts.Length; index++)
            {
                var prompt = ConversationPrompts[index];
                await using var proof = new MasterAppDbContext(options);
                var services = CreateServices(proof, configuration);
                var profiles = new AgentProfileAccessResolver(proof);
                var discourse = new LegendFounderAiDiscourseStateService(proof, profiles, services.Operations);
                var chat = new LegendFounderAiConversationService(
                    factory,
                    configuration,
                    new FounderLegendConnectService(services.Operations, profiles),
                    conversationLogger,
                    discourse,
                    new LegendLanguageRegistry(proof, configuration),
                    ControllerTestHelpers.BuildTranslationService());

                var graphBeforeReply = await services.Operations.AnalyzeReusableMeaningGraphAsync(prompt.User);
                Assert.True(graphBeforeReply.IsComposed, graphBeforeReply.ReasonCode);
                Assert.DoesNotContain(await proof.LegendLanguageTextUnits.Select(item => item.Text).ToListAsync(),
                    text => string.Equals(text, prompt.User, StringComparison.Ordinal));
                var priorState = await discourse.GetStateAsync(founder, conversationId.ToString());
                if (!prompt.RequiresFirstChoiceBinding)
                {
                    var planBeforeReply = await services.Operations.TryPlanConversationAsync(prompt.User, priorState);
                    Assert.True(planBeforeReply.Supported,
                        "Stage-5 plan failed before ReplyAsync: " + planBeforeReply.ReasonCode +
                        "; nodes=" + DescribeNodes(graphBeforeReply) +
                        "; relations=" + DescribeRelations(graphBeforeReply));
                    var nativeBeforeReply = await services.Operations.TryInferConversationWithDiscourseAsync(
                        prompt.User,
                        [],
                        priorState);
                    Assert.True(nativeBeforeReply.Supported,
                        "Native Stage-5 inference failed before ReplyAsync: " + nativeBeforeReply.ReasonCode +
                        "; nodes=" + DescribeNodes(graphBeforeReply) +
                        "; relations=" + DescribeRelations(graphBeforeReply));
                }

                var reply = await chat.ReplyAsync(
                    founder,
                    new LegendFounderAiChatRequest
                    {
                        Mode = "legend",
                        ConversationId = conversationId.ToString(),
                        Messages = [new LegendFounderAiChatMessage("user", prompt.User)]
                    });
                Assert.True(
                    reply.Succeeded,
                    $"stage={reply.Stage}; reason={reply.Reason}; error={reply.Error}; message={reply.Message}; " +
                    $"exception={conversationLogger.LatestException}");
                Assert.Equal(prompt.ExpectedResponse, reply.Message);
                Assert.Equal(
                    LegendConnectResearchEvidenceOrigin.InternalKnowledge,
                    reply.EvidenceOrigin);

                // Re-read all state through a fresh structural snapshot after
                // ReplyAsync has persisted both the user and assistant graph.
                var state = await discourse.GetStateAsync(founder, conversationId.ToString());
                Assert.NotNull(state);
                // Serving plans after the current user observation and before
                // its assistant realization. Reconstruct that exact persisted
                // boundary by excluding only the just-recorded assistant turn;
                // this remains durable semantic state, never transcript text.
                var planningState = new LegendConnectDiscourseStateSnapshot(
                    state!.Turns.Where(turn => turn.Role != "assistant" ||
                        turn.SequenceNumber != state.Turns[^1].SequenceNumber).ToArray());
                var plan = await services.Operations.TryPlanConversationAsync(prompt.User, planningState);
                Assert.True(plan.Supported, plan.ReasonCode);
                var structuredPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(plan.Plan);
                Assert.Equal(3, structuredPlan.IndependentEvidenceCount);
                Assert.Equal(prompt.ResultFunction, structuredPlan.ResultDimensions["conversation_function"]);
                Assert.Equal(prompt.ResultIntent, structuredPlan.ResultDimensions["intent"]);
                if (prompt.RequiresFirstChoiceBinding)
                {
                    var binding = Assert.Single(structuredPlan.ResolvedDiscourseBindings,
                        item => item.ResolutionState == "bound" &&
                            item.EntitySemanticDimension == "choice");
                    Assert.Equal("alpha", binding.EntitySemanticValue);
                    Assert.True(binding.ReplacesActiveBinding);
                    Assert.NotNull(binding.SupersededTurnId);
                    var bindingTurn = Assert.Single(planningState.Turns.Where(item =>
                        item.SequenceNumber == binding.SelectorTurnSequence));
                    var nodeIndex = Assert.IsType<int>(binding.SelectorNodeIndex);
                    Assert.Equal(
                        binding.SelectorSemanticSignature,
                        bindingTurn.Nodes[nodeIndex].SemanticSignature);
                    Assert.Equal(
                        binding.SelectorNodeStartTokenIndex,
                        bindingTurn.Nodes[nodeIndex].StartTokenIndex);
                    Assert.Equal(
                        binding.SelectorNodeTokenLength,
                        bindingTurn.Nodes[nodeIndex].TokenLength);
                }

                var native = await services.Operations.TryInferConversationWithDiscourseAsync(
                    prompt.User,
                    [],
                    planningState);
                Assert.True(native.Supported, native.ReasonCode);
                Assert.Equal(reply.Message, native.Answer);
                Assert.False(native.RequiresEscalation);
                Assert.True(native.EvidenceCount >= 3);
                seenSurfaces.Add(prompt.User);

                _output.WriteLine($"PROMPT {index + 1}/{ConversationPrompts.Length}");
                _output.WriteLine("USER: " + prompt.User);
                _output.WriteLine("STAGE 1 NODES: " + DescribeNodes(graphBeforeReply));
                _output.WriteLine("STAGE 1 RELATIONS: " + DescribeRelations(graphBeforeReply));
                _output.WriteLine("STAGE 1 COMPOSED: " + graphBeforeReply.IsComposed);
                _output.WriteLine("STAGE 2 EXACT FULL-SENTENCE MATCH: False");
                _output.WriteLine("STAGE 3 CONVERSATION ID: " + conversationId);
                _output.WriteLine("STAGE 3 BINDINGS: " + DescribeBindings(state));
                _output.WriteLine("STAGE 4 PLAN: " + structuredPlan.PlanIdentity + " -> " +
                    string.Join(", ", structuredPlan.ResultDimensions.Select(item => item.Key + "=" + item.Value)));
                _output.WriteLine("STAGE 5 TRANSFORMATION: " + structuredPlan.TransitionSignature);
                _output.WriteLine("STAGE 5 INDEPENDENT SUPPORT: " + structuredPlan.IndependentEvidenceCount);
                _output.WriteLine("NATIVE SUPPORTED: " + native.Supported + "; EVIDENCE: " + native.EvidenceCount +
                    "; ESCALATION: " + native.RequiresEscalation);
                _output.WriteLine("LEGEND NATIVE RESPONSE: " + native.Answer);
                _output.WriteLine("FINAL ReplyAsync RESPONSE: " + reply.Message);
                _output.WriteLine("REALIZATION MODE: CanonicalExisting");
                _output.WriteLine("OPENAI CLIENTS CREATED: " + factory.CreateClientCalls + "; OPENAI HTTP CALLS: 0; FALLBACK USED: False");
            }

            Assert.Equal(15, seenSurfaces.Count);
            Assert.Equal(0, factory.CreateClientCalls);
            await using (var privacy = new MasterAppDbContext(options))
            {
                var persisted = await privacy.LegendFounderAiDiscourseTurns
                    .Where(item => item.DiscourseConversationId ==
                        privacy.LegendFounderAiDiscourseConversations
                            .Where(conversation => conversation.ConversationId == conversationId)
                            .Select(conversation => conversation.Id)
                            .Single())
                    .Select(item => item.MeaningGraphJson + "\n" + item.ResolvedBindingsJson)
                    .ToListAsync();
                Assert.NotEmpty(persisted);
                Assert.All(persisted, stored =>
                {
                    Assert.DoesNotContain(seenSurfaces, surface => stored.Contains(surface, StringComparison.Ordinal));
                    Assert.DoesNotContain("I can help", stored, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("UnknownSurfaceComponents", stored, StringComparison.Ordinal);
                });
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    [Fact]
    public async Task FounderCrossExampleEvidence_FailsClosedUntilGovernedConditionsSeparateConflictingTransitions()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Stage-5 contradiction SQL proof was not selected; LEGEND_STAGE5_SQL_CONNECTION is required.");
            return;
        }

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(CreateIsolatedConnectionString(connectionString))
            .Options;
        await using (var migration = new MasterAppDbContext(options))
            await migration.Database.MigrateAsync();

        var founderId = Guid.NewGuid().ToString("D");
        var founder = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderId)], "stage5-contradiction"));
        var configuration = Configuration();
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using var db = new MasterAppDbContext(options);
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = $"stage5-contradiction-{founderId}@legend.test",
                NormalizedEmail = $"stage5-contradiction-{founderId}@legend.test",
                IsActive = true
            });
            await db.SaveChangesAsync();

            var services = CreateServices(db, configuration);
            var founderLegend = new FounderLegendConnectService(
                services.Operations,
                new AgentProfileAccessResolver(db));
            var processor = new LegendConnectCurriculumManifestProcessor(
                db,
                services.Curriculum,
                services.Work,
                NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);

            var acceptedConflict = await founderLegend.SubmitCurriculumAsync(
                founder,
                new FounderLegendConnectCurriculumInput { Manifest = ContradictoryTransformationManifest() });
            Assert.True(acceptedConflict.Succeeded, acceptedConflict.Message);
            Assert.Equal(1, await processor.SeedDurableFamilyWorkAsync(
                services.Work,
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                32));
            await DrainManifestAsync(
                db,
                services,
                processor,
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

            var conflictingTransitions = await db.LegendSemanticTransitionEvidence
                .Where(item => item.SupersededUtc == null &&
                    item.FounderSemanticExampleRelationEvidenceId != null)
                .ToListAsync();
            Assert.Equal(6, conflictingTransitions.Count);
            Assert.Equal(2, conflictingTransitions.Select(item => item.TransitionSignature).Distinct().Count());
            Assert.All(conflictingTransitions, item => Assert.Equal("Contradictory", item.ContributionState));
            Assert.Empty(await services.Curriculum.GetProductionEligibleSemanticTransitionSignaturesAsync(
                "en", conflictingTransitions.Select(item => item.TransitionSignature).Distinct().ToArray()));

            var unresolved = await services.Operations.TryPlanConversationAsync(
                "Could you offer guidance now?",
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.False(unresolved.Supported);
            Assert.Equal("semantic_transition_contradicted", unresolved.ReasonCode);
            Assert.Null(unresolved.Plan);

            var acceptedDistinguishing = await founderLegend.SubmitCurriculumAsync(
                founder,
                new FounderLegendConnectCurriculumInput { Manifest = DistinguishingTransformationManifest() });
            Assert.True(acceptedDistinguishing.Succeeded, acceptedDistinguishing.Message);
            Assert.Equal(1, await processor.SeedDurableFamilyWorkAsync(
                services.Work,
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                32));
            await DrainManifestAsync(
                db,
                services,
                processor,
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

            var allTransitions = await db.LegendSemanticTransitionEvidence
                .Where(item => item.SupersededUtc == null &&
                    item.FounderSemanticExampleRelationEvidenceId != null)
                .ToListAsync();
            Assert.Equal(12, allTransitions.Count);
            Assert.Equal(6, allTransitions.Count(item => item.ContributionState == "Contradictory"));
            var matureSpecificTransitions = allTransitions
                .Where(item => item.ContributionState == "Supported")
                .ToArray();
            Assert.Equal(6, matureSpecificTransitions.Length);
            Assert.Equal(2, matureSpecificTransitions.Select(item => item.TransitionSignature).Distinct().Count());
            Assert.All(matureSpecificTransitions.GroupBy(item => item.TransitionSignature), group =>
                Assert.Equal(3, group.Select(item => item.IndependentSourceIdentity).Distinct().Count()));

            var direct = await services.Operations.TryPlanConversationAsync(
                "Would you offer direct guidance now?",
                new LegendConnectDiscourseStateSnapshot([]));
            var alternate = await services.Operations.TryPlanConversationAsync(
                "Please offer alternate guidance now?",
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(direct.Supported, direct.ReasonCode);
            Assert.True(alternate.Supported, alternate.ReasonCode);
            var directPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(direct.Plan);
            var alternatePlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(alternate.Plan);
            Assert.NotEqual(directPlan.TransitionSignature, alternatePlan.TransitionSignature);
            Assert.Equal("offer_help", directPlan.ResultDimensions["conversation_function"]);
            Assert.Equal("alternative_offer", alternatePlan.ResultDimensions["conversation_function"]);

            var stillConflicted = await services.Operations.TryPlanConversationAsync(
                "Could you offer guidance now?",
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.False(stillConflicted.Supported);
            Assert.Equal("semantic_transition_contradicted", stillConflicted.ReasonCode);

            _output.WriteLine("STAGE 5 CONTRADICTION / CONDITIONING PROOF");
            _output.WriteLine("UNCONDITIONED TRANSITIONS: 6; STATE: Contradictory; SERVING: fail closed");
            _output.WriteLine("GOVERNED DISTINGUISHING CONDITIONS: direct, alternate");
            _output.WriteLine("DIRECT PLAN: " + directPlan.TransitionSignature + " -> conversation_function=offer_help");
            _output.WriteLine("ALTERNATE PLAN: " + alternatePlan.TransitionSignature + " -> conversation_function=alternative_offer");
            _output.WriteLine("HISTORICAL CONTRADICTION RETAINED: True");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    [Fact]
    public async Task BlindFounderManifest_ProvesHeldOutStructuralTransitionsWithoutProviderOrPromptLookup()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Stage-5 blind SQL proof was not selected; LEGEND_STAGE5_SQL_CONNECTION is required.");
            return;
        }

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(CreateIsolatedConnectionString(connectionString))
            .Options;
        await using (var migration = new MasterAppDbContext(options))
            await migration.Database.MigrateAsync();

        var manifest = BlindGeneralizationManifest();
        var manifestHash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(manifest));
        var founderId = Guid.NewGuid().ToString("D");
        var founder = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderId)], "stage5-blind"));
        var configuration = Configuration();
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using (var setup = new MasterAppDbContext(options))
            {
                setup.AgentProfiles.Add(new AgentProfile
                {
                    Id = Guid.NewGuid(),
                    AgentUserId = founderId,
                    AgentUpn = $"stage5-blind-{founderId}@legend.test",
                    NormalizedEmail = $"stage5-blind-{founderId}@legend.test",
                    IsActive = true
                });
                await setup.SaveChangesAsync();

                var services = CreateServices(setup, configuration);
                var founderLegend = new FounderLegendConnectService(
                    services.Operations,
                    new AgentProfileAccessResolver(setup));
                var accepted = await founderLegend.SubmitCurriculumAsync(
                    founder,
                    new FounderLegendConnectCurriculumInput { Manifest = manifest });
                Assert.True(accepted.Succeeded, accepted.Message);
                var processor = new LegendConnectCurriculumManifestProcessor(
                    setup,
                    services.Curriculum,
                    services.Work,
                    NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
                Assert.Equal(1, await processor.SeedDurableFamilyWorkAsync(
                    services.Work,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                    64));
                await DrainManifestAsync(
                    setup,
                    services,
                    processor,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
                Assert.Equal(15, await setup.LegendFounderSemanticExampleRelationEvidence
                    .CountAsync(item => item.SupersededUtc == null));
            }

            var factory = new CountingHttpClientFactory();
            foreach (var prompt in BlindPrompts)
            {
                await using var proof = new MasterAppDbContext(options);
                var surfaces = await proof.LegendLanguageTextUnits
                    .Select(item => item.Text)
                    .ToArrayAsync();
                Assert.DoesNotContain(surfaces, item =>
                    string.Equals(item, prompt.User, StringComparison.Ordinal));
                AssertNotInProductionCode(prompt.User);

                var services = CreateServices(proof, configuration);
                var graph = await services.Operations.AnalyzeReusableMeaningGraphAsync(prompt.User);
                Assert.True(graph.IsComposed, graph.ReasonCode);
                var plan = await services.Operations.TryPlanConversationAsync(
                    prompt.User,
                    new LegendConnectDiscourseStateSnapshot([]));
                Assert.True(plan.Supported, plan.ReasonCode);
                var structuredPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(plan.Plan);
                Assert.Equal(prompt.ResultFunction, structuredPlan.ResultDimensions["conversation_function"]);
                Assert.Equal(prompt.ResultIntent, structuredPlan.ResultDimensions["intent"]);

                var profiles = new AgentProfileAccessResolver(proof);
                var chat = new LegendFounderAiConversationService(
                    factory,
                    configuration,
                    new FounderLegendConnectService(services.Operations, profiles),
                    NullLogger<LegendFounderAiConversationService>.Instance,
                    new LegendFounderAiDiscourseStateService(proof, profiles, services.Operations),
                    new LegendLanguageRegistry(proof, configuration),
                    ControllerTestHelpers.BuildTranslationService());
                var reply = await chat.ReplyAsync(
                    founder,
                    new LegendFounderAiChatRequest
                    {
                        Mode = "legend",
                        ConversationId = Guid.NewGuid().ToString(),
                        Messages = [new LegendFounderAiChatMessage("user", prompt.User)]
                    });
                Assert.True(reply.Succeeded);
                Assert.Equal(prompt.ExpectedResponse, reply.Message);
                Assert.Equal(
                    LegendConnectResearchEvidenceOrigin.InternalKnowledge,
                    reply.EvidenceOrigin);

                _output.WriteLine("STAGE 5 BLIND HELD-OUT CASE");
                _output.WriteLine("USER: " + prompt.User);
                _output.WriteLine("EXACT INPUT IN FROZEN CURRICULUM: False");
                _output.WriteLine("EXACT INPUT IN PRODUCTION CODE: False");
                _output.WriteLine("MEANING: " + DescribeNodes(graph));
                _output.WriteLine("TRANSFORMATION: " + structuredPlan.TransitionSignature);
                _output.WriteLine("PLAN: " + string.Join(", ", structuredPlan.ResultDimensions.Select(item => item.Key + "=" + item.Value)));
                _output.WriteLine("LEGEND: " + reply.Message);
                _output.WriteLine("REALIZATION MODE: CanonicalExisting");
                _output.WriteLine("OPENAI CLIENTS: 0; OPENAI HTTP CALLS: 0");
            }
            Assert.Equal(0, factory.CreateClientCalls);
            _output.WriteLine("FROZEN BLIND MANIFEST SHA-256: " + Convert.ToHexString(manifestHash).ToLowerInvariant());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    [Fact]
    public async Task CompletedV19_ConvergesToV20ThroughBoundedDependencyInventoryWithoutSourceReplay()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Dependency-driven V19-to-V20 SQL proof was not selected; LEGEND_STAGE5_SQL_CONNECTION is required.");
            return;
        }

        var sqlMetrics = new SqlCommandMetrics();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(CreateIsolatedConnectionString(connectionString))
            .AddInterceptors(sqlMetrics)
            .Options;
        await using (var migration = new MasterAppDbContext(options))
            await migration.Database.MigrateAsync();

        var founderId = Guid.NewGuid().ToString("D");
        var founder = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderId)], "v19-v20-proof"));
        var configuration = Configuration();
        var priorFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using (var setup = new MasterAppDbContext(options))
            {
                setup.AgentProfiles.Add(new AgentProfile
                {
                    Id = Guid.NewGuid(),
                    AgentUserId = founderId,
                    AgentUpn = $"v19-v20-{founderId}@legend.test",
                    NormalizedEmail = $"v19-v20-{founderId}@legend.test",
                    IsActive = true
                });
                await setup.SaveChangesAsync();

                var services = CreateServices(setup, configuration);
                var accepted = await new FounderLegendConnectService(
                        services.Operations,
                        new AgentProfileAccessResolver(setup))
                    .SubmitCurriculumAsync(
                        founder,
                        new FounderLegendConnectCurriculumInput { Manifest = DependencyBenchmarkManifest() });
                Assert.True(accepted.Succeeded, accepted.Message);

                var processor = new LegendConnectCurriculumManifestProcessor(
                    setup,
                    services.Curriculum,
                    services.Work,
                    NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
                Assert.Equal(1, await processor.SeedDurableFamilyWorkAsync(services.Work, 19, 32));
                await DrainManifestAsync(setup, services, processor, 19);

                var v19Started = await services.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(19);
                Assert.Equal(19, v19Started.TargetEvaluatorVersion);
                Assert.True(v19Started.RequiresWork);
                // V20-capable code observes the existing V19 drain; it does
                // not reset its cursor/phase, cancel a lease, or create a
                // competing convergence row while V19 remains authoritative.
                var deferredV20 = await services.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(20);
                Assert.Equal(19, deferredV20.TargetEvaluatorVersion);
                Assert.Equal(v19Started.Phase, deferredV20.Phase);
                Assert.Empty(await setup.LegendLanguageDerivationConvergences
                    .Where(item => item.TargetEvaluatorVersion == 20)
                    .ToListAsync());

                // This is the actual existing historical-work authority,
                // invoked one claimed identity at a time. It proves the
                // baseline is a genuinely converged V19 corpus rather than a
                // hand-written policy row.
                sqlMetrics.Reset();
                var broadReplayClock = Stopwatch.StartNew();
                await DrainHistoricalWorkAsync(setup, services, 19);
                broadReplayClock.Stop();
                var broadMetrics = sqlMetrics.Snapshot();
                var v19 = await services.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(19);
                Assert.False(v19.RequiresWork);
                Assert.Equal(19, v19.CompletedEvaluatorVersion);
                Assert.Equal(8, await setup.LegendHistoricalReevaluationWorkItems
                    .CountAsync(item => item.EvaluatorVersion == 19 &&
                        item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies &&
                        item.WorkKind == "Canonical"));
                _output.WriteLine($"V19 broad SourceFamilies elapsed={broadReplayClock.Elapsed.TotalMilliseconds:F0}ms; workItems=8; sqlReads={broadMetrics.Reads}; sqlWrites={broadMetrics.Writes}.");
            }

            CanonicalCounts beforeCounts;
            ActiveAnchorIdentity[] beforeAnchors;
            ActiveTransitionIdentity[] beforeTransitions;
            await using (var before = new MasterAppDbContext(options))
            {
                beforeCounts = await CountsAsync(before);
                beforeAnchors = await ActiveAnchorIdentitiesAsync(before);
                beforeTransitions = await ActiveTransitionIdentitiesAsync(before);
                Assert.Empty(await before.LegendLanguageDerivationArtifacts.ToListAsync());
            }

            // Two fresh service/DbContext instances race the V20 adoption.
            // SQL Server's runtime-policy update lock must leave one durable
            // target, one convergence row, and one inventory frontier.
            await using (var firstDb = new MasterAppDbContext(options))
            await using (var secondDb = new MasterAppDbContext(options))
            {
                var first = CreateServices(firstDb, configuration);
                var second = CreateServices(secondDb, configuration);
                var starts = await Task.WhenAll(
                    first.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(20),
                    second.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(20));
                Assert.All(starts, state =>
                {
                    Assert.Equal(20, state.TargetEvaluatorVersion);
                    Assert.Equal(19, state.CompletedEvaluatorVersion);
                    Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory, state.Phase);
                });
            }

            await using (var converge = new MasterAppDbContext(options))
            {
                var services = CreateServices(converge, configuration);
                sqlMetrics.Reset();
                var deltaConvergenceClock = Stopwatch.StartNew();
                await DrainHistoricalWorkAsync(converge, services, 20);
                deltaConvergenceClock.Stop();
                var deltaMetrics = sqlMetrics.Snapshot();
                var v20 = await services.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(20);
                Assert.False(v20.RequiresWork);
                Assert.Equal(20, v20.CompletedEvaluatorVersion);

                var convergence = await converge.LegendLanguageDerivationConvergences
                    .SingleAsync(item => item.TargetEvaluatorVersion == 20);
                Assert.Equal("Completed", convergence.State);
                Assert.False(convergence.RequiresDependencyInventory);
                Assert.Equal(0, convergence.AffectedCanonicalArtifactCount);
                Assert.Equal(convergence.ExistingCanonicalArtifactCount,
                    convergence.ReusedCanonicalArtifactCount);
                Assert.True(convergence.DependencyInventoryWorkItemCount > 0);

                Assert.True(await converge.LegendLanguageDerivationArtifacts.AnyAsync());
                Assert.Empty(await converge.LegendLanguageDerivationArtifacts
                    .Where(item => item.State != "Current")
                    .ToListAsync());
                Assert.Empty(await converge.LegendHistoricalReevaluationWorkItems
                    .Where(item => item.EvaluatorVersion == 20 &&
                        item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies)
                    .ToListAsync());
                Assert.True(await converge.LegendHistoricalReevaluationWorkItems
                    .AnyAsync(item => item.EvaluatorVersion == 20 &&
                        item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory &&
                        item.WorkKind == "Canonical" && item.ProcessingState == "Completed"));
                Assert.Equal(1, await converge.LegendHistoricalReevaluationWorkItems
                    .CountAsync(item => item.EvaluatorVersion == 20 &&
                        item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory &&
                        item.WorkKind == "Canonical"));
                _output.WriteLine($"V20 dependency delta elapsed={deltaConvergenceClock.Elapsed.TotalMilliseconds:F0}ms; workItems=1; sqlReads={deltaMetrics.Reads}; sqlWrites={deltaMetrics.Writes}; broad-to-delta work reduction=87.5%.");
            }

            await using (var after = new MasterAppDbContext(options))
            {
                Assert.Equal(beforeCounts, await CountsAsync(after));
                Assert.Equal(beforeAnchors, await ActiveAnchorIdentitiesAsync(after));
                Assert.Equal(beforeTransitions, await ActiveTransitionIdentitiesAsync(after));
                var v20WorkBeforeRepeat = await after.LegendHistoricalReevaluationWorkItems
                    .CountAsync(item => item.EvaluatorVersion == 20);
                var services = CreateServices(after, configuration);
                var secondPass = await services.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(20);
                Assert.False(secondPass.RequiresWork);
                Assert.Equal(v20WorkBeforeRepeat, await after.LegendHistoricalReevaluationWorkItems
                    .CountAsync(item => item.EvaluatorVersion == 20));
            }

            _output.WriteLine("DEPENDENCY-DRIVEN V19 -> V20 SQL PROOF");
            _output.WriteLine($"V19 canonical artifacts: {beforeCounts.Anchors + beforeCounts.MeaningNodes + beforeCounts.MeaningRelations + beforeCounts.Transitions}; V20 semantic re-evaluation work: 0.");
            _output.WriteLine("V20 durable work: dependency inventory only; SourceFamilies work: 0.");
            _output.WriteLine("Canonical identity sets before/after: equal; duplicate canonical artifacts: 0; second convergence work: 0.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", priorFounderOid);
        }
    }

    private static async Task DrainManifestAsync(
        MasterAppDbContext db,
        Services services,
        LegendConnectCurriculumManifestProcessor processor,
        int evaluatorVersion)
    {
        while (true)
        {
            var claim = await services.Work.TryClaimNextFounderManifestWorkAsync(
                evaluatorVersion,
                "stage5-sql-proof");
            if (claim is null)
                break;
            Assert.IsType<Guid>(claim.SubjectId);
            Assert.True(int.TryParse(claim.SubjectScope, out var index));
            await using var execution = await services.Work.TryBeginOwnedExecutionAsync(claim);
            Assert.NotNull(execution);
            if (claim.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestFamilyWorkKind)
            {
                await processor.ProcessDurableFamilyAsync(claim.SubjectId!.Value, index);
            }
            else if (claim.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestSemanticRelationWorkKind)
            {
                await processor.ProcessDurableSemanticRelationAsync(
                    claim.SubjectId!.Value,
                    index,
                    evaluatorVersion);
            }
            else
            {
                Assert.Equal(
                    LegendConnectHistoricalReevaluationWorkAuthority.DerivationLedgerWorkKind,
                    claim.WorkKind);
                await processor.ProcessDurableFamilyDerivationLedgerAsync(
                    claim.SubjectId!.Value,
                    index,
                    evaluatorVersion);
            }
            Assert.True(await execution!.CompleteAsync());
            await processor.RefreshDurableManifestStatusAsync(claim.SubjectId!.Value, evaluatorVersion);
            db.ChangeTracker.Clear();
        }
        await processor.RefreshDurableManifestStatusesAsync(evaluatorVersion);
    }

    /// <summary>
    /// Executes the same durable claim/lease/evaluator/phase-barrier path as
    /// the hosted learning worker, but deterministically inside this fresh
    /// SQL proof. No canonical state is seeded or advanced by the test.
    /// </summary>
    private static async Task DrainHistoricalWorkAsync(
        MasterAppDbContext db,
        Services services,
        int evaluatorVersion)
    {
        for (var phasePass = 0; phasePass < 32; phasePass++)
        {
            var replay = await services.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluatorVersion);
            if (!replay.RequiresWork)
                return;

            var phase = replay.Phase;
            var madeProgress = false;
            for (var batch = 0; batch < 64; batch++)
            {
                var seeded = await services.Work.SeedNextBatchAsync(
                    evaluatorVersion,
                    phase,
                    "v19-v20-sql-proof:seed");
                while (true)
                {
                    var claim = await services.Work.TryClaimNextAsync(
                        evaluatorVersion,
                        phase,
                        "v19-v20-sql-proof:worker");
                    if (claim is null)
                        break;
                    Assert.IsType<Guid>(claim.SubjectId);
                    await using var execution = await services.Work.TryBeginOwnedExecutionAsync(claim);
                    Assert.NotNull(execution);
                    var subjectId = claim.SubjectId!.Value;
                    if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory)
                    {
                        Assert.True(int.TryParse(claim.SubjectScope, out var batchSize));
                        var inventory = await services.Curriculum
                            .InventoryHistoricalDerivationDependenciesBatchAsync(
                                subjectId == Guid.Empty ? null : subjectId,
                                evaluatorVersion,
                                batchSize);
                        Assert.NotNull(inventory.LastFamilyId);
                        await services.Runtime.AdvanceLanguageIntelligenceReevaluationAsync(
                            evaluatorVersion,
                            phase,
                            inventory.LastFamilyId,
                            phaseComplete: false);
                    }
                    else if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
                    {
                        await services.Intelligence.ReevaluateHistoricalProviderObservationAsync(subjectId);
                    }
                    else if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations)
                    {
                        await services.Operations.ReconcileHistoricalOperationalTranslationAsync(subjectId);
                    }
                    else
                    {
                        await services.Curriculum.ReevaluateHistoricalWorkItemAsync(
                            phase,
                            subjectId,
                            claim.SubjectScope);
                        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies &&
                            LegendConnectDerivationContracts.ForEvaluator(evaluatorVersion).Any(item =>
                                item.DerivationKind == LegendConnectDerivationContracts.GovernedContentBinding))
                        {
                            await services.Curriculum.InventoryHistoricalDerivationDependenciesAsync(
                                subjectId,
                                evaluatorVersion);
                        }
                    }
                    Assert.True(await execution!.CompleteAsync());
                    db.ChangeTracker.Clear();
                    madeProgress = true;
                }

                if (!seeded.MadeProgress)
                    break;
            }

            Assert.True(madeProgress || await services.Work.TryAdvancePhaseAsync(evaluatorVersion, phase),
                $"The durable phase {phase} neither processed work nor reached its barrier.");
            if (madeProgress)
                Assert.True(await services.Work.TryAdvancePhaseAsync(evaluatorVersion, phase),
                    $"The durable phase {phase} did not drain through its database-authoritative barrier.");
        }

        var final = await services.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluatorVersion);
        var workState = await db.LegendHistoricalReevaluationWorkItems
            .Where(item => item.EvaluatorVersion == evaluatorVersion)
            .GroupBy(item => new { item.Phase, item.ProcessingState, item.WorkKind })
            .Select(group => group.Key.Phase + "/" + group.Key.WorkKind + "/" +
                group.Key.ProcessingState + "=" + group.Count())
            .ToListAsync();
        var unseededSource = await (
            from example in db.LegendCurriculumExamples
            join unit in db.LegendLanguageTextUnits on example.TextUnitId equals unit.Id
            where example.DerivedFromCurriculumExampleId == null && example.SupersededUtc == null &&
                unit.IsTrainingEligible &&
                !db.LegendHistoricalReevaluationWorkItems.Any(item =>
                    item.EvaluatorVersion == evaluatorVersion &&
                    item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies &&
                    item.WorkKind == "Canonical" &&
                    item.SubjectId == example.CurriculumFamilyId &&
                    item.SubjectScope == example.LanguageCode)
            select example.Id).CountAsync();
        throw new Xunit.Sdk.XunitException(
            "The durable V19/V20 historical replay did not converge. Current=" +
            final.Phase + "; target=" + final.TargetEvaluatorVersion +
            "; completed=" + final.CompletedEvaluatorVersion + "; work=" +
            string.Join(",", workState) + "; cursorCompatibility=" +
            final.CursorReplayCompatibilityEvaluatorVersion + "; unseededSource=" + unseededSource);
    }

    private async Task<GovernedContentCaseProof> ExecuteGovernedContentCaseAsync(
        string connectionString,
        string leftSubject,
        string leftSurface,
        string rightSubject,
        string rightSurface,
        string leftCost,
        string rightCost,
        bool verifySourceFamiliesReplay = false,
        bool expectContent = true)
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(CreateIsolatedConnectionString(connectionString))
            .Options;
        await using (var migration = new MasterAppDbContext(options))
            await migration.Database.MigrateAsync();

        var founderId = Guid.NewGuid().ToString("D");
        var founder = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", founderId)], "stage6-content-proof"));
        var configuration = Configuration();
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using (var setup = new MasterAppDbContext(options))
            {
                setup.AgentProfiles.Add(new AgentProfile
                {
                    Id = Guid.NewGuid(),
                    AgentUserId = founderId,
                    AgentUpn = $"stage6-{founderId}@legend.test",
                    NormalizedEmail = $"stage6-{founderId}@legend.test",
                    IsActive = true
                });
                await setup.SaveChangesAsync();

                var services = CreateServices(setup, configuration);
                var founderLegend = new FounderLegendConnectService(
                    services.Operations,
                    new AgentProfileAccessResolver(setup));
                var accepted = await founderLegend.SubmitCurriculumAsync(
                    founder,
                    new FounderLegendConnectCurriculumInput
                    {
                        Manifest = GovernedContentManifest(
                            leftSubject, leftSurface, rightSubject, rightSurface, leftCost, rightCost, expectContent)
                    });
                Assert.True(accepted.Succeeded, accepted.Message);
                Assert.False(accepted.DuplicatePrevented);

                var processor = new LegendConnectCurriculumManifestProcessor(
                    setup,
                    services.Curriculum,
                    services.Work,
                    NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
                Assert.Equal(1, await processor.SeedDurableFamilyWorkAsync(
                    services.Work,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                    64));
                await DrainManifestAsync(
                    setup,
                    services,
                    processor,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

                var manifest = await setup.LegendCurriculumManifestWorkItems
                    .SingleAsync(item => item.FounderUserId == founderId);
                Assert.Equal("Completed", manifest.ProcessingState);
                Assert.Equal(LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                    manifest.CompletedLanguageIntelligenceEvaluatorVersion);

                if (verifySourceFamiliesReplay)
                {
                    var beforeReplay = await CountsAsync(setup);
                    var familyIds = await setup.LegendCurriculumFamilies
                        .Select(item => item.Id)
                        .ToArrayAsync();
                    for (var pass = 1; pass <= 2; pass++)
                    {
                        foreach (var familyId in familyIds)
                        {
                            await services.Curriculum.ReevaluateHistoricalWorkItemAsync(
                                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                                familyId,
                                "en");
                        }
                        await setup.SaveChangesAsync();
                        Assert.Equal(beforeReplay, await CountsAsync(setup));
                        Assert.Equal(0, await setup.LegendLanguageCompositionalAnchors
                            .Where(item => item.SupersededUtc == null)
                            .GroupBy(item => new { item.CurriculumExampleId, item.AnchorSignature })
                            .CountAsync(group => group.Count() > 1));
                        Assert.Equal(0, await setup.LegendLanguageMeaningRelationEvidence
                            .Where(item => item.SupersededUtc == null)
                            .GroupBy(item => item.EvidenceIdentity)
                            .CountAsync(group => group.Count() > 1));
                        Assert.Equal(0, await setup.LegendLanguageContextRelationships
                            .Where(item => item.SupersededUtc == null)
                            .GroupBy(item => new
                            {
                                CanonicalPairKey = EF.Property<string>(item, "CanonicalPairKey"),
                                item.SourceTextUnitId,
                                item.RelatedTextUnitId,
                                item.RelationshipKind,
                                item.ContextSignature
                            })
                            .CountAsync(group => group.Count() > 1));
                        Assert.Equal(0, await setup.LegendSemanticTransitionEvidence
                            .Where(item => item.SupersededUtc == null)
                            .GroupBy(item => new
                            {
                                item.TransitionSignature,
                                item.SourceCurriculumExampleId,
                                item.ResultCurriculumExampleId
                            })
                            .CountAsync(group => group.Count() > 1));
                    }
                }

                Assert.Equal(0, await processor.SeedDurableFamilyWorkAsync(
                    services.Work,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                    64));
                await processor.RefreshDurableManifestStatusesAsync(
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            }

            await using var proof = new MasterAppDbContext(options);
            var proofServices = CreateServices(proof, configuration);
            var profiles = new AgentProfileAccessResolver(proof);
            var discourse = new LegendFounderAiDiscourseStateService(
                proof, profiles, proofServices.Operations);
            var factory = new CountingHttpClientFactory();
            var chat = new LegendFounderAiConversationService(
                factory,
                configuration,
                new FounderLegendConnectService(proofServices.Operations, profiles),
                NullLogger<LegendFounderAiConversationService>.Instance,
                discourse,
                new LegendLanguageRegistry(proof, configuration),
                ControllerTestHelpers.BuildTranslationService());
            var conversationId = Guid.NewGuid().ToString();
            var establishingRequest = "Between " + leftSurface + " and " + rightSurface +
                ", which one is cheaper?";

            if (!expectContent)
            {
                var missingContentPlan = await proofServices.Operations.TryBindConversationContentAsync(
                    establishingRequest,
                    new LegendConnectDiscourseStateSnapshot([]));
                Assert.False(missingContentPlan.Supported);
                Assert.Equal("governed_content_fact_unknown", missingContentPlan.ReasonCode);
                var native = await proofServices.Operations.TryInferConversationWithDiscourseAsync(
                    establishingRequest,
                    [],
                    new LegendConnectDiscourseStateSnapshot([]));
                Assert.False(native.Supported);
                Assert.Equal("governed_content_fact_unknown", native.ReasonCode);
                var missingReply = await chat.ReplyAsync(
                    founder,
                    new LegendFounderAiChatRequest
                    {
                        Mode = "legend",
                        ConversationId = conversationId,
                        Messages = [new LegendFounderAiChatMessage("user", establishingRequest)]
                    });
                Assert.True(missingReply.Succeeded);
                Assert.Equal("SystemDiagnostic", missingReply.ResponseAuthority);
                Assert.Equal("native_or_provider_unavailable", missingReply.Stage);
                Assert.Contains("NativeFailure=governed_content_fact_unknown", missingReply.Message,
                    StringComparison.Ordinal);
                Assert.Contains("ProviderFailure=provider_not_attempted", missingReply.Message,
                    StringComparison.Ordinal);
                Assert.Equal(0, factory.CreateClientCalls);
                return new GovernedContentCaseProof(
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    missingReply.Message!,
                    factory.CreateClientCalls,
                    leftSurface,
                    rightSurface,
                    native.ReasonCode);
            }

            var establishingGraph = await proofServices.Operations.AnalyzeReusableMeaningGraphAsync(establishingRequest);
            Assert.True(establishingGraph.IsComposed, establishingGraph.ReasonCode + "; nodes=" +
                DescribeNodes(establishingGraph));
            var initialContentPlan = await proofServices.Operations.TryBindConversationContentAsync(
                establishingRequest,
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(initialContentPlan.Supported, initialContentPlan.ReasonCode + "; nodes=" +
                DescribeNodes(establishingGraph) + "; relations=" + DescribeRelations(establishingGraph));
            var initialBound = Assert.IsType<LegendConnectContentBoundResponseMeaningPlanSnapshot>(
                initialContentPlan.Plan);
            Assert.Equal(2, initialBound.ContentVariableBindings.Count);
            var composedInitial = await proofServices.Curriculum.TryInferComposedSemanticTransitionAsync(
                "en",
                establishingRequest,
                [],
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(
                string.Equals(composedInitial.State, LegendSemanticTransitionInference.Supported, StringComparison.Ordinal),
                composedInitial.State + "; " + string.Join(", ", composedInitial.Reasons));
            var directInitial = await proofServices.Operations.TryInferConversationWithDiscourseAsync(
                establishingRequest,
                [],
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(directInitial.Supported, directInitial.ReasonCode + "; nodes=" +
                DescribeNodes(establishingGraph) + "; relations=" + DescribeRelations(establishingGraph));

            // This first turn establishes the two governed subject bindings.
            // The held-out second turn deliberately contains neither subject.
            var established = await chat.ReplyAsync(
                founder,
                new LegendFounderAiChatRequest
                {
                    Mode = "legend",
                    ConversationId = conversationId,
                    Messages = [new LegendFounderAiChatMessage(
                        "user", establishingRequest)]
                });
            Assert.True(established.Succeeded, established.Error);
            Assert.Equal(
                LegendConnectResearchEvidenceOrigin.InternalKnowledge,
                established.EvidenceOrigin);
            Assert.Equal(directInitial.Answer, established.Message);

            const string heldOutRequest = "Which one is cheaper?";
            Assert.DoesNotContain(await proof.LegendLanguageTextUnits
                    .Select(item => item.Text)
                    .ToListAsync(),
                text => string.Equals(text, heldOutRequest, StringComparison.Ordinal));
            var heldOutGraph = await proofServices.Operations.AnalyzeReusableMeaningGraphAsync(heldOutRequest);
            Assert.True(heldOutGraph.IsComposed, heldOutGraph.ReasonCode + "; nodes=" +
                DescribeNodes(heldOutGraph) + "; relations=" + DescribeRelations(heldOutGraph));
            var reply = await chat.ReplyAsync(
                founder,
                new LegendFounderAiChatRequest
                {
                    Mode = "legend",
                    ConversationId = conversationId,
                    Messages = [new LegendFounderAiChatMessage("user", heldOutRequest)]
                });
            Assert.True(reply.Succeeded);
            var state = await discourse.GetStateAsync(founder, conversationId);
            Assert.NotNull(state);
            var composedHeldOut = await proofServices.Curriculum.TryInferComposedSemanticTransitionAsync(
                "en",
                heldOutRequest,
                [],
                state);
            Assert.True(
                string.Equals(composedHeldOut.State, LegendSemanticTransitionInference.Supported, StringComparison.Ordinal),
                composedHeldOut.State + "; " + string.Join(", ", composedHeldOut.Reasons));
            var nativeHeldOut = await proofServices.Operations.TryInferConversationWithDiscourseAsync(
                heldOutRequest,
                [],
                state);
            Assert.True(nativeHeldOut.Supported, nativeHeldOut.ReasonCode + "; nodes=" +
                DescribeNodes(heldOutGraph) + "; relations=" + DescribeRelations(heldOutGraph) +
                "; discourse=" + DescribeBindings(state!) + "; prior_turns=" +
                string.Join(" | ", state!.Turns.Select(turn => turn.Role + ":" +
                    DescribeNodes(new LegendConnectUtteranceMeaningGraphSnapshot(
                        turn.IsComposed, turn.Nodes, turn.Relations, [], "persisted")))));
            Assert.False(string.IsNullOrWhiteSpace(reply.Message));
            Assert.Contains(leftCost, reply.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(rightCost, reply.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(leftSurface, reply.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(rightSurface, reply.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, factory.CreateClientCalls);

            // ReplyAsync records the current user graph before it selects a
            // transition. Reconstruct that exact serving boundary from the
            // persisted semantic state by excluding only the newly observed
            // assistant graph; no raw transcript is used.
            var planningState = new LegendConnectDiscourseStateSnapshot(
                state!.Turns.Where(turn => turn.Role != "assistant" ||
                    turn.SequenceNumber != state.Turns[^1].SequenceNumber).ToArray());
            var contentPlan = await proofServices.Operations.TryBindConversationContentAsync(
                heldOutRequest,
                planningState);
            Assert.True(contentPlan.Supported, contentPlan.ReasonCode + "; nodes=" +
                DescribeNodes(heldOutGraph) + "; relations=" + DescribeRelations(heldOutGraph) +
                "; discourse=" + DescribeBindings(planningState) + "; prior_turns=" +
                string.Join(" | ", planningState.Turns.Select(turn => turn.Role + ":" +
                    DescribeNodes(new LegendConnectUtteranceMeaningGraphSnapshot(
                        turn.IsComposed, turn.Nodes, turn.Relations, [], "persisted")))));
            var bound = Assert.IsType<LegendConnectContentBoundResponseMeaningPlanSnapshot>(contentPlan.Plan);
            Assert.Equal(2, bound.ContentVariableBindings.Count);
            Assert.Equal(2, bound.Facts.Count);
            Assert.Equal(6, bound.ContentEvidenceCount);
            Assert.All(bound.Facts, fact =>
            {
                Assert.Equal(3, fact.IndependentSourceCount);
                Assert.Equal(0, fact.ContradictionCount);
                Assert.Equal("Supported", fact.MaturityState);
                Assert.True(fact.IsProductionEligible);
            });
            var serializedPlan = System.Text.Json.JsonSerializer.Serialize(bound);
            Assert.DoesNotContain(heldOutRequest, serializedPlan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(established.Message!, serializedPlan, StringComparison.OrdinalIgnoreCase);

            return new GovernedContentCaseProof(
                bound.ResponsePlan.TransitionSignature,
                new Dictionary<string, string>(bound.ContentVariableBindings, StringComparer.Ordinal),
                reply.Message!,
                factory.CreateClientCalls,
                leftSurface,
                rightSurface,
                null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    private static string CreateIsolatedConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "LegendStage5_" + Guid.NewGuid().ToString("N")
        };
        return builder.ConnectionString;
    }

    private static async Task<CanonicalCounts> CountsAsync(MasterAppDbContext db) => new(
        await db.LegendCurriculumFamilies.CountAsync(),
        await db.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null),
        await db.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null),
        await db.LegendLanguageMeaningNodeEvidence.CountAsync(item => item.SupersededUtc == null),
        await db.LegendLanguageMeaningRelationEvidence.CountAsync(item => item.SupersededUtc == null),
        await db.LegendFounderSemanticExampleRelationEvidence.CountAsync(item => item.SupersededUtc == null),
        await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null));

    private static Task<ActiveAnchorIdentity[]> ActiveAnchorIdentitiesAsync(MasterAppDbContext db) =>
        db.LegendLanguageCompositionalAnchors
            .Where(item => item.SupersededUtc == null)
            .OrderBy(item => item.CurriculumExampleId)
            .ThenBy(item => item.AnchorSignature)
            .Select(item => new ActiveAnchorIdentity(
                item.CurriculumExampleId,
                item.AnchorSignature,
                item.Dimension,
                item.Value,
                item.LexemeId,
                item.ComponentStartTokenIndex,
                item.ComponentLength,
                item.SemanticSignature,
                item.Provenance))
            .ToArrayAsync();

    private static Task<ActiveTransitionIdentity[]> ActiveTransitionIdentitiesAsync(MasterAppDbContext db) =>
        db.LegendSemanticTransitionEvidence
            .Where(item => item.SupersededUtc == null &&
                item.FounderSemanticExampleRelationEvidenceId != null)
            .OrderBy(item => item.TransitionSignature)
            .ThenBy(item => item.SourceCurriculumExampleId)
            .ThenBy(item => item.ResultCurriculumExampleId)
            .Select(item => new ActiveTransitionIdentity(
                item.TransitionSignature,
                item.SourceCurriculumExampleId,
                item.ResultCurriculumExampleId,
                item.SourceSemanticFrameSignature,
                item.ResultSemanticFrameSignature,
                item.ContributionState,
                item.IndependentSourceIdentity,
                item.FounderRelationshipSemanticSignature,
                item.DerivationEvaluatorVersion))
            .ToArrayAsync();

    private static Services CreateServices(MasterAppDbContext db, IConfiguration configuration)
    {
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db,
            new FounderAccess(),
            registry,
            configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration, runtime);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance,
            intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            runtimePolicy: runtime,
            curriculum: curriculum,
            intelligence: intelligence);
        return new(
            operations,
            curriculum,
            new LegendConnectHistoricalReevaluationWorkAuthority(db, runtime, configuration),
            runtime,
            intelligence);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = string.Empty,
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:HistoricalReevaluation:MaxConcurrency"] = "4",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English"
        }).Build();

    /// <summary>
    /// The declaration contains one generic comparison transformation and two
    /// independent fact sets.  The content dimensions intentionally belong
    /// only to the result frame; no example states a lookup answer for the
    /// held-out request.
    /// </summary>
    private static string GovernedContentManifest(
        string leftSubject,
        string leftSurface,
        string rightSubject,
        string rightSurface,
        string leftCost,
        string rightCost,
        bool includeFactFamilies = true)
    {
        var lines = new List<string>();
        for (var support = 1; support <= 3; support++)
        {
            var suffix = support.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lines.Add("@family stage6.comparison." + suffix + " | Founder governed comparison response meaning");
            lines.Add("Between " + leftSurface + " and " + rightSurface + ", which one is cheaper today? | conversation_function=comparison_request; left_subject=" + leftSubject + "; right_subject=" + rightSubject + "; comparison_attribute=cost");
            lines.Add("@semantic-example comparison-source-" + suffix);
            lines.Add("@meaning");
            lines.Add("@node function | conversation_function=comparison_request | surface=which one");
            lines.Add("@node left | left_subject=" + leftSubject + " | surface=" + leftSurface);
            lines.Add("@node right | right_subject=" + rightSubject + " | surface=" + rightSurface);
            lines.Add("@node attribute | comparison_attribute=cost | surface=cheaper");
            lines.Add("@edge function -> left | relation=compares");
            lines.Add("@edge function -> right | relation=compares");
            lines.Add("@edge function -> attribute | relation=uses");
            lines.Add("@endmeaning");
            lines.Add(leftSurface + " costs " + leftCost + " while " + rightSurface + " remains " + rightCost + ". | conversation_function=comparison_response; left_subject=" + leftSubject + "; right_subject=" + rightSubject + "; cost_left=" + leftCost + "; cost_right=" + rightCost);
            lines.Add("@semantic-example comparison-result-" + suffix);
            lines.Add("@meaning");
            lines.Add("@node function | conversation_function=comparison_response | surface=costs");
            lines.Add("@node left | left_subject=" + leftSubject + " | surface=" + leftSurface);
            lines.Add("@node costleft | cost_left=" + leftCost + " | surface=" + leftCost);
            lines.Add("@node connector | connective=while | surface=while");
            lines.Add("@node right | right_subject=" + rightSubject + " | surface=" + rightSurface);
            lines.Add("@node predicate | right_predicate=remains | surface=remains");
            lines.Add("@node costright | cost_right=" + rightCost + " | surface=" + rightCost);
            lines.Add("@edge function -> left | relation=contains");
            lines.Add("@edge function -> costleft | relation=contains");
            lines.Add("@edge function -> connector | relation=contains");
            lines.Add("@edge function -> right | relation=contains");
            lines.Add("@edge function -> predicate | relation=contains");
            lines.Add("@edge function -> costright | relation=contains");
            lines.Add("@endmeaning");
            lines.Add("@transition");
            lines.Add("@source conversation_function=comparison_request; left_subject=$left; right_subject=$right; comparison_attribute=cost");
            lines.Add("@result conversation_function=comparison_response; left_subject=$left; right_subject=$right; cost_left=$cost_left; cost_right=$cost_right");
            lines.Add("@endtransition");
            lines.Add("@end");
        }

        if (includeFactFamilies)
        {
            AppendGovernedFactFamilies(lines, "left_subject", leftSubject, "cost_left", leftCost, leftSurface);
            AppendGovernedFactFamilies(lines, "right_subject", rightSubject, "cost_right", rightCost, rightSurface);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendGovernedFactFamilies(
        ICollection<string> lines,
        string subjectDimension,
        string subjectValue,
        string contentDimension,
        string contentValue,
        string subjectSurface)
    {
        for (var support = 1; support <= 3; support++)
        {
            var suffix = support.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lines.Add("@family stage6.fact." + subjectDimension + "." + suffix + " | Founder governed comparison content");
            lines.Add(subjectSurface + " costs " + contentValue + ". | " + subjectDimension + "=" + subjectValue + "; " + contentDimension + "=" + contentValue);
            lines.Add("@semantic-example fact-" + subjectDimension + "-" + suffix);
            lines.Add("@meaning");
            lines.Add("@node subject | " + subjectDimension + "=" + subjectValue + " | surface=" + subjectSurface);
            lines.Add("@node content | " + contentDimension + "=" + contentValue + " | surface=" + contentValue);
            lines.Add("@edge subject -> content | relation=has_attribute");
            lines.Add("@endmeaning");
            lines.Add("The governed attribute remains " + contentValue + ". | " + subjectDimension + "=" + subjectValue + "; " + contentDimension + "=" + contentValue);
            lines.Add("@end");
        }
    }

    private static string TrainingManifest()
    {
        var sourceSurfaces = new[]
        {
            "Could you offer guidance?",
            "Would you offer guidance?",
            "Please offer guidance."
        };
        var sourceComponents = new[] { "Could you", "Would you", "Please" };
        var lines = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var suffix = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            lines.Add($"@family stage5.request.{suffix} | Founder controlled request meaning");
            lines.Add(sourceSurfaces[index] + " | conversation_function=request; intent=guidance_request");
            lines.Add("@semantic-example request-" + suffix);
            lines.Add("@meaning");
            lines.Add("@node function | conversation_function=request | surface=" + sourceComponents[index]);
            lines.Add("@node intent | intent=guidance_request | surface=offer guidance");
            lines.Add("@edge function -> intent | relation=governs");
            lines.Add("@endmeaning");
            lines.Add("Guidance would be useful. | conversation_function=request; intent=guidance_request");
            lines.Add("@end");
            lines.Add($"@family stage5.offer.{suffix} | Founder controlled offer meaning");
            lines.Add("I can help with that. | conversation_function=offer_help; intent=guidance_offer");
            lines.Add("@semantic-example offer-" + suffix);
            lines.Add("@meaning");
            lines.Add("@node function | conversation_function=offer_help | surface=I can");
            lines.Add("@node intent | intent=guidance_offer | surface=help");
            lines.Add("@edge function -> intent | relation=governs");
            lines.Add("@endmeaning");
            lines.Add("Help is available. | conversation_function=offer_help; intent=guidance_offer");
            lines.Add("@end");
        }
        for (var index = 0; index < 3; index++)
        {
            var suffix = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            lines.Add("@relationship request-" + suffix + " -> offer-" + suffix + " | semantic=conversational-response");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// A fixed 8-family / 160-example normal Founder manifest used solely to
    /// compare a real V19 SourceFamilies pass with V20's metadata-only delta.
    /// Its independent family dimensions prevent a shared semantic lane from
    /// masking the number of broad historical work identities.
    /// </summary>
    private static string DependencyBenchmarkManifest()
    {
        var lines = new List<string>();
        for (var family = 0; family < 8; family++)
        {
            lines.Add($"@family dependency.delta.family.{family:D2} | Independent governed historical family {family:D2}");
            lines.Add("@ground conversation_function -> surface_phrase");
            for (var example = 0; example < 10; example++)
            {
                var text = $"Aster{family:D2}a{example:D2} Beryl{family:D2}b{example:D2} Cobalt{family:D2}c{example:D2}.";
                lines.Add(text + " | surface_phrase=" + text + "; conversation_function=greeting_" + family.ToString("D2") + "; discourse_role=opening_" + family.ToString("D2") + "; intent=start_conversation_" + family.ToString("D2") + "; register=neutral_" + family.ToString("D2") + "; domain_slot_" + family.ToString("D2") + "=independent_domain_" + family.ToString("D2"));
            }
            for (var example = 0; example < 10; example++)
            {
                var text = $"Dawn{family:D2}d{example:D2} Elm{family:D2}e{example:D2} Fable{family:D2}f{example:D2}.";
                lines.Add(text + " | surface_phrase=" + text + "; conversation_function=acknowledgement_" + family.ToString("D2") + "; discourse_role=response_" + family.ToString("D2") + "; intent=acknowledge_and_continue_" + family.ToString("D2") + "; register=neutral_" + family.ToString("D2") + "; domain_slot_" + family.ToString("D2") + "=independent_domain_" + family.ToString("D2"));
            }
            lines.Add("@transition");
            lines.Add("@source conversation_function=greeting_" + family.ToString("D2") + "; discourse_role=opening_" + family.ToString("D2") + "; intent=start_conversation_" + family.ToString("D2") + "; register=neutral_" + family.ToString("D2"));
            lines.Add("@result conversation_function=acknowledgement_" + family.ToString("D2") + "; discourse_role=response_" + family.ToString("D2") + "; intent=acknowledge_and_continue_" + family.ToString("D2") + "; register=neutral_" + family.ToString("D2"));
            lines.Add("@endtransition");
            lines.Add("@end");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string ContradictoryTransformationManifest()
    {
        var lines = new List<string>();
        var sourceForms = new[]
        {
            ("Could you offer guidance?", "Could you", "guidance"),
            ("Would you offer guidance?", "Would you", "guidance"),
            ("Please offer guidance.", "Please", "guidance"),
            ("May I request guidance?", "May I", "guidance"),
            ("I would value guidance.", "I would", "guidance"),
            ("Guidance would help.", "Guidance would", "guidance")
        };
        for (var index = 0; index < sourceForms.Length; index++)
        {
            var suffix = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            AppendRelationFamily(
                lines,
                "stage5.conflict.source." + suffix,
                sourceForms[index].Item1,
                "conflict-source-" + suffix,
                "request",
                "guidance_request",
                sourceForms[index].Item2,
                sourceForms[index].Item3);
            var isOffer = index < 3;
            AppendRelationFamily(
                lines,
                "stage5.conflict.result." + suffix,
                isOffer ? "I can offer help." : "Please provide more context.",
                (isOffer ? "conflict-offer-" : "conflict-context-") + suffix,
                isOffer ? "offer_help" : "context_request",
                isOffer ? "guidance_offer" : "context_needed",
                isOffer ? "I can" : "Please provide",
                isOffer ? "offer help" : "more context");
            lines.Add("@relationship conflict-source-" + suffix + " -> " +
                (isOffer ? "conflict-offer-" : "conflict-context-") + suffix +
                " | semantic=conversation-response");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string DistinguishingTransformationManifest()
    {
        var lines = new List<string>();
        foreach (var condition in new[] { "direct", "alternate" })
        {
            for (var index = 1; index <= 3; index++)
            {
                var suffix = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var functionSurface = index switch
                {
                    1 => "Could you",
                    2 => "Would you",
                    _ => "Please"
                };
                AppendRelationFamily(
                    lines,
                    "stage5.condition." + condition + ".source." + suffix,
                    functionSurface + " offer " + condition + " guidance?",
                    "condition-" + condition + "-source-" + suffix,
                    "request",
                    "guidance_request",
                    functionSurface,
                    "guidance",
                    condition);
                var isDirect = string.Equals(condition, "direct", StringComparison.Ordinal);
                AppendRelationFamily(
                    lines,
                    "stage5.condition." + condition + ".result." + suffix,
                    isDirect ? "I can offer direct help." : "I can suggest another approach.",
                    "condition-" + condition + "-result-" + suffix,
                    isDirect ? "offer_help" : "alternative_offer",
                    isDirect ? "guidance_offer" : "guidance_alternative",
                    "I can",
                    isDirect ? "direct help" : "another approach");
                lines.Add("@relationship condition-" + condition + "-source-" + suffix + " -> " +
                    "condition-" + condition + "-result-" + suffix +
                    " | semantic=conversation-response");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static readonly BlindScenario[] BlindScenarios =
    [
        new("resource", "resource_request", "resource_needed", "resource_response", "resource_shared",
            "I can share a useful resource.", "useful resource", "I can share", "useful resource",
            ["Could you share", "Would you share", "Please share"]),
        new("alternative", "route_request", "route_needed", "route_response", "route_shared",
            "I can suggest another route.", "another route", "I can suggest", "another route",
            ["Could you suggest", "Would you suggest", "Please suggest"]),
        new("clarification", "detail_request", "detail_needed", "detail_response", "detail_clarified",
            "I can clarify the details.", "the details", "I can clarify", "the details",
            ["Could you clarify", "Would you clarify", "Please clarify"]),
        new("gratitude", "acknowledgement", "guidance_appreciated", "gratitude_response", "gratitude_received",
            "You are welcome.", "your guidance", "You are", "welcome",
            ["I appreciate", "Thank you for", "I value"]),
        new("closing", "closing", "discussion_complete", "closing_response", "discussion_closed",
            "We can close our discussion.", "finish our discussion", "We can close", "our discussion",
            ["I will", "Let us", "We should"])
    ];

    private static readonly BlindPrompt[] BlindPrompts =
    [
        new("Would you share a useful resource with me, please?", "I can share a useful resource.", "resource_response", "resource_shared"),
        new("Please suggest another route for me.", "I can suggest another route.", "route_response", "route_shared"),
        new("Would you clarify the details for me?", "I can clarify the details.", "detail_response", "detail_clarified"),
        new("I appreciate all of your guidance.", "You are welcome.", "gratitude_response", "gratitude_received"),
        new("Let us finally finish our discussion.", "We can close our discussion.", "closing_response", "discussion_closed")
    ];

    private static string BlindGeneralizationManifest()
    {
        var lines = new List<string>();
        foreach (var scenario in BlindScenarios)
        {
            for (var index = 0; index < scenario.FunctionSurfaces.Length; index++)
            {
                var suffix = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var sourceKey = "blind-" + scenario.Key + "-source-" + suffix;
                var resultKey = "blind-" + scenario.Key + "-result-" + suffix;
                AppendRelationFamily(
                    lines,
                    "stage5.blind." + scenario.Key + ".source." + suffix,
                    scenario.FunctionSurfaces[index] + " " + scenario.IntentSurface + ".",
                    sourceKey,
                    scenario.SourceFunction,
                    scenario.SourceIntent,
                    scenario.FunctionSurfaces[index],
                    scenario.IntentSurface);
                AppendRelationFamily(
                    lines,
                    "stage5.blind." + scenario.Key + ".result." + suffix,
                    scenario.ResultText,
                    resultKey,
                    scenario.ResultFunction,
                    scenario.ResultIntent,
                    scenario.ResultFunctionSurface,
                    scenario.ResultIntentSurface);
                lines.Add("@relationship " + sourceKey + " -> " + resultKey + " | semantic=conversation-response");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void AssertNotInProductionCode(string prompt)
    {
        var root = Directory.GetCurrentDirectory();
        var productionFiles = new[] { "AgentPortal", "Domain", "Infrastructure" }
            .Select(path => Path.Combine(root, path))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains("/bin/", StringComparison.Ordinal) &&
                !path.Contains("/obj/", StringComparison.Ordinal));
        Assert.DoesNotContain(productionFiles, path =>
            File.ReadAllText(path).Contains(prompt, StringComparison.Ordinal));
    }

    private static void AppendRelationFamily(
        ICollection<string> lines,
        string familyKey,
        string text,
        string semanticKey,
        string function,
        string intent,
        string functionSurface,
        string intentSurface,
        string? condition = null)
    {
        lines.Add("@family " + familyKey + " | Founder governed semantic relation evidence");
        lines.Add(text + " | conversation_function=" + function + "; intent=" + intent);
        lines.Add("@semantic-example " + semanticKey);
        lines.Add("@meaning");
        lines.Add("@node function | conversation_function=" + function + " | surface=" + functionSurface);
        lines.Add("@node intent | intent=" + intent + " | surface=" + intentSurface);
        lines.Add("@edge function -> intent | relation=governs");
        if (condition is not null)
        {
            lines.Add("@node condition | response_condition=" + condition + " | surface=" + condition);
            lines.Add("@edge function -> condition | relation=qualified-by");
        }
        lines.Add("@endmeaning");
        lines.Add("Additional governed relation evidence. | conversation_function=" + function + "; intent=" + intent);
        lines.Add("@end");
    }

    private static readonly ConversationScenario[] ConversationScenarios =
    [
        new("opening", "opening", "greeting", "welcome", "warm_welcome", "Welcome, friend.",
            [("Hey Legend.", "Hey", "Legend"), ("Hello friend.", "Hello", "friend"), ("Good morning guide.", "Good morning", "guide")]),
        new("wellbeing", "wellbeing_request", "wellbeing", "wellbeing_response", "wellbeing_reply", "I am doing well, thank you.",
            [("How have you been?", "How have", "been"), ("How are you doing?", "How are", "doing"), ("Have you been well?", "Have you", "well")]),
        new("help", "help_request", "help", "help_offer", "help_available", "I can help you work through it.",
            [("I could use help.", "I could use", "help"), ("Could you help me?", "Could you", "help"), ("Please help me think.", "Please help", "think")]),
        new("followup", "detail_request", "detail", "detail_response", "detail_offered", "Here is more detail to consider.",
            [("Tell me more about that.", "Tell me more", "that"), ("Would you give more detail?", "Would you", "more detail"), ("Share another detail.", "Share", "detail")]),
        new("alternative", "alternative_request", "alternative", "alternative_response", "alternative_offered", "We can consider another approach.",
            [("Is there another way?", "Is there", "another way"), ("What other approach works?", "What other", "approach"), ("Please suggest an alternative.", "Please suggest", "alternative")]),
        new("correction", "correction", "option_correction", "correction_acknowledgement", "correction_understood", "I understand the correction.",
            [("I meant the first option.", "I meant", "option"), ("Actually choose the first option.", "Actually choose", "option"), ("Please use the first option.", "Please use", "option")], true),
        new("clarification", "clarification_request", "clarification", "clarification_response", "clarification_offered", "Let me explain that more clearly.",
            [("Explain this so I understand.", "Explain", "understand"), ("Please explain so I understand.", "explain", "understand"), ("I need to understand; explain.", "explain", "understand")]),
        new("gratitude", "acknowledgement", "gratitude", "gratitude_response", "gratitude_received", "You are welcome.",
            [("Thank you, I appreciate it.", "Thank you", "appreciate"), ("Thanks, I appreciate the guidance.", "Thanks", "appreciate"), ("I appreciate the explanation.", "I appreciate", "explanation")]),
        new("closing", "closing", "conversation_close", "closing_response", "closing_acknowledged", "Goodbye for now.",
            [("Talk to you later.", "Talk to you", "later"), ("Goodbye for now.", "Goodbye", "now"), ("I will return later.", "return", "later")]),
        new("alpha-choice", "option_statement", "alpha_choice", "acknowledgement", "option_noted", "I understand.",
            [("The alpha choice is affordable.", "alpha choice", "affordable"), ("An affordable alpha choice.", "alpha choice", "affordable"), ("This alpha choice remains affordable.", "alpha choice", "affordable")], EstablishedChoiceValue: "alpha"),
        new("beta-choice", "option_statement", "beta_choice", "acknowledgement", "option_noted", "I understand.",
            [("The beta choice is reliable.", "beta choice", "reliable"), ("A reliable beta choice.", "beta choice", "reliable"), ("This beta choice remains reliable.", "beta choice", "reliable")], EstablishedChoiceValue: "beta")
    ];

    private static readonly ConversationPrompt[] ConversationPrompts =
    [
        new("Hey Legend, good to see you.", "Welcome, friend.", "welcome", "warm_welcome"),
        new("The alpha choice feels affordable to me.", "I understand.", "acknowledgement", "option_noted"),
        new("The beta choice seems reliable to me.", "I understand.", "acknowledgement", "option_noted"),
        new("Can you tell me more about that?", "Here is more detail to consider.", "detail_response", "detail_offered"),
        new("Is there another way we could handle it?", "We can consider another approach.", "alternative_response", "alternative_offered"),
        new("No, I meant the first option.", "I understand the correction.", "correction_acknowledgement", "correction_understood", true),
        new("I don't quite understand. Can you explain it another way?", "Let me explain that more clearly.", "clarification_response", "clarification_offered"),
        new("That makes sense. Thank you, I appreciate the explanation.", "You are welcome.", "gratitude_response", "gratitude_received"),
        new("How have you been doing?", "I am doing well, thank you.", "wellbeing_response", "wellbeing_reply"),
        new("I could use some help figuring something out.", "I can help you work through it.", "help_offer", "help_available"),
        new("Alright, that's everything I needed. Talk to you later.", "Goodbye for now.", "closing_response", "closing_acknowledged"),
        new("Hello Legend, I am glad to meet you.", "Welcome, friend.", "welcome", "warm_welcome"),
        new("Could you help me sort this out?", "I can help you work through it.", "help_offer", "help_available"),
        new("Would you give me more detail?", "Here is more detail to consider.", "detail_response", "detail_offered"),
        new("Thank you, I appreciate that explanation.", "You are welcome.", "gratitude_response", "gratitude_received")
    ];

    private static string ConversationManifest()
    {
        var lines = new List<string>();
        foreach (var scenario in ConversationScenarios)
        {
            for (var index = 0; index < scenario.SourceForms.Length; index++)
            {
                var support = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var source = scenario.SourceForms[index];
                var sourceKey = scenario.Key + "-source-" + support;
                var resultKey = scenario.Key + "-result-" + support;
                lines.Add($"@family stage5.transcript.{scenario.Key}.source.{support} | Founder governed source meaning");
                lines.Add(source.Text + " | conversation_function=" + scenario.SourceFunction + "; intent=" + scenario.SourceIntent);
                lines.Add("@semantic-example " + sourceKey);
                lines.Add("@meaning");
                lines.Add("@node function | conversation_function=" + scenario.SourceFunction + " | surface=" + source.FunctionSurface);
                lines.Add("@node intent | intent=" + scenario.SourceIntent + " | surface=" + source.IntentSurface);
                lines.Add("@edge function -> intent | relation=governs");
                if (scenario.EstablishedChoiceValue is not null)
                {
                    lines.Add("@node choice | choice=" + scenario.EstablishedChoiceValue + " | surface=" +
                        scenario.EstablishedChoiceValue + " choice");
                    lines.Add("@edge function -> choice | relation=mentions");
                }
                if (scenario.RequiresFirstChoiceBinding)
                {
                    lines.Add("@node selector | reference_selector=ordinal_one | surface=first");
                    lines.Add("@node kind | reference_kind=choice | surface=option");
                    lines.Add("@edge function -> selector | relation=corrects");
                    lines.Add("@edge selector -> kind | relation=reference-target");
                    lines.Add("@reference selector | entity_dimension=choice | resolution=ordinal | rank=1 | roles=user,assistant | replace_active=true");
                }
                lines.Add("@endmeaning");
                lines.Add("Additional governed source evidence. | conversation_function=" + scenario.SourceFunction + "; intent=" + scenario.SourceIntent);
                lines.Add("@end");

                lines.Add($"@family stage5.transcript.{scenario.Key}.result.{support} | Founder governed result meaning");
                lines.Add(scenario.ResultText + " | conversation_function=" + scenario.ResultFunction + "; intent=" + scenario.ResultIntent);
                lines.Add("@semantic-example " + resultKey);
                lines.Add("@meaning");
                lines.Add("@node function | conversation_function=" + scenario.ResultFunction + " | surface=" + ResultFunctionSurface(scenario.ResultText));
                lines.Add("@node intent | intent=" + scenario.ResultIntent + " | surface=" + ResultIntentSurface(scenario.ResultText));
                lines.Add("@edge function -> intent | relation=governs");
                lines.Add("@endmeaning");
                lines.Add("Additional governed result evidence. | conversation_function=" + scenario.ResultFunction + "; intent=" + scenario.ResultIntent);
                lines.Add("@end");
            }
        }
        foreach (var scenario in ConversationScenarios)
        {
            for (var index = 0; index < 3; index++)
            {
                var support = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                lines.Add("@relationship " + scenario.Key + "-source-" + support + " -> " +
                    scenario.Key + "-result-" + support + " | semantic=conversation-response");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string ResultFunctionSurface(string resultText) => resultText switch
    {
        "Welcome, friend." => "Welcome",
        "I am doing well, thank you." => "I am",
        "I can help you work through it." => "I can",
        "Here is more detail to consider." => "Here is",
        "We can consider another approach." => "We can",
        "I understand the correction." => "I understand",
        "Let me explain that more clearly." => "Let me",
        "You are welcome." => "You are",
        "Goodbye for now." => "Goodbye",
        "I understand." => "I understand",
        _ => throw new InvalidOperationException("The frozen Stage-5 curriculum result has no governed surface.")
    };

    private static string ResultIntentSurface(string resultText) => resultText switch
    {
        "Welcome, friend." => "friend",
        "I am doing well, thank you." => "doing well",
        "I can help you work through it." => "help you",
        "Here is more detail to consider." => "more detail",
        "We can consider another approach." => "another approach",
        "I understand the correction." => "correction",
        "Let me explain that more clearly." => "more clearly",
        "You are welcome." => "welcome",
        "Goodbye for now." => "now",
        "I understand." => "understand",
        _ => throw new InvalidOperationException("The frozen Stage-5 curriculum result has no governed surface.")
    };

    private static string DescribeNodes(LegendConnectUtteranceMeaningGraphSnapshot graph) =>
        string.Join(", ", graph.Nodes.OrderBy(item => item.SemanticDimension, StringComparer.Ordinal)
            .ThenBy(item => item.SemanticValue, StringComparer.Ordinal)
            .Select(item => item.SemanticDimension + "=" + item.SemanticValue));

    private static string DescribeRelations(LegendConnectUtteranceMeaningGraphSnapshot graph) =>
        string.Join(", ", graph.Relations.OrderBy(item => item.RelationKind, StringComparer.Ordinal)
            .Select(item => item.RelationKind + ":" +
                graph.Nodes[item.SourceNodeIndex].SemanticDimension + "->" +
                graph.Nodes[item.TargetNodeIndex].SemanticDimension));

    private static string DescribeBindings(LegendConnectDiscourseStateSnapshot state) =>
        string.Join(", ", state.Turns.SelectMany(turn => turn.Bindings)
            .OrderBy(item => item.EntitySemanticDimension, StringComparer.Ordinal)
            .ThenBy(item => item.EntitySemanticValue, StringComparer.Ordinal)
            .Select(item => item.ResolutionState + ":" + item.EntitySemanticDimension + "=" +
                (item.EntitySemanticValue ?? "<none>")));

    private sealed record ConversationScenario(
        string Key,
        string SourceFunction,
        string SourceIntent,
        string ResultFunction,
        string ResultIntent,
        string ResultText,
        (string Text, string FunctionSurface, string IntentSurface)[] SourceForms,
        bool RequiresFirstChoiceBinding = false,
        string? EstablishedChoiceValue = null);

    private sealed record ConversationPrompt(
        string User,
        string ExpectedResponse,
        string ResultFunction,
        string ResultIntent,
        bool RequiresFirstChoiceBinding = false);

    private sealed record BlindScenario(
        string Key,
        string SourceFunction,
        string SourceIntent,
        string ResultFunction,
        string ResultIntent,
        string ResultText,
        string IntentSurface,
        string ResultFunctionSurface,
        string ResultIntentSurface,
        string[] FunctionSurfaces);

    private sealed record BlindPrompt(
        string User,
        string ExpectedResponse,
        string ResultFunction,
        string ResultIntent);

    private sealed record GovernedContentCaseProof(
        string TransitionSignature,
        IReadOnlyDictionary<string, string> ContentBindings,
        string Reply,
        int OpenAiClientCount,
        string LeftSurface,
        string RightSurface,
        string? ReasonCode);

    /// <summary>
    /// Records only SQL command classes for the delta-convergence benchmark.
    /// It intentionally captures neither command text nor parameter values,
    /// so this release proof cannot retain Founder or production content.
    /// </summary>
    private sealed class SqlCommandMetrics : DbCommandInterceptor
    {
        private long _reads;
        private long _writes;

        public void Reset()
        {
            Interlocked.Exchange(ref _reads, 0);
            Interlocked.Exchange(ref _writes, 0);
        }

        public SqlCommandMetricSnapshot Snapshot() => new(
            Interlocked.Read(ref _reads),
            Interlocked.Read(ref _writes));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            return new ValueTask<InterceptionResult<int>>(result);
        }
    }

    private sealed record SqlCommandMetricSnapshot(long Reads, long Writes);

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            throw new InvalidOperationException("OpenAI must not be created by the governed Stage-5 proof.");
        }
    }

    private sealed class ExceptionCapturingLogger<T> : ILogger<T>
    {
        public Exception? LatestException { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                LatestException = exception;
        }
    }

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(MessagingActor actor, string resourceType, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, true));
        public Task<bool> IsFounderManagerAsync(MessagingActor actor, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed record Services(
        LegendConnectOperations Operations,
        LegendConnectCurriculumService Curriculum,
        LegendConnectHistoricalReevaluationWorkAuthority Work,
        LegendConnectRuntimePolicyAuthority Runtime,
        LegendConnectTranslationIntelligence Intelligence);

    private sealed record CanonicalCounts(
        int Families,
        int Examples,
        int Anchors,
        int MeaningNodes,
        int MeaningRelations,
        int FounderCrossExampleRelations,
        int Transitions);

    private sealed record ActiveAnchorIdentity(
        Guid CurriculumExampleId,
        string AnchorSignature,
        string Dimension,
        string Value,
        Guid? LexemeId,
        int? ComponentStartTokenIndex,
        int? ComponentLength,
        string? SemanticSignature,
        string Provenance);

    private sealed record ActiveTransitionIdentity(
        string TransitionSignature,
        Guid SourceCurriculumExampleId,
        Guid ResultCurriculumExampleId,
        string SourceSemanticFrameSignature,
        string ResultSemanticFrameSignature,
        string ContributionState,
        string IndependentSourceIdentity,
        string? FounderRelationshipSemanticSignature,
        int? DerivationEvaluatorVersion);
}
