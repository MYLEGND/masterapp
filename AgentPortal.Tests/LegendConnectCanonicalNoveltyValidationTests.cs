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

public sealed class LegendConnectCanonicalNoveltyValidationTests
{
    [Fact]
    public async Task NovelHeldOutSurfaces_WithGovernedProfilesAndContrasts_BecomeSystemValidatedMachineOnly()
    {
        await using var harness = await Harness.CreateAsync(NovelFamily());
        var before = await CanonicalCountsAsync(harness.Db);

        var proposal = await harness.ProposeAsync();
        await harness.Service.ProcessOneAsync();

        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal("SystemValidated", proposal.ValidationState);
        Assert.Equal("SystemValidatedMachine", proposal.Provenance);
        Assert.Null(proposal.CanonicalValidationFailureCode);
        Assert.Equal(before, await CanonicalCountsAsync(harness.Db));
        Assert.DoesNotContain(
            await harness.Db.LegendCurriculumFamilies.ToListAsync(),
            item => item.Provenance != "FounderApproved");
    }

    [Fact]
    public async Task ChangedCriticEvidenceIdentity_CannotInheritApproval()
    {
        await using var harness = await Harness.CreateAsync(NovelFamily());
        var proposal = await harness.ProposeAsync();
        proposal.EvidenceIdentityHash =
            LegendLanguageIdentity.TextHash("tampered-critic-evidence");
        await harness.Db.SaveChangesAsync();

        await harness.Service.ProcessOneAsync();

        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal("Rejected", proposal.ValidationState);
        Assert.Equal(
            "canonical_evidence_identity_mismatch",
            proposal.CanonicalValidationFailureCode);
        Assert.Equal("MachineProposed", proposal.Provenance);
    }

    [Fact]
    public async Task AlreadyKnownExamples_AreRejectedAsNonNovelWithoutAdmission()
    {
        await using var harness = await Harness.CreateAsync(KnownFamily());
        var before = await CanonicalCountsAsync(harness.Db);

        var proposal = await harness.ProposeAsync();
        await harness.Service.ProcessOneAsync();

        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal("Rejected", proposal.ValidationState);
        Assert.Equal("canonical_proposal_already_known", proposal.CanonicalValidationFailureCode);
        Assert.Equal("MachineProposed", proposal.Provenance);
        Assert.Equal(before, await CanonicalCountsAsync(harness.Db));
    }

    [Fact]
    public async Task ExistingMeaningWithSwappedDefinition_IsRejectedAsContradictory()
    {
        await using var harness = await Harness.CreateAsync(
            Family(
                Example("confirm", "intent", "cancellation", "confirm"),
                Example("cancel", "intent", "confirmation", "cancel")));

        var proposal = await harness.ProposeAsync();
        await harness.Service.ProcessOneAsync();

        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal("Rejected", proposal.ValidationState);
        Assert.Equal("canonical_source_semantics_contradicted", proposal.CanonicalValidationFailureCode);
        Assert.Equal("MachineProposed", proposal.Provenance);
    }

    [Fact]
    public async Task UnsupportedProviderPrimitive_CannotPassCanonicalValidation()
    {
        await using var harness = await Harness.CreateAsync(NovelFamily());
        var proposal = await harness.ProposeAsync();
        var unsupported = Family(
            Example("Please escalate this.", "intent", "escalation", "escalate"),
            Example("Kindly verify this.", "intent", "confirmation", "verify"));
        await ReplacePayloadAsync(harness.Db, proposal, unsupported);

        await harness.Service.ProcessOneAsync();

        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.NotEqual("SystemValidated", proposal.ValidationState);
        Assert.Equal("MachineProposed", proposal.Provenance);
        Assert.Equal(
            "language_teacher_semantic_primitive_lineage_unproven",
            proposal.CanonicalValidationFailureCode);
    }

