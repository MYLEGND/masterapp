using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

public sealed class LegendConnectConversationMachineProposalTests
{
    [Fact]
    public async Task SameLanguageSemanticTeaching_UsesExistingMachineProposalLifecycle()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedGovernedSameLanguageSemanticsAsync(
            fixture.Db,
            fixture.Curriculum);
        var before = await CanonicalCountsAsync(fixture.Db);

        var result = await fixture.Service.SubmitConversationMachineProposalAsync(
            SameLanguageSubmission());

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.DuplicatePrevented);
        Assert.Equal("AwaitingCritic", result.State);
        Assert.NotNull(result.CorpusCandidateId);
        Assert.NotNull(result.ProposalId);
        Assert.False(result.ProposalAlreadyExisted);

        var candidate = await fixture.Db.LegendCorpusCandidates.SingleAsync();
        Assert.Equal("en", candidate.SourceLanguageCode);
        Assert.Equal("en", candidate.TargetLanguageCode);
        Assert.Equal(
            LegendConnectMachineTeachingSubmission.CandidateCategoryIdentity(
                LegendConnectMachineTeachingSubmission.SameLanguageSemanticCapability,
                LegendConnectMachineTeachingSubmission.ReusableSemanticCategory),
            candidate.Category);
        Assert.Equal("MachineConversation", candidate.Provenance);
        Assert.False(candidate.IsApproved);
        Assert.Equal("ConversationProposal", candidate.ProcessingState);
        Assert.Equal("Pending", candidate.TeacherProposalProcessingState);

        var proposal = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync();
        Assert.Equal("en:en", proposal.PairKey);
        Assert.Equal("MachineProposed", proposal.Provenance);
        Assert.Equal("AwaitingCritic", proposal.ValidationState);
        Assert.False(proposal.CriticApproved);
        var family = Assert.IsType<LegendLanguageTeacherFamilyProposal>(
            JsonSerializer.Deserialize<LegendLanguageTeacherFamilyProposal>(
                proposal.ProposalPayloadJson));
        Assert.Equal(
            LegendConnectMachineTeachingSubmission.SameLanguageSemanticCapability,
            family.CapabilityIdentity);
        Assert.Equal(
            LegendConnectMachineTeachingSubmission.ReusableSemanticCategory,
            family.CategoryIdentity);
        Assert.Empty(await fixture.Db.LegendLanguagePairs
            .Where(item => item.PairKey == "en:en")
            .ToListAsync());
        Assert.Equal(before, await CanonicalCountsAsync(fixture.Db));

        await fixture.Service.ProcessOneAsync();

        Assert.Equal(0, fixture.Teacher.ProposeCalls);
        Assert.Equal(1, fixture.Teacher.CritiqueCalls);
        await fixture.Db.Entry(proposal).ReloadAsync();
        await fixture.Db.Entry(candidate).ReloadAsync();
        Assert.True(proposal.CriticApproved);
        Assert.Equal("AwaitingCanonicalValidation", proposal.ValidationState);
        Assert.Equal("MachineProposed", proposal.Provenance);
        Assert.Equal(
            "AwaitingCanonicalValidation",
            candidate.TeacherProposalProcessingState);
        Assert.Equal(before, await CanonicalCountsAsync(fixture.Db));
    }

    [Fact]
    public async Task TranslationTeaching_UsesEnabledDirectionalPairAndExplicitIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedTrustedTranslationEvidenceAsync(fixture.Db);

        var result = await fixture.Service.SubmitConversationMachineProposalAsync(
            TranslationSubmission());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("AwaitingCritic", result.State);
        var pair = await fixture.Db.LegendLanguagePairs.SingleAsync(
            item => item.PairKey == "en:ht");
        Assert.True(pair.IsEnabled);
        var candidate = await fixture.Db.LegendCorpusCandidates.SingleAsync();
        Assert.Equal(
            LegendConnectMachineTeachingSubmission.CandidateCategoryIdentity(
                LegendConnectMachineTeachingSubmission.TranslationCapability,
                LegendConnectMachineTeachingSubmission.ReusableSemanticCategory),
            candidate.Category);
        var proposal = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync();
        Assert.Equal("MachineProposed", proposal.Provenance);
        var family = Assert.IsType<LegendLanguageTeacherFamilyProposal>(
            JsonSerializer.Deserialize<LegendLanguageTeacherFamilyProposal>(
                proposal.ProposalPayloadJson));
        Assert.Equal(
            LegendConnectMachineTeachingSubmission.TranslationCapability,
            family.CapabilityIdentity);
        Assert.Equal(
            LegendConnectMachineTeachingSubmission.ReusableSemanticCategory,
            family.CategoryIdentity);
    }

    [Fact]
    public async Task SameLanguageSemanticTeaching_WithoutGovernedMeaningEvidenceFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.SubmitConversationMachineProposalAsync(
            SameLanguageSubmission());

        Assert.False(result.Succeeded);
        Assert.Equal(
            "machine_teaching_same_language_evidence_unproven",
            result.ErrorCode);
        Assert.Empty(await fixture.Db.LegendCorpusCandidates.ToListAsync());
        Assert.Empty(await fixture.Db.LegendLanguageTeacherProposals.ToListAsync());
        Assert.Empty(await fixture.Db.LegendLanguagePairs.ToListAsync());
    }

    [Fact]
    public async Task FounderAuthorizedResearchObservation_UsesExistingCriticLifecycleWithoutCanonicalWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var submission = ResearchObservationSubmission();
        await SeedResearchObservationReceiptAsync(
            fixture.Db,
            submission.ResearchObservationLineage!);
        var before = await CanonicalCountsAsync(fixture.Db);

        var result = await fixture.Service.SubmitConversationMachineProposalAsync(submission);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("AwaitingCritic", result.State);
        var candidate = await fixture.Db.LegendCorpusCandidates.SingleAsync();
        var proposal = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync();
        Assert.Equal("ExternalObservation", candidate.Provenance);
        Assert.False(candidate.IsApproved);
        Assert.Equal("MachineProposed", proposal.Provenance);
        Assert.Equal("AwaitingCritic", proposal.ValidationState);
        Assert.Equal(before, await CanonicalCountsAsync(fixture.Db));

        await fixture.Service.ProcessOneAsync();

        Assert.Equal(0, fixture.Teacher.ProposeCalls);
        Assert.Equal(1, fixture.Teacher.CritiqueCalls);
        await fixture.Db.Entry(proposal).ReloadAsync();
        Assert.Equal("AwaitingCanonicalValidation", proposal.ValidationState);
        Assert.Equal("MachineProposed", proposal.Provenance);
        Assert.Equal(before, await CanonicalCountsAsync(fixture.Db));
    }

    [Fact]
    public async Task ResearchObservation_ReplayConvergesOnExistingCandidateAndProposal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var submission = ResearchObservationSubmission();
        await SeedResearchObservationReceiptAsync(
            fixture.Db,
            submission.ResearchObservationLineage!);

        var first = await fixture.Service.SubmitConversationMachineProposalAsync(submission);
        var replay = await fixture.Service.SubmitConversationMachineProposalAsync(submission);

        Assert.True(first.Succeeded);
        Assert.True(replay.Succeeded);
        Assert.True(replay.DuplicatePrevented);
        Assert.True(replay.ProposalAlreadyExisted);
        Assert.Equal(first.CorpusCandidateId, replay.CorpusCandidateId);
        Assert.Equal(first.ProposalId, replay.ProposalId);
        Assert.Single(await fixture.Db.LegendCorpusCandidates.ToListAsync());
        Assert.Single(await fixture.Db.LegendLanguageTeacherProposals.ToListAsync());
    }

    [Fact]
    public async Task ResearchObservation_WithAlteredClaimUnderOriginalReceiptFailsBeforeAnyWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var submission = ResearchObservationSubmission();
        var original = submission.ResearchObservationLineage!;
        submission = submission with
        {
            ResearchObservationLineage = original with
            {
                MaterialClaims = original.MaterialClaims
                    .Select((item, index) => index == 0
                        ? item with { Statement = "A hostile replacement claim." }
                        : item)
                    .ToArray()
            }
        };

        var result = await fixture.Service.SubmitConversationMachineProposalAsync(submission);

        Assert.False(result.Succeeded);
        Assert.Equal("machine_teaching_research_lineage_invalid", result.ErrorCode);
        Assert.Empty(await fixture.Db.LegendCorpusCandidates.ToListAsync());
        Assert.Empty(await fixture.Db.LegendLanguageTeacherProposals.ToListAsync());
    }

    [Fact]
    public async Task ExactDuplicate_ReusesCandidateAndProposalIdentityWithoutAnotherArtifact()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedTrustedTranslationEvidenceAsync(fixture.Db);
        var submission = TranslationSubmission();

        var first = await fixture.Service.SubmitConversationMachineProposalAsync(
            submission);
        var duplicate = await fixture.Service.SubmitConversationMachineProposalAsync(
            submission);

        Assert.True(first.Succeeded, first.Message);
        Assert.True(duplicate.Succeeded, duplicate.Message);
        Assert.True(duplicate.DuplicatePrevented);
        Assert.True(duplicate.ProposalAlreadyExisted);
        Assert.Equal(first.CorpusCandidateId, duplicate.CorpusCandidateId);
        Assert.Equal(first.ProposalId, duplicate.ProposalId);
        Assert.Equal(1, await fixture.Db.LegendCorpusCandidates.CountAsync());
        Assert.Equal(1, await fixture.Db.LegendLanguageTeacherProposals.CountAsync());
    }

    [Fact]
    public async Task MalformedSemanticTransition_IsRejectedBeforeAnyLifecycleWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var malformed = SameLanguageSubmission() with
        {
            SemanticTransitions =
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>()),
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["conversation_response"] = "welcome"
                        }))
            ]
        };

        var result = await fixture.Service.SubmitConversationMachineProposalAsync(
            malformed);

        Assert.False(result.Succeeded);
        Assert.Equal("machine_teaching_semantic_transition_invalid", result.ErrorCode);
        Assert.Empty(await fixture.Db.LegendCorpusCandidates.ToListAsync());
        Assert.Empty(await fixture.Db.LegendLanguageTeacherProposals.ToListAsync());
        Assert.Empty(await fixture.Db.LegendLanguagePairs.ToListAsync());
    }

    [Theory]
    [InlineData("personal_fact")]
    [InlineData("transient_fact")]
    public async Task NonReusableFactCategory_IsRejectedWithoutMutation(
        string categoryIdentity)
    {
        await using var fixture = await Fixture.CreateAsync();
        var submission = SameLanguageSubmission() with
        {
            CategoryIdentity = categoryIdentity
        };

        var result = await fixture.Service.SubmitConversationMachineProposalAsync(
            submission);

        Assert.False(result.Succeeded);
        Assert.Equal("machine_teaching_category_not_reusable", result.ErrorCode);
        Assert.Empty(await fixture.Db.LegendCorpusCandidates.ToListAsync());
        Assert.Empty(await fixture.Db.LegendLanguageTeacherProposals.ToListAsync());
        Assert.Empty(await fixture.Db.LegendLanguagePairs.ToListAsync());
    }

    [Fact]
    public async Task SameLanguageProposal_RemainsNonServingAndNonCanonicalBeforeQualification()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedGovernedSameLanguageSemanticsAsync(
            fixture.Db,
            fixture.Curriculum);
        var before = await CanonicalCountsAsync(fixture.Db);

        var result = await fixture.Service.SubmitConversationMachineProposalAsync(
            SameLanguageSubmission());

        Assert.True(result.Succeeded, result.Message);
        var candidate = await fixture.Db.LegendCorpusCandidates.SingleAsync();
        var proposal = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync();
        Assert.False(candidate.IsApproved);
        Assert.Equal("MachineProposed", proposal.Provenance);
        Assert.False(proposal.CriticApproved);
        Assert.Null(proposal.CanonicalValidatedUtc);
        Assert.Null(proposal.CurriculumAdmittedUtc);
        Assert.Equal("AwaitingCritic", proposal.ValidationState);
        Assert.Equal(before, await CanonicalCountsAsync(fixture.Db));
    }

    private static LegendConnectMachineTeachingSubmission SameLanguageSubmission() =>
        new(
            "en",
            "en",
            "conversation.same-language.greeting",
            "conversation_semantics",
            "Connect already-governed greeting and welcome primitives.",
            0.9m,
            [
                new LegendConnectMachineTeachingExampleSubmission(
                    "Hello.",
                    null,
                    [
                        new LegendConnectMachineTeachingComponentSubmission(
                            "conversation_function",
                            "greeting",
                            "Hello")
                    ]),
                new LegendConnectMachineTeachingExampleSubmission(
                    "Welcome.",
                    null,
                    [
                        new LegendConnectMachineTeachingComponentSubmission(
                            "conversation_response",
                            "welcome",
                            "Welcome")
                    ])
            ],
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] = "greeting"
                        }),
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["conversation_response"] = "welcome"
                        }))
            ],
            LegendConnectMachineTeachingSubmission.SameLanguageSemanticCapability,
            LegendConnectMachineTeachingSubmission.ReusableSemanticCategory);

    private static LegendConnectMachineTeachingSubmission ResearchObservationSubmission()
    {
        var lineage = ResearchObservationLineage();
        return new LegendConnectMachineTeachingSubmission(
            "en",
            "en",
            "research.governed.external-observation",
            "research_observation_semantics",
            "Founder requested that two exact cited observations enter independent review.",
            0.9m,
            lineage.MaterialClaims.Select((item, index) =>
                new LegendConnectMachineTeachingExampleSubmission(
                    item.Statement,
                    null,
                    [
                        new LegendConnectMachineTeachingComponentSubmission(
                            "research_observation_role",
                            index == 0 ? "premise" : "conclusion",
                            item.Statement),
                        new LegendConnectMachineTeachingComponentSubmission(
                            "research_claim_identity",
                            item.NormalizedClaimIdentity,
                            item.Statement)
                    ])).ToArray(),
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string>
                    {
                        ["research_observation_role"] = "premise"
                    }),
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string>
                    {
                        ["research_observation_role"] = "conclusion"
                    }))],
            LegendConnectMachineTeachingSubmission.SameLanguageSemanticCapability,
            LegendConnectMachineTeachingSubmission.ReusableSemanticCategory,
            LegendConnectMachineObservationOrigin.ExternalResearchObservation,
            lineage);
    }

    private static LegendConnectResearchRetentionLineage ResearchObservationLineage()
    {
        var requestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var sessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        const string conclusion = "governed-research-conclusion";
        const string codeSha = "0123456789abcdef0123456789abcdef01234567";
        const string configuration = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        var claims = new[]
        {
            ResearchMaterial("claim-a", "The controlling record establishes claim A.", "source-a", "document-a", "citation-a"),
            ResearchMaterial("claim-b", "The independent record establishes claim B.", "source-b", "document-b", "citation-b")
        };
        var citations = claims.Select(item => new LegendConnectCitation(
            item.CitationIdentity,
            item.SourceIdentity,
            item.DocumentIdentity,
            "Observed source",
            "https://example.test/" + item.SourceIdentity,
            item.RetrievedUtc,
            "en")).ToArray();
        var lineage = new LegendConnectResearchRetentionLineage(
            requestId,
            sessionId,
            conclusion,
            LegendConnectResearchOutcomeState.Conclusion,
            LegendConnectResearchEvidenceOrigin.ExternalResearch,
            claims,
            citations,
            new LegendConnectResearchCitationValidationReceipt(
                true,
                LegendConnectResearchContracts.CitationPresentationPolicy,
                [],
                claims.Length,
                claims.Length,
                DateTime.UtcNow),
            LegendConnectResearchContracts.PublicAuthorizationProvenance,
            null,
            new string('0', 64),
            codeSha,
            configuration);
        return lineage with
        {
            ObservationIdentity =
                LegendConnectResearchRetentionContracts.ObservationIdentity(lineage)
        };
    }

    private static async Task SeedResearchObservationReceiptAsync(
        MasterAppDbContext db,
        LegendConnectResearchRetentionLineage lineage)
    {
        var correlation = lineage.SessionId.ToString("N");
        var now = DateTime.UtcNow;
        var events = new List<LegendConnectOperationalEvent>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Category = LegendConnectResearchContracts.ObservabilityCategory,
                Severity = "Info",
                Status = "Session:Conclusion",
                LanguageCode = "en",
                CorrelationId = correlation,
                Summary = $"code_sha={lineage.CodeSha};configuration={lineage.ConfigurationIdentity}",
                IsResolved = true,
                OccurredUtc = now
            },
            new()
            {
                Id = Guid.NewGuid(),
                Category = LegendConnectResearchContracts.ObservabilityCategory,
                Severity = "Info",
                Status = "Retention:ExternalObservation",
                LanguageCode = "en",
                CorrelationId = correlation,
                Summary = $"observation={lineage.ObservationIdentity};provenance=ExternalObservation",
                IsResolved = true,
                OccurredUtc = now
            }
        };
        events.AddRange(lineage.MaterialClaims.Select(claim =>
            new LegendConnectOperationalEvent
            {
                Id = Guid.NewGuid(),
                Category = LegendConnectResearchContracts.ObservabilityCategory,
                Severity = "Info",
                Status = "Claim:Supported",
                LanguageCode = "en",
                CorrelationId = correlation,
                Summary = $"evidence={claim.EvidenceIdentity}",
                IsResolved = true,
                OccurredUtc = now
            }));
        events.AddRange(lineage.MaterialClaims
            .Select(item => item.SourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Select(source => new LegendConnectOperationalEvent
            {
                Id = Guid.NewGuid(),
                Category = LegendConnectResearchContracts.ObservabilityCategory,
                Severity = "Info",
                Status = "Source:Opened",
                LanguageCode = "en",
                CorrelationId = correlation,
                Summary = $"source={source}",
                IsResolved = true,
                OccurredUtc = now
            }));
        events.AddRange(lineage.Citations.Select(citation =>
            new LegendConnectOperationalEvent
            {
                Id = Guid.NewGuid(),
                Category = LegendConnectResearchContracts.ObservabilityCategory,
                Severity = "Info",
                Status = "Citation:Used",
                LanguageCode = "en",
                CorrelationId = correlation,
                Summary = $"citation={citation.CitationIdentity}",
                IsResolved = true,
                OccurredUtc = now
            }));
        db.LegendConnectOperationalEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    private static LegendConnectResearchMaterialClaimEvidence ResearchMaterial(
        string claimIdentity,
        string statement,
        string sourceIdentity,
        string documentIdentity,
        string citationIdentity)
    {
        var now = DateTime.UtcNow;
        var passageHash = LegendLanguageIdentity.TextHash(statement);
        return new LegendConnectResearchMaterialClaimEvidence(
            LegendLanguageIdentity.TextHash("evidence|" + claimIdentity),
            claimIdentity,
            statement,
            sourceIdentity,
            documentIdentity,
            citationIdentity,
            new LegendConnectResearchPassageLocation(
                documentIdentity,
                0,
                statement.Length,
                statement,
                passageHash,
                "location-" + claimIdentity),
            LegendConnectResearchSourceClass.PrimaryOfficialRecord,
            now.AddDays(-1),
            now,
            LegendConnectResearchClaimSubject.General,
            LegendConnectResearchAuthorityScope.GeneralRecord,
            LegendConnectResearchEvidenceRelationship.DirectSupport,
            "lineage-" + claimIdentity,
            LegendConnectResearchFreshnessState.Current,
            "controlling-record",
            1,
            1,
            new LegendConnectResearchClaimTranslationLineage(
                "en", "en", "en", false, true, null, "NotRequired"),
            LegendConnectResearchExtractionMethod.ModelAssistedProposalValidatedAgainstExactPassage,
            new LegendConnectResearchMaterialClaimProvenance(
                "proposal-" + claimIdentity,
                sourceIdentity,
                documentIdentity,
                citationIdentity,
                "location-" + claimIdentity,
                passageHash,
                LegendConnectResearchContracts.ClaimEvidencePolicy,
                now,
                true,
                true,
                true,
                true,
                true,
                LegendLanguageIdentity.TextHash(statement)),
            LegendConnectResearchStatementKind.Fact,
            LegendConnectResearchClaimVerificationState.VerifiedByControllingEvidence);
    }

    private static LegendConnectMachineTeachingSubmission TranslationSubmission() =>
        new(
            "en",
            "ht",
            "conversation.translation.greeting",
            "conversation_semantics",
            "Retain a controlled translation distinction for independent review.",
            0.9m,
            [
                new LegendConnectMachineTeachingExampleSubmission(
                    "Hello.",
                    "Bonjou.",
                    [
                        new LegendConnectMachineTeachingComponentSubmission(
                            "conversation_function",
                            "greeting",
                            "Hello")
                    ]),
                new LegendConnectMachineTeachingExampleSubmission(
                    "Welcome.",
                    "Byenveni.",
                    [
                        new LegendConnectMachineTeachingComponentSubmission(
                            "conversation_response",
                            "welcome",
                            "Welcome")
                    ])
            ],
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] = "greeting"
                        }),
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["conversation_response"] = "welcome"
                        }))
            ],
            LegendConnectMachineTeachingSubmission.TranslationCapability,
            LegendConnectMachineTeachingSubmission.ReusableSemanticCategory);

    private static async Task SeedGovernedSameLanguageSemanticsAsync(
        MasterAppDbContext db,
        LegendConnectCurriculumService curriculum)
    {
        var familyIds = new List<Guid>();
        for (var index = 1; index <= 3; index++)
        {
            var result = await curriculum.SubmitFounderEnglishBatchAsync(
                new LegendConnectCurriculumBatchSubmission(
                    $"lai013.governed.primitives.{index}",
                    "Founder-governed primitives for same-language proposal admission.",
                    [
                        new LegendConnectCurriculumExampleSubmission(
                            "Hello.",
                            new Dictionary<string, string>
                            {
                                ["conversation_function"] = "greeting"
                            },
                            new LegendConnectMeaningGraphSubmission(
                                [
                                    new LegendConnectMeaningNodeSubmission(
                                        "source",
                                        "conversation_function",
                                        "greeting",
                                        "Hello")
                                ],
                                [])),
                        new LegendConnectCurriculumExampleSubmission(
                            "Welcome.",
                            new Dictionary<string, string>
                            {
                                ["conversation_response"] = "welcome"
                            },
                            new LegendConnectMeaningGraphSubmission(
                                [
                                    new LegendConnectMeaningNodeSubmission(
                                        "result",
                                        "conversation_response",
                                        "welcome",
                                        "Welcome")
                                ],
                                []))
                    ]));
            Assert.True(result.Succeeded, result.Message);
            Assert.True(result.CurriculumFamilyId.HasValue);
            familyIds.Add(result.CurriculumFamilyId.Value);
        }

        foreach (var familyId in familyIds)
        {
            await curriculum.ReevaluateHistoricalWorkItemAsync(
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                familyId,
                "en");
        }

        db.ChangeTracker.Clear();
    }

    private static async Task SeedTrustedTranslationEvidenceAsync(
        MasterAppDbContext db)
    {
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = "conversation.translation.greeting",
            SemanticCategory = "conversation_semantics",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        var sourceGreeting = Unit("en", "Hello.");
        var sourceWelcome = Unit("en", "Welcome.");
        var targetGreeting = Unit("ht", "Bonjou.");
        var targetWelcome = Unit("ht", "Byenveni.");
        var sourceGreetingExample = CurriculumExample(
            family,
            sourceGreeting,
            null);
        var sourceWelcomeExample = CurriculumExample(
            family,
            sourceWelcome,
            null);
        var targetGreetingExample = CurriculumExample(
            family,
            targetGreeting,
            sourceGreetingExample.Id);
        var targetWelcomeExample = CurriculumExample(
            family,
            targetWelcome,
            sourceWelcomeExample.Id);
        var greetingSignature = SemanticSignature(
            "conversation_function",
            "greeting");
        var welcomeSignature = SemanticSignature(
            "conversation_response",
            "welcome");
        var normalizedLineage = Assert.IsType<LegendMachineTeachingSemanticLineage>(
            LegendConnectCurriculumService
                .NormalizeMachineTeachingSemanticLineage(
                    new LegendLanguageTeacherFamilyProposal(
                        "conversation.translation.greeting",
                        "conversation_semantics",
                        "Retain a controlled translation distinction for independent review.",
                        0.9m,
                        [
                            new LegendLanguageTeacherExampleProposal(
                                "Hello.",
                                "Bonjou.",
                                [new LegendLanguageTeacherSemanticComponent(
                                    "conversation_function",
                                    "greeting",
                                    "Hello")]),
                            new LegendLanguageTeacherExampleProposal(
                                "Welcome.",
                                "Byenveni.",
                                [new LegendLanguageTeacherSemanticComponent(
                                    "conversation_response",
                                    "welcome",
                                    "Welcome")])
                        ],
                        TranslationSubmission().SemanticTransitions)));
        var pattern = new LegendLanguageStructuralPattern
        {
            Id = Guid.NewGuid(),
            PropositionSignature = "greeting-welcome-contrast",
            CurriculumFamilyId = family.Id,
            PairKey = string.Empty,
            LanguageCode = "en",
            VariationDimension = "conversation_function",
            MaturityState = "Supported",
            SupportCount = 1,
            IndependentSourceCount = 1,
            HumanVerifiedSupportCount = 1,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        db.AddRange(
            family,
            sourceGreeting,
            sourceWelcome,
            targetGreeting,
            targetWelcome,
            sourceGreetingExample,
            sourceWelcomeExample,
            targetGreetingExample,
            targetWelcomeExample,
            Anchor(sourceGreetingExample, greetingSignature),
            Anchor(targetGreetingExample, greetingSignature),
            Anchor(sourceWelcomeExample, welcomeSignature),
            Anchor(targetWelcomeExample, welcomeSignature),
            pattern,
            new LegendLanguageStructuralEvidence
            {
                Id = Guid.NewGuid(),
                StructuralPatternId = pattern.Id,
                CurriculumFamilyId = family.Id,
                PairKey = string.Empty,
                LanguageCode = "en",
                VariationDimension = "conversation_function",
                BaselineCurriculumExampleId = sourceGreetingExample.Id,
                ComparedCurriculumExampleId = sourceWelcomeExample.Id,
                BaselineVariationValue = "greeting",
                ComparedVariationValue = "welcome",
                EvidenceSignature = Guid.NewGuid().ToString("N"),
                IndependentSourceIdentity = "family:" + family.Id.ToString("N"),
                ContributionState = "Supported",
                IsHumanVerifiedSupport = true,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            },
            new LegendSemanticTransitionEvidence
            {
                Id = Guid.NewGuid(),
                TransitionSignature = Assert.Single(
                    normalizedLineage.TransitionSignatures),
                SourceLanguageCode = "en",
                ResultLanguageCode = "en",
                SourceCurriculumExampleId = sourceGreetingExample.Id,
                ResultCurriculumExampleId = sourceWelcomeExample.Id,
                IndependentSourceIdentity = "family:" + family.Id.ToString("N"),
                ContributionState = "Supported",
                IsHumanVerifiedSupport = true,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            },
            new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceTextUnitId = sourceGreeting.Id,
                TargetTextUnitId = targetGreeting.Id,
                Provider = "FounderApproved",
                Provenance = "FounderApproved",
                Confidence = 1m,
                QualityState = "Verified",
                HumanVerified = true,
                ObservationCount = 1,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            },
            new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceTextUnitId = sourceWelcome.Id,
                TargetTextUnitId = targetWelcome.Id,
                Provider = "FounderApproved",
                Provenance = "FounderApproved",
                Confidence = 1m,
                QualityState = "Verified",
                HumanVerified = true,
                ObservationCount = 1,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static LegendCurriculumExample CurriculumExample(
        LegendCurriculumFamily family,
        LegendLanguageTextUnit unit,
        Guid? derivedFrom) =>
        new()
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = unit.Id,
            LanguageCode = unit.LanguageCode,
            DerivedFromCurriculumExampleId = derivedFrom,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };

    private static LegendLanguageCompositionalAnchor Anchor(
        LegendCurriculumExample example,
        string semanticSignature) =>
        new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = example.LanguageCode,
            PairKey = example.DerivedFromCurriculumExampleId is null
                ? string.Empty
                : "en:ht",
            TextUnitId = example.TextUnitId,
            CurriculumFamilyId = example.CurriculumFamilyId,
            CurriculumExampleId = example.Id,
            Dimension = "semantic_component",
            Value = semanticSignature,
            SemanticSignature = semanticSignature,
            AnchorSignature = Guid.NewGuid().ToString("N"),
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };

    private static string SemanticSignature(
        string dimension,
        string value) =>
        LegendLanguageIdentity.TextHash(
            $"semantic|{dimension.Trim().ToLowerInvariant()}|" +
            value.Trim().ToLowerInvariant());

    private static LegendLanguageTextUnit Unit(
        string languageCode,
        string text) =>
        new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = languageCode,
            StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
            NormalizedHash = LegendLanguageIdentity.TextHash(text),
            Text = LegendLanguageIdentity.NormalizeText(text),
            Provenance = "FounderApproved",
            IsTrainingEligible = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

    private static async Task<CanonicalCounts> CanonicalCountsAsync(
        MasterAppDbContext db) =>
        new(
            await db.LegendLanguageTextUnits.CountAsync(),
            await db.LegendTranslationAlignments.CountAsync(),
            await db.LegendCurriculumFamilies.CountAsync(),
            await db.LegendCurriculumExamples.CountAsync(),
            await db.LegendLanguageStructuralPatterns.CountAsync(),
            await db.LegendSemanticTransitionEvidence.CountAsync());

    private sealed record CanonicalCounts(
        int TextUnits,
        int Alignments,
        int CurriculumFamilies,
        int CurriculumExamples,
        int StructuralPatterns,
        int SemanticTransitions);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            MasterAppDbContext db,
            LegendConnectCurriculumService curriculum,
            LegendConnectAutonomousLearningService service,
            RecordingCritic teacher)
        {
            Db = db;
            Curriculum = curriculum;
            Service = service;
            Teacher = teacher;
        }

        internal MasterAppDbContext Db { get; }
        internal LegendConnectCurriculumService Curriculum { get; }
        internal LegendConnectAutonomousLearningService Service { get; }
        internal RecordingCritic Teacher { get; }

        internal static Task<Fixture> CreateAsync()
        {
            var db = ControllerTestHelpers.BuildDb();
            var configuration = Configuration();
            var registry = new LegendLanguageRegistry(db, configuration);
            var corpus = new LegendConnectCorpusService(
                db,
                registry,
                NullLogger<LegendConnectCorpusService>.Instance);
            var curriculum = new LegendConnectCurriculumService(
                db,
                registry,
                corpus);
            var teacher = new RecordingCritic();
            var service = new LegendConnectAutonomousLearningService(
                db,
                registry,
                new NoopTranslationProvider(),
                new TranslationCapacityAuthority(
                    db,
                    configuration,
                    NullLogger<TranslationCapacityAuthority>.Instance),
                corpus,
                new LegendConnectAutonomousGapPlanner(db, registry),
                configuration,
                curriculum: curriculum,
                languageTeacher: teacher);
            return Task.FromResult(new Fixture(db, curriculum, service, teacher));
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
                    ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
                    ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0"
                })
            .Build();

    private sealed class NoopTranslationProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Conversation proposal submission cannot invoke translation.");
    }

    private sealed class RecordingCritic : ILegendConnectLanguageTeacher
    {
        internal int ProposeCalls { get; private set; }
        internal int CritiqueCalls { get; private set; }

        public LegendLanguageTeacherConfigurationPreflight Preflight(
            string role) =>
            LegendLanguageTeacherConfigurationPreflight.Ready(
                role,
                "test-recording-critic");

        public Task<LegendLanguageTeacherProposalResult> ProposeAsync(
            LegendLanguageTeacherProposalRequest request,
            CancellationToken cancellationToken = default)
        {
            ProposeCalls++;
            throw new InvalidOperationException(
                "Conversation MachineProposed artifacts must use the existing critic path, not request another proposal.");
        }

        public Task<LegendLanguageTeacherCritiqueResult> CritiqueAsync(
            LegendLanguageTeacherCritiqueRequest request,
            CancellationToken cancellationToken = default)
        {
            CritiqueCalls++;
            return Task.FromResult(
                new LegendLanguageTeacherCritiqueResult(
                    true,
                    true,
                    0.95m,
                    ["requires_canonical_validation"]));
        }
    }
}
