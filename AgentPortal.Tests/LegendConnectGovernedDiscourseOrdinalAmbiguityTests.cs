using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
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

    [Fact]
    public async Task HeldOutCorrection_ReplacesCompetingOrdinalChoiceWithoutProviderClients()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var actor = Guid.NewGuid().ToString("D");
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", actor);
        db.AgentProfiles.Add(Profile(actor, "heldout"));
        await db.SaveChangesAsync();
        try
        {
            var curriculum = CreateCurriculum(db);
            for (var family = 1; family <= 3; family++)
            {
                var submitted = await curriculum.SubmitFounderBatchAsync(
                    ProductionStyleChoiceFamily(family));
                Assert.True(submitted.Succeeded, submitted.Message);
                submitted = await curriculum.SubmitFounderBatchAsync(
                    ActiveUniqueBindingFamily(family));
                Assert.True(submitted.Succeeded, submitted.Message);
            }

            Assert.DoesNotContain(
                await db.LegendLanguageTextUnits.Select(item => item.Text).ToListAsync(),
                text => string.Equals(text, "No, I meant the first option.", StringComparison.Ordinal));

            var operations = CreateOperations(db);
            var profiles = new AgentProfileAccessResolver(db);
            var discourse = new LegendFounderAiDiscourseStateService(db, profiles, operations);
            var founder = ControllerTestHelpers.BuildUser(actor);
            var priorMessages = new[]
            {
                new LegendFounderAiChatMessage("user", "The alpha choice feels affordable to me."),
                new LegendFounderAiChatMessage("user", "The beta choice seems reliable to me."),
                new LegendFounderAiChatMessage("user", "the latest one")
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
                "No, I meant the first option.");
            Assert.True(currentGraph.IsComposed, currentGraph.ReasonCode);
            await discourse.RecordObservationAsync(
                founder,
                directConversationId.ToString(),
                "user",
                currentGraph);

            var directState = Assert.IsType<LegendConnectDiscourseStateSnapshot>(
                await discourse.GetStateAsync(founder, directConversationId.ToString()));
            var directPlan = await operations.TryPlanConversationAsync(
                "No, I meant the first option.",
                directState);
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
            Assert.Null(directBinding.SupersededCurrentTurnSemanticSignature);
            Assert.Null(directBinding.SupersededCurrentTurnSemanticDimension);
            Assert.Null(directBinding.SupersededCurrentTurnSemanticValue);
            Assert.Null(directBinding.SupersededCurrentTurnNodeStartTokenIndex);
            Assert.Null(directBinding.SupersededCurrentTurnNodeTokenLength);

            var directNative = await operations.TryInferConversationWithDiscourseAsync(
                "No, I meant the first option.",
                priorMessages.Select(message => new LegendConnectConversationContextItem(
                        message.Role ?? string.Empty,
                        message.Content ?? string.Empty))
                    .ToArray(),
                directState);
            Assert.True(directNative.Supported, directNative.ReasonCode);
            Assert.Equal("I understand the correction.", directNative.Answer);

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
                    ConversationId = replyConversationId.ToString(),
                    Messages =
                    [
                        .. priorMessages,
                        new LegendFounderAiChatMessage("user", "No, I meant the first option.")
                    ]
                });

            Assert.True(
                reply.Succeeded,
                $"stage={reply.Stage}; reason={reply.Reason}; error={reply.Error}; message={reply.Message}");
            Assert.Equal("I understand the correction.", reply.Message);
            Assert.Equal("LegendAi", reply.ResponseAuthority);
            Assert.Equal(0, countingFactory.CreateClientCalls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    [Fact]
    public void PersistedExactOccurrence_RemainsBoundAfterOnlyItsIncidentRelationsAreRemoved()
    {
        var sourceTurnId = Guid.NewGuid();
        var containingTurnId = Guid.NewGuid();
        var sourceNodes = new[]
        {
            new LegendConnectUtteranceMeaningNode("alpha", "choice", "alpha", 0, 1, 3),
            new LegendConnectUtteranceMeaningNode("beta", "choice", "beta", 1, 1, 3),
            new LegendConnectUtteranceMeaningNode("note", "choice_note", "stable", 2, 1, 3)
        };
        var sourceRelationsAfterRemoval = new[]
        {
            new LegendConnectUtteranceMeaningRelation("alpha-note", "described-as", 0, 2, 3)
        };
        Assert.DoesNotContain(sourceRelationsAfterRemoval, relation =>
            relation.SourceNodeIndex == 1 || relation.TargetNodeIndex == 1);

        var sourceTurn = new LegendFounderAiDiscourseTurn
        {
            Id = sourceTurnId,
            SequenceNumber = 1,
            Role = "user",
            MeaningGraphJson = JsonSerializer.Serialize(new
            {
                IsComposed = true,
                Nodes = sourceNodes,
                Relations = sourceRelationsAfterRemoval,
                ReasonCode = "composed"
            })
        };
        var binding = new LegendFounderAiDiscourseReferenceBinding(
            "bound",
            "governed_reference_resolved",
            "selector",
            "choice",
            "alpha",
            "alpha",
            sourceTurnId,
            1,
            0,
            true,
            "rule",
            sourceTurnId,
            1,
            1,
            1,
            1,
            "en",
            "ordinal",
            1,
            "user",
            "beta",
            "choice",
            "beta",
            containingTurnId,
            2,
            0,
            0,
            1);
        var containingGraph = new LegendConnectUtteranceMeaningGraphSnapshot(
            true,
            [new LegendConnectUtteranceMeaningNode("selector", "reference_selector", "ordinal_one", 0, 1, 3)],
            [],
            [],
            "composed");
        var containingTurn = new LegendFounderAiDiscourseTurn
        {
            Id = containingTurnId,
            SequenceNumber = 2,
            Role = "user",
            MeaningGraphJson = JsonSerializer.Serialize(new
            {
                containingGraph.IsComposed,
                containingGraph.Nodes,
                containingGraph.Relations,
                containingGraph.ReasonCode
            }),
            ResolvedBindingsJson = JsonSerializer.Serialize(new[] { binding })
        };
        var method = typeof(LegendFounderAiDiscourseStateService).GetMethod(
            "DeserializeAndValidateBindings",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            [typeof(LegendFounderAiDiscourseTurn), typeof(IReadOnlyList<LegendFounderAiDiscourseTurn>)],
            null);
        Assert.NotNull(method);

        var validated = Assert.IsAssignableFrom<
            IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>>(
            method!.Invoke(null, [containingTurn, new[] { sourceTurn, containingTurn }]));
        var validatedBinding = Assert.Single(validated);
        Assert.Equal("bound", validatedBinding.ResolutionState);
        Assert.Equal(sourceTurnId, validatedBinding.SupersededTurnId);
        Assert.Equal(1, validatedBinding.SupersededTurnSequence);
        Assert.Equal(1, validatedBinding.SupersededNodeIndex);
        Assert.Equal("beta", validatedBinding.SupersededEntitySemanticSignature);
        Assert.Equal("choice", validatedBinding.SupersededEntitySemanticDimension);
        Assert.Equal("beta", validatedBinding.SupersededEntitySemanticValue);
        Assert.Equal(1, validatedBinding.SupersededNodeStartTokenIndex);
        Assert.Equal(1, validatedBinding.SupersededNodeTokenLength);
    }

    [Fact]
    public void CurrentTurnExactOccurrence_RemainsBoundWhenDetachedFromEveryRelation()
    {
        var turnId = Guid.NewGuid();
        var graph = new LegendConnectUtteranceMeaningGraphSnapshot(
            true,
            [
                new LegendConnectUtteranceMeaningNode("selector", "reference_selector", "ordinal_one", 0, 1, 3),
                new LegendConnectUtteranceMeaningNode("old", "choice", "old", 1, 1, 3),
                new LegendConnectUtteranceMeaningNode("context", "context", "stable", 2, 1, 3),
                new LegendConnectUtteranceMeaningNode("detail", "detail", "stable", 3, 1, 3)
            ],
            [new LegendConnectUtteranceMeaningRelation("context-detail", "describes", 2, 3, 3)],
            [],
            "composed");
        Assert.DoesNotContain(graph.Relations, relation =>
            relation.SourceNodeIndex == 1 || relation.TargetNodeIndex == 1);
        var binding = CurrentTurnReplacementBinding(turnId);

        var validated = InvokeCurrentTurnBindingValidation(turnId, graph, [binding]);

        Assert.Equal("bound", Assert.Single(validated).ResolutionState);
    }

    [Fact]
    public void CurrentTurnExactOccurrence_DuplicateOrConflictingBindingsFailClosedWithoutOrderInference()
    {
        var turnId = Guid.NewGuid();
        var graph = new LegendConnectUtteranceMeaningGraphSnapshot(
            true,
            [
                new LegendConnectUtteranceMeaningNode("selector", "reference_selector", "ordinal_one", 0, 1, 3),
                new LegendConnectUtteranceMeaningNode("old", "choice", "old", 1, 1, 3)
            ],
            [new LegendConnectUtteranceMeaningRelation("selector-old", "references", 0, 1, 3)],
            [],
            "composed");
        var binding = CurrentTurnReplacementBinding(turnId);
        var conflicting = binding with
        {
            SupersededCurrentTurnNodeIndex = 0,
            SupersededCurrentTurnSemanticSignature = "selector",
            SupersededCurrentTurnSemanticDimension = "choice",
            SupersededCurrentTurnSemanticValue = "old",
            SupersededCurrentTurnNodeStartTokenIndex = 0
        };

        foreach (var order in new[]
                 {
                     new[] { binding, binding },
                     new[] { binding, conflicting },
                     new[] { conflicting, binding }
                 })
        {
            var validated = InvokeCurrentTurnBindingValidation(turnId, graph, order);
            Assert.Equal(2, validated.Count);
            Assert.All(validated, item =>
            {
                Assert.Equal("unresolved", item.ResolutionState);
                Assert.Equal("reference_replacement_occurrence_invalid", item.ReasonCode);
            });
        }
    }

    [Fact]
    public void ReplacementPruning_RemovesOnlySelectorLocalSupersededOccurrence_AndPreservesOtherRelations()
    {
        var nodes = new[]
        {
            new LegendConnectUtteranceMeaningNode("compare", "conversation_function", "compare", 0, 1, 3),
            new LegendConnectUtteranceMeaningNode("selector", "reference_selector", "ordinal_one", 1, 1, 3),
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
            new LegendConnectUtteranceMeaningNode("selector_one", "reference_selector", "ordinal_one", 1, 1, 3),
            new LegendConnectUtteranceMeaningNode("one", "choice", "one", 2, 1, 3),
            new LegendConnectUtteranceMeaningNode("compare_two", "conversation_function", "compare_two", 3, 1, 3),
            new LegendConnectUtteranceMeaningNode("selector_two", "reference_selector", "ordinal_two", 4, 1, 3),
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
            new LegendConnectDiscourseReferenceBindingSnapshot("bound", "ok", "choice", "alpha", "alpha", 1, 0, true, "selector_one", "rule_one"),
            new LegendConnectDiscourseReferenceBindingSnapshot("bound", "ok", "choice", "beta", "beta", 1, 1, true, "selector_two", "rule_two")
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

    private static LegendConnectCurriculumBatchSubmission ActiveUniqueBindingFamily(int family) => new(
        $"rg5.active.unique.binding.{family}",
        "Founder-governed active binding evidence",
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
            ActiveRecentReferenceExample(family)
        ]);

    private static LegendConnectCurriculumExampleSubmission ActiveRecentReferenceExample(int family) =>
        new(
            $"Founder active reference {family}: the latest one.",
            Variations("reference"),
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission(
                    "selector",
                    "reference_selector",
                    "recent_choice",
                    "latest"),
                new LegendConnectMeaningNodeSubmission("kind", "reference_kind", "choice", "one")
            ],
            [new LegendConnectMeaningRelationSubmission("selector", "reference-target", "kind")],
            [new LegendConnectDiscourseReferenceSubmission(
                "selector",
                "choice",
                "recent",
                null,
                ["user", "assistant"])]));

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

    private static LegendFounderAiDiscourseReferenceBinding CurrentTurnReplacementBinding(Guid turnId) =>
        new(
            "bound",
            "governed_reference_resolved",
            "selector",
            "choice",
            "new",
            "new",
            Guid.NewGuid(),
            1,
            0,
            true,
            "rule",
            Guid.NewGuid(),
            1,
            0,
            0,
            1,
            "en",
            "ordinal",
            1,
            "user",
            "old",
            "choice",
            "old",
            turnId,
            2,
            0,
            0,
            1,
            true,
            1,
            "old",
            "choice",
            "old",
            1,
            1);

    private static IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>
        InvokeCurrentTurnBindingValidation(
            Guid turnId,
            LegendConnectUtteranceMeaningGraphSnapshot graph,
            IReadOnlyList<LegendFounderAiDiscourseReferenceBinding> bindings)
    {
        var method = typeof(LegendFounderAiDiscourseStateService).GetMethod(
            "DeserializeBindings",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            [typeof(LegendFounderAiDiscourseTurn), typeof(LegendConnectUtteranceMeaningGraphSnapshot)],
            null);
        Assert.NotNull(method);
        var turn = new LegendFounderAiDiscourseTurn
        {
            Id = turnId,
            SequenceNumber = 2,
            Role = "user",
            ResolvedBindingsJson = JsonSerializer.Serialize(bindings)
        };
        return Assert.IsAssignableFrom<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>>(
            method!.Invoke(null, [turn, graph]));
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
