using System;
using System.Collections;
using System.Linq;
using System.Reflection;
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

public sealed class LegendSourceSlotDeclarationQueryTests
{
    [Fact]
    public async Task AdmissionUnion_PreservesBothRoutesAndExampleIdentityWithoutDuplicateDeclarations()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anchorOnly = fixture.Add(1);
        var lexicalOnly = fixture.Add(2, lexical: true);
        var both = fixture.Add(3, lexical: true, repeatedOccurrences: true);
        fixture.Add(4); // Neither route.
        fixture.Add(5, sharedUnit: anchorOnly.Unit); // Anchor membership must not spread to sibling examples.
        var lexicalSibling = fixture.Add(6, sharedUnit: lexicalOnly.Unit);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(new[] { anchorOnly.Transition.Id, lexicalOnly.Transition.Id, both.Transition.Id, lexicalSibling.Transition.Id },
            fixture.Read([anchorOnly.Source.Id, both.Source.Id], [Fixture.SignalHash]));
        Assert.Equal(new[] { lexicalOnly.Transition.Id, both.Transition.Id, lexicalSibling.Transition.Id },
            fixture.Read([], [Fixture.SignalHash]));
        Assert.Equal(new[] { anchorOnly.Transition.Id, both.Transition.Id },
            fixture.Read([anchorOnly.Source.Id, both.Source.Id], []));
        Assert.Empty(fixture.Read([], []));
    }

    [Fact]
    public async Task GlobalOrderAndOverflowSentinel_AreAppliedAfterAdmissionUnion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var items = Enumerable.Range(1, 140).Reverse().Select(index =>
            fixture.Add(index, lexical: index % 2 == 0, repeatedOccurrences: index % 4 == 0)).ToArray();
        await fixture.Db.SaveChangesAsync();
        var anchors = items.Where(item => item.Ordinal % 2 != 0 || item.Ordinal % 4 == 0)
            .Select(item => item.Source.Id).ToArray();
        var ids = fixture.Read(anchors, [Fixture.SignalHash]);
        Assert.Equal(129, ids.Length); // 128 retained templates plus unchanged overflow sentinel.
        Assert.Equal(Enumerable.Range(1, 129).Select(Fixture.Identity), ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public async Task GlobalBound_DoesNotDiscardEligibleRowsBeforeEligibilityFiltering()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rejected = Enumerable.Range(1, 140).Select(index => fixture.Add(index, lexical: true)).ToArray();
        foreach (var item in rejected) item.Transition.IsHumanVerifiedSupport = false;
        var accepted = fixture.Add(141, lexical: true);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(new[] { accepted.Transition.Id }, fixture.Read([], [Fixture.SignalHash]));
    }

    [Theory]
    [InlineData("result_retired")]
    [InlineData("result_language")]
    [InlineData("result_provenance")]
    [InlineData("source_retired")]
    [InlineData("source_derived")]
    [InlineData("source_language")]
    [InlineData("source_provenance")]
    [InlineData("family_provenance")]
    [InlineData("unit_ineligible")]
    [InlineData("unit_provenance")]
    [InlineData("transition_source_language")]
    [InlineData("transition_result_language")]
    [InlineData("transition_retired")]
    [InlineData("transition_provenance")]
    [InlineData("transition_unverified")]
    [InlineData("transition_unsupported")]
    [InlineData("frame_without_variable")]
    public async Task AdmissionUnion_DoesNotBypassExistingEligibility(string rejectedCondition)
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Add(1, lexical: true);
        switch (rejectedCondition)
        {
            case "result_retired": item.Result.SupersededUtc = DateTime.UtcNow; break;
            case "result_language": item.Result.LanguageCode = "fr"; break;
            case "result_provenance": item.Result.Provenance = "SystemValidatedMachine"; break;
            case "source_retired": item.Source.SupersededUtc = DateTime.UtcNow; break;
            case "source_derived": item.Source.DerivedFromCurriculumExampleId = item.Result.Id; break;
            case "source_language": item.Source.LanguageCode = "fr"; break;
            case "source_provenance": item.Source.Provenance = "SystemValidatedMachine"; break;
            case "family_provenance": item.Family.Provenance = "SystemValidatedMachine"; break;
            case "unit_ineligible": item.Unit.IsTrainingEligible = false; break;
            case "unit_provenance": item.Unit.Provenance = "SystemValidatedMachine"; break;
            case "transition_source_language": item.Transition.SourceLanguageCode = "fr"; break;
            case "transition_result_language": item.Transition.ResultLanguageCode = "fr"; break;
            case "transition_retired": item.Transition.SupersededUtc = DateTime.UtcNow; break;
            case "transition_provenance": item.Transition.Provenance = "SystemValidatedMachine"; break;
            case "transition_unverified": item.Transition.IsHumanVerifiedSupport = false; break;
            case "transition_unsupported": item.Transition.ContributionState = "Insufficient"; break;
            case "frame_without_variable": item.Transition.SourceSemanticFrame = "fixed=value"; break;
        }
        await fixture.Db.SaveChangesAsync();
        Assert.Empty(fixture.Read([item.Source.Id], [Fixture.SignalHash]));
    }

    [Fact]
    public async Task ContradictoryDeclarationsRemainVisibleForDownstreamRejection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Add(1, lexical: true);
        item.Transition.ContributionState = "Contradictory";
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(new[] { item.Transition.Id }, fixture.Read([], [Fixture.SignalHash]));
    }

    private sealed record Evidence(int Ordinal, LegendCurriculumFamily Family, LegendLanguageTextUnit Unit,
        LegendCurriculumExample Source, LegendCurriculumExample Result, LegendSemanticTransitionEvidence Transition);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly LegendConnectCurriculumService _curriculum;
        private readonly LegendLanguageLexeme _lexeme;
        internal MasterAppDbContext Db { get; }
        internal static readonly string SignalHash = LegendLanguageIdentity.TextHash("signal");
        internal static Guid Identity(int ordinal) => Guid.Parse($"00000000-0000-0000-0000-{ordinal:D12}");

        private Fixture(SqliteConnection connection, MasterAppDbContext db)
        {
            _connection = connection;
            Db = db;
            var configuration = new ConfigurationBuilder().Build();
            var registry = new LegendLanguageRegistry(db, configuration);
            _curriculum = new LegendConnectCurriculumService(db, registry,
                new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance));
            _lexeme = new LegendLanguageLexeme { LanguageCode = "en", NormalizedHash = SignalHash, SurfaceForm = "signal" };
            Db.Add(_lexeme);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        internal Evidence Add(int ordinal, bool lexical = false, bool repeatedOccurrences = false,
            LegendLanguageTextUnit? sharedUnit = null)
        {
            var family = new LegendCurriculumFamily { FamilyKey = "query-proof-" + ordinal };
            var unit = sharedUnit ?? Unit("source-" + ordinal);
            var resultUnit = Unit("result-" + ordinal);
            var source = new LegendCurriculumExample { CurriculumFamilyId = family.Id, TextUnitId = unit.Id, LanguageCode = "en" };
            var result = new LegendCurriculumExample { CurriculumFamilyId = family.Id, TextUnitId = resultUnit.Id, LanguageCode = "en" };
            var transition = new LegendSemanticTransitionEvidence
            {
                Id = Identity(ordinal), TransitionSignature = LegendLanguageIdentity.TextHash("transition-" + ordinal),
                SourceSemanticFrameSignature = LegendLanguageIdentity.TextHash("source-frame"),
                ResultSemanticFrameSignature = LegendLanguageIdentity.TextHash("result-frame"),
                SourceSemanticFrame = "value=$slot", ResultSemanticFrame = "result=$slot",
                SourceLanguageCode = "en", ResultLanguageCode = "en",
                SourceCurriculumExampleId = source.Id, ResultCurriculumExampleId = result.Id,
                IndependentSourceIdentity = "query-proof-" + ordinal,
                ContributionState = "Supported", IsHumanVerifiedSupport = true
            };
            Db.AddRange(family, resultUnit, source, result, transition);
            if (sharedUnit is null) Db.Add(unit);
            if (lexical)
                for (var index = 0; index < (repeatedOccurrences ? 2 : 1); index++)
                    Db.Add(new LegendLanguageLexicalOccurrence
                    { TextUnitId = unit.Id, LexemeId = _lexeme.Id, TokenIndex = index, CharacterOffset = index * 7, CharacterLength = 6 });
            return new Evidence(ordinal, family, unit, source, result, transition);
        }

        private static LegendLanguageTextUnit Unit(string value) => new()
        {
            LanguageCode = "en", Text = value, StoragePartition = "source-slot-query-proof",
            NormalizedHash = LegendLanguageIdentity.TextHash(value), IsTrainingEligible = true,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };

        internal Guid[] Read(Guid[] exampleIds, string[] hashes)
        {
            var method = typeof(LegendConnectCurriculumService).GetMethod("QuerySourceSlotDeclarations",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var query = Assert.IsAssignableFrom<IEnumerable>(method.Invoke(_curriculum, ["en", exampleIds, hashes]));
            return query.Cast<object>().Select(row => Assert.IsType<Guid>(row.GetType().GetProperty("Id")!.GetValue(row))).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
