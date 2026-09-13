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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;
using LearningFixture = AgentPortal.Tests.LegendConnectConversationMachineProposalTests.Fixture;

namespace AgentPortal.Tests;

/// <summary>
/// Real in-process proposal/critic/canonical/admission/reload contracts using
/// the existing recording critic. Exact learned lexical reuse is the serving
/// assertion; this suite does not claim unseen-language generalization or a
/// live external critic. Added Founder examples contain no proposed surface.
/// </summary>
public sealed class LegendConnectMachineEvidenceGrowthContractTests
{
    [Theory]
    [InlineData("after_critic")]
    [InlineData("after_validation")]
    [InlineData("after_admission")]
    public async Task HealthySameFamilyGrowth_PreservesTheExactLearningReceiptAcrossReload(string growthStage)
    {
        var database = Guid.NewGuid().ToString("N");
        var root = new InMemoryDatabaseRoot();
        Guid proposalId;
        string originalIdentity;
        string originalPayload;
        var submission = LegendConnectConversationMachineProposalTests.NovelSameLanguageSubmission();
        await using (var fixture = await LearningFixture.CreateAsync(CreateDb(database, root)))
        {
            await LegendConnectConversationMachineProposalTests.SeedGovernedSameLanguageSemanticsAsync(
                fixture.Db, fixture.Curriculum);
            var before = await fixture.Curriculum.AnalyzeReusableMeaningGraphAsync("en", submission.Examples[0].SourceText);
            Assert.False(before.IsComposed);
            var submitted = await fixture.Service.SubmitConversationMachineProposalAsync(submission);
            Assert.True(submitted.Succeeded, submitted.Message);
            proposalId = submitted.ProposalId!.Value;
            var proposed = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync(item => item.Id == proposalId);
            originalIdentity = proposed.ProposalIdentity;
            originalPayload = proposed.ProposalPayloadJson;
            AssertServerEnvelope(originalPayload);

            await fixture.Service.ProcessOneAsync();
            Assert.Equal("AwaitingCanonicalValidation", (await ReloadProposalAsync(fixture.Db, proposalId)).ValidationState);
            if (growthStage == "after_critic")
                await AddHealthyEvidenceAsync(fixture, submission);
            await fixture.Service.ProcessOneAsync();
            Assert.Equal("SystemValidated", (await ReloadProposalAsync(fixture.Db, proposalId)).ValidationState);
            if (growthStage == "after_validation")
                await AddHealthyEvidenceAsync(fixture, submission);
            await fixture.Service.ProcessOneAsync();
            Assert.Equal("CurriculumAdmitted", (await ReloadProposalAsync(fixture.Db, proposalId)).ValidationState);
            if (growthStage == "after_admission")
                await AddHealthyEvidenceAsync(fixture, submission);
            Assert.Equal(1, fixture.Teacher.CritiqueCalls);
            Assert.Equal(0, fixture.Teacher.ProposeCalls);
        }

        // A new DbContext and new services ensure the receipt is recovered
        // from persistence rather than a tracked proposal or cached packet.
        await using var reloaded = await LearningFixture.CreateAsync(CreateDb(database, root));
        var admitted = await reloaded.Db.LegendLanguageTeacherProposals.SingleAsync(item => item.Id == proposalId);
        Assert.Equal(originalIdentity, admitted.ProposalIdentity);
        Assert.Equal(originalPayload, admitted.ProposalPayloadJson);
        Assert.Equal("CurriculumAdmitted", admitted.ValidationState);
        Assert.Equal("SystemValidatedMachine", admitted.Provenance);
        var countsBeforeServing = await CanonicalCountsAsync(reloaded.Db);
        await AssertLearnedSurfaceServesAsync(reloaded, submission.Examples[0].SourceText);
        Assert.Equal(countsBeforeServing, await CanonicalCountsAsync(reloaded.Db));
        var duplicate = await reloaded.Service.SubmitConversationMachineProposalAsync(submission);
        Assert.True(duplicate.ProposalAlreadyExisted);
        Assert.Equal(proposalId, duplicate.ProposalId);
        Assert.Equal(0, reloaded.Teacher.CritiqueCalls);
        Assert.Equal(0, reloaded.Teacher.ProposeCalls);
    }

