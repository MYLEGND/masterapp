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
/// Command-5 holdout coverage. The synthetic language and its surfaces live
/// only in the fixture; production evaluates supplied registry evidence with
/// no language-name or grammar branch.
/// </summary>
public sealed class LegendConnectCompositionalUnderstandingTests
{
    [Fact]
    public async Task HeldOutCompositionUsesActiveFounderEvidenceAndCorrectionPropagationWithoutProducingOutput()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedSupportedCompositionAsync(fixture);

        var heldOut = HeldOutRequest();
        var exactTargetExists = await db.LegendTranslationAlignments
            .Where(item => item.PairKey == "en:x-test" && item.HumanVerified && item.SupersededUtc == null)
            .Join(db.LegendLanguageTextUnits, alignment => alignment.TargetTextUnitId, unit => unit.Id,
                (_, unit) => unit.Text)
            .AnyAsync(text => text == heldOut.ProposedTargetText);
        Assert.False(exactTargetExists);

        var exact = await fixture.Curriculum.EvaluateShadowCompositionAsync(ExactObservedRequest());
        var supported = await fixture.Curriculum.EvaluateShadowCompositionAsync(heldOut);
        var polarityOnly = await fixture.Curriculum.EvaluateShadowCompositionAsync(PolarityOnlyRequest());
        var missingComponent = await fixture.Curriculum.EvaluateShadowCompositionAsync(MissingComponentRequest());
        var semanticLoss = await fixture.Curriculum.EvaluateShadowCompositionAsync(SemanticLossRequest());
        var missingRelationship = await fixture.Curriculum.EvaluateShadowCompositionAsync(MissingRelationshipRequest());

        Assert.Equal(LegendShadowCompositionCapability.ExactObserved, exact.State);
        Assert.True(exact.IsExactObserved);
        Assert.Equal(LegendShadowCompositionCapability.SupportedForShadowEvaluation, supported.State);
        Assert.False(supported.IsExactObserved);
        Assert.False(supported.IsProductionEligible);
        Assert.Equal(LegendShadowCompositionCapability.SupportedForShadowEvaluation, polarityOnly.State);
        Assert.Equal(LegendShadowCompositionCapability.InsufficientEvidence, missingComponent.State);
        Assert.Contains("known_semantic_component_not_realized", missingComponent.Reasons);
        Assert.Equal(LegendShadowCompositionCapability.InsufficientEvidence, semanticLoss.State);
        Assert.Contains("known_semantic_component_not_realized", semanticLoss.Reasons);
        Assert.Equal(LegendShadowCompositionCapability.InsufficientEvidence, missingRelationship.State);

        var beforeReplay = Counts(await db.LegendLanguageStructuralPatterns.ToListAsync(),
            await db.LegendLanguageStructuralRelationships.ToListAsync(),
            await db.LegendLanguageStructuralEvidence.ToListAsync());
        _ = await fixture.Curriculum.EvaluateShadowCompositionAsync(heldOut);
        _ = await fixture.Curriculum.EvaluateShadowCompositionAsync(heldOut);
        Assert.Equal(beforeReplay, Counts(await db.LegendLanguageStructuralPatterns.ToListAsync(),
            await db.LegendLanguageStructuralRelationships.ToListAsync(),
            await db.LegendLanguageStructuralEvidence.ToListAsync()));

        var conflict = await AddConflictingAgentFamilyAsync(fixture);
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        var contradicted = await fixture.Curriculum.EvaluateShadowCompositionAsync(heldOut);
        var unrelatedDuringConflict = await fixture.Curriculum.EvaluateShadowCompositionAsync(PolarityOnlyRequest());
        Assert.Equal(LegendShadowCompositionCapability.Contradicted, contradicted.State);
        Assert.Equal(LegendShadowCompositionCapability.SupportedForShadowEvaluation, unrelatedDuringConflict.State);

