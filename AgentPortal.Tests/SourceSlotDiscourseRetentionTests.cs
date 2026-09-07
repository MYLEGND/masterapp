using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
using Moq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Discourse durability over real Founder source templates and actual source
/// analysis. The interface-default test is only a transport compatibility
/// check; it does not manufacture positive curriculum evidence.
/// </summary>
public sealed class SourceSlotDiscourseRetentionTests
{
    [Fact]
    public async Task UnsupportedOperations_PreserveLegacyNodesAndRejectEveryNewReceiptShape()
    {
        var operations = new Mock<ILegendConnectOperations> { CallBase = true }.Object;
        var legacy = new LegendConnectUtteranceMeaningNode("known", "quantity", "7", 0, 1, 3);
        Assert.True(await operations.AreSourceSlotBindingsActiveAsync([legacy]));
        Assert.True(await operations.AreSourceSlotBindingsActiveAsync([]));
        var receipt = new LegendConnectSourceSlotBinding("en", "forged", "forged", [], []);
        Assert.False(await operations.AreSourceSlotBindingsActiveAsync([
            legacy with { SourceSlotBinding = receipt }]));
        Assert.False(await operations.AreSourceSlotBindingsActiveAsync([
            legacy with { Provenance = "CurrentTurnAssertion" }]));
        Assert.False(await operations.AreSourceSlotBindingsActiveAsync([
            legacy with { Provenance = "CurrentTurnAssertion", SourceSlotBinding = receipt }]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await operations.AreSourceSlotBindingsActiveAsync([], cancelled.Token));
    }

    [Fact]
    public async Task ActualSourceReceipt_SurvivesReloadAndLosesEligibilityWhenOriginalTemplateIsWithdrawn()
    {
        var options = DatabaseOptions();
        var actor = Guid.NewGuid().ToString("D");
        var conversation = Guid.NewGuid();
        string persisted;
        Guid selectedTransition;
        await using (var fixture = new Fixture(options))
        {
            await SeedAsync(fixture, actor);
            var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Compute 147 against 26.");
            var receipt = AssertActualReceipt(graph);
            selectedTransition = receipt.Evidence[0].TransitionEvidenceId;
            Assert.True(await fixture.Operations.AreSourceSlotBindingsActiveAsync(graph.Nodes));
            await fixture.Discourse.RecordObservationAsync(ControllerTestHelpers.BuildUser(actor),
                conversation.ToString("D"), "user", graph);
            persisted = (await fixture.Db.LegendFounderAiDiscourseTurns.SingleAsync()).MeaningGraphJson;
            Assert.DoesNotContain("Compute 147 against 26", persisted, StringComparison.Ordinal);
            Assert.False(await fixture.Db.LegendLanguageMeaningNodeEvidence.AnyAsync(node =>
                node.SemanticValue == "147" || node.SemanticValue == "26"));
        }

        await using (var reloaded = new Fixture(options))
        {
            var state = Assert.IsType<LegendConnectDiscourseStateSnapshot>(await reloaded.Discourse.GetStateAsync(
                ControllerTestHelpers.BuildUser(actor), conversation.ToString("D")));
            var turn = Assert.Single(state.Turns);
            Assert.True(turn.IsComposed);
            Assert.NotNull(turn.Nodes.Single(node => node.SemanticValue == "147").SourceSlotBinding);
            var original = await reloaded.Db.LegendSemanticTransitionEvidence.SingleAsync(item => item.Id == selectedTransition);
            original.SupersededUtc = DateTime.UtcNow;
            await reloaded.Db.SaveChangesAsync();
        }

        await using var stale = new Fixture(options);
        var withheld = Assert.IsType<LegendConnectDiscourseStateSnapshot>(await stale.Discourse.GetStateAsync(
            ControllerTestHelpers.BuildUser(actor), conversation.ToString("D")));
        Assert.False(Assert.Single(withheld.Turns).IsComposed);
        Assert.Empty(withheld.Turns[0].Nodes);
        Assert.Equal(persisted, (await stale.Db.LegendFounderAiDiscourseTurns.SingleAsync()).MeaningGraphJson);
    }

    [Fact]
    public async Task InvalidCurrentReceipt_CannotPersistAsComposed_AndCancellationWritesNoTurn()
    {
        await using var fixture = new Fixture(DatabaseOptions());
        var actor = Guid.NewGuid().ToString("D");
        await SeedAsync(fixture, actor);
        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Compute 147 against 26.");
        AssertActualReceipt(graph);
        var forged = graph with
        {
            Nodes = graph.Nodes.Select(node => node.SourceSlotBinding is null ? node : node with
            {
                SourceSlotBinding = node.SourceSlotBinding with { NormalizedInputHash = new string('0', 64) }
            }).ToArray()
        };
        Assert.False(await fixture.Operations.AreSourceSlotBindingsActiveAsync(forged.Nodes));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Discourse.RecordObservationAsync(
            ControllerTestHelpers.BuildUser(actor), Guid.NewGuid().ToString("D"), "user", forged, cancelled.Token));
        Assert.Empty(await fixture.Db.LegendFounderAiDiscourseTurns.ToArrayAsync());
        await fixture.Discourse.RecordObservationAsync(ControllerTestHelpers.BuildUser(actor),
            Guid.NewGuid().ToString("D"), "user", forged);
        var stored = await fixture.Db.LegendFounderAiDiscourseTurns.SingleAsync();
        using var payload = JsonDocument.Parse(stored.MeaningGraphJson);
        Assert.False(payload.RootElement.GetProperty("IsComposed").GetBoolean());
        Assert.Empty(payload.RootElement.GetProperty("Nodes").EnumerateArray());
        Assert.Equal("source_slot_evidence_unavailable", stored.AnalysisReasonCode);
    }

