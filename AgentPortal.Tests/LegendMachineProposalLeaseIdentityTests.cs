using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;
using LearningFixture = AgentPortal.Tests.LegendConnectConversationMachineProposalTests.Fixture;

namespace AgentPortal.Tests;

public sealed class LegendMachineProposalLeaseIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ResubmissionDuringOwnedLease_ReusesOnlyTheStillValidExactEvidence(
        bool admissionLease, bool revokeSelectedEvidence)
    {
        await using var fixture = await LearningFixture.CreateAsync();
        var submission = LegendConnectConversationMachineProposalTests.NovelSameLanguageSubmission();
        await LegendConnectConversationMachineProposalTests.SeedGovernedSameLanguageSemanticsAsync(
            fixture.Db, fixture.Curriculum);
        var first = await fixture.Service.SubmitConversationMachineProposalAsync(submission);
        Assert.True(first.Succeeded, first.Message);
        await fixture.Service.ProcessOneAsync(); // Existing independent critic.
        if (admissionLease)
            await fixture.Service.ProcessOneAsync(); // Existing canonical validation.

        // Claim through the existing owner, without manufacturing approvals
        // or advancing the stage whose active lease is under test.
        var owner = admissionLease ? (object)fixture.Curriculum : fixture.Service;
        var claimMethod = owner.GetType().GetMethod(admissionLease
                ? "TryClaimSystemValidatedMachineProposalAsync"
                : "TryClaimCanonicalLanguageProposalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var claimed = await (Task<LegendLanguageTeacherProposal?>)claimMethod.Invoke(
            owner, [3, CancellationToken.None])!;
        Assert.NotNull(claimed);
        Assert.Equal(first.ProposalId, claimed.Id);
        var state = admissionLease ? "CurriculumAdmissionProcessing" : "CanonicalValidationProcessing";
        Assert.Equal(state, claimed.ValidationState);
        var canonicalLease = claimed.CanonicalValidationLeaseExpiresUtc;
        var curriculumLease = claimed.CurriculumAdmissionLeaseExpiresUtc;
        var canonicalAttempts = claimed.CanonicalValidationAttemptCount;
        var curriculumAttempts = claimed.CurriculumAdmissionAttemptCount;
        var originalIdentity = claimed.ProposalIdentity;
        var originalPayload = claimed.ProposalPayloadJson;

        var growth = await fixture.Curriculum.SubmitFounderBatchAsync(new LegendConnectCurriculumBatchSubmission(
            submission.FamilyKey, submission.SemanticCategory,
            [Example("Greetings traveler.", "greeting"), Example("You are welcome here.", "welcome")],
            submission.SemanticTransitions));
        Assert.True(growth.Succeeded, growth.Message);
        await fixture.Curriculum.ReevaluateHistoricalWorkItemAsync(
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            growth.CurriculumFamilyId!.Value, "en");

        if (revokeSelectedEvidence)
        {
            var selected = await (
                from example in fixture.Db.LegendCurriculumExamples
                join unit in fixture.Db.LegendLanguageTextUnits on example.TextUnitId equals unit.Id
                join family in fixture.Db.LegendCurriculumFamilies on example.CurriculumFamilyId equals family.Id
                where family.FamilyKey == submission.FamilyKey && unit.Text == "Hello."
                select example).SingleAsync();
            selected.SupersededUtc = DateTime.UtcNow;
            await fixture.Db.SaveChangesAsync();
        }
        fixture.Db.ChangeTracker.Clear();
        var retained = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync(item => item.Id == first.ProposalId);
        var candidate = await fixture.Db.LegendCorpusCandidates.SingleAsync(item => item.Id == first.CorpusCandidateId);
        Assert.True(LegendConnectAutonomousLearningService.TryReadMachineProposalPayload(
            retained.ProposalPayloadJson, out var retainedFamily, out _));
        Assert.Equal(!revokeSelectedEvidence,
            await LegendConnectAutonomousLearningService.IsCurrentCanonicalMachineEvidenceAsync(
                fixture.Db, fixture.Curriculum, candidate, retained, retainedFamily!, CancellationToken.None));
        var candidateState = candidate.TeacherProposalProcessingState;
        var candidateLease = candidate.TeacherProposalLeaseExpiresUtc;
        var candidateAttempts = candidate.TeacherProposalAttemptCount;
        var examplesBefore = await fixture.Db.LegendCurriculumExamples.CountAsync();

        var repeated = await fixture.Service.SubmitConversationMachineProposalAsync(submission);

        if (revokeSelectedEvidence)
        {
            Assert.False(repeated.ProposalAlreadyExisted && repeated.ProposalId == first.ProposalId,
                "A processing lease must not make revoked evidence reusable.");
        }
        else
        {
            Assert.True(repeated.Succeeded, repeated.Message);
            Assert.True(repeated.ProposalAlreadyExisted);
            Assert.Equal(first.ProposalId, repeated.ProposalId);
            Assert.Equal(state, repeated.State);
            Assert.Single(await fixture.Db.LegendLanguageTeacherProposals.ToArrayAsync());
            Assert.Equal(candidateState, candidate.TeacherProposalProcessingState);
            Assert.Equal(candidateLease, candidate.TeacherProposalLeaseExpiresUtc);
            Assert.Equal(candidateAttempts, candidate.TeacherProposalAttemptCount);
        }
        fixture.Db.ChangeTracker.Clear();
        retained = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync(item => item.Id == first.ProposalId);
        Assert.Equal(state, retained.ValidationState);
        Assert.Equal(canonicalLease, retained.CanonicalValidationLeaseExpiresUtc);
        Assert.Equal(curriculumLease, retained.CurriculumAdmissionLeaseExpiresUtc);
        Assert.Equal(canonicalAttempts, retained.CanonicalValidationAttemptCount);
        Assert.Equal(curriculumAttempts, retained.CurriculumAdmissionAttemptCount);
        Assert.Equal(originalIdentity, retained.ProposalIdentity);
        Assert.Equal(originalPayload, retained.ProposalPayloadJson);
        Assert.Null(retained.CurriculumAdmittedUtc);
        Assert.Equal(examplesBefore, await fixture.Db.LegendCurriculumExamples.CountAsync());
        Assert.Equal(1, fixture.Teacher.CritiqueCalls);
        Assert.Equal(0, fixture.Teacher.ProposeCalls);
    }

    private static LegendConnectCurriculumExampleSubmission Example(string text, string function) =>
        new(text, new Dictionary<string, string> { ["conversation_function"] = function },
            new LegendConnectMeaningGraphSubmission(
                [new LegendConnectMeaningNodeSubmission("meaning", "conversation_function", function, text.TrimEnd('.'))], []));
}
