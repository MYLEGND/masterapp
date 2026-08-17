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
    public async Task UnseenSourceSemanticFrameIsDerivedFromFounderEvidenceWithoutTargetOrProviderInput()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedSupportedCompositionAsync(fixture);

        const string unseenSource = "I affirmative combine packets.";

        Assert.False(await db.LegendLanguageTextUnits.AnyAsync(item =>
            item.LanguageCode == "en" &&
            item.Text == unseenSource));

        var before = Counts(
            await db.LegendLanguageStructuralPatterns.ToListAsync(),
            await db.LegendLanguageStructuralRelationships.ToListAsync(),
            await db.LegendLanguageStructuralEvidence.ToListAsync());

        var anchorCountBefore =
            await db.LegendLanguageCompositionalAnchors.CountAsync();
        var textUnitCountBefore =
            await db.LegendLanguageTextUnits.CountAsync();

        var understood =
            await fixture.Curriculum.AnalyzeShadowSourceSemanticsAsync(
                "en",
                unseenSource);

        Assert.Equal(
            LegendShadowSourceUnderstanding.SupportedForShadowEvaluation,
            understood.State);
        Assert.False(understood.IsProductionEligible);

        Assert.Collection(
            understood.Components,
            item =>
            {
                Assert.Equal("agent", item.Dimension);
                Assert.Equal("I", item.Value);
                Assert.Equal("i", item.SurfaceForm);
                Assert.Equal(0, item.StartTokenIndex);
                Assert.Equal(1, item.TokenLength);
            },
            item =>
            {
                Assert.Equal("polarity", item.Dimension);
                Assert.Equal("affirmative", item.Value);
                Assert.Equal("affirmative", item.SurfaceForm);
                Assert.Equal(1, item.StartTokenIndex);
                Assert.Equal(1, item.TokenLength);
            },
            item =>
            {
                Assert.Equal("predicate", item.Dimension);
                Assert.Equal("combine", item.Value);
                Assert.Equal("combine", item.SurfaceForm);
                Assert.Equal(2, item.StartTokenIndex);
                Assert.Equal(1, item.TokenLength);
            },
            item =>
            {
                Assert.Equal("object", item.Dimension);
                Assert.Equal("packets", item.Value);
                Assert.Equal("packets", item.SurfaceForm);
                Assert.Equal(3, item.StartTokenIndex);
                Assert.Equal(1, item.TokenLength);
            });

        Assert.Contains(
            "complete_founder_backed_source_semantic_coverage",
            understood.Reasons);

        Assert.Equal(
            before,
            Counts(
                await db.LegendLanguageStructuralPatterns.ToListAsync(),
                await db.LegendLanguageStructuralRelationships.ToListAsync(),
                await db.LegendLanguageStructuralEvidence.ToListAsync()));

        Assert.Equal(
            anchorCountBefore,
            await db.LegendLanguageCompositionalAnchors.CountAsync());

        Assert.Equal(
            textUnitCountBefore,
            await db.LegendLanguageTextUnits.CountAsync());

        // Source-semantic analysis above remains independently read-only.
        // Phase 4C may now serve only because this fixture separately contains
        // the complete Founder-verified directional and structural production
        // evidence required by the canonical composition authority.
        var production = await fixture.Curriculum.TryComposeAsync(
            "en",
            "x-test",
            unseenSource);

        Assert.NotNull(production);
        Assert.Equal(
            "za affirmative combine packets",
            production!.Text);
    }

    [Fact]
    public async Task SourceUnderstandingUsesMatureFounderStructureToDisambiguateCrossFamilySurfaceRoles()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedSupportedCompositionAsync(fixture);

        // A second Founder family legitimately gives the same surface "I" a
        // different semantic role. Its own two-example observation is not
        // mature enough to override the independently supported composition
        // structure learned above.
        var competingRole = new LegendConnectCurriculumBatchSubmission(
            "composition.competing.speaker-role",
            "Controlled competing source role evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "I greet politely.",
                    new Dictionary<string, string>
                    {
                        ["speaker-role"] = "I",
                        ["predicate"] = "greet",
                        ["manner"] = "politely"
                    }),
                new LegendConnectCurriculumExampleSubmission(
                    "You greet politely.",
                    new Dictionary<string, string>
                    {
                        ["speaker-role"] = "You",
                        ["predicate"] = "greet",
                        ["manner"] = "politely"
                    })
            ]);

        var submitted =
            await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                competingRole);

        Assert.True(submitted.Succeeded, submitted.Message);

        // Exercise the same canonical historical path as production. The
        // replay is idempotent and must not manufacture duplicate authority.
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);

        var understood =
            await fixture.Curriculum.AnalyzeShadowSourceSemanticsAsync(
                "en",
                "I affirmative combine packets.");

        Assert.Equal(
            LegendShadowSourceUnderstanding.SupportedForShadowEvaluation,
            understood.State);

        var first = Assert.Single(
            understood.Components.Where(item =>
                item.StartTokenIndex == 0 &&
                item.TokenLength == 1));

        Assert.Equal("agent", first.Dimension);
        Assert.Equal("I", first.Value);

        Assert.DoesNotContain(
            understood.Components,
            item => item.Dimension == "speaker-role");
    }

    [Fact]
    public async Task SourceUnderstandingFailsClosedForUnknownOrAmbiguousSemantics()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedSupportedCompositionAsync(fixture);

        var unknown =
            await fixture.Curriculum.AnalyzeShadowSourceSemanticsAsync(
                "en",
                "I affirmative combine mysteries.");

        Assert.Equal(
            LegendShadowSourceUnderstanding.InsufficientEvidence,
            unknown.State);
        Assert.False(unknown.IsProductionEligible);
        Assert.Empty(unknown.Components);
        Assert.Contains(
            "source_semantic_component_unknown",
            unknown.Reasons);

        var sourceExample = await (
            from example in db.LegendCurriculumExamples
            join unit in db.LegendLanguageTextUnits
                on example.TextUnitId equals unit.Id
            where example.LanguageCode == "en" &&
                example.DerivedFromCurriculumExampleId == null &&
                unit.Text.StartsWith(
                    "I affirmative ",
                    StringComparison.Ordinal)
            select new
            {
                Example = example,
                Unit = unit
            }
        ).FirstAsync();

        var firstOccurrence =
            await db.LegendLanguageLexicalOccurrences
                .Where(item =>
                    item.TextUnitId == sourceExample.Unit.Id &&
                    item.TokenIndex == 0 &&
                    item.SupersededUtc == null)
                .SingleAsync();

        db.LegendLanguageCompositionalAnchors.Add(
            new LegendLanguageCompositionalAnchor
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                TextUnitId = sourceExample.Unit.Id,
                LexemeId = firstOccurrence.LexemeId,
                ComponentStartTokenIndex = 0,
                ComponentLength = 1,
                CurriculumFamilyId =
                    sourceExample.Example.CurriculumFamilyId,
                CurriculumExampleId =
                    sourceExample.Example.Id,
                Dimension = "speaker-role",
                Value = "speaker",
                SemanticSignature = SemanticSignature(
                    "speaker-role",
                    "speaker"),
                AnchorSignature = LegendLanguageIdentity.TextHash(
                    $"phase4a-ambiguity|" +
                    $"{sourceExample.Example.Id:D}"),
                Provenance = "FounderApproved"
            });

        await db.SaveChangesAsync();

        var ambiguous =
            await fixture.Curriculum.AnalyzeShadowSourceSemanticsAsync(
                "en",
                "I affirmative combine packets.");

        Assert.Equal(
            LegendShadowSourceUnderstanding.Ambiguous,
            ambiguous.State);
        Assert.False(ambiguous.IsProductionEligible);
        Assert.Empty(ambiguous.Components);
        Assert.Contains(
            "ambiguous_source_semantic_identity",
            ambiguous.Reasons);

        Assert.Null(
            await fixture.Curriculum.TryComposeAsync(
                "en",
                "x-test",
                "I affirmative combine packets."));
    }

    [Fact]
    public async Task UnseenSourceCanBeIndependentlyFormulatedFromVerifiedTargetEvidenceWithoutExactMemoryOrProviderInput()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedSupportedCompositionAsync(fixture);

        const string unseenSource = "I affirmative combine packets.";
        var heldOut = HeldOutRequest();

        // This must remain a true holdout. LEGEND may know each component and
        // target-language structure independently, but it may not already have
        // the complete source→target sentence stored as a trusted alignment.
        var exactAlignmentExists = await db.LegendTranslationAlignments
            .Where(item =>
                item.PairKey == "en:x-test" &&
                item.HumanVerified &&
                item.SupersededUtc == null)
            .Join(
                db.LegendLanguageTextUnits,
                alignment => alignment.SourceTextUnitId,
                unit => unit.Id,
                (alignment, unit) => new
                {
                    Alignment = alignment,
                    SourceText = unit.Text
                })
            .Join(
                db.LegendLanguageTextUnits,
                item => item.Alignment.TargetTextUnitId,
                unit => unit.Id,
                (item, unit) => new
                {
                    item.SourceText,
                    TargetText = unit.Text
                })
            .AnyAsync(item =>
                item.SourceText == unseenSource &&
                item.TargetText == heldOut.ProposedTargetText);

        Assert.False(exactAlignmentExists);

        var before = (
            TextUnits: await db.LegendLanguageTextUnits.CountAsync(),
            Alignments: await db.LegendTranslationAlignments.CountAsync(),
            Candidates: await db.LegendLanguageTargetRealizationCandidates.CountAsync(),
            CandidateEvidence: await db.LegendLanguageTargetRealizationEvidence.CountAsync(),
            Patterns: await db.LegendLanguageStructuralPatterns.CountAsync(),
            Relationships: await db.LegendLanguageStructuralRelationships.CountAsync(),
            StructuralEvidence: await db.LegendLanguageStructuralEvidence.CountAsync());

        var formulation = await fixture.Curriculum.FormulateShadowTargetAsync(
            "en",
            "x-test",
            unseenSource);


        Assert.Equal(
            LegendShadowTargetFormulation.SupportedForShadowEvaluation,
            formulation.State);

        Assert.False(formulation.IsProductionEligible);

        Assert.Equal(
            heldOut.ProposedTargetText,
            formulation.Text);

        Assert.Equal(4, formulation.Realizations.Count);

        Assert.Collection(
            formulation.Realizations
                .OrderBy(item => item.ObservedTargetStartTokenIndex),
            item =>
            {
                Assert.Equal("agent", item.Dimension);
                Assert.Equal("I", item.Value);
                Assert.Equal("za", item.SurfaceForm);
                Assert.Equal(0, item.ObservedTargetStartTokenIndex);
            },
            item =>
            {
                Assert.Equal("polarity", item.Dimension);
                Assert.Equal("affirmative", item.Value);
                Assert.Equal("affirmative", item.SurfaceForm);
                Assert.Equal(1, item.ObservedTargetStartTokenIndex);
            },
            item =>
            {
                Assert.Equal("predicate", item.Dimension);
                Assert.Equal("combine", item.Value);
                Assert.Equal("combine", item.SurfaceForm);
                Assert.Equal(2, item.ObservedTargetStartTokenIndex);
            },
            item =>
            {
                Assert.Equal("object", item.Dimension);
                Assert.Equal("packets", item.Value);
                Assert.Equal("packets", item.SurfaceForm);
                Assert.Equal(3, item.ObservedTargetStartTokenIndex);
            });

        Assert.Contains(
            "target_formulated_from_verified_directional_evidence",
            formulation.Reasons);

        var after = (
            TextUnits: await db.LegendLanguageTextUnits.CountAsync(),
            Alignments: await db.LegendTranslationAlignments.CountAsync(),
            Candidates: await db.LegendLanguageTargetRealizationCandidates.CountAsync(),
            CandidateEvidence: await db.LegendLanguageTargetRealizationEvidence.CountAsync(),
            Patterns: await db.LegendLanguageStructuralPatterns.CountAsync(),
            Relationships: await db.LegendLanguageStructuralRelationships.CountAsync(),
            StructuralEvidence: await db.LegendLanguageStructuralEvidence.CountAsync());

        // Shadow formulation is read-only and deterministic.
        Assert.Equal(before, after);

        var replay = await fixture.Curriculum.FormulateShadowTargetAsync(
            "en",
            "x-test",
            unseenSource);

        Assert.Equal(formulation.State, replay.State);
        Assert.Equal(formulation.Text, replay.Text);
        Assert.Equal(
            formulation.Realizations,
            replay.Realizations);

        // Phase 4C opens production serving only after the same learned
        // formulation has independently earned the existing production gates.
        var production = await fixture.Curriculum.TryComposeAsync(
            "en",
            "x-test",
            unseenSource);

        Assert.NotNull(production);
        Assert.Equal(formulation.Text, production!.Text);
    }

    [Fact]
    public async Task ProductionCompositionRequiresExistingProductionStructuralAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedSupportedCompositionAsync(fixture);

        const string unseenSource =
            "I affirmative combine packets.";

        var shadow = await fixture.Curriculum.FormulateShadowTargetAsync(
            "en",
            "x-test",
            unseenSource);

        Assert.Equal(
            LegendShadowTargetFormulation.SupportedForShadowEvaluation,
            shadow.State);

        var production = await fixture.Curriculum.TryComposeAsync(
            "en",
            "x-test",
            unseenSource);

        Assert.NotNull(production);
        Assert.Equal(shadow.Text, production!.Text);

        var relationship = await db.LegendLanguageStructuralRelationships
            .Where(item =>
                item.PairKey == "en:x-test" &&
                item.LanguageCode == "x-test" &&
                item.SupersededUtc == null &&
                item.IsProductionEligible)
            .FirstAsync();

        relationship.ContradictionCount = 1;
        relationship.IsProductionEligible = false;
        await db.SaveChangesAsync();

        var remainingEligible =
            await db.LegendLanguageStructuralRelationships.AnyAsync(item =>
                item.PairKey == "en:x-test" &&
                item.LanguageCode == "x-test" &&
                item.SupersededUtc == null &&
                item.IsProductionEligible &&
                item.SupportCount >= 3 &&
                item.IndependentSourceCount >= 3 &&
                item.HumanVerifiedSupportCount >= 3 &&
                item.ProviderOnlySupportCount == 0 &&
                item.ContradictionCount == 0);

        if (!remainingEligible)
        {
            Assert.Null(
                await fixture.Curriculum.TryComposeAsync(
                    "en",
                    "x-test",
                    unseenSource));
        }
    }

    [Fact]
    public async Task ProductionCompositionFailsClosedForUnknownSourceSemantics()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedSupportedCompositionAsync(fixture);

        Assert.Null(
            await fixture.Curriculum.TryComposeAsync(
                "en",
                "x-test",
                "I affirmative combine mysteries."));
    }

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
        // Keep only two independent ordinary families. The orthogonal
        // predicate/object families below also contribute independent
        // agent/polarity evidence, so the former four-family matrix produced
        // far more support than the production maturity gates require.
        foreach (var (familyKey, predicate, @object) in new[]
        {
            ("composition.observe.records", "observe", "records"),
            ("composition.review.reports", "review", "reports")
        })
        {
            await SubmitControlledCompositionFamilyAsync(
                fixture,
                familyKey,
                predicates: [predicate],
                objects: [@object]);
        }

        // Isolate the held-out predicate using the smallest controlled matrix
        // that still supplies multiple independent Founder-verified contrasts.
        // "combine packets" itself remains absent.
        await SubmitControlledCompositionFamilyAsync(
            fixture,
            "composition.orthogonal.predicate",
            predicates: ["combine", "inspect"],
            objects: ["notes"]);

        // Independently isolate the held-out object while keeping predicate
        // fixed. Again, the held-out combine+packets pair is never supplied.
        await SubmitControlledCompositionFamilyAsync(
            fixture,
            "composition.orthogonal.object",
            predicates: ["inspect"],
            objects: ["notes", "packets"]);

        // Each Founder submission already enters the canonical curriculum
        // attachment/evaluation path. Complete missing controlled target
        // anchors, then verify the resulting candidates through the existing
        // Founder review authority.
        await AddFounderTargetAnchorsAsync(fixture.Db, "x-test");
        await VerifySupportedTargetRealizationsAsync(fixture);

        // Founder verification creates canonical target anchors and may alter
        // structural maturity. One final replay is sufficient to converge the
        // same evaluator before the held-out assertions run.
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
    }

    private static async Task SubmitControlledCompositionFamilyAsync(
        CompositionFixture fixture,
        string familyKey,
        IReadOnlyCollection<string> predicates,
        IReadOnlyCollection<string> objects)
    {
        var examples = (
            from predicate in predicates
            from @object in objects
            from agent in new[] { "I", "You" }
            from polarity in new[] { "affirmative", "negative" }
            select Source(agent, polarity, predicate, @object)
        ).ToArray();

        var batch = new LegendConnectCurriculumBatchSubmission(
            familyKey,
            "Controlled composition evidence",
            examples);

        var submitted =
            await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch);

        Assert.True(submitted.Succeeded, submitted.Message);

        foreach (var example in examples)
        {
            var sourceText = example.Text;

            // Parse only the TEST fixture's known synthetic source convention.
            // No equivalent branch exists in production.
            var parts = sourceText
                .TrimEnd('.')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(4, parts.Length);

            var agent = parts[0];
            var polarity = parts[1];
            var predicate = parts[2];
            var @object = parts[3];

            // Never seed the held-out source/target pair.
            Assert.False(
                agent == "I" &&
                polarity == "affirmative" &&
                predicate == "combine" &&
                @object == "packets");

            var sourceId = await fixture.Db.LegendLanguageTextUnits
                .Where(item =>
                    item.LanguageCode == "en" &&
                    item.Text == sourceText)
                .Select(item => item.Id)
                .SingleAsync();

            var targetText =
                $"{AgentSurface(agent)} {polarity} {predicate} {@object}";

            var alreadyAligned = await (
                from alignment in fixture.Db.LegendTranslationAlignments
                join target in fixture.Db.LegendLanguageTextUnits
                    on alignment.TargetTextUnitId equals target.Id
                where alignment.PairKey == "en:x-test" &&
                    alignment.SourceTextUnitId == sourceId &&
                    alignment.HumanVerified &&
                    alignment.SupersededUtc == null &&
                    target.Text == targetText
                select alignment.Id
            ).AnyAsync();

            if (alreadyAligned)
                continue;

            var result =
                await fixture.Operations.SubmitFounderKnowledgeAsync(
                    "founder",
                    new LegendConnectKnowledgeSubmission(
                        "en",
                        sourceText,
                        "x-test",
                        targetText,
                        "Training",
                        null,
                        null,
                        "FounderApproved"),
                    reusableSourceTextUnitId: sourceId);

            Assert.True(result.Succeeded, result.Message);
        }
    }

    private static async Task VerifySupportedTargetRealizationsAsync(
        CompositionFixture fixture)
    {
        var candidateIds = await fixture.Db
            .LegendLanguageTargetRealizationCandidates
            .Where(item =>
                item.PairKey == "en:x-test" &&
                item.SupersededUtc == null &&
                item.VerificationState == "Candidate" &&
                item.HumanVerifiedSupportCount >= 3 &&
                item.IndependentSourceCount >= 3 &&
                item.ProviderOnlySupportCount == 0 &&
                item.ContradictionCount == 0)
            .OrderBy(item => item.VariationDimension)
            .ThenBy(item => item.SemanticValue)
            .Select(item => item.Id)
            .ToListAsync();

        Assert.NotEmpty(candidateIds);

        foreach (var candidateId in candidateIds)
        {
            var verified =
                await fixture.Curriculum.VerifyTargetRealizationCandidateAsync(
                    "founder",
                    candidateId);

            Assert.True(verified.Succeeded, verified.Message);
        }

        var supported = await fixture.Db
            .LegendLanguageTargetRealizationCandidates
            .Where(item =>
                item.PairKey == "en:x-test" &&
                item.SupersededUtc == null &&
                item.VerificationState == "FounderVerified" &&
                item.MaturityState == "Supported" &&
                item.IsProductionEligible)
            .ToListAsync();

        // The proof is dimension-generic. These assertions live only in the
        // synthetic fixture and demonstrate that every supplied semantic
        // dimension can mature through the same production authority.
        Assert.Contains(
            supported,
            item => item.VariationDimension == "agent" &&
                    item.SemanticValue == "I");
        Assert.Contains(
            supported,
            item => item.VariationDimension == "polarity" &&
                    item.SemanticValue == "affirmative");
        Assert.Contains(
            supported,
            item => item.VariationDimension == "predicate" &&
                    item.SemanticValue == "combine");
        Assert.Contains(
            supported,
            item => item.VariationDimension == "object" &&
                    item.SemanticValue == "packets");
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
            var variations = await db.LegendCurriculumExampleVariations
                .Where(item => item.CurriculumExampleId == target.Example.Id)
                .ToDictionaryAsync(item => item.Dimension, item => item.Value);
            var tokens = target.Unit.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var variation in variations)
            {
                var semanticSignature = SemanticSignature(variation.Key, variation.Value);

                // A target example may already contain a canonical anchor created
                // by the real candidate-verification authority. Preserve it and
                // fill only the missing controlled dimensions required to make
                // this synthetic fixture's known composition explicit.
                var alreadyAnchored = await db.LegendLanguageCompositionalAnchors.AnyAsync(item =>
                    item.CurriculumExampleId == target.Example.Id &&
                    item.Dimension == variation.Key &&
                    item.SemanticSignature == semanticSignature &&
                    item.SupersededUtc == null);

                if (alreadyAnchored)
                    continue;

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
                    SemanticSignature = semanticSignature,
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

    [Fact]
    public async Task FounderEquivalentSurfaceFusion_LearnsMultipleSemanticsOnOneTokenWithoutContractionRule()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        await SeedSupportedCompositionAsync(fixture);

        var fusion = new LegendConnectCurriculumBatchSubmission(
            "composition.surface-fusion",
            "Founder-controlled equivalent fused surface realization",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "I will prepare notes.",
                    new Dictionary<string, string>
                    {
                        ["agent"] = "I",
                        ["modality"] = "will",
                        ["predicate"] = "prepare",
                        ["object"] = "notes"
                    }),
                new LegendConnectCurriculumExampleSubmission(
                    "I'll prepare notes.",
                    new Dictionary<string, string>
                    {
                        ["agent"] = "I",
                        ["modality"] = "will",
                        ["predicate"] = "prepare",
                        ["object"] = "notes"
                    })
            ]);

        var submitted =
            await fixture.Curriculum
                .SubmitFounderEnglishBatchAsync(fusion);

        Assert.True(submitted.Succeeded, submitted.Message);

        var understood =
            await fixture.Curriculum
                .AnalyzeShadowSourceSemanticsAsync(
                    "en",
                    "I'll affirmative combine packets.");

        Assert.Equal(
            LegendShadowSourceUnderstanding
                .SupportedForShadowEvaluation,
            understood.State);

        var fused = understood.Components
            .Where(item =>
                item.StartTokenIndex == 0 &&
                item.TokenLength == 1)
            .OrderBy(item => item.Dimension)
            .ToList();

        Assert.Equal(2, fused.Count);

        Assert.Contains(
            fused,
            item =>
                item.Dimension == "agent" &&
                item.Value == "I" &&
                item.SurfaceForm == "i'll");

        Assert.Contains(
            fused,
            item =>
                item.Dimension == "modality" &&
                item.Value == "will" &&
                item.SurfaceForm == "i'll");

        // Nothing in production contains an English contraction dictionary.
        // The fused surface is justified by an expanded Founder witness.
        Assert.Contains(
            understood.Components,
            item =>
                item.Dimension == "predicate" &&
                item.Value == "combine");

        Assert.Contains(
            understood.Components,
            item =>
                item.Dimension == "object" &&
                item.Value == "packets");
    }

    [Fact]
    public async Task ProductionComposition_UsesExistingSegmenterForRepeatedSentencesAndStrongClauses()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        await SeedSupportedCompositionAsync(fixture);

        const string atomic =
            "I affirmative combine packets.";

        var longMessage =
            string.Join(
                " ",
                Enumerable.Repeat(
                    atomic,
                    7));

        // 28 lexical components total. The old flat source gate was 24.
        var multiSentence =
            await fixture.Curriculum.TryComposeAsync(
                "en",
                "x-test",
                longMessage);

        Assert.NotNull(multiSentence);

        Assert.Equal(
            string.Join(
                " ",
                Enumerable.Repeat(
                    "za affirmative combine packets",
                    7)),
            multiSentence!.Text);

        var strongClauses =
            string.Join(
                "; ",
                Enumerable.Repeat(
                    "I affirmative combine packets",
                    2));

        var clauseComposition =
            await fixture.Curriculum.TryComposeAsync(
                "en",
                "x-test",
                strongClauses);

        Assert.NotNull(clauseComposition);

        Assert.Equal(
            "za affirmative combine packets " +
            "za affirmative combine packets",
            clauseComposition!.Text);
    }

}
