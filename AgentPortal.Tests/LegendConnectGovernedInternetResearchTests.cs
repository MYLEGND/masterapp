using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void Decision_CurrentOptionQuestionAfterCorrection_UsesGovernedDiscourseInsteadOfResearch()
    {
        var alpha = new LegendConnectUtteranceMeaningNode(
            "choice-alpha",
            "choice",
            "alpha",
            0,
            1,
            3);
        var correctionSelector = new LegendConnectUtteranceMeaningNode(
            "selector-first-option",
            "reference_selector",
            "first",
            1,
            1,
            3);
        var currentSelector = correctionSelector with
        {
            SemanticSignature = "selector-which-option",
            SemanticValue = "which"
        };
        var corrected = new LegendConnectDiscourseReferenceBindingSnapshot(
            "bound",
            "discourse_reference_bound",
            "choice",
            alpha.SemanticSignature,
            alpha.SemanticValue,
            1,
            0,
            true,
            "selector-first-option",
            "ordinal-choice-rule")
        {
            SupersededTurnSequence = 2,
            SupersededNodeIndex = 0,
            SupersededNodeStartTokenIndex = correctionSelector.StartTokenIndex,
            SupersededNodeTokenLength = correctionSelector.TokenLength
        };
        var current = corrected with
        {
            ReplacesActiveBinding = false,
            SelectorSemanticSignature = "selector-which-option",
            SupersededTurnSequence = null,
            SupersededNodeIndex = null,
            SupersededNodeStartTokenIndex = null,
            SupersededNodeTokenLength = null
        };
        var discourseState = new LegendConnectDiscourseStateSnapshot(
        [
            new LegendConnectDiscourseTurnStateSnapshot(
                1,
                "user",
                true,
                [alpha],
                [],
                []),
            new LegendConnectDiscourseTurnStateSnapshot(
                2,
                "user",
                true,
                [correctionSelector],
                [],
                [corrected]),
            new LegendConnectDiscourseTurnStateSnapshot(
                3,
                "user",
                true,
                [currentSelector],
                [],
                [current])
        ]);

        var decision = LegendConnectOperations.DecideResearchNeeded(
            "Which option is reliable now?",
            "en",
            Unsupported(),
            DecisionUtc,
            discourseState: discourseState);

        Assert.False(decision.ResearchRequired);
        Assert.Equal(LegendConnectResearchNeed.NotResearchable, decision.Need);
        Assert.Equal(
            "conversation_context_is_not_external_research",
            decision.ReasonCode);
    }

    [Fact]
    public void Decision_CurrentExternalQuestionWithoutDiscourseStillRequiresResearch()
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            "Which public policy is reliable right now?",
            "en",
            Unsupported(),
            DecisionUtc,
            discourseState: new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(decision.ResearchRequired);
        Assert.Equal(
            LegendConnectResearchNeed.CurrentOrTimeSensitiveInformation,
            decision.Need);
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
    public void Decision_TemporalWordInSupportedConversation_DoesNotOverrideNativeAuthority()
    {
        var decision = Decide(
            "Hello—how are you today?",
            new LegendConnectNativeInferenceSnapshot(
                true,
                1m,
                "I am ready to help.",
                "semantic_transition_governed_composed",
                3,
                "Governed conversation evidence selected.",
                false));

        Assert.False(decision.ResearchRequired);
        Assert.Equal(
            LegendConnectResearchNeed.ExistingGovernedKnowledge,
            decision.Need);
        Assert.Equal(
            "existing_governed_knowledge_answers_request",
            decision.ReasonCode);
    }

    [Theory]
    [InlineData("Hello—how are you today?")]
    [InlineData("Hola, ¿cómo estás hoy?")]
    [InlineData("Bonjour, comment allez-vous aujourd’hui ?")]
    public void Decision_UnsupportedConversationQuestion_DoesNotInventResearchAuthority(
        string question)
    {
        var decision = Decide(question, Unsupported());

        Assert.False(decision.ResearchRequired);
        Assert.Equal(
            "unfamiliar_wording_is_not_research_authority",
            decision.ReasonCode);
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

    [Theory]
    [InlineData("Which one did I say was reliable?")]
    [InlineData("What did I mean in my previous message?")]
    [InlineData("Did you say the first option or the second option?")]
    public void Decision_ConversationEvidenceNeverCreatesInternetAuthority(
        string question)
    {
        var decision = Decide(question, Unsupported());

        Assert.False(decision.ResearchRequired);
        Assert.Equal(
            LegendConnectResearchNeed.NotResearchable,
            decision.Need);
        Assert.Equal(
            "conversation_context_is_not_external_research",
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
        var contradictions = packet.Sources.Select((source, index) =>
            new LegendConnectContradictingEvidence(
                "contra-" + (index + 1),
                "claim-1",
                "The measured value is 11.",
                source.SourceIdentity,
                packet.Documents[index].DocumentIdentity,
                packet.Citations[index].CitationIdentity,
                DecisionUtc,
                SupportingExcerpt: "The measured value is 11.",
                EvidenceLanguageCode: "en")).ToArray();
        var assessment =
            LegendConnectGovernedReasoningExecutor.AssessResearchEvidence(
                packet.Sources,
                packet.Documents,
                packet.Citations,
                packet.Claims,
                contradictions,
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

    [Fact]
    public void TransportLineage_AllowsBoundedFailedPageAttemptsBeforeOneDocument()
    {
        var decision = Decide(
            "Verify the current public evidence.",
            Unsupported());
        var baseRequest = Request(
            decision,
            new LegendConnectResearchAuthorization(
                true,
                LegendConnectResearchContracts.PublicAuthorizationProvenance,
                null,
                LegendConnectResearchAccessClass.PublicReadOnly,
                true,
                true));
        var query = Assert.Single(baseRequest.Queries) with
        {
            MaximumResults = LegendConnectResearchContracts.MaximumResults
        };
        var request = baseRequest with
        {
            Queries = [query],
            MaximumResults = LegendConnectResearchContracts.MaximumResults
        };
        const string uri = "https://example.com/evidence";
        const string excerpt = "The official record supplies bounded public evidence.";
        var source = new LegendConnectResearchSourceIdentity(
            "source-1",
            uri,
            "Official record",
            "Example",
            LegendConnectResearchSourceClass.PrimaryOfficialRecord,
            DecisionUtc,
            DecisionUtc,
            "en",
            true);
        var document = new LegendConnectRetrievedDocument(
            "document-1",
            source.SourceIdentity,
            uri,
            excerpt,
            LegendLanguageIdentity.TextHash(excerpt),
            DecisionUtc,
            true,
            null,
            "en",
            "text/html",
            0,
            128,
            true);
        var citation = new LegendConnectCitation(
            "citation-1",
            source.SourceIdentity,
            document.DocumentIdentity,
            source.Title,
            uri,
            DecisionUtc,
            "en",
            true);
        var pageReceipts = Enumerable.Range(1, 7)
            .Select(index => new LegendConnectResearchPageReceipt(
                "page-receipt-" + index,
                uri + "?candidate=" + index,
                uri + "?candidate=" + index,
                DecisionUtc,
                DecisionUtc.AddMilliseconds(index),
                "FixturePageTransport",
                "PublicInternet",
                1,
                0,
                index == 7 ? 200 : 503,
                "text/html",
                index == 7 ? 128 : 0,
                index,
                null,
                "NotMeteredByTransport",
                index == 7,
                index == 7 ? null : "internet_research_page_http_failed",
                true,
                true))
            .ToArray();
        var packet = new LegendConnectResearchEvidencePacket(
            "FixtureSearch->FixturePages",
            "FixtureSearchProvider",
            "fixture-model",
            "fixture-settings",
            [query],
            [new LegendConnectResearchSearchQueryReceipt(
                "query-receipt-1",
                query.QueryIdentity,
                query.Query,
                "en",
                DecisionUtc,
                "FixtureSearch",
                "FixtureSearchProvider",
                1,
                null,
                "Unavailable",
                true,
                true)],
            pageReceipts,
            [new LegendConnectSearchResult(
                "result-1",
                query.QueryIdentity,
                1,
                source.SourceIdentity,
                source.Title,
                uri,
                null,
                "en",
                "en",
                true)],
            [source],
            [document],
            [],
            [],
            [citation],
            new LegendConnectResearchLanguageLineage(
                "en", ["en"], ["en"], "en", "en", []),
            8,
            null);

        Assert.True(
            LegendConnectOperations.HasCompleteResearchTransportLineage(
                request,
                packet));
        Assert.Null(
            LegendConnectOperations.ResearchTransportLineageFailure(
                request,
                packet));
    }

    [Fact]
    public async Task PageFailure_PreservesValidatedSearchAndStageReceiptLineage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:Research:CodeSha"] =
                    "0123456789abcdef0123456789abcdef01234567"
            })
            .Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var operations = new LegendConnectOperations(
            db,
            registry,
            new LegendConnectCorpusService(
                db,
                registry,
                NullLogger<LegendConnectCorpusService>.Instance),
            configuration,
            researchSearch: new SuccessfulSearchTransport(),
            researchPages: new FailedPageTransport());
        var decision = Decide(
            "Verify the current public evidence.",
            Unsupported());

        var outcome = await operations.ExecuteResearchAsync(Request(
            decision,
            new LegendConnectResearchAuthorization(
                true,
                LegendConnectResearchContracts.PublicAuthorizationProvenance,
                null,
                LegendConnectResearchAccessClass.PublicReadOnly,
                true,
                true)));

        Assert.Equal(LegendConnectResearchOutcomeState.Failure, outcome.State);
        Assert.Equal("internet_research_page_content_oversized", outcome.Failure?.ReasonCode);
        Assert.Single(outcome.Session.Queries);
        Assert.Single(outcome.Session.SearchResults);
        Assert.Single(outcome.Session.Sources);
        Assert.Empty(outcome.Session.Documents);
        Assert.Empty(outcome.Session.Citations);
        Assert.Equal(25, outcome.Session.SearchLatencyMilliseconds);
        Assert.Equal(7, outcome.Session.RetrievalLatencyMilliseconds);
        Assert.Equal(32, outcome.Session.LatencyMilliseconds);
        Assert.Equal(123, outcome.Session.CostMicrounits);
        Assert.Equal(123, outcome.Session.SearchCostMicrounits);
        Assert.Single(outcome.Session.SearchQueryReceipts!);
        Assert.Single(outcome.Session.PageReceipts!);
        Assert.Single(outcome.Provenance.QueryIdentities);
        Assert.Single(outcome.Provenance.SourceIdentities);
        Assert.Single(outcome.Provenance.SearchQueryReceiptIdentities!);
        Assert.Single(outcome.Provenance.PageReceiptIdentities!);
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
            "The measured value is 10. The measured value is 11. Record one.",
            LegendLanguageIdentity.TextHash(
                "The measured value is 10. The measured value is 11. Record one."),
            DecisionUtc,
            true,
            null,
            DocumentLanguageCode: "en");
        var documentTwo = new LegendConnectRetrievedDocument(
            "document-2",
            sourceTwo.SourceIdentity,
            sourceTwo.CanonicalUri,
            "The measured value is 10. The measured value is 11. Record two.",
            LegendLanguageIdentity.TextHash(
                "The measured value is 10. The measured value is 11. Record two."),
            DecisionUtc,
            true,
            null,
            DocumentLanguageCode: "en");
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
                    SupportingExcerpt: "The measured value is 10.",
                    EvidenceLanguageCode: "en"),
                new LegendConnectClaimEvidence(
                    "evidence-2",
                    "claim-1",
                    "The measured value is 10.",
                    sourceTwo.SourceIdentity,
                    documentTwo.DocumentIdentity,
                    citationTwo.CitationIdentity,
                    DecisionUtc,
                    SupportingExcerpt: "The measured value is 10.",
                    EvidenceLanguageCode: "en")
            ]);
    }

    private sealed record EvidenceFixture(
        IReadOnlyList<LegendConnectResearchSourceIdentity> Sources,
        IReadOnlyList<LegendConnectRetrievedDocument> Documents,
        IReadOnlyList<LegendConnectCitation> Citations,
        IReadOnlyList<LegendConnectClaimEvidence> Claims);

    private sealed class SuccessfulSearchTransport : ILegendConnectResearchSearchTransport
    {
        public Task<LegendConnectResearchSearchTransportResult> SearchAsync(
            LegendConnectResearchSearchTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            var query = Assert.Single(request.Queries);
            var source = new LegendConnectResearchSourceIdentity(
                "source-observed",
                "https://example.com/evidence",
                "Observed source",
                "Example",
                LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                DecisionUtc,
                DecisionUtc,
                "en",
                true,
                ProvenanceComplete: true,
                LineageKind: LegendConnectResearchSourceLineageKind.Original,
                AuthorityScopes: [LegendConnectResearchAuthorityScope.GeneralRecord]);
            return Task.FromResult(new LegendConnectResearchSearchTransportResult(
                true,
                "FixtureSearchTransport",
                "FixtureSearchProvider",
                "fixture-model-v1",
                "fixture-settings",
                [query],
                [
                    new LegendConnectResearchSearchQueryReceipt(
                        "search-receipt-1",
                        query.QueryIdentity,
                        query.Query,
                        "en",
                        DecisionUtc,
                        "FixtureSearchTransport",
                        "FixtureSearchProvider",
                        25,
                        123,
                        "Measured",
                        true,
                        true)
                ],
                [
                    new LegendConnectSearchResult(
                        "search-result-1",
                        query.QueryIdentity,
                        1,
                        source.SourceIdentity,
                        source.Title,
                        source.CanonicalUri,
                        "Observed public evidence.",
                        "en",
                        "en",
                        true)
                ],
                [source],
                [],
                [],
                25,
                123,
                null,
                false));
        }
    }

    private sealed class FailedPageTransport : ILegendConnectResearchPageRetriever
    {
        public Task<LegendConnectResearchPageRetrievalResult> RetrieveAsync(
            LegendConnectResearchPageRetrievalRequest request,
            CancellationToken cancellationToken = default)
        {
            var source = Assert.Single(request.Sources);
            return Task.FromResult(new LegendConnectResearchPageRetrievalResult(
                false,
                "FixturePageTransport",
                "fixture-page-settings",
                [],
                [],
                [],
                [],
                [],
                [
                    new LegendConnectResearchPageReceipt(
                        "page-receipt-1",
                        source.CanonicalUri,
                        source.CanonicalUri,
                        DecisionUtc,
                        DecisionUtc,
                        "FixturePageTransport",
                        "PublicInternet",
                        1,
                        0,
                        200,
                        "text/html",
                        0,
                        7,
                        null,
                        "NotMeteredByTransport",
                        false,
                        "internet_research_page_content_oversized",
                        true,
                        true)
                ],
                7,
                "internet_research_page_content_oversized",
                false));
        }
    }
}