    [Fact]
    public async Task CandidateFamilyCannotBeReboundToAnotherProviderDeclaredFamily()
    {
        await using var harness = await Harness.CreateAsync(NovelFamily());
        var proposal = await harness.ProposeAsync();
        var crossFamily = NovelFamily() with
        {
            FamilyKey = "governed.unrelated.family"
        };
        proposal.FamilyKey = crossFamily.FamilyKey;
        await ReplacePayloadAsync(harness.Db, proposal, crossFamily);

        await harness.Service.ProcessOneAsync();

        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal("InsufficientEvidence", proposal.ValidationState);
        Assert.Equal(
            "language_teacher_semantic_family_lineage_mismatch",
            proposal.CanonicalValidationFailureCode);
        Assert.Equal("MachineProposed", proposal.Provenance);
    }

    [Fact]
    public async Task RejectedProposalReplay_IsTerminalAndDoesNotRepeatValidation()
    {
        await using var harness = await Harness.CreateAsync(KnownFamily());
        var proposal = await harness.ProposeAsync();
        await harness.Service.ProcessOneAsync();
        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal(1, proposal.CanonicalValidationAttemptCount);

        harness.Candidate.ProcessingState = "Completed";
        await harness.Db.SaveChangesAsync();
        await harness.Service.ProcessOneAsync();

        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal("Rejected", proposal.ValidationState);
        Assert.Equal(1, proposal.CanonicalValidationAttemptCount);
        Assert.Single(await harness.Db.LegendLanguageTeacherProposals.ToListAsync());
    }

    [Fact]
    public async Task ValidatedProposalAdmission_IsIdempotentAndRetainsMachineAuthority()
    {
        await using var harness = await Harness.CreateAsync(NovelFamily());
        var proposal = await harness.ProposeAsync();
        await harness.Service.ProcessOneAsync();
        await harness.Service.ProcessOneAsync();

        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal("CurriculumAdmitted", proposal.ValidationState);
        Assert.Equal("SystemValidatedMachine", proposal.Provenance);
        Assert.Equal(1, proposal.CanonicalValidationAttemptCount);
        Assert.Equal(1, proposal.CurriculumAdmissionAttemptCount);

        Assert.False(await harness.Curriculum.ProcessOneSystemValidatedMachineProposalAsync());
        Assert.Single(await harness.Db.LegendLanguageTeacherProposals.ToListAsync());
        Assert.DoesNotContain(
            await harness.Db.LegendCurriculumExamples
                .Where(item => item.Provenance == "SystemValidatedMachine")
                .ToListAsync(),
            item => item.Provenance == "FounderApproved");
    }

    [Fact]
    public async Task HostileProviderAuthorityFields_AreIgnoredAndCannotSelfApproveOrServe()
    {
        await using var harness = await Harness.CreateAsync(NovelFamily());
        var before = await CanonicalCountsAsync(harness.Db);
        var proposal = await harness.ProposeAsync();
        var payload = proposal.ProposalPayloadJson;
        proposal.ProposalPayloadJson = payload[..^1] +
            ",\"Provenance\":\"FounderApproved\",\"HumanVerified\":true," +
            "\"ValidationState\":\"CurriculumAdmitted\",\"IsProductionEligible\":true}";
        proposal.ProposalIdentity = ProposalIdentity(proposal, harness.Candidate);
        await harness.Db.SaveChangesAsync();

        await harness.Service.ProcessOneAsync();

        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal("SystemValidated", proposal.ValidationState);
        Assert.Equal("SystemValidatedMachine", proposal.Provenance);
        Assert.Equal(before, await CanonicalCountsAsync(harness.Db));
        Assert.Empty(await harness.Db.LegendLanguageTextUnits
            .Where(item => item.Provenance == "SystemValidatedMachine")
            .ToListAsync());
    }

    [Fact]
    public async Task AdmissionRejectsAStateClaimWithoutCanonicalValidatorReceipt()
    {
        await using var harness = await Harness.CreateAsync(NovelFamily());
        var before = await CanonicalCountsAsync(harness.Db);
        var proposal = await harness.ProposeAsync();
        proposal.ValidationState = "SystemValidated";
        proposal.Provenance = "SystemValidatedMachine";
        proposal.CanonicalValidationAttemptCount = 0;
        proposal.CanonicalValidatedUtc = null;
        await harness.Db.SaveChangesAsync();

        Assert.False(
            await harness.Curriculum
                .ProcessOneSystemValidatedMachineProposalAsync());
        Assert.Equal(before, await CanonicalCountsAsync(harness.Db));
        await harness.Db.Entry(proposal).ReloadAsync();
        Assert.Equal(0, proposal.CurriculumAdmissionAttemptCount);
    }

