using System;
using System.Collections.Generic;
using System.Linq;
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

        var candidateSource = Unit(
            "en",
            candidate.SourceText,
            "FounderApproved");

        var trustedSource = Unit(
            "en",
            "I need your help.",
            "FounderApproved");

        var trustedTarget = Unit(
            "ht",
            "Mwen bezwen èd ou.",
            "FounderApproved");

        var trustedAlignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(),
            PairKey = "en:ht",
            SourceTextUnitId = trustedSource.Id,
            TargetTextUnitId = trustedTarget.Id,
            Provider = "FounderApproved",
            Provenance = "FounderApproved",
            Confidence = 1m,
            QualityState = "Verified",
            HumanVerified = true,
            ObservationCount = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.AddRange(
            candidateSource,
            trustedSource,
            trustedTarget,
            trustedAlignment,
            candidate);

        await db.SaveChangesAsync();

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
            item =>
                item.QualityState == "SystemValidated" &&
                item.CreatedUtc > trustedAlignment.CreatedUtc);

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

        var candidateSource = Unit(
            "en",
            candidate.SourceText,
            "FounderApproved");

        var trustedSource = Unit(
            "en",
            "Please help me.",
            "FounderApproved");

        var trustedTarget = Unit(
            "ht",
            "Tanpri ede m.",
            "FounderApproved");

        db.AddRange(
            candidateSource,
            trustedSource,
            trustedTarget,
            new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceTextUnitId = trustedSource.Id,
                TargetTextUnitId = trustedTarget.Id,
                Provider = "FounderApproved",
                Provenance = "FounderApproved",
                QualityState = "Verified",
                Confidence = 1m,
                HumanVerified = true,
                ObservationCount = 1,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            },
            candidate);

        await db.SaveChangesAsync();

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
            "language_teacher_insufficient_governed_evidence",
            persisted.TeacherProposalFailureCode);
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

        var candidateSource = Unit(
            "en",
            candidate.SourceText,
            "FounderApproved");

        var trustedSource = Unit(
            "en",
            "A trusted source.",
            "FounderApproved");

        var trustedTarget = Unit(
            "ht",
            "Yon sous ki verifye.",
            "FounderApproved");

        db.AddRange(
            candidateSource,
            trustedSource,
            trustedTarget,
            new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceTextUnitId = trustedSource.Id,
                TargetTextUnitId = trustedTarget.Id,
                Provider = "FounderApproved",
                Provenance = "FounderApproved",
                QualityState = "Verified",
                Confidence = 1m,
                HumanVerified = true,
                ObservationCount = 1,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            },
            candidate);

        await db.SaveChangesAsync();

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

                persisted.TeacherProposalLeaseExpiresUtc =
                    DateTime.UtcNow.AddMinutes(-1);

                await db.SaveChangesAsync();
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

    private sealed class RecordingTeacher :
        ILegendConnectLanguageTeacher
    {
        private readonly bool _teacherSucceeds;
        private readonly bool _criticApproves;
        private readonly int _familyCount;

        private RecordingTeacher(
            bool teacherSucceeds,
            bool criticApproves,
            int familyCount)
        {
            _teacherSucceeds = teacherSucceeds;
            _criticApproves = criticApproves;
            _familyCount = familyCount;
        }

        public int ProposeCalls { get; private set; }
        public int CritiqueCalls { get; private set; }

        public static RecordingTeacher ApprovedTwoFamilies() =>
            new(
                teacherSucceeds: true,
                criticApproves: true,
                familyCount: 2);

        public static RecordingTeacher Rejected() =>
            new(
                teacherSucceeds: true,
                criticApproves: false,
                familyCount: 1);

        public static RecordingTeacher Failing() =>
            new(
                teacherSucceeds: false,
                criticApproves: false,
                familyCount: 0);

        public Task<LegendLanguageTeacherProposalResult>
            ProposeAsync(
                LegendLanguageTeacherProposalRequest request,
                CancellationToken cancellationToken = default)
        {
            ProposeCalls++;

            if (!_teacherSucceeds)
            {
                return Task.FromResult(
                    new LegendLanguageTeacherProposalResult(
                        false,
                        Array.Empty<
                            LegendLanguageTeacherFamilyProposal>(),
                        "language_teacher_unavailable"));
            }

            var families =
                Enumerable.Range(1, _familyCount)
                    .Select(
                        index =>
                            new LegendLanguageTeacherFamilyProposal(
                                $"machine.conversation.{index}",
                                "Conversation",
                                "Machine candidate requiring canonical validation.",
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
                                                "statement",
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