    [Theory]
    [InlineData("same_meaning")]
    [InlineData("changed_roles")]
    [InlineData("original_withdrawn")]
    public async Task LaterExactFounderKnowledge_PreservesOnlyCompatibleOriginalObservationReceipts(string growth)
    {
        var options = DatabaseOptions();
        var actor = Guid.NewGuid().ToString("D");
        var conversation = Guid.NewGuid();
        string persisted;
        await using (var fixture = new Fixture(options))
        {
            await SeedAsync(fixture, actor);
            var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Compute 147 against 26.");
            var receipt = AssertActualReceipt(graph);
            await fixture.Discourse.RecordObservationAsync(ControllerTestHelpers.BuildUser(actor),
                conversation.ToString("D"), "user", graph);
            persisted = (await fixture.Db.LegendFounderAiDiscourseTurns.SingleAsync()).MeaningGraphJson;

            // Genuine Founder admission adds source meaning only. No observed
            // result or replacement source-slot template is supplied.
            var addition = await fixture.Curriculum.SubmitFounderBatchAsync(new(
                "discourse.source-slot.later-founder", "Later exact source meanings",
                [ExactFounderSource("147", "26", growth == "changed_roles"),
                 ExactFounderSource("152", "31", growth == "changed_roles")]));
            Assert.True(addition.Succeeded, addition.Message);
            var exact = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Compute 147 against 26.");
            Assert.True(exact.IsComposed, exact.ReasonCode);
            Assert.Equal(2, exact.Nodes.Count);
            Assert.All(exact.Nodes, node => Assert.Equal("FounderApproved", node.Provenance));
            Assert.Equal(growth == "changed_roles" ? "26" : "147",
                exact.Nodes.Single(node => node.SemanticDimension == "z_measure").SemanticValue);
            if (growth == "original_withdrawn")
            {
                var original = await fixture.Db.LegendSemanticTransitionEvidence.SingleAsync(
                    item => item.Id == receipt.Evidence[0].TransitionEvidenceId);
                original.SupersededUtc = DateTime.UtcNow;
                await fixture.Db.SaveChangesAsync();
            }
        }

        await using var reloaded = new Fixture(options);
        var state = Assert.IsType<LegendConnectDiscourseStateSnapshot>(await reloaded.Discourse.GetStateAsync(
            ControllerTestHelpers.BuildUser(actor), conversation.ToString("D")));
        var retained = Assert.Single(state.Turns);
        if (growth == "same_meaning")
        {
            Assert.True(retained.IsComposed);
            Assert.Equal(2, retained.Nodes.Count);
            Assert.All(retained.Nodes, node =>
            {
                Assert.Equal("CurrentTurnAssertion", node.Provenance);
                Assert.Equal(1, node.IndependentSupportCount);
                Assert.NotNull(node.SourceSlotBinding);
            });
            using var original = JsonDocument.Parse(persisted);
            Assert.Equal(original.RootElement.GetProperty("Nodes").GetRawText(), JsonSerializer.Serialize(retained.Nodes));
        }
        else
        {
            Assert.False(retained.IsComposed);
            Assert.Empty(retained.Nodes);
        }
        Assert.Equal(persisted, (await reloaded.Db.LegendFounderAiDiscourseTurns.SingleAsync()).MeaningGraphJson);
    }

