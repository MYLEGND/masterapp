using System;
using System.Collections.Generic;
using System.Linq;
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
/// Stage-3 SQL proof that cross-turn reference binding is derived from mature,
/// Founder-declared semantic rules and persisted graph identities only.  The
/// test intentionally recreates its DbContext for every observed turn.
/// </summary>
public sealed class LegendConnectDiscourseReferenceBindingTests
{
    [Fact]
    public async Task GovernedReferenceBinding_UsesOnlyScopedPersistedSemanticIdentities()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_STAGE3_REFERENCE_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

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
                var result = await curriculum.SubmitFounderEnglishBatchAsync(ReferenceFamily(family));
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
}
