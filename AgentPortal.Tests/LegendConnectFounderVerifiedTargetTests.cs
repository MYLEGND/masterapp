using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// Proves that the Founder verified-target entry mode is only a resolver over
/// the existing canonical source, correction, memory, and curriculum paths.
/// No test creates a parallel source or target authority.
/// </summary>
public sealed class LegendConnectFounderVerifiedTargetTests
{
    [Fact]
    public async Task ExistingCanonicalSource_GainsOneTrustedTargetWithoutDuplicatingSource_AndExactMemoryReusesIt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var source = await SeedExistingFounderSourceAsync(fixture, db, "The package should arrive today.");
        var sourceCountBefore = await db.LegendLanguageTextUnits.CountAsync(item => item.LanguageCode == "en");
        var reusableTarget = Unit("x-test", "paket la rive jodi a", "ProviderDerived");
        db.LegendLanguageTextUnits.Add(reusableTarget);
        await db.SaveChangesAsync();
        var targetCountBefore = await db.LegendLanguageTextUnits.CountAsync(item => item.LanguageCode == "x-test");

        var first = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-test", (1, source.Text, "paket la rive jodi a")));

        Assert.True(first.Succeeded, first.Message);
        var row = Assert.Single(first.Rows);
        Assert.Equal("FounderTargetAdded", row.Status);
        Assert.Equal(source.Id, row.SourceTextUnitId);
        Assert.Equal(reusableTarget.Id, row.TargetTextUnitId);
        Assert.Equal(sourceCountBefore, await db.LegendLanguageTextUnits.CountAsync(item => item.LanguageCode == "en"));
        Assert.Equal(targetCountBefore, await db.LegendLanguageTextUnits.CountAsync(item => item.LanguageCode == "x-test"));
        var alignment = Assert.Single(await db.LegendTranslationAlignments.ToListAsync());
        Assert.True(alignment.HumanVerified);
        Assert.Equal("FounderApproved", alignment.Provenance);

        var second = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-test", (1, source.Text, "paket la rive jodi a")));
        Assert.True(second.Succeeded, second.Message);
        Assert.Equal("AlreadyVerified", Assert.Single(second.Rows).Status);
        Assert.Single(await db.LegendTranslationAlignments.ToListAsync());
        Assert.Empty(await db.LegendTranslationQualityEvidence.ToListAsync());

        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            fixture.Registry,
            new TranslationCapacityAuthority(db, fixture.Configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: fixture.Intelligence,
            structuralComposition: fixture.Curriculum);
        var translated = await router.TranslateAsync(source.Text, "x-test", "en");