    [Fact]
    public async Task WithdrawnReplacementTarget_PreservesTheBarrierAgainstOlderValueResurrection()
    {
        var options = DatabaseOptions();
        var actor = Guid.NewGuid().ToString("D");
        var conversation = Guid.NewGuid();
        string[] originalGraphs;
        string[] originalBindings;
        await using (var fixture = new Fixture(options))
        {
            await SeedAsync(fixture, actor);
            await SeedReferenceRulesAsync(fixture.Curriculum);
            await ObserveAsync(fixture, actor, conversation, "Compute 83 against 29.");
            await ObserveAsync(fixture, actor, conversation, "that");
            Assert.Equal("83", Assert.Single(await fixture.Discourse.GetActiveBindingsAsync(actor, conversation)).EntitySemanticValue);
            var observed = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Compute 147 against 26.");
            var receipt = AssertActualReceipt(observed);
            await fixture.Discourse.RecordObservationAsync(ControllerTestHelpers.BuildUser(actor),
                conversation.ToString("D"), "user", observed);
            await ObserveAsync(fixture, actor, conversation, "instead");
            var replacement = Assert.Single(await fixture.Discourse.GetActiveBindingsAsync(actor, conversation));
            Assert.Equal("147", replacement.EntitySemanticValue);
            Assert.True(replacement.ReplacesActiveBinding);
            Assert.Equal("83", replacement.SupersededEntitySemanticValue);
            var turns = await fixture.Db.LegendFounderAiDiscourseTurns.OrderBy(turn => turn.SequenceNumber).ToArrayAsync();
            originalGraphs = turns.Select(turn => turn.MeaningGraphJson).ToArray();
            originalBindings = turns.Select(turn => turn.ResolvedBindingsJson).ToArray();
            var withdrawn = await fixture.Db.LegendSemanticTransitionEvidence.SingleAsync(
                item => item.Id == receipt.Evidence[0].TransitionEvidenceId);
            withdrawn.SupersededUtc = DateTime.UtcNow;
            await fixture.Db.SaveChangesAsync();
        }

        await using var reloaded = new Fixture(options);
        var state = Assert.IsType<LegendConnectDiscourseStateSnapshot>(await reloaded.Discourse.GetStateAsync(
            ControllerTestHelpers.BuildUser(actor), conversation.ToString("D")));
        Assert.Equal(new[] { 1, 2, 3, 4 }, state.Turns.Select(turn => turn.SequenceNumber));
        Assert.True(state.Turns[0].IsComposed);
        Assert.Contains(state.Turns[0].Nodes, node => node.SemanticValue == "83" && node.Provenance == "FounderApproved");
        Assert.False(state.Turns[2].IsComposed);
        var invalidReplacement = Assert.Single(state.Turns[3].Bindings);
        Assert.Equal("unresolved", invalidReplacement.ResolutionState);
        Assert.True(invalidReplacement.ReplacesActiveBinding);
        Assert.Equal("83", invalidReplacement.SupersededEntitySemanticValue);
        Assert.Empty(await reloaded.Discourse.GetActiveBindingsAsync(actor, conversation));
        var unchanged = await reloaded.Db.LegendFounderAiDiscourseTurns.OrderBy(turn => turn.SequenceNumber).ToArrayAsync();
        Assert.Equal(originalGraphs, unchanged.Select(turn => turn.MeaningGraphJson));
        Assert.Equal(originalBindings, unchanged.Select(turn => turn.ResolvedBindingsJson));
        await ObserveAsync(reloaded, actor, conversation, "that");
        var unresolved = Assert.Single(await reloaded.Discourse.GetLatestBindingsAsync(actor, conversation));
        Assert.Equal("unresolved", unresolved.ResolutionState);
        Assert.Equal("reference_active_binding_invalid", unresolved.ReasonCode);
        Assert.Null(unresolved.EntitySemanticValue);
    }