    [Theory]
    [InlineData("example")]
    [InlineData("meaning_node")]
    [InlineData("contrast")]
    public async Task RevokedSelectedEvidence_CannotBeReplacedByHealthyAlternativeSupport(string revokedDependency)
    {
        var database = Guid.NewGuid().ToString("N");
        var root = new InMemoryDatabaseRoot();
        var submission = LegendConnectConversationMachineProposalTests.NovelSameLanguageSubmission();
        await using (var fixture = await LearningFixture.CreateAsync(CreateDb(database, root)))
        {
            await AdmitAsync(fixture, submission);
            await AddHealthyEvidenceAsync(fixture, submission);
            await AssertLearnedSurfaceServesAsync(fixture, submission.Examples[0].SourceText);
            var family = await fixture.Db.LegendCurriculumFamilies.SingleAsync(item => item.FamilyKey == submission.FamilyKey);
            var selectedExample = await (
                from example in fixture.Db.LegendCurriculumExamples
                join unit in fixture.Db.LegendLanguageTextUnits on example.TextUnitId equals unit.Id
                where example.CurriculumFamilyId == family.Id && unit.Text == "Hello."
                select example).SingleAsync();
            Assert.True(await fixture.Db.Set<LegendLanguageMeaningNodeEvidence>().AnyAsync(node =>
                node.CurriculumFamilyId == family.Id && node.CurriculumExampleId != selectedExample.Id &&
                node.Provenance == "FounderApproved" && node.SemanticValue == "greeting" && node.SupersededUtc == null));

            if (revokedDependency == "example")
                selectedExample.SupersededUtc = DateTime.UtcNow;
            else if (revokedDependency == "meaning_node")
            {
                var node = await fixture.Db.Set<LegendLanguageMeaningNodeEvidence>().SingleAsync(item =>
                    item.CurriculumExampleId == selectedExample.Id && item.Provenance == "FounderApproved");
                node.SupersededUtc = DateTime.UtcNow;
            }
            else
            {
                var originalWelcome = await (
                    from example in fixture.Db.LegendCurriculumExamples
                    join unit in fixture.Db.LegendLanguageTextUnits on example.TextUnitId equals unit.Id
                    where example.CurriculumFamilyId == family.Id && unit.Text == "Welcome."
                    select example.Id).SingleAsync();
                var contrasts = await fixture.Db.Set<LegendLanguageStructuralEvidence>().Where(item =>
                    item.CurriculumFamilyId == family.Id && item.SupersededUtc == null &&
                    ((item.BaselineCurriculumExampleId == selectedExample.Id && item.ComparedCurriculumExampleId == originalWelcome) ||
                     (item.ComparedCurriculumExampleId == selectedExample.Id && item.BaselineCurriculumExampleId == originalWelcome)))
                    .ToArrayAsync();
                Assert.NotEmpty(contrasts);
                foreach (var contrast in contrasts)
                    contrast.SupersededUtc = DateTime.UtcNow;
            }
            await fixture.Db.SaveChangesAsync();
        }

        await using var reloaded = await LearningFixture.CreateAsync(CreateDb(database, root));
        var inference = await reloaded.Curriculum.TryInferComposedSemanticTransitionAsync(
            "en", submission.Examples[0].SourceText, [], null);
        Assert.NotEqual(LegendSemanticTransitionInference.Supported, inference.State);
        Assert.Null(inference.RealizedText);
        Assert.Equal(0, reloaded.Teacher.CritiqueCalls);
    }

