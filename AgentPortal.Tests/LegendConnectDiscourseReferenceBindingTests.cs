using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Proof that cross-turn reference binding is derived from mature,
/// Founder-declared semantic rules and persisted graph identities only. The
/// SQL-gated durability proof intentionally recreates its DbContext for every
/// observed turn.
/// </summary>
public sealed class LegendConnectDiscourseReferenceBindingTests
{
    [Fact]
    public async Task UniquePronounsAndThat_CompleteTheCurrentMeaningGraphBeforeSelection()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();

        var pronounConversation = Guid.NewGuid();
        await fixture.ObserveAsync(fixture.FirstActor, pronounConversation, "user", "Alpha explanation.");
        await fixture.ObserveAsync(fixture.FirstActor, pronounConversation, "user", "Explain it.");
        var pronounPlan = await fixture.PlanAsync(
            fixture.FirstActor,
            pronounConversation,
            "Explain it.");
        Assert.True(pronounPlan.Supported, pronounPlan.ReasonCode);
        AssertCompletedReference(pronounPlan, "alpha");

        var demonstrativeConversation = Guid.NewGuid();
        await fixture.ObserveAsync(fixture.FirstActor, demonstrativeConversation, "user", "Beta explanation.");
        await fixture.ObserveAsync(
            fixture.FirstActor,
            demonstrativeConversation,
            "user",
            "Tell me more about that.");
        var demonstrativePlan = await fixture.PlanAsync(
            fixture.FirstActor,
            demonstrativeConversation,
            "Tell me more about that.");
        Assert.True(demonstrativePlan.Supported, demonstrativePlan.ReasonCode);
        AssertCompletedReference(demonstrativePlan, "beta");
    }

    [Fact]
    public async Task ThoseTwoExplanations_ResolveOneGovernedPairIdentity()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();
        var conversationId = Guid.NewGuid();

        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Alpha and beta explanations.");
        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Compare the second one.");
        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Contrast those two explanations.");
        var planned = await fixture.PlanAsync(
            fixture.FirstActor,
            conversationId,
            "Contrast those two explanations.");

        Assert.True(planned.Supported, planned.ReasonCode);
        AssertCompletedReference(planned, "alpha_beta", "explanation_pair");
    }

    [Fact]
    public async Task AmbiguousUniqueReference_FailsClosedBeforeTransitionSelection()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();
        var conversationId = Guid.NewGuid();
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "user", "Alpha explanation.");
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "user", "Beta explanation.");
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "user", "Explain it.");

        var binding = Assert.Single(await fixture.LatestBindingsAsync(
            fixture.FirstActor,
            conversationId));
        Assert.Equal("unresolved", binding.ResolutionState);
        Assert.Equal("reference_candidate_ambiguous", binding.ReasonCode);

        var planned = await fixture.PlanAsync(
            fixture.FirstActor,
            conversationId,
            "Explain it.");
        Assert.False(planned.Supported);
        Assert.Equal("discourse_reference_unresolved", planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Fact]
    public async Task OrdinalReference_UsesTheMostRecentGovernedEntitySet()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();
        var conversationId = Guid.NewGuid();
        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Alpha and beta explanations.");
        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Compare the second one.");

        var planned = await fixture.PlanAsync(
            fixture.FirstActor,
            conversationId,
            "Compare the second one.");

        Assert.True(planned.Supported, planned.ReasonCode);
        AssertCompletedReference(planned, "beta");
    }

    [Fact]
    public async Task RoleRestrictedReference_UsesOnlyTheGovernedAllowedSourceRole()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();
        var conversationId = Guid.NewGuid();
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "user", "Alpha explanation.");
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "assistant", "Beta explanation.");
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "user", "Review her proposal.");

        var planned = await fixture.PlanAsync(
            fixture.FirstActor,
            conversationId,
            "Review her proposal.");

        Assert.True(planned.Supported, planned.ReasonCode);
        AssertCompletedReference(planned, "beta");
        var binding = Assert.Single(planned.Plan!.ResolvedDiscourseBindings);
        var sourceTurn = Assert.Single((await fixture.StateAsync(
                fixture.FirstActor,
                conversationId)).Turns
            .Where(item => item.SequenceNumber == binding.EntityTurnSequence));
        Assert.Equal("assistant", sourceTurn.Role);
    }

    [Fact]
    public async Task RecentReference_SelectsOnlyTheUniqueMostRecentGovernedEntity()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();
        var conversationId = Guid.NewGuid();
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "user", "Alpha explanation.");
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "user", "Beta explanation.");
        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Revisit the latest explanation.");

        var planned = await fixture.PlanAsync(
            fixture.FirstActor,
            conversationId,
            "Revisit the latest explanation.");

        Assert.True(planned.Supported, planned.ReasonCode);
        AssertCompletedReference(planned, "beta");
    }

    [Fact]
    public async Task GovernedCorrection_ReplacesTheActiveResolvedSemanticIdentity()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();
        var conversationId = Guid.NewGuid();
        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Alpha and beta explanations.");
        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Compare the second one.");
        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "No use the first one.");

        var planned = await fixture.PlanAsync(
            fixture.FirstActor,
            conversationId,
            "No use the first one.");
        Assert.True(planned.Supported, planned.ReasonCode);
        AssertCompletedReference(planned, "alpha");
        var replacement = Assert.Single(planned.Plan!.ResolvedDiscourseBindings);
        Assert.True(replacement.ReplacesActiveBinding);
        Assert.NotNull(replacement.SupersededTurnId);
        var replacementTurn = Assert.Single((await fixture.StateAsync(
                fixture.FirstActor,
                conversationId)).Turns
            .Where(item => item.SequenceNumber == replacement.SupersededTurnSequence));
        var supersededNodeIndex = Assert.IsType<int>(replacement.SupersededNodeIndex);
        var supersededNode = replacementTurn.Nodes[supersededNodeIndex];
        Assert.Equal("beta", supersededNode.SemanticValue);
        Assert.Equal(replacement.SupersededEntitySemanticSignature, supersededNode.SemanticSignature);
        Assert.Equal(replacement.SupersededNodeStartTokenIndex, supersededNode.StartTokenIndex);
        Assert.Equal(replacement.SupersededNodeTokenLength, supersededNode.TokenLength);
        var selectorTurn = Assert.Single((await fixture.StateAsync(
                fixture.FirstActor,
                conversationId)).Turns
            .Where(item => item.SequenceNumber == replacement.SelectorTurnSequence));
        var selectorNodeIndex = Assert.IsType<int>(replacement.SelectorNodeIndex);
        Assert.Equal(
            replacement.SelectorSemanticSignature,
            selectorTurn.Nodes[selectorNodeIndex].SemanticSignature);

        var active = Assert.Single(await fixture.ActiveBindingsAsync(
            fixture.FirstActor,
            conversationId));
        Assert.Equal("alpha", active.EntitySemanticValue);
        Assert.True(active.ReplacesActiveBinding);

        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Explain it.");
        AssertCompletedReference(
            await fixture.PlanAsync(
                fixture.FirstActor,
                conversationId,
                "Explain it."),
            "alpha");
    }

    [Fact]
    public async Task ReplacementOccurrenceIdentity_PersistsDistinctSelectorOccurrencesWithoutChangingRelations()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();
        var conversationId = Guid.NewGuid();
        var permutedConversationId = Guid.NewGuid();
        var unfamiliarEntities = new LegendConnectUtteranceMeaningGraphSnapshot(
            true,
            [
                new LegendConnectUtteranceMeaningNode(
                    "mineral-cobalt", "explanation", "cobalt", 0, 1, 3),
                new LegendConnectUtteranceMeaningNode(
                    "mineral-saffron", "explanation", "saffron", 2, 1, 3)
            ],
            [new LegendConnectUtteranceMeaningRelation(
                "mineral-sequence", "precedes", 0, 1, 3)],
            [],
            "meaning_graph_observational_composed");
        await fixture.ObserveGraphAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            unfamiliarEntities);
        await fixture.ObserveGraphAsync(
            fixture.FirstActor,
            permutedConversationId,
            "user",
            unfamiliarEntities);
        await fixture.ObserveAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            "Compare the second one.");
        await fixture.ObserveAsync(
            fixture.FirstActor,
            permutedConversationId,
            "user",
            "Compare the second one.");

        var graph = await fixture.AnalyzeAsync("No use the first one.");
        var selector = Assert.Single(graph.Nodes.Where(item =>
            item.SemanticDimension == "reference_selector"));
        var duplicateSelector = selector with
        {
            StartTokenIndex = selector.StartTokenIndex + selector.TokenLength + 1
        };
        var repeatedSelectorGraph = graph with
        {
            Nodes = graph.Nodes.Concat([duplicateSelector]).ToArray()
        };
        await fixture.ObserveGraphAsync(
            fixture.FirstActor,
            conversationId,
            "user",
            repeatedSelectorGraph);
        var permutation = Enumerable.Range(0, repeatedSelectorGraph.Nodes.Count)
            .Reverse()
            .ToArray();
        var remap = permutation
            .Select((originalIndex, newIndex) => new { originalIndex, newIndex })
            .ToDictionary(item => item.originalIndex, item => item.newIndex);
        var permutedGraph = repeatedSelectorGraph with
        {
            Nodes = permutation.Select(index => repeatedSelectorGraph.Nodes[index]).ToArray(),
            Relations = repeatedSelectorGraph.Relations.Select(item => item with
            {
                SourceNodeIndex = remap[item.SourceNodeIndex],
                TargetNodeIndex = remap[item.TargetNodeIndex]
            }).ToArray()
        };
        await fixture.ObserveGraphAsync(
            fixture.FirstActor,
            permutedConversationId,
            "user",
            permutedGraph);

        var projected = Assert.Single((await fixture.StateAsync(
            fixture.FirstActor,
            conversationId)).Turns.Where(item => item.SequenceNumber == 3));
        var replacements = projected.Bindings
            .Where(item => item.ResolutionState == "bound" && item.ReplacesActiveBinding)
            .OrderBy(item => item.SelectorNodeIndex)
            .ToArray();
        Assert.Equal(2, replacements.Length);
        Assert.Equal(
            replacements.Select(item => item.SelectorNodeIndex).Distinct().Count(),
            replacements.Length);
        Assert.Equal(repeatedSelectorGraph.Nodes, projected.Nodes);
        Assert.Equal(repeatedSelectorGraph.Relations, projected.Relations);
        Assert.All(replacements, binding =>
        {
            Assert.Equal(projected.SequenceNumber, binding.SelectorTurnSequence);
            var nodeIndex = Assert.IsType<int>(binding.SelectorNodeIndex);
            Assert.Equal(binding.SelectorSemanticSignature, projected.Nodes[nodeIndex].SemanticSignature);
            Assert.Equal(binding.SelectorNodeStartTokenIndex, projected.Nodes[nodeIndex].StartTokenIndex);
            Assert.Equal(binding.SelectorNodeTokenLength, projected.Nodes[nodeIndex].TokenLength);
        });
        var permutedProjection = Assert.Single((await fixture.StateAsync(
            fixture.FirstActor,
            permutedConversationId)).Turns.Where(item => item.SequenceNumber == 3));
        Assert.Equal(
            replacements.Select(item => (
                    item.SelectorSemanticSignature,
                    item.SelectorNodeStartTokenIndex,
                    item.SelectorNodeTokenLength)),
            permutedProjection.Bindings
                .Where(item => item.ResolutionState == "bound" && item.ReplacesActiveBinding)
                .OrderBy(item => item.SelectorNodeStartTokenIndex)
                .Select(item => (
                    item.SelectorSemanticSignature,
                    item.SelectorNodeStartTokenIndex,
                    item.SelectorNodeTokenLength)));
        Assert.All(permutedProjection.Bindings, binding =>
        {
            Assert.NotNull(binding.SelectorTurnId);
            var nodeIndex = Assert.IsType<int>(binding.SelectorNodeIndex);
            Assert.Equal(binding.SelectorSemanticSignature, permutedProjection.Nodes[nodeIndex].SemanticSignature);
        });

        var persistedTurn = await fixture.Db.LegendFounderAiDiscourseTurns
            .Where(item => item.SequenceNumber == 3)
            .Join(
                fixture.Db.LegendFounderAiDiscourseConversations
                    .Where(item => item.ConversationId == conversationId),
                turn => turn.DiscourseConversationId,
                conversation => conversation.Id,
                (turn, _) => turn)
            .SingleAsync();
        var persistedBindings = JsonSerializer.Deserialize<
            LegendFounderAiDiscourseReferenceBinding[]>(persistedTurn.ResolvedBindingsJson)!;
        var conflicting = persistedBindings[1] with
        {
            EntitySemanticSignature = unfamiliarEntities.Nodes[1].SemanticSignature,
            EntitySemanticValue = unfamiliarEntities.Nodes[1].SemanticValue,
            EntityNodeIndex = 1
        };
        foreach (var order in new[]
                 {
                     new[] { persistedBindings[0], conflicting },
                     new[] { conflicting, persistedBindings[0] }
                 })
        {
            persistedTurn.ResolvedBindingsJson = JsonSerializer.Serialize(order);
            await fixture.Db.SaveChangesAsync();
            Assert.Empty(await fixture.ActiveBindingsAsync(
                fixture.FirstActor,
                conversationId));
        }
    }

    [Fact]
    public async Task ReplacementOccurrenceIdentity_MissingStaleOrConflictingStateFailsClosed()
    {
        foreach (var mutation in new[]
                 {
                     "missing", "stale", "conflicting", "flag", "antecedent", "dimension",
                     "rule", "language", "mode", "roles", "current"
                 })
        {
            await using var fixture = await CreateInMemoryFixtureAsync();
            var conversationId = Guid.NewGuid();
            await fixture.ObserveAsync(
                fixture.FirstActor,
                conversationId,
                "user",
                "Alpha and beta explanations.");
            await fixture.ObserveAsync(
                fixture.FirstActor,
                conversationId,
                "user",
                "Compare the second one.");
            await fixture.ObserveAsync(
                fixture.FirstActor,
                conversationId,
                "user",
                "No use the first one.");

            var turn = await fixture.Db.LegendFounderAiDiscourseTurns
                .OrderByDescending(item => item.SequenceNumber)
                .FirstAsync();
            var binding = Assert.Single(JsonSerializer.Deserialize<
                LegendFounderAiDiscourseReferenceBinding[]>(turn.ResolvedBindingsJson)!);
            var alternateRuleSignature = await fixture.Db.LegendLanguageDiscourseReferenceRules
                .Where(item => item.RuleSignature != binding.ReferenceRuleSignature)
                .Select(item => item.RuleSignature)
                .FirstAsync();
            var invalid = mutation switch
            {
                "missing" => binding with { SupersededNodeIndex = null },
                "stale" => binding with
                {
                    SupersededTurnSequence = binding.SupersededTurnSequence + 1
                },
                "flag" => binding with { ReplacesActiveBinding = false },
                "antecedent" => binding with { EntityNodeIndex = int.MaxValue },
                "dimension" => binding with { EntitySemanticDimension = null! },
                "rule" => binding with { ReferenceRuleSignature = alternateRuleSignature },
                "language" => binding with { RuleLanguageCode = "zz" },
                "mode" => binding with { RuleResolutionMode = "recent" },
                "roles" => binding with { RuleAllowedSourceRoles = "assistant" },
                "current" => binding with
                {
                    HasSupersededCurrentTurnEntity = true,
                    SupersededCurrentTurnNodeIndex = binding.SelectorNodeIndex,
                    SupersededCurrentTurnSemanticSignature = binding.SelectorSemanticSignature,
                    SupersededCurrentTurnSemanticDimension = binding.EntitySemanticDimension,
                    SupersededCurrentTurnSemanticValue = binding.EntitySemanticValue,
                    SupersededCurrentTurnNodeStartTokenIndex = binding.SelectorNodeStartTokenIndex,
                    SupersededCurrentTurnNodeTokenLength = binding.SelectorNodeTokenLength
                },
                _ => binding
            };
            IReadOnlyList<LegendFounderAiDiscourseReferenceBinding> invalidBindings =
                mutation == "conflicting" ? [invalid, invalid] : [invalid];
            turn.ResolvedBindingsJson = JsonSerializer.Serialize(invalidBindings);
            await fixture.Db.SaveChangesAsync();

            var reloaded = await fixture.LatestBindingsAsync(
                fixture.FirstActor,
                conversationId);
            Assert.NotEmpty(reloaded);
            Assert.All(reloaded, item =>
            {
                Assert.Equal("unresolved", item.ResolutionState);
                Assert.Equal(
                    mutation is "antecedent" or "dimension"
                        ? "reference_antecedent_identity_invalid"
                        : mutation is "rule" or "language" or "mode" or "roles"
                            ? "reference_rule_provenance_invalid"
                        : "reference_replacement_occurrence_invalid",
                    item.ReasonCode);
            });
            Assert.Empty(await fixture.ActiveBindingsAsync(
                fixture.FirstActor,
                conversationId));
        }

        await using var malformedFixture = await CreateInMemoryFixtureAsync();
        var malformedConversationId = Guid.NewGuid();
        await malformedFixture.ObserveAsync(
            malformedFixture.FirstActor,
            malformedConversationId,
            "user",
            "Alpha and beta explanations.");
        await malformedFixture.ObserveAsync(
            malformedFixture.FirstActor,
            malformedConversationId,
            "user",
            "Compare the second one.");
        await malformedFixture.ObserveAsync(
            malformedFixture.FirstActor,
            malformedConversationId,
            "user",
            "No use the first one.");
        var malformedTurn = await malformedFixture.Db.LegendFounderAiDiscourseTurns
            .OrderByDescending(item => item.SequenceNumber)
            .FirstAsync();
        malformedTurn.ResolvedBindingsJson = "[null]";
        await malformedFixture.Db.SaveChangesAsync();
        var malformed = Assert.Single(await malformedFixture.LatestBindingsAsync(
            malformedFixture.FirstActor,
            malformedConversationId));
        Assert.Equal("unresolved", malformed.ResolutionState);
        Assert.Equal("reference_binding_state_invalid", malformed.ReasonCode);
        Assert.Empty(await malformedFixture.ActiveBindingsAsync(
            malformedFixture.FirstActor,
            malformedConversationId));
    }

    [Fact]
    public async Task ReferenceBinding_IsIsolatedAcrossFounderActors()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();
        var conversationId = Guid.NewGuid();
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "user", "Alpha explanation.");
        await fixture.ObserveAsync(fixture.SecondActor, conversationId, "user", "Beta explanation.");
        await fixture.ObserveAsync(fixture.FirstActor, conversationId, "user", "Explain it.");
        await fixture.ObserveAsync(fixture.SecondActor, conversationId, "user", "Explain it.");

        AssertCompletedReference(
            await fixture.PlanAsync(fixture.FirstActor, conversationId, "Explain it."),
            "alpha");
        AssertCompletedReference(
            await fixture.PlanAsync(fixture.SecondActor, conversationId, "Explain it."),
            "beta");
    }

    [Fact]
    public async Task ReferenceBinding_IsIsolatedAcrossConversations()
    {
        await using var fixture = await CreateInMemoryFixtureAsync();
        var firstConversation = Guid.NewGuid();
        var secondConversation = Guid.NewGuid();
        await fixture.ObserveAsync(fixture.FirstActor, firstConversation, "user", "Alpha explanation.");
        await fixture.ObserveAsync(fixture.FirstActor, secondConversation, "user", "Beta explanation.");
        await fixture.ObserveAsync(fixture.FirstActor, firstConversation, "user", "Explain it.");
        await fixture.ObserveAsync(fixture.FirstActor, secondConversation, "user", "Explain it.");

        AssertCompletedReference(
            await fixture.PlanAsync(fixture.FirstActor, firstConversation, "Explain it."),
            "alpha");
        AssertCompletedReference(
            await fixture.PlanAsync(fixture.FirstActor, secondConversation, "Explain it."),
            "beta");
    }

    [Fact]
    public async Task GovernedReferenceBinding_UsesOnlyScopedPersistedSemanticIdentities()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_STAGE3_REFERENCE_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "LEGEND_STAGE3_REFERENCE_SQL_CONNECTION is required for the discourse-reference SQL proof.");

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using (var migration = new MasterAppDbContext(options))
            await migration.Database.MigrateAsync();

        var firstActor = Guid.NewGuid().ToString("D");
        var secondActor = Guid.NewGuid().ToString("D");
        await using (var setup = new MasterAppDbContext(options))
        {
            setup.AgentProfiles.AddRange(
                Profile(firstActor, "first"),
                Profile(secondActor, "second"));
            await setup.SaveChangesAsync();
            var curriculum = CreateCurriculum(setup);
            for (var family = 1; family <= 3; family++)
            {
                var result = await curriculum.SubmitFounderBatchAsync(ReferenceFamily(family));
                Assert.True(result.Succeeded, result.Message);
            }

            var rules = await setup.LegendLanguageDiscourseReferenceRules
                .OrderBy(item => item.ResolutionMode)
                .ThenBy(item => item.SelectionRank)
                .ToListAsync();
            Assert.Equal(3, rules.Count);
            Assert.All(rules, rule =>
            {
                Assert.Equal("Supported", rule.MaturityState);
                Assert.True(rule.IsProductionEligible);
                Assert.Equal(3, rule.IndependentSourceCount);
                Assert.Equal(0, rule.ContradictionCount);
            });
        }

        async Task ObserveAsync(string actor, Guid conversationId, string role, string surface)
        {
            await using var db = new MasterAppDbContext(options);
            var operations = CreateOperations(db);
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(surface);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            var state = new LegendFounderAiDiscourseStateService(
                db,
                new AgentProfileAccessResolver(db),
                operations);
            await state.RecordObservationAsync(
                ControllerTestHelpers.BuildUser(actor),
                conversationId.ToString(),
                role,
                graph);
        }

        async Task<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> LatestAsync(string actor, Guid conversationId)
        {
            await using var db = new MasterAppDbContext(options);
            return await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    CreateOperations(db))
                .GetLatestBindingsAsync(actor, conversationId);
        }

        async Task<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> ActiveAsync(string actor, Guid conversationId)
        {
            await using var db = new MasterAppDbContext(options);
            return await new LegendFounderAiDiscourseStateService(
                    db,
                    new AgentProfileAccessResolver(db),
                    CreateOperations(db))
                .GetActiveBindingsAsync(actor, conversationId);
        }

        async Task<LegendConnectDiscourseStateSnapshot> StateAsync(string actor, Guid conversationId)
        {
            await using var db = new MasterAppDbContext(options);
            return Assert.IsType<LegendConnectDiscourseStateSnapshot>(
                await new LegendFounderAiDiscourseStateService(
                        db,
                        new AgentProfileAccessResolver(db),
                        CreateOperations(db))
                    .GetStateAsync(
                        ControllerTestHelpers.BuildUser(actor),
                        conversationId.ToString()));
        }

        // 1. A/B/C -> “the second one” resolves B.
        var ordinalConversation = Guid.NewGuid();
        await ObserveAsync(firstActor, ordinalConversation, "user", "a b c");
        await ObserveAsync(firstActor, ordinalConversation, "user", "the second one");
        var second = Assert.Single(await LatestAsync(firstActor, ordinalConversation));
        Assert.Equal("bound", second.ResolutionState);
        Assert.Equal("b", second.EntitySemanticValue);

        // 2. The governed correction selector replaces the active B binding with A.
        await ObserveAsync(firstActor, ordinalConversation, "user", "no i meant the first one");
        var correction = Assert.Single(await LatestAsync(firstActor, ordinalConversation));
        Assert.Equal("bound", correction.ResolutionState);
        Assert.Equal("a", correction.EntitySemanticValue);
        Assert.True(correction.ReplacesActiveBinding);
        Assert.Equal(3, correction.SupersededTurnSequence);
        Assert.NotNull(correction.SupersededNodeIndex);
        Assert.NotNull(correction.SupersededNodeStartTokenIndex);
        Assert.NotNull(correction.SupersededNodeTokenLength);
        var correctionState = await StateAsync(firstActor, ordinalConversation);
        var projectedCorrection = Assert.Single(correctionState.Turns
            .Single(item => item.SequenceNumber == correction.SelectorTurnSequence)
            .Bindings);
        Assert.Equal(correction.SupersededTurnId, projectedCorrection.SupersededTurnId);
        Assert.Equal(correction.SupersededTurnSequence, projectedCorrection.SupersededTurnSequence);
        Assert.Equal(correction.SupersededNodeIndex, projectedCorrection.SupersededNodeIndex);
        Assert.Equal(
            correction.SupersededNodeStartTokenIndex,
            projectedCorrection.SupersededNodeStartTokenIndex);
        Assert.Equal(correction.SupersededNodeTokenLength, projectedCorrection.SupersededNodeTokenLength);
        Assert.Equal(
            correction.HasSupersededCurrentTurnEntity,
            projectedCorrection.HasSupersededCurrentTurnEntity);
        Assert.Equal(
            correction.SupersededCurrentTurnNodeIndex,
            projectedCorrection.SupersededCurrentTurnNodeIndex);
        Assert.Equal(
            correction.SupersededCurrentTurnSemanticSignature,
            projectedCorrection.SupersededCurrentTurnSemanticSignature);
        var active = Assert.Single(await ActiveAsync(firstActor, ordinalConversation));
        Assert.Equal("a", active.EntitySemanticValue);

        // 3. Assistant-established entities are eligible only because the
        // Founder-declared rule explicitly permits assistant source roles.
        var assistantConversation = Guid.NewGuid();
        await ObserveAsync(firstActor, assistantConversation, "assistant", "a b c");
        await ObserveAsync(firstActor, assistantConversation, "user", "the second one");
        var assistantReference = Assert.Single(await LatestAsync(firstActor, assistantConversation));
        Assert.Equal("bound", assistantReference.ResolutionState);
        Assert.Equal("b", assistantReference.EntitySemanticValue);
        await using (var assertion = new MasterAppDbContext(options))
        {
            var sourceRole = await assertion.LegendFounderAiDiscourseTurns
                .Where(item => item.Id == assistantReference.EntityTurnId)
                .Select(item => item.Role)
                .SingleAsync();
            Assert.Equal("assistant", sourceRole);
        }

        // 4. The unique rule must remain unresolved when A/B/C are all viable.
        var ambiguousConversation = Guid.NewGuid();
        await ObserveAsync(firstActor, ambiguousConversation, "user", "a b c");
        await ObserveAsync(firstActor, ambiguousConversation, "user", "that one");
        var ambiguous = Assert.Single(await LatestAsync(firstActor, ambiguousConversation));
        Assert.Equal("unresolved", ambiguous.ResolutionState);
        Assert.Equal("reference_candidate_ambiguous", ambiguous.ReasonCode);
        Assert.Null(ambiguous.EntitySemanticSignature);

        // 5. Structurally similar contexts remain conversation-scoped.
        var firstConversation = Guid.NewGuid();
        var secondConversation = Guid.NewGuid();
        await ObserveAsync(firstActor, firstConversation, "user", "a b c");
        await ObserveAsync(firstActor, secondConversation, "user", "d e f");
        await ObserveAsync(firstActor, firstConversation, "user", "the second one");
        await ObserveAsync(firstActor, secondConversation, "user", "the second one");
        Assert.Equal("b", Assert.Single(await LatestAsync(firstActor, firstConversation)).EntitySemanticValue);
        Assert.Equal("e", Assert.Single(await LatestAsync(firstActor, secondConversation)).EntitySemanticValue);

        // 6. The actor boundary is part of the durable conversation identity.
        var actorConversation = Guid.NewGuid();
        await ObserveAsync(secondActor, actorConversation, "user", "d e f");
        await ObserveAsync(firstActor, actorConversation, "user", "a b c");
        await ObserveAsync(firstActor, actorConversation, "user", "the second one");
        Assert.Equal("b", Assert.Single(await LatestAsync(firstActor, actorConversation)).EntitySemanticValue);

        // 7. Every ObserveAsync owns a fresh DbContext/service, so this final
        // reference is a restart-durability proof rather than in-memory state.
        var restartConversation = Guid.NewGuid();
        await ObserveAsync(firstActor, restartConversation, "user", "a b c");
        await ObserveAsync(firstActor, restartConversation, "user", "the second one");
        Assert.Equal("b", Assert.Single(await LatestAsync(firstActor, restartConversation)).EntitySemanticValue);

        // 8. Persisted state contains only structural graph identities and
        // resolution coordinates—never transcript/provider/cache fields.
        await using (var privacy = new MasterAppDbContext(options))
        {
            var turns = await privacy.LegendFounderAiDiscourseTurns.ToListAsync();
            Assert.NotEmpty(turns);
            foreach (var turn in turns)
            {
                var persisted = turn.MeaningGraphJson + turn.ResolvedBindingsJson;
                Assert.DoesNotContain("a b c", persisted, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("the second one", persisted, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("no i meant", persisted, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unknownsurfacecomponents", persisted, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("assistant response", persisted, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("provider", persisted, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("answercache", persisted, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task ResponseMeaningPlan_ConsumesDurableGovernedDiscourseBindingsWithoutSurfaceRouting()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_STAGE3_REFERENCE_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "LEGEND_STAGE3_REFERENCE_SQL_CONNECTION is required for the response-plan SQL proof.");

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using (var migration = new MasterAppDbContext(options))
            await migration.Database.MigrateAsync();

        var actor = Guid.NewGuid().ToString("D");
        await using (var setup = new MasterAppDbContext(options))
        {
            setup.AgentProfiles.Add(Profile(actor, "stage4"));
            await setup.SaveChangesAsync();
            var curriculum = CreateCurriculum(setup);
            var operations = CreateOperations(setup);
            var configuration = Configuration();
            var registry = new LegendLanguageRegistry(setup, configuration);
            var runtime = new LegendConnectRuntimePolicyAuthority(
                setup,
                new FounderAccess(),
                registry,
                configuration,
                NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
            var durable = new LegendConnectHistoricalReevaluationWorkAuthority(setup, runtime, configuration);
            var accepted = await operations.SubmitFounderCurriculumManifestAsync(
                actor,
                new LegendConnectCurriculumManifestSubmission(
                    Enumerable.Range(1, 3).Select(ResponsePlanReferenceFamily).ToArray(),
                    null,
                    "en"));
            Assert.True(accepted.Succeeded, accepted.Message);
            var processor = new LegendConnectCurriculumManifestProcessor(
                setup, curriculum, durable, NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
            Assert.Equal(1, await processor.ProcessPendingAsync(1));
            Assert.Equal(1, await processor.ProcessPendingAsync(1));
            Assert.Equal(1, await processor.ProcessPendingAsync(1));
            Assert.Equal("Completed", await setup.LegendCurriculumManifestWorkItems
                .Select(item => item.ProcessingState).SingleAsync());
            var canonicalBeforeRetry = new
            {
                Families = await setup.LegendCurriculumFamilies.CountAsync(),
                Examples = await setup.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null),
                Anchors = await setup.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null),
                Transitions = await setup.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null)
            };
            Assert.Equal(0, await processor.ProcessPendingAsync(1));
            Assert.Equal(canonicalBeforeRetry.Families, await setup.LegendCurriculumFamilies.CountAsync());
            Assert.Equal(canonicalBeforeRetry.Examples, await setup.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null));
            Assert.Equal(canonicalBeforeRetry.Anchors, await setup.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null));
            Assert.Equal(canonicalBeforeRetry.Transitions, await setup.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null));
            Assert.Equal(0, await setup.LegendLanguageCompositionalAnchors
                .Where(item => item.SupersededUtc == null)
                .GroupBy(item => new { item.CurriculumExampleId, item.AnchorSignature })
                .CountAsync(group => group.Count() > 1));
            Assert.Equal(0, await setup.LegendSemanticTransitionEvidence
                .Where(item => item.SupersededUtc == null)
                .GroupBy(item => new
                {
                    item.TransitionSignature,
                    item.SourceCurriculumExampleId,
                    item.ResultCurriculumExampleId,
                    item.IndependentSourceIdentity
                })
                .CountAsync(group => group.Count() > 1));
            Assert.DoesNotContain(await setup.LegendCurriculumManifestWorkItems.ToListAsync(),
                item => item.ProcessingState == "Failed");
        }

        async Task ObserveAsync(Guid conversationId, string role, string surface)
        {
            await using var db = new MasterAppDbContext(options);
            var operations = CreateOperations(db);
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(surface);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            await new LegendFounderAiDiscourseStateService(
                    db, new AgentProfileAccessResolver(db), operations)
                .RecordObservationAsync(ControllerTestHelpers.BuildUser(actor), conversationId.ToString(), role, graph);
        }

        async Task<LegendConnectDiscourseStateSnapshot> StateAsync(Guid conversationId)
        {
            await using var db = new MasterAppDbContext(options);
            return Assert.IsType<LegendConnectDiscourseStateSnapshot>(
                await new LegendFounderAiDiscourseStateService(
                        db, new AgentProfileAccessResolver(db), CreateOperations(db))
                    .GetStateAsync(ControllerTestHelpers.BuildUser(actor), conversationId.ToString()));
        }

        async Task<LegendConnectResponseMeaningPlanResult> PlanAsync(
            string surface, LegendConnectDiscourseStateSnapshot state)
        {
            await using var db = new MasterAppDbContext(options);
            return await CreateOperations(db).TryPlanConversationAsync(surface, state);
        }

        // The current wording is byte-for-byte identical. Only durable,
        // actor/conversation-scoped semantic bindings differ.
        const string request = "Tell me more about that.";
        var aConversation = Guid.NewGuid();
        var bConversation = Guid.NewGuid();
        await ObserveAsync(aConversation, "user", "a");
        await ObserveAsync(bConversation, "user", "b");
        await ObserveAsync(aConversation, "user", request);
        await ObserveAsync(bConversation, "user", request);
        var planA = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(
            (await PlanAsync(request, await StateAsync(aConversation))).Plan);
        var planB = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(
            (await PlanAsync(request, await StateAsync(bConversation))).Plan);
        Assert.NotEqual(planA.PlanIdentity, planB.PlanIdentity);
        Assert.Equal("a", Assert.Single(planA.ResolvedDiscourseBindings).EntitySemanticValue);
        Assert.Equal("b", Assert.Single(planB.ResolvedDiscourseBindings).EntitySemanticValue);

        // An assistant-established entity is available only through a
        // Founder-declared role-permitting semantic reference rule.
        var assistantConversation = Guid.NewGuid();
        await ObserveAsync(assistantConversation, "assistant", "a");
        await ObserveAsync(assistantConversation, "user", request);
        var assistantPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(
            (await PlanAsync(request, await StateAsync(assistantConversation))).Plan);
        Assert.Equal("a", Assert.Single(assistantPlan.ResolvedDiscourseBindings).EntitySemanticValue);

        // Correction replaces the active ordinal binding before the plan is
        // selected; old B must never appear as the correction target.
        var correctionConversation = Guid.NewGuid();
        await ObserveAsync(correctionConversation, "user", "a b c");
        await ObserveAsync(correctionConversation, "user", "the second one");
        await ObserveAsync(correctionConversation, "user", "No, I meant the first one.");
        var correctionPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(
            (await PlanAsync("No, I meant the first one.", await StateAsync(correctionConversation))).Plan);
        var correctionBinding = Assert.Single(correctionPlan.ResolvedDiscourseBindings);
        Assert.Equal("a", correctionBinding.EntitySemanticValue);
        Assert.True(correctionBinding.ReplacesActiveBinding);

        // Ambiguity remains an explicit non-plan outcome; a plan can never
        // invent a target simply because an eligible transition exists.
        var ambiguousConversation = Guid.NewGuid();
        await ObserveAsync(ambiguousConversation, "user", "a b");
        await ObserveAsync(ambiguousConversation, "user", request);
        var ambiguous = await PlanAsync(request, await StateAsync(ambiguousConversation));
        Assert.False(ambiguous.Supported);
        Assert.Equal("discourse_reference_unresolved", ambiguous.ReasonCode);
        Assert.Null(ambiguous.Plan);

        await using (var privacy = new MasterAppDbContext(options))
        {
            var persisted = await privacy.LegendFounderAiDiscourseTurns
                .Select(item => item.MeaningGraphJson + item.ResolvedBindingsJson)
                .ToArrayAsync();
            Assert.All(persisted, item =>
            {
                Assert.DoesNotContain("Tell me more about that", item, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("No, I meant", item, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("provider", item, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("answercache", item, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    private static void AssertCompletedReference(
        LegendConnectResponseMeaningPlanResult planned,
        string expectedValue,
        string expectedDimension = "explanation")
    {
        Assert.True(planned.Supported, planned.ReasonCode);
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
        var binding = Assert.Single(plan.ResolvedDiscourseBindings);
        Assert.Equal("bound", binding.ResolutionState);
        Assert.Equal(expectedDimension, binding.EntitySemanticDimension);
        Assert.Equal(expectedValue, binding.EntitySemanticValue);
        Assert.False(string.IsNullOrWhiteSpace(binding.SelectorSemanticSignature));
        Assert.False(string.IsNullOrWhiteSpace(binding.ReferenceRuleSignature));
        Assert.NotNull(plan.BoundSemanticVariables);
        Assert.Equal(expectedValue, plan.BoundSemanticVariables!["$subject"]);
        Assert.False(string.IsNullOrWhiteSpace(plan.SourceMeaningGraphIdentity));
    }

    private static async Task<InMemoryReferenceFixture> CreateInMemoryFixtureAsync()
    {
        var db = ControllerTestHelpers.BuildDb();
        var firstActor = Guid.NewGuid().ToString("D");
        var secondActor = Guid.NewGuid().ToString("D");
        db.AgentProfiles.AddRange(
            Profile(firstActor, "early-first"),
            Profile(secondActor, "early-second"));
        await db.SaveChangesAsync();

        var curriculum = CreateCurriculum(db);
        for (var family = 1; family <= 3; family++)
        {
            var submitted = await curriculum.SubmitFounderBatchAsync(
                EarlyBindingReferenceFamily(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var operations = CreateOperations(db);
        return new InMemoryReferenceFixture(
            db,
            operations,
            new LegendFounderAiDiscourseStateService(
                db,
                new AgentProfileAccessResolver(db),
                operations),
            firstActor,
            secondActor);
    }

    private static LegendConnectCurriculumBatchSubmission EarlyBindingReferenceFamily(int family)
    {
        var examples = new List<LegendConnectCurriculumExampleSubmission>
        {
            ExplanationEntityExample(family, "alpha", "Alpha"),
            ExplanationEntityExample(family, "beta", "Beta"),
            ExplanationPairExample(family),
            ReferenceRequestExample(
                family,
                "pronoun",
                "Explain it.",
                "explain_reference",
                "Explain",
                "it",
                "pronoun_it",
                "explanation",
                "unique",
                null,
                ["user", "assistant"],
                false),
            ReferenceRequestExample(
                family,
                "demonstrative",
                "Tell me more about that.",
                "detail_reference",
                "Tell me more about",
                "that",
                "demonstrative_that",
                "explanation",
                "unique",
                null,
                ["user", "assistant"],
                false),
            ReferenceRequestExample(
                family,
                "ordinal",
                "Compare the second one.",
                "ordinal_reference",
                "Compare",
                "second",
                "ordinal_two",
                "explanation",
                "ordinal",
                2,
                ["user", "assistant"],
                false),
            ReferenceRequestExample(
                family,
                "role",
                "Review her proposal.",
                "role_reference",
                "Review",
                "her",
                "assistant_role_pronoun",
                "explanation",
                "unique",
                null,
                ["assistant"],
                false),
            ReferenceRequestExample(
                family,
                "recent",
                "Revisit the latest explanation.",
                "recent_reference",
                "Revisit",
                "latest",
                "recent_explanation",
                "explanation",
                "recent",
                null,
                ["user", "assistant"],
                false),
            ReferenceRequestExample(
                family,
                "correction",
                "No use the first one.",
                "correction_reference",
                "No use",
                "first",
                "ordinal_one_correction",
                "explanation",
                "ordinal",
                1,
                ["user", "assistant"],
                true),
            ReferenceRequestExample(
                family,
                "plural",
                "Contrast those two explanations.",
                "plural_reference",
                "Contrast",
                "those two explanations",
                "two_explanations",
                "explanation_pair",
                "unique",
                null,
                ["user", "assistant"],
                false)
        };

        var functions = new[]
        {
            (Source: "explain_reference", Result: "explanation_response", Dimension: "explanation"),
            (Source: "detail_reference", Result: "detail_response", Dimension: "explanation"),
            (Source: "ordinal_reference", Result: "ordinal_response", Dimension: "explanation"),
            (Source: "role_reference", Result: "role_response", Dimension: "explanation"),
            (Source: "recent_reference", Result: "recent_response", Dimension: "explanation"),
            (Source: "correction_reference", Result: "correction_response", Dimension: "explanation"),
            (Source: "plural_reference", Result: "plural_response", Dimension: "explanation_pair")
        };
        examples.AddRange(functions.Select(item =>
            new LegendConnectCurriculumExampleSubmission(
                $"Founder {item.Result} evidence {family}.",
                new Dictionary<string, string>
                {
                    ["conversation_function"] = item.Result
                })));

        return new LegendConnectCurriculumBatchSubmission(
            $"discourse.reference.early-binding.{family}",
            "Founder-governed early discourse completion evidence",
            examples,
            functions.Select(item => new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = item.Source,
                    [item.Dimension] = "$subject"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = item.Result
                }))).ToArray());
    }

    private static LegendConnectCurriculumExampleSubmission ExplanationEntityExample(
        int family,
        string value,
        string surface) =>
        new(
            $"Founder explanation {value} evidence {family}: {surface} explanation.",
            new Dictionary<string, string>
            {
                ["conversation_function"] = "establish_explanation",
                ["explanation"] = value
            },
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission(
                    "entity", "explanation", value, surface),
                new LegendConnectMeaningNodeSubmission(
                    "kind", "entity_kind", "explanation", "explanation")
            ],
            [new LegendConnectMeaningRelationSubmission(
                "entity", "has-kind", "kind")]));

    private static LegendConnectCurriculumExampleSubmission ExplanationPairExample(int family) =>
        new(
            $"Founder explanation pair evidence {family}: Alpha and beta explanations.",
            new Dictionary<string, string>
            {
                ["conversation_function"] = "establish_explanation_pair",
                ["explanation_pair"] = "alpha_beta"
            },
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission(
                    "first", "explanation", "alpha", "Alpha"),
                new LegendConnectMeaningNodeSubmission(
                    "second", "explanation", "beta", "beta"),
                new LegendConnectMeaningNodeSubmission(
                    "pair", "explanation_pair", "alpha_beta", "Alpha and beta explanations")
            ],
            [
                new LegendConnectMeaningRelationSubmission(
                    "first", "contrasted-with", "second"),
                new LegendConnectMeaningRelationSubmission(
                    "pair", "contains", "first"),
                new LegendConnectMeaningRelationSubmission(
                    "pair", "contains", "second")
            ]));

    private static LegendConnectCurriculumExampleSubmission ReferenceRequestExample(
        int family,
        string key,
        string sentence,
        string function,
        string functionSurface,
        string selectorSurface,
        string selectorValue,
        string entityDimension,
        string resolutionMode,
        int? selectionRank,
        IReadOnlyList<string> allowedRoles,
        bool replacesActiveBinding) =>
        new(
            $"Founder {key} reference evidence {family}: {sentence}",
            new Dictionary<string, string>
            {
                ["conversation_function"] = function,
                [entityDimension] = entityDimension == "explanation_pair"
                    ? "training_explanation_pair"
                    : "training_explanation"
            },
            new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission(
                    "function", "conversation_function", function, functionSurface),
                new LegendConnectMeaningNodeSubmission(
                    "selector", "reference_selector", selectorValue, selectorSurface)
            ],
            [new LegendConnectMeaningRelationSubmission(
                "function", "references", "selector")],
            [new LegendConnectDiscourseReferenceSubmission(
                "selector",
                entityDimension,
                resolutionMode,
                selectionRank,
                allowedRoles,
                replacesActiveBinding)]));

    private static AgentProfile Profile(string actor, string prefix) => new()
    {
        Id = Guid.NewGuid(),
        AgentUserId = actor,
        AgentUpn = $"{prefix}-{actor}@legend.test",
        NormalizedEmail = $"{prefix}-{actor}@legend.test",
        IsActive = true
    };

    private static LegendConnectOperations CreateOperations(MasterAppDbContext db)
    {
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        return new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);
    }

    private static LegendConnectCurriculumService CreateCurriculum(MasterAppDbContext db)
    {
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        return new LegendConnectCurriculumService(db, registry, corpus);
    }

    private static LegendConnectCurriculumBatchSubmission ReferenceFamily(int family) => new(
        $"discourse.reference.support.{family}",
        "Founder-governed discourse reference evidence",
        [
            EntityExample(family),
            OrdinalExample(family, "second", "ordinal_two", 2, false),
            OrdinalExample(family, "first", "ordinal_one", 1, true),
            UniqueExample(family),
            AlternateEntityExample(family)
        ]);

    private static LegendConnectCurriculumBatchSubmission ResponsePlanReferenceFamily(int family) => new(
        $"response.plan.reference.{family}",
        "Founder-governed response-plan and discourse-reference evidence",
        [
            SingleEntityExample(family, "a"),
            SingleEntityExample(family, "b"),
            OrderedEntityExample(family),
            SecondOrdinalReferenceExample(family),
            DetailRequestExample(family),
            CorrectionRequestExample(family),
            new LegendConnectCurriculumExampleSubmission(
                $"Detail response {family}.",
                new Dictionary<string, string> { ["conversation_function"] = "detail_response" }),
            new LegendConnectCurriculumExampleSubmission(
                $"Correction response {family}.",
                new Dictionary<string, string> { ["conversation_function"] = "correction_acknowledgement" })
        ],
        [
            new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "detail_request"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "detail_response"
                })),
            new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "correction_acknowledgement"
                }))
        ]);

    private static LegendConnectCurriculumExampleSubmission SingleEntityExample(int family, string value) =>
        new($"Founder entity evidence {family}: {value}", Variations("establish"),
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission("entity", "choice", value, value),
                    new LegendConnectMeaningNodeSubmission("kind", "entity_kind", "choice", value)
                ],
                [new LegendConnectMeaningRelationSubmission("entity", "has-kind", "kind")]));

    private static LegendConnectCurriculumExampleSubmission OrderedEntityExample(int family) =>
        new($"Founder ordered entities {family}: a b c", Variations("establish"),
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

    private static LegendConnectCurriculumExampleSubmission SecondOrdinalReferenceExample(int family) =>
        new($"Founder ordinal reference {family}: the second one.", Variations("reference"),
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission("selector", "reference_selector", "ordinal_two", "second"),
                    new LegendConnectMeaningNodeSubmission("kind", "reference_kind", "choice", "one")
                ],
                [new LegendConnectMeaningRelationSubmission("selector", "reference-target", "kind")],
                [new LegendConnectDiscourseReferenceSubmission(
                    "selector", "choice", "ordinal", 2, ["user", "assistant"])]));

    private static LegendConnectCurriculumExampleSubmission DetailRequestExample(int family) =>
        new($"Founder detail request {family}: Tell me more about that.",
            new Dictionary<string, string> { ["conversation_function"] = "detail_request" },
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission("function", "conversation_function", "detail_request", "Tell me more"),
                    new LegendConnectMeaningNodeSubmission("selector", "reference_selector", "unique_choice", "that")
                ],
                [new LegendConnectMeaningRelationSubmission("function", "about", "selector")],
                [new LegendConnectDiscourseReferenceSubmission(
                    "selector", "choice", "unique", null, ["user", "assistant"])]));

    private static LegendConnectCurriculumExampleSubmission CorrectionRequestExample(int family) =>
        new($"Founder correction request {family}: No, I meant the first one.",
            new Dictionary<string, string> { ["conversation_function"] = "correction" },
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission("function", "conversation_function", "correction", "No I meant"),
                    new LegendConnectMeaningNodeSubmission("selector", "reference_selector", "ordinal_one", "first")
                ],
                [new LegendConnectMeaningRelationSubmission("function", "corrects", "selector")],
                [new LegendConnectDiscourseReferenceSubmission(
                    "selector", "choice", "ordinal", 1, ["user", "assistant"], true)]));

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

    private static LegendConnectCurriculumExampleSubmission OrdinalExample(
        int family,
        string surface,
        string selectorValue,
        int rank,
        bool replaceActive) =>
        new($"Please use the {surface} one variant {family}.",
            Variations("reference"),
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission("selector", "reference_selector", selectorValue, surface),
                    new LegendConnectMeaningNodeSubmission("kind", "reference_kind", "choice", "one")
                ],
                [new LegendConnectMeaningRelationSubmission("selector", "reference-target", "kind")],
                [new LegendConnectDiscourseReferenceSubmission(
                    "selector", "choice", "ordinal", rank, ["user", "assistant"], replaceActive)]));

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

    private static IReadOnlyDictionary<string, string> Variations(string function) =>
        new Dictionary<string, string>
        {
            ["function"] = function,
            ["utterance_kind"] = "discourse"
        };

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English"
        })
        .Build();

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(MessagingActor actor, string resourceType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, true));
        public Task<bool> IsFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class InMemoryReferenceFixture : IAsyncDisposable
    {
        internal InMemoryReferenceFixture(
            MasterAppDbContext db,
            LegendConnectOperations operations,
            LegendFounderAiDiscourseStateService discourse,
            string firstActor,
            string secondActor)
        {
            Db = db;
            Operations = operations;
            Discourse = discourse;
            FirstActor = firstActor;
            SecondActor = secondActor;
        }

        internal MasterAppDbContext Db { get; }
        private LegendConnectOperations Operations { get; }
        private LegendFounderAiDiscourseStateService Discourse { get; }
        internal string FirstActor { get; }
        internal string SecondActor { get; }

        internal Task<LegendConnectUtteranceMeaningGraphSnapshot> AnalyzeAsync(string surface) =>
            Operations.AnalyzeReusableMeaningGraphAsync(surface);

        internal Task ObserveGraphAsync(
            string actor,
            Guid conversationId,
            string role,
            LegendConnectUtteranceMeaningGraphSnapshot graph) =>
            Discourse.RecordObservationAsync(
                ControllerTestHelpers.BuildUser(actor),
                conversationId.ToString(),
                role,
                graph);

        internal async Task ObserveAsync(
            string actor,
            Guid conversationId,
            string role,
            string surface)
        {
            var graph = await Operations.AnalyzeReusableMeaningGraphAsync(surface);
            Assert.NotEmpty(graph.Nodes);
            await Discourse.RecordObservationAsync(
                ControllerTestHelpers.BuildUser(actor),
                conversationId.ToString(),
                role,
                graph);
        }

        internal async Task<LegendConnectDiscourseStateSnapshot> StateAsync(
            string actor,
            Guid conversationId) =>
            Assert.IsType<LegendConnectDiscourseStateSnapshot>(
                await Discourse.GetStateAsync(
                    ControllerTestHelpers.BuildUser(actor),
                    conversationId.ToString()));

        internal async Task<LegendConnectResponseMeaningPlanResult> PlanAsync(
            string actor,
            Guid conversationId,
            string surface) =>
            await Operations.TryPlanConversationAsync(
                surface,
                await StateAsync(actor, conversationId));

        internal Task<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> LatestBindingsAsync(
            string actor,
            Guid conversationId) =>
            Discourse.GetLatestBindingsAsync(actor, conversationId);

        internal Task<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> ActiveBindingsAsync(
            string actor,
            Guid conversationId) =>
            Discourse.GetActiveBindingsAsync(actor, conversationId);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
