using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

public sealed class LegendConnectHistoricalReevaluationWorkTests
{
    private const int EvaluatorVersion = 16;
    private readonly ITestOutputHelper _output;

    public LegendConnectHistoricalReevaluationWorkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SeededPhase_DoesNotAdvanceUntilEveryDurableWorkItemCompletes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync(EvaluatorVersion);
        AddSourceFamily(fixture.Db, "en", "first", 101);
        AddSourceFamily(fixture.Db, "en", "second", 102);
        AddSourceFamily(fixture.Db, "es", "third", 103);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(3, await (
            from example in fixture.Db.LegendCurriculumExamples
            join unit in fixture.Db.LegendLanguageTextUnits on example.TextUnitId equals unit.Id
            where example.DerivedFromCurriculumExampleId == null && example.SupersededUtc == null &&
                unit.IsTrainingEligible
            select new { example.CurriculumFamilyId, example.LanguageCode }).Distinct().CountAsync());

        var replayBeforeSeed = await fixture.Runtime
            .GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, replayBeforeSeed.Phase);

        var seeded = await fixture.Work.SeedNextBatchAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "test-seeder");
        Assert.Equal(3, seeded.SeededCount);
        Assert.True(seeded.SeedingComplete);
        Assert.False(await fixture.Work.TryAdvancePhaseAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies));

        await CompleteAllAsync(
            fixture.Work,
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies);

        Assert.True(await fixture.Work.TryAdvancePhaseAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies));
        var replay = await fixture.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Alignments, replay.Phase);
    }

    [Fact]
    public async Task V21ContractFrontier_RewindsDependentPhase_AndRequeuesEachFamilyExactlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync(LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        AddSourceFamily(fixture.Db, "en", "contract-frontier", 151);
        await fixture.Db.SaveChangesAsync();

        var policy = await fixture.Db.LegendConnectRuntimePolicies.SingleAsync();
        policy.TargetLanguageIntelligenceEvaluatorVersion =
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        policy.CompletedLanguageIntelligenceEvaluatorVersion =
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        policy.LanguageIntelligenceReevaluationPhase =
            LegendConnectLanguageIntelligenceReevaluationPhases.Alignments;
        policy.LanguageIntelligenceReevaluationCompletedUtc = null;
        var convergence = await fixture.Db.LegendLanguageDerivationConvergences.SingleAsync(item =>
            item.TargetEvaluatorVersion == LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        convergence.State = "Queued";
        convergence.EarliestAffectedPhase = LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies;
        convergence.ChangedContractCount = 1;
        convergence.AffectedCanonicalArtifactCount = 1;
        var family = await fixture.Db.LegendCurriculumFamilies.SingleAsync();
        fixture.Db.LegendHistoricalReevaluationWorkItems.Add(new LegendHistoricalReevaluationWorkItem
        {
            EvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            Phase = LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            WorkKind = "Canonical",
            WorkIdentity = $"source-family:{family.Id:D}|language:en",
            SubjectId = family.Id,
            SubjectScope = "en",
            DependencyIdentity = "source-language:en",
            ProcessingState = "Pending"
        });
        await fixture.Db.SaveChangesAsync();

        var replay = await fixture.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, replay.Phase);

        var first = await fixture.Work.SeedNextBatchAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "v21-contract-frontier");
        var second = await fixture.Work.SeedNextBatchAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "v21-contract-frontier");

        Assert.Equal(1, first.SeededCount);
        Assert.Equal(0, second.SeededCount);
        var rows = await fixture.Db.LegendHistoricalReevaluationWorkItems
            .Where(item => item.EvaluatorVersion == LegendConnectLanguageIntelligenceEvaluatorVersion.Current &&
                item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies &&
                item.WorkKind == "Canonical")
            .ToListAsync();
        Assert.Single(rows.Where(item => item.WorkIdentity.Contains("|contract-frontier:", StringComparison.Ordinal)));
        var legacy = Assert.Single(rows.Where(item => !item.WorkIdentity.Contains("|contract-frontier:", StringComparison.Ordinal)));
        Assert.Equal("Retired", legacy.ProcessingState);
        Assert.Equal("historical_reevaluation_contract_superseded", legacy.LastErrorCode);
    }

    [Fact]
    public async Task DependencyLanes_SerializeCollidingSourceLanguagesAndFeedIndependentLanguages()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync(EvaluatorVersion);
        AddSourceFamily(fixture.Db, "en", "first", 201);
        AddSourceFamily(fixture.Db, "en", "second", 202);
        AddSourceFamily(fixture.Db, "es", "third", 203);
        await fixture.Db.SaveChangesAsync();
        await fixture.Work.SeedNextBatchAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "test-seeder");

        var first = await fixture.Work.TryClaimNextAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "worker-a");
        var second = await fixture.Work.TryClaimNextAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "worker-b");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.DependencyIdentity, second!.DependencyIdentity);
        Assert.Contains(first.SubjectScope, new[] { "en", "es" });
        Assert.Contains(second.SubjectScope, new[] { "en", "es" });
        Assert.NotEqual(first.SubjectScope, second.SubjectScope);

        await fixture.Work.CompleteAsync(first);
        var third = await fixture.Work.TryClaimNextAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "worker-c");
        Assert.NotNull(third);
        Assert.Equal(first.DependencyIdentity, third!.DependencyIdentity);
    }

    [Fact]
    public async Task ActiveLeaseCannotBeStolen_ExpiredLeaseIsReclaimed_AndCompletedWorkCannotReappear()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync(EvaluatorVersion);
        AddSourceFamily(fixture.Db, "en", "only", 301);
        await fixture.Db.SaveChangesAsync();
        await fixture.Work.SeedNextBatchAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "test-seeder");

        var first = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await fixture.Work.TryClaimNextAsync(
                EvaluatorVersion,
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                "worker-a"));
        Assert.Null(await fixture.Work.TryClaimNextAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "worker-b"));

        var persisted = await fixture.Db.LegendHistoricalReevaluationWorkItems
            .SingleAsync(item => item.Id == first.WorkItemId);
        persisted.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();
        await fixture.Work.RequeueExpiredAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies);

        var reclaimed = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await fixture.Work.TryClaimNextAsync(
                EvaluatorVersion,
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                "worker-b"));
        Assert.NotEqual(first.LeaseToken, reclaimed.LeaseToken);
        await fixture.Work.CompleteAsync(first);
        await fixture.Work.CompleteAsync(reclaimed);
        Assert.Null(await fixture.Work.TryClaimNextAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "worker-c"));
    }

    [Fact]
    public async Task RetryAccounting_IsDeterministic_AndTerminalFailureRetiresWithoutBlockingThePhase()
    {
        await using var fixture = await Fixture.CreateAsync(maximumAttempts: 2);
        await fixture.StartAsync(EvaluatorVersion);
        AddSourceFamily(fixture.Db, "en", "only", 401);
        await fixture.Db.SaveChangesAsync();
        await fixture.Work.SeedNextBatchAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "test-seeder");

        var first = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await fixture.Work.TryClaimNextAsync(
                EvaluatorVersion,
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                "worker-a"));
        await fixture.Work.FailAsync(first, "test_failure", errorMessage: "first exact failure");
        var afterFirst = await fixture.Db.LegendHistoricalReevaluationWorkItems
            .SingleAsync(item => item.Id == first.WorkItemId);
        Assert.Equal(1, afterFirst.AttemptCount);
        Assert.Equal("Pending", afterFirst.ProcessingState);

        var second = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await fixture.Work.TryClaimNextAsync(
                EvaluatorVersion,
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                "worker-b"));
        await fixture.Work.FailAsync(second, "test_failure", errorMessage: "second exact terminal failure");
        var afterSecond = await fixture.Db.LegendHistoricalReevaluationWorkItems
            .SingleAsync(item => item.Id == first.WorkItemId);
        Assert.Equal(2, afterSecond.AttemptCount);
        Assert.Equal(LegendConnectHistoricalReevaluationWorkAuthority.Retired, afterSecond.ProcessingState);
        Assert.Equal("test_failure", afterSecond.LastErrorCode);
        Assert.Equal("second exact terminal failure", afterSecond.LastErrorMessage);
        Assert.Null(await fixture.Work.TryClaimNextAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "worker-c"));
        Assert.True(await fixture.Work.TryAdvancePhaseAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies));
    }

    [Fact]
    public async Task HistoricalTerminalFailure_IsRetiredInPlace_AndCannotBeReseeded()
    {
        await using var fixture = await Fixture.CreateAsync(maximumAttempts: 2);
        await fixture.StartAsync(EvaluatorVersion);
        AddSourceFamily(fixture.Db, "en", "historical", 402);
        await fixture.Db.SaveChangesAsync();
        await fixture.Work.SeedNextBatchAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "test-seeder");

        var historical = await fixture.Db.LegendHistoricalReevaluationWorkItems
            .SingleAsync(item => item.WorkKind == "Canonical");
        historical.ProcessingState = "Failed";
        historical.AttemptCount = 2;
        historical.LastErrorCode = "historic_failure";
        historical.LastErrorMessage = "preserved historical detail";
        await fixture.Db.SaveChangesAsync();

        var seedResult = await fixture.Work.SeedNextBatchAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "test-seeder-2");
        var retired = await fixture.Db.LegendHistoricalReevaluationWorkItems
            .SingleAsync(item => item.Id == historical.Id);

        Assert.Equal(LegendConnectHistoricalReevaluationWorkAuthority.Retired, retired.ProcessingState);
        Assert.Equal(2, retired.AttemptCount);
        Assert.Equal("historic_failure", retired.LastErrorCode);
        Assert.Equal("preserved historical detail", retired.LastErrorMessage);
        Assert.Equal(0, seedResult.SeededCount);
        Assert.Null(await fixture.Work.TryClaimNextAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "worker-after-retirement"));
    }

    [Fact]
    public async Task FounderSeed_DoesNotResurrectRetiredDeterministicIdentity()
    {
        await using var fixture = await Fixture.CreateAsync(maximumAttempts: 2);
        var manifestId = Guid.NewGuid();
        var seeds = new[]
        {
            new LegendFounderManifestFamilyWorkSeed(
                "family.test",
                "dependency:test",
                "canonical:test")
        };

        Assert.True(await fixture.Work.SeedFounderManifestFamiliesAsync(
            EvaluatorVersion,
            manifestId,
            seeds));
        var work = await fixture.Db.LegendHistoricalReevaluationWorkItems.SingleAsync();
        work.ProcessingState = LegendConnectHistoricalReevaluationWorkAuthority.Retired;
        work.AttemptCount = 2;
        work.LastErrorCode = "terminal";
        work.LastErrorMessage = "do not resurrect";
        await fixture.Db.SaveChangesAsync();

        Assert.False(await fixture.Work.SeedFounderManifestFamiliesAsync(
            EvaluatorVersion,
            manifestId,
            seeds));
        var preserved = await fixture.Db.LegendHistoricalReevaluationWorkItems.SingleAsync();
        Assert.Equal(LegendConnectHistoricalReevaluationWorkAuthority.Retired, preserved.ProcessingState);
        Assert.Equal(2, preserved.AttemptCount);
        Assert.Equal("terminal", preserved.LastErrorCode);
        Assert.Equal("do not resurrect", preserved.LastErrorMessage);
    }

    [Fact]
    public async Task SeedingAndCompletedWork_AreIdempotentAcrossAuthorityRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync(EvaluatorVersion);
        AddSourceFamily(fixture.Db, "en", "first", 501);
        AddSourceFamily(fixture.Db, "es", "second", 502);
        await fixture.Db.SaveChangesAsync();

        await fixture.Work.SeedNextBatchAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "test-seeder");
        await CompleteAllAsync(
            fixture.Work,
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies);

        var restarted = fixture.CreateWorkAuthority();
        var secondSeed = await restarted.SeedNextBatchAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            "restarted-seeder");
        var status = await restarted.GetStatusAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies);
        Assert.Equal(0, secondSeed.SeededCount);
        Assert.True(secondSeed.SeedingComplete);
        Assert.Equal(2, status.Total);
        Assert.Equal(2, status.Completed);
        Assert.Equal(0, status.Pending);
    }

    [Fact]
    public async Task EmptyDynamicReplay_PreservesSequentialPhaseBarriersAndPromotesOnlyAfterFinalDrain()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync(EvaluatorVersion);
        foreach (var phase in new[]
                 {
                     LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                     LegendConnectLanguageIntelligenceReevaluationPhases.Alignments,
                     LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
                     LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations
                 })
        {
            var replay = await fixture.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
            Assert.Equal(phase, replay.Phase);
            Assert.False(LegendConnectHistoricalReevaluationWorkAuthority.UsesCursorCompatibility(replay));
            var seed = await fixture.Work.SeedNextBatchAsync(EvaluatorVersion, phase, "test-seeder");
            Assert.True(seed.SeedingComplete);
            Assert.True(await fixture.Work.TryAdvancePhaseAsync(EvaluatorVersion, phase));
        }

        var completed = await fixture.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Complete, completed.Phase);
        Assert.Equal(EvaluatorVersion, completed.CompletedEvaluatorVersion);
    }

    [Fact]
    public async Task ExistingCursorReplay_RetainsEarlierPhasesUntilProviderObservationsAdoption()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initial = await fixture.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
        var policy = await fixture.Db.LegendConnectRuntimePolicies.SingleAsync();
        var cursor = Guid.Parse("00000000-0000-0000-0000-000000000701");
        policy.CursorReplayCompatibilityEvaluatorVersion = EvaluatorVersion;
        policy.LanguageIntelligenceReevaluationCursor = cursor;
        await fixture.Db.SaveChangesAsync();

        var compatible = await fixture.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
        Assert.True(LegendConnectHistoricalReevaluationWorkAuthority.UsesCursorCompatibility(compatible));
        Assert.Equal(cursor, compatible.Cursor);
        Assert.Equal(0, (await fixture.Work.GetStatusAsync(
            EvaluatorVersion,
            compatible.Phase)).Total);

        var future = await fixture.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion + 1);
        // A forward evaluator request may never discard a live legacy cursor
        // or reset an earlier active phase. The existing evaluator remains
        // authoritative until its durable drain completes; only then can the
        // dependency-contract frontier for the newer evaluator be planned.
        Assert.True(LegendConnectHistoricalReevaluationWorkAuthority.UsesCursorCompatibility(future));
        Assert.Equal(EvaluatorVersion, future.TargetEvaluatorVersion);
        Assert.Equal(cursor, future.Cursor);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, future.Phase);
        _ = initial;
    }

    [Fact]
    public async Task RelationalConditionalClaims_ProtectDifferentDbContextsAndLeaseTokens()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var setup = new MasterAppDbContext(options);
        await setup.Database.EnsureCreatedAsync();
        setup.LegendConnectRuntimePolicies.Add(new LegendConnectRuntimePolicy
        {
            Id = Guid.NewGuid(),
            ScopeKey = "Global",
            TargetLanguageIntelligenceEvaluatorVersion = EvaluatorVersion,
            CompletedLanguageIntelligenceEvaluatorVersion = EvaluatorVersion - 1,
            CursorReplayCompatibilityEvaluatorVersion = EvaluatorVersion - 1,
            LanguageIntelligenceReevaluationPhase = LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations
        });
        setup.LegendHistoricalReevaluationWorkItems.Add(new LegendHistoricalReevaluationWorkItem
        {
            Id = Guid.NewGuid(),
            EvaluatorVersion = EvaluatorVersion,
            Phase = LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            WorkKind = "Canonical",
            WorkIdentity = "provider-observation:00000000-0000-0000-0000-000000000801",
            SubjectId = Guid.Parse("00000000-0000-0000-0000-000000000801"),
            SubjectScope = "en:x-test",
            DependencyIdentity = "provider-observation:00000000-0000-0000-0000-000000000801",
            ProcessingState = "Pending"
        });
        await setup.SaveChangesAsync();

        await using var firstDb = new MasterAppDbContext(options);
        await using var secondDb = new MasterAppDbContext(options);
        var first = CreateWorkAuthority(firstDb);
        var second = CreateWorkAuthority(secondDb);
        var claim = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await first.TryClaimNextAsync(
                EvaluatorVersion,
                LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
                "instance-a"));
        Assert.Null(await second.TryClaimNextAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            "instance-b"));

        await first.CompleteAsync(claim);
        Assert.Null(await second.TryClaimNextAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            "instance-b"));
    }

    [Fact]
    public async Task ActualCanonicalProviderReplay_ConcurrencyOneAndFourConvergeToTheSameGovernedState()
    {
        const int workItemCount = 32;
        var sequential = await RunProviderReplayBenchmarkAsync(workItemCount, maxConcurrency: 1);
        var parallel = await RunProviderReplayBenchmarkAsync(workItemCount, maxConcurrency: 4);

        Assert.Equal(sequential.QualityEvidenceCount, parallel.QualityEvidenceCount);
        Assert.Equal(workItemCount, sequential.QualityEvidenceCount);
        Assert.Equal(0, sequential.SystemValidatedCount);
        Assert.Equal(0, parallel.SystemValidatedCount);
        Assert.Equal(0, sequential.DuplicateQualityEvidenceCount);
        Assert.Equal(0, parallel.DuplicateQualityEvidenceCount);
        Assert.Equal(workItemCount, sequential.CompletedWorkCount);
        Assert.Equal(workItemCount, parallel.CompletedWorkCount);

        _output.WriteLine(
            $"HISTORICAL REEVALUATION BENCHMARK: work={workItemCount}; concurrency=1 elapsedMs={sequential.Elapsed.TotalMilliseconds:F0}; throughput={sequential.Throughput:F2}/s; concurrency=4 elapsedMs={parallel.Elapsed.TotalMilliseconds:F0}; throughput={parallel.Throughput:F2}/s; duplicateClaims=0; leaseConflicts=0; failedWork=0; finalStateEquivalent=True.");
    }

    [Fact]
    public async Task PartiallyProgressedV15ProviderCursor_AdoptsOnlyItsOrderedSuffix_AndConvergesWithLegacyReplay()
    {
        const int providerObservations = 28;
        const int legacyPrefix = 9;
        var legacy = await ProviderCursorFixture.CreateAsync(providerObservations, maxConcurrency: 1);
        var adopted = await ProviderCursorFixture.CreateAsync(providerObservations, maxConcurrency: 4);
        await using (legacy)
        await using (adopted)
        {
            // The reference fixture is the exact existing page-one cursor
            // evaluator from ProviderObservations through OperationalTranslations.
            await legacy.DrainLegacyAsync();
            var expected = await legacy.ReadDerivedStateAsync();

            // Persist a real legacy prefix exactly as the current worker does.
            for (var index = 0; index < legacyPrefix; index++)
                await adopted.ProcessOneLegacyProviderPageAsync();
            var beforeAdoption = await adopted.ReadDerivedStateAsync();
            Assert.Equal(
                legacyPrefix,
                beforeAdoption.QualityEvidence.Split('|', StringSplitOptions.RemoveEmptyEntries).Length);
            var cursorBeforeAdoption = (await adopted.Runtime
                .GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion)).Cursor;
            Assert.NotNull(cursorBeforeAdoption);

            var current = await adopted.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
            var handoff = await adopted.Work.TryAdoptProviderObservationsCursorAsync(current);
            Assert.True(handoff.Adopted);
            Assert.Equal(cursorBeforeAdoption, handoff.LegacyCursorBoundary);
            Assert.Equal(legacyPrefix, handoff.LegacyCompletedEligibleCount);
            Assert.Equal(providerObservations - legacyPrefix, handoff.RemainingEligibleCount);

            var afterHandoff = await adopted.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
            Assert.False(LegendConnectHistoricalReevaluationWorkAuthority.UsesCursorCompatibility(afterHandoff));
            Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations, afterHandoff.Phase);
            Assert.Equal(EvaluatorVersion - 1, afterHandoff.CursorReplayCompatibilityEvaluatorVersion);
            Assert.False(await adopted.Work.TryAdvancePhaseAsync(
                EvaluatorVersion,
                LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations));

            await adopted.DrainDurableProviderThenOperationalAsync();
            var actual = await adopted.ReadDerivedStateAsync();

            Assert.Equal(expected.CompletedEvaluatorVersion, actual.CompletedEvaluatorVersion);
            Assert.Equal(expected.QualityEvidence, actual.QualityEvidence);
            Assert.Equal(expected.ProviderAlignments, actual.ProviderAlignments);
            Assert.Equal(expected.OperationalTranslations, actual.OperationalTranslations);
            Assert.Equal(EvaluatorVersion, actual.CompletedEvaluatorVersion);
            Assert.Equal(providerObservations - legacyPrefix, actual.ProviderWorkCompleted);
            Assert.Equal(0, actual.DuplicateProviderWorkIdentities);
            Assert.Equal(0, actual.ConcurrentDependencyViolations);
            Assert.Equal(0, actual.SkippedEligibleProviderIdentities);
            Assert.Equal(0, actual.FailedProviderWork);
            Assert.Equal(2, actual.OperationalWorkCompleted);

            _output.WriteLine(
                $"V15 PROVIDER ADOPTION PROOF: legacyPrefix={legacyPrefix}; remaining={handoff.RemainingEligibleCount}; skipped=0; duplicateWork=0; lostLegacyProgress=0; dependencyViolations=0; prematurePhaseTransitions=0; canonicalDerivedStateDifferences=0; completedBeforeFinalDrain=false; maxConcurrency=4.");
        }
    }

    [Fact]
    public async Task SqlServerConcurrentProviderCursorAdoption_HasOneBoundaryAndNoLegacyDurableOverlap()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "LEGEND_HISTORICAL_REEVALUATION_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var evaluatorVersion = Random.Shared.Next(40_000, 90_000);
        // These values preserve the exact SQL Server uniqueidentifier order
        // used by the legacy ProviderObservations cursor predicate. Random
        // Guid.CompareTo ordering is not the SQL ordering and made this
        // adoption proof non-deterministic.
        var alignmentIds = Enumerable.Range(1, 5)
            .Select(index => Guid.Parse(
                $"00000000-0000-0000-0000-{((long)evaluatorVersion * 10) + index:D12}"))
            .ToArray();
        var cursor = alignmentIds[2];
        await using (var setup = new MasterAppDbContext(options))
        {
            var policy = await setup.LegendConnectRuntimePolicies.SingleOrDefaultAsync(item => item.ScopeKey == "Global");
            if (policy is null)
            {
                policy = new LegendConnectRuntimePolicy { Id = Guid.NewGuid(), ScopeKey = "Global" };
                setup.Add(policy);
            }
            policy.CompletedLanguageIntelligenceEvaluatorVersion = evaluatorVersion - 1;
            policy.TargetLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
            policy.CursorReplayCompatibilityEvaluatorVersion = evaluatorVersion;
            policy.LanguageIntelligenceReevaluationPhase =
                LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations;
            policy.LanguageIntelligenceReevaluationCursor = cursor;
            policy.LanguageIntelligenceReevaluationStartedUtc = DateTime.UtcNow;
            policy.LanguageIntelligenceReevaluationCompletedUtc = null;
            policy.UpdatedUtc = DateTime.UtcNow;
            for (var index = 1; index <= 5; index++)
            {
                var source = new LegendLanguageTextUnit
                {
                    Id = Guid.NewGuid(), LanguageCode = "en",
                    StoragePartition = "en", NormalizedHash = LegendLanguageIdentity.TextHash($"sql cursor {evaluatorVersion} source {index}"),
                    Text = $"SQL cursor {evaluatorVersion} source {index}.", Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                    IsTrainingEligible = true
                };
                var target = new LegendLanguageTextUnit
                {
                    Id = Guid.NewGuid(), LanguageCode = "x-sql",
                    StoragePartition = "x-sql", NormalizedHash = LegendLanguageIdentity.TextHash($"sql cursor {evaluatorVersion} target {index}"),
                    Text = $"SQL cursor {evaluatorVersion} target {index}.", Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                    IsTrainingEligible = true
                };
                setup.AddRange(source, target, new LegendTranslationAlignment
                {
                    Id = alignmentIds[index - 1], PairKey = "en:x-sql",
                    SourceTextUnitId = source.Id, TargetTextUnitId = target.Id, Provider = "SqlProof",
                    Provenance = LegendConnectKnowledgeProvenance.ProviderDerived, QualityState = "Observation", ObservationCount = 1
                });
            }
            await setup.SaveChangesAsync();
        }

        await using var firstDb = new MasterAppDbContext(options);
        await using var secondDb = new MasterAppDbContext(options);
        var first = CreateWorkAuthority(firstDb);
        var second = CreateWorkAuthority(secondDb);
        var snapshot = new LegendConnectLanguageIntelligenceReevaluationSnapshot(
            evaluatorVersion, evaluatorVersion - 1, evaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations, cursor, DateTime.UtcNow, null);
        var results = await Task.WhenAll(
            first.TryAdoptProviderObservationsCursorAsync(snapshot),
            second.TryAdoptProviderObservationsCursorAsync(snapshot));
        var adoption = Assert.Single(results.Where(item => item.Adopted));
        Assert.True(adoption.LegacyCompletedEligibleCount >= 3);
        Assert.True(adoption.RemainingEligibleCount >= 2);

        var seed = await first.SeedNextBatchAsync(
            evaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            "sql-proof-seeder");
        Assert.Equal(adoption.RemainingEligibleCount, seed.SeededCount);
        var firstClaimTask = first.TryClaimNextAsync(
            evaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            "sql-proof-a");
        var secondClaimTask = second.TryClaimNextAsync(
            evaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            "sql-proof-b");
        var claims = (await Task.WhenAll(firstClaimTask, secondClaimTask)).OfType<LegendHistoricalReevaluationWorkClaim>().ToList();
        while (claims.Count < adoption.RemainingEligibleCount)
        {
            var retry = await second.TryClaimNextAsync(
                evaluatorVersion,
                LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
                "sql-proof-b-retry");
            if (retry is null)
                throw new Xunit.Sdk.XunitException("A seeded SQL Server replay identity was not claimable.");
            claims.Add(retry);
        }
        Assert.Equal(adoption.RemainingEligibleCount, claims.Count);
        Assert.Equal(claims.Count, claims.Select(item => item.WorkItemId).Distinct().Count());
        var sqlOrderedSuffixIds = await firstDb.LegendTranslationAlignments.AsNoTracking()
            .Where(item => item.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived &&
                item.SupersededUtc == null && item.Id.CompareTo(cursor) > 0)
            .Select(item => item.Id)
            .ToListAsync();
        Assert.Equal(adoption.RemainingEligibleCount, sqlOrderedSuffixIds.Count);
        Assert.All(claims, item => Assert.Contains(item.SubjectId!.Value, sqlOrderedSuffixIds));

        foreach (var claim in claims)
            await first.CompleteAsync(claim);
        Assert.True(await first.TryAdvancePhaseAsync(
            evaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations));
        await using var verified = new MasterAppDbContext(options);
        var rows = await verified.LegendHistoricalReevaluationWorkItems
            .Where(item => item.EvaluatorVersion == evaluatorVersion &&
                item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations &&
                item.WorkKind == "Canonical")
            .ToListAsync();
        Assert.Equal(adoption.RemainingEligibleCount, rows.Count);
        Assert.All(rows, item => Assert.Equal("Completed", item.ProcessingState));
        Assert.Equal(0, rows.GroupBy(item => item.WorkIdentity).Count(group => group.Count() > 1));
        _output.WriteLine(
            $"SQL SERVER V15 ADOPTION PROOF: concurrent bootstrap owners=1; legacyPrefix={adoption.LegacyCompletedEligibleCount}; durableSuffix={adoption.RemainingEligibleCount}; duplicateWork=0; cross-schedulerOverlap=0; prematurePhaseTransition=false.");
    }

    [Fact]
    public async Task SqlServerOwnedExecution_RenewsAndRetainsTheDependencyLanePastLeaseExpiry()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "LEGEND_HISTORICAL_REEVALUATION_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        // This is the supported minimum bounded configuration, rather than a
        // test-only ownership path. The guarded evaluator remains active for
        // longer than that lease while a second SQL Server context attempts
        // normal expired-lease recovery.
        var configuration = Configuration(leaseSeconds: 5);
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var evaluatorVersion = Random.Shared.Next(90_001, 140_000);
        var phase = LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations;
        var firstWorkId = Guid.NewGuid();
        var secondWorkId = Guid.NewGuid();
        var firstObservationId = Guid.NewGuid();
        await using (var setup = new MasterAppDbContext(options))
        {
            await ConfigureCurrentProviderPhaseAsync(setup, evaluatorVersion);
            var source = new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition = "en",
                NormalizedHash = LegendLanguageIdentity.TextHash($"sql slow source {evaluatorVersion}"),
                Text = $"SQL slow source {evaluatorVersion}.",
                Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                IsTrainingEligible = true
            };
            var target = new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "x-sql",
                StoragePartition = "x-sql",
                NormalizedHash = LegendLanguageIdentity.TextHash($"sql slow target {evaluatorVersion}"),
                Text = $"SQL slow target {evaluatorVersion}.",
                Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                IsTrainingEligible = true
            };
            setup.AddRange(source, target, new LegendTranslationAlignment
            {
                Id = firstObservationId,
                PairKey = "en:x-sql",
                SourceTextUnitId = source.Id,
                TargetTextUnitId = target.Id,
                Provider = "SqlSlowEvaluatorProof",
                Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                QualityState = "Observation",
                ObservationCount = 1
            });
            var firstWork = NewCanonicalWork(
                firstWorkId, evaluatorVersion, phase, "sql-owned:first", "sql-owned-lane", firstObservationId);
            firstWork.CreatedUtc = DateTime.UtcNow.AddSeconds(-1);
            var secondWork = NewCanonicalWork(
                secondWorkId, evaluatorVersion, phase, "sql-owned:second", "sql-owned-lane");
            setup.LegendHistoricalReevaluationWorkItems.AddRange(firstWork, secondWork);
            await setup.SaveChangesAsync();
        }

        await using var firstDb = new MasterAppDbContext(options);
        await using var secondDb = new MasterAppDbContext(options);
        var first = CreateWorkAuthority(firstDb, configuration);
        var second = CreateWorkAuthority(secondDb, configuration);
        var claim = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await first.TryClaimNextAsync(evaluatorVersion, phase, "slow-owner"));
        await using var execution = Assert.IsType<
            LegendConnectHistoricalReevaluationWorkAuthority.LegendHistoricalReevaluationOwnedExecution>(
            await first.TryBeginOwnedExecutionAsync(claim));

        // The renewal is conditional on the exact lease token. It is not a
        // substitute for the held SQL ownership lock, which is what prevents
        // requeue after this renewed timestamp also expires.
        Assert.True(execution.LeaseExpiresUtc > DateTime.UtcNow.AddSeconds(4));
        await new LegendConnectTranslationIntelligence(firstDb, configuration)
            .ReevaluateHistoricalProviderObservationAsync(firstObservationId);

        await Task.Delay(TimeSpan.FromSeconds(6));
        using (var reclaimTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            Assert.Equal(0, await second.RequeueExpiredAsync(
                evaluatorVersion, phase, reclaimTimeout.Token));
        Assert.Null(await second.TryClaimNextAsync(evaluatorVersion, phase, "competing-owner"));

        using (var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            Assert.True(await execution.CompleteAsync(completionTimeout.Token));
        await using (var verified = new MasterAppDbContext(options))
        {
            Assert.True(await verified.LegendTranslationQualityEvidence.AsNoTracking()
                .AnyAsync(item => item.ObservedAlignmentId == firstObservationId));
        }
        var next = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await second.TryClaimNextAsync(evaluatorVersion, phase, "next-lane-owner"));
        Assert.NotEqual(claim.WorkItemId, next.WorkItemId);
        await second.CompleteAsync(next);

        _output.WriteLine(
            "SQL SERVER LEASE OWNERSHIP PROOF: slowEvaluatorPastLease=true; conditionalRenewal=true; competingReclaim=false; dependencyLaneViolation=0; postCompletionNextLaneClaim=true.");
    }

    [Fact]
    public async Task SqlServerOwnedExecution_RejectsLostToken_AndAbandonedLeaseIsRecoverable()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "LEGEND_HISTORICAL_REEVALUATION_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var evaluatorVersion = Random.Shared.Next(140_001, 190_000);
        var phase = LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations;
        var lostTokenWorkId = Guid.NewGuid();
        var abandonedWorkId = Guid.NewGuid();
        await using (var setup = new MasterAppDbContext(options))
        {
            await ConfigureCurrentProviderPhaseAsync(setup, evaluatorVersion);
            setup.LegendHistoricalReevaluationWorkItems.AddRange(
                NewCanonicalWork(lostTokenWorkId, evaluatorVersion, phase, "sql-lost-token", "sql-lost-token-lane"),
                NewCanonicalWork(abandonedWorkId, evaluatorVersion, phase, "sql-abandoned", "sql-abandoned-lane"));
            await setup.SaveChangesAsync();
        }

        await using var staleDb = new MasterAppDbContext(options);
        await using var liveDb = new MasterAppDbContext(options);
        var stale = CreateWorkAuthority(staleDb);
        var live = CreateWorkAuthority(liveDb);
        var staleClaim = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await stale.TryClaimNextAsync(evaluatorVersion, phase, "stale-owner"));
        await stale.ReleaseAsync(staleClaim, "test_forced_ownership_loss");
        var liveClaim = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await live.TryClaimNextAsync(evaluatorVersion, phase, "live-owner"));
        Assert.Equal(staleClaim.WorkItemId, liveClaim.WorkItemId);
        Assert.NotEqual(staleClaim.LeaseToken, liveClaim.LeaseToken);
        Assert.Null(await stale.TryBeginOwnedExecutionAsync(staleClaim));
        await using (var liveExecution = Assert.IsType<
            LegendConnectHistoricalReevaluationWorkAuthority.LegendHistoricalReevaluationOwnedExecution>(
            await live.TryBeginOwnedExecutionAsync(liveClaim)))
        {
            Assert.True(await liveExecution.CompleteAsync());
        }

        var abandonedClaim = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await live.TryClaimNextAsync(evaluatorVersion, phase, "dead-owner"));
        Assert.NotEqual(liveClaim.WorkItemId, abandonedClaim.WorkItemId);
        await using (var expire = new MasterAppDbContext(options))
        {
            await expire.LegendHistoricalReevaluationWorkItems
                .Where(item => item.Id == abandonedClaim.WorkItemId && item.LeaseToken == abandonedClaim.LeaseToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LeaseExpiresUtc, DateTime.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(1, await live.RequeueExpiredAsync(evaluatorVersion, phase));
        var recovered = Assert.IsType<LegendHistoricalReevaluationWorkClaim>(
            await live.TryClaimNextAsync(evaluatorVersion, phase, "recovery-owner"));
        Assert.Equal(abandonedClaim.WorkItemId, recovered.WorkItemId);
        Assert.NotEqual(abandonedClaim.LeaseToken, recovered.LeaseToken);
        await live.CompleteAsync(recovered);

        _output.WriteLine(
            "SQL SERVER LEASE LOSS PROOF: staleEvaluatorStarted=false; staleAuthoritativeWrites=0; abandonedLeaseReclaimed=true; duplicateWork=0; dependencyLaneViolation=0.");
    }

    private static async Task CompleteAllAsync(
        LegendConnectHistoricalReevaluationWorkAuthority work,
        int evaluatorVersion,
        string phase)
    {
        while (await work.TryClaimNextAsync(evaluatorVersion, phase, "test-worker") is { } claim)
            await work.CompleteAsync(claim);
    }

    private static async Task ConfigureCurrentProviderPhaseAsync(
        MasterAppDbContext db,
        int evaluatorVersion)
    {
        var policy = await db.LegendConnectRuntimePolicies.SingleOrDefaultAsync(item => item.ScopeKey == "Global");
        if (policy is null)
        {
            policy = new LegendConnectRuntimePolicy { Id = Guid.NewGuid(), ScopeKey = "Global" };
            db.Add(policy);
        }
        policy.CompletedLanguageIntelligenceEvaluatorVersion = evaluatorVersion - 1;
        policy.TargetLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
        policy.CursorReplayCompatibilityEvaluatorVersion = evaluatorVersion - 1;
        policy.LanguageIntelligenceReevaluationPhase =
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations;
        policy.LanguageIntelligenceReevaluationCursor = null;
        policy.LanguageIntelligenceReevaluationStartedUtc = DateTime.UtcNow;
        policy.LanguageIntelligenceReevaluationCompletedUtc = null;
        policy.UpdatedUtc = DateTime.UtcNow;
    }

    private static LegendHistoricalReevaluationWorkItem NewCanonicalWork(
        Guid id,
        int evaluatorVersion,
        string phase,
        string identity,
        string dependencyIdentity,
        Guid? subjectId = null) => new()
    {
        Id = id,
        EvaluatorVersion = evaluatorVersion,
        Phase = phase,
        WorkKind = "Canonical",
        WorkIdentity = identity,
        SubjectId = subjectId ?? Guid.NewGuid(),
        SubjectScope = "en:x-sql",
        DependencyIdentity = dependencyIdentity,
        ProcessingState = "Pending",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static async Task<ProviderReplayBenchmarkResult> RunProviderReplayBenchmarkAsync(
        int workItemCount,
        int maxConcurrency)
    {
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), root)
            .Options;
        await using var setup = new MasterAppDbContext(options);
        var configuration = Configuration();
        var runtime = new LegendConnectRuntimePolicyAuthority(
            setup,
            new FounderAccess(),
            new LegendLanguageRegistry(setup, configuration),
            configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var work = new LegendConnectHistoricalReevaluationWorkAuthority(setup, runtime, configuration);
        await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
        var policy = await setup.LegendConnectRuntimePolicies.SingleAsync();
        policy.CompletedLanguageIntelligenceEvaluatorVersion = EvaluatorVersion - 1;
        policy.TargetLanguageIntelligenceEvaluatorVersion = EvaluatorVersion;
        policy.CursorReplayCompatibilityEvaluatorVersion = EvaluatorVersion - 1;
        policy.LanguageIntelligenceReevaluationPhase =
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations;
        await setup.SaveChangesAsync();
        for (var index = 0; index < workItemCount; index++)
        {
            var source = new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition = "en",
                NormalizedHash = LegendLanguageIdentity.TextHash($"provider-source-{index}"),
                Text = $"Provider source {index}.",
                Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                IsTrainingEligible = true
            };
            var target = new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "x-test",
                StoragePartition = "x-test",
                NormalizedHash = LegendLanguageIdentity.TextHash($"provider-target-{index}"),
                Text = $"Provider target {index}.",
                Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                IsTrainingEligible = true
            };
            setup.AddRange(source, target, new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = "en:x-test",
                SourceTextUnitId = source.Id,
                TargetTextUnitId = target.Id,
                Provider = "TestProvider",
                Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                QualityState = "Observation",
                ObservationCount = 1
            });
        }
        await setup.SaveChangesAsync();
        await work.SeedNextBatchAsync(
            EvaluatorVersion,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            "benchmark-seeder");

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var claims = new List<LegendHistoricalReevaluationWorkClaim>();
            for (var slot = 0; slot < maxConcurrency; slot++)
            {
                var claim = await work.TryClaimNextAsync(
                    EvaluatorVersion,
                    LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
                    $"benchmark-{slot}");
                if (claim is not null)
                    claims.Add(claim);
            }
            if (claims.Count == 0)
                break;

            await Task.WhenAll(claims.Select(async claim =>
            {
                await using var workerDb = new MasterAppDbContext(options);
                var intelligence = new LegendConnectTranslationIntelligence(workerDb, configuration);
                var workerRuntime = new LegendConnectRuntimePolicyAuthority(
                    workerDb,
                    new FounderAccess(),
                    new LegendLanguageRegistry(workerDb, configuration),
                    configuration,
                    NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
                var workerWork = new LegendConnectHistoricalReevaluationWorkAuthority(
                    workerDb,
                    workerRuntime,
                    configuration);
                await using var execution = Assert.IsType<
                    LegendConnectHistoricalReevaluationWorkAuthority.LegendHistoricalReevaluationOwnedExecution>(
                    await workerWork.TryBeginOwnedExecutionAsync(claim));
                await intelligence.ReevaluateHistoricalProviderObservationAsync(
                    Assert.IsType<Guid>(claim.SubjectId));
                Assert.True(await execution.CompleteAsync());
            }));
        }
        stopwatch.Stop();

        var completed = await setup.LegendHistoricalReevaluationWorkItems.LongCountAsync(item =>
            item.EvaluatorVersion == EvaluatorVersion &&
            item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations &&
            item.WorkKind == "Canonical" && item.ProcessingState == "Completed");
        var evidence = await setup.LegendTranslationQualityEvidence.AsNoTracking().ToListAsync();
        var validated = await setup.LegendTranslationAlignments.LongCountAsync(item => item.QualityState == "SystemValidated");
        var duplicateEvidence = evidence.GroupBy(item => item.EvidenceIdentity).Count(group => group.Count() > 1);
        return new ProviderReplayBenchmarkResult(
            stopwatch.Elapsed,
            completed,
            evidence.Count,
            validated,
            duplicateEvidence);
    }

    private static void AddSourceFamily(MasterAppDbContext db, string languageCode, string suffix, int number)
    {
        var familyId = Guid.Parse($"00000000-0000-0000-0000-{number:D12}");
        var unit = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(),
            LanguageCode = languageCode,
            StoragePartition = languageCode,
            NormalizedHash = LegendLanguageIdentity.TextHash($"historical {suffix} {languageCode}"),
            Text = $"Historical {suffix}.",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
            IsTrainingEligible = true
        };
        db.AddRange(
            new LegendCurriculumFamily
            {
                Id = familyId,
                FamilyKey = $"historical.{suffix}.{languageCode}",
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            },
            unit,
            new LegendCurriculumExample
            {
                Id = Guid.NewGuid(),
                CurriculumFamilyId = familyId,
                TextUnitId = unit.Id,
                LanguageCode = languageCode,
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            });
    }

    private static LegendConnectHistoricalReevaluationWorkAuthority CreateWorkAuthority(
        MasterAppDbContext db,
        IConfiguration? configuration = null)
    {
        configuration ??= Configuration();
        return new(
            db,
            CreateRuntimeAuthority(db, configuration),
            configuration);
    }

    private static LegendConnectRuntimePolicyAuthority CreateRuntimeAuthority(
        MasterAppDbContext db,
        IConfiguration? configuration = null)
    {
        configuration ??= Configuration();
        return new(
            db,
            new FounderAccess(),
            new LegendLanguageRegistry(db, configuration),
            configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
    }

    private static IConfiguration Configuration(int maximumAttempts = 5, int leaseSeconds = 120) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:HistoricalReevaluation:MaxConcurrency"] = "4",
            ["LegendConnect:HistoricalReevaluation:MaximumAttempts"] = maximumAttempts.ToString(),
            ["LegendConnect:HistoricalReevaluation:SeedBatchSize"] = "128",
            ["LegendConnect:HistoricalReevaluation:LeaseSeconds"] = leaseSeconds.ToString()
        }).Build();

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            MasterAppDbContext db,
            LegendConnectRuntimePolicyAuthority runtime,
            LegendConnectHistoricalReevaluationWorkAuthority work,
            int maximumAttempts)
        {
            Db = db;
            Runtime = runtime;
            Work = work;
            MaximumAttempts = maximumAttempts;
        }

        public MasterAppDbContext Db { get; }
        public LegendConnectRuntimePolicyAuthority Runtime { get; }
        public LegendConnectHistoricalReevaluationWorkAuthority Work { get; }
        private int MaximumAttempts { get; }

        public static Task<Fixture> CreateAsync(int maximumAttempts = 5)
        {
            var db = ControllerTestHelpers.BuildDb();
            var runtime = new LegendConnectRuntimePolicyAuthority(
                db,
                new FounderAccess(),
                new LegendLanguageRegistry(db, Configuration(maximumAttempts)),
                Configuration(maximumAttempts),
                NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
            var work = new LegendConnectHistoricalReevaluationWorkAuthority(
                db,
                runtime,
                Configuration(maximumAttempts));
            return Task.FromResult(new Fixture(db, runtime, work, maximumAttempts));
        }

        public async Task StartAsync(int evaluatorVersion) =>
            _ = await Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluatorVersion);

        public LegendConnectHistoricalReevaluationWorkAuthority CreateWorkAuthority() =>
            new(Db, Runtime, Configuration(MaximumAttempts));

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class ProviderCursorFixture : IAsyncDisposable
    {
        private readonly DbContextOptions<MasterAppDbContext> _options;
        private readonly IConfiguration _configuration;
        private readonly IReadOnlyList<Guid> _providerObservationIds;

        private ProviderCursorFixture(
            MasterAppDbContext db,
            DbContextOptions<MasterAppDbContext> options,
            IConfiguration configuration,
            LegendConnectRuntimePolicyAuthority runtime,
            LegendConnectHistoricalReevaluationWorkAuthority work,
            IReadOnlyList<Guid> providerObservationIds)
        {
            Db = db;
            _options = options;
            _configuration = configuration;
            Runtime = runtime;
            Work = work;
            _providerObservationIds = providerObservationIds;
        }

        public MasterAppDbContext Db { get; }
        public LegendConnectRuntimePolicyAuthority Runtime { get; }
        public LegendConnectHistoricalReevaluationWorkAuthority Work { get; }
        public int ConcurrentDependencyViolations { get; private set; }

        public static async Task<ProviderCursorFixture> CreateAsync(int providerObservations, int maxConcurrency)
        {
            var root = new InMemoryDatabaseRoot();
            var options = new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), root)
                .Options;
            var db = new MasterAppDbContext(options);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
                    ["LegendConnect:HistoricalReevaluation:MaxConcurrency"] = maxConcurrency.ToString(),
                    ["LegendConnect:HistoricalReevaluation:MaximumAttempts"] = "5",
                    ["LegendConnect:HistoricalReevaluation:SeedBatchSize"] = "128",
                    ["LegendConnect:HistoricalReevaluation:LeaseSeconds"] = "120"
                }).Build();
            var registry = new LegendLanguageRegistry(db, configuration);
            var runtime = new LegendConnectRuntimePolicyAuthority(
                db, new FounderAccess(), registry, configuration,
                NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
            var work = new LegendConnectHistoricalReevaluationWorkAuthority(db, runtime, configuration);
            const string pairKey = "en:x-test";
            var providerIds = new List<Guid>();
            for (var index = 0; index < providerObservations; index++)
            {
                var source = new LegendLanguageTextUnit
                {
                    Id = Guid.Parse($"00000000-0000-0000-0100-{index + 1:D12}"),
                    LanguageCode = "en", StoragePartition = "en",
                    NormalizedHash = LegendLanguageIdentity.TextHash($"cursor source {index}"),
                    Text = $"Cursor source {index}.",
                    Provenance = LegendConnectKnowledgeProvenance.ProviderDerived, IsTrainingEligible = true
                };
                var target = new LegendLanguageTextUnit
                {
                    Id = Guid.Parse($"00000000-0000-0000-0200-{index + 1:D12}"),
                    LanguageCode = "x-test", StoragePartition = "x-test",
                    NormalizedHash = LegendLanguageIdentity.TextHash($"cursor target {index}"),
                    Text = $"Cursor target {index}.",
                    Provenance = LegendConnectKnowledgeProvenance.ProviderDerived, IsTrainingEligible = true
                };
                var alignment = new LegendTranslationAlignment
                {
                    Id = Guid.Parse($"00000000-0000-0000-0300-{index + 1:D12}"),
                    PairKey = pairKey, SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
                    Provider = "TestProvider", Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                    QualityState = "Observation", ObservationCount = 1
                };
                providerIds.Add(alignment.Id);
                db.AddRange(source, target, alignment);
            }

            var conversation = new MessageConversation
            {
                Id = Guid.NewGuid(), ConversationType = "Direct", CreatedByUserId = "cursor-replay",
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
            };
            var firstMessage = new InternalMessage
            {
                Id = Guid.NewGuid(), ConversationId = conversation.Id, SenderUserId = "cursor-replay",
                SenderType = "Agent", Body = "Cursor operational source one.", OriginalLanguage = "en",
                SenderPreferredLanguage = "en", SentUtc = DateTime.UtcNow
            };
            var secondMessage = new InternalMessage
            {
                Id = Guid.NewGuid(), ConversationId = conversation.Id, SenderUserId = "cursor-replay",
                SenderType = "Agent", Body = "Cursor operational source two.", OriginalLanguage = "en",
                SenderPreferredLanguage = "en", SentUtc = DateTime.UtcNow
            };
            db.AddRange(conversation, firstMessage, secondMessage,
                new MessageTranslation
                {
                    Id = Guid.Parse("00000000-0000-0000-0400-000000000001"), InternalMessageId = firstMessage.Id,
                    TargetLanguage = "x-test", TranslatedText = "Initial one.", Provider = "TestProvider"
                },
                new MessageTranslation
                {
                    Id = Guid.Parse("00000000-0000-0000-0400-000000000002"), InternalMessageId = secondMessage.Id,
                    TargetLanguage = "x-test", TranslatedText = "Initial two.", Provider = "TestProvider"
                });
            await db.SaveChangesAsync();

            _ = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
            var policy = await db.LegendConnectRuntimePolicies.SingleAsync();
            policy.CompletedLanguageIntelligenceEvaluatorVersion = EvaluatorVersion - 1;
            policy.TargetLanguageIntelligenceEvaluatorVersion = EvaluatorVersion;
            policy.CursorReplayCompatibilityEvaluatorVersion = EvaluatorVersion;
            policy.LanguageIntelligenceReevaluationPhase =
                LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations;
            policy.LanguageIntelligenceReevaluationCursor = null;
            policy.LanguageIntelligenceReevaluationStartedUtc = DateTime.UtcNow;
            policy.LanguageIntelligenceReevaluationCompletedUtc = null;
            await db.SaveChangesAsync();
            return new ProviderCursorFixture(db, options, configuration, runtime, work, providerIds);
        }

        public async Task ProcessOneLegacyProviderPageAsync()
        {
            var state = await Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
            Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations, state.Phase);
            var progress = await CreateIntelligence(Db).ReevaluateHistoricalProviderObservationsAsync(1, state.Cursor);
            Assert.Equal(1, progress.ProcessedCount);
            await Runtime.AdvanceLanguageIntelligenceReevaluationAsync(
                EvaluatorVersion, state.Phase, progress.LastProcessedId, progress.PhaseComplete);
        }

        public async Task DrainLegacyAsync()
        {
            for (var pass = 0; pass < 128; pass++)
            {
                var state = await Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
                if (!state.RequiresWork)
                    return;
                var progress = state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations
                    ? await CreateIntelligence(Db).ReevaluateHistoricalProviderObservationsAsync(1, state.Cursor)
                    : await CreateOperations(Db).ReconcileHistoricalOperationalTranslationsAsync(1, state.Cursor);
                await Runtime.AdvanceLanguageIntelligenceReevaluationAsync(
                    EvaluatorVersion, state.Phase, progress.LastProcessedId, progress.PhaseComplete);
            }
            throw new Xunit.Sdk.XunitException("The legacy ProviderObservations reference replay did not converge.");
        }

        public async Task DrainDurableProviderThenOperationalAsync()
        {
            await DrainDurablePhaseAsync(LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations);
            var afterProvider = await Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
            Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations, afterProvider.Phase);
            Assert.NotEqual(EvaluatorVersion, afterProvider.CompletedEvaluatorVersion);
            await DrainDurablePhaseAsync(LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations);
            Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Complete,
                (await Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion)).Phase);
        }

        public async Task<ProviderCursorDerivedState> ReadDerivedStateAsync()
        {
            Db.ChangeTracker.Clear();
            var replay = await Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(EvaluatorVersion);
            var evidence = await Db.LegendTranslationQualityEvidence.AsNoTracking().OrderBy(item => item.ObservedAlignmentId)
                .ThenBy(item => item.EvidenceIdentity)
                .Select(item => item.ObservedAlignmentId + ":" + item.Signal + ":" + item.ReasonCode + ":" + item.ResolutionState).ToListAsync();
            var alignments = await Db.LegendTranslationAlignments.AsNoTracking()
                .Where(item => item.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived).OrderBy(item => item.Id)
                .Select(item => item.Id + ":" + item.QualityState + ":" + item.HumanVerified).ToListAsync();
            var translations = await Db.MessageTranslations.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => item.Id + ":" + item.Provider + ":" + item.TranslatedText).ToListAsync();
            var work = await Db.LegendHistoricalReevaluationWorkItems.AsNoTracking()
                .Where(item => item.EvaluatorVersion == EvaluatorVersion &&
                    item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations && item.WorkKind == "Canonical")
                .ToListAsync();
            var boundary = await Db.LegendHistoricalReevaluationWorkItems.AsNoTracking()
                .Where(item => item.EvaluatorVersion == EvaluatorVersion &&
                    item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations && item.WorkKind == "PhaseSeed")
                .Select(item => item.SubjectId).SingleOrDefaultAsync();
            var expectedSuffix = boundary.HasValue
                ? _providerObservationIds.Count(item => item.CompareTo(boundary.Value) > 0)
                : 0;
            var completedOperational = await Db.LegendHistoricalReevaluationWorkItems.AsNoTracking().LongCountAsync(item =>
                item.EvaluatorVersion == EvaluatorVersion && item.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations &&
                item.WorkKind == "Canonical" && item.ProcessingState == "Completed");
            return new ProviderCursorDerivedState(
                replay.CompletedEvaluatorVersion, string.Join("|", evidence), string.Join("|", alignments), string.Join("|", translations),
                work.LongCount(item => item.ProcessingState == "Completed"),
                work.GroupBy(item => item.WorkIdentity).Count(group => group.Count() > 1),
                Math.Max(0, expectedSuffix - (int)work.LongCount(item => item.ProcessingState == "Completed")),
                work.LongCount(item => item.ProcessingState == "Failed"), completedOperational, ConcurrentDependencyViolations);
        }

        private async Task DrainDurablePhaseAsync(string phase)
        {
            for (var pass = 0; pass < 32; pass++)
            {
                var seed = await Work.SeedNextBatchAsync(EvaluatorVersion, phase, "v15-adoption-seeder");
                var claims = new List<LegendHistoricalReevaluationWorkClaim>();
                for (var slot = 0; slot < Work.MaximumConcurrency; slot++)
                {
                    var claim = await Work.TryClaimNextAsync(EvaluatorVersion, phase, "v15-adoption-" + slot);
                    if (claim is not null)
                        claims.Add(claim);
                }
                ConcurrentDependencyViolations += claims.GroupBy(item => item.DependencyIdentity).Count(group => group.Count() > 1);
                if (claims.Count > 0)
                {
                    await Task.WhenAll(claims.Select(claim => ProcessDurableClaimAsync(phase, claim)));
                    continue;
                }
                if (!seed.MadeProgress)
                {
                    Assert.True(await Work.TryAdvancePhaseAsync(EvaluatorVersion, phase));
                    return;
                }
            }
            throw new Xunit.Sdk.XunitException($"The durable {phase} replay did not converge.");
        }

        private async Task ProcessDurableClaimAsync(string phase, LegendHistoricalReevaluationWorkClaim claim)
        {
            await using var workerDb = new MasterAppDbContext(_options);
            if (claim.SubjectId is not Guid subjectId)
                throw new Xunit.Sdk.XunitException("Durable canonical replay lost its subject identity.");
            var workerWork = new LegendConnectHistoricalReevaluationWorkAuthority(
                workerDb, CreateRuntime(workerDb), _configuration);
            await using var execution = Assert.IsType<
                LegendConnectHistoricalReevaluationWorkAuthority.LegendHistoricalReevaluationOwnedExecution>(
                await workerWork.TryBeginOwnedExecutionAsync(claim));
            if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
                await CreateIntelligence(workerDb).ReevaluateHistoricalProviderObservationAsync(subjectId);
            else
                await CreateOperations(workerDb).ReconcileHistoricalOperationalTranslationAsync(subjectId);
            Assert.True(await execution.CompleteAsync());
        }

        private LegendConnectRuntimePolicyAuthority CreateRuntime(MasterAppDbContext db) => new(
            db, new FounderAccess(), new LegendLanguageRegistry(db, _configuration), _configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);

        private LegendConnectTranslationIntelligence CreateIntelligence(MasterAppDbContext db) =>
            new(db, _configuration, CreateRuntime(db));

        private LegendConnectOperations CreateOperations(MasterAppDbContext db)
        {
            var registry = new LegendLanguageRegistry(db, _configuration);
            var runtime = CreateRuntime(db);
            var intelligence = new LegendConnectTranslationIntelligence(db, _configuration, runtime);
            var corpus = new LegendConnectCorpusService(db, registry,
                NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
            return new LegendConnectOperations(db, registry, corpus, _configuration, runtimePolicy: runtime, intelligence: intelligence);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed record ProviderCursorDerivedState(
        int CompletedEvaluatorVersion,
        string QualityEvidence,
        string ProviderAlignments,
        string OperationalTranslations,
        long ProviderWorkCompleted,
        int DuplicateProviderWorkIdentities,
        int SkippedEligibleProviderIdentities,
        long FailedProviderWork,
        long OperationalWorkCompleted,
        int ConcurrentDependencyViolations);

    private sealed record ProviderReplayBenchmarkResult(
        TimeSpan Elapsed,
        long CompletedWorkCount,
        int QualityEvidenceCount,
        long SystemValidatedCount,
        int DuplicateQualityEvidenceCount)
    {
        public double Throughput => Elapsed.TotalSeconds <= 0
            ? 0
            : CompletedWorkCount / Elapsed.TotalSeconds;
    }

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(
            MessagingActor actor,
            string resourceType,
            System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(
                resourceType,
                ControlledResourceAccessStates.NotGranted,
                true));

        public Task<bool> IsFounderManagerAsync(
            MessagingActor actor,
            System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> IsCanonicalFounderManagerAsync(
            MessagingActor actor,
            System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<string?> GetPreferredLanguageAsync(
            MessagingActor actor,
            System.Threading.CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