    [Theory]
    [InlineData("missing_bindings")]
    [InlineData("missing_family")]
    [InlineData("unsupported_version")]
    [InlineData("version_string")]
    [InlineData("version_null")]
    [InlineData("duplicate_reserved")]
    [InlineData("confusable_version_name")]
    [InlineData("unknown_root_field")]
    public async Task TamperedServerEnvelope_CannotServePreviouslyLearnedMeaning(string corruption)
    {
        var database = Guid.NewGuid().ToString("N");
        var root = new InMemoryDatabaseRoot();
        var submission = LegendConnectConversationMachineProposalTests.NovelSameLanguageSubmission();
        await using (var fixture = await LearningFixture.CreateAsync(CreateDb(database, root)))
        {
            var proposalId = await AdmitAsync(fixture, submission);
            await AssertLearnedSurfaceServesAsync(fixture, submission.Examples[0].SourceText);
            var proposal = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync(item => item.Id == proposalId);
            var envelope = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(proposal.ProposalPayloadJson)!;
            if (corruption == "missing_bindings")
                Assert.True(envelope.Remove("CriticEvidenceBindings"));
            else if (corruption == "missing_family")
                Assert.True(envelope.Remove("Family"));
            else if (corruption is "version_string" or "version_null")
                envelope["LegendServerPayloadVersion"] = JsonSerializer.SerializeToElement<string?>(
                    corruption == "version_string" ? "2" : null);
            else if (corruption == "confusable_version_name")
            {
                Assert.True(envelope.Remove("LegendServerPayloadVersion"));
                envelope["legendServerPayloadVersion"] = JsonSerializer.SerializeToElement(2);
            }
            else if (corruption == "unknown_root_field")
                envelope["UnexpectedAuthority"] = JsonSerializer.SerializeToElement(true);
            else if (corruption == "duplicate_reserved")
            {
                // Preserve duplicate JSON syntax; a dictionary serialization
                // would silently remove the very ambiguity being tested.
            }
            else
                envelope["LegendServerPayloadVersion"] = JsonSerializer.SerializeToElement(999);
            proposal.ProposalPayloadJson = corruption == "duplicate_reserved"
                ? "{\"LegendServerPayloadVersion\":2," + proposal.ProposalPayloadJson[1..]
                : JsonSerializer.Serialize(envelope);
            Assert.False(LegendConnectAutonomousLearningService.TryReadMachineProposalPayload(
                proposal.ProposalPayloadJson, out _, out _));
            await fixture.Db.SaveChangesAsync();
        }

        await using var reloaded = await LearningFixture.CreateAsync(CreateDb(database, root));
        var inference = await reloaded.Curriculum.TryInferComposedSemanticTransitionAsync(
            "en", submission.Examples[0].SourceText, [], null);
        Assert.NotEqual(LegendSemanticTransitionInference.Supported, inference.State);
        Assert.Null(inference.RealizedText);
        var proposalCount = await reloaded.Db.LegendLanguageTeacherProposals.CountAsync();
        var candidateCount = await reloaded.Db.LegendCorpusCandidates.CountAsync();
        var resubmitted = await reloaded.Service.SubmitConversationMachineProposalAsync(submission);
        Assert.False(resubmitted.Succeeded);
        Assert.False(resubmitted.ProposalAlreadyExisted);
        Assert.Equal(proposalCount, await reloaded.Db.LegendLanguageTeacherProposals.CountAsync());
        Assert.Equal(candidateCount, await reloaded.Db.LegendCorpusCandidates.CountAsync());
        Assert.Equal(0, reloaded.Teacher.CritiqueCalls);
    }

    [Fact]
    public async Task GenuineLegacyProposal_ResubmissionReusesItsExactEvidenceWithoutInventingABinding()
    {
        await using var fixture = await LearningFixture.CreateAsync();
        var submission = LegendConnectConversationMachineProposalTests.NovelSameLanguageSubmission();
        await LegendConnectConversationMachineProposalTests.SeedGovernedSameLanguageSemanticsAsync(fixture.Db, fixture.Curriculum);
        var created = await fixture.Service.SubmitConversationMachineProposalAsync(submission);
        Assert.True(created.Succeeded, created.Message);
        var proposal = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync(item => item.Id == created.ProposalId);
        Assert.Equal("AwaitingCritic", proposal.ValidationState);
        Assert.True(LegendConnectAutonomousLearningService.TryReadMachineProposalPayload(
            proposal.ProposalPayloadJson, out var family, out var originalBinding));
        Assert.NotNull(family);
        Assert.NotNull(originalBinding);
        var candidate = await fixture.Db.LegendCorpusCandidates.SingleAsync(item => item.Id == proposal.CorpusCandidateId);

        // Reconstruct the prior server format before any critic approval.
        // The authentic candidate, source packet, and unchanged evidence hash
        // are preserved; no approval or curriculum state is manufactured.
        proposal.ProposalPayloadJson = JsonSerializer.Serialize(family);
        proposal.ProposalIdentity = LegendLanguageIdentity.TextHash(string.Join("|",
            "language-teacher-proposal:v1", candidate.IdempotencyKey,
            proposal.EvidenceIdentityHash, proposal.ProposalPayloadJson));
        var legacyIdentity = proposal.ProposalIdentity;
        var legacyPayload = proposal.ProposalPayloadJson;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var repeated = await fixture.Service.SubmitConversationMachineProposalAsync(submission);
        Assert.True(repeated.Succeeded, repeated.Message);
        Assert.True(repeated.ProposalAlreadyExisted);
        Assert.Equal(created.ProposalId, repeated.ProposalId);
        var retained = Assert.Single(await fixture.Db.LegendLanguageTeacherProposals.ToArrayAsync());
        Assert.Equal(legacyIdentity, retained.ProposalIdentity);
        Assert.Equal(legacyPayload, retained.ProposalPayloadJson);
        Assert.True(LegendConnectAutonomousLearningService.TryReadMachineProposalPayload(
            retained.ProposalPayloadJson, out _, out var fabricatedBinding));
        Assert.Null(fabricatedBinding);
        Assert.False(retained.CriticApproved);
        Assert.Null(retained.CurriculumAdmittedUtc);
        Assert.Equal(0, fixture.Teacher.CritiqueCalls);
    }