        Assert.True(translated.Succeeded);
        Assert.Equal("paket la rive jodi a", translated.TranslatedText);
        Assert.Equal("LegendConnectTranslationMemory", translated.Provider);
        Assert.Equal(0, provider.TranslateCalls);
    }

    [Fact]
    public async Task MatchingProviderIsVerified_WrongHistoricalProviderIsCorrected_AndLaterFounderCorrectionPreservesLineage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var source = await SeedExistingFounderSourceAsync(fixture, db, "A trusted source sentence.");
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await fixture.Registry.GetOrCreateEnabledPairAsync("en", "x-test"));
        var matchingTarget = Unit("x-test", "target already correct", "ProviderDerived");
        var matchingProvider = ProviderAlignment(pair, source, matchingTarget, DateTime.UtcNow.AddDays(-30));
        db.AddRange(matchingTarget, matchingProvider);
        await db.SaveChangesAsync();

        var verified = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-test", (1, source.Text, matchingTarget.Text)));
        Assert.True(verified.Succeeded, verified.Message);
        Assert.Equal("ExistingTargetVerified", Assert.Single(verified.Rows).Status);
        var approvedProvider = await db.LegendTranslationAlignments.SingleAsync(item => item.Id == matchingProvider.Id);
        Assert.True(approvedProvider.HumanVerified);
        Assert.Equal("ProviderDerived", approvedProvider.Provenance);

        var sourceTwo = await SeedExistingFounderSourceAsync(fixture, db, "A historical source sentence.");
        var wrongTarget = Unit("x-test", "historical provider error", "ProviderDerived");
        var wrongProvider = ProviderAlignment(pair, sourceTwo, wrongTarget, DateTime.UtcNow.AddDays(-90));
        db.AddRange(wrongTarget, wrongProvider);
        await db.SaveChangesAsync();
        var submissionsBefore = await db.LegendFounderTrainingSubmissions.CountAsync();

        var corrected = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-test", (1, sourceTwo.Text, "Founder-approved historical correction")));
        Assert.True(corrected.Succeeded, corrected.Message);
        Assert.Equal("ProviderTargetCorrected", Assert.Single(corrected.Rows).Status);
        Assert.Equal(submissionsBefore, await db.LegendFounderTrainingSubmissions.CountAsync());
        var retiredProvider = await db.LegendTranslationAlignments.SingleAsync(item => item.Id == wrongProvider.Id);
        Assert.Equal("ProviderDerived", retiredProvider.Provenance);
        Assert.NotNull(retiredProvider.SupersededUtc);
        Assert.Contains(await db.LegendTranslationQualityEvidence.ToListAsync(), item =>
            item.ObservedAlignmentId == wrongProvider.Id &&
            item.ReasonCode == "human_verified_directional_correction" &&
            item.ResolutionState == "Corrected");

        var later = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-test", (1, sourceTwo.Text, "A later Founder-approved correction")));
        Assert.True(later.Succeeded, later.Message);
        Assert.Equal("FounderTargetCorrected", Assert.Single(later.Rows).Status);
        var active = await db.LegendTranslationAlignments
            .Where(item => item.PairKey == pair.PairKey && item.SourceTextUnitId == sourceTwo.Id && item.SupersededUtc == null)
            .SingleAsync();
        var activeTarget = await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == active.TargetTextUnitId);
        Assert.Equal("A later Founder-approved correction", activeTarget.Text);
        Assert.True(active.HumanVerified);
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-test", "A novel sentence."));
    }

    [Fact]
    public async Task UnmatchedAndAmbiguousSourcesFailClosedWithoutAttachingTargetEvidence()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var unmatched = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-test", (1, "No canonical source exists.", "No target may attach.")));
        Assert.False(unmatched.Succeeded);
        Assert.Equal("Unmatched", Assert.Single(unmatched.Rows).Status);
        Assert.Empty(await db.LegendTranslationAlignments.ToListAsync());
        Assert.Empty(await db.LegendLanguageTextUnits.Where(item => item.LanguageCode == "x-test").ToListAsync());

        var duplicateOne = Unit("en", "An ambiguous canonical source.", "FounderApproved");
        var duplicateTwo = Unit("en", "An ambiguous canonical source.", "FounderApproved");
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = "ambiguous.source.identity",
            SemanticCategory = "Test",
            Provenance = "FounderApproved"
        };
        db.AddRange(
            duplicateOne,
            duplicateTwo,
            family,
            new LegendCurriculumExample
            {
                Id = Guid.NewGuid(),
                CurriculumFamilyId = family.Id,
                TextUnitId = duplicateOne.Id,
                LanguageCode = "en",
                Provenance = "FounderApproved"
            },
            new LegendCurriculumExample
            {
                Id = Guid.NewGuid(),
                CurriculumFamilyId = family.Id,
                TextUnitId = duplicateTwo.Id,
                LanguageCode = "en",
                Provenance = "FounderApproved"
            });
        await db.SaveChangesAsync();

        var ambiguous = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-test", (1, duplicateOne.Text, "No target may attach.")));
        Assert.False(ambiguous.Succeeded);
        Assert.Equal("Ambiguous", Assert.Single(ambiguous.Rows).Status);
        Assert.Empty(await db.LegendTranslationAlignments.ToListAsync());
        Assert.Empty(await db.LegendLanguageTextUnits.Where(item => item.LanguageCode == "x-test").ToListAsync());
    }

    [Fact]
    public async Task VerifiedTargetsFlowIntoExistingTargetLanguageStructuralEvidence_WithoutOpeningComposition()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var curriculum = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(new LegendConnectCurriculumBatchSubmission(
            "verified.targets.structural",
            "Controlled variation",
            [
                new LegendConnectCurriculumExampleSubmission("I inspect reports.", Variations("agent", "first")),
                new LegendConnectCurriculumExampleSubmission("You inspect reports.", Variations("agent", "second"))
            ]));
        Assert.True(curriculum.Succeeded, curriculum.Message);
        var sources = await db.LegendLanguageTextUnits
            .Where(item => item.LanguageCode == "en" &&
                (item.Text == "I inspect reports." || item.Text == "You inspect reports."))
            .OrderBy(item => item.Text)
            .ToListAsync();

        var result = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-test",
                (1, sources[0].Text, "za inspect reports"),
                (2, sources[1].Text, "zi inspect reports")));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.FounderTargetAddedCount);

        var evidence = await db.LegendLanguageStructuralEvidence
            .Where(item => item.PairKey == "en:x-test" && item.LanguageCode == "x-test" && item.SupersededUtc == null)
            .ToListAsync();
        Assert.NotEmpty(evidence);
        Assert.All(evidence, item =>
        {
            Assert.True(item.IsHumanVerifiedSupport);
            Assert.Equal("FounderApproved", item.Provenance);
        });
        Assert.Empty(await db.LegendLanguageStructuralEvidence
            .Where(item => item.PairKey == "en:x-alt" && item.LanguageCode == "x-alt")
            .ToListAsync());
        Assert.All(await db.LegendLanguageStructuralPatterns.ToListAsync(), item => Assert.False(item.IsProductionEligible));
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-test", "A novel formulation."));
    }

    [Fact]
    public async Task SameCanonicalSourceCanAttachDistinctDirectionalPairsWithoutCrossPairReuse()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var source = await SeedExistingFounderSourceAsync(fixture, db, "Pair-scoped canonical source.");

        var first = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-test", (1, source.Text, "x-test target")));
        var second = await fixture.Operations.SubmitFounderVerifiedTargetsAsync(
            "founder",
            Batch("en", "x-alt", (1, source.Text, "x-alt target")));

        Assert.True(first.Succeeded, first.Message);
        Assert.True(second.Succeeded, second.Message);
        var alignments = await db.LegendTranslationAlignments
            .Where(item => item.SourceTextUnitId == source.Id && item.SupersededUtc == null)
            .OrderBy(item => item.PairKey)
            .ToListAsync();
        Assert.Equal(["en:x-alt", "en:x-test"], alignments.Select(item => item.PairKey));
        Assert.NotNull(await fixture.Intelligence.TryGetTrustedExactMemoryAsync("en", "x-test", source.Text));
        Assert.NotNull(await fixture.Intelligence.TryGetTrustedExactMemoryAsync("en", "x-alt", source.Text));
    }

    private static LegendConnectVerifiedTargetSubmission Batch(
        string sourceLanguage,
        string targetLanguage,
        params (int Row, string Source, string Target)[] rows) => new(
            sourceLanguage,
            targetLanguage,
            rows.Select(item => new LegendConnectVerifiedTargetRow(item.Row, item.Source, item.Target)).ToList(),
            "Founder verified target",
            null,
            null);

    private static async Task<LegendLanguageTextUnit> SeedExistingFounderSourceAsync(
        Fixture fixture,
        MasterAppDbContext db,
        string text)
    {
        var seeded = await fixture.Operations.SubmitFounderKnowledgeAsync(
            "founder",
            new LegendConnectKnowledgeSubmission("en", text, null, null, "Existing canonical source", null, null, "FounderApproved"));
        Assert.True(seeded.Succeeded, seeded.Message);
        return await db.LegendLanguageTextUnits.SingleAsync(item =>
            item.LanguageCode == "en" && item.NormalizedHash == LegendLanguageIdentity.TextHash(text));
    }

    private static Fixture CreateFixture(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
                ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
                ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0",
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-test",
                ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Test target",
                ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Test target",
                ["LegendConnect:LanguageRegistry:Baseline:2:Code"] = "x-alt",
                ["LegendConnect:LanguageRegistry:Baseline:2:Name"] = "Alternate target",
                ["LegendConnect:LanguageRegistry:Baseline:2:NativeName"] = "Alternate target"
            })
            .Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance,
            intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var founderTraining = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum,
            founderTrainingIngestion: founderTraining,
            intelligence: intelligence);
        return new Fixture(configuration, registry, intelligence, curriculum, operations);
    }

    private static IReadOnlyDictionary<string, string> Variations(string dimension, string value) =>
        new Dictionary<string, string> { [dimension] = value };

    private static LegendLanguageTextUnit Unit(string languageCode, string text, string provenance) => new()
    {
        Id = Guid.NewGuid(),
        LanguageCode = languageCode,
        StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
        NormalizedHash = LegendLanguageIdentity.TextHash(text),
        Text = LegendLanguageIdentity.NormalizeText(text),
        Provenance = provenance,
        IsTrainingEligible = true
    };

    private static LegendTranslationAlignment ProviderAlignment(
        LegendLanguagePairSnapshot pair,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target,
        DateTime createdUtc) => new()
    {
        Id = Guid.NewGuid(),
        PairKey = pair.PairKey,
        SourceTextUnitId = source.Id,
        TargetTextUnitId = target.Id,
        Provider = "AzureTranslator",
        Provenance = "ProviderDerived",
        QualityState = "Observation",
        ObservationCount = 1,
        CreatedUtc = createdUtc,
        UpdatedUtc = createdUtc
    };

    private sealed record Fixture(
        IConfiguration Configuration,
        LegendLanguageRegistry Registry,
        LegendConnectTranslationIntelligence Intelligence,
        LegendConnectCurriculumService Curriculum,
        LegendConnectOperations Operations);

    private sealed class RecordingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            return Task.FromResult(new TranslationProviderResult(true, "Unexpected provider result", sourceLanguage, ProviderName));
        }
    }
}
