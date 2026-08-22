using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

/// <summary>
/// Opt-in SQL Server release proof for the normal Founder manifest path. The
/// two connection strings must target separately-created, freshly migrated,
/// disposable local databases. Keeping database creation outside the test
/// prevents an accidental environment variable from ever creating or
/// destroying a production database.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendConnectFounderManifestParallelSqlTests
{
    private const string ConcurrencyOneConnection =
        "LEGEND_FOUNDER_MANIFEST_SQL_CONCURRENCY_1_CONNECTION";
    private const string ConcurrencyFourConnection =
        "LEGEND_FOUNDER_MANIFEST_SQL_CONCURRENCY_4_CONNECTION";
    private const string SharedIdentityConnection =
        "LEGEND_FOUNDER_MANIFEST_SQL_SHARED_IDENTITY_CONNECTION";
    private readonly ITestOutputHelper _output;

    public LegendConnectFounderManifestParallelSqlTests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public async Task FounderManifest_DurableWorkers_OneAndFourAreCanonicallyEquivalent()
    {
        var oneConnection = Environment.GetEnvironmentVariable(ConcurrencyOneConnection);
        var fourConnection = Environment.GetEnvironmentVariable(ConcurrencyFourConnection);
        if (string.IsNullOrWhiteSpace(oneConnection) || string.IsNullOrWhiteSpace(fourConnection))
        {
            _output.WriteLine(
                "Founder manifest parallel SQL proof was not selected; both isolated connection strings are required.");
            return;
        }

        // The normal Founder service verifies the same runtime identity
        // authority as the web action. Scope the test-only process value and
        // restore it so no other test can inherit this Founder identity.
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
            var one = await RunManifestAsync(oneConnection, 1, BuildManifest(), expectedFamilyCount: 8);
            var four = await RunManifestAsync(fourConnection, 4, BuildManifest(), expectedFamilyCount: 8);

            Assert.Equal(8, one.FamiliesCompleted);
            Assert.Equal(8, four.FamiliesCompleted);
            Assert.Equal(0, one.FailedWork);
            Assert.Equal(0, four.FailedWork);
            Assert.Equal(0, one.DuplicateAnchorIdentities);
            Assert.Equal(0, four.DuplicateAnchorIdentities);
            Assert.Equal(0, one.DuplicateWorkIdentities);
            Assert.Equal(0, four.DuplicateWorkIdentities);
            Assert.Equal(0, one.DuplicateStructuralRelationshipIdentities);
            Assert.Equal(0, four.DuplicateStructuralRelationshipIdentities);
            foreach (var error in one.Errors.Concat(four.Errors))
                _output.WriteLine("WORKER ERROR: " + error);
            Assert.Empty(one.Errors);
            Assert.Empty(four.Errors);
            Assert.True(four.MaximumObservedParallelism > 1,
                "The four-slot durable worker never had more than one independent family in canonical execution.");
            Assert.Equal(4, four.WorkerClaims.Count);
            Assert.All(four.WorkerClaims.Values, claimed => Assert.True(claimed > 0,
                "The durable scheduler did not refill every available independent worker slot."));
            Assert.Equal(one.CanonicalState, four.CanonicalState);

            var speedup = one.Elapsed.TotalMilliseconds / four.Elapsed.TotalMilliseconds;
            Assert.True(speedup > 1.10d,
                $"Four independently claimable workers did not improve throughput: {speedup:F2}x.");
            _output.WriteLine($"FOUNDER MANIFEST SQL 1 VS 4: one={one.Elapsed.TotalMilliseconds:F0}ms; four={four.Elapsed.TotalMilliseconds:F0}ms; speedup={speedup:F2}x.");
            _output.WriteLine($"FAMILIES: {one.FamiliesCompleted}/8 vs {four.FamiliesCompleted}/8.");
            _output.WriteLine($"PARALLEL EXECUTION: maxInFlight={four.MaximumObservedParallelism}; workerClaims=" +
                string.Join(',', four.WorkerClaims.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}")) + ".");
            _output.WriteLine("CANONICAL EQUIVALENCE: PASS; duplicate anchors=0; duplicate durable identities=0; failed work=0; deadlocks/unique-key errors=0.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    [Fact]
    public async Task FounderManifest_DurableWorkers_SerializesSharedStructuralRelationshipIdentity()
    {
        var connection = Environment.GetEnvironmentVariable(SharedIdentityConnection);
        if (string.IsNullOrWhiteSpace(connection))
        {
            _output.WriteLine("Founder manifest shared-identity SQL proof was not selected; its isolated connection string is required.");
            return;
        }

        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
            var shared = await RunManifestAsync(connection, 4, BuildSharedIdentityManifest(), expectedFamilyCount: 4);

            Assert.Equal(4, shared.FamiliesCompleted);
            Assert.Equal(0, shared.FailedWork);
            Assert.Equal(0, shared.DuplicateAnchorIdentities);
            Assert.Equal(0, shared.DuplicateWorkIdentities);
            Assert.Equal(0, shared.DuplicateStructuralRelationshipIdentities);
            Assert.Empty(shared.Errors);
            Assert.True(shared.MaximumObservedParallelism > 1);
            Assert.Equal(4, shared.WorkerClaims.Count);
            Assert.All(shared.WorkerClaims.Values, claimed => Assert.True(claimed > 0));
            _output.WriteLine($"SHARED STRUCTURAL IDENTITY: completed={shared.FamiliesCompleted}/4; maxInFlight={shared.MaximumObservedParallelism}; duplicate relationships=0; errors=0.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    private static async Task<ManifestRunResult> RunManifestAsync(
        string connectionString,
        int maxConcurrency,
        string manifestText,
        int expectedFamilyCount)
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using (var guard = new MasterAppDbContext(options))
        {
            Assert.Empty(await guard.LegendCurriculumFamilies.AsNoTracking().Take(1).ToListAsync());
            Assert.Empty(await guard.LegendCurriculumManifestWorkItems.AsNoTracking().Take(1).ToListAsync());
            Assert.Empty(await guard.LegendHistoricalReevaluationWorkItems.AsNoTracking().Take(1).ToListAsync());
        }

        var configuration = Configuration(maxConcurrency);
        await using (var db = new MasterAppDbContext(options))
        {
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = FounderId,
                AgentUpn = "parallel-founder@legend.local",
                NormalizedEmail = "parallel-founder@legend.local",
                IsActive = true
            });
            await db.SaveChangesAsync();

            var services = CreateServices(db, configuration);
            var founder = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", FounderId)], "parallel-proof"));
            var founderLegend = new FounderLegendConnectService(
                services.Operations,
                new AgentProfileAccessResolver(db));
            var accepted = await founderLegend.SubmitCurriculumAsync(
                founder,
                new FounderLegendConnectCurriculumInput { Manifest = manifestText });
            Assert.True(accepted.Succeeded, accepted.Message);
            Assert.False(accepted.DuplicatePrevented);

            var seeded = await new LegendConnectCurriculumManifestProcessor(
                    db,
                    services.Curriculum,
                    NullLogger<LegendConnectCurriculumManifestProcessor>.Instance)
                .SeedDurableFamilyWorkAsync(
                    services.Work,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                    16);
            Assert.Equal(1, seeded);
        }

        var inFlight = 0;
        var maximumObservedParallelism = 0;
        var errors = new ConcurrentQueue<string>();
        var workerClaims = new ConcurrentDictionary<int, int>();
        var clock = Stopwatch.StartNew();
        var slots = Enumerable.Range(0, maxConcurrency)
            .Select(slot => ProcessSlotAsync(
                options,
                configuration,
                slot,
                workerClaims,
                errors,
                () => Interlocked.Increment(ref inFlight),
                () => Interlocked.Decrement(ref inFlight),
                value => SetMaximum(ref maximumObservedParallelism, value)))
            .ToArray();
        await Task.WhenAll(slots);
        clock.Stop();

        await using var verification = new MasterAppDbContext(options);
        var verificationServices = CreateServices(verification, configuration);
        var processor = new LegendConnectCurriculumManifestProcessor(
            verification,
            verificationServices.Curriculum,
            NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
        // This is the normal bounded worker's post-drain receipt projection.
        // It uses fresh SQL state rather than a worker's concurrent snapshot.
        await processor.RefreshDurableManifestStatusesAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

        var manifest = Assert.Single(await verification.LegendCurriculumManifestWorkItems.ToListAsync());
        var work = await verification.LegendHistoricalReevaluationWorkItems
            .Where(item => item.EvaluatorVersion == LegendConnectLanguageIntelligenceEvaluatorVersion.Current &&
                item.Phase == LegendConnectHistoricalReevaluationWorkAuthority.FounderCurriculumPhase &&
                item.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestFamilyWorkKind)
            .ToListAsync();
        Assert.True(errors.IsEmpty,
            "Durable family worker errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        Assert.Equal("Completed", manifest.ProcessingState);
        Assert.Equal(LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            manifest.CompletedLanguageIntelligenceEvaluatorVersion);
        Assert.Equal(expectedFamilyCount, work.Count);
        Assert.All(work, item => Assert.Equal("Completed", item.ProcessingState));

        var canonical = new ManifestCanonicalState(
            await verification.LegendCurriculumFamilies.CountAsync(),
            await verification.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null),
            await verification.LegendLanguageTextUnits.CountAsync(item => item.IsTrainingEligible),
            await verification.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null),
            await verification.LegendLanguageStructuralEvidence.CountAsync(item => item.SupersededUtc == null),
            await verification.LegendLanguageLexicalRelationships.CountAsync(item => item.SupersededUtc == null) +
            await verification.LegendLanguageStructuralRelationships.CountAsync(item => item.SupersededUtc == null) +
            await verification.LegendLanguageContextRelationships.CountAsync(item => item.SupersededUtc == null),
            await verification.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null),
            await verification.LegendSemanticTransitionEvidence
                .Where(item => item.SupersededUtc == null && item.ContributionState == "Supported" && item.IsHumanVerifiedSupport)
                .Select(item => item.TransitionSignature)
                .Distinct()
                .CountAsync());
        Assert.True(canonical.SemanticTransitionEvidence > 0);
        Assert.True(canonical.EligibleTransitionSignatures > 0);

        return new ManifestRunResult(
            clock.Elapsed,
            manifest.NextFamilyIndex,
            work.Count(item => item.ProcessingState == "Failed"),
            await verification.LegendLanguageCompositionalAnchors
                .Where(item => item.SupersededUtc == null)
                .GroupBy(item => new { item.CurriculumExampleId, item.AnchorSignature })
                .CountAsync(group => group.Count() > 1),
            work.GroupBy(item => item.WorkIdentity).Count(group => group.Count() > 1),
            await verification.LegendLanguageStructuralRelationships
                .Where(item => item.SupersededUtc == null)
                .GroupBy(item => new
                {
                    item.PairKey,
                    item.LanguageCode,
                    item.VariationDimension,
                    item.RelationshipSignature
                })
                .CountAsync(group => group.Count() > 1),
            maximumObservedParallelism,
            workerClaims.ToDictionary(item => item.Key, item => item.Value),
            errors.ToArray(),
            canonical);
    }

    private static async Task ProcessSlotAsync(
        DbContextOptions<MasterAppDbContext> options,
        IConfiguration configuration,
        int slot,
        ConcurrentDictionary<int, int> workerClaims,
        ConcurrentQueue<string> errors,
        Func<int> enterExecution,
        Func<int> leaveExecution,
        Action<int> observeParallelism)
    {
        while (true)
        {
            await using var db = new MasterAppDbContext(options);
            var services = CreateServices(db, configuration);
            var claim = await services.Work.TryClaimNextFounderManifestFamilyAsync(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                $"sql-manifest-proof:{slot}");
            if (claim is null)
                return;

            workerClaims.AddOrUpdate(slot, 1, static (_, count) => count + 1);
            try
            {
                if (claim.SubjectId is not Guid manifestId ||
                    !int.TryParse(claim.SubjectScope, out var familyIndex))
                {
                    throw new InvalidOperationException("A Founder manifest claim did not retain its stable family identity.");
                }

                await using var execution = await services.Work.TryBeginOwnedExecutionAsync(claim);
                if (execution is null)
                    throw new InvalidOperationException("A claimed Founder manifest family lost durable ownership before evaluation.");

                var active = enterExecution();
                observeParallelism(active);
                try
                {
                    var processor = new LegendConnectCurriculumManifestProcessor(
                        db,
                        services.Curriculum,
                        NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
                    await processor.ProcessDurableFamilyAsync(manifestId, familyIndex);
                    Assert.True(await execution.CompleteAsync(),
                        "The durable owner lost its canonical completion token.");
                    await processor.RefreshDurableManifestStatusAsync(
                        manifestId,
                        LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
                }
                finally
                {
                    _ = leaveExecution();
                }
            }
            catch (Exception exception)
            {
                errors.Enqueue(exception.ToString());
                await services.Work.FailAsync(claim, "sql_manifest_parallel_proof_failure");
                return;
            }
        }
    }

    private static void SetMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref target);
            if (candidate <= observed || Interlocked.CompareExchange(ref target, candidate, observed) == observed)
                return;
        }
    }

    private static ManifestServices CreateServices(MasterAppDbContext db, IConfiguration configuration)
    {
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db,
            new FounderAccess(),
            registry,
            configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration, runtime);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance,
            intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        return new ManifestServices(
            new LegendConnectOperations(
                db,
                registry,
                corpus,
                configuration,
                runtimePolicy: runtime,
                curriculum: curriculum,
                intelligence: intelligence),
            curriculum,
            new LegendConnectHistoricalReevaluationWorkAuthority(db, runtime, configuration));
    }

    private static IConfiguration Configuration(int maxConcurrency) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = string.Empty,
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:HistoricalReevaluation:MaxConcurrency"] = maxConcurrency.ToString(),
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English"
        }).Build();

    private const string FounderId = "0e8a91bd-b94f-49b4-baf8-1afcf769f17b";

    private static string BuildManifest()
    {
        var lines = new List<string>();
        for (var family = 0; family < 8; family++)
        {
            lines.Add($"@family parallel.proof.family.{family:D2} | Independent governed conversation family {family:D2}");
            lines.Add("@ground conversation_function -> surface_phrase");
            for (var example = 0; example < 10; example++)
            {
                // Both surface and governed semantic values are intentionally
                // distinct across families. That makes this the required
                // independent-work benchmark rather than treating writers to
                // one shared semantic primitive as parallel-safe work.
                var text = $"Aster{family:D2}a{example:D2} Beryl{family:D2}b{example:D2} Cobalt{family:D2}c{example:D2}.";
                lines.Add(text + " | surface_phrase=" + text + "; conversation_function=greeting_" + family.ToString("D2") + "; discourse_role=opening_" + family.ToString("D2") + "; intent=start_conversation_" + family.ToString("D2") + "; register=neutral_" + family.ToString("D2") + "; domain_slot_" + family.ToString("D2") + "=independent_domain_" + family.ToString("D2"));
            }
            for (var example = 0; example < 10; example++)
            {
                var text = $"Dawn{family:D2}d{example:D2} Elm{family:D2}e{example:D2} Fable{family:D2}f{example:D2}.";
                lines.Add(text + " | surface_phrase=" + text + "; conversation_function=acknowledgement_" + family.ToString("D2") + "; discourse_role=response_" + family.ToString("D2") + "; intent=acknowledge_and_continue_" + family.ToString("D2") + "; register=neutral_" + family.ToString("D2") + "; domain_slot_" + family.ToString("D2") + "=independent_domain_" + family.ToString("D2"));
            }
            lines.Add("@transition");
            lines.Add("@source conversation_function=greeting_" + family.ToString("D2") + "; discourse_role=opening_" + family.ToString("D2") + "; intent=start_conversation_" + family.ToString("D2") + "; register=neutral_" + family.ToString("D2"));
            lines.Add("@result conversation_function=acknowledgement_" + family.ToString("D2") + "; discourse_role=response_" + family.ToString("D2") + "; intent=acknowledge_and_continue_" + family.ToString("D2") + "; register=neutral_" + family.ToString("D2"));
            lines.Add("@endtransition");
            lines.Add("@end");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSharedIdentityManifest()
    {
        var lines = new List<string>();
        for (var family = 0; family < 4; family++)
        {
            lines.Add($"@family shared.relationship.proof.{family:D2} | Shared reusable structural relationship contribution {family:D2}");
            lines.Add("@ground conversation_function -> surface_phrase");
            var greeting = $"Shared{family:D2} opening phrase.";
            var response = $"Shared{family:D2} response phrase.";
            lines.Add(greeting + " | surface_phrase=" + greeting + "; conversation_function=greeting; discourse_role=opening; intent=start_conversation; register=neutral");
            lines.Add(response + " | surface_phrase=" + response + "; conversation_function=acknowledgement; discourse_role=response; intent=acknowledge_and_continue; register=neutral");
            lines.Add("@transition");
            lines.Add("@source conversation_function=greeting; discourse_role=opening; intent=start_conversation; register=neutral");
            lines.Add("@result conversation_function=acknowledgement; discourse_role=response; intent=acknowledge_and_continue; register=neutral");
            lines.Add("@endtransition");
            lines.Add("@end");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(MessagingActor actor, string resourceType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, true));
        public Task<bool> IsFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed record ManifestServices(
        LegendConnectOperations Operations,
        LegendConnectCurriculumService Curriculum,
        LegendConnectHistoricalReevaluationWorkAuthority Work);

    private sealed record ManifestCanonicalState(
        int Families,
        int Examples,
        int TrainingEligibleTextUnits,
        int Anchors,
        int StructuralEvidence,
        int Relationships,
        int SemanticTransitionEvidence,
        int EligibleTransitionSignatures);

    private sealed record ManifestRunResult(
        TimeSpan Elapsed,
        int FamiliesCompleted,
        int FailedWork,
        int DuplicateAnchorIdentities,
        int DuplicateWorkIdentities,
        int DuplicateStructuralRelationshipIdentities,
        int MaximumObservedParallelism,
        IReadOnlyDictionary<int, int> WorkerClaims,
        IReadOnlyList<string> Errors,
        ManifestCanonicalState CanonicalState);
}
