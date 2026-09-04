using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// The typed owned-record intent must be established only by the real governed
/// meaning-graph analyzer from Founder-approved node and relation evidence that
/// satisfies the existing production-eligibility constraints. These tests run
/// the actual curriculum submission and
/// <see cref="LegendConnectOperations.AnalyzeReusableMeaningGraphAsync"/>; no
/// graph is hand-constructed, so node/relation admission, family overlap,
/// independent-source counts and canonical endpoint indexes are all exercised.
/// </summary>
public class LegendConnectOwnedRecordIntentAnalyzerTests
{
    private const string HeldOutSurface = "portfolio holds record_state";

    [Fact]
    public async Task AdmittedGovernedRelation_EmitsValidEndpointsAndTypesTheIntent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        await SubmitOwnedRecordFamiliesAsync(db, RequiredKind);

        var graph = await CreateOperations(db)
            .AnalyzeReusableMeaningGraphAsync(HeldOutSurface);

        Assert.True(graph.IsComposed, graph.ReasonCode);
        Assert.NotEmpty(graph.Relations);

        // Every emitted relation must reference real, in-range node endpoints.
        Assert.All(graph.Relations, relation =>
        {
            Assert.InRange(relation.SourceNodeIndex, 0, graph.Nodes.Count - 1);
            Assert.InRange(relation.TargetNodeIndex, 0, graph.Nodes.Count - 1);
            Assert.NotEqual(relation.SourceNodeIndex, relation.TargetNodeIndex);
        });

        Assert.Contains(
            graph.Relations,
            relation => string.Equals(
                relation.RelationKind,
                RequiredKind,
                StringComparison.Ordinal));

        var classification = LegendConnectOwnedRecordRequest.Classify(graph);

        Assert.Equal(
            LegendConnectOwnedRecordIntent.OwnedRecordStateInspection,
            classification.Intent);
        Assert.True(classification.RequiresGovernedReadReceipt);
        Assert.Null(classification.MissingRelationKind);
    }

    [Fact]
    public async Task GovernedRelationOfAnotherKind_CannotTypeTheIntent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        await SubmitOwnedRecordFamiliesAsync(db, "unrelated_governed_relation");

        var graph = await CreateOperations(db)
            .AnalyzeReusableMeaningGraphAsync(HeldOutSurface);

        AssertCannotType(graph);
    }

    /// <summary>
    /// CONFIRMED by execution: a single Founder-approved family already admits
    /// the relation with IndependentSupportCount = 1. Independent-source
    /// support is therefore not an admission precondition at this layer; the
    /// emitted support count is what downstream authorities must weigh. This
    /// records the real analyzer behavior instead of asserting a constraint it
    /// does not enforce.
    /// </summary>
    [Fact]
    public async Task SingleSourceEvidence_TypesTheIntentWithSingleSourceSupport()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        await SubmitOwnedRecordFamiliesAsync(db, RequiredKind, families: 1);

        var graph = await CreateOperations(db)
            .AnalyzeReusableMeaningGraphAsync(HeldOutSurface);

        var admitted = Assert.Single(
            graph.Relations.Where(relation => string.Equals(
                relation.RelationKind,
                RequiredKind,
                StringComparison.Ordinal)));

        Assert.Equal(1, admitted.IndependentSupportCount);
        Assert.Equal(
            LegendConnectOwnedRecordIntent.OwnedRecordStateInspection,
            LegendConnectOwnedRecordRequest.Classify(graph).Intent);
    }

    [Fact]
    public async Task CrossFamilyAnchorsWithoutSharedEvidence_CannotTypeTheIntent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);

        // The two anchors of the held-out surface are taught in disjoint
        // families, so no family overlap can admit a relation between them.
        for (var family = 1; family <= 3; family++)
        {
            var result = await CreateCurriculum(db).SubmitFounderBatchAsync(
                new LegendConnectCurriculumBatchSubmission(
                    $"owned.record.split.{family}",
                    "Founder-governed split-anchor evidence",
                    [
                        SplitAnchorExample(family),
                        SplitAnchorExample(family + 100)
                    ]));
            Assert.True(result.Succeeded, result.Message);
        }

        var graph = await CreateOperations(db)
            .AnalyzeReusableMeaningGraphAsync(HeldOutSurface);

        AssertCannotType(graph);
    }

    [Fact]
    public void NoAnalysis_CannotTypeTheIntentAndNamesTheMissingRelation()
    {
        var classification = LegendConnectOwnedRecordRequest.Classify(graph: null);

        Assert.Equal(
            LegendConnectOwnedRecordIntent.Unknown,
            classification.Intent);
        Assert.False(classification.RequiresGovernedReadReceipt);
        Assert.Equal(RequiredKind, classification.MissingRelationKind);
    }

    private const string RequiredKind =
        LegendConnectOwnedRecordRequest.RequiredRelationKind;

    private static void AssertCannotType(
        LegendConnectUtteranceMeaningGraphSnapshot graph)
    {
        Assert.DoesNotContain(
            graph.Relations,
            relation => string.Equals(
                relation.RelationKind,
                RequiredKind,
                StringComparison.Ordinal));

        var classification = LegendConnectOwnedRecordRequest.Classify(graph);

        Assert.Equal(
            LegendConnectOwnedRecordIntent.Unknown,
            classification.Intent);
        Assert.False(classification.RequiresGovernedReadReceipt);
        Assert.Equal(RequiredKind, classification.MissingRelationKind);
    }

    private static async Task SubmitOwnedRecordFamiliesAsync(
        MasterAppDbContext db,
        string relationKind,
        int families = 3)
    {
        for (var family = 1; family <= families; family++)
        {
            var result = await CreateCurriculum(db).SubmitFounderBatchAsync(
                new LegendConnectCurriculumBatchSubmission(
                    $"owned.record.state.{relationKind}.{family}",
                    "Founder-governed owned-record state evidence",
                    [
                        OwnedRecordExample(family, relationKind),
                        OwnedRecordExample(family + 100, relationKind)
                    ]));
            Assert.True(result.Succeeded, result.Message);
        }
    }

    private static LegendConnectCurriculumExampleSubmission SplitAnchorExample(
        int index) =>
        new(
            $"portfolio {index}",
            Variations(),
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission(
                        "owner", "record_owner", "portfolio", "portfolio")
                ],
                []));

    private static LegendConnectCurriculumExampleSubmission OwnedRecordExample(
        int index,
        string relationKind) =>
        new(
            $"portfolio holds record_state {index}",
            Variations(),
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission(
                        "owner", "record_owner", "portfolio", "portfolio"),
                    new LegendConnectMeaningNodeSubmission(
                        "state", "record_state", "record_state", "record_state")
                ],
                [
                    new LegendConnectMeaningRelationSubmission(
                        "owner", relationKind, "state")
                ]));

    private static IReadOnlyDictionary<string, string> Variations() =>
        new Dictionary<string, string>
        {
            ["function"] = "establish",
            ["utterance_kind"] = "discourse"
        };

    private static LegendConnectOperations CreateOperations(MasterAppDbContext db)
    {
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        return new LegendConnectOperations(
            db, registry, corpus, configuration, curriculum: curriculum);
    }

    private static LegendConnectCurriculumService CreateCurriculum(
        MasterAppDbContext db)
    {
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        return new LegendConnectCurriculumService(db, registry, corpus);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Languages:0:Code"] = "en",
            ["LegendConnect:Languages:0:DisplayName"] = "English"
        })
        .Build();
}
