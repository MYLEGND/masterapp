using System;
using System.Collections.Generic;
using System.Linq;
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
/// High-level regression coverage for the one dynamic language-intelligence
/// path. These tests intentionally exercise canonical source assets,
/// curriculum, alignments, quality evidence, correction, and structural
/// maturity rather than target-language rules or helper-only heuristics.
/// </summary>
public sealed class LegendConnectAutonomousLanguageUnderstandingTests
{
    [Fact]
    public async Task KnownSemanticComponentsFlagAConflictingProviderObservationWithoutGuessingTargetWords()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db, futureLanguageOnly: true);
        var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(new LegendConnectCurriculumBatchSubmission(
            "semantic.components",
            "Controlled semantic preservation",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "She must not read before dinner.",
                    new Dictionary<string, string>
                    {
                        ["polarity"] = "negative",
                        ["modality"] = "obligation",
                        ["predicate"] = "read"
                    }),
                new LegendConnectCurriculumExampleSubmission(
                    "She may read after dinner.",
                    new Dictionary<string, string>
                    {
                        ["polarity"] = "affirmative",
                        ["modality"] = "permission",
                        ["predicate"] = "read"
                    })
            ]));
        Assert.True(submitted.Succeeded, submitted.Message);
        var source = await db.LegendLanguageTextUnits.SingleAsync(item => item.Text == "She must not read before dinner.");
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await fixture.Registry.GetOrCreateEnabledPairAsync("en", "x-test"));
        var providerTarget = Unit("x-test", "provider candidate", "ProviderDerived");
        var verifiedTarget = Unit("x-test", "human verified target", "FounderApproved");
        var provider = ProviderAlignment(pair, source, providerTarget);
        var verified = HumanAlignment(pair, source, verifiedTarget);
        db.AddRange(providerTarget, verifiedTarget, provider, verified);
        await db.SaveChangesAsync();

        await fixture.Intelligence.EvaluateProviderObservationAsync(provider.Id);

        var anchors = await db.LegendLanguageCompositionalAnchors
            .Where(item => item.TextUnitId == source.Id && item.SemanticSignature != null)
            .Select(item => item.SemanticSignature!)
            .Distinct()
            .ToListAsync();
        var componentEvidence = await db.LegendTranslationQualityEvidence
            .Where(item => item.ObservedAlignmentId == provider.Id &&
                item.ReasonCode == "known_semantic_component_not_realized")
            .ToListAsync();
        Assert.Equal(3, anchors.Count);
        Assert.Equal(anchors.Order(), componentEvidence.Select(item => item.SemanticSignature).Order());
        Assert.All(componentEvidence, item => Assert.Equal("Insufficient", item.Signal));
        Assert.False((await db.LegendTranslationAlignments.SingleAsync(item => item.Id == provider.Id)).HumanVerified);
    }

    [Fact]
    public async Task ProviderRetriesAndHistoricalReevaluationStayIdempotentAndDoNotCreateTrustedStructure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db, futureLanguageOnly: true);
        var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(Batch("historical.provider"));
        Assert.True(submitted.Succeeded, submitted.Message);
        var source = await db.LegendLanguageTextUnits.SingleAsync(item => item.Text == "I form one.");
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await fixture.Registry.GetOrCreateEnabledPairAsync("en", "x-test"));
        var target = Unit("x-test", "candidate one", "ProviderDerived");
        var provider = ProviderAlignment(pair, source, target);
        db.AddRange(target, provider);
        await db.SaveChangesAsync();

        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(25);
        await fixture.Intelligence.ReevaluateHistoricalProviderObservationsAsync(25);
        var first = new
        {
            Evidence = await db.LegendTranslationQualityEvidence.CountAsync(),
            Examples = await db.LegendCurriculumExamples.CountAsync(),
            Structural = await db.LegendLanguageStructuralEvidence.CountAsync()
        };
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(25);
        await fixture.Intelligence.ReevaluateHistoricalProviderObservationsAsync(25);
        var second = new
        {
            Evidence = await db.LegendTranslationQualityEvidence.CountAsync(),
            Examples = await db.LegendCurriculumExamples.CountAsync(),
            Structural = await db.LegendLanguageStructuralEvidence.CountAsync()
        };

        Assert.Equal(first, second);
        Assert.Equal("Observation", (await db.LegendTranslationAlignments.SingleAsync(item => item.Id == provider.Id)).QualityState);
        Assert.Null(await fixture.Intelligence.TryGetTrustedExactMemoryAsync("en", "x-test", source.Text));
        Assert.Contains(await db.LegendTranslationQualityEvidence.ToListAsync(), item =>
            item.ObservedAlignmentId == provider.Id && item.Signal == "Insufficient");
    }

    [Fact]
    public async Task ThreeIndependentHumanExamplesMatureOnlyTheirRegisteredDirectionalPair()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db, futureLanguageOnly: true);
        var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(Batch("future.independent"));
        Assert.True(submitted.Succeeded, submitted.Message);
        var sourceUnits = await db.LegendLanguageTextUnits
            .Where(item => item.LanguageCode == "en" && item.Text.StartsWith("I form") || item.Text.StartsWith("She form"))
            .OrderBy(item => item.Text)
            .ToListAsync();
        Assert.Equal(4, sourceUnits.Count);
        var targets = new[] { "ka lo", "ki lu", "ko la", "ku le" };
        for (var index = 0; index < sourceUnits.Count; index++)
        {
            var result = await fixture.Operations.SubmitFounderKnowledgeAsync(
                "founder",
                new LegendConnectKnowledgeSubmission(
                    "en", sourceUnits[index].Text, "x-test", targets[index], null, null, null, "ignored"),
                reusableSourceTextUnitId: sourceUnits[index].Id);
            Assert.True(result.Succeeded, result.Message);
        }

        var pairKey = "en:x-test";
        var pattern = await db.LegendLanguageStructuralPatterns
            .Where(item => item.PairKey == pairKey && item.LanguageCode == "x-test" && item.VariationDimension == "person")
            .OrderByDescending(item => item.IndependentSourceCount)
            .FirstAsync();
        Assert.True(pattern.SupportCount >= 3);
        Assert.True(pattern.IndependentSourceCount >= 3);
        Assert.True(pattern.HumanVerifiedSupportCount >= 3);
        Assert.Equal(0, pattern.ProviderOnlySupportCount);
        Assert.Equal("Supported", pattern.MaturityState);
        Assert.True(await fixture.Curriculum.TryValidatePatternAsync(pattern.Id));
        Assert.False((await db.LegendLanguageStructuralPatterns.SingleAsync(item => item.Id == pattern.Id)).IsProductionEligible);
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-test", "A future-language sentence."));
        Assert.DoesNotContain(await db.LegendLanguageStructuralPatterns.ToListAsync(), item =>
            item.PairKey == "en:ht" && item.CurriculumFamilyId == pattern.CurriculumFamilyId);
    }

    [Fact]
    public async Task HumanCorrectionsRetainProviderHistoryAndRecalculateRelatedPairMaturity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db, futureLanguageOnly: true);
        var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(Batch("correction.recalculation"));
        Assert.True(submitted.Succeeded, submitted.Message);
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await fixture.Registry.GetOrCreateEnabledPairAsync("en", "x-test"));
        var sources = await db.LegendLanguageTextUnits
            .Where(item => item.LanguageCode == "en" &&
                (item.Text.StartsWith("I form") || item.Text.StartsWith("She forms")))
            .OrderBy(item => item.Text)
            .ToListAsync();
        var providerAlignments = new List<LegendTranslationAlignment>();
        foreach (var source in sources)
        {
            var target = Unit("x-test", "provider " + source.NormalizedHash[..8], "ProviderDerived");
            var alignment = ProviderAlignment(pair, source, target);
            db.AddRange(target, alignment);
            providerAlignments.Add(alignment);
        }
        await db.SaveChangesAsync();
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(25);
        Assert.All(await db.LegendLanguageStructuralPatterns
            .Where(item => item.PairKey == pair.PairKey).ToListAsync(), item => Assert.Equal(0, item.SupportCount));

        var correctedTargets = new[] { "qa lo", "qi lu", "qo la", "qu le" };
        for (var index = 0; index < providerAlignments.Count; index++)
        {
            var source = sources.Single(item => item.Id == providerAlignments[index].SourceTextUnitId);
            var correction = await fixture.Operations.CorrectFounderKnowledgeAsync(
                "founder",
                providerAlignments[index].Id,
                new LegendConnectKnowledgeSubmission(
                    "en", source.Text, "x-test", correctedTargets[index], null, null, null, "ignored"));
            Assert.True(correction.Succeeded, correction.Message);
        }

        var pattern = await db.LegendLanguageStructuralPatterns
            .Where(item => item.PairKey == pair.PairKey && item.LanguageCode == "x-test" && item.VariationDimension == "person")
            .OrderByDescending(item => item.IndependentSourceCount)
            .FirstAsync();
        Assert.Equal("Supported", pattern.MaturityState);
        Assert.True(pattern.HumanVerifiedSupportCount >= 3);
        Assert.True(await fixture.Curriculum.TryValidatePatternAsync(pattern.Id));
        Assert.All(await db.LegendTranslationAlignments
            .Where(item => providerAlignments.Select(provider => provider.Id).Contains(item.Id))
            .ToListAsync(), item => Assert.NotNull(item.SupersededUtc));
        Assert.Equal(providerAlignments.Count, await db.LegendTranslationQualityEvidence
            .CountAsync(item => item.ReasonCode == "human_verified_directional_correction"));
    }

    [Fact]
    public async Task IndependentFounderBatchesAndFamiliesAccumulateOneControlledPropositionWithoutOpeningProduction()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db, futureLanguageOnly: true);
        var submissions = new[]
        {
            new LegendConnectCurriculumBatchSubmission(
                "controlled.assertion.one", "Controlled assertion",
                [
                    new LegendConnectCurriculumExampleSubmission("I confirm the schedule.", Polarity("affirmative")),
                    new LegendConnectCurriculumExampleSubmission("I do not confirm the schedule.", Polarity("negative"))
                ]),
            new LegendConnectCurriculumBatchSubmission(
                "controlled.assertion.two", "Controlled assertion",
                [
                    new LegendConnectCurriculumExampleSubmission("We accept the request.", Polarity("affirmative")),
                    new LegendConnectCurriculumExampleSubmission("We do not accept the request.", Polarity("negative"))
                ]),
            new LegendConnectCurriculumBatchSubmission(
                "controlled.assertion.three", "Controlled assertion",
                [
                    new LegendConnectCurriculumExampleSubmission("They recognize the change.", Polarity("affirmative")),
                    new LegendConnectCurriculumExampleSubmission("They do not recognize the change.", Polarity("negative"))
                ])
        };
        foreach (var submission in submissions)
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(submission)).Succeeded);

        var pattern = await db.LegendLanguageStructuralPatterns.SingleAsync(item =>
            item.PairKey == string.Empty && item.LanguageCode == "en" &&
            item.VariationDimension == "polarity" && item.SupersededUtc == null);
        var evidence = await db.LegendLanguageStructuralEvidence
            .Where(item => item.StructuralPatternId == pattern.Id && item.SupersededUtc == null)
            .ToListAsync();
        Assert.Equal(3, evidence.Select(item => item.CurriculumFamilyId).Distinct().Count());
        Assert.Equal(3, pattern.SupportCount);
        Assert.True(pattern.IndependentSourceCount >= 3);
        Assert.Equal(3, pattern.HumanVerifiedSupportCount);
        Assert.Equal(0, pattern.ProviderOnlySupportCount);
        Assert.Equal("Supported", pattern.MaturityState);
        Assert.False(pattern.IsProductionEligible);
        Assert.Equal(3, await db.LegendLanguageContextRelationships.CountAsync(item =>
            item.RelationshipKind == "ControlledVariation" &&
            item.ContextCategory == "ControlledVariation:polarity" &&
            item.Provenance == "FounderApproved" && item.SupersededUtc == null));

        var first = new
        {
            PatternCount = await db.LegendLanguageStructuralPatterns.CountAsync(),
            EvidenceCount = await db.LegendLanguageStructuralEvidence.CountAsync(),
            ContextCount = await db.LegendLanguageContextRelationships.CountAsync()
        };
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        var second = new
        {
            PatternCount = await db.LegendLanguageStructuralPatterns.CountAsync(),
            EvidenceCount = await db.LegendLanguageStructuralEvidence.CountAsync(),
            ContextCount = await db.LegendLanguageContextRelationships.CountAsync()
        };
        Assert.Equal(first, second);

        Assert.True(await fixture.Curriculum.TryValidatePatternAsync(pattern.Id));
        var validated = await db.LegendLanguageStructuralPatterns.SingleAsync(item => item.Id == pattern.Id);
        Assert.Equal("Validated", validated.MaturityState);
        Assert.False(validated.IsProductionEligible);
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-test", "An unobserved statement."));
    }

    [Fact]
    public async Task LexicalSenseAndPhrasalAnchorsRemainSeparateEvidenceInsteadOfUniversalWordMappings()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var first = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(new LegendConnectCurriculumBatchSubmission(
            "lexical.watch.timepiece", null,
            [
                new LegendConnectCurriculumExampleSubmission("I watch time.", LexicalVariations("timepiece", "watch")),
                new LegendConnectCurriculumExampleSubmission("She watches time.", LexicalVariations("timepiece", "watches"))
            ]));
        var second = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(new LegendConnectCurriculumBatchSubmission(
            "lexical.watch.observe", null,
            [
                new LegendConnectCurriculumExampleSubmission("I watch birds.", LexicalVariations("observe", "watch")),
                new LegendConnectCurriculumExampleSubmission("She watches birds.", LexicalVariations("observe", "watches"))
            ]));
        var phrase = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(new LegendConnectCurriculumBatchSubmission(
            "lexical.phrase.give-up", null,
            [
                new LegendConnectCurriculumExampleSubmission("I will give up.", LexicalVariations("abandon", "give up")),
                new LegendConnectCurriculumExampleSubmission("She will give up.", LexicalVariations("abandon", "give up"))
            ]));
        Assert.True(first.Succeeded && second.Succeeded && phrase.Succeeded);

        var senses = await db.LegendLanguageCompositionalAnchors
            .Where(item => item.Dimension == "lexical-sense" && item.SemanticSignature != null)
            .Select(item => new { item.Value, item.SemanticSignature })
            .Distinct()
            .ToListAsync();
        Assert.Contains(senses, item => item.Value == "timepiece");
        Assert.Contains(senses, item => item.Value == "observe");
        Assert.NotEqual(
            senses.Single(item => item.Value == "timepiece").SemanticSignature,
            senses.Single(item => item.Value == "observe").SemanticSignature);
        Assert.Contains(await db.LegendLanguageCompositionalAnchors.ToListAsync(), item =>
            item.Value == "give up" && item.ComponentLength == 2 && item.LexemeId != null);
    }

    private static LegendConnectCurriculumBatchSubmission Batch(string familyKey) => new(
        familyKey,
        "Dynamic pair evidence",
        [
            new LegendConnectCurriculumExampleSubmission("I form one.", Variations("first", "one")),
            new LegendConnectCurriculumExampleSubmission("She forms two.", Variations("third", "two")),
            new LegendConnectCurriculumExampleSubmission("I form three.", Variations("first", "three")),
            new LegendConnectCurriculumExampleSubmission("She forms four.", Variations("third", "four"))
        ]);

    private static IReadOnlyDictionary<string, string> Variations(string person, string @object) =>
        new Dictionary<string, string>
        {
            ["person"] = person,
            ["object"] = @object,
            ["tense"] = "present"
        };

    private static IReadOnlyDictionary<string, string> LexicalVariations(string sense, string semanticUnit) =>
        new Dictionary<string, string>
        {
            ["lexical-sense"] = sense,
            ["semantic-unit"] = semanticUnit
        };

    private static IReadOnlyDictionary<string, string> Polarity(string value) =>
        new Dictionary<string, string> { ["polarity"] = value };

    private static QualityFixture CreateFixture(MasterAppDbContext db, bool futureLanguageOnly = false)
    {
        var configuration = Configuration(futureLanguageOnly);
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum, intelligence: intelligence);
        return new QualityFixture(registry, intelligence, curriculum, operations);
    }

    private static IConfiguration Configuration(bool futureLanguageOnly) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = futureLanguageOnly ? "x-test" : "ht",
            ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = futureLanguageOnly ? "Future test language" : "Haitian Creole",
            ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = futureLanguageOnly ? "Future test language" : "Kreyòl ayisyen"
        }).Build();

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
        LegendLanguageTextUnit target) => new()
    {
        Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
        Provider = "AzureTranslator", Provenance = "ProviderDerived", QualityState = "Observation", ObservationCount = 1
    };

    private static LegendTranslationAlignment HumanAlignment(
        LegendLanguagePairSnapshot pair,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) => new()
    {
        Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
        Provider = "FounderApproved", Provenance = "FounderApproved", Confidence = 1m,
        QualityState = "Verified", HumanVerified = true, ObservationCount = 1
    };

    private sealed record QualityFixture(
        LegendLanguageRegistry Registry,
        LegendConnectTranslationIntelligence Intelligence,
        LegendConnectCurriculumService Curriculum,
        LegendConnectOperations Operations);
}