    private static LegendLanguageTeacherFamilyProposal NovelFamily() =>
        Family(
            Example(
                "Kindly verify the schedule.",
                "intent",
                "confirmation",
                "verify"),
            Example(
                "Please abandon the schedule.",
                "intent",
                "cancellation",
                "abandon"));

    private static LegendLanguageTeacherFamilyProposal KnownFamily() =>
        Family(
            Example("confirm", "intent", "confirmation", "confirm"),
            Example("cancel", "intent", "cancellation", "cancel"));

    private static LegendLanguageTeacherFamilyProposal Family(
        LegendLanguageTeacherExampleProposal first,
        LegendLanguageTeacherExampleProposal second) =>
        new(
            "governed.intent.family",
            "Conversation",
            "A bounded critic-approved controlled contrast.",
            0.95m,
            [first, second]);

    private static LegendLanguageTeacherExampleProposal Example(
        string source,
        string dimension,
        string value,
        string surface) =>
        new(
            source,
            null,
            [new LegendLanguageTeacherSemanticComponent(
                dimension,
                value,
                surface)]);

    private static async Task ReplacePayloadAsync(
        MasterAppDbContext db,
        LegendLanguageTeacherProposal proposal,
        LegendLanguageTeacherFamilyProposal family)
    {
        proposal.FamilyKey = family.FamilyKey;
        proposal.SemanticCategory = family.SemanticCategory;
        proposal.ProposalPayloadJson = JsonSerializer.Serialize(family);
        var candidate = await db.LegendCorpusCandidates
            .SingleAsync(item => item.Id == proposal.CorpusCandidateId);
        proposal.ProposalIdentity = ProposalIdentity(proposal, candidate);
        await db.SaveChangesAsync();
    }

    private static string ProposalIdentity(
        LegendLanguageTeacherProposal proposal,
        LegendCorpusCandidate candidate) =>
        LegendLanguageIdentity.TextHash(
            string.Join(
                "|",
                "language-teacher-proposal:v1",
                candidate.IdempotencyKey,
                proposal.EvidenceIdentityHash,
                proposal.ProposalPayloadJson));

    private static async Task<CanonicalCounts> CanonicalCountsAsync(
        MasterAppDbContext db) =>
        new(
            await db.LegendLanguageTextUnits.CountAsync(),
            await db.LegendTranslationAlignments.CountAsync(),
            await db.LegendCurriculumFamilies.CountAsync(),
            await db.LegendCurriculumExamples.CountAsync(),
            await db.LegendLanguageCompositionalAnchors.CountAsync(),
            await db.LegendSemanticTransitionEvidence.CountAsync());

