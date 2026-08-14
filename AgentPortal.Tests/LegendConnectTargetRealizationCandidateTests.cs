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
/// Proof for the canonical candidate-to-Founder-anchor lifecycle. Synthetic
/// surfaces are deliberately registry-neutral: no production language or
/// grammar branch participates in this test.
/// </summary>
public sealed class LegendConnectTargetRealizationCandidateTests
{
    [Fact]
    public async Task OneFounderDirectionalPair_CannotCreateATargetRealizationCandidateOrAnchor()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedFamilyAsync(
            fixture,
            "en",
            "x-alpha",
            "single.pair",
            "review",
            "records",
            "pe",
            "dwe",
            humanVerified: true,
            includeShouldExample: false);

        Assert.Empty(await db.LegendLanguageTargetRealizationCandidates.ToListAsync());
        Assert.Empty(await db.LegendLanguageTargetRealizationEvidence.ToListAsync());
        Assert.Empty(await db.LegendLanguageCompositionalAnchors
            .Where(item => item.LanguageCode == "x-alpha" && item.PairKey == "en:x-alpha")
            .ToListAsync());
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-alpha", "I may review records."));
    }

    [Fact]
    public async Task IndependentFounderPairs_DeriveReviewOnlyCandidate_ThenFounderVerificationCreatesOneTrustedAnchor()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedFamiliesAsync(fixture, "en", "x-alpha", "pe", "dwe", humanVerified: true);

        var candidate = await db.LegendLanguageTargetRealizationCandidates.SingleAsync(item =>
            item.PairKey == "en:x-alpha" && item.VariationDimension == "modality" &&
            item.SemanticValue == "may" && item.TargetRealization == "pe");
        Assert.Equal("Candidate", candidate.VerificationState);
        Assert.Equal("Observation", candidate.MaturityState);
        Assert.Equal(3, candidate.SupportCount);
        Assert.Equal(3, candidate.IndependentSourceCount);
        Assert.Equal(3, candidate.HumanVerifiedSupportCount);
        Assert.Equal(0, candidate.ProviderOnlySupportCount);
        Assert.False(candidate.IsProductionEligible);
        Assert.Equal(3, await db.LegendLanguageTargetRealizationEvidence
            .CountAsync(item => item.CandidateId == candidate.Id && item.SupersededUtc == null));

        var verified = await fixture.Operations.VerifyTargetRealizationCandidateAsync("founder", candidate.Id);
        Assert.True(verified.Succeeded, verified.Message);
        var refreshed = await db.LegendLanguageTargetRealizationCandidates.SingleAsync(item => item.Id == candidate.Id);
        Assert.Equal("FounderVerified", refreshed.VerificationState);
        Assert.Equal("Supported", refreshed.MaturityState);
        Assert.False(refreshed.IsProductionEligible);
        Assert.NotNull(refreshed.VerifiedAnchorId);
        var anchor = await db.LegendLanguageCompositionalAnchors.SingleAsync(item => item.Id == refreshed.VerifiedAnchorId);
        Assert.Equal("x-alpha", anchor.LanguageCode);
        Assert.Equal("modality", anchor.Dimension);
        Assert.Equal("may", anchor.Value);
        Assert.Equal(LegendConnectKnowledgeProvenance.FounderApproved, anchor.Provenance);
        Assert.Equal(3, await db.LegendLanguageCompositionalAnchors.CountAsync(item =>
            item.PairKey == "en:x-alpha" && item.SemanticSignature == refreshed.SemanticSignature &&
            item.SupersededUtc == null));

        // Verifying the independently derived counterpart creates the same
        // canonical target-anchor projection for its supporting examples.
        // These two components alone still do not constitute a complete
        // template: the existing relationship authority requires independently
        // anchored invariant components before it can form a reusable layout.
        var shouldCandidate = await db.LegendLanguageTargetRealizationCandidates.SingleAsync(item =>
            item.PairKey == "en:x-alpha" && item.SemanticValue == "should" && item.TargetRealization == "dwe");
        var verifiedShould = await fixture.Operations.VerifyTargetRealizationCandidateAsync("founder", shouldCandidate.Id);
        Assert.True(verifiedShould.Succeeded, verifiedShould.Message);
        Assert.Empty(await db.LegendLanguageStructuralRelationships.Where(item =>
            item.PairKey == "en:x-alpha" && item.LanguageCode == "x-alpha" &&
            item.VariationDimension == "modality" && item.SupersededUtc == null).ToListAsync());

        // The new realization representation deliberately does not open the
        // existing production composition boundary.
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-alpha", "I may combine packets."));

        // The first historical pass may restore pre-existing source anchor
        // projections. Measure convergence only after that canonical repair.
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        var beforeReplay = new
        {
            Candidates = await db.LegendLanguageTargetRealizationCandidates.CountAsync(),
            Evidence = await db.LegendLanguageTargetRealizationEvidence.CountAsync(),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync()
        };
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        var afterReplay = new
        {
            Candidates = await db.LegendLanguageTargetRealizationCandidates.CountAsync(),
            Evidence = await db.LegendLanguageTargetRealizationEvidence.CountAsync(),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync()
        };
        Assert.Equal(beforeReplay, afterReplay);
    }

    [Fact]
    public async Task ProviderOnlyCandidatesRemainObservational_AndFounderRejectionRetainsHistoryWithoutTrust()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedFamiliesAsync(fixture, "en", "x-alpha", "pe", "dwe", humanVerified: false);

        var candidate = await db.LegendLanguageTargetRealizationCandidates.SingleAsync(item =>
            item.PairKey == "en:x-alpha" && item.SemanticValue == "may" && item.TargetRealization == "pe");
        Assert.Equal("Candidate", candidate.VerificationState);
        Assert.Equal(0, candidate.HumanVerifiedSupportCount);
        Assert.Equal(3, candidate.ProviderOnlySupportCount);
        Assert.False(candidate.IsProductionEligible);
        Assert.All(await db.LegendLanguageTargetRealizationEvidence
            .Where(item => item.CandidateId == candidate.Id)
            .ToListAsync(), item => Assert.Equal(LegendConnectKnowledgeProvenance.ProviderDerived, item.Provenance));

        var rejected = await fixture.Operations.RejectTargetRealizationCandidateAsync("founder", candidate.Id);
        Assert.True(rejected.Succeeded, rejected.Message);
        var retained = await db.LegendLanguageTargetRealizationCandidates.SingleAsync(item => item.Id == candidate.Id);
        Assert.Equal("Rejected", retained.VerificationState);
        Assert.Equal("Superseded", retained.MaturityState);
        Assert.False(retained.IsProductionEligible);
        Assert.Null(retained.VerifiedAnchorId);
        Assert.Equal(3, await db.LegendLanguageTargetRealizationEvidence.CountAsync(item => item.CandidateId == candidate.Id));

        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        Assert.Equal("Rejected", (await db.LegendLanguageTargetRealizationCandidates.SingleAsync(item => item.Id == candidate.Id)).VerificationState);
    }

    [Fact]
    public async Task ConflictingCandidatesFailClosed_AndDirectNonEnglishPairUsesTheSameAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SeedFamiliesAsync(fixture, "x-alpha", "x-beta", "ka", "ra", humanVerified: true, familyPrefix: "first");
        await SeedFamiliesAsync(fixture, "x-alpha", "x-beta", "ta", "ra", humanVerified: true, familyPrefix: "conflict");

        var candidates = await db.LegendLanguageTargetRealizationCandidates
            .Where(item => item.PairKey == "x-alpha:x-beta" && item.SemanticValue == "may")
            .OrderBy(item => item.TargetRealization)
            .ToListAsync();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, item =>
        {
            Assert.True(item.ContradictionCount > 0);
            Assert.Equal("Contradicted", item.VerificationState);
            Assert.False(item.IsProductionEligible);
        });
        var attemptedVerification = await fixture.Operations.VerifyTargetRealizationCandidateAsync("founder", candidates[0].Id);
        Assert.False(attemptedVerification.Succeeded);
        Assert.Equal("candidate_contradicted", attemptedVerification.ErrorCode);
        Assert.Empty(await db.LegendLanguageCompositionalAnchors
            .Where(item => item.LanguageCode == "x-beta" && item.Dimension == "modality" && item.Value == "may")
            .ToListAsync());
    }

    private static async Task SeedFamiliesAsync(
        CandidateFixture fixture,
        string sourceLanguage,
        string targetLanguage,
        string mayTarget,
        string shouldTarget,
        bool humanVerified,
        string familyPrefix = "seed")
    {
        foreach (var (verb, @object) in new[]
        {
            ("observe", "records"),
            ("review", "reports"),
            ("combine", "packets")
        })
        {
            await SeedFamilyAsync(
                fixture,
                sourceLanguage,
                targetLanguage,
                $"{familyPrefix}.{verb}",
                verb,
                @object,
                mayTarget,
                shouldTarget,
                humanVerified);
        }
    }

    private static async Task SeedFamilyAsync(
        CandidateFixture fixture,
        string sourceLanguage,
        string targetLanguage,
        string familyKey,
        string verb,
        string @object,
        string mayTarget,
        string shouldTarget,
        bool humanVerified,
        bool includeShouldExample = true)
    {
        var pair = await fixture.Registry.GetOrCreateEnabledPairAsync(sourceLanguage, targetLanguage);
        Assert.NotNull(pair);
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = familyKey,
            SemanticCategory = "controlled target realization",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        fixture.Db.Add(family);
        var sources = new List<(string Value, string Text, string Target)>
        {
            ("may", $"I may {verb} {@object}.", $"za {mayTarget} {verb} {@object}")
        };
        if (includeShouldExample)
            sources.Add(("should", $"I should {verb} {@object}.", $"za {shouldTarget} {verb} {@object}"));
        var alignments = new List<(LegendTranslationAlignment Alignment, LegendLanguageTextUnit Source)>();
        foreach (var source in sources)
        {
            var sourceUnit = new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = sourceLanguage,
                StoragePartition = LegendLanguageIdentity.DatasetNamespace(sourceLanguage),
                NormalizedHash = LegendLanguageIdentity.TextHash(source.Text),
                Text = LegendLanguageIdentity.NormalizeText(source.Text),
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
                IsTrainingEligible = true
            };
            var sourceExample = new LegendCurriculumExample
            {
                Id = Guid.NewGuid(),
                CurriculumFamilyId = family.Id,
                TextUnitId = sourceUnit.Id,
                LanguageCode = sourceLanguage,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            };
            var targetUnit = new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = targetLanguage,
                StoragePartition = LegendLanguageIdentity.DatasetNamespace(targetLanguage),
                NormalizedHash = LegendLanguageIdentity.TextHash(source.Target),
                Text = LegendLanguageIdentity.NormalizeText(source.Target),
                Provenance = humanVerified
                    ? LegendConnectKnowledgeProvenance.FounderApproved
                    : LegendConnectKnowledgeProvenance.ProviderDerived,
                IsTrainingEligible = true
            };
            var alignment = new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = pair!.PairKey,
                SourceTextUnitId = sourceUnit.Id,
                TargetTextUnitId = targetUnit.Id,
                Provider = humanVerified ? "Founder" : "AzureTranslator",
                Provenance = humanVerified
                    ? LegendConnectKnowledgeProvenance.FounderApproved
                    : LegendConnectKnowledgeProvenance.ProviderDerived,
                HumanVerified = humanVerified,
                QualityState = humanVerified ? "Verified" : "Observation",
                Confidence = humanVerified ? 1m : 0m,
                ObservationCount = 1
            };
            fixture.Db.AddRange(sourceUnit, sourceExample, targetUnit, alignment,
                Variation(sourceExample.Id, "agent", "I"),
                Variation(sourceExample.Id, "modality", source.Value),
                Variation(sourceExample.Id, "predicate", verb),
                Variation(sourceExample.Id, "object", @object));
            alignments.Add((alignment, sourceUnit));
        }
        await fixture.Db.SaveChangesAsync();
        foreach (var item in alignments)
        {
            if (humanVerified)
            {
                await fixture.Curriculum.AttachValidatedAlignmentAsync(item.Alignment.Id);
            }
            else
            {
                await fixture.Curriculum.AttachProcessedExpansionAsync(
                    new LegendCorpusCandidate { SourceTextHash = item.Source.NormalizedHash },
                    pair!);
            }
        }
    }

    private static LegendCurriculumExampleVariation Variation(Guid exampleId, string dimension, string value) => new()
    {
        Id = Guid.NewGuid(),
        CurriculumExampleId = exampleId,
        Dimension = dimension,
        Value = value
    };

    private static CandidateFixture CreateFixture(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-alpha",
            ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Alpha",
            ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Alpha",
            ["LegendConnect:LanguageRegistry:Baseline:2:Code"] = "x-beta",
            ["LegendConnect:LanguageRegistry:Baseline:2:Name"] = "Beta",
            ["LegendConnect:LanguageRegistry:Baseline:2:NativeName"] = "Beta"
        }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum, intelligence: intelligence);
        return new CandidateFixture(db, registry, curriculum, operations);
    }

    private sealed record CandidateFixture(
        MasterAppDbContext Db,
        LegendLanguageRegistry Registry,
        LegendConnectCurriculumService Curriculum,
        LegendConnectOperations Operations);
}
