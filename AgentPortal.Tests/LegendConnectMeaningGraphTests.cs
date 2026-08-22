using System;
using System.Collections.Generic;
using System.Data;
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
/// Phase-1 proof for explicit Founder utterance-meaning graphs.  These tests
/// use the canonical curriculum submission authority only; they never supply
/// a runtime interpretation, response, or direct graph row.
/// </summary>
public sealed class LegendConnectMeaningGraphTests
{
    [Fact]
    public async Task ExplicitFounderMeaningGraphs_PreserveCanonicalNodesRelationsAndIndependentSupport()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var curriculum = CreateCurriculum(db);

        for (var family = 1; family <= 3; family++)
        {
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(
                Family(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var relation = await db.LegendLanguageMeaningRelations.SingleAsync();
        Assert.Equal("request-target", relation.RelationKind);
        Assert.Equal(3, relation.SupportCount);
        Assert.Equal(3, relation.IndependentSourceCount);
        Assert.Equal(3, relation.HumanVerifiedSupportCount);
        Assert.Equal(0, relation.ContradictionCount);
        Assert.Equal("Supported", relation.MaturityState);
        Assert.False(relation.IsProductionEligible);

        var nodes = await db.LegendLanguageMeaningNodeEvidence
            .OrderBy(item => item.CurriculumExampleId)
            .ThenBy(item => item.NodeKey)
            .ToListAsync();
        Assert.Equal(6, nodes.Count);
        Assert.All(nodes, node =>
        {
            Assert.Equal("en", node.LanguageCode);
            Assert.Equal("FounderApproved", node.Provenance);
            Assert.Null(node.SupersededUtc);
            Assert.NotEqual(Guid.Empty, node.CompositionalAnchorId);
        });
        Assert.Equal(3, await db.LegendLanguageMeaningRelationEvidence.CountAsync(item =>
            item.MeaningRelationId == relation.Id &&
            item.ContributionState == "Supported" &&
            item.IsHumanVerifiedSupport &&
            item.SupersededUtc == null));

        var before = await CountsAsync(db);
        for (var family = 1; family <= 3; family++)
        {
            var replayed = await curriculum.SubmitFounderEnglishBatchAsync(Family(family));
            Assert.True(replayed.Succeeded, replayed.Message);
            Assert.True(replayed.DuplicatePrevented);
        }
        Assert.Equal(before, await CountsAsync(db));
    }

    [Fact]
    public async Task MeaningGraph_RejectsUnknownEndpointsAndDoesNotPromoteAdjacencyIntoMeaning()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var curriculum = CreateCurriculum(db);

        var rejected = await curriculum.SubmitFounderEnglishBatchAsync(
            new LegendConnectCurriculumBatchSubmission(
                "meaning.graph.invalid.endpoint",
                "Invalid explicit graph must fail closed",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        "Please explain the option.",
                        new Dictionary<string, string>
                        {
                            ["predicate"] = "explain",
                            ["request_target"] = "option"
                        },
                        new LegendConnectMeaningGraphSubmission(
                            [
                                new LegendConnectMeaningNodeSubmission(
                                    "act", "predicate", "explain", "explain")
                            ],
                            [
                                new LegendConnectMeaningRelationSubmission(
                                    "act", "request-target", "missing")
                            ])),
                    new LegendConnectCurriculumExampleSubmission(
                        "Please describe the alternative.",
                        new Dictionary<string, string>
                        {
                            ["predicate"] = "describe",
                            ["request_target"] = "alternative"
                        })
                ]));

        Assert.False(rejected.Succeeded);
        Assert.Equal("invalid_curriculum_examples", rejected.ErrorCode);
        Assert.Equal(0, await db.LegendLanguageMeaningNodeEvidence.CountAsync());
        Assert.Equal(0, await db.LegendLanguageMeaningRelationEvidence.CountAsync());
        Assert.Equal(0, await db.LegendLanguageMeaningRelations.CountAsync());

