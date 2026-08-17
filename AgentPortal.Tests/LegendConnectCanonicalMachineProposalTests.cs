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

public sealed class LegendConnectCanonicalMachineProposalTests
{
    [Fact]
    public async Task ExactPhase3Lineage_WithFounderBackedSourceSemantics_BecomesSystemValidatedMachineOnly()
    {
        await using var db = ControllerTestHelpers.BuildDb();
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

        var candidate = Candidate(
            "phase4-valid",
            "Please confirm",
            "AwaitingCanonicalValidation");

        var candidateSource = Unit(
            "en",
            candidate.SourceText,
            "FounderApproved");

        var trustedSource = Unit(
            "en",
            "I need help",
            "FounderApproved");

        var trustedTarget = Unit(
            "ht",
            "Mwen bezwen èd",
            "FounderApproved");

        var trustedAlignment = Alignment(
            trustedSource,
            trustedTarget);

        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = "conversation.confirmation.phase4-proof",
            SemanticCategory = "Conversation",
            Provenance = "FounderApproved"
        };

        var sourceExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = candidateSource.Id,
            LanguageCode = "en",
            Provenance = "FounderApproved"
        };

        var semanticSignature =
            LegendLanguageIdentity.TextHash(
                "phase4|intent|confirmation");

        var lexeme = new LegendLanguageLexeme
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            NormalizedHash =
                LegendLanguageIdentity.TextHash("please"),
            SurfaceForm = "please",
            Provenance = "FounderApproved",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var occurrence = new LegendLanguageLexicalOccurrence
        {
            Id = Guid.NewGuid(),
            TextUnitId = candidateSource.Id,
            LexemeId = lexeme.Id,
            TokenIndex = 0,
            CharacterOffset = 0,
            CharacterLength = 6,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var anchor = new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            TextUnitId = candidateSource.Id,
            LexemeId = lexeme.Id,
            ComponentStartTokenIndex = 0,
            ComponentLength = 2,
            CurriculumFamilyId = family.Id,
            CurriculumExampleId = sourceExample.Id,
            Dimension = "intent",
            Value = "confirmation",
            SemanticSignature = semanticSignature,
            AnchorSignature =
                LegendLanguageIdentity.TextHash(
                    "phase4-anchor|" + sourceExample.Id),
            Provenance = "FounderApproved"
        };

        db.AddRange(
            candidateSource,
            trustedSource,
            trustedTarget,
            trustedAlignment,
            family,
            sourceExample,
            lexeme,
            occurrence,
            anchor,
            candidate);

        await db.SaveChangesAsync();

        var proposalFamily =
            new LegendLanguageTeacherFamilyProposal(
                "machine.confirmation",
                "Conversation",
                "Controlled confirmation intent.",
                0.95m,
                [
                    new LegendLanguageTeacherExampleProposal(
                        "Please confirm",
                        null,
                        [
                            new LegendLanguageTeacherSemanticComponent(
                                "intent",
                                "confirmation",
                                "Please confirm")
                        ]),
                    new LegendLanguageTeacherExampleProposal(
                        "Please confirm",
                        null,
                        [
                            new LegendLanguageTeacherSemanticComponent(
                                "intent",
                                "confirmation",
                                "Please confirm")
                        ])
                ]);

        var proposal = await AddProposalAsync(
            db,
            candidate,
            proposalFamily);

        var before = await CanonicalCountsAsync(db);

        await Service(
            db,
            configuration,
            registry,
            corpus,
            curriculum)
            .ProcessOneAsync();

        var persisted =
            await db.LegendLanguageTeacherProposals
                .SingleAsync(item => item.Id == proposal.Id);

        Assert.Equal("SystemValidated", persisted.ValidationState);
        Assert.Equal(
            "SystemValidatedMachine",
            persisted.Provenance);
        Assert.Equal(1, persisted.CanonicalValidationAttemptCount);
        Assert.Null(persisted.CanonicalValidationFailureCode);
        Assert.Null(persisted.CanonicalValidationLeaseExpiresUtc);
        Assert.NotNull(persisted.CanonicalValidatedUtc);

        Assert.Equal(
            before,
            await CanonicalCountsAsync(db));
    }

    [Fact]
    public async Task ChangedEvidenceIdentity_IsRejectedWithoutCanonicalMutation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
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

        var candidate = Candidate(
            "phase4-lineage-mismatch",
            "Please confirm",
            "AwaitingCanonicalValidation");

        var candidateSource =
            Unit("en", candidate.SourceText, "FounderApproved");
        var trustedSource =
            Unit("en", "I need help", "FounderApproved");
        var trustedTarget =
            Unit("ht", "Mwen bezwen èd", "FounderApproved");

        db.AddRange(
            candidateSource,
            trustedSource,
            trustedTarget,
            Alignment(trustedSource, trustedTarget),
            candidate);

        await db.SaveChangesAsync();

        var proposalFamily = Family(
            "Please confirm",
            target: null);

        var proposal = await AddProposalAsync(
            db,
            candidate,
            proposalFamily);

        proposal.EvidenceIdentityHash =
            LegendLanguageIdentity.TextHash(
                "tampered-evidence-lineage");

        await db.SaveChangesAsync();

        var before = await CanonicalCountsAsync(db);

        await Service(
            db,
            configuration,
            registry,
            corpus,
            curriculum)
            .ProcessOneAsync();

        var persisted =
            await db.LegendLanguageTeacherProposals
                .SingleAsync(item => item.Id == proposal.Id);

        Assert.Equal("Rejected", persisted.ValidationState);
        Assert.Equal(
            "MachineProposed",
            persisted.Provenance);
        Assert.Equal(
            "canonical_evidence_identity_mismatch",
            persisted.CanonicalValidationFailureCode);

        Assert.Equal(
            before,
            await CanonicalCountsAsync(db));
    }

    [Fact]
    public async Task MissingEstablishedSourceSemantics_RemainsInsufficientAndCannotSelfPromote()
    {
        await using var db = ControllerTestHelpers.BuildDb();
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

        var candidate = Candidate(
            "phase4-semantic-insufficient",
            "Please confirm",
            "AwaitingCanonicalValidation");

        var candidateSource =
            Unit("en", candidate.SourceText, "FounderApproved");
        var trustedSource =
            Unit("en", "I need help", "FounderApproved");
        var trustedTarget =
            Unit("ht", "Mwen bezwen èd", "FounderApproved");

        db.AddRange(
            candidateSource,
            trustedSource,
            trustedTarget,
            Alignment(trustedSource, trustedTarget),
            candidate);

        await db.SaveChangesAsync();

        var proposal = await AddProposalAsync(
            db,
            candidate,
            Family("Please confirm", target: null));

        var before = await CanonicalCountsAsync(db);

        await Service(
            db,
            configuration,
            registry,
            corpus,
            curriculum)
            .ProcessOneAsync();

        var persisted =
            await db.LegendLanguageTeacherProposals
                .SingleAsync(item => item.Id == proposal.Id);

        Assert.Equal(
            "InsufficientEvidence",
            persisted.ValidationState);
        Assert.Equal(
            "MachineProposed",
            persisted.Provenance);
        Assert.Equal(
            "canonical_source_semantics_insufficient",
            persisted.CanonicalValidationFailureCode);

        Assert.Equal(
            before,
            await CanonicalCountsAsync(db));
    }

    [Fact]
    public async Task ProposedTargetWithoutExistingDirectionalFormulation_RemainsInsufficient()
    {
        await using var db = ControllerTestHelpers.BuildDb();
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

        var candidate = Candidate(
            "phase4-target-insufficient",
            "Please confirm",
            "AwaitingCanonicalValidation");

        var candidateSource =
            Unit("en", candidate.SourceText, "FounderApproved");
        var trustedSource =
            Unit("en", "I need help", "FounderApproved");
        var trustedTarget =
            Unit("ht", "Mwen bezwen èd", "FounderApproved");

        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = "conversation.confirmation.phase4-target",
            SemanticCategory = "Conversation",
            Provenance = "FounderApproved"
        };

        var sourceExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = candidateSource.Id,
            LanguageCode = "en",
            Provenance = "FounderApproved"
        };

        var lexeme = new LegendLanguageLexeme
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            NormalizedHash =
                LegendLanguageIdentity.TextHash("please"),
            SurfaceForm = "please",
            Provenance = "FounderApproved",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var occurrence = new LegendLanguageLexicalOccurrence
        {
            Id = Guid.NewGuid(),
            TextUnitId = candidateSource.Id,
            LexemeId = lexeme.Id,
            TokenIndex = 0,
            CharacterOffset = 0,
            CharacterLength = 6,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var anchor = new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            TextUnitId = candidateSource.Id,
            LexemeId = lexeme.Id,
            ComponentStartTokenIndex = 0,
            ComponentLength = 2,
            CurriculumFamilyId = family.Id,
            CurriculumExampleId = sourceExample.Id,
            Dimension = "intent",
            Value = "confirmation",
            SemanticSignature =
                LegendLanguageIdentity.TextHash(
                    "phase4|intent|confirmation"),
            AnchorSignature =
                LegendLanguageIdentity.TextHash(
                    "phase4-target-anchor|" + sourceExample.Id),
            Provenance = "FounderApproved"
        };

        db.AddRange(
            candidateSource,
            trustedSource,
            trustedTarget,
            Alignment(trustedSource, trustedTarget),
            family,
            sourceExample,
            lexeme,
            occurrence,
            anchor,
            candidate);

        await db.SaveChangesAsync();

        var proposal = await AddProposalAsync(
            db,
            candidate,
            Family(
                "Please confirm",
                "Tanpri konfime"));

        var before = await CanonicalCountsAsync(db);

        await Service(
            db,
            configuration,
            registry,
            corpus,
            curriculum)
            .ProcessOneAsync();

        var persisted =
            await db.LegendLanguageTeacherProposals
                .SingleAsync(item => item.Id == proposal.Id);

        Assert.Equal(
            "InsufficientEvidence",
            persisted.ValidationState);
        Assert.Equal(
            "MachineProposed",
            persisted.Provenance);
        Assert.Equal(
            "canonical_target_formulation_insufficient",
            persisted.CanonicalValidationFailureCode);

        Assert.Equal(
            before,
            await CanonicalCountsAsync(db));
    }

    private static LegendConnectAutonomousLearningService Service(
        MasterAppDbContext db,
        IConfiguration configuration,
        ILegendLanguageRegistry registry,
        LegendConnectCorpusService corpus,
        LegendConnectCurriculumService curriculum) =>
        new(
            db,
            registry,
            new NoopTranslationProvider(),
            new TranslationCapacityAuthority(
                db,
                configuration,
                NullLogger<TranslationCapacityAuthority>.Instance),
            corpus,
            new LegendConnectAutonomousGapPlanner(
                db,
                registry),
            configuration,
            curriculum: curriculum);

    private static async Task<LegendLanguageTeacherProposal>
        AddProposalAsync(
            MasterAppDbContext db,
            LegendCorpusCandidate candidate,
            LegendLanguageTeacherFamilyProposal family)
    {
        var evidence = await BuildEvidenceIdentitiesAsync(
            db,
            candidate);

        var evidenceHash =
            LegendLanguageIdentity.TextHash(
                string.Join(
                    "\n",
                    evidence.OrderBy(
                        item => item,
                        StringComparer.Ordinal)));

        var payload = JsonSerializer.Serialize(family);

        var identity =
            LegendLanguageIdentity.TextHash(
                string.Join(
                    "|",
                    "language-teacher-proposal:v1",
                    candidate.IdempotencyKey,
                    evidenceHash,
                    payload));

        var proposal = new LegendLanguageTeacherProposal
        {
            Id = Guid.NewGuid(),
            CorpusCandidateId = candidate.Id,
            ProposalIdentity = identity,
            PairKey = "en:ht",
            SourceLanguageCode = "en",
            TargetLanguageCode = "ht",
            EvidenceIdentityHash = evidenceHash,
            FamilyKey = family.FamilyKey,
            SemanticCategory = family.SemanticCategory,
            Rationale = family.Rationale,
            Confidence = family.Confidence,
            ProposalPayloadJson = payload,
            CriticApproved = true,
            CriticConfidence = 0.95m,
            CriticReasonCodesJson = "[]",
            ValidationState = "AwaitingCanonicalValidation",
            Provenance = "MachineProposed",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.Add(proposal);
        await db.SaveChangesAsync();

        return proposal;
    }

    private static async Task<IReadOnlyList<string>>
        BuildEvidenceIdentitiesAsync(
            MasterAppDbContext db,
            LegendCorpusCandidate candidate)
    {
        var source = await db.LegendLanguageTextUnits
            .SingleAsync(item =>
                item.LanguageCode ==
                    candidate.SourceLanguageCode &&
                item.NormalizedHash ==
                    candidate.SourceTextHash &&
                item.IsTrainingEligible);

        var alignments =
            await (
                from alignment in db.LegendTranslationAlignments
                join evidenceSource in db.LegendLanguageTextUnits
                    on alignment.SourceTextUnitId equals
                    evidenceSource.Id
                join evidenceTarget in db.LegendLanguageTextUnits
                    on alignment.TargetTextUnitId equals
                    evidenceTarget.Id
                where
                    alignment.PairKey == "en:ht" &&
                    alignment.SupersededUtc == null &&
                    evidenceSource.IsTrainingEligible &&
                    evidenceTarget.IsTrainingEligible &&
                    evidenceSource.LanguageCode == "en" &&
                    evidenceTarget.LanguageCode == "ht" &&
                    (
                        alignment.HumanVerified ||
                        alignment.QualityState ==
                            "SystemValidated"
                    )
                orderby
                    alignment.HumanVerified descending,
                    alignment.Confidence descending,
                    alignment.UpdatedUtc descending
                select alignment
            )
            .Take(64)
            .ToListAsync();

        var identities = new List<string>
        {
            $"source:{source.Id:D}"
        };

        identities.AddRange(
            alignments
                .Where(item => item.HumanVerified)
                .Take(31)
                .Select(item => $"alignment:{item.Id:D}"));

        return identities;
    }

    private static LegendLanguageTeacherFamilyProposal Family(
        string source,
        string? target) =>
        new(
            "machine.confirmation",
            "Conversation",
            "Controlled confirmation intent.",
            0.95m,
            [
                new LegendLanguageTeacherExampleProposal(
                    source,
                    target,
                    [
                        new LegendLanguageTeacherSemanticComponent(
                            "intent",
                            "confirmation",
                            source)
                    ]),
                new LegendLanguageTeacherExampleProposal(
                    source,
                    target,
                    [
                        new LegendLanguageTeacherSemanticComponent(
                            "intent",
                            "confirmation",
                            source)
                    ])
            ]);

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

    private static LegendTranslationAlignment Alignment(
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) =>
        new()
        {
            Id = Guid.NewGuid(),
            PairKey = "en:ht",
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = "FounderApproved",
            Provenance = "FounderApproved",
            Confidence = 1m,
            QualityState = "Verified",
            HumanVerified = true,
            ObservationCount = 1,
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
                        "0"
                })
            .Build();

    private static async Task<CanonicalCounts>
        CanonicalCountsAsync(
            MasterAppDbContext db) =>
        new(
            await db.LegendLanguageTextUnits.CountAsync(),
            await db.LegendTranslationAlignments.CountAsync(),
            await db.LegendCurriculumFamilies.CountAsync(),
            await db.LegendCurriculumExamples.CountAsync(),
            await db.LegendLanguageStructuralPatterns.CountAsync(),
            await db.LegendLanguageStructuralEvidence.CountAsync(),
            await db.LegendCorpusCandidates.CountAsync());

    private sealed record CanonicalCounts(
        int TextUnits,
        int Alignments,
        int CurriculumFamilies,
        int CurriculumExamples,
        int StructuralPatterns,
        int StructuralEvidence,
        int CorpusCandidates);

    private sealed class NoopTranslationProvider :
        ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";

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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new TranslationProviderResult(
                    false,
                    null,
                    sourceLanguage,
                    ProviderName,
                    "provider_should_not_be_called"));
    }
}
