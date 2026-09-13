using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendIndexedRetrievalMultiplicityTests
{
    [Theory]
    [InlineData("amber birch cedar")]
    [InlineData("amber amber birch cedar")]
    public async Task RelationalIndexedRetrieval_TranslatesSingleAndMixedMultiplicityGroups(string request)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var configuration = new ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var method = typeof(LegendConnectCurriculumService).GetMethod("LoadIndexedSemanticAnchorIdsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        foreach (var requireMeaning in new[] { false, true })
        {
            var task = Assert.IsAssignableFrom<Task>(method.Invoke(curriculum,
                ["en", request.Split(' ').Select(LegendLanguageIdentity.TextHash).ToArray(), CancellationToken.None, requireMeaning]));
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Guid>>(
                result.GetType().GetProperty("AnchorIds")!.GetValue(result)));
            Assert.False(Assert.IsType<bool>(result.GetType().GetProperty("BoundExceeded")!.GetValue(result)));
        }
    }

    [Theory]
    [InlineData("amber birch cedar", "amber birch cedar", -1, true)]
    [InlineData("amber amber birch birch", "amber amber birch birch", -1, true)]
    [InlineData("amber amber birch cedar", "amber amber birch cedar", -1, true)]
    [InlineData("amber amber birch cedar", "amber birch cedar", -1, true)]
    [InlineData("amber birch cedar", "amber amber birch", -1, false)]
    [InlineData("amber amber birch cedar", "amber birch birch cedar", -1, false)]
    [InlineData("amber birch cedar", "amber birch outsider", -1, false)]
    [InlineData("amber birch cedar", "amber birch cedar", 1, false)]
    public async Task IndexedRetrieval_PreservesPerHashMultiplicityAndCompleteSpan(
        string request, string span, int missingPosition, bool expected)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = new ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var tokens = span.Split(' ');
        var lexemes = tokens.Distinct(StringComparer.Ordinal).ToDictionary(token => token, token =>
            new LegendLanguageLexeme
            {
                LanguageCode = "en", NormalizedHash = LegendLanguageIdentity.TextHash(token),
                SurfaceForm = token, Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            });
        db.LegendLanguageLexemes.AddRange(lexemes.Values);
        var unit = new LegendLanguageTextUnit
        {
            LanguageCode = "en", NormalizedHash = LegendLanguageIdentity.TextHash(span), Text = span,
            StoragePartition = "retrieval-multiplicity", IsTrainingEligible = true,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        db.LegendLanguageTextUnits.Add(unit);
        var signature = LegendLanguageIdentity.TextHash("retrieval-multiplicity-span");
        var familyId = Guid.NewGuid();
        var exampleId = Guid.NewGuid();
        var anchor = new LegendLanguageCompositionalAnchor
        {
            LanguageCode = "en", TextUnitId = unit.Id, LexemeId = lexemes[tokens[0]].Id,
            ComponentStartTokenIndex = 0, ComponentLength = tokens.Length,
            CurriculumFamilyId = familyId, CurriculumExampleId = exampleId,
            Dimension = "retrieval_test", Value = "span", SemanticSignature = signature,
            AnchorSignature = LegendLanguageIdentity.TextHash("retrieval-multiplicity-anchor"),
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        db.LegendLanguageCompositionalAnchors.Add(anchor);
        for (var index = 0; index < tokens.Length; index++)
        {
            if (index == missingPosition) continue;
            db.LegendLanguageLexicalOccurrences.Add(new LegendLanguageLexicalOccurrence
            {
                TextUnitId = unit.Id, LexemeId = lexemes[tokens[index]].Id, TokenIndex = index,
                CharacterOffset = index * 6, CharacterLength = tokens[index].Length
            });
        }
        db.LegendLanguageMeaningNodeEvidence.Add(new LegendLanguageMeaningNodeEvidence
        {
            LanguageCode = "en", CurriculumFamilyId = familyId, CurriculumExampleId = exampleId,
            CompositionalAnchorId = anchor.Id, NodeKey = "span", SemanticSignature = signature,
            SemanticDimension = "retrieval_test", SemanticValue = "span",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        });
        db.LegendLanguageMeaningPrimitives.Add(new LegendLanguageMeaningPrimitive
        {
            LanguageCode = "en", SemanticSignature = signature,
            SemanticDimension = "retrieval_test", SemanticValue = "span", MaturityState = "Validated",
            SupportCount = 1, IndependentSourceCount = 1, HumanVerifiedSupportCount = 1,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        });
        await db.SaveChangesAsync();

        // Invoke the existing indexed authority directly; assert anchor membership,
        // not a semantic answer or an implementation-mirroring substitute query.
        var method = typeof(LegendConnectCurriculumService).GetMethod("LoadIndexedSemanticAnchorIdsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        foreach (var requireMeaning in new[] { false, true })
        {
            var task = Assert.IsAssignableFrom<Task>(method.Invoke(curriculum,
                ["en", request.Split(' ').Select(LegendLanguageIdentity.TextHash).ToArray(), CancellationToken.None, requireMeaning]));
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var anchorIds = Assert.IsAssignableFrom<IReadOnlyList<Guid>>(
                result.GetType().GetProperty("AnchorIds")!.GetValue(result));
            Assert.Equal(expected, anchorIds.Contains(anchor.Id));
            Assert.False(Assert.IsType<bool>(result.GetType().GetProperty("BoundExceeded")!.GetValue(result)));
        }
    }
}