    [Fact]
    public async Task VersionTwoEnvelope_CannotMasqueradeAsALegacyBareFamilyDuringRollback()
    {
        await using var fixture = await LearningFixture.CreateAsync();
        var submission = LegendConnectConversationMachineProposalTests.NovelSameLanguageSubmission();
        var id = await AdmitAsync(fixture, submission);
        var proposal = await fixture.Db.LegendLanguageTeacherProposals.SingleAsync(item => item.Id == id);

        var legacyFamily = JsonSerializer.Deserialize<LegendLanguageTeacherFamilyProposal>(proposal.ProposalPayloadJson);
        Assert.NotNull(legacyFamily);
        Assert.Null(legacyFamily.Examples);
        Assert.True(string.IsNullOrWhiteSpace(legacyFamily.FamilyKey));
        Assert.True(LegendConnectAutonomousLearningService.TryReadMachineProposalPayload(
            proposal.ProposalPayloadJson, out var actualFamily, out var binding));
        Assert.NotNull(actualFamily);
        Assert.Equal(2, actualFamily.Examples.Count);
        Assert.NotNull(binding);
    }

    [Fact]
    public async Task NewFounderContradiction_VetoesThePreviouslyLearnedInterpretation()
    {
        var database = Guid.NewGuid().ToString("N");
        var root = new InMemoryDatabaseRoot();
        var submission = LegendConnectConversationMachineProposalTests.NovelSameLanguageSubmission();
        await using (var fixture = await LearningFixture.CreateAsync(CreateDb(database, root)))
        {
            await AdmitAsync(fixture, submission);
            await AddHealthyEvidenceAsync(fixture, submission);
            await AssertLearnedSurfaceServesAsync(fixture, submission.Examples[0].SourceText);
            var contradictory = await fixture.Curriculum.SubmitFounderBatchAsync(new LegendConnectCurriculumBatchSubmission(
                submission.FamilyKey, submission.SemanticCategory,
                [
                    new LegendConnectCurriculumExampleSubmission(
                        "The farewell form is " + submission.Examples[0].SourceText,
                        new Dictionary<string, string> { ["conversation_function"] = "farewell" },
                        new LegendConnectMeaningGraphSubmission(
                            [new LegendConnectMeaningNodeSubmission("meaning", "conversation_function", "farewell",
                                submission.Examples[0].SourceText.TrimEnd('.'))], [])),
                    Example("Farewell traveler.", "departure")
                ],
                [new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(new Dictionary<string, string> { ["conversation_function"] = "farewell" }),
                    new LegendConnectSemanticFrameSubmission(new Dictionary<string, string> { ["conversation_function"] = "departure" }))]));
            Assert.True(contradictory.Succeeded, contradictory.Message);
        }

