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

/// <summary>
/// Regression proof for the durable evaluator-version contract. It exercises
/// the same runtime policy, curriculum, and quality authorities used by the
/// hosted learning worker; no test-only historical processor is introduced.
/// </summary>
public sealed class LegendConnectHistoricalReevaluationVersioningTests
{
    [Fact]
    public void CurrentEvaluatorVersion_IsTwentyOneForGovernedExecutableProjectionReplay()
    {
        Assert.Equal(
            21,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
    }

    [Fact]
    public async Task InFlightV19_IsNeverResetOrReclassifiedWhenV20CodeStarts()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);

        var v19 = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(19);
        Assert.True(v19.RequiresWork);
        Assert.Equal(19, v19.TargetEvaluatorVersion);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, v19.Phase);

        var v20Request = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(20);
        Assert.Equal(19, v20Request.TargetEvaluatorVersion);
        Assert.Equal(v19.Phase, v20Request.Phase);
        Assert.Equal(v19.CompletedEvaluatorVersion, v20Request.CompletedEvaluatorVersion);
        Assert.Empty(await db.LegendLanguageDerivationConvergences
            .Where(item => item.TargetEvaluatorVersion == 20)
            .ToListAsync());
    }

    [Fact]
    public async Task UnchangedDerivationContracts_AdvanceWithoutBroadHistoricalReplay()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration, runtime);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            runtimePolicy: runtime,
            curriculum: curriculum,
            intelligence: intelligence);

        foreach (var familyKey in new[] { "version.one", "version.two", "version.three" })
        {
            var submitted = await curriculum.SubmitFounderBatchAsync(new LegendConnectCurriculumBatchSubmission(
                familyKey,
                "Versioned historical evidence",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        $"I inspect {familyKey}.", new Dictionary<string, string> { ["agent"] = "I", ["predicate"] = "inspect" }),
                    new LegendConnectCurriculumExampleSubmission(
                        $"You inspect {familyKey}.", new Dictionary<string, string> { ["agent"] = "You", ["predicate"] = "inspect" })
                ]));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var pair = Assert.IsType<LegendLanguagePairSnapshot>(await registry.GetOrCreateEnabledPairAsync("en", "x-test"));
        var source = await db.LegendLanguageTextUnits.SingleAsync(item => item.Text == "I inspect version.one.");
        var providerTarget = Unit("x-test", "provider-only historical observation", "ProviderDerived");
        var providerAlignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = providerTarget.Id,
            Provider = "AzureTranslator", Provenance = "ProviderDerived", QualityState = "Observation", ObservationCount = 1
        };
        db.AddRange(providerTarget, providerAlignment);
        await db.SaveChangesAsync();

        var initial = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(1);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, initial.Phase);
        await DrainCanonicalWorkerCycleAsync(runtime, curriculum, intelligence, operations, 1, take: 1);

        var versionOne = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(1);
        Assert.False(versionOne.RequiresWork);
        Assert.Equal(1, versionOne.CompletedEvaluatorVersion);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Complete, versionOne.Phase);

        var sourceLineageBefore = new
        {
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Alignments = await db.LegendTranslationAlignments.CountAsync(),
            Submissions = await db.LegendFounderTrainingSubmissions.CountAsync(),
            SubmissionUnits = await db.LegendFounderTrainingSubmissionUnits.CountAsync(),
            QualityEvidence = await db.LegendTranslationQualityEvidence.CountAsync()
        };
        var pattern = await db.LegendLanguageStructuralPatterns.FirstAsync();
        pattern.MaturityState = "StaleTestState";
        pattern.SupportCount = 99;
        await db.SaveChangesAsync();

        // A version number alone is not semantic invalidation. The contract
        // graph is unchanged from v1 to v2, so all canonical evidence must be
        // reused rather than rebuilding SourceFamilies merely because the
        // deployment marker advanced.
        var versionTwoStart = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(2);
        Assert.False(versionTwoStart.RequiresWork);
        Assert.Equal(2, versionTwoStart.CompletedEvaluatorVersion);
        Assert.Equal(2, versionTwoStart.TargetEvaluatorVersion);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Complete, versionTwoStart.Phase);
        Assert.Null(versionTwoStart.Cursor);

        var versionTwo = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(2);
        var sourceLineageAfter = new
        {
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Alignments = await db.LegendTranslationAlignments.CountAsync(),
            Submissions = await db.LegendFounderTrainingSubmissions.CountAsync(),
            SubmissionUnits = await db.LegendFounderTrainingSubmissionUnits.CountAsync(),
            QualityEvidence = await db.LegendTranslationQualityEvidence.CountAsync()
        };
        var unrecomputed = await db.LegendLanguageStructuralPatterns.SingleAsync(item => item.Id == pattern.Id);

        Assert.False(versionTwo.RequiresWork);
        Assert.Equal(2, versionTwo.CompletedEvaluatorVersion);
        Assert.Equal(sourceLineageBefore, sourceLineageAfter);
        Assert.Equal("StaleTestState", unrecomputed.MaturityState);
        Assert.Equal(99, unrecomputed.SupportCount);
        Assert.False((await db.LegendTranslationAlignments.SingleAsync(item => item.Id == providerAlignment.Id)).HumanVerified);
        Assert.Equal(sourceLineageBefore.QualityEvidence,
            await db.LegendTranslationQualityEvidence.CountAsync());
        var convergence = await db.LegendLanguageDerivationConvergences
            .SingleAsync(item => item.TargetEvaluatorVersion == 2);
        Assert.Equal("Reused", convergence.State);
        Assert.Equal(0, convergence.AffectedCanonicalArtifactCount);
        Assert.Equal(convergence.ExistingCanonicalArtifactCount, convergence.ReusedCanonicalArtifactCount);

        var converged = new
        {
            Patterns = await db.LegendLanguageStructuralPatterns.CountAsync(),
            Evidence = await db.LegendLanguageStructuralEvidence.CountAsync(),
            Quality = await db.LegendTranslationQualityEvidence.CountAsync(),
            Relationships = await db.LegendLanguageStructuralRelationships.CountAsync()
        };
        var secondPass = new
        {
            Patterns = await db.LegendLanguageStructuralPatterns.CountAsync(),
            Evidence = await db.LegendLanguageStructuralEvidence.CountAsync(),
            Quality = await db.LegendTranslationQualityEvidence.CountAsync(),
            Relationships = await db.LegendLanguageStructuralRelationships.CountAsync()
        };
        Assert.Equal(converged, secondPass);
    }

    [Fact]
    public async Task ChangedEarlyDerivationContract_ExpandsOnlyThroughItsDeclaredDownstreamDependencies()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration, runtime);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db, registry, corpus, configuration, runtimePolicy: runtime,
            curriculum: curriculum, intelligence: intelligence);

        var submitted = await curriculum.SubmitFounderBatchAsync(new LegendConnectCurriculumBatchSubmission(
            "dependency.frontier", "Controlled historical evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "I compare the governed state.", new Dictionary<string, string>
                    {
                        ["actor"] = "I", ["intent"] = "compare"
                    }),
                new LegendConnectCurriculumExampleSubmission(
                    "You compare the governed state.", new Dictionary<string, string>
                    {
                        ["actor"] = "You", ["intent"] = "compare"
                    })
            ]));
        Assert.True(submitted.Succeeded, submitted.Message);

        await EstablishCompletedContractBaselineAsync(
            db,
            19,
            LegendConnectDerivationContracts.SourceSemanticProjection);
        var start = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(20);
        Assert.True(start.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory, start.Phase);
        Assert.Equal(19, start.CompletedEvaluatorVersion);

        await DrainCanonicalWorkerCycleAsync(runtime, curriculum, intelligence, operations, 20, take: 1);
        var completed = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(20);
        Assert.False(completed.RequiresWork);
        Assert.Equal(20, completed.CompletedEvaluatorVersion);
        var convergence = await db.LegendLanguageDerivationConvergences
            .SingleAsync(item => item.TargetEvaluatorVersion == 20);
        Assert.Equal("Completed", convergence.State);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            convergence.EarliestAffectedPhase);
        Assert.True(convergence.AffectedCanonicalArtifactCount > 0);
        Assert.True(convergence.ReusedCanonicalArtifactCount < convergence.ExistingCanonicalArtifactCount);
    }

    [Fact]
    public async Task CompletedV21_WithV20SourceArtifacts_ReopensTheCanonicalSourceFrontierExactlyOnce()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);

        // Model the failed V21 deployment shape precisely: v3 was recorded
        // as Current, but retained source artifacts still carry the v2
        // identity and the policy watermark was incorrectly marked complete.
        await EstablishCompletedContractBaselineAsync(db, 20);
        var sourceV2 = LegendConnectDerivationContracts.ContractIdentityFor(
            20,
            LegendConnectDerivationContracts.SourceSemanticProjection);
        var sourceV3 = LegendConnectDerivationContracts.ContractIdentityFor(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            LegendConnectDerivationContracts.SourceSemanticProjection);
        db.LegendLanguageDerivationContracts.Add(new LegendLanguageDerivationContract
        {
            Id = Guid.NewGuid(),
            DerivationKind = LegendConnectDerivationContracts.SourceSemanticProjection,
            ContractVersion = "3",
            ContractIdentity = sourceV3,
            EarliestPhase = LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            RequiresHistoricalWork = true,
            IntroducedEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            State = "Current",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.LegendLanguageDerivationArtifacts.Add(new LegendLanguageDerivationArtifact
        {
            Id = Guid.NewGuid(),
            ArtifactKind = "compositional-anchor",
            ResultArtifactIdentity = "anchor:historical-v20",
            SourceDependencyIdentity = "anchor-evidence:historical-v20",
            SourceDependencySemanticVersion = "historical-v20",
            DerivationContractIdentity = sourceV2,
            State = "Current",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.LegendLanguageDerivationArtifacts.Add(new LegendLanguageDerivationArtifact
        {
            Id = Guid.NewGuid(),
            ArtifactKind = "meaning-primitive",
            ResultArtifactIdentity = "meaning-primitive:historical-v20",
            SourceDependencyIdentity = "meaning-node:historical-v20",
            SourceDependencySemanticVersion = "historical-v20",
            DerivationContractIdentity = sourceV2,
            State = "Current",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.LegendLanguageCompositionalAnchors.Add(new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            TextUnitId = Guid.NewGuid(),
            CurriculumFamilyId = Guid.NewGuid(),
            CurriculumExampleId = Guid.NewGuid(),
            Dimension = "intent",
            Value = "historical",
            AnchorSignature = "historical-v20-anchor",
            Provenance = "FounderApproved"
        });
        var policy = await db.LegendConnectRuntimePolicies.SingleAsync();
        policy.TargetLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        policy.CompletedLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        policy.LanguageIntelligenceReevaluationPhase = LegendConnectLanguageIntelligenceReevaluationPhases.Complete;
        await db.SaveChangesAsync();

        var first = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        var second = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

        Assert.True(first.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, first.Phase);
        Assert.Equal(first, second);
        Assert.All(await db.LegendLanguageDerivationArtifacts.ToListAsync(),
            item => Assert.Equal("Stale", item.State));
        Assert.Single(await db.LegendLanguageDerivationConvergences
            .Where(item => item.TargetEvaluatorVersion == LegendConnectLanguageIntelligenceEvaluatorVersion.Current)
            .ToListAsync());
        var convergence = await db.LegendLanguageDerivationConvergences.SingleAsync(
            item => item.TargetEvaluatorVersion == LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            convergence.EarliestAffectedPhase);
        Assert.True(convergence.AffectedCanonicalArtifactCount > 0);
    }

    [Fact]
    public async Task DependencyInventory_ReactivatesTheExactRetainedArtifactWithoutCreatingADuplicate()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var family = AddHistoricalSourceFamily(
            db,
            Guid.Parse("00000000-0000-0000-0000-000000000099"),
            "replay.source.reactivate-exact-artifact");
        await db.SaveChangesAsync();
        var anchor = await db.LegendLanguageCompositionalAnchors.SingleAsync(item =>
            item.Id == family.AnchorId);
        var contractIdentity = LegendConnectDerivationContracts.ContractIdentityFor(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            LegendConnectDerivationContracts.SourceSemanticProjection);
        var retained = new LegendLanguageDerivationArtifact
        {
            Id = Guid.NewGuid(),
            ArtifactKind = "compositional-anchor",
            ResultArtifactIdentity = $"anchor:{anchor.CurriculumExampleId:D}:{anchor.AnchorSignature}",
            SourceDependencyIdentity = $"anchor-evidence:{anchor.Id:D}",
            SourceDependencySemanticVersion = "stale-semantic-version",
            DerivationContractIdentity = contractIdentity,
            State = "Stale",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.LegendLanguageDerivationArtifacts.Add(retained);
        await db.SaveChangesAsync();

        await curriculum.InventoryHistoricalDerivationDependenciesAsync(
            family.FamilyId,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

        var artifacts = await db.LegendLanguageDerivationArtifacts.ToListAsync();
        Assert.Single(artifacts);
        Assert.Equal(retained.Id, artifacts[0].Id);
        Assert.Equal("Current", artifacts[0].State);
        Assert.Equal(anchor.AnchorSignature, artifacts[0].SourceDependencySemanticVersion);
    }

    [Fact]
    public async Task MatchingV21ContractAndArtifacts_AreSafelyReusedWithoutReplay()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var evaluator = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        await EstablishCompletedContractBaselineAsync(db, evaluator);
        var sourceIdentity = LegendConnectDerivationContracts.ContractIdentityFor(
            evaluator,
            LegendConnectDerivationContracts.SourceSemanticProjection);
        db.LegendLanguageDerivationArtifacts.Add(new LegendLanguageDerivationArtifact
        {
            Id = Guid.NewGuid(),
            ArtifactKind = "compositional-anchor",
            ResultArtifactIdentity = "anchor:current-v21",
            SourceDependencyIdentity = "anchor-evidence:current-v21",
            SourceDependencySemanticVersion = "current-v21",
            DerivationContractIdentity = sourceIdentity,
            State = "Current",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var replay = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluator);

        Assert.False(replay.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Complete, replay.Phase);
        Assert.Empty(await db.LegendLanguageDerivationConvergences.ToListAsync());
        Assert.Equal("Current", (await db.LegendLanguageDerivationArtifacts.SingleAsync()).State);
    }

    [Fact]
    public async Task CompletedPolicy_RepairsOnlyDrainedConvergenceInspectionProjection()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var evaluator = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        await EstablishCompletedContractBaselineAsync(db, evaluator);
        db.LegendLanguageDerivationConvergences.Add(new LegendLanguageDerivationConvergence
        {
            Id = Guid.NewGuid(),
            TargetEvaluatorVersion = evaluator,
            BaselineEvaluatorVersion = evaluator,
            State = "Processing",
            EarliestAffectedPhase = LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            PlannedWorkItemCount = 0,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var replay = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluator);

        Assert.False(replay.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Complete, replay.Phase);
        var convergence = await db.LegendLanguageDerivationConvergences.SingleAsync(item =>
            item.TargetEvaluatorVersion == evaluator);
        Assert.Equal("Completed", convergence.State);
        Assert.NotNull(convergence.CompletedUtc);
    }

    [Fact]
    public async Task CompletedPolicy_DoesNotHideActiveDurableConvergenceWork()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var evaluator = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        await EstablishCompletedContractBaselineAsync(db, evaluator);
        db.LegendLanguageDerivationConvergences.Add(new LegendLanguageDerivationConvergence
        {
            Id = Guid.NewGuid(),
            TargetEvaluatorVersion = evaluator,
            BaselineEvaluatorVersion = evaluator,
            State = "Processing",
            EarliestAffectedPhase = LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            PlannedWorkItemCount = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.LegendHistoricalReevaluationWorkItems.Add(new LegendHistoricalReevaluationWorkItem
        {
            Id = Guid.NewGuid(),
            EvaluatorVersion = evaluator,
            Phase = LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            WorkKind = "Canonical",
            WorkIdentity = "active-convergence-regression",
            SubjectScope = "en",
            DependencyIdentity = "active-convergence-regression",
            ProcessingState = "Pending",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        _ = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluator);

        var convergence = await db.LegendLanguageDerivationConvergences.SingleAsync(item =>
            item.TargetEvaluatorVersion == evaluator);
        Assert.Equal("Processing", convergence.State);
        Assert.Null(convergence.CompletedUtc);
    }

    [Fact]
    public async Task CompletedV21_WithAnUnpersistedV20Declaration_StillDetectsTheStaleSourceArtifact()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var evaluator = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;

        // This is the production-shaped interrupted deployment: the newer
        // declaration was persisted as Current, but the older declaration
        // was never recorded even though its artifact remained current.
        foreach (var definition in LegendConnectDerivationContracts.ForEvaluator(evaluator))
        {
            db.LegendLanguageDerivationContracts.Add(new LegendLanguageDerivationContract
            {
                Id = Guid.NewGuid(),
                DerivationKind = definition.DerivationKind,
                ContractVersion = definition.ContractVersion,
                ContractIdentity = definition.ContractIdentity,
                EarliestPhase = definition.EarliestPhase,
                RequiresHistoricalWork = definition.RequiresHistoricalWork,
                IntroducedEvaluatorVersion = definition.IntroducedEvaluatorVersion,
                State = "Current",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }
        var v20SourceIdentity = LegendConnectDerivationContracts.ContractIdentityFor(
            20,
            LegendConnectDerivationContracts.SourceSemanticProjection);
        db.LegendLanguageDerivationArtifacts.Add(new LegendLanguageDerivationArtifact
        {
            Id = Guid.NewGuid(),
            ArtifactKind = "compositional-anchor",
            ResultArtifactIdentity = "anchor:unpersisted-v20-contract",
            SourceDependencyIdentity = "anchor-evidence:unpersisted-v20-contract",
            SourceDependencySemanticVersion = "unpersisted-v20-contract",
            DerivationContractIdentity = v20SourceIdentity,
            State = "Current",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.LegendConnectRuntimePolicies.Add(new LegendConnectRuntimePolicy
        {
            Id = Guid.NewGuid(),
            ScopeKey = "Global",
            TargetLanguageIntelligenceEvaluatorVersion = evaluator,
            CompletedLanguageIntelligenceEvaluatorVersion = evaluator,
            LanguageIntelligenceReevaluationPhase = LegendConnectLanguageIntelligenceReevaluationPhases.Complete,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var replay = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluator);

        Assert.True(replay.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, replay.Phase);
        Assert.Equal("Stale", (await db.LegendLanguageDerivationArtifacts.SingleAsync()).State);
    }

    [Fact]
    public async Task CurrentVersion_ReplaysHistoricalProviderSemanticConflictsWithoutAnEnglishPivot_AndConverges()
    {
        await using var historicalDb = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var historicalRegistry = new LegendLanguageRegistry(historicalDb, configuration);
        var historicalRuntime = new LegendConnectRuntimePolicyAuthority(
            historicalDb, new FounderAccess(), historicalRegistry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var historicalIntelligence = new LegendConnectTranslationIntelligence(historicalDb, configuration, historicalRuntime);
        var historicalCorpus = new LegendConnectCorpusService(
            historicalDb, historicalRegistry, NullLogger<LegendConnectCorpusService>.Instance,
            intelligence: historicalIntelligence);
        var historicalCurriculum = new LegendConnectCurriculumService(historicalDb, historicalRegistry, historicalCorpus);
        var historicalOperations = new LegendConnectOperations(
            historicalDb,
            historicalRegistry,
            historicalCorpus,
            configuration,
            runtimePolicy: historicalRuntime,
            curriculum: historicalCurriculum,
            intelligence: historicalIntelligence);

        // The v3 checkpoint represents the already-deployed evaluator before
        // this precise provider-quality retention correction.
        await DrainCanonicalWorkerCycleAsync(
            historicalRuntime,
            historicalCurriculum,
            historicalIntelligence,
            historicalOperations,
            3,
            take: 1);
        var historicalSeed = await SeedFounderConflictAsync(historicalDb, historicalRegistry);
        await EstablishCompletedContractBaselineAsync(historicalDb, 19, corruptedKind:
            LegendConnectDerivationContracts.ProviderObservationProjection);
        var replay = await historicalRuntime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.True(replay.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory, replay.Phase);

        await DrainCanonicalWorkerCycleAsync(
            historicalRuntime,
            historicalCurriculum,
            historicalIntelligence,
            historicalOperations,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            take: 1);

        var historicalEvidence = await QualityShapeAsync(historicalDb, historicalSeed.ProviderAlignmentId);
        Assert.Equal(
            [
                "Contradictory|human_verified_directional_conflict|none|Open",
                "Insufficient|known_semantic_component_not_realized|semantic|Open"
            ],
            historicalEvidence);
        var historicalProvider = await historicalDb.LegendTranslationAlignments
            .SingleAsync(item => item.Id == historicalSeed.ProviderAlignmentId);
        Assert.Equal("ProviderDerived", historicalProvider.Provenance);
        Assert.False(historicalProvider.HumanVerified);
        var trustedMemory = await historicalIntelligence.TryGetTrustedExactMemoryAsync(
            historicalSeed.SourceLanguageCode,
            historicalSeed.TargetLanguageCode,
            historicalSeed.SourceText);
        Assert.Equal("trusted correction target", trustedMemory?.Text);

        var historicalFirstPass = new
        {
            Quality = await historicalDb.LegendTranslationQualityEvidence.CountAsync(),
            Structural = await historicalDb.LegendLanguageStructuralEvidence.CountAsync(),
            Alignments = await historicalDb.LegendTranslationAlignments.CountAsync()
        };
        await DrainCanonicalWorkerCycleAsync(
            historicalRuntime,
            historicalCurriculum,
            historicalIntelligence,
            historicalOperations,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            take: 1);
        var historicalSecondPass = new
        {
            Quality = await historicalDb.LegendTranslationQualityEvidence.CountAsync(),
            Structural = await historicalDb.LegendLanguageStructuralEvidence.CountAsync(),
            Alignments = await historicalDb.LegendTranslationAlignments.CountAsync()
        };
        Assert.Equal(historicalFirstPass, historicalSecondPass);

        await using var currentDb = ControllerTestHelpers.BuildDb();
        var currentRegistry = new LegendLanguageRegistry(currentDb, configuration);
        var currentIntelligence = new LegendConnectTranslationIntelligence(currentDb, configuration);
        var currentSeed = await SeedFounderConflictAsync(currentDb, currentRegistry);
        await currentIntelligence.EvaluateProviderObservationAsync(currentSeed.ProviderAlignmentId);

        Assert.Equal(historicalEvidence, await QualityShapeAsync(currentDb, currentSeed.ProviderAlignmentId));

        await historicalIntelligence.RecordHumanCorrectionAsync(
            historicalSeed.ProviderAlignmentId,
            historicalSeed.TrustedAlignmentId);
        var correctedHistory = await historicalDb.LegendTranslationQualityEvidence
            .Where(item => item.ObservedAlignmentId == historicalSeed.ProviderAlignmentId)
            .ToListAsync();
        Assert.Contains(correctedHistory, item =>
            item.Signal == "Contradictory" &&
            item.ReasonCode == "human_verified_directional_correction" &&
            item.RelatedAlignmentId == historicalSeed.TrustedAlignmentId &&
            item.ResolutionState == "Corrected");
        Assert.Equal("ProviderDerived", (await historicalDb.LegendTranslationAlignments
            .SingleAsync(item => item.Id == historicalSeed.ProviderAlignmentId)).Provenance);
    }

    [Fact]
    public async Task ProviderOnlyOutliers_RemainRetainedInsufficientAndCannotManufactureTrustedSupport()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync("x-source", "x-target"));
        var source = Unit("x-source", "provider-only source", "FounderApproved");
        var firstTarget = Unit("x-target", "provider outcome one", "ProviderDerived");
        var secondTarget = Unit("x-target", "provider outcome two", "ProviderDerived");
        var first = ProviderObservation(pair, source, firstTarget);
        var second = ProviderObservation(pair, source, secondTarget);
        db.AddRange(source, firstTarget, secondTarget, first, second);
        await db.SaveChangesAsync();

        await intelligence.EvaluateProviderObservationAsync(first.Id);
        await intelligence.EvaluateProviderObservationAsync(second.Id);
        await intelligence.EvaluateProviderObservationAsync(first.Id);
        await intelligence.EvaluateProviderObservationAsync(second.Id);

        var evidence = await db.LegendTranslationQualityEvidence.ToListAsync();
        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, item =>
        {
            Assert.Equal("Insufficient", item.Signal);
            Assert.Equal("no_established_pair_specific_evidence", item.ReasonCode);
        });
        Assert.All(await db.LegendTranslationAlignments.ToListAsync(), item => Assert.False(item.HumanVerified));
        Assert.Null(await intelligence.TryGetTrustedExactMemoryAsync("x-source", "x-target", source.Text));
    }

    [Fact]
    public async Task SourceFamilies_BoundedPageRepairsOnlyItsCurrentFamily_ThenResumesFromTheDurableCursor()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);

        var first = AddHistoricalSourceFamily(
            db,
            Guid.Parse("00000000-0000-0000-0000-000000000101"),
            "replay.source.first");
        var second = AddHistoricalSourceFamily(
            db,
            Guid.Parse("00000000-0000-0000-0000-000000000102"),
            "replay.source.second");
        await db.SaveChangesAsync();

        var start = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, start.Phase);
        Assert.Null(start.Cursor);

        var firstPage = await curriculum.ReevaluateHistoricalAlignmentsAsync(
            1,
            start.Phase,
            start.Cursor);
        Assert.Equal(first.FamilyId, firstPage.LastProcessedId);
        Assert.False(firstPage.PhaseComplete);
        await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            start.Phase,
            firstPage.LastProcessedId,
            firstPage.PhaseComplete);

        db.ChangeTracker.Clear();
        Assert.NotNull((await db.LegendLanguageCompositionalAnchors
            .SingleAsync(item => item.Id == first.AnchorId)).SemanticSignature);
        Assert.Null((await db.LegendLanguageCompositionalAnchors
            .SingleAsync(item => item.Id == second.AnchorId)).SemanticSignature);

        // Recreate the authority to model an application/worker restart. The
        // runtime policy, not an in-memory loop variable, owns resumption.
        var restarted = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), new LegendLanguageRegistry(db, configuration), configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var resumed = await restarted.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, resumed.Phase);
        Assert.Equal(first.FamilyId, resumed.Cursor);

        var secondPage = await curriculum.ReevaluateHistoricalAlignmentsAsync(
            1,
            resumed.Phase,
            resumed.Cursor);
        Assert.Equal(second.FamilyId, secondPage.LastProcessedId);
        await restarted.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            resumed.Phase,
            secondPage.LastProcessedId,
            secondPage.PhaseComplete);
        Assert.NotNull((await db.LegendLanguageCompositionalAnchors
            .SingleAsync(item => item.Id == second.AnchorId)).SemanticSignature);
    }

    [Fact]
    public async Task HistoricalAlignmentConflict_IsQuarantinedWithoutSelectingAValue_AndTheCursorContinues()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var events = new LegendConnectOperationalEventWriter(
            db, NullLogger<LegendConnectOperationalEventWriter>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus, events);
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync("en", "x-test"));

        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(), FamilyKey = "replay.alignment.conflict", Provenance = "FounderApproved"
        };
        var source = Unit("en", "A governed source.", "FounderApproved");
        var target = Unit("x-test", "A governed target.", "FounderApproved");
        var sourceExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = source.Id,
            LanguageCode = "en", Provenance = "FounderApproved"
        };
        var targetExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = target.Id,
            LanguageCode = "x-test", DerivedFromCurriculumExampleId = sourceExample.Id,
            Provenance = "FounderApproved"
        };
        var conflict = new LegendTranslationAlignment
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000201"),
            PairKey = pair.PairKey,
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = "FounderApproved",
            Provenance = "FounderApproved",
            HumanVerified = true,
            QualityState = "Verified",
            Confidence = 1m,
            ObservationCount = 1
        };
        db.AddRange(
            family,
            source,
            target,
            sourceExample,
            targetExample,
            new LegendCurriculumExampleVariation
            {
                Id = Guid.NewGuid(), CurriculumExampleId = sourceExample.Id,
                Dimension = "register", Value = "warm"
            },
            new LegendCurriculumExampleVariation
            {
                Id = Guid.NewGuid(), CurriculumExampleId = targetExample.Id,
                Dimension = "register", Value = "formal"
            },
            conflict);
        await db.SaveChangesAsync();

        var start = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            start.Phase,
            null,
            phaseComplete: true);
        var alignments = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Alignments, alignments.Phase);

        var page = await curriculum.ReevaluateHistoricalAlignmentsAsync(1, alignments.Phase, alignments.Cursor);
        Assert.Equal(conflict.Id, page.LastProcessedId);
        Assert.False(page.PhaseComplete);
        await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            alignments.Phase,
            page.LastProcessedId,
            page.PhaseComplete);
        db.ChangeTracker.Clear();

        var retained = await db.LegendCurriculumExampleVariations
            .SingleAsync(item => item.CurriculumExampleId == targetExample.Id && item.Dimension == "register");
        Assert.Equal("formal", retained.Value);
        Assert.Equal(1, await db.LegendCurriculumExampleVariations
            .CountAsync(item => item.CurriculumExampleId == targetExample.Id));
        Assert.Single(await db.LegendConnectOperationalEvents.Where(item =>
            item.Category == "HistoricalCurriculumReplay" &&
            item.ErrorCode == "conflicting_controlled_variation" &&
            item.CorrelationId == conflict.Id.ToString("D")).ToListAsync());

        var restarted = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), new LegendLanguageRegistry(db, configuration), configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var resumed = await restarted.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Alignments, resumed.Phase);
        Assert.Equal(conflict.Id, resumed.Cursor);

        var tail = await curriculum.ReevaluateHistoricalAlignmentsAsync(1, resumed.Phase, resumed.Cursor);
        Assert.True(tail.PhaseComplete);
        await restarted.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            resumed.Phase,
            tail.LastProcessedId,
            tail.PhaseComplete);
        Assert.Equal(
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            (await restarted.GetOrStartLanguageIntelligenceReevaluationAsync(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current)).Phase);
    }

    private static async Task DrainCanonicalWorkerCycleAsync(
        LegendConnectRuntimePolicyAuthority runtime,
        LegendConnectCurriculumService curriculum,
        ILegendConnectTranslationIntelligence intelligence,
        ILegendConnectOperations operations,
        int evaluatorVersion,
        int take)
    {
        for (var pass = 0; pass < 32; pass++)
        {
            var state = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluatorVersion);
            if (!state.RequiresWork)
                return;
            LegendConnectHistoricalReevaluationProgress progress;
            if (state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory)
            {
                var familyIds = await curriculum.GetHistoricalDependencyInventoryFamilyIdsAsync();
                foreach (var familyId in familyIds)
                {
                    await curriculum.InventoryHistoricalDerivationDependenciesAsync(
                        familyId,
                        evaluatorVersion);
                }
                progress = new LegendConnectHistoricalReevaluationProgress(
                    familyIds.Count,
                    familyIds.Count == 0 ? null : familyIds[^1],
                    PhaseComplete: true);
            }
            else if (state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
            {
                progress = await intelligence.ReevaluateHistoricalProviderObservationsAsync(
                    take,
                    state.Cursor);
            }
            else if (state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations)
            {
                progress = await operations.ReconcileHistoricalOperationalTranslationsAsync(
                    take,
                    state.Cursor);
            }
            else
            {
                progress = await curriculum.ReevaluateHistoricalAlignmentsAsync(
                    take,
                    state.Phase,
                    state.Cursor);
            }
            await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
                evaluatorVersion, state.Phase, progress.LastProcessedId, progress.PhaseComplete);
        }

        throw new Xunit.Sdk.XunitException("The bounded canonical historical replay did not converge.");
    }

    /// <summary>
    /// Models a completed pre-contract evaluator without re-evaluating or
    /// rewriting any canonical evidence.  A selected legacy identity is the
    /// test declaration of a genuine semantic-contract change; the runtime
    /// then derives the affected frontier from its normal contract graph.
    /// </summary>
    private static async Task EstablishCompletedContractBaselineAsync(
        MasterAppDbContext db,
        int evaluatorVersion,
        string? corruptedKind = null)
    {
        var policy = await db.LegendConnectRuntimePolicies.SingleOrDefaultAsync();
        if (policy is null)
        {
            policy = new LegendConnectRuntimePolicy
            {
                Id = Guid.NewGuid(),
                ScopeKey = "Global",
                LearningEnabled = true,
                ContextualCompositionMode = "Disabled",
                UpdatedUtc = DateTime.UtcNow
            };
            db.LegendConnectRuntimePolicies.Add(policy);
        }
        policy.TargetLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
        policy.CompletedLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
        policy.LanguageIntelligenceReevaluationPhase = LegendConnectLanguageIntelligenceReevaluationPhases.Complete;
        policy.LanguageIntelligenceReevaluationCursor = null;
        policy.LanguageIntelligenceReevaluationCompletedUtc = DateTime.UtcNow;
        foreach (var definition in LegendConnectDerivationContracts.ForEvaluator(evaluatorVersion))
        {
            var isCorrupted = string.Equals(definition.DerivationKind, corruptedKind, StringComparison.Ordinal);
            var contract = await db.LegendLanguageDerivationContracts
                .SingleOrDefaultAsync(item => item.DerivationKind == definition.DerivationKind &&
                    item.SupersededUtc == null)
                ?? new LegendLanguageDerivationContract
                {
                    Id = Guid.NewGuid(),
                    DerivationKind = definition.DerivationKind,
                    CreatedUtc = DateTime.UtcNow
                };
            if (db.Entry(contract).State == EntityState.Detached)
                db.LegendLanguageDerivationContracts.Add(contract);
            contract.ContractVersion = isCorrupted ? "legacy-test-contract" : definition.ContractVersion;
            contract.ContractIdentity = isCorrupted
                ? LegendLanguageIdentity.TextHash("legacy-test-contract|" + definition.DerivationKind)
                : definition.ContractIdentity;
            contract.EarliestPhase = definition.EarliestPhase;
            contract.RequiresHistoricalWork = definition.RequiresHistoricalWork;
            contract.IntroducedEvaluatorVersion = evaluatorVersion;
            contract.State = "Current";
            contract.SupersededUtc = null;
            contract.UpdatedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<ProviderConflictSeed> SeedFounderConflictAsync(
        MasterAppDbContext db,
        LegendLanguageRegistry registry)
    {
        const string sourceLanguageCode = "x-source";
        const string targetLanguageCode = "x-target";
        const string sourceText = "controlled provider audit source";
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync(sourceLanguageCode, targetLanguageCode));
        var source = Unit(sourceLanguageCode, sourceText, "FounderApproved");
        var providerTarget = Unit(targetLanguageCode, "provider observation target", "ProviderDerived");
        var trustedTarget = Unit(targetLanguageCode, "trusted correction target", "FounderApproved");
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(), FamilyKey = "provider.audit.semantic", Provenance = "FounderApproved"
        };
        var example = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = source.Id,
            LanguageCode = sourceLanguageCode, Provenance = "FounderApproved"
        };
        var semanticSignature = LegendLanguageIdentity.TextHash("semantic|controlled-state|reviewed");
        var anchor = new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(), LanguageCode = sourceLanguageCode, TextUnitId = source.Id,
            CurriculumFamilyId = family.Id, CurriculumExampleId = example.Id,
            Dimension = "controlled-state", Value = "reviewed", SemanticSignature = semanticSignature,
            AnchorSignature = LegendLanguageIdentity.TextHash($"{example.Id:D}|sentence|controlled-state|reviewed"),
            Provenance = "FounderApproved"
        };
        var provider = ProviderObservation(pair, source, providerTarget);
        var trusted = HumanAlignment(pair, source, trustedTarget);
        db.AddRange(source, providerTarget, trustedTarget, family, example, anchor, provider, trusted);
        await db.SaveChangesAsync();
        return new ProviderConflictSeed(provider.Id, trusted.Id, sourceLanguageCode, targetLanguageCode, sourceText);
    }

    private static HistoricalSourceFamily AddHistoricalSourceFamily(
        MasterAppDbContext db,
        Guid familyId,
        string familyKey)
    {
        var unit = Unit("en", $"Historical source {familyKey}.", "FounderApproved");
        var example = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = familyId,
            TextUnitId = unit.Id,
            LanguageCode = "en",
            Provenance = "FounderApproved"
        };
        var anchor = new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            TextUnitId = unit.Id,
            CurriculumFamilyId = familyId,
            CurriculumExampleId = example.Id,
            Dimension = "conversation_function",
            Value = "opening",
            SemanticSignature = null,
            AnchorSignature = LegendLanguageIdentity.TextHash($"{example.Id:D}|opening"),
            Provenance = "FounderApproved"
        };
        db.AddRange(
            new LegendCurriculumFamily
            {
                Id = familyId,
                FamilyKey = familyKey,
                Provenance = "FounderApproved"
            },
            unit,
            example,
            new LegendCurriculumExampleVariation
            {
                Id = Guid.NewGuid(),
                CurriculumExampleId = example.Id,
                Dimension = "conversation_function",
                Value = "opening"
            },
            anchor);
        return new HistoricalSourceFamily(familyId, anchor.Id);
    }

    private static async Task<List<string>> QualityShapeAsync(MasterAppDbContext db, Guid alignmentId)
    {
        var evidence = await db.LegendTranslationQualityEvidence
            .Where(item => item.ObservedAlignmentId == alignmentId && item.SupersededUtc == null)
            .OrderBy(item => item.Signal).ThenBy(item => item.ReasonCode)
            .ToListAsync();
        return evidence.Select(item => string.Join("|", item.Signal, item.ReasonCode,
            item.SemanticSignature == null ? "none" : "semantic", item.ResolutionState)).ToList();
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:Learning:Enabled"] = "true",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "0",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-test",
            ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Synthetic test language",
            ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Synthetic test language",
            ["LegendConnect:LanguageRegistry:Baseline:2:Code"] = "x-source",
            ["LegendConnect:LanguageRegistry:Baseline:2:Name"] = "Synthetic source language",
            ["LegendConnect:LanguageRegistry:Baseline:2:NativeName"] = "Synthetic source language",
            ["LegendConnect:LanguageRegistry:Baseline:3:Code"] = "x-target",
            ["LegendConnect:LanguageRegistry:Baseline:3:Name"] = "Synthetic target language",
            ["LegendConnect:LanguageRegistry:Baseline:3:NativeName"] = "Synthetic target language"
        }).Build();

    private static LegendLanguageTextUnit Unit(string languageCode, string text, string provenance) => new()
    {
        Id = Guid.NewGuid(),
        LanguageCode = languageCode,
        StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
        NormalizedHash = LegendLanguageIdentity.TextHash(text),
        Text = LegendLanguageIdentity.NormalizeText(text),
        Provenance = provenance,
        IsTrainingEligible = true
    };

    private static LegendTranslationAlignment ProviderObservation(
        LegendLanguagePairSnapshot pair,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) => new()
    {
        Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
        Provider = "AzureTranslator", Provenance = "ProviderDerived", QualityState = "Observation", ObservationCount = 1
    };

    private static LegendTranslationAlignment HumanAlignment(
        LegendLanguagePairSnapshot pair,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) => new()
    {
        Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
        Provider = "FounderApproved", Provenance = "FounderApproved", Confidence = 1m,
        QualityState = "Verified", HumanVerified = true, ObservationCount = 1
    };

    private sealed record ProviderConflictSeed(
        Guid ProviderAlignmentId,
        Guid TrustedAlignmentId,
        string SourceLanguageCode,
        string TargetLanguageCode,
        string SourceText);

    private sealed record HistoricalSourceFamily(Guid FamilyId, Guid AnchorId);

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(MessagingActor actor, string resourceType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, true));
        public Task<bool> IsFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
