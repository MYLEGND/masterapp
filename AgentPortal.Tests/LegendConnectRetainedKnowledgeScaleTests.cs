using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectRetainedKnowledgeScaleTests
{
    [Fact]
    public async Task RetainedKnowledgeSearch_RetrievesTheGovernedSemanticFamily_NotOnlyTheMatchingSurface()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = await SeedGovernedFamilyAsync(db);
        var operations = CreateOperations(db);

        var snapshot = await operations.SearchRetainedKnowledgeAsync(
            "lantern",
            sourceLanguageCode: "en",
            take: 12);

        Assert.Contains(snapshot.Items, item =>
            item.Kind == "CanonicalText" &&
            item.Content == fixture.MatchingUnit.Text);
        Assert.Contains(snapshot.Items, item =>
            item.Kind == "CanonicalText" &&
            item.Content == fixture.SemanticallyEquivalentUnit.Text);
    }

    [Fact]
    public async Task RetainedKnowledgeSearch_DoesNotRetrieveAnUnindexedSubstringOrFuzzyPrefix()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = await SeedGovernedFamilyAsync(db);
        var raw = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            StoragePartition = "/en",
            NormalizedHash = LegendLanguageIdentity.TextHash(
                "The sapphire lantern appears only in an ungoverned raw row."),
            Text = "The sapphire lantern appears only in an ungoverned raw row.",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
            IsTrainingEligible = true
        };
        db.LegendLanguageTextUnits.Add(raw);
        await db.SaveChangesAsync();
        var operations = CreateOperations(db);

        var substring = await operations.SearchRetainedKnowledgeAsync(
            "sapphire lantern",
            sourceLanguageCode: "en");
        var fuzzy = await operations.SearchRetainedKnowledgeAsync(
            "lanter",
            sourceLanguageCode: "en");

        Assert.DoesNotContain(substring.Items, item => item.Content == raw.Text);
        Assert.DoesNotContain(fuzzy.Items, item =>
            item.Content == fixture.MatchingUnit.Text ||
            item.Content == fixture.SemanticallyEquivalentUnit.Text);
    }

    [Fact]
    public async Task RetainedKnowledgeSearch_ProjectsOnlyTheSelectedAlignmentsOpenContradictionIdentity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = await SeedGovernedFamilyAsync(db);
        var target = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(),
            LanguageCode = "es",
            StoragePartition = "/es",
            NormalizedHash = LegendLanguageIdentity.TextHash("La baliza zafiro está encendida."),
            Text = "La baliza zafiro está encendida.",
            Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
            IsTrainingEligible = true
        };
        var alignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(),
            PairKey = "en:es",
            SourceTextUnitId = fixture.MatchingUnit.Id,
            TargetTextUnitId = target.Id,
            Provider = "AzureTranslator",
            Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
            QualityState = "Observation",
            Confidence = 0.8m,
            HumanVerified = false,
            ObservationCount = 1
        };
        db.AddRange(target, alignment, new LegendTranslationQualityEvidence
        {
            Id = Guid.NewGuid(),
            ObservedAlignmentId = alignment.Id,
            PairKey = alignment.PairKey,
            SourceTextUnitId = fixture.MatchingUnit.Id,
            TargetTextUnitId = target.Id,
            Signal = "Contradictory",
            ReasonCode = "governed_target_conflict",
            ResolutionState = "Open",
            EvidenceIdentity = "retained-indexed-contradiction"
        });
        await db.SaveChangesAsync();
        var operations = CreateOperations(db);

        var snapshot = await operations.SearchRetainedKnowledgeAsync(
            "lantern",
            sourceLanguageCode: "en",
            targetLanguageCode: "es");

        var result = Assert.Single(snapshot.Items.Where(item =>
            item.Kind == "DirectionalAlignment" &&
            item.PairKey == "en:es"));
        Assert.True(result.IsContradicted);
        Assert.False(result.IsCanonical);
        Assert.Equal(target.Text, result.RelatedContent);
    }

    private static LegendConnectOperations CreateOperations(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        return new LegendConnectOperations(
            db,
            registry,
            new LegendConnectCorpusService(
                db,
                registry,
                NullLogger<LegendConnectCorpusService>.Instance),
            configuration);
    }

    private static async Task<GovernedFamilyFixture> SeedGovernedFamilyAsync(
        MasterAppDbContext db)
    {
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = "retained.indexed.sapphire-beacon",
            SemanticCategory = "retrieval-proof",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        var matching = TextUnit("The sapphire lantern marks the governed beacon.");
        var equivalent = TextUnit("The founder-approved beacon carries the same controlled meaning.");
        var matchingExample = Example(family.Id, matching.Id);
        var equivalentExample = Example(family.Id, equivalent.Id);
        var lexeme = new LegendLanguageLexeme
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            NormalizedHash = LegendLanguageIdentity.TextHash("lantern"),
            SurfaceForm = "lantern",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        var occurrence = new LegendLanguageLexicalOccurrence
        {
            Id = Guid.NewGuid(),
            TextUnitId = matching.Id,
            LexemeId = lexeme.Id,
            TokenIndex = 2,
            CharacterOffset = matching.Text.IndexOf("lantern", StringComparison.Ordinal),
            CharacterLength = "lantern".Length
        };
        var semanticSignature = LegendLanguageIdentity.TextHash("retrieval_marker|sapphire_beacon");
        var anchor = new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            TextUnitId = matching.Id,
            LexemeId = lexeme.Id,
            ComponentStartTokenIndex = 2,
            ComponentLength = 1,
            CurriculumFamilyId = family.Id,
            CurriculumExampleId = matchingExample.Id,
            Dimension = "retrieval_marker",
            Value = "sapphire_beacon",
            SemanticSignature = semanticSignature,
            AnchorSignature = LegendLanguageIdentity.TextHash("retained-indexed-anchor"),
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        var node = new LegendLanguageMeaningNodeEvidence
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            CurriculumFamilyId = family.Id,
            CurriculumExampleId = matchingExample.Id,
            CompositionalAnchorId = anchor.Id,
            NodeKey = "beacon",
            SemanticSignature = semanticSignature,
            SemanticDimension = "retrieval_marker",
            SemanticValue = "sapphire_beacon",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        var primitive = new LegendLanguageMeaningPrimitive
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            SemanticSignature = semanticSignature,
            SemanticDimension = "retrieval_marker",
            SemanticValue = "sapphire_beacon",
            MaturityState = "Validated",
            SupportCount = 3,
            IndependentSourceCount = 3,
            HumanVerifiedSupportCount = 3,
            Confidence = 1m,
            IsProductionEligible = true,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        db.AddRange(
            family,
            matching,
            equivalent,
            matchingExample,
            equivalentExample,
            lexeme,
            occurrence,
            anchor,
            node,
            primitive);
        await db.SaveChangesAsync();
        return new(matching, equivalent);
    }

    private static LegendLanguageTextUnit TextUnit(string text) => new()
    {
        Id = Guid.NewGuid(),
        LanguageCode = "en",
        StoragePartition = "/en",
        NormalizedHash = LegendLanguageIdentity.TextHash(text),
        Text = text,
        Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
        IsTrainingEligible = true
    };

    private static LegendCurriculumExample Example(Guid familyId, Guid textUnitId) => new()
    {
        Id = Guid.NewGuid(),
        CurriculumFamilyId = familyId,
        TextUnitId = textUnitId,
        LanguageCode = "en",
        Provenance = LegendConnectKnowledgeProvenance.FounderApproved
    };

    private sealed record GovernedFamilyFixture(
        LegendLanguageTextUnit MatchingUnit,
        LegendLanguageTextUnit SemanticallyEquivalentUnit);
}
