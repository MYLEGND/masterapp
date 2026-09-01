using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Messaging;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectResearchEvidenceAdmissibilityPolicyTests
{
    private static readonly DateTime AssessedUtc =
        new(2026, 8, 31, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CanonicalCatalog_CoversEveryGovernedClaimSubject()
    {
        foreach (var subject in Enum.GetValues<LegendConnectResearchClaimSubject>())
        {
            Assert.Equal(
                subject,
                LegendConnectResearchEvidenceAdmissibilityPolicy.StandardFor(subject).Subject);
        }
    }

    [Fact]
    public void SourceClassification_ContainsEveryGovernedAuthorityClass()
    {
        Assert.Equal(
            new[]
            {
                LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority,
                LegendConnectResearchSourceClass.PeerReviewedOriginalResearch,
                LegendConnectResearchSourceClass.SystematicReviewOrRecognizedScientificMedicalAuthority,
                LegendConnectResearchSourceClass.RegulatoryFilingOrAuditedFinancialReport,
                LegendConnectResearchSourceClass.OfficialProductOrTechnicalDocumentation,
                LegendConnectResearchSourceClass.FirstPartyCompanyStatement,
                LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                LegendConnectResearchSourceClass.IndependentSecondaryAnalysis,
                LegendConnectResearchSourceClass.Aggregator,
                LegendConnectResearchSourceClass.OpinionOrCommentary,
                LegendConnectResearchSourceClass.UserGeneratedContent,
                LegendConnectResearchSourceClass.AnonymousOrUnverifiableContent,
                LegendConnectResearchSourceClass.UnknownSource
            },
            Enum.GetValues<LegendConnectResearchSourceClass>());
    }

    [Fact]
    public void DefinitivePrimaryRecord_ControlsItsExactClaimWithoutSyntheticCorroboration()
    {
        var assessment = Assess(
            minimumIndependentSources: 3,
            Spec(
                "primary",
                LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                controlling: true));

        Assert.Equal(LegendResearchEvidenceAssessmentState.Conclusion, assessment.State);
        Assert.Single(assessment.Claims);
        Assert.Equal(
            LegendConnectResearchEvidenceDisposition.ControllingEvidence,
            Assert.Single(assessment.Admissibility).Disposition);
    }

    [Fact]
    public void FirstPartyStatement_OutsideItsClaimAuthority_IsOnlyAnObservation()
    {
        var assessment = Assess(
            minimumIndependentSources: 1,
            Spec(
                "company",
                LegendConnectResearchSourceClass.FirstPartyCompanyStatement,
                subject: LegendConnectResearchClaimSubject.Scientific,
                authorityScope: LegendConnectResearchAuthorityScope.OwnPublishedPolicy,
                requiredScope: LegendConnectResearchAuthorityScope.MedicalScientificEvidence,
                statementKind: LegendConnectResearchStatementKind.SourceAssertion));

        Assert.Equal(LegendResearchEvidenceAssessmentState.InsufficientEvidence, assessment.State);
        var receipt = Assert.Single(assessment.Admissibility);
        Assert.Equal(LegendConnectResearchEvidenceDisposition.ObservationOnly, receipt.Disposition);
        Assert.Equal("research_first_party_authority_outside_claim_scope", receipt.ReasonCode);
    }

    [Fact]
    public void FirstPartyPublishedPolicy_ControlsOnlyItsOwnPolicyClaim()
    {
        var assessment = Assess(
            minimumIndependentSources: 3,
            Spec(
                "company-policy",
                LegendConnectResearchSourceClass.FirstPartyCompanyStatement,
                authorityScope: LegendConnectResearchAuthorityScope.OwnPublishedPolicy,
                requiredScope: LegendConnectResearchAuthorityScope.OwnPublishedPolicy,
                statementKind: LegendConnectResearchStatementKind.PublishedStatement,
                controlling: true));

        Assert.Equal(LegendResearchEvidenceAssessmentState.Conclusion, assessment.State);
        Assert.Equal(
            LegendConnectResearchEvidenceDisposition.ControllingEvidence,
            Assert.Single(assessment.Admissibility).Disposition);
    }

    [Fact]
    public void CopiedAndPressReleaseDerivedReporting_CountsAsOneLineage()
    {
        var first = Spec(
            "report-one",
            LegendConnectResearchSourceClass.IndependentProfessionalReporting,
            subject: LegendConnectResearchClaimSubject.CurrentEvent,
            authorityScope: LegendConnectResearchAuthorityScope.CurrentEventRecord,
            requiredScope: LegendConnectResearchAuthorityScope.CurrentEventRecord,
            lineageKind: LegendConnectResearchSourceLineageKind.PressReleaseDerived,
            commonOrigin: "press-release-origin",
            content: "The same syndicated source text.");
        var second = first with { Identity = "report-two" };

        var assessment = Assess(2, first, second);

        Assert.Equal(LegendResearchEvidenceAssessmentState.InsufficientEvidence, assessment.State);
        Assert.Single(assessment.Admissibility
            .Select(item => item.IndependentLineageIdentity)
            .Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void CircularCitationSources_AreRejected()
    {
        var assessment = Assess(
            2,
            Spec("cycle-one", citationTargets: ["cycle-two"]),
            Spec("cycle-two", citationTargets: ["cycle-one"]));

        Assert.All(
            assessment.Admissibility,
            item => Assert.Equal("research_source_citation_cycle", item.ReasonCode));
        Assert.Equal(LegendResearchEvidenceAssessmentState.InsufficientEvidence, assessment.State);
    }

    [Fact]
    public void StaleCurrentEventSource_IsPreservedAsStaleButCannotAuthorizeClaim()
    {
        var assessment = Assess(
            1,
            Spec(
                "stale",
                LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                subject: LegendConnectResearchClaimSubject.CurrentEvent,
                authorityScope: LegendConnectResearchAuthorityScope.CurrentEventRecord,
                requiredScope: LegendConnectResearchAuthorityScope.CurrentEventRecord,
                publishedUtc: AssessedUtc.AddDays(-30)));

        Assert.Equal(
            "research_source_stale_for_claim",
            Assert.Single(assessment.Admissibility).ReasonCode);
        Assert.Equal(
            LegendConnectResearchClaimVerificationState.Stale,
            Assert.Single(assessment.ClaimResolutions).State);
    }

    [Fact]
    public void AnonymousClaim_FailsClosed()
    {
        var assessment = Assess(
            1,
            Spec(
                "anonymous",
                LegendConnectResearchSourceClass.AnonymousOrUnverifiableContent,
                author: null));

        var receipt = Assert.Single(assessment.Admissibility);
        Assert.Equal(LegendConnectResearchEvidenceDisposition.Rejected, receipt.Disposition);
        Assert.Equal("research_source_anonymous_or_unverifiable", receipt.ReasonCode);
    }

    [Fact]
    public void ConflictingPublicationAndUpdateDates_AreRejected()
    {
        var assessment = Assess(
            1,
            Spec(
                "bad-dates",
                publishedUtc: AssessedUtc.AddDays(-2),
                updatedUtc: AssessedUtc.AddDays(-3)));

        Assert.Equal(
            "research_source_timestamps_conflict",
            Assert.Single(assessment.Admissibility).ReasonCode);
    }

    [Fact]
    public void PeerReviewedClaimWithoutMethodology_IsRejected()
    {
        var assessment = Assess(
            1,
            Spec(
                "study",
                LegendConnectResearchSourceClass.PeerReviewedOriginalResearch,
                subject: LegendConnectResearchClaimSubject.Medical,
                authorityScope: LegendConnectResearchAuthorityScope.MedicalScientificEvidence,
                requiredScope: LegendConnectResearchAuthorityScope.MedicalScientificEvidence,
                methodologyAvailable: false));

        Assert.Equal(
            "research_source_methodology_missing",
            Assert.Single(assessment.Admissibility).ReasonCode);
    }

    [Fact]
    public void ControllingRegulatoryFiling_CanEstablishExactFinancialRecord()
    {
        var assessment = Assess(
            3,
            Spec(
                "filing",
                LegendConnectResearchSourceClass.RegulatoryFilingOrAuditedFinancialReport,
                subject: LegendConnectResearchClaimSubject.Financial,
                authorityScope: LegendConnectResearchAuthorityScope.RegulatoryFinancialDisclosure,
                requiredScope: LegendConnectResearchAuthorityScope.RegulatoryFinancialDisclosure,
                controlling: true));

        Assert.Equal(LegendResearchEvidenceAssessmentState.Conclusion, assessment.State);
        Assert.Equal(
            "research_definitive_controlling_record",
            Assert.Single(assessment.Admissibility).ReasonCode);
    }

    [Fact]
    public void LowerAuthorityMaterial_RemainsAnObservationAndCannotControlTheClaim()
    {
        var assessment = Assess(
            1,
            Spec(
                "aggregator",
                LegendConnectResearchSourceClass.Aggregator));

        var receipt = Assert.Single(assessment.Admissibility);
        Assert.Equal(LegendConnectResearchEvidenceDisposition.ObservationOnly, receipt.Disposition);
        Assert.Empty(assessment.Claims);
        Assert.Equal(LegendResearchEvidenceAssessmentState.InsufficientEvidence, assessment.State);
    }

    [Fact]
    public void MissingProvenance_ReducesOtherwiseUsefulEvidenceToObservation()
    {
        var assessment = Assess(
            1,
            Spec("incomplete-provenance", provenanceComplete: false));

        var receipt = Assert.Single(assessment.Admissibility);
        Assert.Equal(LegendConnectResearchEvidenceDisposition.ObservationOnly, receipt.Disposition);
        Assert.Equal("research_source_provenance_incomplete", receipt.ReasonCode);
    }

    [Fact]
    public void UserGeneratedEvidence_IsClaimSpecificAndNeverControlling()
    {
        var experience = Spec(
            "person-one",
            LegendConnectResearchSourceClass.UserGeneratedContent,
            statementKind: LegendConnectResearchStatementKind.FirsthandExperience);
        var unrelatedFact = Spec(
            "person-two",
            LegendConnectResearchSourceClass.UserGeneratedContent);

        var assessment = Assess(1, experience, unrelatedFact);

        Assert.Contains(
            assessment.Admissibility,
            item => item.EvidenceIdentity == "evidence-person-one" &&
                    item.Disposition == LegendConnectResearchEvidenceDisposition.CorroboratingEvidence);
        Assert.Contains(
            assessment.Admissibility,
            item => item.EvidenceIdentity == "evidence-person-two" &&
                    item.Disposition == LegendConnectResearchEvidenceDisposition.ObservationOnly);
        Assert.DoesNotContain(
            assessment.Admissibility,
            item => item.Disposition == LegendConnectResearchEvidenceDisposition.ControllingEvidence);
    }

    [Fact]
    public void CitationChainWithoutDirectTerminalEvidence_IsRejected()
    {
        var assessment = Assess(
            1,
            Spec(
                "chain",
                support: LegendConnectResearchEvidenceSupport.CitationChain,
                citationTargets: ["missing-terminal"]));

        Assert.Equal(
            "research_citation_chain_without_direct_support",
            Assert.Single(assessment.Admissibility).ReasonCode);
    }

    [Fact]
    public void ProviderDeclaredDirectSupport_WithoutExactPageExcerpt_IsRejected()
    {
        var spec = Spec("unsupported") with
        {
            Content = "The retrieved page says something else."
        };
        var sources = new[] { spec };
        var assessment = AssessWithExcerpt(
            sources,
            "A fabricated excerpt that is absent from the page.");

        Assert.Equal(
            "research_claim_direct_support_missing",
            Assert.Single(assessment.Admissibility).ReasonCode);
    }

    private static LegendResearchEvidenceAssessment Assess(
        int minimumIndependentSources,
        params EvidenceSpec[] specs)
    {
        var sources = specs.Select(item => new LegendConnectResearchSourceIdentity(
            item.Identity,
            Uri(item.Identity),
            "Title " + item.Identity,
            "Publisher " + item.Identity,
            item.SourceClass,
            item.PublishedUtc,
            AssessedUtc,
            Author: item.Author,
            UpdatedUtc: item.UpdatedUtc,
            EffectiveUtc: item.EffectiveUtc,
            MethodologyAvailable: item.MethodologyAvailable,
            ProvenanceComplete: item.ProvenanceComplete,
            LineageKind: item.LineageKind,
            OriginalSourceIdentity: item.OriginalSourceIdentity,
            CommonOriginIdentity: item.CommonOriginIdentity,
            CitationTargetSourceIdentities: item.CitationTargets,
            AuthorityScopes: [item.AuthorityScope],
            IsControllingRecord: item.Controlling)).ToArray();
        var documents = specs.Select(item => new LegendConnectRetrievedDocument(
            "document-" + item.Identity,
            item.Identity,
            Uri(item.Identity),
            item.Content + " The exact claim under review.",
            LegendLanguageIdentity.TextHash(item.Content + " The exact claim under review."),
            AssessedUtc,
            true,
            null,
            DocumentLanguageCode: "en")).ToArray();
        var citations = specs.Select(item => new LegendConnectCitation(
            "citation-" + item.Identity,
            item.Identity,
            "document-" + item.Identity,
            "Title " + item.Identity,
            Uri(item.Identity),
            AssessedUtc)).ToArray();
        var claims = specs.Select(item => new LegendConnectClaimEvidence(
            "evidence-" + item.Identity,
            "claim-one",
            "The exact claim under review.",
            item.Identity,
            "document-" + item.Identity,
            "citation-" + item.Identity,
            AssessedUtc,
            item.Subject,
            item.StatementKind,
            item.Support,
            item.RequiredScope,
            AssessedUtc,
            "The exact claim under review.",
            EvidenceLanguageCode: "en")).ToArray();

        return LegendConnectGovernedReasoningExecutor.AssessResearchEvidence(
            sources,
            documents,
            citations,
            claims,
            [],
            minimumIndependentSources,
            AssessedUtc);
    }

    private static LegendResearchEvidenceAssessment AssessWithExcerpt(
        IReadOnlyList<EvidenceSpec> specs,
        string supportingExcerpt)
    {
        var item = Assert.Single(specs);
        var source = new LegendConnectResearchSourceIdentity(
            item.Identity,
            Uri(item.Identity),
            "Title",
            "Publisher",
            item.SourceClass,
            item.PublishedUtc,
            AssessedUtc,
            Author: item.Author,
            MethodologyAvailable: item.MethodologyAvailable,
            ProvenanceComplete: item.ProvenanceComplete,
            LineageKind: item.LineageKind,
            AuthorityScopes: [item.AuthorityScope]);
        var document = new LegendConnectRetrievedDocument(
            "document-" + item.Identity,
            item.Identity,
            Uri(item.Identity),
            item.Content,
            LegendLanguageIdentity.TextHash(item.Content),
            AssessedUtc,
            true,
            null,
            DocumentLanguageCode: "en");
        var citation = new LegendConnectCitation(
            "citation-" + item.Identity,
            item.Identity,
            document.DocumentIdentity,
            "Title",
            Uri(item.Identity),
            AssessedUtc);
        var claim = new LegendConnectClaimEvidence(
            "evidence-" + item.Identity,
            "claim-one",
            "The exact claim under review.",
            item.Identity,
            document.DocumentIdentity,
            citation.CitationIdentity,
            AssessedUtc,
            item.Subject,
            item.StatementKind,
            item.Support,
            item.RequiredScope,
            AssessedUtc,
            supportingExcerpt,
            EvidenceLanguageCode: "en");
        return LegendConnectGovernedReasoningExecutor.AssessResearchEvidence(
            [source],
            [document],
            [citation],
            [claim],
            [],
            1,
            AssessedUtc);
    }

    private static EvidenceSpec Spec(
        string identity,
        LegendConnectResearchSourceClass sourceClass =
            LegendConnectResearchSourceClass.PrimaryOfficialRecord,
        LegendConnectResearchClaimSubject subject = LegendConnectResearchClaimSubject.General,
        LegendConnectResearchAuthorityScope authorityScope =
            LegendConnectResearchAuthorityScope.GeneralRecord,
        LegendConnectResearchAuthorityScope requiredScope =
            LegendConnectResearchAuthorityScope.GeneralRecord,
        LegendConnectResearchStatementKind statementKind =
            LegendConnectResearchStatementKind.Fact,
        LegendConnectResearchEvidenceSupport support =
            LegendConnectResearchEvidenceSupport.Direct,
        LegendConnectResearchSourceLineageKind lineageKind =
            LegendConnectResearchSourceLineageKind.Original,
        string? commonOrigin = null,
        IReadOnlyList<string>? citationTargets = null,
        string? content = null,
        string? author = "Named Author",
        DateTime? publishedUtc = null,
        DateTime? updatedUtc = null,
        DateTime? effectiveUtc = null,
        bool methodologyAvailable = true,
        bool provenanceComplete = true,
        bool controlling = false) =>
        new(
            identity,
            sourceClass,
            subject,
            authorityScope,
            requiredScope,
            statementKind,
            support,
            lineageKind,
            null,
            commonOrigin,
            citationTargets ?? [],
            content ?? "Direct source content for " + identity + ".",
            author,
            publishedUtc ?? AssessedUtc.AddDays(-1),
            updatedUtc,
            effectiveUtc,
            methodologyAvailable,
            provenanceComplete,
            controlling);

    private static string Uri(string identity) =>
        "https://" + identity + ".example/evidence";

    private sealed record EvidenceSpec(
        string Identity,
        LegendConnectResearchSourceClass SourceClass,
        LegendConnectResearchClaimSubject Subject,
        LegendConnectResearchAuthorityScope AuthorityScope,
        LegendConnectResearchAuthorityScope RequiredScope,
        LegendConnectResearchStatementKind StatementKind,
        LegendConnectResearchEvidenceSupport Support,
        LegendConnectResearchSourceLineageKind LineageKind,
        string? OriginalSourceIdentity,
        string? CommonOriginIdentity,
        IReadOnlyList<string> CitationTargets,
        string Content,
        string? Author,
        DateTime PublishedUtc,
        DateTime? UpdatedUtc,
        DateTime? EffectiveUtc,
        bool MethodologyAvailable,
        bool ProvenanceComplete,
        bool Controlling);
}
