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

public sealed class LegendConnectLanguageCriticEvidenceLineageTests
{
    [Fact]
    public async Task CriticPacket_IsLimitedToTheProposalSemanticFamily()
    {
        await using var fixture = Fixture.Create();
        await SeedGovernedFamilyAsync(fixture.Db);
        await SeedUnrelatedFamilyAlignmentAsync(fixture.Db);

        var result = await fixture.Service
            .SubmitConversationMachineProposalAsync(Submission());
        Assert.Equal("AwaitingCritic", result.State);

        await fixture.Service.ProcessOneAsync();

        var packet = Assert.Single(fixture.Critic.Requests).Context;
        Assert.Equal("diagnostic.handoff-capacity", packet.SemanticFamilyKey);
        Assert.Equal("diagnostic_reasoning", packet.SemanticCategory);
        Assert.Equal(2, packet.Evidence.Count);
        Assert.All(packet.Evidence, item =>
            Assert.StartsWith("lineage:", item.EvidenceIdentity));
        Assert.Equal(
            new[] { "Check ownership.", "Check throughput." },
            packet.Evidence
                .Select(item => item.SourceText)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(packet.Evidence, item =>
            item.SourceText.Contains("dispatch", StringComparison.OrdinalIgnoreCase) ||
            item.TargetText?.Contains("dispatch", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task CriticPacket_ExcludesEvidenceFromAnotherLanguagePair()
    {
        await using var fixture = Fixture.Create();
        await SeedGovernedFamilyAsync(fixture.Db);
        await SeedUnrelatedPairAlignmentAsync(fixture.Db);

        var result = await fixture.Service
            .SubmitConversationMachineProposalAsync(Submission());
        Assert.Equal("AwaitingCritic", result.State);
        await fixture.Service.ProcessOneAsync();

        var packet = Assert.Single(fixture.Critic.Requests).Context;
        Assert.Equal(2, packet.Evidence.Count);
        Assert.DoesNotContain(packet.Evidence, item =>
            item.SourceText.Contains("capacité", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InsufficientRelevantEvidence_IsRetainedWithPreciseRejection()
    {
        await using var fixture = Fixture.Create();
        await SeedGovernedFamilyAsync(
            fixture.Db,
            includeSecondAlignment: false);

        var result = await fixture.Service
            .SubmitConversationMachineProposalAsync(Submission());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("InsufficientEvidence", result.State);
        Assert.Equal(
            "language_teacher_relevant_evidence_insufficient",
            result.ErrorCode);
        Assert.Empty(fixture.Critic.Requests);
        var proposal = await fixture.Db.LegendLanguageTeacherProposals
            .SingleAsync();
        Assert.Equal("MachineProposed", proposal.Provenance);
        Assert.Equal("InsufficientEvidence", proposal.ValidationState);
        var candidate = await fixture.Db.LegendCorpusCandidates.SingleAsync();
        Assert.Equal(
            "language_teacher_relevant_evidence_insufficient",
            candidate.TeacherProposalFailureCode);
    }

    [Fact]
    public async Task MissingControlledContrast_IsRejectedBeforeCriticExecution()
    {
        await using var fixture = Fixture.Create();
        await SeedGovernedFamilyAsync(
            fixture.Db,
            includeControlledContrast: false);

        var result = await fixture.Service
            .SubmitConversationMachineProposalAsync(Submission());

        Assert.Equal("InsufficientEvidence", result.State);
        Assert.Equal(
            "language_teacher_controlled_contrast_lineage_unproven",
            result.ErrorCode);
        Assert.Empty(fixture.Critic.Requests);
    }

    [Fact]
    public async Task DuplicatePhysicalAlignments_DoNotDuplicateDurableLineage()
    {
        await using var fixture = Fixture.Create();
        await SeedGovernedFamilyAsync(
            fixture.Db,
            includeDuplicateAlignments: true);

        var result = await fixture.Service
            .SubmitConversationMachineProposalAsync(Submission());
        Assert.Equal("AwaitingCritic", result.State);
        await fixture.Service.ProcessOneAsync();

        var evidence = Assert.Single(fixture.Critic.Requests)
            .Context.Evidence;
        Assert.Equal(2, evidence.Count);
        Assert.Equal(
            2,
            evidence.Select(item => item.EvidenceIdentity)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public async Task CriticPacket_IsDeterministicAcrossInsertionOrder()
    {
        var forward = await CapturePacketAsync(reverseInsertionOrder: false);
        var reverse = await CapturePacketAsync(reverseInsertionOrder: true);

        Assert.Equal(forward, reverse);
    }

    private static async Task<string[]> CapturePacketAsync(
        bool reverseInsertionOrder)
    {
        await using var fixture = Fixture.Create();
        await SeedGovernedFamilyAsync(
            fixture.Db,
            includeDuplicateAlignments: true,
            reverseInsertionOrder: reverseInsertionOrder);
        var result = await fixture.Service
            .SubmitConversationMachineProposalAsync(Submission());
        Assert.Equal("AwaitingCritic", result.State);
        await fixture.Service.ProcessOneAsync();
        return Assert.Single(fixture.Critic.Requests)
            .Context.Evidence
            .Select(item => string.Join(
                "|",
                item.EvidenceIdentity,
                item.SourceText,
                item.TargetText,
                item.Provenance,
                item.QualityState))
            .ToArray();
    }

    private static LegendConnectMachineTeachingSubmission Submission() =>
        new(
            "en",
            "ht",
            "diagnostic.handoff-capacity",
            "diagnostic_reasoning",
            "Teach one governed diagnostic contrast.",
            0.9m,
            [
                new LegendConnectMachineTeachingExampleSubmission(
                    "Inspect who owns the handoff.",
                    "Tcheke kiyès ki responsab transmisyon an.",
                    [
                        new LegendConnectMachineTeachingComponentSubmission(
                            "diagnostic_state",
                            "handoff_failure",
                            "handoff")
                    ]),
                new LegendConnectMachineTeachingExampleSubmission(
                    "Inspect available throughput.",
                    "Tcheke kapasite ki disponib la.",
                    [
                        new LegendConnectMachineTeachingComponentSubmission(
                            "diagnostic_state",
                            "capacity_shortage",
                            "throughput")
                    ])
            ],
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["diagnostic_state"] = "handoff_failure"
                        }),
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["diagnostic_state"] = "capacity_shortage"
                        }))
            ],
            LegendConnectMachineTeachingSubmission.TranslationCapability,
            LegendConnectMachineTeachingSubmission.ReusableSemanticCategory);

    private static async Task SeedGovernedFamilyAsync(
        MasterAppDbContext db,
        bool includeSecondAlignment = true,
        bool includeControlledContrast = true,
        bool includeDuplicateAlignments = false,
        bool reverseInsertionOrder = false)
    {
        var familyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var sourceOne = Unit(
            Guid.Parse("10000000-0000-0000-0000-000000000011"),
            "en",
            "Check ownership.");
        var sourceTwo = Unit(
            Guid.Parse("10000000-0000-0000-0000-000000000012"),
            "en",
            "Check throughput.");
        var targetOne = Unit(
            Guid.Parse("10000000-0000-0000-0000-000000000021"),
            "ht",
            "Tcheke responsablite a.");
        var targetTwo = Unit(
            Guid.Parse("10000000-0000-0000-0000-000000000022"),
            "ht",
            "Tcheke kapasite a.");
        var sourceExampleOne = Example(
            Guid.Parse("10000000-0000-0000-0000-000000000031"),
            familyId,
            sourceOne,
            null);
        var sourceExampleTwo = Example(
            Guid.Parse("10000000-0000-0000-0000-000000000032"),
            familyId,
            sourceTwo,
            null);
        var targetExampleOne = Example(
            Guid.Parse("10000000-0000-0000-0000-000000000041"),
            familyId,
            targetOne,
            sourceExampleOne.Id);
        var targetExampleTwo = Example(
            Guid.Parse("10000000-0000-0000-0000-000000000042"),
            familyId,
            targetTwo,
            sourceExampleTwo.Id);
        var handoffSignature = SemanticSignature(
            "diagnostic_state",
            "handoff_failure");
        var capacitySignature = SemanticSignature(
            "diagnostic_state",
            "capacity_shortage");
        var proposalLineage = Assert.IsType<LegendMachineTeachingSemanticLineage>(
            LegendConnectCurriculumService
                .NormalizeMachineTeachingSemanticLineage(TeacherFamily()));
        var transitionSignature = Assert.Single(
            proposalLineage.TransitionSignatures);

        var entities = new List<object>
        {
            new LegendCurriculumFamily
            {
                Id = familyId,
                FamilyKey = "diagnostic.handoff-capacity",
                SemanticCategory = "diagnostic_reasoning",
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            },
            sourceOne,
            sourceTwo,
            targetOne,
            targetTwo,
            sourceExampleOne,
            sourceExampleTwo,
            targetExampleOne,
            targetExampleTwo,
            Anchor(sourceExampleOne, handoffSignature, "diagnostic_state", "handoff_failure"),
            Anchor(targetExampleOne, handoffSignature, "diagnostic_state", "handoff_failure"),
            Anchor(sourceExampleTwo, capacitySignature, "diagnostic_state", "capacity_shortage"),
            Anchor(targetExampleTwo, capacitySignature, "diagnostic_state", "capacity_shortage"),
            new LegendSemanticTransitionEvidence
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000051"),
                TransitionSignature = transitionSignature,
                SourceLanguageCode = "en",
                ResultLanguageCode = "en",
                SourceCurriculumExampleId = sourceExampleOne.Id,
                ResultCurriculumExampleId = sourceExampleTwo.Id,
                IndependentSourceIdentity = "family:" + familyId.ToString("N"),
                ContributionState = "Supported",
                IsHumanVerifiedSupport = true,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            },
            Alignment(
                Guid.Parse("10000000-0000-0000-0000-000000000061"),
                "en:ht",
                sourceOne,
                targetOne)
        };

        if (includeSecondAlignment)
        {
            entities.Add(Alignment(
                Guid.Parse("10000000-0000-0000-0000-000000000062"),
                "en:ht",
                sourceTwo,
                targetTwo));
        }

        if (includeControlledContrast)
        {
            var patternId = Guid.Parse(
                "10000000-0000-0000-0000-000000000071");
            entities.Add(new LegendLanguageStructuralPattern
            {
                Id = patternId,
                PropositionSignature = "diagnostic-contrast",
                CurriculumFamilyId = familyId,
                PairKey = string.Empty,
                LanguageCode = "en",
                VariationDimension = "diagnostic_state",
                MaturityState = "Supported",
                SupportCount = 1,
                IndependentSourceCount = 1,
                HumanVerifiedSupportCount = 1,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            });
            entities.Add(new LegendLanguageStructuralEvidence
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000072"),
                StructuralPatternId = patternId,
                CurriculumFamilyId = familyId,
                PairKey = string.Empty,
                LanguageCode = "en",
                VariationDimension = "diagnostic_state",
                BaselineCurriculumExampleId = sourceExampleOne.Id,
                ComparedCurriculumExampleId = sourceExampleTwo.Id,
                BaselineVariationValue = "handoff_failure",
                ComparedVariationValue = "capacity_shortage",
                EvidenceSignature = "diagnostic-controlled-contrast",
                IndependentSourceIdentity = "family:" + familyId.ToString("N"),
                ContributionState = "Supported",
                IsHumanVerifiedSupport = true,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            });
        }

        if (includeDuplicateAlignments)
        {
            entities.Add(Alignment(
                Guid.Parse("10000000-0000-0000-0000-000000000063"),
                "en:ht",
                sourceOne,
                targetOne));
            if (includeSecondAlignment)
            {
                entities.Add(Alignment(
                    Guid.Parse("10000000-0000-0000-0000-000000000064"),
                    "en:ht",
                    sourceTwo,
                    targetTwo));
            }
        }

        db.AddRange(reverseInsertionOrder
            ? entities.AsEnumerable().Reverse()
            : entities);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedUnrelatedFamilyAlignmentAsync(
        MasterAppDbContext db)
    {
        var familyId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var source = Unit(
            Guid.Parse("20000000-0000-0000-0000-000000000011"),
            "en",
            "Inspect dispatch scheduling.");
        var target = Unit(
            Guid.Parse("20000000-0000-0000-0000-000000000021"),
            "ht",
            "Tcheke orè dispatch la.");
        var sourceExample = Example(
            Guid.Parse("20000000-0000-0000-0000-000000000031"),
            familyId,
            source,
            null);
        var targetExample = Example(
            Guid.Parse("20000000-0000-0000-0000-000000000041"),
            familyId,
            target,
            sourceExample.Id);
        db.AddRange(
            new LegendCurriculumFamily
            {
                Id = familyId,
                FamilyKey = "operations.dispatch",
                SemanticCategory = "dispatch_scheduling",
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            },
            source,
            target,
            sourceExample,
            targetExample,
            Alignment(
                Guid.Parse("20000000-0000-0000-0000-000000000061"),
                "en:ht",
                source,
                target));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedUnrelatedPairAlignmentAsync(
        MasterAppDbContext db)
    {
        var source = Unit(
            Guid.Parse("30000000-0000-0000-0000-000000000011"),
            "fr",
            "Vérifiez la capacité.");
        var target = Unit(
            Guid.Parse("30000000-0000-0000-0000-000000000021"),
            "ht",
            "Tcheke kapasite rejyonal la.");
        db.AddRange(
            source,
            target,
            Alignment(
                Guid.Parse("30000000-0000-0000-0000-000000000061"),
                "fr:ht",
                source,
                target));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static LegendLanguageTeacherFamilyProposal TeacherFamily()
    {
        var submission = Submission();
        return new LegendLanguageTeacherFamilyProposal(
            submission.FamilyKey,
            submission.SemanticCategory,
            submission.Rationale,
            submission.Confidence,
            submission.Examples.Select(example =>
                new LegendLanguageTeacherExampleProposal(
                    example.SourceText,
                    example.TargetText,
                    example.Components.Select(component =>
                        new LegendLanguageTeacherSemanticComponent(
                            component.Dimension,
                            component.Value,
                            component.SurfaceForm)).ToArray())).ToArray(),
            submission.SemanticTransitions,
            submission.CapabilityIdentity,
            submission.CategoryIdentity);
    }

    private static LegendLanguageTextUnit Unit(
        Guid id,
        string languageCode,
        string text) =>
        new()
        {
            Id = id,
            LanguageCode = languageCode,
            StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
            NormalizedHash = LegendLanguageIdentity.TextHash(text),
            Text = LegendLanguageIdentity.NormalizeText(text),
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
            IsTrainingEligible = true
        };

    private static LegendCurriculumExample Example(
        Guid id,
        Guid familyId,
        LegendLanguageTextUnit unit,
        Guid? derivedFrom) =>
        new()
        {
            Id = id,
            CurriculumFamilyId = familyId,
            TextUnitId = unit.Id,
            LanguageCode = unit.LanguageCode,
            DerivedFromCurriculumExampleId = derivedFrom,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };

    private static LegendLanguageCompositionalAnchor Anchor(
        LegendCurriculumExample example,
        string semanticSignature,
        string dimension,
        string value) =>
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
            Dimension = dimension,
            Value = value,
            SemanticSignature = semanticSignature,
            AnchorSignature = Guid.NewGuid().ToString("N"),
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };

    private static LegendTranslationAlignment Alignment(
        Guid id,
        string pairKey,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) =>
        new()
        {
            Id = id,
            PairKey = pairKey,
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = "FounderApproved",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
            Confidence = 1m,
            QualityState = "Verified",
            HumanVerified = true,
            ObservationCount = 1
        };

    private static string SemanticSignature(
        string dimension,
        string value) =>
        LegendLanguageIdentity.TextHash(
            $"semantic|{dimension.Trim().ToLowerInvariant()}|" +
            value.Trim().ToLowerInvariant());

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            MasterAppDbContext db,
            LegendConnectAutonomousLearningService service,
            RecordingCritic critic)
        {
            Db = db;
            Service = service;
            Critic = critic;
        }

        internal MasterAppDbContext Db { get; }
        internal LegendConnectAutonomousLearningService Service { get; }
        internal RecordingCritic Critic { get; }

        internal static Fixture Create()
        {
            var db = ControllerTestHelpers.BuildDb();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
                    ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
                    ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0"
                })
                .Build();
            var registry = new LegendLanguageRegistry(db, configuration);
            var corpus = new LegendConnectCorpusService(
                db,
                registry,
                NullLogger<LegendConnectCorpusService>.Instance);
            var curriculum = new LegendConnectCurriculumService(
                db,
                registry,
                corpus);
            var critic = new RecordingCritic();
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
                languageTeacher: critic);
            return new Fixture(db, service, critic);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class RecordingCritic : ILegendConnectLanguageTeacher
    {
        internal List<LegendLanguageTeacherCritiqueRequest> Requests { get; } = [];

        public LegendLanguageTeacherConfigurationPreflight Preflight(
            string role) =>
            LegendLanguageTeacherConfigurationPreflight.Ready(
                role,
                "test-recording-critic");

        public Task<LegendLanguageTeacherProposalResult> ProposeAsync(
            LegendLanguageTeacherProposalRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Conversation teaching must use the existing critic path.");

        public Task<LegendLanguageTeacherCritiqueResult> CritiqueAsync(
            LegendLanguageTeacherCritiqueRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new LegendLanguageTeacherCritiqueResult(
                true,
                true,
                0.95m,
                ["requires_canonical_validation"]));
        }
    }

    private sealed class NoopTranslationProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";

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
                "Critic evidence tests cannot invoke translation.");
    }
}