        var correction = await fixture.Operations.CorrectFounderKnowledgeAsync(
            "founder",
            conflict.ReversedAlignmentId,
            new LegendConnectKnowledgeSubmission(
                "en", conflict.ReversedSourceText, "x-test", "zi affirmative audit reports",
                "Training", null, null, "FounderApproved"));
        Assert.True(correction.Succeeded, correction.Message);
        await AddFounderTargetAnchorsAsync(db, "x-test");
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);

        var restored = await fixture.Curriculum.EvaluateShadowCompositionAsync(heldOut);
        var unrelatedAfterCorrection = await fixture.Curriculum.EvaluateShadowCompositionAsync(PolarityOnlyRequest());
        Assert.Equal(LegendShadowCompositionCapability.SupportedForShadowEvaluation, restored.State);
        Assert.Equal(LegendShadowCompositionCapability.SupportedForShadowEvaluation, unrelatedAfterCorrection.State);

        // Shadow capability remains non-production because this method evaluates
        // a caller-supplied construction; it does not itself authorize serving.
        Assert.False(restored.IsProductionEligible);

        // Some directional structural facts may now earn production eligibility,
        // but the formulation boundary remains closed in Phase 3.
        Assert.Contains(
            await db.LegendLanguageStructuralRelationships.ToListAsync(),
            item => item.PairKey == "en:x-test" &&
                    item.IsProductionEligible);
        Assert.Null(
            await fixture.Curriculum.TryComposeAsync(
                "en",
                "x-test",
                heldOut.ProposedTargetText));
    }

    [Fact]
    public async Task ProviderOnlyStructuralRowsCannotAuthorizeShadowComposition()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedSupportedCompositionAsync(fixture);
        var heldOut = HeldOutRequest() with
        {
            RequiredRelationships =
            [
                new LegendShadowCompositionRelationshipRequirement("agent", "I", "You"),
                new LegendShadowCompositionRelationshipRequirement("provider-only", "first", "second")
            ]
        };
        var family = await db.LegendCurriculumFamilies.FirstAsync();
        var example = await db.LegendCurriculumExamples.FirstAsync(item => item.LanguageCode == "x-test");
        var providerPattern = new LegendLanguageStructuralPattern
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, PairKey = "en:x-test", LanguageCode = "x-test",
            VariationDimension = "provider-only", PropositionSignature = ControlledPropositionSignature("provider-only", "first", "second"),
            RealizationSignature = "provider-only", MaturityState = "Supported", SupportCount = 3,
            IndependentSourceCount = 3, HumanVerifiedSupportCount = 0, ProviderOnlySupportCount = 3,
            Confidence = 0m, Provenance = "ProviderDerived"
        };
        var providerRelationship = new LegendLanguageStructuralRelationship
        {
            Id = Guid.NewGuid(), PairKey = "en:x-test", LanguageCode = "x-test", VariationDimension = "provider-only",
            RelationshipSignature = "provider-only-test", AnchorLayoutSignature = "provider-only-test",
            MaturityState = "Supported", SupportCount = 3, IndependentSourceCount = 3,
            HumanVerifiedSupportCount = 0, ProviderOnlySupportCount = 3, Confidence = 0m,
            Provenance = "ProviderDerived"
        };
        db.AddRange(providerPattern, providerRelationship,
            new LegendLanguageStructuralEvidence
            {
                Id = Guid.NewGuid(), StructuralPatternId = providerPattern.Id,
                StructuralRelationshipId = providerRelationship.Id, StructuralRelationshipContributionState = "Supported",
                CurriculumFamilyId = family.Id, PairKey = "en:x-test", LanguageCode = "x-test", VariationDimension = "provider-only",
                BaselineCurriculumExampleId = example.Id, ComparedCurriculumExampleId = example.Id,
                BaselineVariationValue = "first", ComparedVariationValue = "second", EvidenceSignature = "provider-only",
                BaselineComponentSignature = "provider-only", ComparedComponentSignature = "provider-only",
                IndependentSourceIdentity = "provider-only", ContributionState = "Insufficient",
                IsHumanVerifiedSupport = false, Provenance = "ProviderDerived"
            });
        await db.SaveChangesAsync();

        var result = await fixture.Curriculum.EvaluateShadowCompositionAsync(heldOut);

        Assert.Equal(LegendShadowCompositionCapability.InsufficientEvidence, result.State);
        Assert.Contains(result.Reasons, reason => reason.StartsWith("required_proposition_not_supported:provider-only", StringComparison.Ordinal));
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-test", heldOut.ProposedTargetText));
    }

    private static async Task SeedSupportedCompositionAsync(CompositionFixture fixture)
    {
        foreach (var (familyKey, predicate, @object) in new[]
        {
            ("composition.observe.records", "observe", "records"),
            ("composition.review.reports", "review", "reports"),
            ("composition.combine.notes", "combine", "notes"),
            ("composition.inspect.packets", "inspect", "packets")
        })
        {
            var batch = new LegendConnectCurriculumBatchSubmission(familyKey, "Controlled composition evidence",
            [
                Source("I", "affirmative", predicate, @object),
                Source("You", "affirmative", predicate, @object),
                Source("I", "negative", predicate, @object),
                Source("You", "negative", predicate, @object)
            ]);
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch)).Succeeded);
            foreach (var (agent, polarity) in new[]
                { ("I", "affirmative"), ("You", "affirmative"), ("I", "negative"), ("You", "negative") })
            {
                var sourceText = SourceText(agent, polarity, predicate, @object);
                var sourceId = await fixture.Db.LegendLanguageTextUnits
                    .Where(item => item.LanguageCode == "en" && item.Text == sourceText)
                    .Select(item => item.Id)
                    .SingleAsync();
                var target = $"{AgentSurface(agent)} {polarity} {predicate} {@object}";
                var result = await fixture.Operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
                    "en", sourceText, "x-test", target, "Training", null, null, "FounderApproved"),
                    reusableSourceTextUnitId: sourceId);
                Assert.True(result.Succeeded, result.Message);
            }
        }
        await AddFounderTargetAnchorsAsync(fixture.Db, "x-test");
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
    }

    private static async Task<ConflictFixture> AddConflictingAgentFamilyAsync(CompositionFixture fixture)
    {
        var batch = new LegendConnectCurriculumBatchSubmission("composition.correctable.conflict", "Controlled composition evidence",
        [
            Source("I", "affirmative", "audit", "reports"),
            Source("You", "affirmative", "audit", "reports")
        ]);
        Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch)).Succeeded);
        var leftSource = SourceText("I", "affirmative", "audit", "reports");
        var rightSource = SourceText("You", "affirmative", "audit", "reports");
        var leftId = await fixture.Db.LegendLanguageTextUnits.Where(item => item.Text == leftSource).Select(item => item.Id).SingleAsync();
        var rightId = await fixture.Db.LegendLanguageTextUnits.Where(item => item.Text == rightSource).Select(item => item.Id).SingleAsync();
        var left = await fixture.Operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", leftSource, "x-test", "za affirmative audit reports", "Training", null, null, "FounderApproved"),
            reusableSourceTextUnitId: leftId);
        var reversed = await fixture.Operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", rightSource, "x-test", "reports audit affirmative zi", "Training", null, null, "FounderApproved"),
            reusableSourceTextUnitId: rightId);
        Assert.True(left.Succeeded, left.Message);
        Assert.True(reversed.Succeeded, reversed.Message);
        await AddFounderTargetAnchorsAsync(fixture.Db, "x-test");
        return new ConflictFixture(rightSource, reversed.AlignmentId!.Value);
    }

    private static async Task AddFounderTargetAnchorsAsync(MasterAppDbContext db, string languageCode)
    {
        var targets = await (
            from example in db.LegendCurriculumExamples
            join unit in db.LegendLanguageTextUnits on example.TextUnitId equals unit.Id
            where example.LanguageCode == languageCode && example.DerivedFromCurriculumExampleId != null &&
                example.SupersededUtc == null && unit.Provenance == "FounderApproved"
            select new { Example = example, Unit = unit }
        ).ToListAsync();
        foreach (var target in targets)
        {
            if (await db.LegendLanguageCompositionalAnchors.AnyAsync(item =>
                item.CurriculumExampleId == target.Example.Id && item.SupersededUtc == null))
            {
                continue;
            }
            var variations = await db.LegendCurriculumExampleVariations
                .Where(item => item.CurriculumExampleId == target.Example.Id)
                .ToDictionaryAsync(item => item.Dimension, item => item.Value);
            var tokens = target.Unit.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var variation in variations)
            {
                var surface = variation.Key == "agent" ? AgentSurface(variation.Value) : variation.Value.ToLowerInvariant();
                var index = Array.FindIndex(tokens, item => string.Equals(item, surface, StringComparison.OrdinalIgnoreCase));
                Assert.True(index >= 0, $"Expected surface '{surface}' in '{target.Unit.Text}'.");
                var hash = LegendLanguageIdentity.TextHash(surface);
                var lexeme = await db.LegendLanguageLexemes.SingleAsync(item =>
                    item.LanguageCode == languageCode && item.NormalizedHash == hash);
                db.LegendLanguageCompositionalAnchors.Add(new LegendLanguageCompositionalAnchor
                {
                    Id = Guid.NewGuid(), LanguageCode = languageCode, TextUnitId = target.Unit.Id, LexemeId = lexeme.Id,
                    ComponentStartTokenIndex = index, ComponentLength = 1,
                    CurriculumFamilyId = target.Example.CurriculumFamilyId, CurriculumExampleId = target.Example.Id,
                    Dimension = variation.Key, Value = variation.Value,
                    SemanticSignature = SemanticSignature(variation.Key, variation.Value),
                    AnchorSignature = LegendLanguageIdentity.TextHash($"composition-anchor|{target.Example.Id:D}|{variation.Key}"),
                    Provenance = "FounderApproved"
                });
            }
        }
        await db.SaveChangesAsync();
    }

    private static LegendConnectCurriculumExampleSubmission Source(string agent, string polarity, string predicate, string @object) =>
        new(SourceText(agent, polarity, predicate, @object), new Dictionary<string, string>
        {
            ["agent"] = agent,
            ["polarity"] = polarity,
            ["predicate"] = predicate,
            ["object"] = @object
        });

    private static string SourceText(string agent, string polarity, string predicate, string @object) =>
        $"{agent} {polarity} {predicate} {@object}.";

    private static string AgentSurface(string value) => value switch
    {
        "I" => "za",
        "You" => "zi",
        _ => throw new InvalidOperationException("The test fixture has an unknown controlled agent value.")
    };

    private static LegendShadowCompositionRequest HeldOutRequest() => new(
        "en", "x-test", "za affirmative combine packets",
        Components("I", "affirmative", "combine", "packets"),
        [
            new LegendShadowCompositionRelationshipRequirement("agent", "I", "You"),
            new LegendShadowCompositionRelationshipRequirement("polarity", "affirmative", "negative")
        ]);

    private static LegendShadowCompositionRequest PolarityOnlyRequest() => HeldOutRequest() with
    {
        RequiredRelationships =
        [new LegendShadowCompositionRelationshipRequirement("polarity", "affirmative", "negative")]
    };

    private static LegendShadowCompositionRequest ExactObservedRequest() => new(
        "en", "x-test", "za affirmative observe records",
        Components("I", "affirmative", "observe", "records"),
        [new LegendShadowCompositionRelationshipRequirement("agent", "I", "You")]);

    private static LegendShadowCompositionRequest MissingComponentRequest() => new(
        "en", "x-test", "za affirmative combine unknown",
        [
            new LegendShadowCompositionComponent("agent", "I", "za", 0),
            new LegendShadowCompositionComponent("polarity", "affirmative", "affirmative", 1),
            new LegendShadowCompositionComponent("predicate", "combine", "combine", 2),
            new LegendShadowCompositionComponent("object", "packets", "unknown", 3)
        ],
        [new LegendShadowCompositionRelationshipRequirement("agent", "I", "You")]);

    private static LegendShadowCompositionRequest SemanticLossRequest() => new(
        "en", "x-test", "za affirmative combine packets",
        [
            new LegendShadowCompositionComponent("agent", "I", "za", 0),
            new LegendShadowCompositionComponent("polarity", "affirmative", "affirmative", 1),
            new LegendShadowCompositionComponent("predicate", "combine", "combine", 2),
            new LegendShadowCompositionComponent("object", "records", "packets", 3)
        ],
        [new LegendShadowCompositionRelationshipRequirement("agent", "I", "You")]);

    private static LegendShadowCompositionRequest MissingRelationshipRequest() => HeldOutRequest() with
    {
        RequiredRelationships =
        [new LegendShadowCompositionRelationshipRequirement("agent", "I", "She")]
    };

    private static IReadOnlyList<LegendShadowCompositionComponent> Components(
        string agent,
        string polarity,
        string predicate,
        string @object) =>
    [
        new("agent", agent, AgentSurface(agent), 0),
        new("polarity", polarity, polarity, 1),
        new("predicate", predicate, predicate, 2),
        new("object", @object, @object, 3)
    ];

    private static string SemanticSignature(string dimension, string value) =>
        LegendLanguageIdentity.TextHash($"semantic|{dimension.Trim().ToLowerInvariant()}|{value.Trim().ToLowerInvariant()}");

    private static string ControlledPropositionSignature(string dimension, string first, string second)
    {
        var ordered = new[] { first.Trim().ToLowerInvariant(), second.Trim().ToLowerInvariant() }
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        return LegendLanguageIdentity.TextHash($"controlled-proposition|{dimension.Trim().ToLowerInvariant()}|{ordered[0]}|{ordered[1]}");
    }

    private static (int Patterns, int Relationships, int Evidence) Counts(
        IReadOnlyCollection<LegendLanguageStructuralPattern> patterns,
        IReadOnlyCollection<LegendLanguageStructuralRelationship> relationships,
        IReadOnlyCollection<LegendLanguageStructuralEvidence> evidence) =>
        (patterns.Count, relationships.Count, evidence.Count);

    private static CompositionFixture CreateFixture(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-test",
            ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Synthetic registry language",
            ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Synthetic registry language"
        }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum, intelligence: intelligence);
        return new CompositionFixture(db, registry, curriculum, operations);
    }

    private sealed record CompositionFixture(
        MasterAppDbContext Db,
        LegendLanguageRegistry Registry,
        LegendConnectCurriculumService Curriculum,
        LegendConnectOperations Operations);

    private sealed record ConflictFixture(string ReversedSourceText, Guid ReversedAlignmentId);
}
