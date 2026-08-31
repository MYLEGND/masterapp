using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectMachineLearningLifecycleTests
{
    [Fact]
    public async Task LifecycleSurface_PreservesCorrelationIdentityAcrossEachPersistedTransition()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = await CreateOperationsAsync(db);
        var now = DateTime.UtcNow;
        var candidate = Candidate(
            now.AddMinutes(-5),
            "Pending",
            attemptCount: 0);
        db.Add(candidate);
        await db.SaveChangesAsync();

        var candidatePage = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle", "en", null, null);
        var candidateRow = Assert.Single(candidatePage.Rows);
        Assert.Equal(candidate.Id.ToString("D"), candidateRow[0]);
        Assert.Equal("Pending", Value(candidatePage, candidateRow, "Actual state"));

        var proposal = Proposal(
            candidate,
            now.AddMinutes(-4),
            "AwaitingCritic",
            criticApproved: false);
        proposal.CriticConfidence = null;
        candidate.TeacherProposalProcessingState = "Pending";
        db.Add(proposal);
        await db.SaveChangesAsync();

        var criticPage = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle", "en", null, null);
        var criticRow = Assert.Single(criticPage.Rows);
        Assert.Equal(candidate.Id.ToString("D"), criticRow[0]);
        Assert.Equal(
            proposal.Id.ToString("D"),
            Value(criticPage, criticRow, "Proposal ID"));
        Assert.Equal("Pending", Value(criticPage, criticRow, "Critic result"));

        proposal.CriticApproved = true;
        proposal.CriticConfidence = 0.95m;
        proposal.CriticReasonCodesJson = "[\"requires_canonical_validation\"]";
        proposal.ValidationState = "AwaitingCanonicalValidation";
        proposal.UpdatedUtc = now.AddMinutes(-3);
        candidate.TeacherProposalProcessingState = "AwaitingCanonicalValidation";
        await db.SaveChangesAsync();

        var validatorPage = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle", "en", null, null);
        var validatorRow = Assert.Single(validatorPage.Rows);
        Assert.Equal(candidate.Id.ToString("D"), validatorRow[0]);
        Assert.Equal("Approved", Value(validatorPage, validatorRow, "Critic result"));
        Assert.Equal("Pending", Value(validatorPage, validatorRow, "Validator result"));

        proposal.ValidationState = "SystemValidated";
        proposal.Provenance = LegendConnectKnowledgeProvenance.SystemValidatedMachine;
        proposal.CanonicalValidationAttemptCount = 1;
        proposal.CanonicalValidatedUtc = now.AddMinutes(-2);
        proposal.UpdatedUtc = now.AddMinutes(-2);
        await db.SaveChangesAsync();

        var admissionPage = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle", "en", null, null);
        var admissionRow = Assert.Single(admissionPage.Rows);
        Assert.Equal(candidate.Id.ToString("D"), admissionRow[0]);
        Assert.Equal("SystemValidated", Value(admissionPage, admissionRow, "Validator result"));
        Assert.Equal("Pending", Value(admissionPage, admissionRow, "Admission result"));

        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = proposal.FamilyKey,
            SemanticCategory = proposal.SemanticCategory,
            Provenance = LegendConnectKnowledgeProvenance.SystemValidatedMachine,
            CreatedUtc = now.AddMinutes(-1),
            UpdatedUtc = now.AddMinutes(-1)
        };
        proposal.ValidationState = "CurriculumAdmitted";
        proposal.CurriculumAdmissionAttemptCount = 1;
        proposal.CurriculumAdmittedUtc = now.AddMinutes(-1);
        proposal.UpdatedUtc = now.AddMinutes(-1);
        db.Add(family);
        await db.SaveChangesAsync();

        var admittedPage = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle", "en", null, null);
        var admittedRow = Assert.Single(admittedPage.Rows);
        Assert.Equal(candidate.Id.ToString("D"), admittedRow[0]);
        Assert.Equal(
            proposal.Id.ToString("D"),
            Value(admittedPage, admittedRow, "Proposal ID"));
        Assert.Equal(
            family.Id.ToString("D"),
            Value(admittedPage, admittedRow, "Admission identity"));
        Assert.Equal("CurriculumAdmitted", Value(admittedPage, admittedRow, "Actual state"));
    }

    [Fact]
    public async Task LifecycleSurface_ProjectsLineageAndStageFailuresWithoutCallingPendingLearned()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = await CreateOperationsAsync(db);
        var now = DateTime.UtcNow;

        var configurationFailure = Candidate(
            now.AddMinutes(-8),
            "Failed",
            attemptCount: 0,
            failureCode: "language_teacher_configuration_missing");
        var criticRejected = Candidate(
            now.AddMinutes(-6),
            "CriticRejected",
            attemptCount: 1);
        var validatorRejected = Candidate(
            now.AddMinutes(-4),
            "AwaitingCanonicalValidation",
            attemptCount: 1);
        var admitted = Candidate(
            now.AddMinutes(-2),
            "AwaitingCanonicalValidation",
            attemptCount: 1);
        var criticProposal = Proposal(
            criticRejected,
            now.AddMinutes(-5),
            "CriticRejected",
            criticApproved: false,
            criticReasons: "[\"insufficient_relevant_evidence\"]");
        var validatorProposal = Proposal(
            validatorRejected,
            now.AddMinutes(-3),
            "Rejected",
            criticApproved: true,
            validatorAttempts: 1,
            validatorCompletedUtc: now.AddMinutes(-3),
            validatorFailureCode: "canonical_family_boundary_mismatch");
        var admittedProposal = Proposal(
            admitted,
            now.AddMinutes(-1),
            "CurriculumAdmitted",
            criticApproved: true,
            provenance: LegendConnectKnowledgeProvenance.SystemValidatedMachine,
            validatorAttempts: 1,
            validatorCompletedUtc: now.AddMinutes(-2),
            admissionAttempts: 1,
            admissionCompletedUtc: now.AddMinutes(-1));
        var admittedFamily = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = admittedProposal.FamilyKey,
            SemanticCategory = "governed_reasoning",
            Provenance = LegendConnectKnowledgeProvenance.SystemValidatedMachine,
            CreatedUtc = now.AddMinutes(-1),
            UpdatedUtc = now.AddMinutes(-1)
        };

        db.AddRange(
            configurationFailure,
            criticRejected,
            validatorRejected,
            admitted,
            criticProposal,
            validatorProposal,
            admittedProposal,
            admittedFamily);
        await db.SaveChangesAsync();

        var page = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle",
            "en",
            null,
            null);

        Assert.Equal("machine-learning-lifecycle", page.Section);
        Assert.Equal(4, page.Rows.Count);

        var failedRow = Row(page, configurationFailure.Id);
        Assert.Equal(
            configurationFailure.Id.ToString("D"),
            Value(page, failedRow, "Lifecycle / candidate ID"));
        Assert.Equal("—", Value(page, failedRow, "Proposal ID"));
        Assert.Equal("Failed", Value(page, failedRow, "Actual state"));
        Assert.Equal("0", Value(page, failedRow, "Candidate attempts"));
        Assert.Equal(
            "language_teacher_configuration_missing",
            Value(page, failedRow, "Candidate failure"));
        Assert.Equal("Not created", Value(page, failedRow, "Critic result"));

        var criticRow = Row(page, criticRejected.Id);
        Assert.Equal(
            criticProposal.Id.ToString("D"),
            Value(page, criticRow, "Proposal ID"));
        Assert.Equal("CriticRejected", Value(page, criticRow, "Actual state"));
        Assert.Equal("Rejected", Value(page, criticRow, "Critic result"));
        Assert.Equal(
            "insufficient_relevant_evidence",
            Value(page, criticRow, "Critic reasons"));
        Assert.Equal("Not started", Value(page, criticRow, "Validator result"));
        Assert.Equal("—", Value(page, criticRow, "Admission identity"));

        var validatorRow = Row(page, validatorRejected.Id);
        Assert.Equal("Rejected", Value(page, validatorRow, "Actual state"));
        Assert.Equal("Approved", Value(page, validatorRow, "Critic result"));
        Assert.Equal("1", Value(page, validatorRow, "Validator attempts"));
        Assert.Equal("Rejected", Value(page, validatorRow, "Validator result"));
        Assert.Equal(
            "canonical_family_boundary_mismatch",
            Value(page, validatorRow, "Validator failure"));
        Assert.Equal("Not started", Value(page, validatorRow, "Admission result"));

        var admittedRow = Row(page, admitted.Id);
        Assert.Equal(
            admitted.Id.ToString("D"),
            Value(page, admittedRow, "Lifecycle / candidate ID"));
        Assert.Equal(
            admittedProposal.Id.ToString("D"),
            Value(page, admittedRow, "Proposal ID"));
        Assert.Equal(
            LegendConnectKnowledgeProvenance.SystemValidatedMachine,
            Value(page, admittedRow, "Proposal provenance"));
        Assert.Equal("CurriculumAdmitted", Value(page, admittedRow, "Actual state"));
        Assert.Equal("SystemValidated", Value(page, admittedRow, "Validator result"));
        Assert.Equal("CurriculumAdmitted", Value(page, admittedRow, "Admission result"));
        Assert.Equal(
            admittedFamily.Id.ToString("D"),
            Value(page, admittedRow, "Admission identity"));
        Assert.NotEqual("—", Value(page, admittedRow, "Validator completed"));
        Assert.NotEqual("—", Value(page, admittedRow, "Admission completed"));

        var serializedPage = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("private-candidate-source", serializedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-proposal-rationale", serializedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-proposal-payload", serializedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("learned", serializedPage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LifecycleSurface_SanitizesUntrustedDiagnosticsAndNeverProjectsPrivateCorpusContent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = await CreateOperationsAsync(db);
        var candidate = Candidate(
            DateTime.UtcNow.AddMinutes(-1),
            "CriticRejected",
            attemptCount: 1,
            failureCode: "<private-corpus-sentence>");
        candidate.SourceText = "PRIVATE CANDIDATE CORPUS CONTENT";
        var proposal = Proposal(
            candidate,
            DateTime.UtcNow,
            "CriticRejected",
            criticApproved: false,
            criticReasons: "[\"PRIVATE CRITIC CORPUS CONTENT\"]");
        proposal.Rationale = "PRIVATE PROPOSAL RATIONALE";
        proposal.ProposalPayloadJson = "{\"text\":\"PRIVATE PROPOSAL PAYLOAD\"}";
        db.AddRange(candidate, proposal);
        await db.SaveChangesAsync();

        var page = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle",
            "en",
            null,
            null);

        var row = Assert.Single(page.Rows);
        Assert.Equal(
            "withheld_invalid_diagnostic",
            Value(page, row, "Candidate failure"));
        Assert.Equal(
            "withheld_invalid_critic_result",
            Value(page, row, "Critic reasons"));
        var serializedPage = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("PRIVATE", serializedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("<private", serializedPage, StringComparison.Ordinal);

        var retainedKnowledge = await operations.SearchRetainedKnowledgeAsync(
            "PRIVATE",
            "en",
            "en");
        Assert.Empty(retainedKnowledge.Items);
    }

    [Fact]
    public async Task LifecycleSurface_UsesStableKeysetPaginationAndIdentitySearch()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = await CreateOperationsAsync(db);
        var start = DateTime.UtcNow.AddHours(-2);
        var candidates = Enumerable.Range(0, 55)
            .Select(index => Candidate(
                start.AddMinutes(index),
                "Failed",
                attemptCount: index % 3,
                failureCode: "language_teacher_timeout"))
            .ToArray();
        db.AddRange(candidates);
        await db.SaveChangesAsync();

        var first = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle",
            "en",
            null,
            null);
        var second = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle",
            "en",
            null,
            first.NextCursor);

        Assert.Equal(50, first.Rows.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(5, second.Rows.Count);
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Rows.Select(item => item[0]).Intersect(
            second.Rows.Select(item => item[0]),
            StringComparer.Ordinal));

        var searched = await operations.GetFounderSectionPageAsync(
            "machine-learning-lifecycle",
            "en",
            candidates[17].Id.ToString("D"),
            null);
        var searchedRow = Assert.Single(searched.Rows);
        Assert.Equal(candidates[17].Id.ToString("D"), searchedRow[0]);
    }

    [Fact]
    public async Task LifecycleSurface_RehydratesSameDurableLineageWithoutMutationAfterContextRestart()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(databaseName, databaseRoot)
            .Options;
        var candidateId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        await using (var firstDb = new MasterAppDbContext(options))
        {
            var operations = await CreateOperationsAsync(firstDb);
            var candidate = Candidate(
                DateTime.UtcNow.AddMinutes(-1),
                "AwaitingCanonicalValidation",
                attemptCount: 1);
            candidate.Id = candidateId;
            var proposal = Proposal(
                candidate,
                DateTime.UtcNow,
                "AwaitingCanonicalValidation",
                criticApproved: true);
            proposal.Id = proposalId;
            firstDb.AddRange(candidate, proposal);
            await firstDb.SaveChangesAsync();

            var firstPage = await operations.GetFounderSectionPageAsync(
                "machine-learning-lifecycle",
                "en",
                null,
                null);
            Assert.Equal(candidateId.ToString("D"), Assert.Single(firstPage.Rows)[0]);
        }

        await using (var restartedDb = new MasterAppDbContext(options))
        {
            var operations = await CreateOperationsAsync(restartedDb);
            var restartedPage = await operations.GetFounderSectionPageAsync(
                "machine-learning-lifecycle",
                "en",
                null,
                null);
            var row = Assert.Single(restartedPage.Rows);
            Assert.Equal(candidateId.ToString("D"), row[0]);
            Assert.Equal(
                proposalId.ToString("D"),
                Value(restartedPage, row, "Proposal ID"));
            Assert.Equal(1, await restartedDb.LegendCorpusCandidates.CountAsync());
            Assert.Equal(1, await restartedDb.LegendLanguageTeacherProposals.CountAsync());
        }
    }

    private static async Task<LegendConnectOperations> CreateOperationsAsync(
        MasterAppDbContext db)
    {
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        await registry.ListEnabledTranslationLanguagesAsync();
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        return new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration);
    }

    private static LegendCorpusCandidate Candidate(
        DateTime createdUtc,
        string state,
        int attemptCount,
        string? failureCode = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            SourceLanguageCode = "en",
            TargetLanguageCode = "en",
            SourceText = "private-candidate-source",
            SourceTextHash = Guid.NewGuid().ToString("N"),
            Category = "same_language_semantic:reusable_semantic",
            Provenance = "MachineConversation",
            ProcessingState = "ConversationProposal",
            TeacherProposalProcessingState = state,
            TeacherProposalAttemptCount = attemptCount,
            TeacherProposalProcessedUtc = createdUtc.AddSeconds(1),
            TeacherProposalFailureCode = failureCode,
            CreatedUtc = createdUtc
        };

    private static LegendLanguageTeacherProposal Proposal(
        LegendCorpusCandidate candidate,
        DateTime updatedUtc,
        string state,
        bool criticApproved,
        string criticReasons = "[]",
        string provenance = "MachineProposed",
        int validatorAttempts = 0,
        DateTime? validatorCompletedUtc = null,
        string? validatorFailureCode = null,
        int admissionAttempts = 0,
        DateTime? admissionCompletedUtc = null,
        string? admissionFailureCode = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            CorpusCandidateId = candidate.Id,
            ProposalIdentity = Guid.NewGuid().ToString("N"),
            PairKey = "en:en",
            SourceLanguageCode = "en",
            TargetLanguageCode = "en",
            EvidenceIdentityHash = Guid.NewGuid().ToString("N"),
            FamilyKey = "lifecycle.family." + Guid.NewGuid().ToString("N"),
            SemanticCategory = "governed_reasoning",
            Rationale = "private-proposal-rationale",
            Confidence = 0.9m,
            ProposalPayloadJson = "{\"private\":\"private-proposal-payload\"}",
            CriticApproved = criticApproved,
            CriticConfidence = criticApproved ? 0.95m : 0.25m,
            CriticReasonCodesJson = criticReasons,
            ValidationState = state,
            Provenance = provenance,
            CanonicalValidationAttemptCount = validatorAttempts,
            CanonicalValidatedUtc = validatorCompletedUtc,
            CanonicalValidationFailureCode = validatorFailureCode,
            CurriculumAdmissionAttemptCount = admissionAttempts,
            CurriculumAdmittedUtc = admissionCompletedUtc,
            CurriculumAdmissionFailureCode = admissionFailureCode,
            CreatedUtc = updatedUtc.AddMinutes(-1),
            UpdatedUtc = updatedUtc
        };

    private static IReadOnlyList<string> Row(
        LegendConnectFounderSectionPageSnapshot page,
        Guid correlationId) =>
        page.Rows.Single(item =>
            string.Equals(
                item[0],
                correlationId.ToString("D"),
                StringComparison.Ordinal));

    private static string Value(
        LegendConnectFounderSectionPageSnapshot page,
        IReadOnlyList<string> row,
        string column)
    {
        var index = page.Columns
            .Select((value, index) => new { value, index })
            .Single(item => string.Equals(
                item.value,
                column,
                StringComparison.Ordinal))
            .index;
        return row[index];
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["LegendConnect:CorpusAcquisition:Enabled"] = "true"
                })
            .Build();
}
