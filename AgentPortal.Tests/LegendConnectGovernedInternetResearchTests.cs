using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Messaging;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectGovernedInternetResearchTests
{
    private static readonly DateTime DecisionUtc =
        new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(
        "What is the latest published inflation rate?",
        LegendConnectResearchNeed.CurrentOrTimeSensitiveInformation,
        "time_sensitive_information_requires_research")]
    [InlineData(
        "Please fact-check this claim and cite sources.",
        LegendConnectResearchNeed.ExplicitVerificationRequest,
        "explicit_verification_requires_research")]
    [InlineData(
        "What does RFC 9110 say about request methods?",
        LegendConnectResearchNeed.NamedExternalDocumentOrSource,
        "named_external_source_requires_research")]
    public void Decision_RecognizesExplicitExternalResearchReasons(
        string question,
        LegendConnectResearchNeed expectedNeed,
        string expectedReason)
    {
        var decision = Decide(question, Unsupported());

        Assert.True(decision.ResearchRequired);
        Assert.Equal(expectedNeed, decision.Need);
        Assert.Equal(expectedReason, decision.ReasonCode);
        Assert.Equal(DecisionUtc, decision.DecidedUtc);
    }

    [Fact]
    public void Decision_RecognizesExternalFactualInternalKnowledgeGap()
    {
        var decision = Decide(
            "What is the boiling point of tungsten?",
            Unsupported());

        Assert.True(decision.ResearchRequired);
        Assert.Equal(
            LegendConnectResearchNeed.InternalKnowledgeGap,
            decision.Need);
        Assert.Equal(
            "external_factual_internal_knowledge_gap",
            decision.ReasonCode);
    }

    [Theory]
    [InlineData(
        "stale_internal_evidence",
        LegendConnectResearchNeed.StaleInternalEvidence)]
    [InlineData(
        "contradictory_internal_evidence",
        LegendConnectResearchNeed.ConflictingInternalEvidence)]
    public void Decision_RecognizesStaleAndConflictingInternalEvidence(
        string reason,
        LegendConnectResearchNeed expected)
    {
        var decision = Decide(
            "Evaluate the evidence.",
            new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                reason,
                2,
                reason,
                false));

        Assert.True(decision.ResearchRequired);
        Assert.Equal(expected, decision.Need);
    }

    [Fact]
    public void Decision_UsesExistingGovernedKnowledgeWithoutInternet()
    {
        var decision = Decide(
            "Explain the established governed distinction.",
            new LegendConnectNativeInferenceSnapshot(
                true,
                1m,
                "The governed answer.",
                "semantic_transition_governed_composed",
                4,
                "Governed internal evidence selected.",
                false));

        Assert.False(decision.ResearchRequired);
        Assert.Equal(
            LegendConnectResearchNeed.ExistingGovernedKnowledge,
            decision.Need);
        Assert.True(decision.InternalKnowledgeAvailable);
    }

    [Fact]
    public void Decision_DoesNotSearchMerelyBecauseWordingIsUnfamiliar()
    {
        var decision = Decide(
            "Frobnicate the blue widget.",
            Unsupported());

        Assert.False(decision.ResearchRequired);
        Assert.Equal(
            LegendConnectResearchNeed.NotResearchable,
            decision.Need);
        Assert.Equal(
            "unfamiliar_wording_is_not_research_authority",
            decision.ReasonCode);
    }

    [Fact]
    public void Decision_DoesNotReplaceInternalLegendStateToolsWithInternet()
    {
        var decision = Decide(
            "What does LEGEND currently know about Haitian Creole?",
            Unsupported());

        Assert.False(decision.ResearchRequired);
        Assert.Equal(
            "internal_legend_state_requires_governed_tools",
            decision.ReasonCode);
    }

    [Theory]
    [InlineData(
        "Research the current public release notes.",
        LegendConnectResearchAccessClass.PublicReadOnly)]
    [InlineData(
        "Fact-check this medical advice.",
        LegendConnectResearchAccessClass.SensitiveReadOnly)]
    [InlineData(
        "Verify the private document at https://example.com/private.",
        LegendConnectResearchAccessClass.PrivateReadOnly)]
    [InlineData(
        "Research the authenticated page behind a login.",
        LegendConnectResearchAccessClass.AuthenticatedReadOnly)]
    [InlineData(
        "Research this restricted source.",
        LegendConnectResearchAccessClass.RestrictedReadOnly)]
    [InlineData(
        "Research the current policy and post this.",
        LegendConnectResearchAccessClass.MutationCapable)]
    public void Decision_ClassifiesResearchAccessBeforeAuthorization(
        string question,
        LegendConnectResearchAccessClass expected)
    {
        var decision = Decide(question, Unsupported());

        Assert.True(decision.ResearchRequired);
        Assert.Equal(expected, decision.AccessClass);
    }

    [Fact]
    public void Decision_FailsClosedForUngovernedLanguage()
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            "What is current?",
            "zz",
            Unsupported(),
            DecisionUtc,
            languageGoverned: false);

        Assert.False(decision.ResearchRequired);
        Assert.Equal(
            "research_source_language_not_governed",
            decision.ReasonCode);
    }

    [Fact]
    public void RestrictedResearch_RequiresExactExistingAuthorizationCorrelation()
    {
        var decision = Decide(
            "Fact-check this medical advice.",
            Unsupported());
        var request = Request(
            decision,
            new LegendConnectResearchAuthorization(
                true,
                LegendConnectResearchContracts.PublicAuthorizationProvenance,
                null,
                decision.AccessClass,
                true,
                true));

        Assert.False(
            LegendConnectOperations.TryValidateResearchRequest(
                request,
                out var missingReason));
        Assert.Equal(
            "research_restricted_authorization_required",
            missingReason);

        var authorized = request with
        {
            Authorization = new LegendConnectResearchAuthorization(
                true,
                LegendConnectResearchContracts.RestrictedAuthorizationProvenance,
                Guid.NewGuid().ToString("N"),
                decision.AccessClass,
                true,
                true)
        };
        Assert.True(
            LegendConnectOperations.TryValidateResearchRequest(
                authorized,
                out var authorizedReason));
        Assert.Equal("research_request_governed", authorizedReason);
    }

    [Fact]
    public void PublicResearch_RemainsReadOnlyZeroWriteAndNeedsNoMutationCorrelation()
    {
        var decision = Decide(
            "What is the latest public release?",
            Unsupported());
        var authorization = new LegendConnectResearchAuthorization(
            true,
            LegendConnectResearchContracts.PublicAuthorizationProvenance,
            null,
            LegendConnectResearchAccessClass.PublicReadOnly,
            true,
            true);

        Assert.True(
            LegendConnectOperations.TryValidateResearchRequest(
                Request(decision, authorization),
                out var reason));
        Assert.Equal("research_request_governed", reason);
    }

    [Fact]
    public void ReasoningExecutor_RequiresCompleteIndependentCitationLineage()
    {
        var packet = EvidencePacket();
        var assessment =
            LegendConnectGovernedReasoningExecutor.AssessResearchEvidence(
                packet.Sources,
                packet.Documents,
                packet.Citations,
                packet.Claims,
                [],
                minimumIndependentSources: 2);

        Assert.Equal(
            LegendResearchEvidenceAssessmentState.Conclusion,
            assessment.State);
        Assert.Equal(2, assessment.IndependentSourceCount);
        Assert.Equal(2, assessment.Claims.Count);
    }

    [Fact]
    public void ReasoningExecutor_RejectsIncompleteDocumentLineage()
    {
        var packet = EvidencePacket();
        var assessment =
            LegendConnectGovernedReasoningExecutor.AssessResearchEvidence(
                packet.Sources,
                packet.Documents.Take(1).ToArray(),
                packet.Citations,
                packet.Claims,
                [],
                minimumIndependentSources: 2);

        Assert.Equal(
            LegendResearchEvidenceAssessmentState.InsufficientEvidence,
            assessment.State);
        Assert.Equal(
            "research_evidence_standard_unmet",
            assessment.ReasonCode);
    }

    [Fact]
    public void ReasoningExecutor_PreservesContradictionAsUnresolved()
    {
        var packet = EvidencePacket();
        var contradiction = new LegendConnectContradictingEvidence(
            "contra-1",
            "claim-1",
            "The measured value is 11.",
            packet.Sources[1].SourceIdentity,
            packet.Documents[1].DocumentIdentity,
            packet.Citations[1].CitationIdentity,
            DecisionUtc,
            SupportingExcerpt: "Direct evidence two.");
        var assessment =
            LegendConnectGovernedReasoningExecutor.AssessResearchEvidence(
                packet.Sources,
                packet.Documents,
                packet.Citations,
                packet.Claims,
                [contradiction],
                minimumIndependentSources: 2);

        Assert.Equal(
            LegendResearchEvidenceAssessmentState.UnresolvedConflict,
            assessment.State);
        Assert.Equal(
            "research_evidence_conflict_unresolved",
            assessment.ReasonCode);
    }

    [Fact]
    public void ResearchContracts_RepresentEveryRequiredTerminalOutcomeAndOrigin()
    {
        Assert.Equal(
            new[]
            {
                LegendConnectResearchOutcomeState.Conclusion,
                LegendConnectResearchOutcomeState.InsufficientEvidence,
                LegendConnectResearchOutcomeState.UnresolvedConflict,
                LegendConnectResearchOutcomeState.Failure
            },
            Enum.GetValues<LegendConnectResearchOutcomeState>());
        Assert.Equal(
            new[]
            {
                LegendConnectResearchEvidenceOrigin.InternalKnowledge,
                LegendConnectResearchEvidenceOrigin.ExternalResearch,
                LegendConnectResearchEvidenceOrigin.Combined,
                LegendConnectResearchEvidenceOrigin.UnresolvedEvidence
            },
            Enum.GetValues<LegendConnectResearchEvidenceOrigin>());
    }

    private static LegendConnectResearchNeededDecision Decide(
        string question,
        LegendConnectNativeInferenceSnapshot inference) =>
        LegendConnectOperations.DecideResearchNeeded(
            question,
            "en",
            inference,
            DecisionUtc);

    private static LegendConnectNativeInferenceSnapshot Unsupported() =>
        new(
            false,
            0m,
            null,
            "meaning_graph_component_unknown",
            0,
            "A required governed meaning component is unavailable.",
            true);

    private static LegendConnectResearchRequest Request(
        LegendConnectResearchNeededDecision decision,
        LegendConnectResearchAuthorization authorization)
    {
        const string question = "Verify the current public evidence.";
        return new LegendConnectResearchRequest(
            Guid.NewGuid(),
            question,
            decision,
            [
                new LegendConnectBoundedSearchQuery(
                    "query-1",
                    1,
                    question,
                    decision.SourceLanguageCode,
                    4)
            ],
            4,
            4,
            8,
            2_000,
            1,
            authorization,
            null,
            "meaning_graph_component_unknown",
            0,
            DecisionUtc);
    }

    private static EvidenceFixture EvidencePacket()
    {
        var sourceOne = new LegendConnectResearchSourceIdentity(
            "source-1",
            "https://one.example/evidence",
            "Source One",
            "Publisher One",
            LegendConnectResearchSourceClass.PrimaryOfficialRecord,
            DecisionUtc,
            DecisionUtc,
            Author: "Record Custodian One",
            ProvenanceComplete: true,
            LineageKind: LegendConnectResearchSourceLineageKind.Original,
            AuthorityScopes: [LegendConnectResearchAuthorityScope.GeneralRecord]);
        var sourceTwo = new LegendConnectResearchSourceIdentity(
            "source-2",
            "https://two.example/evidence",
            "Source Two",
            "Publisher Two",
            LegendConnectResearchSourceClass.PrimaryOfficialRecord,
            DecisionUtc,
            DecisionUtc,
            Author: "Record Custodian Two",
            ProvenanceComplete: true,
            LineageKind: LegendConnectResearchSourceLineageKind.Original,
            AuthorityScopes: [LegendConnectResearchAuthorityScope.GeneralRecord]);
        var documentOne = new LegendConnectRetrievedDocument(
            "document-1",
            sourceOne.SourceIdentity,
            sourceOne.CanonicalUri,
            "Direct evidence one.",
            LegendLanguageIdentity.TextHash("Direct evidence one."),
            DecisionUtc,
            true,
            null);
        var documentTwo = new LegendConnectRetrievedDocument(
            "document-2",
            sourceTwo.SourceIdentity,
            sourceTwo.CanonicalUri,
            "Direct evidence two.",
            LegendLanguageIdentity.TextHash("Direct evidence two."),
            DecisionUtc,
            true,
            null);
        var citationOne = new LegendConnectCitation(
            "citation-1",
            sourceOne.SourceIdentity,
            documentOne.DocumentIdentity,
            sourceOne.Title,
            sourceOne.CanonicalUri,
            DecisionUtc);
        var citationTwo = new LegendConnectCitation(
            "citation-2",
            sourceTwo.SourceIdentity,
            documentTwo.DocumentIdentity,
            sourceTwo.Title,
            sourceTwo.CanonicalUri,
            DecisionUtc);
        return new EvidenceFixture(
            [sourceOne, sourceTwo],
            [documentOne, documentTwo],
            [citationOne, citationTwo],
            [
                new LegendConnectClaimEvidence(
                    "evidence-1",
                    "claim-1",
                    "The measured value is 10.",
                    sourceOne.SourceIdentity,
                    documentOne.DocumentIdentity,
                    citationOne.CitationIdentity,
                    DecisionUtc,
                    SupportingExcerpt: "Direct evidence one."),
                new LegendConnectClaimEvidence(
                    "evidence-2",
                    "claim-1",
                    "The measured value is 10.",
                    sourceTwo.SourceIdentity,
                    documentTwo.DocumentIdentity,
                    citationTwo.CitationIdentity,
                    DecisionUtc,
                    SupportingExcerpt: "Direct evidence two.")
            ]);
    }

    private sealed record EvidenceFixture(
        IReadOnlyList<LegendConnectResearchSourceIdentity> Sources,
        IReadOnlyList<LegendConnectRetrievedDocument> Documents,
        IReadOnlyList<LegendConnectCitation> Citations,
        IReadOnlyList<LegendConnectClaimEvidence> Claims);
}