    private sealed record CanonicalCounts(
        int TextUnits,
        int Alignments,
        int Families,
        int Examples,
        int Anchors,
        int Transitions);

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            MasterAppDbContext db,
            LegendConnectCurriculumService curriculum,
            LegendConnectAutonomousLearningService service,
            LegendCorpusCandidate candidate)
        {
            Db = db;
            Curriculum = curriculum;
            Service = service;
            Candidate = candidate;
        }

        internal MasterAppDbContext Db { get; }
        internal LegendConnectCurriculumService Curriculum { get; }
        internal LegendConnectAutonomousLearningService Service { get; }
        internal LegendCorpusCandidate Candidate { get; }

        internal static async Task<Harness> CreateAsync(
            LegendLanguageTeacherFamilyProposal teacherFamily)
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
            var candidate = await SeedFounderFamilyAsync(db);
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
                languageTeacher: new ApprovedTeacher(teacherFamily),
                curriculum: curriculum);
            return new Harness(db, curriculum, service, candidate);
        }

        internal async Task<LegendLanguageTeacherProposal> ProposeAsync()
        {
            await Service.ProcessOneAsync();
            return await Db.LegendLanguageTeacherProposals.SingleAsync();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<LegendCorpusCandidate> SeedFounderFamilyAsync(
        MasterAppDbContext db)
    {
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = "governed.intent.family",
            SemanticCategory = "Conversation",
            Provenance = "FounderApproved"
        };
        var confirmSource = Unit("en", "confirm");
        var cancelSource = Unit("en", "cancel");
        var confirmTarget = Unit("ht", "konfime");
        var cancelTarget = Unit("ht", "anile");
        var confirmExample = SourceExample(family, confirmSource);
        var cancelExample = SourceExample(family, cancelSource);
        var confirmTargetExample = TargetExample(
            family,
            confirmTarget,
            confirmExample.Id);
        var cancelTargetExample = TargetExample(
            family,
            cancelTarget,
            cancelExample.Id);
        var confirmation = SemanticSignature("intent", "confirmation");
        var cancellation = SemanticSignature("intent", "cancellation");
        var confirmLexeme = Lexeme("en", "confirm");
        var cancelLexeme = Lexeme("en", "cancel");
        var pattern = new LegendLanguageStructuralPattern
        {
            Id = Guid.NewGuid(),
            PropositionSignature = "governed-intent-contrast",
            CurriculumFamilyId = family.Id,
            PairKey = string.Empty,
            LanguageCode = "en",
            VariationDimension = "intent",
            MaturityState = "Supported",
            SupportCount = 1,
            IndependentSourceCount = 1,
            HumanVerifiedSupportCount = 1,
            Provenance = "FounderApproved"
        };
        var candidate = new LegendCorpusCandidate
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = "lai-015-canonical-novelty",
            SourceLanguageCode = "en",
            TargetLanguageCode = "ht",
            SourceText = confirmSource.Text,
            SourceTextHash = confirmSource.NormalizedHash,
            Category = "Conversation",
            CurriculumFamilyId = family.Id,
            SourceCurriculumExampleId = confirmExample.Id,
            Provenance = "FounderApproved",
            IsApproved = true,
            Priority = 10,
            ProcessingState = "Queued",
            TeacherProposalProcessingState = "Pending",
            CreatedUtc = DateTime.UtcNow,
            ProcessedUtc = DateTime.UtcNow
        };

        db.AddRange(
            family,
            confirmSource,
            cancelSource,
            confirmTarget,
            cancelTarget,
            confirmExample,
            cancelExample,
            confirmTargetExample,
            cancelTargetExample,
            confirmLexeme,
            cancelLexeme,
            Occurrence(confirmSource, confirmLexeme),
            Occurrence(cancelSource, cancelLexeme),
            Anchor(confirmExample, confirmLexeme.Id, "confirmation", confirmation),
            Anchor(cancelExample, cancelLexeme.Id, "cancellation", cancellation),
            Anchor(confirmTargetExample, null, "confirmation", confirmation),
            Anchor(cancelTargetExample, null, "cancellation", cancellation),
            pattern,
            new LegendLanguageStructuralEvidence
            {
                Id = Guid.NewGuid(),
                StructuralPatternId = pattern.Id,
                CurriculumFamilyId = family.Id,
                PairKey = string.Empty,
                LanguageCode = "en",
                VariationDimension = "intent",
                BaselineCurriculumExampleId = confirmExample.Id,
                ComparedCurriculumExampleId = cancelExample.Id,
                BaselineVariationValue = "confirmation",
                ComparedVariationValue = "cancellation",
                EvidenceSignature = "governed-intent-contrast-evidence",
                IndependentSourceIdentity = "founder:governed-intent-contrast",
                ContributionState = "Supported",
                IsHumanVerifiedSupport = true,
                Provenance = "FounderApproved"
            },
            Alignment(confirmSource, confirmTarget),
            Alignment(cancelSource, cancelTarget),
            candidate);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return await db.LegendCorpusCandidates.SingleAsync(
            item => item.Id == candidate.Id);
    }

    private static LegendLanguageTextUnit Unit(
        string language,
        string text) =>
        new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = language,
            StoragePartition = LegendLanguageIdentity.DatasetNamespace(language),
            NormalizedHash = LegendLanguageIdentity.TextHash(text),
            Text = LegendLanguageIdentity.NormalizeText(text),
            Provenance = "FounderApproved",
            IsTrainingEligible = true
        };

    private static LegendCurriculumExample SourceExample(
        LegendCurriculumFamily family,
        LegendLanguageTextUnit unit) =>
        new()
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = unit.Id,
            LanguageCode = unit.LanguageCode,
            Provenance = "FounderApproved"
        };

    private static LegendCurriculumExample TargetExample(
        LegendCurriculumFamily family,
        LegendLanguageTextUnit unit,
        Guid sourceExampleId) =>
        new()
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = unit.Id,
            LanguageCode = unit.LanguageCode,
            DerivedFromCurriculumExampleId = sourceExampleId,
            Provenance = "FounderApproved"
        };

    private static LegendLanguageLexeme Lexeme(
        string language,
        string surface) =>
        new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = language,
            NormalizedHash = LegendLanguageIdentity.TextHash(surface),
            SurfaceForm = surface,
            Provenance = "FounderApproved"
        };

    private static LegendLanguageLexicalOccurrence Occurrence(
        LegendLanguageTextUnit unit,
        LegendLanguageLexeme lexeme) =>
        new()
        {
            Id = Guid.NewGuid(),
            TextUnitId = unit.Id,
            LexemeId = lexeme.Id,
            TokenIndex = 0,
            CharacterOffset = 0,
            CharacterLength = unit.Text.Length
        };

    private static LegendLanguageCompositionalAnchor Anchor(
        LegendCurriculumExample example,
        Guid? lexemeId,
        string value,
        string semanticSignature) =>
        new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = example.LanguageCode,
            PairKey = example.DerivedFromCurriculumExampleId is null
                ? string.Empty
                : "en:ht",
            TextUnitId = example.TextUnitId,
            LexemeId = lexemeId,
            ComponentStartTokenIndex = 0,
            ComponentLength = 1,
            CurriculumFamilyId = example.CurriculumFamilyId,
            CurriculumExampleId = example.Id,
            Dimension = "intent",
            Value = value,
            SemanticSignature = semanticSignature,
            AnchorSignature = Guid.NewGuid().ToString("N"),
            Provenance = "FounderApproved"
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
            ObservationCount = 1
        };

    private static string SemanticSignature(
        string dimension,
        string value) =>
        LegendLanguageIdentity.TextHash(
            $"semantic|{dimension}|{value}");

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
                    ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
                    ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0",
                    ["LegendConnect:LanguageTeacher:MaximumAutonomousAttempts"] = "3"
                })
            .Build();

    private sealed class ApprovedTeacher(
        LegendLanguageTeacherFamilyProposal family) :
        ILegendConnectLanguageTeacher
    {
        public LegendLanguageTeacherConfigurationPreflight Preflight(
            string role) =>
            LegendLanguageTeacherConfigurationPreflight.Ready(
                role,
                "test-approved-teacher");

        public Task<LegendLanguageTeacherProposalResult> ProposeAsync(
            LegendLanguageTeacherProposalRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new LegendLanguageTeacherProposalResult(
                    true,
                    [family]));

        public Task<LegendLanguageTeacherCritiqueResult> CritiqueAsync(
            LegendLanguageTeacherCritiqueRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new LegendLanguageTeacherCritiqueResult(
                    true,
                    true,
                    0.95m,
                    ["critic_approved_for_canonical_validation"]));
    }

    private sealed class NoopTranslationProvider : ITranslationProvider
    {
        public string ProviderName => "Noop";

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default,
        LegendConnectExternalProviderPolicy? providerPolicy = null) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default,
        LegendConnectExternalProviderPolicy? providerPolicy = null) =>
            throw new InvalidOperationException(
                "Canonical validation must not call a translation provider.");
    }
}
