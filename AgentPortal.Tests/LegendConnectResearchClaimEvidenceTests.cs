using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Messaging;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectResearchClaimEvidenceTests
{
    private static readonly DateTime AssessedUtc =
        new(2026, 8, 31, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExactPassageEntailment_MaterializesBoundedClaimWithCompleteLineage()
    {
        var assessment = Assess(
            Seed(
                "one",
                "claim-value",
                "The measured value is 10.",
                passage: "The measured value is 10. Record one."),
            Seed(
                "two",
                "claim-value",
                "The measured value is 10.",
                passage: "The measured value is 10. Record two."));

        Assert.Equal(LegendResearchEvidenceAssessmentState.Conclusion, assessment.State);
        Assert.All(assessment.MaterialEvidence, evidence =>
        {
            Assert.Contains("The measured value is 10.", evidence.Passage.ExactPassage);
            Assert.Equal(0, evidence.Passage.StartCharacterOffset);
            Assert.Equal(evidence.Passage.ExactPassage.Length, evidence.Passage.CharacterLength);
            Assert.Equal(evidence.SourceIdentity, evidence.Provenance.SourceIdentity);
            Assert.Equal(evidence.DocumentIdentity, evidence.Passage.DocumentIdentity);
            Assert.Equal(AssessedUtc.AddDays(-1), evidence.PublishedUtc);
            Assert.Equal(AssessedUtc, evidence.RetrievedUtc);
            Assert.True(evidence.Provenance.PassageValidated);
            Assert.True(evidence.Provenance.ZeroWrite);
            Assert.Equal(
                LegendConnectResearchExtractionMethod.ModelAssistedProposalValidatedAgainstExactPassage,
                evidence.ExtractionMethod);
        });
        Assert.All(
            assessment.ClaimResolutions,
            resolution => Assert.Equal(
                LegendConnectResearchClaimVerificationState.SupportedByIndependentlyCorroboratedEvidence,
                resolution.State));
    }

    [Fact]
    public void PartialPassageSupport_CannotMaterializeWholeClaim()
    {
        var assessment = Assess(Seed(
            "partial",
            "claim-financials",
            "Revenue was 10 and margin was 20.",
            passage: "Revenue was 10."));

        Assert.Empty(assessment.MaterialEvidence);
        Assert.Equal(
            "research_claim_passage_entailment_failed",
            Assert.Single(assessment.Admissibility).ReasonCode);
    }

    [Fact]
    public void ClaimScopeMismatch_PreservesObservationButCannotAuthorizeConclusion()
    {
        var assessment = Assess(Seed(
            "company",
            "claim-science",
            "The company reports that the treatment is effective.",
            sourceClass: LegendConnectResearchSourceClass.FirstPartyCompanyStatement,
            subject: LegendConnectResearchClaimSubject.Scientific,
            sourceScope: LegendConnectResearchAuthorityScope.OwnPublishedPolicy,
            requiredScope: LegendConnectResearchAuthorityScope.MedicalScientificEvidence,
            statementKind: LegendConnectResearchStatementKind.SourceAssertion,
            controlling: true));

        Assert.Equal(LegendResearchEvidenceAssessmentState.InsufficientEvidence, assessment.State);
        Assert.Equal(
            LegendConnectResearchClaimVerificationState.SourceReportedButNotIndependentlyVerified,
            Assert.Single(assessment.ClaimResolutions).State);
        Assert.Equal(
            LegendConnectResearchEvidenceDisposition.ObservationOnly,
            Assert.Single(assessment.Admissibility).Disposition);
    }

    [Fact]
    public void CopiedSources_CountAsOneIndependentLineage()
    {
        var first = Seed(
            "copied-one",
            "claim-event",
            "The event occurred on Monday.",
            sourceClass: LegendConnectResearchSourceClass.IndependentProfessionalReporting,
            subject: LegendConnectResearchClaimSubject.CurrentEvent,
            sourceScope: LegendConnectResearchAuthorityScope.CurrentEventRecord,
            requiredScope: LegendConnectResearchAuthorityScope.CurrentEventRecord,
            commonOrigin: "wire-origin");
        var assessment = Assess(first, first with { Identity = "copied-two" });

        Assert.Equal(LegendResearchEvidenceAssessmentState.InsufficientEvidence, assessment.State);
        Assert.Single(assessment.MaterialEvidence
            .Select(item => item.IndependentSourceLineage)
            .Distinct(StringComparer.Ordinal));
        Assert.Equal(
            LegendConnectResearchClaimVerificationState.SourceReportedButNotIndependentlyVerified,
            Assert.Single(assessment.ClaimResolutions).State);
    }

    [Fact]
    public void NewerControllingCorrection_IsSelectedAndClaimRemainsDisputed()
    {
        var assessment = Assess(
            Seed(
                "old-record",
                "claim-value",
                "The measured value is 10.",
                controlling: true,
                publishedUtc: AssessedUtc.AddDays(-2)),
            Seed(
                "corrected-record",
                "claim-value",
                "The measured value is 11.",
                contradicting: true,
                controlling: true,
                publishedUtc: AssessedUtc.AddDays(-1),
                correctsSourceIdentity: "source-old-record"));

        Assert.Equal(LegendResearchEvidenceAssessmentState.Conclusion, assessment.State);
        var resolution = Assert.Single(assessment.ClaimResolutions);
        Assert.Equal(LegendConnectResearchClaimVerificationState.Disputed, resolution.State);
        Assert.Equal("research_newer_controlling_correction_selected", resolution.ReasonCode);
        Assert.Equal("The measured value is 11.", resolution.SelectedStatement);
    }

    [Fact]
    public void OneControllingRecord_VerifiesExactClaimWithoutPopularityOrRepetition()
    {
        var assessment = Assess(Seed(
            "controlling",
            "claim-policy",
            "The policy took effect on Monday.",
            controlling: true));

        Assert.Equal(LegendResearchEvidenceAssessmentState.Conclusion, assessment.State);
        Assert.Equal(
            LegendConnectResearchClaimVerificationState.VerifiedByControllingEvidence,
            Assert.Single(assessment.ClaimResolutions).State);
        Assert.Single(assessment.Claims);
    }

    [Fact]
    public void RepeatedSecondaryContradictions_CannotDefeatDirectControllingRecord()
    {
        var assessment = Assess(
            Seed(
                "record",
                "claim-status",
                "The controlling status is active.",
                controlling: true),
            Seed(
                "report-one",
                "claim-status",
                "The controlling status is inactive.",
                sourceClass: LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                contradicting: true),
            Seed(
                "report-two",
                "claim-status",
                "The controlling status is inactive.",
                sourceClass: LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                contradicting: true),
            Seed(
                "report-three",
                "claim-status",
                "The controlling status is inactive.",
                sourceClass: LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                contradicting: true));

        var resolution = Assert.Single(assessment.ClaimResolutions);
        Assert.Equal(LegendConnectResearchClaimVerificationState.Disputed, resolution.State);
        Assert.Equal("research_higher_standard_conflict_selected", resolution.ReasonCode);
        Assert.Equal("The controlling status is active.", resolution.SelectedStatement);
    }

    [Fact]
    public void EqualAuthorityContradiction_RemainsUnresolved()
    {
        var assessment = Assess(
            Seed(
                "record-a",
                "claim-status",
                "The filing status is active.",
                controlling: true),
            Seed(
                "record-b",
                "claim-status",
                "The filing status is inactive.",
                contradicting: true,
                controlling: true));

        Assert.Equal(LegendResearchEvidenceAssessmentState.UnresolvedConflict, assessment.State);
        Assert.Equal(
            LegendConnectResearchClaimVerificationState.UnresolvedConflict,
            Assert.Single(assessment.ClaimResolutions).State);
        Assert.NotEmpty(assessment.Claims);
        Assert.NotEmpty(assessment.Contradictions);
    }

    [Fact]
    public void NonDiscriminatingContext_RemainsObservationallyEquivalent()
    {
        var assessment = Assess(Seed(
            "context",
            "claim-cause",
            "The symptom followed the deployment.",
            support: LegendConnectResearchEvidenceSupport.Observation));

        Assert.Equal(LegendResearchEvidenceAssessmentState.InsufficientEvidence, assessment.State);
        var resolution = Assert.Single(assessment.ClaimResolutions);
        Assert.True(resolution.RequiresDiscriminatingEvidence);
        Assert.Equal(
            "research_observational_equivalence_requires_discriminating_evidence",
            resolution.ReasonCode);
    }

    [Fact]
    public void GovernedTranslatedPassage_PreservesTranslationLineage()
    {
        const string evidenceIdentity = "evidence-translated";
        var seed = Seed(
            "translated",
            "claim-translated",
            "The measured value is 10.",
            passage: "La valeur mesurée est 10.",
            evidenceIdentity: evidenceIdentity,
            documentLanguageCode: "fr",
            evidenceLanguageCode: "en",
            controlling: true);
        var documentHash = LegendLanguageIdentity.TextHash(seed.Passage);
        var lineage = new LegendConnectResearchLanguageLineage(
            "en",
            ["en"],
            ["fr"],
            "en",
            "en",
            [
                new LegendConnectResearchTranslationReceipt(
                    "translation-one",
                    "fr",
                    "en",
                    "GovernedTranslation",
                    documentHash,
                    LegendLanguageIdentity.TextHash(seed.Statement),
                    AssessedUtc,
                    "GovernedTranslationValidated",
                    [evidenceIdentity])
            ]);

        var assessment = Assess([seed], lineage);

        var evidence = Assert.Single(assessment.Claims);
        Assert.True(evidence.TranslationLineage.TranslationApplied);
        Assert.True(evidence.TranslationLineage.GovernedTranslationValidated);
        Assert.Equal("translation-one", evidence.TranslationLineage.TranslationReceiptIdentity);
        Assert.Equal(
            LegendConnectResearchExtractionMethod.GovernedTranslationValidated,
            evidence.ExtractionMethod);
    }

    [Fact]
    public void BoundedInference_IsLabeledAndRequiresGovernedPremisesAndDiscriminator()
    {
        var assessment = Assess(
            Seed("premise-a", "premise-a", "Condition A is present.", controlling: true),
            Seed("premise-b", "premise-b", "Condition B is present.", controlling: true),
            Seed("discriminator", "discriminator", "Test D distinguishes the causes.", controlling: true),
            Seed(
                "inference",
                "inferred-cause",
                "Conditions A and B support cause C.",
                statementKind: LegendConnectResearchStatementKind.Inference,
                premiseClaimIdentities: ["premise-a", "premise-b"],
                discriminatingClaimIdentity: "discriminator"));

        var resolution = Assert.Single(assessment.ClaimResolutions.Where(item =>
            item.State == LegendConnectResearchClaimVerificationState.ReasonedInferenceFromEvidence));
        Assert.Equal("research_bounded_causal_inference_supported", resolution.ReasonCode);
        var inference = Assert.Single(assessment.Claims.Where(item =>
            item.VerificationState ==
                LegendConnectResearchClaimVerificationState.ReasonedInferenceFromEvidence));
        Assert.Equal(
            LegendConnectResearchExtractionMethod.BoundedGovernedInference,
            inference.ExtractionMethod);
    }

    [Fact]
    public void HostileExtractionOutput_CannotDeclareItselfValidatedEvidence()
    {
        var assessment = Assess(Seed(
            "hostile",
            "claim-hostile",
            "The record is authoritative.",
            extractionMethod:
                LegendConnectResearchExtractionMethod.ModelAssistedProposalValidatedAgainstExactPassage,
            controlling: true));

        Assert.Empty(assessment.MaterialEvidence);
        Assert.Equal(
            "research_extraction_method_not_proposal",
            Assert.Single(assessment.Admissibility).ReasonCode);
    }

    private static LegendResearchEvidenceAssessment Assess(params EvidenceSeed[] seeds) =>
        Assess(seeds, null);

    private static LegendResearchEvidenceAssessment Assess(
        IReadOnlyList<EvidenceSeed> seeds,
        LegendConnectResearchLanguageLineage? languageLineage)
    {
        var sources = seeds.Select(seed => new LegendConnectResearchSourceIdentity(
            "source-" + seed.Identity,
            Uri(seed.Identity),
            "Title " + seed.Identity,
            "Publisher " + seed.Identity,
            seed.SourceClass,
            seed.PublishedUtc,
            AssessedUtc,
            seed.DocumentLanguageCode,
            Author: "Named Author",
            MethodologyAvailable: true,
            ProvenanceComplete: true,
            LineageKind: seed.CommonOrigin is null
                ? LegendConnectResearchSourceLineageKind.Original
                : LegendConnectResearchSourceLineageKind.CommonOrigin,
            CommonOriginIdentity: seed.CommonOrigin,
            AuthorityScopes: [seed.SourceScope],
            IsControllingRecord: seed.Controlling)).ToArray();
        var documents = seeds.Select(seed => new LegendConnectRetrievedDocument(
            "document-" + seed.Identity,
            "source-" + seed.Identity,
            Uri(seed.Identity),
            seed.Passage,
            LegendLanguageIdentity.TextHash(seed.Passage),
            AssessedUtc,
            true,
            null,
            seed.DocumentLanguageCode,
            "text/plain",
            ReturnedBytes: seed.Passage.Length)).ToArray();
        var citations = seeds.Select(seed => new LegendConnectCitation(
            "citation-" + seed.Identity,
            "source-" + seed.Identity,
            "document-" + seed.Identity,
            "Title " + seed.Identity,
            Uri(seed.Identity),
            AssessedUtc,
            seed.DocumentLanguageCode)).ToArray();
        var claims = seeds.Where(seed => !seed.Contradicting)
            .Select(seed => new LegendConnectClaimEvidence(
                seed.EvidenceIdentity ?? "evidence-" + seed.Identity,
                seed.ClaimIdentity,
                seed.Statement,
                "source-" + seed.Identity,
                "document-" + seed.Identity,
                "citation-" + seed.Identity,
                AssessedUtc,
                seed.Subject,
                seed.StatementKind,
                seed.Support,
                seed.RequiredScope,
                AssessedUtc,
                seed.Passage,
                seed.EvidenceLanguageCode,
                seed.ExtractionMethod,
                seed.PremiseClaimIdentities,
                seed.DiscriminatingClaimIdentity,
                seed.CorrectsSourceIdentity)).ToArray();
        var contradictions = seeds.Where(seed => seed.Contradicting)
            .Select(seed => new LegendConnectContradictingEvidence(
                seed.EvidenceIdentity ?? "evidence-" + seed.Identity,
                seed.ClaimIdentity,
                seed.Statement,
                "source-" + seed.Identity,
                "document-" + seed.Identity,
                "citation-" + seed.Identity,
                AssessedUtc,
                seed.Subject,
                seed.StatementKind,
                seed.Support,
                seed.RequiredScope,
                AssessedUtc,
                seed.Passage,
                seed.EvidenceLanguageCode,
                seed.ExtractionMethod,
                seed.PremiseClaimIdentities,
                seed.DiscriminatingClaimIdentity,
                seed.CorrectsSourceIdentity)).ToArray();
        return LegendConnectGovernedReasoningExecutor.AssessResearchEvidence(
            sources,
            documents,
            citations,
            claims,
            contradictions,
            2,
            AssessedUtc,
            languageLineage);
    }

    private static EvidenceSeed Seed(
        string identity,
        string claimIdentity,
        string statement,
        string? passage = null,
        string? evidenceIdentity = null,
        LegendConnectResearchSourceClass sourceClass =
            LegendConnectResearchSourceClass.PrimaryOfficialRecord,
        LegendConnectResearchClaimSubject subject = LegendConnectResearchClaimSubject.General,
        LegendConnectResearchAuthorityScope sourceScope =
            LegendConnectResearchAuthorityScope.GeneralRecord,
        LegendConnectResearchAuthorityScope requiredScope =
            LegendConnectResearchAuthorityScope.GeneralRecord,
        LegendConnectResearchStatementKind statementKind =
            LegendConnectResearchStatementKind.Fact,
        LegendConnectResearchEvidenceSupport support =
            LegendConnectResearchEvidenceSupport.Direct,
        LegendConnectResearchExtractionMethod extractionMethod =
            LegendConnectResearchExtractionMethod.ModelAssistedProposal,
        bool contradicting = false,
        bool controlling = false,
        string? commonOrigin = null,
        DateTime? publishedUtc = null,
        string documentLanguageCode = "en",
        string evidenceLanguageCode = "en",
        IReadOnlyList<string>? premiseClaimIdentities = null,
        string? discriminatingClaimIdentity = null,
        string? correctsSourceIdentity = null) =>
        new(
            identity,
            claimIdentity,
            statement,
            passage ?? statement,
            evidenceIdentity,
            sourceClass,
            subject,
            sourceScope,
            requiredScope,
            statementKind,
            support,
            extractionMethod,
            contradicting,
            controlling,
            commonOrigin,
            publishedUtc ?? AssessedUtc.AddDays(-1),
            documentLanguageCode,
            evidenceLanguageCode,
            premiseClaimIdentities,
            discriminatingClaimIdentity,
            correctsSourceIdentity);

    private static string Uri(string identity) =>
        "https://" + identity + ".example/evidence";

    private sealed record EvidenceSeed(
        string Identity,
        string ClaimIdentity,
        string Statement,
        string Passage,
        string? EvidenceIdentity,
        LegendConnectResearchSourceClass SourceClass,
        LegendConnectResearchClaimSubject Subject,
        LegendConnectResearchAuthorityScope SourceScope,
        LegendConnectResearchAuthorityScope RequiredScope,
        LegendConnectResearchStatementKind StatementKind,
        LegendConnectResearchEvidenceSupport Support,
        LegendConnectResearchExtractionMethod ExtractionMethod,
        bool Contradicting,
        bool Controlling,
        string? CommonOrigin,
        DateTime PublishedUtc,
        string DocumentLanguageCode,
        string EvidenceLanguageCode,
        IReadOnlyList<string>? PremiseClaimIdentities,
        string? DiscriminatingClaimIdentity,
        string? CorrectsSourceIdentity);
}