        var ordinary = await curriculum.SubmitFounderEnglishBatchAsync(
            new LegendConnectCurriculumBatchSubmission(
                "meaning.graph.no-implicit-adjacency",
                "Lexical sequence is not a semantic relation",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        "Please explain the option.",
                        new Dictionary<string, string>
                        {
                            ["predicate"] = "explain",
                            ["request_target"] = "option"
                        }),
                    new LegendConnectCurriculumExampleSubmission(
                        "Please describe the alternative.",
                        new Dictionary<string, string>
                        {
                            ["predicate"] = "describe",
                            ["request_target"] = "alternative"
                        })
                ]));

        Assert.True(ordinary.Succeeded, ordinary.Message);
        Assert.Equal(0, await db.LegendLanguageMeaningNodeEvidence.CountAsync());
        Assert.Equal(0, await db.LegendLanguageMeaningRelationEvidence.CountAsync());
        Assert.Equal(0, await db.LegendLanguageMeaningRelations.CountAsync());
    }

    [Fact]
    public async Task MeaningGraph_RepresentsFounderDeclaredRepeatedRolesWithoutVariationMapInference()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var curriculum = CreateCurriculum(db);

        var submitted = await curriculum.SubmitFounderEnglishBatchAsync(
            new LegendConnectCurriculumBatchSubmission(
                "meaning.graph.repeated.roles",
                "Founder-declared repeated role evidence",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        "Compare the option with the option.",
                        new Dictionary<string, string>
                        {
                            ["utterance_kind"] = "request",
                            ["function"] = "compare"
                        },
                        new LegendConnectMeaningGraphSubmission(
                            [
                                new LegendConnectMeaningNodeSubmission(
                                    "act", "predicate", "compare", "Compare", "main"),
                                new LegendConnectMeaningNodeSubmission(
                                    "left", "comparison_item", "option", "the option", "main", 1),
                                new LegendConnectMeaningNodeSubmission(
                                    "right", "comparison_item", "option", "the option", "main", 2)
                            ],
                            [
                                new LegendConnectMeaningRelationSubmission("act", "comparison-left", "left", "main"),
                                new LegendConnectMeaningRelationSubmission("act", "comparison-right", "right", "main")
                            ])),
                    new LegendConnectCurriculumExampleSubmission(
                        "Compare another option with that option.",
                        new Dictionary<string, string>
                        {
                            ["utterance_kind"] = "request",
                            ["function"] = "compare"
                        })
                ]));

        Assert.True(submitted.Succeeded, submitted.Message);
        var nodes = await db.LegendLanguageMeaningNodeEvidence
            .OrderBy(item => item.NodeKey)
            .ToListAsync();
        Assert.Equal(3, nodes.Count);
        Assert.True(new[] { "act", "left", "right" }
            .SequenceEqual(nodes.Select(item => item.NodeKey), StringComparer.Ordinal));
        Assert.Equal(2, await db.LegendLanguageMeaningRelationEvidence.CountAsync());

        var anchors = await db.LegendLanguageCompositionalAnchors
            .Where(item => item.CurriculumExampleId == nodes[0].CurriculumExampleId &&
                item.Dimension == "comparison_item")
            .OrderBy(item => item.ComponentStartTokenIndex)
            .ToListAsync();
        Assert.Equal(2, anchors.Count);
        Assert.NotEqual(anchors[0].ComponentStartTokenIndex, anchors[1].ComponentStartTokenIndex);
    }

    [Fact]
    public async Task FounderManifestMeaningGraph_UsesTheNormalDurableManifestAuthority()
    {
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        const string founderId = "d23bb7a3-0d0a-4a7a-8f2a-38ca14f50f9f";
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var configuration = Configuration();
            var registry = new LegendLanguageRegistry(db, configuration);
            var corpus = new LegendConnectCorpusService(
                db,
                registry,
                NullLogger<LegendConnectCorpusService>.Instance);
            var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
            var operations = new LegendConnectOperations(
                db,
                registry,
                corpus,
                configuration,
                curriculum: curriculum);
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = "meaning-graph-founder@legend.test",
                NormalizedEmail = "meaning-graph-founder@legend.test",
                IsActive = true
            });
            await db.SaveChangesAsync();

            var founder = new FounderLegendConnectService(
                operations,
                new AgentProfileAccessResolver(db));
            var accepted = await founder.SubmitCurriculumAsync(
                ControllerTestHelpers.BuildUser(founderId),
                new FounderLegendConnectCurriculumInput
                {
                    Manifest = """
                        @family meaning.graph.manifest | Explicit meaning graph
                        Please explain the option. | predicate=explain; request_target=option; utterance_kind=request
                        @meaning
                        @node act | predicate=explain | surface=explain | clause=main
                        @node target | request_target=option | surface=the option | clause=main
                        @edge act -> target | relation=request-target | clause=main
                        @endmeaning
                        Please describe the alternative. | predicate=describe; request_target=alternative; utterance_kind=request
                        @end
                        """
                });
            Assert.True(accepted.Succeeded, accepted.Message);

            var processor = new LegendConnectCurriculumManifestProcessor(
                db,
                curriculum,
                NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
            Assert.Equal(1, await processor.ProcessPendingAsync(1));

            Assert.Single(await db.LegendLanguageMeaningRelations.ToListAsync());
            Assert.Equal(2, await db.LegendLanguageMeaningNodeEvidence.CountAsync());
            Assert.Single(await db.LegendLanguageMeaningRelationEvidence.ToListAsync());
            Assert.Equal("Completed", await db.LegendCurriculumManifestWorkItems
                .Select(item => item.ProcessingState)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    [Fact]
    public async Task FounderMeaningGraph_RelationalSchemaPreservesCanonicalIdentitiesAcrossReplay()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_MEANING_GRAPH_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var db = new MasterAppDbContext(options);
        var curriculum = CreateCurriculum(db);

        Assert.True(await HasUniqueIndexAsync(
            db,
            "LegendLanguageMeaningNodeEvidence",
            "IX_LegendLanguageMeaningNodeEvidence_CurriculumExampleId_NodeKey"));
        Assert.True(await HasUniqueIndexAsync(
            db,
            "LegendLanguageMeaningRelations",
            "IX_LegendLanguageMeaningRelations_LanguageCode_RelationSignature"));
        Assert.True(await HasUniqueIndexAsync(
            db,
            "LegendLanguageMeaningRelationEvidence",
            "IX_LegendLanguageMeaningRelationEvidence_EvidenceIdentity"));

        for (var family = 1; family <= 3; family++)
        {
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(Family(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        db.RemoveRange(await db.LegendLanguageMeaningPrimitiveEvidence.ToListAsync());
        db.RemoveRange(await db.LegendLanguageMeaningPrimitives.ToListAsync());
        await db.SaveChangesAsync();
        var familyIds = await db.LegendCurriculumFamilies
            .Where(item => item.FamilyKey.StartsWith("meaning.graph.support."))
            .OrderBy(item => item.FamilyKey)
            .Select(item => item.Id)
            .ToListAsync();
        foreach (var familyId in familyIds)
        {
            await curriculum.ReevaluateHistoricalWorkItemAsync(
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                familyId,
                "en");
        }
        Assert.Equal(2, await db.LegendLanguageMeaningPrimitives.CountAsync());
        Assert.Equal(6, await db.LegendLanguageMeaningPrimitiveEvidence.CountAsync());

        var before = await CountsAsync(db);
        for (var family = 1; family <= 3; family++)
        {
            var replayed = await curriculum.SubmitFounderEnglishBatchAsync(Family(family));
            Assert.True(replayed.Succeeded, replayed.Message);
            Assert.True(replayed.DuplicatePrevented);
        }

        Assert.Equal(before, await CountsAsync(db));
        Assert.Equal(0, await db.LegendLanguageMeaningNodeEvidence
            .GroupBy(item => new { item.CurriculumExampleId, item.NodeKey })
            .CountAsync(group => group.Count() > 1));
        Assert.Equal(0, await db.LegendLanguageMeaningRelations
            .GroupBy(item => new { item.LanguageCode, item.RelationSignature })
            .CountAsync(group => group.Count() > 1));
        Assert.Equal(0, await db.LegendLanguageMeaningRelationEvidence
            .GroupBy(item => item.EvidenceIdentity)
            .CountAsync(group => group.Count() > 1));
        Assert.Equal(0, await db.LegendLanguageMeaningPrimitiveEvidence
            .GroupBy(item => item.MeaningNodeEvidenceId)
            .CountAsync(group => group.Count() > 1));
    }

    [Fact]
    public async Task SourceFamiliesReplay_DerivesReusablePrimitivesFromIndependentFounderGraphEvidence()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var curriculum = CreateCurriculum(db);
        for (var family = 1; family <= 3; family++)
            Assert.True((await curriculum.SubmitFounderEnglishBatchAsync(Family(family))).Succeeded);

        // Model an already-canonical Phase-1 graph at a subsequent evaluator
        // version: derived abstractions are absent, but no Founder evidence is
        // rewritten or resubmitted.
        db.RemoveRange(await db.LegendLanguageMeaningPrimitiveEvidence.ToListAsync());
        db.RemoveRange(await db.LegendLanguageMeaningPrimitives.ToListAsync());
        await db.SaveChangesAsync();

        var familyIds = await db.LegendCurriculumFamilies
            .Where(item => item.FamilyKey.StartsWith("meaning.graph.support."))
            .OrderBy(item => item.FamilyKey)
            .Select(item => item.Id)
            .ToListAsync();
        foreach (var familyId in familyIds)
        {
            await curriculum.ReevaluateHistoricalWorkItemAsync(
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                familyId,
                "en");
        }

        var primitives = await db.LegendLanguageMeaningPrimitives
            .OrderBy(item => item.SemanticDimension)
            .ToListAsync();
        Assert.Equal(2, primitives.Count);
        Assert.All(primitives, primitive =>
        {
            Assert.Equal(3, primitive.SupportCount);
            Assert.Equal(3, primitive.IndependentSourceCount);
            Assert.Equal("Supported", primitive.MaturityState);
            Assert.False(primitive.IsProductionEligible);
            Assert.Equal("FounderApproved", primitive.Provenance);
        });
        Assert.Equal(6, await db.LegendLanguageMeaningPrimitiveEvidence.CountAsync());

        var before = await PrimitiveCountsAsync(db);
        foreach (var familyId in familyIds)
        {
            await curriculum.ReevaluateHistoricalWorkItemAsync(
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                familyId,
                "en");
        }
        Assert.Equal(before, await PrimitiveCountsAsync(db));
    }

    [Fact]
    public async Task ReusableMeaningAnalysis_ComposesIndependentlySupportedComponentsWithoutStoredSentenceLookup()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var curriculum = CreateCurriculum(db);
        for (var family = 1; family <= 3; family++)
            Assert.True((await curriculum.SubmitFounderEnglishBatchAsync(Family(family))).Succeeded);

        const string heldOut = "Could you explain the option clearly today?";
        Assert.False(await db.LegendLanguageTextUnits.AnyAsync(item => item.Text == heldOut));
        var analysis = await curriculum.AnalyzeReusableMeaningGraphAsync("en", heldOut);

        Assert.True(analysis.IsComposed);
        Assert.Equal("meaning_graph_observational_composed", analysis.ReasonCode);
        Assert.Equal(2, analysis.Nodes.Count);
        Assert.Single(analysis.Relations);
        Assert.Contains(analysis.Nodes, item => item.SemanticDimension == "predicate" && item.SemanticValue == "explain");
        Assert.Contains(analysis.Nodes, item => item.SemanticDimension == "request_target" && item.SemanticValue == "option");
        Assert.Contains("could", analysis.UnknownSurfaceComponents);
        Assert.Contains("today", analysis.UnknownSurfaceComponents);
    }

    private static LegendConnectCurriculumBatchSubmission Family(int family) =>
        new(
            $"meaning.graph.support.{family}",
            "Explicit request construction evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    family switch
                    {
                        1 => "Please explain the option clearly.",
                        2 => "Could you explain the option now?",
                        _ => "Would you explain the option today?"
                    },
                    new Dictionary<string, string>
                    {
                        ["predicate"] = "explain",
                        ["request_target"] = "option",
                        ["utterance_kind"] = "request"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "act", "predicate", "explain", "explain", "main"),
                            new LegendConnectMeaningNodeSubmission(
                                "target", "request_target", "option", "the option", "main")
                        ],
                        [
                            new LegendConnectMeaningRelationSubmission(
                                "act", "request-target", "target", "main")
                        ])),
                new LegendConnectCurriculumExampleSubmission(
                    family switch
                    {
                        1 => "Please describe the alternative clearly.",
                        2 => "Could you describe the alternative now?",
                        _ => "Would you describe the alternative today?"
                    },
                    new Dictionary<string, string>
                    {
                        ["predicate"] = "describe",
                        ["request_target"] = "alternative",
                        ["utterance_kind"] = "request"
                    })
            ]);

    private static async Task<(int Nodes, int Relations, int Evidence, int Anchors)> CountsAsync(
        MasterAppDbContext db) =>
        (
            await db.LegendLanguageMeaningNodeEvidence.CountAsync(),
            await db.LegendLanguageMeaningRelations.CountAsync(),
            await db.LegendLanguageMeaningRelationEvidence.CountAsync(),
            await db.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null));

    private static async Task<(int Primitives, int Evidence)> PrimitiveCountsAsync(MasterAppDbContext db) =>
        (await db.LegendLanguageMeaningPrimitives.CountAsync(),
         await db.LegendLanguageMeaningPrimitiveEvidence.CountAsync());

    private static async Task<bool> HasUniqueIndexAsync(
        MasterAppDbContext db,
        string table,
        string index)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close)
            await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT CAST(CASE WHEN EXISTS (
                    SELECT 1
                    FROM sys.indexes AS [index]
                    INNER JOIN sys.tables AS [table] ON [table].object_id = [index].object_id
                    WHERE [table].name = @tableName
                      AND [index].name = @indexName
                      AND [index].is_unique = 1
                ) THEN 1 ELSE 0 END AS int);
                """;
            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = "@tableName";
            tableParameter.Value = table;
            command.Parameters.Add(tableParameter);
            var indexParameter = command.CreateParameter();
            indexParameter.ParameterName = "@indexName";
            indexParameter.Value = index;
            command.Parameters.Add(indexParameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
        }
        finally
        {
            if (close)
                await connection.CloseAsync();
        }
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
                ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-test",
                ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Test language",
                ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Test language"
            })
            .Build();
}
