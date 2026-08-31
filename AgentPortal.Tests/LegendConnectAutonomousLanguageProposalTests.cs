using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectAutonomousLanguageProposalTests
{
    [Fact]
    public async Task ExistingQueuedCandidate_ProducesIndependentMachineProposalArtifactsWithoutCanonicalAdmission()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);

        var candidate = Candidate(
            "phase3-approved",
            "Please confirm the meeting.",
            proposalState: "Pending");

        await SeedGovernedCriticEvidenceAsync(db, candidate);

        var before = await CanonicalCountsAsync(db);

        var teacher = RecordingTeacher.ApprovedTwoFamilies();

        var provider = new NoopTranslationProvider();

        var service = Service(
            db,
            configuration,
            registry,
            corpus,
            provider,
            teacher);

        await service.ProcessOneAsync();

        Assert.Equal(1, teacher.ProposeCalls);
        Assert.Equal(2, teacher.CritiqueCalls);
        Assert.Equal(0, provider.TranslateCalls);

        var proposals = await db.LegendLanguageTeacherProposals
            .OrderBy(item => item.FamilyKey)
            .ToListAsync();

        Assert.Equal(2, proposals.Count);
        Assert.Equal(
            2,
            proposals
                .Select(item => item.ProposalIdentity)
                .Distinct(StringComparer.Ordinal)
                .Count());

        Assert.All(
            proposals,
            item =>
            {
                Assert.Equal(
                    candidate.Id,
                    item.CorpusCandidateId);
                Assert.Equal(
                    "MachineProposed",
                    item.Provenance);
                Assert.Equal(
                    "AwaitingCanonicalValidation",
                    item.ValidationState);
                Assert.True(item.CriticApproved);
            });

        var persistedCandidate =
            await db.LegendCorpusCandidates
                .SingleAsync(item => item.Id == candidate.Id);

        Assert.Equal(
            "AwaitingCanonicalValidation",
            persistedCandidate.TeacherProposalProcessingState);

        Assert.Null(
            persistedCandidate.TeacherProposalFailureCode);

        Assert.NotNull(
            persistedCandidate.TeacherProposalProcessedUtc);

        var after = await CanonicalCountsAsync(db);

        Assert.Equal(before, after);

        Assert.DoesNotContain(
            await db.LegendTranslationAlignments.ToListAsync(),
            item => item.QualityState == "SystemValidated");

        // Retry/idempotency proof: terminal proposal state cannot fan out
        // another logical proposal.
        await service.ProcessOneAsync();

        Assert.Equal(
            2,
            await db.LegendLanguageTeacherProposals.CountAsync());

        Assert.Equal(1, teacher.ProposeCalls);
        Assert.Equal(2, teacher.CritiqueCalls);
        Assert.Equal(0, provider.TranslateCalls);
    }

    [Fact]
    public async Task CriticRejection_RemainsMachineArtifactAndCannotBecomeCanonicalKnowledge()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);

        var candidate = Candidate(
            "phase3-rejected",
            "Please review the request.",
            proposalState: "Pending");

        await SeedGovernedCriticEvidenceAsync(db, candidate);

        var before = await CanonicalCountsAsync(db);
        var teacher = RecordingTeacher.Rejected();

        await Service(
            db,
            configuration,
            registry,
            corpus,
            new NoopTranslationProvider(),
            teacher)
            .ProcessOneAsync();

        var proposal = Assert.Single(
            await db.LegendLanguageTeacherProposals.ToListAsync());

        Assert.False(proposal.CriticApproved);
        Assert.Equal(
            "CriticRejected",
            proposal.ValidationState);
        Assert.Equal(
            "MachineProposed",
            proposal.Provenance);

        var persistedCandidate =
            await db.LegendCorpusCandidates
                .SingleAsync(item => item.Id == candidate.Id);

        Assert.Equal(
            "CriticRejected",
            persistedCandidate.TeacherProposalProcessingState);

        Assert.Equal(
            before,
            await CanonicalCountsAsync(db));
    }

    [Fact]
    public async Task InsufficientGovernedEvidence_DoesNotCallTeacher()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);

        var candidate = Candidate(
            "phase3-insufficient",
            "This source has no trusted pair evidence.",
            proposalState: "Pending");

        db.Add(
            Unit(
                "en",
                candidate.SourceText,
                "FounderApproved"));

        db.Add(candidate);

        await db.SaveChangesAsync();

        var teacher = RecordingTeacher.ApprovedTwoFamilies();
        var provider = new NoopTranslationProvider();

        await Service(
            db,
            configuration,
            registry,
            corpus,
            provider,
            teacher)
            .ProcessOneAsync();

        Assert.Equal(0, teacher.ProposeCalls);
        Assert.Equal(0, teacher.CritiqueCalls);
        Assert.Equal(0, provider.TranslateCalls);
        Assert.Empty(
            await db.LegendLanguageTeacherProposals.ToListAsync());

        var persisted =
            await db.LegendCorpusCandidates
                .SingleAsync(item => item.Id == candidate.Id);

        Assert.Equal(
            "InsufficientEvidence",
            persisted.TeacherProposalProcessingState);

        Assert.Equal(
            "language_teacher_semantic_family_lineage_unproven",
            persisted.TeacherProposalFailureCode);
    }

    [Fact]
    public async Task MissingConfiguration_PreflightDoesNotLeaseOrConsumeAttempts_AndCooldownDeduplicatesIssue()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var candidate = Candidate(
            "phase3-missing-configuration",
            "Do not lease this candidate without configuration.",
            proposalState: "Pending");
        db.Add(candidate);
        await db.SaveChangesAsync();
        var teacher = PreflightTeacher.MissingConfiguration();
        var service = Service(
            db,
            configuration,
            registry,
            corpus,
            new NoopTranslationProvider(),
            teacher);

        await service.ProcessOneAsync();
        await service.ProcessOneAsync();

        var persisted = await db.LegendCorpusCandidates
            .SingleAsync(item => item.Id == candidate.Id);
        Assert.Equal(0, persisted.TeacherProposalAttemptCount);
        Assert.Equal(
            "Pending",
            persisted.TeacherProposalProcessingState);
        Assert.Null(persisted.TeacherProposalLeaseExpiresUtc);
        Assert.Equal(0, teacher.ProposeCalls);
        Assert.Equal(0, teacher.CritiqueCalls);

        var issues = await db.LegendConnectOperationalEvents
            .Where(item =>
                item.Category == "LanguageTeacherCircuitIssue")
            .OrderBy(item => item.CorrelationId)
            .ToListAsync();
        var occurrences = await db.LegendConnectOperationalEvents
            .Where(item =>
                item.Category == "LanguageTeacherFailureOccurrence")
            .ToListAsync();
        Assert.Equal(2, issues.Count);
        Assert.Equal(2, occurrences.Count);
        Assert.Contains(
            issues,
            item => item.CorrelationId?.StartsWith(
                "teacher:",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            issues,
            item => item.CorrelationId?.StartsWith(
                "critic:",
                StringComparison.Ordinal) == true);
        foreach (var issue in issues)
        {
            Assert.False(issue.IsResolved);
            Assert.Equal("CircuitOpen", issue.Status);
            Assert.Equal(
                "language_teacher_configuration_missing",
                issue.ErrorCode);
            var occurrence = Assert.Single(
                occurrences.Where(item =>
                    item.CorrelationId == issue.CorrelationId));
            Assert.Contains(
                "Occurrences=1;",
                issue.Summary ?? string.Empty);
            Assert.Equal(occurrence.OccurredUtc, issue.OccurredUtc);
        }
    }

    [Fact]
    public async Task ConcurrentMissingConfiguration_PreflightsCreateOneFingerprintIssueWithoutLeasing()
    {
        var connectionString =
            $"Data Source=file:legend-teacher-{Guid.NewGuid():N}" +
            "?mode=memory&cache=shared;Default Timeout=30";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var candidate = Candidate(
            "phase3-concurrent-missing-configuration",
            "Keep this unleased across worker instances.",
            proposalState: "Pending");
        await using (var seed = new MasterAppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Add(candidate);
            await seed.SaveChangesAsync();
        }

        var configuration = Configuration();
        var teacher = PreflightTeacher.MissingConfiguration();
        await using var dbOne = new MasterAppDbContext(options);
        await using var dbTwo = new MasterAppDbContext(options);
        var registryOne = new LegendLanguageRegistry(
            dbOne,
            configuration);
        var registryTwo = new LegendLanguageRegistry(
            dbTwo,
            configuration);
        var first = Service(
            dbOne,
            configuration,
            registryOne,
            new LegendConnectCorpusService(
                dbOne,
                registryOne,
                NullLogger<LegendConnectCorpusService>.Instance),
            new NoopTranslationProvider(),
            teacher);
        var second = Service(
            dbTwo,
            configuration,
            registryTwo,
            new LegendConnectCorpusService(
                dbTwo,
                registryTwo,
                NullLogger<LegendConnectCorpusService>.Instance),
            new NoopTranslationProvider(),
            teacher);

        await Task.WhenAll(
            first.ProcessOneAsync(),
            second.ProcessOneAsync());

        await using var verify = new MasterAppDbContext(options);
        var persisted = await verify.LegendCorpusCandidates
            .SingleAsync(item => item.Id == candidate.Id);
        var issues = await verify.LegendConnectOperationalEvents
            .Where(item =>
                item.Category == "LanguageTeacherCircuitIssue")
            .ToListAsync();
        var occurrences = await verify.LegendConnectOperationalEvents
            .Where(item =>
                item.Category == "LanguageTeacherFailureOccurrence")
            .OrderBy(item => item.OccurredUtc)
            .ToListAsync();

        Assert.Equal(2, issues.Count);
        Assert.InRange(occurrences.Count, 2, 4);
        foreach (var issue in issues)
        {
            var roleOccurrences = occurrences
                .Where(item =>
                    item.CorrelationId == issue.CorrelationId)
                .ToArray();
            Assert.InRange(roleOccurrences.Length, 1, 2);
            Assert.Contains(
                $"Occurrences={roleOccurrences.Length};",
                issue.Summary ?? string.Empty);
            Assert.Equal(
                roleOccurrences[0].OccurredUtc,
                issue.OccurredUtc);
        }
        Assert.Equal(0, persisted.TeacherProposalAttemptCount);
        Assert.Null(persisted.TeacherProposalLeaseExpiresUtc);
        Assert.Equal(0, teacher.ProposeCalls);
        Assert.Equal(0, teacher.CritiqueCalls);
    }

    [Fact]
    public async Task TimeoutCircuit_CoolsDownWithoutAnotherAttempt_ThenRecordsRecovery()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var candidate = Candidate(
            "phase3-timeout-recovery",
            "Recover this governed proposal after cooldown.",
            proposalState: "Pending");
        await SeedGovernedCriticEvidenceAsync(db, candidate);
        var teacher = RecordingTeacher.RecoveringAfterTimeout();
        var service = Service(
            db,
            configuration,
            registry,
            corpus,
            new NoopTranslationProvider(),
            teacher);

        await service.ProcessOneAsync();

        var persisted = await db.LegendCorpusCandidates
            .SingleAsync(item => item.Id == candidate.Id);
        Assert.Equal(1, persisted.TeacherProposalAttemptCount);
        Assert.Equal(
            "language_teacher_timeout",
            persisted.TeacherProposalFailureCode);
        Assert.Equal(1, teacher.ProposeCalls);
        var issue = await db.LegendConnectOperationalEvents
            .SingleAsync(item =>
                item.Category == "LanguageTeacherCircuitIssue");
        Assert.False(issue.IsResolved);
        Assert.Equal("CircuitOpen", issue.Status);

        persisted.TeacherProposalLeaseExpiresUtc =
            DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        await service.ProcessOneAsync();

        Assert.Equal(1, persisted.TeacherProposalAttemptCount);
        Assert.Equal(1, teacher.ProposeCalls);

        await ExpireLanguageTeacherCooldownAsync(
            db,
            persisted);
        await service.ProcessOneAsync();

        Assert.Equal(2, teacher.ProposeCalls);
        Assert.Equal(1, teacher.CritiqueCalls);
        Assert.Equal(2, persisted.TeacherProposalAttemptCount);
        Assert.Equal(
            "AwaitingCanonicalValidation",
            persisted.TeacherProposalProcessingState);
        Assert.True(issue.IsResolved);
        Assert.Equal("ProviderRecovered", issue.Status);
        var recovery = Assert.Single(
            await db.LegendConnectOperationalEvents
                .Where(item =>
                    item.Category ==
                        "LanguageTeacherCircuitRecovery")
                .ToListAsync());
        Assert.Equal(issue.CorrelationId, recovery.CorrelationId);
        Assert.True(recovery.IsResolved);
        Assert.Contains(
            "RecoveredUtc=",
            issue.Summary ?? string.Empty);
    }

    [Fact]
    public async Task CriticAuthenticationFailure_OpensOnlyCriticFingerprintCircuit()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var candidate = Candidate(
            "phase3-critic-authentication",
            "Keep the critic failure role isolated.",
            proposalState: "Pending");
        await SeedGovernedCriticEvidenceAsync(db, candidate);
        var teacher = RecordingTeacher.CriticAuthenticationFailure();

        await Service(
            db,
            configuration,
            registry,
            corpus,
            new NoopTranslationProvider(),
            teacher)
            .ProcessOneAsync();

        var persisted = await db.LegendCorpusCandidates
            .SingleAsync(item => item.Id == candidate.Id);
        var issue = Assert.Single(
            await db.LegendConnectOperationalEvents
                .Where(item =>
                    item.Category == "LanguageTeacherCircuitIssue")
                .ToListAsync());
        Assert.Equal(1, teacher.ProposeCalls);
        Assert.Equal(1, teacher.CritiqueCalls);
        Assert.Equal(1, persisted.TeacherProposalAttemptCount);
        Assert.Equal(
            "language_teacher_authentication_failed",
            persisted.TeacherProposalFailureCode);
        Assert.StartsWith(
            "critic:",
            issue.CorrelationId ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(
            "language_teacher_authentication_failed",
            issue.ErrorCode);
        Assert.False(issue.IsResolved);
    }

    [Fact]
    public async Task TeacherFailure_RetriesThroughExistingCandidateAuthorityAndStopsAtBound()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);

        var candidate = Candidate(
            "phase3-retry",
            "Retry this governed proposal.",
            proposalState: "Pending");

        await SeedGovernedCriticEvidenceAsync(db, candidate);

        var teacher = RecordingTeacher.Failing();
        var provider = new NoopTranslationProvider();

        var service = Service(
            db,
            configuration,
            registry,
            corpus,
            provider,
            teacher);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await service.ProcessOneAsync();

            var persisted =
                await db.LegendCorpusCandidates
                    .SingleAsync(item => item.Id == candidate.Id);

            Assert.Equal(
                attempt,
                persisted.TeacherProposalAttemptCount);

            if (attempt < 3)
            {
                Assert.Equal(
                    "Processing",
                    persisted.TeacherProposalProcessingState);

                await ExpireLanguageTeacherCooldownAsync(
                    db,
                    persisted);
            }
        }

        var final =
            await db.LegendCorpusCandidates
                .SingleAsync(item => item.Id == candidate.Id);

        Assert.Equal(
            "Failed",
            final.TeacherProposalProcessingState);

        Assert.Equal(
            3,
            final.TeacherProposalAttemptCount);

        Assert.Null(final.TeacherProposalLeaseExpiresUtc);
        Assert.NotNull(final.TeacherProposalProcessedUtc);

        Assert.Equal(3, teacher.ProposeCalls);
        Assert.Equal(0, teacher.CritiqueCalls);
        Assert.Equal(0, provider.TranslateCalls);

        Assert.Empty(
            await db.LegendLanguageTeacherProposals.ToListAsync());

        await service.ProcessOneAsync();

        Assert.Equal(3, teacher.ProposeCalls);
    }

    private static async Task ExpireLanguageTeacherCooldownAsync(
        MasterAppDbContext db,
        LegendCorpusCandidate candidate)
    {
        candidate.TeacherProposalLeaseExpiresUtc =
            DateTime.UtcNow.AddMinutes(-1);
        var oldOccurrenceUtc = DateTime.UtcNow.AddMinutes(-20);
        var occurrences = await db.LegendConnectOperationalEvents
            .Where(item =>
                item.Category == "LanguageTeacherFailureOccurrence")
            .ToListAsync();
        foreach (var occurrence in occurrences)
            occurrence.OccurredUtc = oldOccurrenceUtc;
        await db.SaveChangesAsync();
    }

    private static async Task SeedGovernedCriticEvidenceAsync(
        MasterAppDbContext db,
        LegendCorpusCandidate candidate)
    {
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = "machine.conversation",
            SemanticCategory = "Conversation",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        var sourceOne = Unit(
            "en",
            candidate.SourceText,
            "FounderApproved");
        var sourceTwo = Unit(
            "en",
            "A controlled request contrast.",
            "FounderApproved");
        var targetOne = Unit(
            "ht",
            "Yon deklarasyon kontwole.",
            "FounderApproved");
        var targetTwo = Unit(
            "ht",
            "Yon demann kontwole.",
            "FounderApproved");
        var sourceExampleOne = Example(family, sourceOne, null);
        var sourceExampleTwo = Example(family, sourceTwo, null);
        var targetExampleOne = Example(
            family,
            targetOne,
            sourceExampleOne.Id);
        var targetExampleTwo = Example(
            family,
            targetTwo,
            sourceExampleTwo.Id);
        var statementSignature = SemanticSignature("intent", "statement");
        var requestSignature = SemanticSignature("intent", "request");
        var pattern = new LegendLanguageStructuralPattern
        {
            Id = Guid.NewGuid(),
            PropositionSignature = "statement-request-contrast",
            CurriculumFamilyId = family.Id,
            PairKey = string.Empty,
            LanguageCode = "en",
            VariationDimension = "intent",
            MaturityState = "Supported",
            SupportCount = 1,
            IndependentSourceCount = 1,
            HumanVerifiedSupportCount = 1,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };

        candidate.CurriculumFamilyId = family.Id;
        candidate.SourceCurriculumExampleId = sourceExampleOne.Id;

        db.AddRange(
            family,
            sourceOne,
            sourceTwo,
            targetOne,
            targetTwo,
            sourceExampleOne,
            sourceExampleTwo,
            targetExampleOne,
            targetExampleTwo,
            Anchor(sourceExampleOne, statementSignature),
            Anchor(targetExampleOne, statementSignature),
            Anchor(sourceExampleTwo, requestSignature),
            Anchor(targetExampleTwo, requestSignature),
            pattern,
            new LegendLanguageStructuralEvidence
            {
                Id = Guid.NewGuid(),
                StructuralPatternId = pattern.Id,
                CurriculumFamilyId = family.Id,
                PairKey = string.Empty,
                LanguageCode = "en",
                VariationDimension = "intent",
                BaselineCurriculumExampleId = sourceExampleOne.Id,
                ComparedCurriculumExampleId = sourceExampleTwo.Id,
                BaselineVariationValue = "statement",
                ComparedVariationValue = "request",
                EvidenceSignature = Guid.NewGuid().ToString("N"),
                IndependentSourceIdentity = "family:" + family.Id.ToString("N"),
                ContributionState = "Supported",
                IsHumanVerifiedSupport = true,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            },
            TrustedAlignment(sourceOne, targetOne),
            TrustedAlignment(sourceTwo, targetTwo),
            candidate);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static LegendCurriculumExample Example(
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
            Dimension = "intent",
            Value = semanticSignature,
            SemanticSignature = semanticSignature,
            AnchorSignature = Guid.NewGuid().ToString("N"),
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };

    private static LegendTranslationAlignment TrustedAlignment(
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) =>
        new()
        {
            Id = Guid.NewGuid(),
            PairKey = "en:ht",
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = "FounderApproved",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
            QualityState = "Verified",
            Confidence = 1m,
            HumanVerified = true,
            ObservationCount = 1
        };

    private static string SemanticSignature(
        string dimension,
        string value) =>
        LegendLanguageIdentity.TextHash(
            $"semantic|{dimension.Trim().ToLowerInvariant()}|" +
            value.Trim().ToLowerInvariant());

    private static LegendConnectAutonomousLearningService Service(
        MasterAppDbContext db,
        IConfiguration configuration,
        ILegendLanguageRegistry registry,
        LegendConnectCorpusService corpus,
        ITranslationProvider provider,
        ILegendConnectLanguageTeacher teacher) =>
        new(
            db,
            registry,
            provider,
            new TranslationCapacityAuthority(
                db,
                configuration,
                NullLogger<TranslationCapacityAuthority>.Instance),
            corpus,
            new LegendConnectAutonomousGapPlanner(
                db,
                registry),
            configuration,
            languageTeacher: teacher);

    private static LegendCorpusCandidate Candidate(
        string key,
        string text,
        string proposalState) =>
        new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            SourceLanguageCode = "en",
            TargetLanguageCode = "ht",
            SourceText = text,
            SourceTextHash =
                LegendLanguageIdentity.TextHash(text),
            Category = "Conversation",
            Provenance = "FounderApproved",
            IsApproved = true,
            Priority = 10,
            ProcessingState = "Queued",
            TeacherProposalProcessingState = proposalState,
            CreatedUtc = DateTime.UtcNow,
            ProcessedUtc = DateTime.UtcNow
        };

    private static LegendLanguageTextUnit Unit(
        string languageCode,
        string text,
        string provenance) =>
        new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = languageCode,
            StoragePartition =
                LegendLanguageIdentity.DatasetNamespace(
                    languageCode),
            NormalizedHash =
                LegendLanguageIdentity.TextHash(text),
            Text =
                LegendLanguageIdentity.NormalizeText(text),
            Provenance = provenance,
            IsTrainingEligible = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["LegendConnect:CorpusAcquisition:Enabled"] =
                        "true",
                    ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] =
                        "100000",
                    ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] =
                        "0",
                    ["LegendConnect:LanguageTeacher:MaximumAutonomousAttempts"] =
                        "3"
                })
            .Build();

    private static async Task<CanonicalCounts> CanonicalCountsAsync(
        MasterAppDbContext db) =>
        new(
            await db.LegendLanguageTextUnits.CountAsync(),
            await db.LegendTranslationAlignments.CountAsync(),
            await db.LegendCurriculumFamilies.CountAsync(),
            await db.LegendCurriculumExamples.CountAsync(),
            await db.LegendLanguageStructuralPatterns.CountAsync(),
            await db.LegendLanguageStructuralEvidence.CountAsync());

    private sealed record CanonicalCounts(
        int TextUnits,
        int Alignments,
        int CurriculumFamilies,
        int CurriculumExamples,
        int StructuralPatterns,
        int StructuralEvidence);

    private sealed class NoopTranslationProvider :
        ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new TranslationDetectionResult(
                    true,
                    "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            TranslateCalls++;

            return Task.FromResult(
                new TranslationProviderResult(
                    false,
                    null,
                    sourceLanguage,
                    ProviderName,
                    "provider_should_not_be_called"));
        }
    }

    private sealed class PreflightTeacher :
        ILegendConnectLanguageTeacher
    {
        private int _proposeCalls;
        private int _critiqueCalls;

        public int ProposeCalls => _proposeCalls;
        public int CritiqueCalls => _critiqueCalls;

        public static PreflightTeacher MissingConfiguration() => new();

        public LegendLanguageTeacherConfigurationPreflight Preflight(
            string role) =>
            new(
                role,
                new string('a', 64),
                false,
                LegendLanguageTeacherFailureClassification
                    .ConfigurationMissing);

        public Task<LegendLanguageTeacherProposalResult>
            ProposeAsync(
                LegendLanguageTeacherProposalRequest request,
                CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _proposeCalls);
            return Task.FromResult(
                new LegendLanguageTeacherProposalResult(
                    false,
                    [],
                    LegendLanguageTeacherFailureClassification
                        .ConfigurationMissing));
        }

        public Task<LegendLanguageTeacherCritiqueResult>
            CritiqueAsync(
                LegendLanguageTeacherCritiqueRequest request,
                CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _critiqueCalls);
            return Task.FromResult(
                new LegendLanguageTeacherCritiqueResult(
                    false,
                    false,
                    null,
                    [],
                    LegendLanguageTeacherFailureClassification
                        .ConfigurationMissing));
        }
    }

    private sealed class RecordingTeacher :
        ILegendConnectLanguageTeacher
    {
        private readonly int _failuresBeforeSuccess;
        private readonly string _failureCode;
        private readonly int _criticFailuresBeforeSuccess;
        private readonly string _criticFailureCode;
        private readonly bool _criticApproves;
        private readonly int _familyCount;

        private RecordingTeacher(
            int failuresBeforeSuccess,
            string failureCode,
            int criticFailuresBeforeSuccess,
            string criticFailureCode,
            bool criticApproves,
            int familyCount)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
            _failureCode = failureCode;
            _criticFailuresBeforeSuccess =
                criticFailuresBeforeSuccess;
            _criticFailureCode = criticFailureCode;
            _criticApproves = criticApproves;
            _familyCount = familyCount;
        }

        public int ProposeCalls { get; private set; }
        public int CritiqueCalls { get; private set; }

        public LegendLanguageTeacherConfigurationPreflight Preflight(
            string role) =>
            LegendLanguageTeacherConfigurationPreflight.Ready(
                role,
                "test-recording-teacher");

        public static RecordingTeacher ApprovedTwoFamilies() =>
            new(
                failuresBeforeSuccess: 0,
                failureCode: string.Empty,
                criticFailuresBeforeSuccess: 0,
                criticFailureCode: string.Empty,
                criticApproves: true,
                familyCount: 2);

        public static RecordingTeacher Rejected() =>
            new(
                failuresBeforeSuccess: 0,
                failureCode: string.Empty,
                criticFailuresBeforeSuccess: 0,
                criticFailureCode: string.Empty,
                criticApproves: false,
                familyCount: 1);

        public static RecordingTeacher Failing() =>
            new(
                failuresBeforeSuccess: int.MaxValue,
                failureCode:
                    LegendLanguageTeacherFailureClassification.Provider,
                criticFailuresBeforeSuccess: 0,
                criticFailureCode: string.Empty,
                criticApproves: false,
                familyCount: 0);

        public static RecordingTeacher RecoveringAfterTimeout() =>
            new(
                failuresBeforeSuccess: 1,
                failureCode:
                    LegendLanguageTeacherFailureClassification.Timeout,
                criticFailuresBeforeSuccess: 0,
                criticFailureCode: string.Empty,
                criticApproves: true,
                familyCount: 1);

        public static RecordingTeacher CriticAuthenticationFailure() =>
            new(
                failuresBeforeSuccess: 0,
                failureCode: string.Empty,
                criticFailuresBeforeSuccess: int.MaxValue,
                criticFailureCode:
                    LegendLanguageTeacherFailureClassification
                        .Authentication,
                criticApproves: false,
                familyCount: 1);

        public Task<LegendLanguageTeacherProposalResult>
            ProposeAsync(
                LegendLanguageTeacherProposalRequest request,
                CancellationToken cancellationToken = default)
        {
            ProposeCalls++;

            if (ProposeCalls <= _failuresBeforeSuccess)
            {
                return Task.FromResult(
                    new LegendLanguageTeacherProposalResult(
                        false,
                        Array.Empty<
                            LegendLanguageTeacherFamilyProposal>(),
                        _failureCode));
            }

            var families =
                Enumerable.Range(1, _familyCount)
                    .Select(
                        index =>
                            new LegendLanguageTeacherFamilyProposal(
                                "machine.conversation",
                                "Conversation",
                                $"Machine candidate {index} requiring canonical validation.",
                                0.90m,
                                new[]
                                {
                                    new LegendLanguageTeacherExampleProposal(
                                        $"Controlled source {index}.",
                                        $"Controlled target {index}.",
                                        new[]
                                        {
                                            new LegendLanguageTeacherSemanticComponent(
                                                "intent",
                                                "statement",
                                                "Controlled")
                                        }),
                                    new LegendLanguageTeacherExampleProposal(
                                        $"Controlled source variation {index}.",
                                        $"Controlled target variation {index}.",
                                        new[]
                                        {
                                            new LegendLanguageTeacherSemanticComponent(
                                                "intent",
                                                "request",
                                                "Controlled")
                                        })
                                }))
                    .ToArray();

            return Task.FromResult(
                new LegendLanguageTeacherProposalResult(
                    true,
                    families));
        }

        public Task<LegendLanguageTeacherCritiqueResult>
            CritiqueAsync(
                LegendLanguageTeacherCritiqueRequest request,
                CancellationToken cancellationToken = default)
        {
            CritiqueCalls++;

            if (CritiqueCalls <= _criticFailuresBeforeSuccess)
            {
                return Task.FromResult(
                    new LegendLanguageTeacherCritiqueResult(
                        false,
                        false,
                        null,
                        [],
                        _criticFailureCode));
            }

            return Task.FromResult(
                new LegendLanguageTeacherCritiqueResult(
                    true,
                    _criticApproves,
                    0.95m,
                    _criticApproves
                        ? new[]
                        {
                            "requires_canonical_validation"
                        }
                        : new[]
                        {
                            "semantic_support_insufficient",
                            "requires_canonical_validation"
                        }));
        }
    }
}
