using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectMeaningLanguageCandidateTests
{
    [Fact]
    public async Task IndependentLanguageMeanings_RemainCandidatesWhileEmptyLanguagesAreExcluded()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en", "ht", "fr");
        var curriculum = CreateCurriculum(db);
        foreach (var language in new[] { "en", "ht" })
        {
            var taught = await curriculum.SubmitFounderBatchAsync(AtomicTeaching(language), sourceLanguageCode: language);
            Assert.True(taught.Succeeded, taught.Message);
        }
        const string request = "signal";
        Assert.False(await db.LegendLanguageTextUnits.AnyAsync(unit => unit.Text == request));
        var shortlist = await curriculum.GetReusableMeaningLanguageCandidatesAsync(["fr", "ht", "en"], request);
        Assert.True(shortlist.IsComplete, shortlist.ReasonCode);
        Assert.Equal(new[] { "ht", "en" }, shortlist.CandidateLanguageCodes);
        foreach (var language in shortlist.CandidateLanguageCodes)
            Assert.True((await curriculum.AnalyzeReusableMeaningGraphAsync(language, request)).IsComposed);
        Assert.False((await curriculum.AnalyzeReusableMeaningGraphAsync("fr", request)).IsComposed);
        var unknown = await curriculum.GetReusableMeaningLanguageCandidatesAsync(["en", "ht", "fr"], "unobservedword");
        Assert.True(unknown.IsComplete, unknown.ReasonCode);
        Assert.Empty(unknown.CandidateLanguageCodes);
    }

    [Fact]
    public async Task ExactFounderEndpoint_RemainsCandidateWithoutLexicalOccurrenceIndex()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en", "ht");
        var curriculum = CreateCurriculum(db);
        var taught = await curriculum.SubmitFounderBatchAsync(AtomicTeaching("en"));
        Assert.True(taught.Succeeded, taught.Message);
        db.RemoveRange(await db.LegendLanguageLexicalOccurrences.ToArrayAsync());
        await db.SaveChangesAsync();
        var shortlist = await curriculum.GetReusableMeaningLanguageCandidatesAsync(["ht", "en"], "The en context contains signal.");
        Assert.True(shortlist.IsComplete, shortlist.ReasonCode);
        Assert.Equal("en", Assert.Single(shortlist.CandidateLanguageCodes));
        var graph = await curriculum.AnalyzeReusableMeaningGraphAsync("en", "The en context contains signal.");
        Assert.True(graph.IsComposed, graph.ReasonCode);
    }

    [Fact]
    public async Task UnseenNumericSlots_RemainCandidatesThroughLiteralOccurrencesWithoutKnownOperands()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en", "ht", "fr");
        var curriculum = CreateCurriculum(db);
        await LegendConnectComputedLanguageEndToEndContractTests.SeedTeachingAsync(curriculum);
        const string request = "Compute 147 against 26.";
        Assert.False(await db.LegendLanguageTextUnits.AnyAsync(unit => unit.Text == request));
        Assert.False(await db.LegendLanguageMeaningNodeEvidence.AnyAsync(node =>
            node.SemanticValue == "147" || node.SemanticValue == "26"));
        var shortlist = await curriculum.GetReusableMeaningLanguageCandidatesAsync(["en", "ht", "fr"], request);
        Assert.True(shortlist.IsComplete, shortlist.ReasonCode);
        Assert.Equal("en", Assert.Single(shortlist.CandidateLanguageCodes));
        var graph = await curriculum.AnalyzeReusableMeaningGraphAsync("en", request);
        Assert.True(graph.IsComposed, graph.ReasonCode);
        Assert.All(graph.Nodes, node => Assert.NotNull(node.SourceSlotBinding));
    }

    [Fact]
    public async Task ActiveMachineLexicalOccurrence_PreventsExclusionWithoutGrantingMeaning()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en", "ht");
        var curriculum = CreateCurriculum(db);
        // A lexical observation carries no semantic answer or production
        // eligibility. The exclusion query must still allow its full review.
        var lexeme = new LegendLanguageLexeme
        {
            LanguageCode = "ht", SurfaceForm = "observed",
            NormalizedHash = LegendLanguageIdentity.TextHash("observed"),
            Provenance = LegendConnectKnowledgeProvenance.SystemValidatedMachine
        };
        db.Add(lexeme);
        db.Add(new LegendLanguageLexicalOccurrence { LexemeId = lexeme.Id, TextUnitId = Guid.NewGuid(), CharacterLength = 8 });
        await db.SaveChangesAsync();
        var shortlist = await curriculum.GetReusableMeaningLanguageCandidatesAsync(["en", "ht"], "observed");
        Assert.True(shortlist.IsComplete, shortlist.ReasonCode);
        Assert.Equal("ht", Assert.Single(shortlist.CandidateLanguageCodes));
        Assert.False((await curriculum.AnalyzeReusableMeaningGraphAsync("ht", "observed")).IsComposed);
    }

    [Fact]
    public async Task UnknownCodesAndProcessingBounds_RetainEveryRequestedLanguage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en", "ht");
        var curriculum = CreateCurriculum(db);
        foreach (var (languages, request) in new[]
        {
            (new[] { "en", "missing" }, "signal"),
            (new[] { "en", "en-GB" }, "signal"),
            (new[] { "en", "ht" }, new string('a', 8193)),
            (new[] { "en", "ht" }, string.Join(' ', Enumerable.Repeat("token", 513))),
            (Enumerable.Repeat("en", 513).ToArray(), "signal")
        })
        {
            var shortlist = await curriculum.GetReusableMeaningLanguageCandidatesAsync(languages, request);
            Assert.False(shortlist.IsComplete);
            Assert.Equal(languages, shortlist.CandidateLanguageCodes);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            curriculum.GetReusableMeaningLanguageCandidatesAsync(["en", "ht"], "signal", new CancellationToken(true)));
    }

    [Fact]
    public async Task RelationalCandidateQuery_ExecutesWithEmptyGovernedEvidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en", "ht");
        var shortlist = await CreateCurriculum(db).GetReusableMeaningLanguageCandidatesAsync(["en", "ht"], "signal");
        Assert.True(shortlist.IsComplete, shortlist.ReasonCode);
        Assert.Empty(shortlist.CandidateLanguageCodes);
    }

    private static LegendConnectCurriculumBatchSubmission AtomicTeaching(string language) =>
        new("meaning.language-candidates." + language, "An independently taught atomic observation",
            [new("The " + language + " context contains signal.", new Dictionary<string, string> { ["observation"] = "signal" },
                new([new("signal", "observation", "signal", "signal")], [])),
             new("A signal appears in the " + language + " observation.", new Dictionary<string, string> { ["observation"] = "signal" },
                new([new("signal", "observation", "signal", "signal")], []))]);

    private static LegendConnectCurriculumService CreateCurriculum(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["LegendConnect:CorpusAcquisition:Enabled"] = "false" }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        return new(db, registry, corpus);
    }
}
