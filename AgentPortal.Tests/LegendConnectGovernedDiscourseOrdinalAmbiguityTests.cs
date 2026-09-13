using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendConnectGovernedDiscourseOrdinalAmbiguityTests
{
    [Fact]
    public async Task OrdinalReferences_FirstSecondLast_PersistAcrossFreshReloads()
    {
        var databaseName = Guid.NewGuid().ToString("D");
        var root = new InMemoryDatabaseRoot();
        var actor = Guid.NewGuid().ToString("D");
        await using (var setup = CreateDb(databaseName, root))
        {
            setup.AgentProfiles.Add(Profile(actor, "ordinal"));
            await setup.SaveChangesAsync();
            var curriculum = CreateCurriculum(setup);
            for (var family = 1; family <= 3; family++)
            {
                var submitted = await curriculum.SubmitFounderBatchAsync(
                    OrdinalBindingFamily(family));
                Assert.True(submitted.Succeeded, submitted.Message);
            }
        }

        async Task ObserveAsync(Guid conversationId, string surface)
        {
            await using var db = CreateDb(databaseName, root);
            var operations = CreateOperations(db);
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(surface);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    operations)
                .RecordObservationAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString(),
                    "user",
                    graph);
        }

        async Task<LegendFounderAiDiscourseReferenceBinding> LatestAsync(Guid conversationId)
        {
            await using var db = CreateDb(databaseName, root);
            return Assert.Single(await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    CreateOperations(db))
                .GetLatestBindingsAsync(actor, conversationId));
        }

        var firstConversation = Guid.NewGuid();
        await ObserveAsync(firstConversation, "a b c");
        await ObserveAsync(firstConversation, "the first one");
        var first = await LatestAsync(firstConversation);
        Assert.Equal("bound", first.ResolutionState);
        Assert.Equal("a", first.EntitySemanticValue);

        var secondConversation = Guid.NewGuid();
        await ObserveAsync(secondConversation, "a b c");
        await ObserveAsync(secondConversation, "the second one");
        var second = await LatestAsync(secondConversation);
        Assert.Equal("bound", second.ResolutionState);
        Assert.Equal("b", second.EntitySemanticValue);

        var lastConversation = Guid.NewGuid();
        await ObserveAsync(lastConversation, "a b c");
        await ObserveAsync(lastConversation, "the last one");
        var last = await LatestAsync(lastConversation);
        Assert.Equal("bound", last.ResolutionState);
        Assert.Equal("c", last.EntitySemanticValue);
    }

    [Fact]
    public async Task OrdinalReference_WithoutAntecedent_RemainsFailClosed()
    {
        var databaseName = Guid.NewGuid().ToString("D");
        var root = new InMemoryDatabaseRoot();
        var actor = Guid.NewGuid().ToString("D");
        await using (var setup = CreateDb(databaseName, root))
        {
            setup.AgentProfiles.Add(Profile(actor, "missing"));
            await setup.SaveChangesAsync();
            var curriculum = CreateCurriculum(setup);
            for (var family = 1; family <= 3; family++)
            {
                var submitted = await curriculum.SubmitFounderBatchAsync(
                    OrdinalBindingFamily(family));
                Assert.True(submitted.Succeeded, submitted.Message);
            }
        }

        var conversationId = Guid.NewGuid();
        await using (var db = CreateDb(databaseName, root))
        {
            var operations = CreateOperations(db);
            var graph = await operations.AnalyzeReusableMeaningGraphAsync("the first one");
            Assert.True(graph.IsComposed, graph.ReasonCode);
            var discourse = new LegendFounderAiDiscourseStateService(
                db,
                new AgentProfileAccessResolver(db),
                operations);
            await discourse.RecordObservationAsync(
                ControllerTestHelpers.BuildUser(actor),
                conversationId.ToString(),
                "user",
                graph);
        }

        await using (var db = CreateDb(databaseName, root))
        {
            var operations = CreateOperations(db);
            var discourse = new LegendFounderAiDiscourseStateService(
                db,
                new AgentProfileAccessResolver(db),
                operations);
            var binding = Assert.Single(await discourse.GetLatestBindingsAsync(actor, conversationId));
            Assert.Equal("unresolved", binding.ResolutionState);
            Assert.Equal("reference_candidate_missing", binding.ReasonCode);

            var state = Assert.IsType<LegendConnectDiscourseStateSnapshot>(
                await discourse.GetStateAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString()));
            var planned = await operations.TryPlanConversationAsync("the first one", state);
            Assert.False(planned.Supported);
            Assert.Equal("discourse_reference_unresolved", planned.ReasonCode);
        }
    }

    [Theory]
    [InlineData("No, I meant the first option.", false)]
    [InlineData("No, please use the first option instead.", true)]
    public async Task HeldOutCorrection_RequiresAGroundedFunctionBeforeAnsweringTheBoundOrdinalWithoutProviders(
        string currentRequest, bool hasGroundedCorrection)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var actor = Guid.NewGuid().ToString("D");
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", actor);
            db.AgentProfiles.Add(Profile(actor, "heldout"));
            await db.SaveChangesAsync();
            var curriculum = CreateCurriculum(db);
            for (var family = 1; family <= 3; family++)
            {
                var submitted = await curriculum.SubmitFounderBatchAsync(
                    ProductionStyleChoiceFamily(family));
                Assert.True(submitted.Succeeded, submitted.Message);
            }

            Assert.DoesNotContain(
                await db.LegendLanguageTextUnits.Select(item => item.Text).ToListAsync(),
                text => string.Equals(text, currentRequest, StringComparison.Ordinal));

            var operations = CreateOperations(db);
            var profiles = new AgentProfileAccessResolver(db);
            var discourse = new LegendFounderAiDiscourseStateService(db, profiles, operations);
            var founder = ControllerTestHelpers.BuildUser(actor);
            var priorMessages = new[]
            {
                new LegendFounderAiChatMessage("user", "The alpha choice feels affordable to me."),
                new LegendFounderAiChatMessage("user", "The beta choice seems reliable to me.")
            };
            var directConversationId = Guid.NewGuid();
            foreach (var message in priorMessages)
            {
                var graph = await operations.AnalyzeReusableMeaningGraphAsync(message.Content ?? string.Empty);
                Assert.True(graph.IsComposed, graph.ReasonCode);
                await discourse.RecordObservationAsync(
                    founder,
                    directConversationId.ToString(),
                    message.Role ?? string.Empty,
                    graph);
            }

            var currentGraph = await operations.AnalyzeReusableMeaningGraphAsync(
                currentRequest);
            Assert.True(currentGraph.IsComposed, currentGraph.ReasonCode);
            Assert.Equal(hasGroundedCorrection, currentGraph.Nodes.Any(node =>
                node.SemanticDimension == "conversation_function" && node.SemanticValue == "correction"));
            await discourse.RecordObservationAsync(
                founder,
                directConversationId.ToString(),
                "user",
                currentGraph);

            var directState = Assert.IsType<LegendConnectDiscourseStateSnapshot>(
                await discourse.GetStateAsync(founder, directConversationId.ToString()));
            var directPlan = await operations.TryPlanConversationAsync(
                currentRequest,
                directState);
            if (hasGroundedCorrection)
            {
                Assert.True(directPlan.Supported, directPlan.ReasonCode);
                var directStructuredPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(
                    directPlan.Plan);
                var directBinding = Assert.Single(directStructuredPlan.ResolvedDiscourseBindings);
                Assert.Equal("bound", directBinding.ResolutionState);
                Assert.Equal("choice", directBinding.EntitySemanticDimension);
                Assert.Equal("alpha", directBinding.EntitySemanticValue);
                Assert.True(directBinding.ReplacesActiveBinding);
                Assert.False(directBinding.HasSupersededCurrentTurnEntity);
                Assert.Null(directBinding.SupersededCurrentTurnNodeIndex);
                Assert.Null(directBinding.SupersededCurrentTurnSemanticDimension);
                Assert.Null(directBinding.SupersededCurrentTurnSemanticSignature);
                Assert.Null(directBinding.SupersededCurrentTurnSemanticValue);
                Assert.Null(directBinding.SupersededCurrentTurnNodeStartTokenIndex);
                Assert.Null(directBinding.SupersededCurrentTurnNodeTokenLength);
            }
            else
            {
                // An ordinal replacement binds the choice; it does not supply
                // the separate, unobserved correction-function premise.
                Assert.False(directPlan.Supported);
                Assert.Equal("semantic_transition_not_supported", directPlan.ReasonCode);
                Assert.Null(directPlan.Plan);
            }

            var directNative = await operations.TryInferConversationWithDiscourseAsync(
                currentRequest,
                priorMessages.Select(message => new LegendConnectConversationContextItem(
                        message.Role ?? string.Empty,
                        message.Content ?? string.Empty))
                    .ToArray(),
                directState);
            Assert.Equal(hasGroundedCorrection, directNative.Supported);
            if (hasGroundedCorrection)
                Assert.Equal("I understand the correction.", directNative.Answer);
            else
                Assert.Null(directNative.Answer);

            var replyConversationId = Guid.NewGuid();
            foreach (var message in priorMessages)
            {
                var graph = await operations.AnalyzeReusableMeaningGraphAsync(message.Content ?? string.Empty);
                Assert.True(graph.IsComposed, graph.ReasonCode);
                await discourse.RecordObservationAsync(
                    founder,
                    replyConversationId.ToString(),
                    message.Role ?? string.Empty,
                    graph);
            }

            var countingFactory = new CountingHttpClientFactory();
            var chat = new LegendFounderAiConversationService(
                countingFactory,
                Configuration(),
                new FounderLegendConnectService(operations, profiles),
                NullLogger<LegendFounderAiConversationService>.Instance,
                discourse,
                new LegendLanguageRegistry(db, Configuration()),
                ControllerTestHelpers.BuildTranslationService());
            var reply = await chat.ReplyAsync(
                founder,
                new LegendFounderAiChatRequest
                {
                    Mode = "legend",
                    NativeOnly = true,
                    SourceLanguageCode = "en",
                    ConversationId = replyConversationId.ToString(),
                    Messages =
                    [
                        .. priorMessages,
                        new LegendFounderAiChatMessage("user", currentRequest)
                    ]
                });

            // A governed diagnostic is not a successfully answered correction.
            Assert.Equal(hasGroundedCorrection, reply.Succeeded);
            if (hasGroundedCorrection)
            {
                Assert.Equal("I understand the correction.", reply.Message);
                Assert.Equal("LegendAi", reply.ResponseAuthority);
            }
            else
            {
                Assert.Equal("SystemDiagnostic", reply.ResponseAuthority);
                Assert.NotEqual("I understand the correction.", reply.Message);
            }
            Assert.Equal(0, countingFactory.CreateClientCalls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    [Fact]
    public async Task ReplacementBinding_PersistsProducerIssuedCurrentTurnOccurrenceIdentity()
    {
        var databaseName = Guid.NewGuid().ToString("D");
        var root = new InMemoryDatabaseRoot();
        var actor = Guid.NewGuid().ToString("D");
        await using (var setup = CreateDb(databaseName, root))
        {
            setup.AgentProfiles.Add(Profile(actor, "replacement-missing"));
            await setup.SaveChangesAsync();
            var curriculum = CreateCurriculum(setup);
            for (var family = 1; family <= 3; family++)
            {
                var submitted = await curriculum.SubmitFounderBatchAsync(
                    ProducerIssuedReplacementFamily(family));
                Assert.True(submitted.Succeeded, submitted.Message);
            }
        }

        var conversationId = Guid.NewGuid();
        async Task ObserveAnalyzedAsync(string surface)
        {
            await using var db = CreateDb(databaseName, root);
            var operations = CreateOperations(db);
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(surface);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    operations)
                .RecordObservationAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString(),
                    "user",
                    graph);
        }

        await ObserveAnalyzedAsync("The alpha choice feels affordable to me.");
        await ObserveAnalyzedAsync("The beta choice seems reliable to me.");

        await using (var db = CreateDb(databaseName, root))
        {
            var operations = CreateOperations(db);
            var correctionGraph = await operations.AnalyzeReusableMeaningGraphAsync(
                "Please route the first marker.");
            Assert.True(correctionGraph.IsComposed, correctionGraph.ReasonCode);
            var selector = Assert.Single(correctionGraph.Nodes.Where(item =>
                item.SemanticDimension == "reference_selector"));
            Assert.NotNull(selector.SupersededCurrentTurnOccurrence);
            var occurrence = selector.SupersededCurrentTurnOccurrence!;
            var superseded = correctionGraph.Nodes[occurrence.NodeIndex];
            Assert.Equal("choice", occurrence.SemanticDimension);
            Assert.Equal("beta", occurrence.SemanticValue);
            Assert.Equal("choice", superseded.SemanticDimension);
            Assert.Equal("beta", superseded.SemanticValue);

            await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    operations)
                .RecordObservationAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString(),
                    "user",
                    correctionGraph);
        }

        await using (var db = CreateDb(databaseName, root))
        {
            var operations = CreateOperations(db);
            var discourse = new LegendFounderAiDiscourseStateService(
                db,
                new AgentProfileAccessResolver(db),
                operations);
            var binding = Assert.Single(await discourse.GetLatestBindingsAsync(actor, conversationId));
            Assert.Equal("bound", binding.ResolutionState);
            Assert.Equal("alpha", binding.EntitySemanticValue);
            Assert.True(binding.ReplacesActiveBinding);
            Assert.True(binding.HasSupersededCurrentTurnEntity);
            Assert.NotNull(binding.SupersededCurrentTurnNodeIndex);
            Assert.Equal("choice", binding.SupersededCurrentTurnSemanticDimension);
            Assert.Equal("beta", binding.SupersededCurrentTurnSemanticValue);

            var state = Assert.IsType<LegendConnectDiscourseStateSnapshot>(
                await discourse.GetStateAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString()));
            var projectedTurn = Assert.Single(state.Turns.Where(item => item.SequenceNumber == 3));
            var projectedSelector = Assert.Single(projectedTurn.Nodes.Where(item =>
                item.SemanticDimension == "reference_selector"));
            Assert.NotNull(projectedSelector.SupersededCurrentTurnOccurrence);

            var pruning = InvokeReplacementPruning(
                projectedTurn.Nodes,
                projectedTurn.Relations,
                projectedTurn.Bindings);
            Assert.True(pruning.Succeeded);
            Assert.DoesNotContain(
                pruning.Nodes,
                item => item.SemanticDimension == "choice" && item.SemanticValue == "beta");

            var plan = await operations.TryPlanConversationAsync("Please route the first marker.", state);
            Assert.True(plan.Supported, plan.ReasonCode);
        }
    }

    [Fact]
    public async Task ReplacementBinding_PreservesBroadContainingCurrentTurnCandidateWithoutProducerLineage()
    {
        var databaseName = Guid.NewGuid().ToString("D");
        var root = new InMemoryDatabaseRoot();
        var actor = Guid.NewGuid().ToString("D");
        await using (var setup = CreateDb(databaseName, root))
        {
            setup.AgentProfiles.Add(Profile(actor, "replacement-anchored"));
            await setup.SaveChangesAsync();
            var curriculum = CreateCurriculum(setup);
            for (var family = 1; family <= 3; family++)
            {
                var submitted = await curriculum.SubmitFounderBatchAsync(
                    ProducerIssuedBroadReplacementFamily(family));
                Assert.True(submitted.Succeeded, submitted.Message);
            }
        }

        var conversationId = Guid.NewGuid();
        async Task ObserveAnalyzedAsync(string surface)
        {
            await using var db = CreateDb(databaseName, root);
            var operations = CreateOperations(db);
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(surface);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    operations)
                .RecordObservationAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString(),
                    "user",
                    graph);
        }

        await ObserveAnalyzedAsync("The alpha choice feels affordable to me.");
        await ObserveAnalyzedAsync("The beta choice seems reliable to me.");

        await using (var db = CreateDb(databaseName, root))
        {
            var operations = CreateOperations(db);
            var correctionGraph = await operations.AnalyzeReusableMeaningGraphAsync(
                "Please route the first marker now.");
            Assert.True(correctionGraph.IsComposed, correctionGraph.ReasonCode);
            var selector = Assert.Single(correctionGraph.Nodes.Where(item =>
                item.SemanticDimension == "reference_selector"));
            Assert.Null(selector.SupersededCurrentTurnOccurrence);
            Assert.Contains(correctionGraph.Nodes, item =>
                item.SemanticDimension == "choice" &&
                item.SemanticValue == "beta" &&
                item.TokenLength > selector.TokenLength);

            await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    operations)
                .RecordObservationAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString(),
                    "user",
                    correctionGraph);
        }

        await using (var db = CreateDb(databaseName, root))
        {
            var operations = CreateOperations(db);
            var discourse = new LegendFounderAiDiscourseStateService(
                db,
                new AgentProfileAccessResolver(db),
                operations);
            var binding = Assert.Single(await discourse.GetLatestBindingsAsync(actor, conversationId));
            Assert.Equal("bound", binding.ResolutionState);
            Assert.Equal("alpha", binding.EntitySemanticValue);
            Assert.True(binding.ReplacesActiveBinding);
            Assert.False(binding.HasSupersededCurrentTurnEntity);
            Assert.Null(binding.SupersededCurrentTurnNodeIndex);

            var state = Assert.IsType<LegendConnectDiscourseStateSnapshot>(
                await discourse.GetStateAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString()));
            var projectedTurn = Assert.Single(state.Turns.Where(item => item.SequenceNumber == 3));
            var projectedSelector = Assert.Single(projectedTurn.Nodes.Where(item =>
                item.SemanticDimension == "reference_selector"));
            Assert.Null(projectedSelector.SupersededCurrentTurnOccurrence);

            var pruning = InvokeReplacementPruning(
                projectedTurn.Nodes,
                projectedTurn.Relations,
                projectedTurn.Bindings);
            Assert.True(pruning.Succeeded);
            Assert.Equal(projectedTurn.Nodes, pruning.Nodes);
            Assert.Equal(projectedTurn.Relations, pruning.Relations);

            var plan = await operations.TryPlanConversationAsync("Please route the first marker now.", state);
            Assert.False(plan.Supported);
            Assert.Equal("ambiguous_composed_meaning", plan.ReasonCode);
        }
    }

    [Fact]
    public async Task ReplacementBinding_DuplicateProducerCandidatesPreserveGraphAndFailClosed()
    {
        var databaseName = Guid.NewGuid().ToString("D");
        var root = new InMemoryDatabaseRoot();
        var actor = Guid.NewGuid().ToString("D");
        await using (var setup = CreateDb(databaseName, root))
        {
            setup.AgentProfiles.Add(Profile(actor, "replacement-duplicate"));
            await setup.SaveChangesAsync();
            var curriculum = CreateCurriculum(setup);
            for (var family = 1; family <= 3; family++)
            {
                var submitted = await curriculum.SubmitFounderBatchAsync(
                    ProducerIssuedDuplicateReplacementFamily(family));
                Assert.True(submitted.Succeeded, submitted.Message);
            }
        }

        var conversationId = Guid.NewGuid();
        async Task ObserveAnalyzedAsync(string surface)
        {
            await using var db = CreateDb(databaseName, root);
            var operations = CreateOperations(db);
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(surface);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    operations)
                .RecordObservationAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString(),
                    "user",
                    graph);
        }

        await ObserveAnalyzedAsync("The alpha choice feels affordable to me.");
        await ObserveAnalyzedAsync("The beta choice seems reliable to me.");

        await using (var db = CreateDb(databaseName, root))
        {
            var operations = CreateOperations(db);
            var correctionGraph = await operations.AnalyzeReusableMeaningGraphAsync(
                "Please route the first marker twice.");
            Assert.True(correctionGraph.IsComposed, correctionGraph.ReasonCode);
            var selector = Assert.Single(correctionGraph.Nodes.Where(item =>
                item.SemanticDimension == "reference_selector"));
            Assert.Null(selector.SupersededCurrentTurnOccurrence);
            Assert.Equal(2, correctionGraph.Nodes.Count(item =>
                item.SemanticDimension == "choice" &&
                item.StartTokenIndex == selector.StartTokenIndex &&
                item.TokenLength == selector.TokenLength));

            await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    operations)
                .RecordObservationAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString(),
                    "user",
                    correctionGraph);
        }

        await using (var db = CreateDb(databaseName, root))
        {
            var operations = CreateOperations(db);
            var discourse = new LegendFounderAiDiscourseStateService(
                db,
                new AgentProfileAccessResolver(db),
                operations);
            var binding = Assert.Single(await discourse.GetLatestBindingsAsync(actor, conversationId));
            Assert.Equal("bound", binding.ResolutionState);
            Assert.Equal("alpha", binding.EntitySemanticValue);
            Assert.False(binding.HasSupersededCurrentTurnEntity);

            var state = Assert.IsType<LegendConnectDiscourseStateSnapshot>(
                await discourse.GetStateAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString()));
            var plan = await operations.TryPlanConversationAsync("Please route the first marker twice.", state);
            Assert.False(plan.Supported);
            Assert.Equal("ambiguous_composed_meaning", plan.ReasonCode);
        }
    }

    [Fact]
    public void ReplacementPruning_RemovesOnlySelectorLocalSupersededOccurrence_AndPreservesOtherRelations()
    {
        var nodes = new[]
        {
            new LegendConnectUtteranceMeaningNode("compare", "conversation_function", "compare", 0, 1, 3),
            new LegendConnectUtteranceMeaningNode(
                "selector",
                "reference_selector",
                "ordinal_one",
                1,
                1,
                3,
                SupersededCurrentTurnOccurrence: new LegendConnectCurrentTurnOccurrenceSnapshot(
                    2,
                    "one",
                    "choice",
                    "one",
                    2,
                    1)),
            new LegendConnectUtteranceMeaningNode("one", "choice", "one", 2, 1, 3),
            new LegendConnectUtteranceMeaningNode("two", "choice", "two", 3, 1, 3),
            new LegendConnectUtteranceMeaningNode("note", "choice_note", "stable", 4, 1, 3)
        };
        var relations = new[]
        {
            new LegendConnectUtteranceMeaningRelation("r1", "references", 0, 1, 3),
            new LegendConnectUtteranceMeaningRelation("r2", "describes", 0, 2, 3),
            new LegendConnectUtteranceMeaningRelation("r3", "describes", 3, 4, 3)
        };
        var bindings = new[]
        {
            new LegendConnectDiscourseReferenceBindingSnapshot(
                "bound",
                "ok",
                "choice",
                "alpha",
                "alpha",
                1,
                0,
                true,
                "selector",
                "rule")
            {
                HasSupersededCurrentTurnEntity = true,
                SelectorNodeIndex = 1,
                SupersededCurrentTurnNodeIndex = 2,
                SupersededCurrentTurnSemanticSignature = "one",
                SupersededCurrentTurnSemanticDimension = "choice",
                SupersededCurrentTurnSemanticValue = "one",
                SupersededCurrentTurnNodeStartTokenIndex = 2,
                SupersededCurrentTurnNodeTokenLength = 1
            }
        };

        var result = InvokeReplacementPruning(nodes, relations, bindings);

        Assert.True(result.Succeeded);
        Assert.Equal(["compare", "selector", "two", "note"], result.Nodes.Select(item => item.SemanticSignature));
        Assert.Equal(2, result.Relations.Count);
        var preservedRelation = Assert.Single(result.Relations.Where(item => item.RelationKind == "describes"));
        Assert.Equal("two", result.Nodes[preservedRelation.SourceNodeIndex].SemanticSignature);
        Assert.Equal("note", result.Nodes[preservedRelation.TargetNodeIndex].SemanticSignature);
    }

    [Fact]
    public void ReplacementPruning_FailsClosedForConflictingReplacementOrderPermutations()
    {
        var nodes = new[]
        {
            new LegendConnectUtteranceMeaningNode("compare_one", "conversation_function", "compare_one", 0, 1, 3),
            new LegendConnectUtteranceMeaningNode(
                "selector_one",
                "reference_selector",
                "ordinal_one",
                1,
                1,
                3,
                SupersededCurrentTurnOccurrence: new LegendConnectCurrentTurnOccurrenceSnapshot(
                    2,
                    "one",
                    "choice",
                    "one",
                    2,
                    1)),
            new LegendConnectUtteranceMeaningNode("one", "choice", "one", 2, 1, 3),
            new LegendConnectUtteranceMeaningNode("compare_two", "conversation_function", "compare_two", 3, 1, 3),
            new LegendConnectUtteranceMeaningNode(
                "selector_two",
                "reference_selector",
                "ordinal_two",
                4,
                1,
                3,
                SupersededCurrentTurnOccurrence: new LegendConnectCurrentTurnOccurrenceSnapshot(
                    5,
                    "two",
                    "choice",
                    "two",
                    5,
                    1)),
            new LegendConnectUtteranceMeaningNode("two", "choice", "two", 5, 1, 3)
        };
        var relations = new[]
        {
            new LegendConnectUtteranceMeaningRelation("r1", "references", 0, 1, 3),
            new LegendConnectUtteranceMeaningRelation("r2", "describes", 0, 2, 3),
            new LegendConnectUtteranceMeaningRelation("r3", "references", 3, 4, 3),
            new LegendConnectUtteranceMeaningRelation("r4", "describes", 3, 5, 3)
        };
        var firstOrder = new[]
        {
            new LegendConnectDiscourseReferenceBindingSnapshot("bound", "ok", "choice", "alpha", "alpha", 1, 0, true, "selector_one", "rule_one")
            {
                SelectorNodeIndex = 1,
                HasSupersededCurrentTurnEntity = true,
                SupersededCurrentTurnNodeIndex = 2,
                SupersededCurrentTurnSemanticSignature = "one",
                SupersededCurrentTurnSemanticDimension = "choice",
                SupersededCurrentTurnSemanticValue = "one",
                SupersededCurrentTurnNodeStartTokenIndex = 2,
                SupersededCurrentTurnNodeTokenLength = 1
            },
            new LegendConnectDiscourseReferenceBindingSnapshot("bound", "ok", "choice", "beta", "beta", 1, 1, true, "selector_two", "rule_two")
            {
                SelectorNodeIndex = 4,
                HasSupersededCurrentTurnEntity = true,
                SupersededCurrentTurnNodeIndex = 5,
                SupersededCurrentTurnSemanticSignature = "two",
                SupersededCurrentTurnSemanticDimension = "choice",
                SupersededCurrentTurnSemanticValue = "two",
                SupersededCurrentTurnNodeStartTokenIndex = 5,
                SupersededCurrentTurnNodeTokenLength = 1
            }
        };
        var reversedOrder = firstOrder.Reverse().ToArray();

        Assert.False(InvokeReplacementPruning(nodes, relations, firstOrder).Succeeded);
        Assert.False(InvokeReplacementPruning(nodes, relations, reversedOrder).Succeeded);
    }

    [Fact]
    public void ReplacementPruning_DoesNotInferSupersededOccurrenceFromGraphShape()
    {
        var nodes = new[]
        {
            new LegendConnectUtteranceMeaningNode("compare", "conversation_function", "compare", 0, 1, 3),
            new LegendConnectUtteranceMeaningNode("selector", "reference_selector", "ordinal_one", 1, 1, 3),
            new LegendConnectUtteranceMeaningNode("one", "choice", "one", 2, 1, 3),
            new LegendConnectUtteranceMeaningNode("two", "choice", "two", 3, 1, 3)
        };
        var relations = new[]
        {
            new LegendConnectUtteranceMeaningRelation("r1", "references", 0, 1, 3),
            new LegendConnectUtteranceMeaningRelation("r2", "describes", 0, 2, 3),
            new LegendConnectUtteranceMeaningRelation("r3", "describes", 0, 3, 3)
        };
        var bindings = new[]
        {
            new LegendConnectDiscourseReferenceBindingSnapshot(
                "bound",
                "ok",
                "choice",
                "alpha",
                "alpha",
                1,
                0,
                true,
                "selector",
                "rule")
        };

        var result = InvokeReplacementPruning(nodes, relations, bindings);

        Assert.True(result.Succeeded);
        Assert.Equal(nodes, result.Nodes);
        Assert.Equal(relations, result.Relations);
    }

    [Fact]
    public void ReplacementPruning_FailsClosedForTamperedPersistedOccurrenceIdentity()
    {
        var nodes = new[]
        {
            new LegendConnectUtteranceMeaningNode("selector", "reference_selector", "ordinal_one", 0, 1, 3),
            new LegendConnectUtteranceMeaningNode("one", "choice", "one", 1, 1, 3)
        };
        var relations = new[]
        {
            new LegendConnectUtteranceMeaningRelation("r1", "references", 0, 1, 3)
        };
        var binding = new LegendConnectDiscourseReferenceBindingSnapshot(
            "bound", "ok", "choice", "alpha", "alpha", 1, 0, true, "selector", "rule")
        {
            SelectorNodeIndex = 0,
            HasSupersededCurrentTurnEntity = true,
            SupersededCurrentTurnNodeIndex = 1,
            SupersededCurrentTurnSemanticSignature = "tampered",
            SupersededCurrentTurnSemanticDimension = "choice",
            SupersededCurrentTurnSemanticValue = "one",
            SupersededCurrentTurnNodeStartTokenIndex = 1,
            SupersededCurrentTurnNodeTokenLength = 1
        };

        Assert.False(InvokeReplacementPruning(nodes, relations, [binding]).Succeeded);
    }

    private static MasterAppDbContext CreateDb(
        string databaseName,
        InMemoryDatabaseRoot root)
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(databaseName, root)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MasterAppDbContext(options);
    }

    private static LegendConnectOperations CreateOperations(MasterAppDbContext db)
    {
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        return new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum);
    }

    private static LegendConnectCurriculumService CreateCurriculum(MasterAppDbContext db)
    {
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        return new LegendConnectCurriculumService(db, registry, corpus);
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:ChatDeployment"] = "test-chat",
                ["AzureOpenAI:Endpoint"] = "https://legend.invalid",
                ["AzureOpenAI:ApiKey"] = "test-key"
            })
            .Build();

    private static AgentProfile Profile(string actor, string prefix) => new()
    {
        Id = Guid.NewGuid(),
        AgentUserId = actor,
        AgentUpn = $"{prefix}-{actor}@legend.test",
        NormalizedEmail = $"{prefix}-{actor}@legend.test",
        IsActive = true
    };

    private static LegendConnectCurriculumBatchSubmission OrdinalBindingFamily(int family) => new(
        $"rg5.ordinal.binding.{family}",
        "Founder-governed ordinal discourse evidence",
        [
            EntityExample(family),
            AlternateEntityExample(family),
            UniqueExample(family),
            OrdinalReferenceExample(family, "first", "ordinal_one", 1, false),
            OrdinalReferenceExample(family, "second", "ordinal_two", 2, false),
            OrdinalReferenceExample(family, "last", "ordinal_last", 3, false)
        ]);

    private static LegendConnectCurriculumBatchSubmission ProductionStyleChoiceFamily(int family) => new(
        $"rg5.production.style.choice.{family}",
        "Founder-governed held-out correction evidence",
        [
            ChoiceEntityExample(
                family,
                "alpha",
                "The alpha choice feels affordable to me.",
                "affordable"),
            ChoiceEntityExample(
                family,
                "beta",
                "The beta choice seems reliable to me.",
                "reliable"),
            CorrectionReferenceExample(family),
            ResponseEvidenceExample(family, "correction_acknowledgement")
        ],
        [
            new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction",
                    ["choice"] = "$subject"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction_acknowledgement"
                }))
        ]);

    private static LegendConnectCurriculumBatchSubmission ProducerIssuedReplacementFamily(int family) => new(
        $"rg5.producer.issued.choice.{family}",
        "Founder-governed replacement occurrence lineage",
        [
            ChoiceEntityExample(
                family,
                "alpha",
                "The alpha choice feels affordable to me.",
                "affordable"),
            ChoiceEntityExample(
                family,
                "beta",
                "The beta choice seems reliable to me.",
                "reliable"),
            ProducerIssuedReplacementExample(family, "Please route the first marker.", "first", "beta"),
            ResponseEvidenceExample(family, "correction_acknowledgement")
        ],
        [
            new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction",
                    ["choice"] = "$subject"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction_acknowledgement"
                }))
        ]);

    private static LegendConnectCurriculumBatchSubmission ProducerIssuedBroadReplacementFamily(int family) => new(
        $"rg5.producer.broad.choice.{family}",
        "Founder-governed broad replacement occurrence lineage",
        [
            ChoiceEntityExample(
                family,
                "alpha",
                "The alpha choice feels affordable to me.",
                "affordable"),
            ChoiceEntityExample(
                family,
                "beta",
                "The beta choice seems reliable to me.",
                "reliable"),
            ProducerIssuedBroadReplacementExample(family),
            ResponseEvidenceExample(family, "correction_acknowledgement")
        ],
        [
            new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction",
                    ["choice"] = "$subject"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction_acknowledgement"
                }))
        ]);

    private static LegendConnectCurriculumBatchSubmission ProducerIssuedDuplicateReplacementFamily(int family) => new(
        $"rg5.producer.duplicate.choice.{family}",
        "Founder-governed duplicate replacement occurrence lineage",
        [
            ChoiceEntityExample(
                family,
                "alpha",
                "The alpha choice feels affordable to me.",
                "affordable"),
            ChoiceEntityExample(
                family,
                "beta",
                "The beta choice seems reliable to me.",
                "reliable"),
            ProducerIssuedDuplicateReplacementExample(family),
            ResponseEvidenceExample(family, "correction_acknowledgement")
        ],
        [
            new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction",
                    ["choice"] = "$subject"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction_acknowledgement"
                }))
        ]);

    private static LegendConnectCurriculumExampleSubmission EntityExample(int family) =>
        new($"Please consider a b c variant {family}.",
            Variations("establish"),
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("first", "choice", "a", "a"),
                new LegendConnectMeaningNodeSubmission("second", "choice", "b", "b"),
                new LegendConnectMeaningNodeSubmission("third", "choice", "c", "c")
            ],
            [
                new LegendConnectMeaningRelationSubmission("first", "ordered-with", "second"),
                new LegendConnectMeaningRelationSubmission("second", "ordered-with", "third")
            ]));

    private static LegendConnectCurriculumExampleSubmission AlternateEntityExample(int family) =>
        new($"Please consider d e f variant {family}.",
            Variations("establish"),
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("first", "choice", "d", "d"),
                new LegendConnectMeaningNodeSubmission("second", "choice", "e", "e"),
                new LegendConnectMeaningNodeSubmission("third", "choice", "f", "f")
            ],
            [
                new LegendConnectMeaningRelationSubmission("first", "ordered-with", "second"),
                new LegendConnectMeaningRelationSubmission("second", "ordered-with", "third")
            ]));

    private static LegendConnectCurriculumExampleSubmission UniqueExample(int family) =>
        new($"Please use that one variant {family}.",
            Variations("reference"),
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("selector", "reference_selector", "unique_choice", "that"),
                new LegendConnectMeaningNodeSubmission("kind", "reference_kind", "choice", "one")
            ],
            [new LegendConnectMeaningRelationSubmission("selector", "reference-target", "kind")],
            [new LegendConnectDiscourseReferenceSubmission(
                "selector", "choice", "unique", null, ["user", "assistant"])]));

    private static LegendConnectCurriculumExampleSubmission OrdinalReferenceExample(
        int family,
        string surface,
        string selectorValue,
        int rank,
        bool replacesActiveBinding) =>
        new($"Founder ordinal reference {family}: the {surface} one.",
            Variations("reference"),
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("selector", "reference_selector", selectorValue, surface),
                new LegendConnectMeaningNodeSubmission("kind", "reference_kind", "choice", "one")
            ],
            [new LegendConnectMeaningRelationSubmission("selector", "reference-target", "kind")],
            [new LegendConnectDiscourseReferenceSubmission(
                "selector",
                "choice",
                "ordinal",
                rank,
                ["user", "assistant"],
                replacesActiveBinding)]));

    private static LegendConnectCurriculumExampleSubmission ResponseEvidenceExample(
        int family,
        string function) =>
        new(
            function == "correction_acknowledgement"
                ? "I understand the correction."
                : $"Founder response evidence {family}: {function}.",
            new Dictionary<string, string>
            {
                ["conversation_function"] = function
            });

    private static LegendConnectCurriculumExampleSubmission ChoiceEntityExample(
        int family,
        string choice,
        string surface,
        string attribute) =>
        new(
            $"Founder choice evidence {family}: {surface}",
            new Dictionary<string, string>
            {
                ["conversation_function"] = "establish_choice",
                ["choice"] = choice
            },
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("entity", "choice", choice, choice),
                new LegendConnectMeaningNodeSubmission("attribute", "choice_attribute", attribute, attribute)
            ],
            [new LegendConnectMeaningRelationSubmission("entity", "described-as", "attribute")]));

    private static LegendConnectCurriculumExampleSubmission CorrectionReferenceExample(int family) =>
        new(
            $"Founder correction reference {family}: Please use the first one instead.",
            new Dictionary<string, string>
            {
                ["conversation_function"] = "correction",
                ["choice"] = "training_choice"
            },
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("function", "conversation_function", "correction", "use"),
                new LegendConnectMeaningNodeSubmission("selector", "reference_selector", "ordinal_one", "first")
            ],
            [new LegendConnectMeaningRelationSubmission("function", "corrects", "selector")],
            [new LegendConnectDiscourseReferenceSubmission(
                "selector",
                "choice",
                "ordinal",
                1,
                ["user", "assistant"],
                true)]));

    private static LegendConnectCurriculumExampleSubmission ProducerIssuedReplacementExample(
        int family,
        string surface,
        string selectorSurface,
        string supersededValue) =>
        new(
            $"Founder producer-issued replacement {family}: {surface}",
            new Dictionary<string, string>
            {
                ["conversation_function"] = "correction",
                ["choice"] = supersededValue
            },
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("function", "conversation_function", "correction", "route"),
                new LegendConnectMeaningNodeSubmission("selector", "reference_selector", "ordinal_one", selectorSurface),
                new LegendConnectMeaningNodeSubmission("kind", "reference_kind", "choice", "marker"),
                new LegendConnectMeaningNodeSubmission("choice", "choice", supersededValue, selectorSurface)
            ],
            [
                new LegendConnectMeaningRelationSubmission("function", "corrects", "selector"),
                new LegendConnectMeaningRelationSubmission("selector", "reference-target", "kind"),
                new LegendConnectMeaningRelationSubmission("function", "mentions", "choice")
            ],
            [new LegendConnectDiscourseReferenceSubmission(
                "selector",
                "choice",
                "ordinal",
                1,
                ["user", "assistant"],
                true)]));

    private static LegendConnectCurriculumExampleSubmission ProducerIssuedBroadReplacementExample(int family) =>
        new(
            $"Founder producer-issued broad replacement {family}: Please route the first marker now.",
            new Dictionary<string, string>
            {
                ["conversation_function"] = "correction",
                ["choice"] = "beta"
            },
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("function", "conversation_function", "correction", "route"),
                new LegendConnectMeaningNodeSubmission("selector", "reference_selector", "ordinal_one", "first"),
                new LegendConnectMeaningNodeSubmission("kind", "reference_kind", "choice", "marker"),
                new LegendConnectMeaningNodeSubmission("choice", "choice", "beta", "first marker")
            ],
            [
                new LegendConnectMeaningRelationSubmission("function", "corrects", "selector"),
                new LegendConnectMeaningRelationSubmission("selector", "reference-target", "kind"),
                new LegendConnectMeaningRelationSubmission("function", "mentions", "choice")
            ],
            [new LegendConnectDiscourseReferenceSubmission(
                "selector",
                "choice",
                "ordinal",
                1,
                ["user", "assistant"],
                true)]));

    private static LegendConnectCurriculumExampleSubmission ProducerIssuedDuplicateReplacementExample(int family) =>
        new(
            $"Founder producer-issued duplicate replacement {family}: Please route the first marker twice.",
            new Dictionary<string, string>
            {
                ["conversation_function"] = "correction",
                ["choice"] = "beta"
            },
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("function", "conversation_function", "correction", "route"),
                new LegendConnectMeaningNodeSubmission("selector", "reference_selector", "ordinal_one", "first"),
                new LegendConnectMeaningNodeSubmission("kind", "reference_kind", "choice", "marker"),
                new LegendConnectMeaningNodeSubmission("choice_beta", "choice", "beta", "first"),
                new LegendConnectMeaningNodeSubmission("choice_gamma", "choice", "gamma", "first")
            ],
            [
                new LegendConnectMeaningRelationSubmission("function", "corrects", "selector"),
                new LegendConnectMeaningRelationSubmission("selector", "reference-target", "kind"),
                new LegendConnectMeaningRelationSubmission("function", "mentions", "choice_beta"),
                new LegendConnectMeaningRelationSubmission("function", "mentions", "choice_gamma")
            ],
            [new LegendConnectDiscourseReferenceSubmission(
                "selector",
                "choice",
                "ordinal",
                1,
                ["user", "assistant"],
                true)]));

    private static IReadOnlyDictionary<string, string> Variations(string function) =>
        new Dictionary<string, string>
        {
            ["conversation_function"] = function,
            ["utterance_kind"] = "discourse"
        };

    private static ReplacementPruningInvocationResult InvokeReplacementPruning(
        IReadOnlyList<LegendConnectUtteranceMeaningNode> nodes,
        IReadOnlyList<LegendConnectUtteranceMeaningRelation> relations,
        IReadOnlyList<LegendConnectDiscourseReferenceBindingSnapshot> bindings)
    {
        var method = typeof(LegendConnectCurriculumService).GetMethod(
            "TryPruneSupersededReplacementEntities",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var arguments = new object?[]
        {
            nodes,
            relations,
            bindings,
            null,
            null,
            null
        };
        var succeeded = Assert.IsType<bool>(method!.Invoke(null, arguments));
        var prunedNodes = arguments[3] is List<LegendConnectUtteranceMeaningNode> nodeList
            ? nodeList
            : [];
        var prunedRelations = arguments[4] is List<LegendConnectUtteranceMeaningRelation> relationList
            ? relationList
            : [];
        var selectorRemap = arguments[5] is Dictionary<int, int> remap
            ? remap
            : [];
        return new ReplacementPruningInvocationResult(
            succeeded,
            prunedNodes,
            prunedRelations,
            selectorRemap);
    }

    private sealed record ReplacementPruningInvocationResult(
        bool Succeeded,
        IReadOnlyList<LegendConnectUtteranceMeaningNode> Nodes,
        IReadOnlyList<LegendConnectUtteranceMeaningRelation> Relations,
        IReadOnlyDictionary<int, int> SelectorRemap);

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            return new HttpClient(new NoNetworkHandler())
            {
                BaseAddress = new Uri("https://legend.invalid/")
            };
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provider HTTP must not be used by native-only discourse inference.");
    }
}