        await using var reloaded = await LearningFixture.CreateAsync(CreateDb(database, root));
        var current = await reloaded.Curriculum.AnalyzeReusableMeaningGraphAsync("en", submission.Examples[0].SourceText);
        Assert.False(current.IsComposed && current.Nodes.Any(node =>
                node.Provenance == "SystemValidatedMachine" && node.SemanticValue == "greeting"),
            "A diagnostic graph may retain competing candidates, but it cannot admit the contradicted machine interpretation.");
        var inference = await reloaded.Curriculum.TryInferComposedSemanticTransitionAsync(
            "en", submission.Examples[0].SourceText, [], null);
        if (inference.State == LegendSemanticTransitionInference.Supported)
        {
            // A new Founder interpretation may legitimately answer. The old
            // machine greeting-to-welcome path must not survive its contradiction.
            var response = await reloaded.Curriculum.AnalyzeReusableMeaningGraphAsync("en", inference.RealizedText!);
            Assert.True(response.IsComposed, response.ReasonCode);
            Assert.DoesNotContain(response.Nodes, node => node.SemanticValue == "welcome");
            Assert.Contains(response.Nodes, node => node.SemanticValue == "departure");
        }
        else
        {
            Assert.Null(inference.RealizedText);
        }
    }

    private static async Task<Guid> AdmitAsync(LearningFixture fixture, LegendConnectMachineTeachingSubmission submission)
    {
        await LegendConnectConversationMachineProposalTests.SeedGovernedSameLanguageSemanticsAsync(fixture.Db, fixture.Curriculum);
        var result = await fixture.Service.SubmitConversationMachineProposalAsync(submission);
        Assert.True(result.Succeeded, result.Message);
        for (var phase = 0; phase < 3; phase++)
            await fixture.Service.ProcessOneAsync();
        var proposal = await ReloadProposalAsync(fixture.Db, result.ProposalId!.Value);
        Assert.Equal("CurriculumAdmitted", proposal.ValidationState);
        Assert.Equal(1, fixture.Teacher.CritiqueCalls);
        Assert.Equal(0, fixture.Teacher.ProposeCalls);
        AssertServerEnvelope(proposal.ProposalPayloadJson);
        return proposal.Id;
    }

    private static async Task AddHealthyEvidenceAsync(LearningFixture fixture, LegendConnectMachineTeachingSubmission submission)
    {
        var beforeExamples = await fixture.Db.LegendCurriculumExamples.CountAsync();
        var beforeContrasts = await fixture.Db.Set<LegendLanguageStructuralEvidence>().CountAsync();
        var result = await fixture.Curriculum.SubmitFounderBatchAsync(new LegendConnectCurriculumBatchSubmission(
            submission.FamilyKey, submission.SemanticCategory,
            [
                Example("Greetings traveler.", "greeting"),
                Example("You are welcome here.", "welcome")
            ], submission.SemanticTransitions));
        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.CurriculumFamilyId.HasValue);
        await fixture.Curriculum.ReevaluateHistoricalWorkItemAsync(
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            result.CurriculumFamilyId.Value, "en");
        Assert.True(await fixture.Db.LegendCurriculumExamples.CountAsync() > beforeExamples);
        Assert.True(await fixture.Db.Set<LegendLanguageStructuralEvidence>().CountAsync() > beforeContrasts,
            "Healthy growth must add real independent contrast evidence, not just an unused example row.");
        fixture.Db.ChangeTracker.Clear();
    }

    private static LegendConnectCurriculumExampleSubmission Example(string text, string function) =>
        new(text, new Dictionary<string, string> { ["conversation_function"] = function },
            new LegendConnectMeaningGraphSubmission(
                [new LegendConnectMeaningNodeSubmission("meaning", "conversation_function", function, text.TrimEnd('.'))], []));

    private static async Task AssertLearnedSurfaceServesAsync(LearningFixture fixture, string surface)
    {
        var graph = await fixture.Curriculum.AnalyzeReusableMeaningGraphAsync("en", surface);
        Assert.True(graph.IsComposed, graph.ReasonCode);
        Assert.Equal("SystemValidatedMachine", Assert.Single(graph.Nodes).Provenance);
        var inference = await fixture.Curriculum.TryInferComposedSemanticTransitionAsync("en", surface, [], null);
        Assert.Equal(LegendSemanticTransitionInference.Supported, inference.State);
        Assert.Contains("broad_governed_semantic_transition", inference.Reasons);
        Assert.False(string.IsNullOrWhiteSpace(inference.RealizedText));
        var response = await fixture.Curriculum.AnalyzeReusableMeaningGraphAsync("en", inference.RealizedText!);
        Assert.True(response.IsComposed, response.ReasonCode);
        Assert.Equal("welcome", Assert.Single(response.Nodes).SemanticValue);
    }

    private static void AssertServerEnvelope(string payload)
    {
        using var parsed = JsonDocument.Parse(payload);
        Assert.Equal(2, parsed.RootElement.GetProperty("LegendServerPayloadVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.GetProperty("Family").ValueKind);
        var bindings = parsed.RootElement.GetProperty("CriticEvidenceBindings");
        Assert.Contains(bindings.ValueKind, new[] { JsonValueKind.Object, JsonValueKind.Array });
        Assert.NotEqual("[]", bindings.GetRawText());
        Assert.NotEqual("{}", bindings.GetRawText());
    }

    private static async Task<LegendLanguageTeacherProposal> ReloadProposalAsync(MasterAppDbContext db, Guid id)
    {
        db.ChangeTracker.Clear();
        return await db.LegendLanguageTeacherProposals.SingleAsync(item => item.Id == id);
    }

    private static async Task<(int Examples, int Anchors, int Nodes, int Transitions)> CanonicalCountsAsync(MasterAppDbContext db) =>
        (await db.LegendCurriculumExamples.CountAsync(), await db.LegendLanguageCompositionalAnchors.CountAsync(),
         await db.Set<LegendLanguageMeaningNodeEvidence>().CountAsync(), await db.LegendSemanticTransitionEvidence.CountAsync());

    private static MasterAppDbContext CreateDb(string name, InMemoryDatabaseRoot root) =>
        new(new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase(name, root)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
}