    private static LegendConnectSourceSlotBinding AssertActualReceipt(LegendConnectUtteranceMeaningGraphSnapshot graph)
    {
        Assert.True(graph.IsComposed, graph.ReasonCode);
        var nodes = graph.Nodes.Where(node => node.SourceSlotBinding is not null).ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.All(nodes, node =>
        {
            Assert.Equal("CurrentTurnAssertion", node.Provenance);
            Assert.Equal(1, node.IndependentSupportCount);
            Assert.Null(node.SourceMeaningNodeEvidenceId);
        });
        var receipt = nodes[0].SourceSlotBinding!;
        Assert.Equal(2, receipt.Captures.Count);
        Assert.True(receipt.Evidence.Select(item => item.SourceFamilyId).Distinct().Count() >= 2);
        return receipt;
    }

    private static async Task ObserveAsync(Fixture fixture, string actor, Guid conversation, string input)
    {
        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync(input);
        Assert.True(graph.IsComposed, graph.ReasonCode);
        await fixture.Discourse.RecordObservationAsync(ControllerTestHelpers.BuildUser(actor),
            conversation.ToString("D"), "user", graph);
    }

    private static async Task SeedAsync(Fixture fixture, string actor)
    {
        ControllerTestHelpers.SeedGovernedLanguageBaseline(fixture.Db);
        fixture.Db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(), AgentUserId = actor, AgentUpn = actor + "@legend.test",
            NormalizedEmail = actor + "@legend.test", IsActive = true
        });
        await fixture.Db.SaveChangesAsync();
        await LegendConnectComputedLanguageEndToEndContractTests.SeedTeachingAsync(fixture.Curriculum);
    }

    private static async Task SeedReferenceRulesAsync(LegendConnectCurriculumService curriculum)
    {
        for (var family = 1; family <= 3; family++)
        {
            LegendConnectCurriculumExampleSubmission Reference(string surface, string value, bool replaces) =>
                new(surface, new Dictionary<string, string> { ["reference_selector"] = value },
                    new([new("selector", "reference_selector", value, surface)], [],
                        [new("selector", "z_measure", replaces ? "recent" : "unique", null, ["user"], replaces)]));
            var result = await curriculum.SubmitFounderBatchAsync(new(
                "discourse.source-slot.reference." + family, "Explicit quantity reference and replacement",
                [Reference("that", "quantity_choice", false), Reference("instead", "quantity_replacement", true)]));
            Assert.True(result.Succeeded, result.Message);
        }
    }

    private static LegendConnectCurriculumExampleSubmission ExactFounderSource(string left, string right, bool changedRoles)
    {
        var leftDimension = changedRoles ? "a_measure" : "z_measure";
        var rightDimension = changedRoles ? "z_measure" : "a_measure";
        return new("Compute " + left + " against " + right + ".",
            new Dictionary<string, string> { [leftDimension] = left, [rightDimension] = right },
            new([new("left", leftDimension, left, left), new("right", rightDimension, right, right)],
                [new("left", "paired-with", "right")]));
    }

    private static DbContextOptions<MasterAppDbContext> DatabaseOptions() =>
        new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), new InMemoryDatabaseRoot())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;

    private sealed class Fixture : IAsyncDisposable
    {
        internal Fixture(DbContextOptions<MasterAppDbContext> options)
        {
            Db = new(options);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["LegendConnect:CorpusAcquisition:Enabled"] = "false" }).Build();
            var registry = new LegendLanguageRegistry(Db, configuration);
            var corpus = new LegendConnectCorpusService(Db, registry, NullLogger<LegendConnectCorpusService>.Instance);
            Curriculum = new(Db, registry, corpus);
            Operations = new(Db, registry, corpus, configuration, curriculum: Curriculum);
            Discourse = new(Db, new AgentProfileAccessResolver(Db), Operations);
        }

        internal MasterAppDbContext Db { get; }
        internal LegendConnectCurriculumService Curriculum { get; }
        internal LegendConnectOperations Operations { get; }
        internal LegendFounderAiDiscourseStateService Discourse { get; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
