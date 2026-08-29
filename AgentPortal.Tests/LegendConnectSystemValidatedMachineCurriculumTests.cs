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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectSystemValidatedMachineCurriculumTests
{
    [Fact]
    public async Task CanonicalMachineConversationTransition_BecomesBroadGovernedNativeReuse()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);

        for (var index = 1; index <= 3; index++)
        {
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(
                new LegendConnectCurriculumBatchSubmission(
                    $"machine.native.primitives.{index}",
                    "Founder-controlled native semantic primitives",
                    [
                        new LegendConnectCurriculumExampleSubmission(
                            "Hello.",
                            new Dictionary<string, string>
                            {
                                ["conversation_function"] = "greeting"
                            },
                            new LegendConnectMeaningGraphSubmission(
                                [new LegendConnectMeaningNodeSubmission(
                                    "source", "conversation_function", "greeting", "Hello")],
                                [])),
                        new LegendConnectCurriculumExampleSubmission(
                            "Welcome.",
                            new Dictionary<string, string>
                            {
                                ["conversation_response"] = "welcome"
                            },
                            new LegendConnectMeaningGraphSubmission(
                                [new LegendConnectMeaningNodeSubmission(
                                    "result", "conversation_response", "welcome", "Welcome")],
                                []))
                    ]));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var founderFamilyIds = await db.LegendCurriculumFamilies
            .Where(item => item.FamilyKey.StartsWith("machine.native.primitives."))
            .Select(item => item.Id)
            .ToListAsync();
        foreach (var familyId in founderFamilyIds)
        {
            await curriculum.ReevaluateHistoricalWorkItemAsync(
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                familyId,
                "en");
        }

        var family = new LegendLanguageTeacherFamilyProposal(
            "machine.native.greeting.transition",
            "Conversation",
            "Critic-approved lower-tier transition over established Founder semantic primitives.",
            0.98m,
            [
                new LegendLanguageTeacherExampleProposal(
                    "Hello.", null,
                    [new LegendLanguageTeacherSemanticComponent(
                        "conversation_function", "greeting", "Hello")]),
                new LegendLanguageTeacherExampleProposal(
                    "Welcome.", null,
                    [new LegendLanguageTeacherSemanticComponent(
                        "conversation_response", "welcome", "Welcome")])
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string> { ["conversation_function"] = "greeting" }),
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string> { ["conversation_response"] = "welcome" }))]);
        var payload = JsonSerializer.Serialize(family);
        var candidate = new LegendCorpusCandidate
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = "machine-native-transition-candidate",
            SourceLanguageCode = "en",
            TargetLanguageCode = "es",
            SourceText = "Hello.",
            SourceTextHash = LegendLanguageIdentity.TextHash("Hello."),
            Category = "Conversation",
            Provenance = "MachineConversation",
            IsApproved = false,
            Priority = 0,
            ProcessingState = "ConversationProposal",
            TeacherProposalProcessingState = "AwaitingCanonicalValidation",
            CreatedUtc = DateTime.UtcNow
        };
        var proposal = new LegendLanguageTeacherProposal
        {
            Id = Guid.NewGuid(),
            CorpusCandidateId = candidate.Id,
            ProposalIdentity = LegendLanguageIdentity.TextHash("machine-native-transition|" + payload),
            PairKey = "en:es",
            SourceLanguageCode = "en",
            TargetLanguageCode = "es",
            EvidenceIdentityHash = LegendLanguageIdentity.TextHash("machine-native-transition-evidence"),
            FamilyKey = family.FamilyKey,
            SemanticCategory = family.SemanticCategory,
            Rationale = family.Rationale,
            Confidence = family.Confidence,
            ProposalPayloadJson = payload,
            CriticApproved = true,
            CriticConfidence = 0.98m,
            CriticReasonCodesJson = "[]",
            ValidationState = "SystemValidated",
            Provenance = "SystemValidatedMachine",
            CanonicalValidationAttemptCount = 1,
            CanonicalValidatedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.AddRange(candidate, proposal);
        await db.SaveChangesAsync();

        Assert.True(await curriculum.ProcessOneSystemValidatedMachineProposalAsync());
        var inference = await curriculum.TryInferComposedSemanticTransitionAsync(
            "en", "Hello.", [], null);

        Assert.Equal(LegendSemanticTransitionInference.Supported, inference.State);
        Assert.False(string.IsNullOrWhiteSpace(inference.RealizedText));
        Assert.Contains("broad_governed_semantic_transition", inference.Reasons);
        var transition = Assert.Single(await db.LegendSemanticTransitionEvidence
            .Where(item => item.Provenance == "SystemValidatedMachine")
            .ToListAsync());
        Assert.False(transition.IsHumanVerifiedSupport);
        Assert.Equal("Supported", transition.ContributionState);
    }

    [Fact]
    public async Task CanonicalMachineProposal_AdmitsWithoutFounderOrProviderLaundering()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var configuration =
            Configuration();

        var registry =
            new LegendLanguageRegistry(
                db,
                configuration);

        var corpus =
            new LegendConnectCorpusService(
                db,
                registry,
                NullLogger<LegendConnectCorpusService>.Instance);

        var curriculum =
            new LegendConnectCurriculumService(
                db,
                registry,
                corpus);

        await SeedFounderSemanticProofAsync(
            db,
            "confirm",
            "intent",
            "confirmation");

        await SeedFounderSemanticProofAsync(
            db,
            "cancel",
            "intent",
            "cancellation");

        var family =
            new LegendLanguageTeacherFamilyProposal(
                "machine.phase5.intent",
                "Conversation",
                "Canonical machine-controlled intent contrast.",
                0.98m,
                [
                    new LegendLanguageTeacherExampleProposal(
                        "confirm",
                        null,
                        [
                            new LegendLanguageTeacherSemanticComponent(
                                "intent",
                                "confirmation",
                                "confirm")
                        ]),
                    new LegendLanguageTeacherExampleProposal(
                        "cancel",
                        null,
                        [
                            new LegendLanguageTeacherSemanticComponent(
                                "intent",
                                "cancellation",
                                "cancel")
                        ])
                ]);

        var payload =
            JsonSerializer.Serialize(family);

        var proposal =
            new LegendLanguageTeacherProposal
            {
                Id = Guid.NewGuid(),
                CorpusCandidateId = Guid.NewGuid(),
                ProposalIdentity =
                    LegendLanguageIdentity.TextHash(
                        "phase5|" + payload),
                PairKey = "en:ht",
                SourceLanguageCode = "en",
                TargetLanguageCode = "ht",
                EvidenceIdentityHash =
                    LegendLanguageIdentity.TextHash(
                        "phase5-evidence|" + payload),
                FamilyKey = family.FamilyKey,
                SemanticCategory =
                    family.SemanticCategory,
                Rationale =
                    family.Rationale,
                Confidence =
                    family.Confidence,
                ProposalPayloadJson = payload,
                CriticApproved = true,
                CriticConfidence = 0.98m,
                CriticReasonCodesJson = "[]",
                ValidationState = "SystemValidated",
                Provenance = "SystemValidatedMachine",
                CanonicalValidationAttemptCount = 1,
                CanonicalValidatedUtc = DateTime.UtcNow,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

        db.Add(proposal);
        await db.SaveChangesAsync();

        Assert.True(
            await curriculum
                .ProcessOneSystemValidatedMachineProposalAsync());

        var admitted =
            await db.LegendLanguageTeacherProposals
                .SingleAsync(item =>
                    item.Id == proposal.Id);

        Assert.Equal(
            "CurriculumAdmitted",
            admitted.ValidationState);

        Assert.Equal(
            "SystemValidatedMachine",
            admitted.Provenance);

        Assert.Equal(
            1,
            admitted.CurriculumAdmissionAttemptCount);

        Assert.NotNull(
            admitted.CurriculumAdmittedUtc);

        var admittedFamily =
            await db.LegendCurriculumFamilies
                .SingleAsync(item =>
                    item.FamilyKey ==
                        "machine.phase5.intent");

        Assert.Equal(
            "SystemValidatedMachine",
            admittedFamily.Provenance);

        var examples =
            await db.LegendCurriculumExamples
                .Where(item =>
                    item.CurriculumFamilyId ==
                        admittedFamily.Id &&
                    item.DerivedFromCurriculumExampleId ==
                        null)
                .ToListAsync();

        Assert.Equal(2, examples.Count);

        Assert.All(
            examples,
            item =>
                Assert.Equal(
                    "SystemValidatedMachine",
                    item.Provenance));

        // Founder source assets are reused but never downgraded.
        var sourceUnits =
            await db.LegendLanguageTextUnits
                .Where(item =>
                    item.LanguageCode == "en" &&
                    (
                        item.Text == "confirm" ||
                        item.Text == "cancel"
                    ))
                .ToListAsync();

        Assert.Equal(2, sourceUnits.Count);

        Assert.All(
            sourceUnits,
            item =>
                Assert.Equal(
                    "FounderApproved",
                    item.Provenance));

        var evidence =
            await db.LegendLanguageStructuralEvidence
                .Where(item =>
                    item.CurriculumFamilyId ==
                        admittedFamily.Id)
                .ToListAsync();

        Assert.NotEmpty(evidence);

        Assert.All(
            evidence,
            item =>
            {
                Assert.Equal(
                    "Supported",
                    item.ContributionState);

                Assert.Equal(
                    "SystemValidatedMachine",
                    item.Provenance);

                Assert.False(
                    item.IsHumanVerifiedSupport);
            });

        var patterns =
            await db.LegendLanguageStructuralPatterns
                .Where(item =>
                    item.CurriculumFamilyId ==
                        admittedFamily.Id)
                .ToListAsync();

        Assert.NotEmpty(patterns);

        Assert.All(
            patterns,
            item =>
            {
                Assert.Equal(
                    "SystemValidatedMachine",
                    item.Provenance);

                Assert.Equal(
                    0,
                    item.HumanVerifiedSupportCount);

                Assert.Equal(
                    0,
                    item.ProviderOnlySupportCount);

                Assert.False(
                    item.IsProductionEligible);
            });

        var before =
            (
                Families:
                    await db.LegendCurriculumFamilies
                        .CountAsync(),
                Examples:
                    await db.LegendCurriculumExamples
                        .CountAsync(),
                Evidence:
                    await db.LegendLanguageStructuralEvidence
                        .CountAsync()
            );

        Assert.False(
            await curriculum
                .ProcessOneSystemValidatedMachineProposalAsync());

        var after =
            (
                Families:
                    await db.LegendCurriculumFamilies
                        .CountAsync(),
                Examples:
                    await db.LegendCurriculumExamples
                        .CountAsync(),
                Evidence:
                    await db.LegendLanguageStructuralEvidence
                        .CountAsync()
            );

        Assert.Equal(before, after);
    }

    private static async Task
        SeedFounderSemanticProofAsync(
            MasterAppDbContext db,
            string text,
            string dimension,
            string value)
    {
        var normalized =
            LegendLanguageIdentity.NormalizeText(text);

        var unit =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition =
                    LegendLanguageIdentity
                        .DatasetNamespace("en"),
                NormalizedHash =
                    LegendLanguageIdentity
                        .TextHash(normalized),
                Text = normalized,
                Provenance = "FounderApproved",
                IsTrainingEligible = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

        var family =
            new LegendCurriculumFamily
            {
                Id = Guid.NewGuid(),
                FamilyKey =
                    $"phase5.proof.{value}.{Guid.NewGuid():N}",
                SemanticCategory = "Proof",
                Provenance = "FounderApproved",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

        var example =
            new LegendCurriculumExample
            {
                Id = Guid.NewGuid(),
                CurriculumFamilyId =
                    family.Id,
                TextUnitId =
                    unit.Id,
                LanguageCode = "en",
                Provenance = "FounderApproved",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

        var lexeme =
            new LegendLanguageLexeme
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                NormalizedHash =
                    LegendLanguageIdentity
                        .TextHash(normalized),
                SurfaceForm = normalized,
                Provenance = "FounderApproved",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

        var occurrence =
            new LegendLanguageLexicalOccurrence
            {
                Id = Guid.NewGuid(),
                TextUnitId = unit.Id,
                LexemeId = lexeme.Id,
                TokenIndex = 0,
                CharacterOffset = 0,
                CharacterLength =
                    normalized.Length,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

        var anchor =
            new LegendLanguageCompositionalAnchor
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                TextUnitId = unit.Id,
                LexemeId = lexeme.Id,
                ComponentStartTokenIndex = 0,
                ComponentLength = 1,
                CurriculumFamilyId =
                    family.Id,
                CurriculumExampleId =
                    example.Id,
                Dimension = dimension,
                Value = value,
                SemanticSignature =
                    LegendLanguageIdentity.TextHash(
                        $"phase5|{dimension}|{value}"),
                AnchorSignature =
                    LegendLanguageIdentity.TextHash(
                        $"phase5-anchor|{example.Id:D}"),
                Provenance = "FounderApproved",
                CreatedUtc = DateTime.UtcNow
            };

        db.AddRange(
            unit,
            family,
            example,
            lexeme,
            occurrence,
            anchor);

        await db.SaveChangesAsync();
    }

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
}
