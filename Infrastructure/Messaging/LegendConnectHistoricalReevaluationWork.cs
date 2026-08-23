using System.Data;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Messaging;

/// <summary>
/// The one durable execution-state authority for phase-local historical
/// reevaluation. It never interprets curriculum or evidence. Its only job is
/// to seed, lease, complete, retry, and observe bounded canonical work that
/// the established curriculum, intelligence, and operations authorities own.
/// </summary>
internal sealed class LegendConnectHistoricalReevaluationWorkAuthority
{
    private const string CanonicalWorkKind = "Canonical";
    // Founder manifests use the same durable lease/claim table as historical
    // reevaluation.  This is an execution kind, not a second curriculum or
    // evidence authority: the canonical curriculum service still evaluates
    // every family.
    internal const string FounderManifestFamilyWorkKind = "FounderManifestFamily";
    // WorkKind is deliberately constrained to nvarchar(24) by the durable
    // work schema.  This compact operational label identifies governed
    // cross-example relation projection without widening that established
    // schema or introducing a second queue.
    internal const string FounderManifestSemanticRelationWorkKind = "FounderManifestRelation";
    // A bounded, post-canonical ledger projection.  It is deliberately a
    // durable work kind in this same authority rather than an inline read
    // inside a family mutation transaction.  The projection owns no language
    // semantics; it records the dependencies of already committed canonical
    // evidence for the evaluator contract.
    internal const string DerivationLedgerWorkKind = "DerivationLedger";
    internal const string FounderCurriculumPhase = "FounderCurriculum";
    private const string SeedWorkKind = "PhaseSeed";
    private const string Pending = "Pending";
    private const string Processing = "Processing";
    private const string Completed = "Completed";
    private const string Failed = "Failed";
    private const int DefaultSeedBatchSize = 128;
    private const int DependencyInventoryFamiliesPerWorkItem = 32;
    private const int DefaultMaximumConcurrency = 4;
    private const int DefaultMaximumAttempts = 5;
    private const int MaximumClaimContentionRetries = 8;
    private const int MaximumExpiredLeaseReclaimsPerPass = 250;
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(10);

    private readonly MasterAppDbContext _db;
    private readonly ILegendConnectRuntimePolicyAuthority _runtime;
    private readonly IConfiguration _configuration;

    public LegendConnectHistoricalReevaluationWorkAuthority(
        MasterAppDbContext db,
        ILegendConnectRuntimePolicyAuthority runtime,
        IConfiguration configuration)
    {
        _db = db;
        _runtime = runtime;
        _configuration = configuration;
    }

    internal int MaximumConcurrency => Math.Clamp(
        _configuration.GetValue<int?>("LegendConnect:HistoricalReevaluation:MaxConcurrency") ??
            DefaultMaximumConcurrency,
        1,
        8);

    internal TimeSpan LeaseDuration => TimeSpan.FromSeconds(Math.Clamp(
        _configuration.GetValue<int?>("LegendConnect:HistoricalReevaluation:LeaseSeconds") ??
            (int)DefaultLeaseDuration.TotalSeconds,
        5,
        1_800));

    private int MaximumAttempts => Math.Clamp(
        _configuration.GetValue<int?>("LegendConnect:HistoricalReevaluation:MaximumAttempts") ??
            DefaultMaximumAttempts,
        1,
        10);

    private int SeedBatchSize => Math.Clamp(
        _configuration.GetValue<int?>("LegendConnect:HistoricalReevaluation:SeedBatchSize") ??
            DefaultSeedBatchSize,
        16,
        250);

    internal static bool UsesCursorCompatibility(
        LegendConnectLanguageIntelligenceReevaluationSnapshot replay) =>
        replay.TargetEvaluatorVersion <= replay.CursorReplayCompatibilityEvaluatorVersion;

    /// <summary>
    /// Atomically adopts a partially progressed legacy ProviderObservations
    /// cursor. The legacy worker orders the exact active provider-observation
    /// predicate by alignment identity, so its persisted cursor is a durable
    /// prefix boundary: identities at or before it remain legacy-completed and
    /// only identities after it are eligible for durable work.
    ///
    /// New binaries call this before selecting a replay executor. On SQL
    /// Server the singleton policy row is locked for the full bootstrap, so
    /// concurrent new App Service starts observe either cursor execution or a
    /// fully established durable boundary—never a mixed scheduler. A mixed
    /// old/new binary rollout cannot be fenced by code the old binary lacks;
    /// production activation must quiesce legacy binaries before enabling this
    /// migration.
    /// </summary>
    internal async Task<LegendHistoricalReevaluationCursorAdoptionResult>
        TryAdoptProviderObservationsCursorAsync(
            LegendConnectLanguageIntelligenceReevaluationSnapshot replay,
            CancellationToken cancellationToken = default)
    {
        if (!replay.RequiresWork ||
            replay.Phase != LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations ||
            !UsesCursorCompatibility(replay))
        {
            return LegendHistoricalReevaluationCursorAdoptionResult.NotApplicable;
        }

        if (_db.Database.IsRelational())
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            try
            {
                var adopted = await TryAdoptProviderObservationsCursorCoreAsync(
                    replay.TargetEvaluatorVersion,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return adopted;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        return await TryAdoptProviderObservationsCursorCoreAsync(
            replay.TargetEvaluatorVersion,
            cancellationToken);
    }

    /// <summary>
    /// Reclaims expired durable leases before any new claim. An expired owner
    /// can no longer complete its old lease token, so recovery is safe across
    /// process recycle and App Service instances.
    /// </summary>
    internal async Task<int> RequeueExpiredAsync(
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = WorkFor(evaluatorVersion, phase)
            .Where(item => item.ProcessingState == Processing &&
                item.LeaseExpiresUtc != null && item.LeaseExpiresUtc <= now);

        if (_db.Database.IsSqlServer())
        {
            // Never wait behind a live evaluator's ownership lock. NOWAIT
            // makes a contended bounded scan empty for this tick; the next
            // normal worker tick retries. The conditional update remains the
            // database authority if another worker changes a selected row.
            // This avoids a broad set-based UPDATE obstructing an owner from
            // completing canonical writes under any SQL isolation setting.
            List<Guid> candidateIds;
            try
            {
                candidateIds = await _db.Set<LegendHistoricalReevaluationWorkItem>()
                    .FromSqlInterpolated($"""
                        SELECT TOP ({MaximumExpiredLeaseReclaimsPerPass}) *
                        FROM [LegendHistoricalReevaluationWorkItems] WITH (UPDLOCK, ROWLOCK, NOWAIT)
                        WHERE [EvaluatorVersion] = {evaluatorVersion}
                          AND [Phase] = {phase}
                          AND [ProcessingState] = {Processing}
                          AND [LeaseExpiresUtc] IS NOT NULL
                          AND [LeaseExpiresUtc] <= {now}
                        ORDER BY [LeaseExpiresUtc], [Id]
                        """)
                    .AsNoTracking()
                    .Select(item => item.Id)
                    .ToListAsync(cancellationToken);
            }
            catch (SqlException exception) when (exception.Number == 1222)
            {
                // A live owner may hold an index/page lock even when the
                // optimiser cannot READPAST it as a row. Treat that bounded
                // scan as empty; the next normal worker tick retries rather
                // than ever obstructing an authoritative owner.
                return 0;
            }
            var reclaimed = 0;
            foreach (var candidateId in candidateIds)
            {
                var updated = await WorkFor(evaluatorVersion, phase)
                    .Where(item => item.Id == candidateId && item.ProcessingState == Processing &&
                        item.LeaseExpiresUtc != null && item.LeaseExpiresUtc <= now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.ProcessingState, Pending)
                        .SetProperty(item => item.LeaseOwner, (string?)null)
                        .SetProperty(item => item.LeaseToken, (Guid?)null)
                        .SetProperty(item => item.LeaseExpiresUtc, (DateTime?)null)
                        .SetProperty(item => item.LastErrorCode, "historical_reevaluation_lease_expired")
                        .SetProperty(item => item.LastErrorMessage,
                            "The bounded replay lease expired and is available for governed retry.")
                        .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
                reclaimed += updated;
            }
            return reclaimed;
        }

        if (_db.Database.IsRelational())
        {
            return await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingState, Pending)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(item => item.LastErrorCode, "historical_reevaluation_lease_expired")
                .SetProperty(item => item.LastErrorMessage,
                    "The bounded replay lease expired and is available for governed retry.")
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
        }

        var items = await query.ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            item.ProcessingState = Pending;
            item.LeaseOwner = null;
            item.LeaseToken = null;
            item.LeaseExpiresUtc = null;
            item.LastErrorCode = "historical_reevaluation_lease_expired";
            item.LastErrorMessage = "The bounded replay lease expired and is available for governed retry.";
            item.UpdatedUtc = now;
        }
        if (items.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);
        return items.Count;
    }

    /// <summary>
    /// Seeds at most one bounded page of execution state for the currently
    /// active phase. The phase seed itself is leased through this same table,
    /// so concurrent app instances cannot manufacture duplicate work rows.
    /// </summary>
    internal async Task<LegendHistoricalReevaluationSeedResult> SeedNextBatchAsync(
        int evaluatorVersion,
        string phase,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsCurrentPhaseAsync(evaluatorVersion, phase, cancellationToken))
            return LegendHistoricalReevaluationSeedResult.NotApplicable;

        await RequeueExpiredAsync(evaluatorVersion, phase, cancellationToken);
        var seed = await EnsureSeedWorkAsync(evaluatorVersion, phase, cancellationToken);
        if (seed is null || seed.ProcessingState == Failed)
            return LegendHistoricalReevaluationSeedResult.NotApplicable;

        if (seed.ProcessingState == Completed &&
            !await HasUnseededEligibleWorkAsync(evaluatorVersion, phase, cancellationToken))
        {
            return new LegendHistoricalReevaluationSeedResult(0, true, false);
        }

        if (seed.ProcessingState == Completed)
        {
            await ReopenSeedAsync(seed.Id, evaluatorVersion, phase, cancellationToken);
        }

        var claim = await TryClaimByIdAsync(
            seed.Id,
            evaluatorVersion,
            phase,
            workerId,
            SeedWorkKind,
            cancellationToken);
        if (claim is null)
            return LegendHistoricalReevaluationSeedResult.NotApplicable;

        try
        {
            var candidates = await SelectUnseededEligibleWorkAsync(
                evaluatorVersion,
                phase,
                SeedBatchSize + 1,
                cancellationToken);
            var hasMore = candidates.Count > SeedBatchSize;
            foreach (var candidate in candidates.Take(SeedBatchSize))
            {
                _db.Set<LegendHistoricalReevaluationWorkItem>().Add(new LegendHistoricalReevaluationWorkItem
                {
                    Id = Guid.NewGuid(),
                    EvaluatorVersion = evaluatorVersion,
                    Phase = phase,
                    WorkKind = CanonicalWorkKind,
                    WorkIdentity = candidate.WorkIdentity,
                    SubjectId = candidate.SubjectId,
                    SubjectScope = candidate.SubjectScope,
                    DependencyIdentity = candidate.DependencyIdentity,
                    CanonicalMutationLane = candidate.CanonicalMutationLane,
                    ProcessingState = Pending,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
            }
            if (candidates.Count > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
                // This is observability only. The immutable work identities
                // above remain the sole scheduler authority; recording their
                // bounded count lets Founder inspection distinguish a true
                // dependency delta from a broad historical replay.
                await RecordSeededConvergenceWorkAsync(
                    evaluatorVersion,
                    phase,
                    candidates.Count,
                    cancellationToken);
            }

            if (hasMore)
            {
                await ReleaseSeedForNextBatchAsync(claim, cancellationToken);
                return new LegendHistoricalReevaluationSeedResult(SeedBatchSize, false, true);
            }

            await CompleteAsync(claim, cancellationToken);
            return new LegendHistoricalReevaluationSeedResult(candidates.Count, true, candidates.Count > 0);
        }
        catch (OperationCanceledException)
        {
            await ReleaseAsync(claim, "historical_reevaluation_seed_cancelled", CancellationToken.None);
            throw;
        }
        catch (DbUpdateException)
        {
            // The unique execution identity is the database authority. A
            // concurrent seed that won the race has already created the same
            // durable work; retry through the normal bounded lifecycle.
            _db.ChangeTracker.Clear();
            await ReleaseAsync(claim, "historical_reevaluation_seed_raced", cancellationToken);
            return LegendHistoricalReevaluationSeedResult.NotApplicable;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await FailAsync(claim, FailureCode(exception), cancellationToken);
            return LegendHistoricalReevaluationSeedResult.NotApplicable;
        }
    }

    internal async Task<LegendHistoricalReevaluationWorkClaim?> TryClaimNextAsync(
        int evaluatorVersion,
        string phase,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsCurrentPhaseAsync(evaluatorVersion, phase, cancellationToken))
            return null;

        await RequeueExpiredAsync(evaluatorVersion, phase, cancellationToken);
        // A conditional claim can legitimately lose a race after the
        // candidate read. Continue scanning bounded eligible identities so
        // independent worker slots refill instead of all returning after
        // competing for the first row.
        for (var attempt = 0; attempt < MaximumClaimContentionRetries; attempt++)
        {
            var candidate = await SelectNextClaimableWorkAsync(
                evaluatorVersion,
                phase,
                cancellationToken);
            if (candidate is null)
                return null;

            var claim = await TryClaimByIdAsync(
                candidate.WorkItemId,
                evaluatorVersion,
                phase,
                workerId,
                candidate.WorkKind,
                cancellationToken);
            if (claim is not null)
                return claim;
        }
        return null;
    }

    /// <summary>
    /// Seeds the existing durable work authority with independently executable
    /// Founder-manifest families. Work identity is deterministic per retained
    /// manifest and family index. The caller supplies the bounded, declared
    /// canonical write lane: ordinary independent families retain their own
    /// lane while a manifest-local controlled semantic collision shares one
    /// lane before any canonical mutation begins.
    /// </summary>
    internal async Task<bool> SeedFounderManifestFamiliesAsync(
        int evaluatorVersion,
        Guid manifestId,
        IReadOnlyList<LegendFounderManifestFamilyWorkSeed> families,
        CancellationToken cancellationToken = default)
    {
        var existing = await WorkFor(evaluatorVersion, FounderCurriculumPhase)
            .Where(item => item.WorkKind == FounderManifestFamilyWorkKind && item.SubjectId == manifestId)
            .ToDictionaryAsync(item => item.SubjectScope, cancellationToken);
        var now = DateTime.UtcNow;
        var changed = false;
        for (var index = 0; index < families.Count; index++)
        {
            var scope = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var family = families[index];
            if (existing.TryGetValue(scope, out var prior))
            {
                // Explicit Founder resubmission is the only supported way to
                // retry a terminal manifest. Preserve canonical rows and
                // reopen only its durable execution identity.
                if (prior.ProcessingState == Failed)
                {
                    prior.ProcessingState = Pending;
                    prior.LeaseOwner = null;
                    prior.LeaseToken = null;
                    prior.LeaseExpiresUtc = null;
                    prior.AttemptCount = 0;
                    prior.LastErrorCode = null;
                    prior.LastErrorMessage = null;
                    prior.UpdatedUtc = now;
                    changed = true;
                }
                continue;
            }

            _db.Set<LegendHistoricalReevaluationWorkItem>().Add(new LegendHistoricalReevaluationWorkItem
            {
                Id = Guid.NewGuid(),
                EvaluatorVersion = evaluatorVersion,
                Phase = FounderCurriculumPhase,
                WorkKind = FounderManifestFamilyWorkKind,
                WorkIdentity = $"founder-manifest:{manifestId:D}:family:{index}",
                SubjectId = manifestId,
                SubjectScope = scope,
                DependencyIdentity = family.DependencyIdentity,
                CanonicalMutationLane = family.CanonicalMutationLane,
                ProcessingState = Pending,
                CreatedUtc = now,
                UpdatedUtc = now
            });
            changed = true;
        }
        if (changed)
            await _db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// Adds Founder-declared cross-example semantic relation projections to
    /// the existing durable work authority. Each item is claimable only after
    /// the manifest's family work has drained, so a relationship can never
    /// race an endpoint meaning-graph write.
    /// </summary>
    internal async Task<bool> SeedFounderManifestSemanticRelationsAsync(
        int evaluatorVersion,
        Guid manifestId,
        int relationshipCount,
        CancellationToken cancellationToken = default)
    {
        if (relationshipCount <= 0)
            return false;

        var existing = await WorkFor(evaluatorVersion, FounderCurriculumPhase)
            .Where(item => item.WorkKind == FounderManifestSemanticRelationWorkKind &&
                item.SubjectId == manifestId)
            .ToDictionaryAsync(item => item.SubjectScope, cancellationToken);
        var now = DateTime.UtcNow;
        var changed = false;
        for (var index = 0; index < relationshipCount; index++)
        {
            var scope = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (existing.TryGetValue(scope, out var prior))
            {
                if (prior.ProcessingState == Failed)
                {
                    prior.ProcessingState = Pending;
                    prior.LeaseOwner = null;
                    prior.LeaseToken = null;
                    prior.LeaseExpiresUtc = null;
                    prior.AttemptCount = 0;
                    prior.LastErrorCode = null;
                    prior.LastErrorMessage = null;
                    prior.UpdatedUtc = now;
                    changed = true;
                }
                continue;
            }

            _db.Set<LegendHistoricalReevaluationWorkItem>().Add(new LegendHistoricalReevaluationWorkItem
            {
                Id = Guid.NewGuid(),
                EvaluatorVersion = evaluatorVersion,
                Phase = FounderCurriculumPhase,
                WorkKind = FounderManifestSemanticRelationWorkKind,
                WorkIdentity = $"founder-manifest:{manifestId:D}:semantic-relation:{index}",
                SubjectId = manifestId,
                SubjectScope = scope,
                // One manifest-level lane preserves a deterministic evidence
                // reconciliation boundary while unrelated manifests retain
                // the normal bounded parallelism of this same authority.
                DependencyIdentity = $"founder-manifest-semantic-relations:{manifestId:D}",
                ProcessingState = Pending,
                CreatedUtc = now,
                UpdatedUtc = now
            });
            changed = true;
        }
        if (changed)
            await _db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// Seeds one post-canonical derivation-ledger identity per retained Founder
    /// family.  These rows are intentionally created before processing starts
    /// but cannot be claimed until every canonical family and declared
    /// cross-example relationship in the same manifest is committed.  This
    /// makes dependency inventory a downstream durable step, never a read
    /// interleaved with a sibling family's canonical mutation transaction.
    /// </summary>
    internal async Task<bool> SeedFounderManifestDerivationLedgersAsync(
        int evaluatorVersion,
        Guid manifestId,
        IReadOnlyList<LegendFounderManifestFamilyWorkSeed> families,
        CancellationToken cancellationToken = default)
    {
        var existing = await WorkFor(evaluatorVersion, FounderCurriculumPhase)
            .Where(item => item.WorkKind == DerivationLedgerWorkKind && item.SubjectId == manifestId)
            .ToDictionaryAsync(item => item.SubjectScope, cancellationToken);
        var now = DateTime.UtcNow;
        var changed = false;
        for (var index = 0; index < families.Count; index++)
        {
            var scope = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (existing.TryGetValue(scope, out var prior))
            {
                if (prior.ProcessingState == Failed)
                {
                    prior.ProcessingState = Pending;
                    prior.LeaseOwner = null;
                    prior.LeaseToken = null;
                    prior.LeaseExpiresUtc = null;
                    prior.AttemptCount = 0;
                    prior.LastErrorCode = null;
                    prior.LastErrorMessage = null;
                    prior.UpdatedUtc = now;
                    changed = true;
                }
                continue;
            }

            var family = families[index];
            _db.Set<LegendHistoricalReevaluationWorkItem>().Add(new LegendHistoricalReevaluationWorkItem
            {
                Id = Guid.NewGuid(),
                EvaluatorVersion = evaluatorVersion,
                Phase = FounderCurriculumPhase,
                WorkKind = DerivationLedgerWorkKind,
                WorkIdentity = $"founder-manifest:{manifestId:D}:derivation-ledger:{index}",
                SubjectId = manifestId,
                SubjectScope = scope,
                DependencyIdentity = $"founder-manifest-ledger:{manifestId:D}:family:{index}",
                CanonicalMutationLane = family.CanonicalMutationLane,
                ProcessingState = Pending,
                CreatedUtc = now,
                UpdatedUtc = now
            });
            changed = true;
        }
        if (changed)
            await _db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// Appends the post-canonical ledger work for a SourceFamilies evaluator
    /// item while its canonical family owner still holds the durable execution
    /// transaction.  The child has the exact same canonical-family lane and
    /// cannot execute until every source-family canonical item in this phase
    /// has committed.  Thus normal intake, replay, and convergence all use
    /// one ownership model: mutate the family first, then project its compact
    /// dependency ledger through separately leased durable work.
    /// </summary>
    internal async Task EnqueueFamilyDerivationLedgerAsync(
        LegendHistoricalReevaluationWorkClaim parent,
        CancellationToken cancellationToken = default)
    {
        if (!LegendConnectDerivationContracts.ForEvaluator(parent.EvaluatorVersion)
                .Any(item => item.RequiresDependencyInventory) ||
            parent.WorkKind != CanonicalWorkKind ||
            parent.SubjectId is not Guid familyId ||
            !string.Equals(parent.Phase,
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                StringComparison.Ordinal))
        {
            return;
        }

        var parentItem = await WorkFor(parent.EvaluatorVersion, parent.Phase)
            .AsNoTracking()
            .SingleAsync(item => item.Id == parent.WorkItemId &&
                item.ProcessingState == Processing && item.LeaseToken == parent.LeaseToken,
                cancellationToken);
        var workIdentity = $"derivation-ledger:{parentItem.WorkIdentity}";
        var exists = await WorkFor(parent.EvaluatorVersion, parent.Phase)
            .AnyAsync(item => item.WorkIdentity == workIdentity, cancellationToken);
        if (exists)
            return;

        var now = DateTime.UtcNow;
        _db.Set<LegendHistoricalReevaluationWorkItem>().Add(new LegendHistoricalReevaluationWorkItem
        {
            Id = Guid.NewGuid(),
            EvaluatorVersion = parent.EvaluatorVersion,
            Phase = parent.Phase,
            WorkKind = DerivationLedgerWorkKind,
            WorkIdentity = workIdentity,
            SubjectId = familyId,
            SubjectScope = parent.SubjectScope,
            DependencyIdentity = $"derivation-ledger:family:{familyId:D}:{parent.SubjectScope}",
            CanonicalMutationLane = parentItem.CanonicalMutationLane,
            ProcessingState = Pending,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    internal async Task<LegendHistoricalReevaluationWorkClaim?> TryClaimNextFounderManifestWorkAsync(
        int evaluatorVersion,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        await RequeueExpiredAsync(evaluatorVersion, FounderCurriculumPhase, cancellationToken);
        for (var attempt = 0; attempt < MaximumClaimContentionRetries; attempt++)
        {
            var candidate = await WorkFor(evaluatorVersion, FounderCurriculumPhase)
                .AsNoTracking()
                .Where(item => (item.WorkKind == FounderManifestFamilyWorkKind ||
                                 item.WorkKind == FounderManifestSemanticRelationWorkKind ||
                                 item.WorkKind == DerivationLedgerWorkKind) &&
                    item.ProcessingState == Pending &&
                    !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(active =>
                        active.Id != item.Id && active.EvaluatorVersion == evaluatorVersion &&
                        active.Phase == FounderCurriculumPhase && active.ProcessingState == Processing &&
                        active.DependencyIdentity == item.DependencyIdentity) &&
                    (item.CanonicalMutationLane == null ||
                     !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(active =>
                         active.Id != item.Id &&
                         active.ProcessingState == Processing &&
                         active.CanonicalMutationLane == item.CanonicalMutationLane)) &&
                    (item.WorkKind != FounderManifestSemanticRelationWorkKind ||
                     !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(family =>
                         family.EvaluatorVersion == evaluatorVersion &&
                         family.Phase == FounderCurriculumPhase &&
                         family.WorkKind == FounderManifestFamilyWorkKind &&
                         family.SubjectId == item.SubjectId &&
                         family.ProcessingState != Completed)) &&
                    // Ledger projection is a manifest-local downstream
                    // boundary: it starts only after all canonical family and
                    // relationship work for this retained submission commits.
                    // This preserves independent family parallelism while
                    // forbidding a dependency read from interleaving with a
                    // sibling canonical write in the same manifest.
                    (item.WorkKind != DerivationLedgerWorkKind ||
                     !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(prerequisite =>
                         prerequisite.EvaluatorVersion == evaluatorVersion &&
                         prerequisite.Phase == FounderCurriculumPhase &&
                         prerequisite.SubjectId == item.SubjectId &&
                         (prerequisite.WorkKind == FounderManifestFamilyWorkKind ||
                          prerequisite.WorkKind == FounderManifestSemanticRelationWorkKind) &&
                         prerequisite.ProcessingState != Completed)))
                .OrderBy(item => item.CreatedUtc)
                .ThenBy(item => item.Id)
                .Select(item => new { item.Id, item.WorkKind })
                .FirstOrDefaultAsync(cancellationToken);
            if (candidate is null)
                return null;

            var claim = await TryClaimByIdAsync(
                candidate.Id,
                evaluatorVersion,
                FounderCurriculumPhase,
                workerId,
                candidate.WorkKind,
                cancellationToken);
            if (claim is not null)
                return claim;
        }
        return null;
    }

    /// <summary>
    /// Establishes database-owned execution ownership before a canonical
    /// evaluator is allowed to write.  The claim itself is deliberately a
    /// short committed operation, but this guard takes an update/hold lock on
    /// that exact work row and retains the transaction through evaluator
    /// completion.  Consequently an expired timestamp cannot be requeued by
    /// another new worker while the original evaluator is still capable of
    /// committing canonical or derived evidence.  If the process dies, SQL
    /// Server rolls the guarded transaction back; the original committed
    /// lease is then recoverable through the ordinary expiry path.
    ///
    /// The conditional renewal is the ownership check.  An evaluator that
    /// cannot renew the exact state/token never receives this execution guard
    /// and therefore must not perform authoritative writes.
    /// </summary>
    internal async Task<LegendHistoricalReevaluationOwnedExecution?> TryBeginOwnedExecutionAsync(
        LegendHistoricalReevaluationWorkClaim claim,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (_db.Database.IsRelational())
            {
                // UPDLOCK is retained by this transaction through evaluator
                // completion. It is deliberately an exact primary-key row
                // fence, not a serializable range lock: expired-lease
                // recovery must be able to READPAST this live owner and keep
                // unrelated dependency lanes moving.
                transaction = await _db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            }

            var renewedUntil = DateTime.UtcNow.Add(LeaseDuration);
            var ownsClaim = await HasLockedOwnershipAsync(claim, cancellationToken);
            if (!ownsClaim || !await TryRenewLeaseAsync(claim, renewedUntil, cancellationToken))
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    await transaction.DisposeAsync();
                }
                return null;
            }

            return new LegendHistoricalReevaluationOwnedExecution(this, claim, renewedUntil, transaction);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await transaction.DisposeAsync();
            }
            throw;
        }
    }

    internal async Task CompleteAsync(
        LegendHistoricalReevaluationWorkClaim claim,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = WorkFor(claim.EvaluatorVersion, claim.Phase)
            .Where(item => item.Id == claim.WorkItemId &&
                item.ProcessingState == Processing && item.LeaseToken == claim.LeaseToken);
        if (_db.Database.IsRelational())
        {
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingState, Completed)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(item => item.LastErrorCode, (string?)null)
                .SetProperty(item => item.LastErrorMessage, (string?)null)
                .SetProperty(item => item.CompletedUtc, now)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return;
        }

        var item = await query.SingleOrDefaultAsync(cancellationToken);
        if (item is null)
            return;
        item.ProcessingState = Completed;
        item.LeaseOwner = null;
        item.LeaseToken = null;
        item.LeaseExpiresUtc = null;
        item.LastErrorCode = null;
        item.LastErrorMessage = null;
        item.CompletedUtc = now;
        item.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> CompleteOwnedAsync(
        LegendHistoricalReevaluationWorkClaim claim,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var query = WorkFor(claim.EvaluatorVersion, claim.Phase)
            .Where(item => item.Id == claim.WorkItemId &&
                item.ProcessingState == Processing && item.LeaseToken == claim.LeaseToken);
        if (_db.Database.IsRelational())
        {
            var updated = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingState, Completed)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(item => item.LastErrorCode, (string?)null)
                .SetProperty(item => item.LastErrorMessage, (string?)null)
                .SetProperty(item => item.CompletedUtc, now)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return updated == 1;
        }

        var item = await query.SingleOrDefaultAsync(cancellationToken);
        if (item is null)
            return false;
        item.ProcessingState = Completed;
        item.LeaseOwner = null;
        item.LeaseToken = null;
        item.LeaseExpiresUtc = null;
        item.LastErrorCode = null;
        item.LastErrorMessage = null;
        item.CompletedUtc = now;
        item.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal async Task ReleaseAsync(
        LegendHistoricalReevaluationWorkClaim claim,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = WorkFor(claim.EvaluatorVersion, claim.Phase)
            .Where(item => item.Id == claim.WorkItemId &&
                item.ProcessingState == Processing && item.LeaseToken == claim.LeaseToken);
        if (_db.Database.IsRelational())
        {
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingState, Pending)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(item => item.LastErrorCode, errorCode)
                .SetProperty(item => item.LastErrorMessage,
                    "The bounded replay work was released for governed recovery.")
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return;
        }

        var item = await query.SingleOrDefaultAsync(cancellationToken);
        if (item is null)
            return;
        item.ProcessingState = Pending;
        item.LeaseOwner = null;
        item.LeaseToken = null;
        item.LeaseExpiresUtc = null;
        item.LastErrorCode = errorCode;
        item.LastErrorMessage = "The bounded replay work was released for governed recovery.";
        item.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    internal async Task FailAsync(
        LegendHistoricalReevaluationWorkClaim claim,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = WorkFor(claim.EvaluatorVersion, claim.Phase)
            .Where(item => item.Id == claim.WorkItemId &&
                item.ProcessingState == Processing && item.LeaseToken == claim.LeaseToken);
        if (_db.Database.IsRelational())
        {
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingState,
                    item => item.AttemptCount >= MaximumAttempts ? Failed : Pending)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(item => item.LastErrorCode, errorCode)
                .SetProperty(item => item.LastErrorMessage,
                    "The canonical evaluator failed without creating a replacement authority.")
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return;
        }

        var item = await query.SingleOrDefaultAsync(cancellationToken);
        if (item is null)
            return;
        item.ProcessingState = item.AttemptCount >= MaximumAttempts ? Failed : Pending;
        item.LeaseOwner = null;
        item.LeaseToken = null;
        item.LeaseExpiresUtc = null;
        item.LastErrorCode = errorCode;
        item.LastErrorMessage = "The canonical evaluator failed without creating a replacement authority.";
        item.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Advances a phase only under a serializable relational barrier: no
    /// unseeded eligible identity, no Pending or Processing work, no expired
    /// reclaimable work, and no terminal failure may remain.
    /// </summary>
    internal async Task<bool> TryAdvancePhaseAsync(
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken = default)
    {
        if (_db.Database.IsRelational())
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var advanced = await TryAdvancePhaseCoreAsync(evaluatorVersion, phase, cancellationToken);
            if (advanced)
                await transaction.CommitAsync(cancellationToken);
            else
                await transaction.RollbackAsync(cancellationToken);
            return advanced;
        }

        return await TryAdvancePhaseCoreAsync(evaluatorVersion, phase, cancellationToken);
    }

    internal async Task<LegendHistoricalReevaluationStatus> GetStatusAsync(
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var work = WorkFor(evaluatorVersion, phase).AsNoTracking()
            .Where(item => item.WorkKind == CanonicalWorkKind ||
                item.WorkKind == DerivationLedgerWorkKind);
        var total = await work.LongCountAsync(cancellationToken);
        var pending = await work.LongCountAsync(item => item.ProcessingState == Pending, cancellationToken);
        var processing = await work.LongCountAsync(item => item.ProcessingState == Processing, cancellationToken);
        var completed = await work.LongCountAsync(item => item.ProcessingState == Completed, cancellationToken);
        var failed = await work.LongCountAsync(item => item.ProcessingState == Failed, cancellationToken);
        var reclaimable = await work.LongCountAsync(item => item.ProcessingState == Processing &&
            item.LeaseExpiresUtc != null && item.LeaseExpiresUtc <= now, cancellationToken);
        var active = await work.LongCountAsync(item => item.ProcessingState == Processing &&
            item.LeaseExpiresUtc != null && item.LeaseExpiresUtc > now, cancellationToken);
        var oldestPending = await work.Where(item => item.ProcessingState == Pending)
            .Select(item => (DateTime?)item.CreatedUtc).MinAsync(cancellationToken);
        var lastCompleted = await work.Where(item => item.ProcessingState == Completed)
            .Select(item => item.CompletedUtc).MaxAsync(cancellationToken);
        var replay = await _runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            evaluatorVersion,
            cancellationToken);
        return new LegendHistoricalReevaluationStatus(
            evaluatorVersion,
            phase,
            total,
            pending,
            processing,
            completed,
            failed,
            reclaimable,
            active,
            MaximumConcurrency,
            total == 0 ? 0m : decimal.Round((decimal)completed / total * 100m, 2),
            oldestPending,
            lastCompleted,
            replay.TargetEvaluatorVersion,
            replay.CompletedEvaluatorVersion,
            replay.Phase);
    }

    private async Task<bool> TryAdvancePhaseCoreAsync(
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken)
    {
        var replay = await _runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            evaluatorVersion,
            cancellationToken);
        if (!replay.RequiresWork || UsesCursorCompatibility(replay) ||
            replay.TargetEvaluatorVersion != evaluatorVersion ||
            !string.Equals(replay.Phase, phase, StringComparison.Ordinal))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var work = WorkFor(evaluatorVersion, phase);
        if (await work.AnyAsync(item => item.ProcessingState == Failed ||
                item.ProcessingState == Pending ||
                item.ProcessingState == Processing ||
                (item.ProcessingState == Processing && item.LeaseExpiresUtc != null && item.LeaseExpiresUtc <= now),
                cancellationToken))
        {
            return false;
        }

        if (await HasUnseededEligibleWorkAsync(evaluatorVersion, phase, cancellationToken))
            return false;

        var seedCompleted = await WorkFor(evaluatorVersion, phase)
            .AnyAsync(item => item.WorkKind == SeedWorkKind && item.ProcessingState == Completed, cancellationToken);
        if (!seedCompleted)
            return false;

        await _runtime.AdvanceLanguageIntelligenceReevaluationAsync(
            evaluatorVersion,
            phase,
            lastProcessedId: null,
            phaseComplete: true,
            cancellationToken);
        return true;
    }

    private IQueryable<LegendHistoricalReevaluationWorkItem> WorkFor(int evaluatorVersion, string phase) =>
        _db.Set<LegendHistoricalReevaluationWorkItem>()
            .Where(item => item.EvaluatorVersion == evaluatorVersion && item.Phase == phase);

    private async Task<bool> IsCurrentPhaseAsync(
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken)
    {
        var replay = await _runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            evaluatorVersion,
            cancellationToken);
        return replay.RequiresWork && !UsesCursorCompatibility(replay) &&
            replay.TargetEvaluatorVersion == evaluatorVersion &&
            string.Equals(replay.Phase, phase, StringComparison.Ordinal);
    }

    private async Task<bool> HasLockedOwnershipAsync(
        LegendHistoricalReevaluationWorkClaim claim,
        CancellationToken cancellationToken)
    {
        IQueryable<LegendHistoricalReevaluationWorkItem> work = _db.Set<LegendHistoricalReevaluationWorkItem>();
        if (_db.Database.IsSqlServer())
        {
            work = _db.Set<LegendHistoricalReevaluationWorkItem>().FromSqlInterpolated($"""
                SELECT * FROM [LegendHistoricalReevaluationWorkItems] WITH (UPDLOCK, ROWLOCK)
                WHERE [Id] = {claim.WorkItemId}
                """);
        }

        // On providers without SQL Server locking hints the same transaction
        // still validates the precise token. Production uses SQL Server; the
        // fallback preserves the existing test/provider behavior without
        // inventing a second ownership authority.
        return await work.AsNoTracking().AnyAsync(item =>
            item.Id == claim.WorkItemId &&
            item.EvaluatorVersion == claim.EvaluatorVersion &&
            item.Phase == claim.Phase &&
                (item.WorkKind == CanonicalWorkKind ||
                 item.WorkKind == DerivationLedgerWorkKind ||
                 item.WorkKind == FounderManifestFamilyWorkKind ||
                 item.WorkKind == FounderManifestSemanticRelationWorkKind) &&
            item.ProcessingState == Processing &&
            item.LeaseToken == claim.LeaseToken,
            cancellationToken);
    }

    private async Task<bool> TryRenewLeaseAsync(
        LegendHistoricalReevaluationWorkClaim claim,
        DateTime renewedUntil,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var query = WorkFor(claim.EvaluatorVersion, claim.Phase)
            .Where(item => item.Id == claim.WorkItemId &&
                item.ProcessingState == Processing && item.LeaseToken == claim.LeaseToken);
        if (_db.Database.IsRelational())
        {
            var updated = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LeaseExpiresUtc, renewedUntil)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return updated == 1;
        }

        var item = await query.SingleOrDefaultAsync(cancellationToken);
        if (item is null)
            return false;
        item.LeaseExpiresUtc = renewedUntil;
        item.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<LegendHistoricalReevaluationCursorAdoptionResult>
        TryAdoptProviderObservationsCursorCoreAsync(
            int evaluatorVersion,
            CancellationToken cancellationToken)
    {
        IQueryable<LegendConnectRuntimePolicy> policies = _db.Set<LegendConnectRuntimePolicy>();
        // UPDLOCK/HOLDLOCK is deliberately limited to the singleton execution
        // policy. It serializes only the cursor-to-work handoff; canonical
        // curriculum/evidence rows stay with their existing evaluators.
        if (_db.Database.IsSqlServer())
        {
            policies = _db.Set<LegendConnectRuntimePolicy>().FromSqlInterpolated($"""
                SELECT * FROM [LegendConnectRuntimePolicies] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ScopeKey] = {"Global"}
                """);
        }

        var policy = await policies.SingleOrDefaultAsync(
            item => item.ScopeKey == "Global",
            cancellationToken);
        if (policy is null ||
            policy.TargetLanguageIntelligenceEvaluatorVersion != evaluatorVersion ||
            policy.LanguageIntelligenceReevaluationPhase !=
                LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations ||
            evaluatorVersion > policy.CursorReplayCompatibilityEvaluatorVersion)
        {
            return LegendHistoricalReevaluationCursorAdoptionResult.NotApplicable;
        }

        var existingSeed = await WorkFor(
                evaluatorVersion,
                LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
            .SingleOrDefaultAsync(item => item.WorkKind == SeedWorkKind, cancellationToken);
        var boundary = existingSeed?.SubjectId ?? policy.LanguageIntelligenceReevaluationCursor;
        var observations = _db.Set<LegendTranslationAlignment>().AsNoTracking()
            .Where(item => item.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived &&
                item.SupersededUtc == null);
        var legacyCompleted = boundary.HasValue
            ? await observations.LongCountAsync(item => item.Id.CompareTo(boundary.Value) <= 0, cancellationToken)
            : 0;
        var remaining = boundary.HasValue
            ? await observations.LongCountAsync(item => item.Id.CompareTo(boundary.Value) > 0, cancellationToken)
            : await observations.LongCountAsync(cancellationToken);

        if (existingSeed is null)
        {
            _db.Set<LegendHistoricalReevaluationWorkItem>().Add(new LegendHistoricalReevaluationWorkItem
            {
                Id = Guid.NewGuid(),
                EvaluatorVersion = evaluatorVersion,
                Phase = LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
                WorkKind = SeedWorkKind,
                WorkIdentity = "__phase_seed__",
                // For this phase only, SubjectId is immutable execution
                // metadata: the legacy cursor boundary, not a canonical item.
                SubjectId = boundary,
                SubjectScope = string.Empty,
                DependencyIdentity = "phase-seed",
                ProcessingState = Pending,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }

        // Clear compatibility in the same transaction as the boundary seed.
        // A newly starting worker can therefore never select the cursor path
        // once a durable worker is able to claim remaining work.
        policy.CursorReplayCompatibilityEvaluatorVersion = Math.Min(
            policy.CursorReplayCompatibilityEvaluatorVersion,
            evaluatorVersion - 1);
        policy.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return new LegendHistoricalReevaluationCursorAdoptionResult(
            true,
            boundary,
            legacyCompleted,
            remaining);
    }

    private async Task<LegendHistoricalReevaluationWorkItem?> EnsureSeedWorkAsync(
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken)
    {
        var seed = await WorkFor(evaluatorVersion, phase).AsNoTracking()
            .SingleOrDefaultAsync(item => item.WorkKind == SeedWorkKind, cancellationToken);
        if (seed is not null)
            return seed;

        _db.Set<LegendHistoricalReevaluationWorkItem>().Add(new LegendHistoricalReevaluationWorkItem
        {
            Id = Guid.NewGuid(),
            EvaluatorVersion = evaluatorVersion,
            Phase = phase,
            WorkKind = SeedWorkKind,
            WorkIdentity = "__phase_seed__",
            SubjectScope = string.Empty,
            DependencyIdentity = "phase-seed",
            ProcessingState = Pending,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return await WorkFor(evaluatorVersion, phase).AsNoTracking()
                .SingleAsync(item => item.WorkKind == SeedWorkKind, cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            return await WorkFor(evaluatorVersion, phase).AsNoTracking()
                .SingleOrDefaultAsync(item => item.WorkKind == SeedWorkKind, cancellationToken);
        }
    }

    private async Task ReopenSeedAsync(
        Guid seedId,
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var query = WorkFor(evaluatorVersion, phase)
            .Where(item => item.Id == seedId && item.WorkKind == SeedWorkKind && item.ProcessingState == Completed);
        if (_db.Database.IsRelational())
        {
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingState, Pending)
                .SetProperty(item => item.CompletedUtc, (DateTime?)null)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return;
        }

        var seed = await query.SingleOrDefaultAsync(cancellationToken);
        if (seed is null)
            return;
        seed.ProcessingState = Pending;
        seed.CompletedUtc = null;
        seed.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private Task<LegendHistoricalReevaluationWorkClaim?> TryClaimByIdAsync(
        Guid id,
        int evaluatorVersion,
        string phase,
        string workerId,
        CancellationToken cancellationToken) =>
        TryClaimByIdAsync(id, evaluatorVersion, phase, workerId, CanonicalWorkKind, cancellationToken);

    private async Task<LegendHistoricalReevaluationWorkClaim?> TryClaimByIdAsync(
        Guid id,
        int evaluatorVersion,
        string phase,
        string workerId,
        string workKind,
        CancellationToken cancellationToken)
    {
        var ledgerRequiresPhaseCanonicalDrain =
            string.Equals(phase, LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
                StringComparison.Ordinal);
        var now = DateTime.UtcNow;
        var token = Guid.NewGuid();
        var expires = now.Add(LeaseDuration);
        var normalizedWorker = string.IsNullOrWhiteSpace(workerId)
            ? "historical-reevaluation"
            : workerId[..Math.Min(workerId.Length, 128)];
        var query = WorkFor(evaluatorVersion, phase)
            .Where(item => item.Id == id && item.WorkKind == workKind && item.ProcessingState == Pending &&
                !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(active =>
                    active.Id != item.Id && active.EvaluatorVersion == evaluatorVersion && active.Phase == phase &&
                    active.ProcessingState == Processing &&
                    active.DependencyIdentity == item.DependencyIdentity) &&
                (item.CanonicalMutationLane == null ||
                 !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(active =>
                     active.Id != item.Id &&
                     active.ProcessingState == Processing &&
                     active.CanonicalMutationLane == item.CanonicalMutationLane)))
            .Where(item => !ledgerRequiresPhaseCanonicalDrain || item.WorkKind != DerivationLedgerWorkKind ||
                (_db.Set<LegendHistoricalReevaluationWorkItem>().Any(seed =>
                    seed.EvaluatorVersion == evaluatorVersion && seed.Phase == phase &&
                    seed.WorkKind == SeedWorkKind && seed.ProcessingState == Completed) &&
                 !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(canonical =>
                    canonical.EvaluatorVersion == evaluatorVersion && canonical.Phase == phase &&
                    canonical.WorkKind == CanonicalWorkKind && canonical.ProcessingState != Completed)));

        if (_db.Database.IsSqlServer())
        {
            try
            {
                // Claim only the exact pending row.  The filtered unique
                // indexes are the database-authoritative collision fence for
                // both the dependency lane and the cross-phase canonical
                // family lane; no dirty-read admission observation is needed.
                // A losing claimant performs no evaluator work and simply
                // leaves the identity for the normal bounded scheduler.
                var updated = await _db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [target] WITH (ROWLOCK)
                    SET [ProcessingState] = {Processing},
                        [LeaseOwner] = {normalizedWorker},
                        [LeaseToken] = {token},
                        [LeaseExpiresUtc] = {expires},
                        [AttemptCount] = [target].[AttemptCount] + 1,
                        [UpdatedUtc] = {now}
                    FROM [LegendHistoricalReevaluationWorkItems] AS [target]
                    WHERE [target].[Id] = {id}
                      AND [target].[EvaluatorVersion] = {evaluatorVersion}
                      AND [target].[Phase] = {phase}
                      AND [target].[WorkKind] = {workKind}
                      AND [target].[ProcessingState] = {Pending}
                      AND
                      (
                          {ledgerRequiresPhaseCanonicalDrain} = CAST(0 AS bit)
                          OR [target].[WorkKind] <> {DerivationLedgerWorkKind}
                          OR
                          (
                              EXISTS
                              (
                                  SELECT 1
                                  FROM [LegendHistoricalReevaluationWorkItems] AS [seed]
                                  WHERE [seed].[EvaluatorVersion] = {evaluatorVersion}
                                    AND [seed].[Phase] = {phase}
                                    AND [seed].[WorkKind] = {SeedWorkKind}
                                    AND [seed].[ProcessingState] = {Completed}
                              )
                              AND NOT EXISTS
                              (
                                  SELECT 1
                                  FROM [LegendHistoricalReevaluationWorkItems] AS [canonical]
                                  WHERE [canonical].[EvaluatorVersion] = {evaluatorVersion}
                                    AND [canonical].[Phase] = {phase}
                                    AND [canonical].[WorkKind] = {CanonicalWorkKind}
                                    AND [canonical].[ProcessingState] <> {Completed}
                              )
                          )
                      )
                    """, cancellationToken);
                if (updated != 1)
                    return null;
            }
            catch (SqlException exception) when (exception.Number is 1205 or 1222 or 2601 or 2627)
            {
                return null;
            }

            var claimed = await WorkFor(evaluatorVersion, phase).AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id && item.LeaseToken == token, cancellationToken);
            return claimed is null ? null : ToClaim(claimed, token, expires);
        }

        if (_db.Database.IsRelational())
        {
            try
            {
                var updated = await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ProcessingState, Processing)
                    .SetProperty(item => item.LeaseOwner, normalizedWorker)
                    .SetProperty(item => item.LeaseToken, token)
                    .SetProperty(item => item.LeaseExpiresUtc, expires)
                    .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
                if (updated != 1)
                    return null;
            }
            catch (SqlException exception) when (exception.Number is 2601 or 2627)
            {
                // The filtered active-dependency unique index won a safe
                // cross-instance collision race. No semantic work ran.
                return null;
            }
            catch (DbUpdateException)
            {
                return null;
            }

            var claimed = await WorkFor(evaluatorVersion, phase).AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id && item.LeaseToken == token, cancellationToken);
            return claimed is null ? null : ToClaim(claimed, token, expires);
        }

        var item = await query.SingleOrDefaultAsync(cancellationToken);
        if (item is null)
            return null;
        item.ProcessingState = Processing;
        item.LeaseOwner = normalizedWorker;
        item.LeaseToken = token;
        item.LeaseExpiresUtc = expires;
        item.AttemptCount++;
        item.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
        return ToClaim(item, token, expires);
    }

    private async Task<LegendClaimCandidate?> SelectNextClaimableWorkAsync(
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsSqlServer())
        {
            try
            {
                return await _db.Set<LegendHistoricalReevaluationWorkItem>()
                    .FromSqlInterpolated($"""
                        SELECT TOP (1) [item].*
                        FROM [LegendHistoricalReevaluationWorkItems] AS [item] WITH (ROWLOCK, NOWAIT)
                        WHERE [item].[EvaluatorVersion] = {evaluatorVersion}
                          AND [item].[Phase] = {phase}
                          AND [item].[WorkKind] IN ({CanonicalWorkKind}, {DerivationLedgerWorkKind})
                          AND [item].[ProcessingState] = {Pending}
                          AND
                          (
                              [item].[WorkKind] <> {DerivationLedgerWorkKind}
                              OR
                              (
                                  EXISTS
                                  (
                                      SELECT 1
                                      FROM [LegendHistoricalReevaluationWorkItems] AS [seed]
                                      WHERE [seed].[EvaluatorVersion] = {evaluatorVersion}
                                        AND [seed].[Phase] = {phase}
                                        AND [seed].[WorkKind] = {SeedWorkKind}
                                        AND [seed].[ProcessingState] = {Completed}
                                  )
                                  AND NOT EXISTS
                                  (
                                      SELECT 1
                                      FROM [LegendHistoricalReevaluationWorkItems] AS [canonical]
                                      WHERE [canonical].[EvaluatorVersion] = {evaluatorVersion}
                                        AND [canonical].[Phase] = {phase}
                                        AND [canonical].[WorkKind] = {CanonicalWorkKind}
                                        AND [canonical].[ProcessingState] <> {Completed}
                                  )
                              )
                          )
                        ORDER BY [item].[CreatedUtc], [item].[Id]
                        """)
                    .AsNoTracking()
                    .Select(item => new LegendClaimCandidate(item.Id, item.WorkKind))
                    .FirstOrDefaultAsync(cancellationToken);
            }
            catch (SqlException exception) when (exception.Number == 1222)
            {
                return null;
            }
        }

        return await WorkFor(evaluatorVersion, phase)
            .AsNoTracking()
            .Where(item => (item.WorkKind == CanonicalWorkKind ||
                            item.WorkKind == DerivationLedgerWorkKind) &&
                item.ProcessingState == Pending)
            .Where(item => !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(active =>
                active.Id != item.Id &&
                active.EvaluatorVersion == evaluatorVersion &&
                active.Phase == phase &&
                active.ProcessingState == Processing &&
                active.DependencyIdentity == item.DependencyIdentity))
            .Where(item => item.CanonicalMutationLane == null ||
                !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(active =>
                    active.Id != item.Id &&
                    active.ProcessingState == Processing &&
                    active.CanonicalMutationLane == item.CanonicalMutationLane))
            .Where(item => item.WorkKind != DerivationLedgerWorkKind ||
                (_db.Set<LegendHistoricalReevaluationWorkItem>().Any(seed =>
                    seed.EvaluatorVersion == evaluatorVersion && seed.Phase == phase &&
                    seed.WorkKind == SeedWorkKind && seed.ProcessingState == Completed) &&
                 !_db.Set<LegendHistoricalReevaluationWorkItem>().Any(canonical =>
                    canonical.EvaluatorVersion == evaluatorVersion && canonical.Phase == phase &&
                    canonical.WorkKind == CanonicalWorkKind && canonical.ProcessingState != Completed)))
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id)
            .Select(item => new LegendClaimCandidate(item.Id, item.WorkKind))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task ReleaseSeedForNextBatchAsync(
        LegendHistoricalReevaluationWorkClaim claim,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var query = WorkFor(claim.EvaluatorVersion, claim.Phase)
            .Where(item => item.Id == claim.WorkItemId && item.WorkKind == SeedWorkKind &&
                item.ProcessingState == Processing && item.LeaseToken == claim.LeaseToken);
        if (_db.Database.IsRelational())
        {
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingState, Pending)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(item => item.AttemptCount, 0)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return;
        }

        var seed = await query.SingleOrDefaultAsync(cancellationToken);
        if (seed is null)
            return;
        seed.ProcessingState = Pending;
        seed.LeaseOwner = null;
        seed.LeaseToken = null;
        seed.LeaseExpiresUtc = null;
        seed.AttemptCount = 0;
        seed.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> HasUnseededEligibleWorkAsync(
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken) =>
        (await SelectUnseededEligibleWorkAsync(evaluatorVersion, phase, 1, cancellationToken)).Count > 0;

    /// <summary>
    /// The convergence row is strictly an inspection projection; durable work
    /// identities remain the scheduler authority. Keep this update provider
    /// safe so the in-memory lifecycle contract exercises the same phase and
    /// work decisions instead of turning a read-model update into a seeded
    /// work failure.
    /// </summary>
    private async Task RecordSeededConvergenceWorkAsync(
        int evaluatorVersion,
        string phase,
        int seededCount,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var query = _db.Set<LegendLanguageDerivationConvergence>()
            .Where(item => item.TargetEvaluatorVersion == evaluatorVersion &&
                (item.State == "Queued" || item.State == "Processing"));
        if (_db.Database.IsRelational())
        {
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PlannedWorkItemCount,
                    item => item.PlannedWorkItemCount + seededCount)
                .SetProperty(item => item.DependencyInventoryWorkItemCount,
                    item => phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory
                        ? item.DependencyInventoryWorkItemCount + seededCount
                        : item.DependencyInventoryWorkItemCount)
                .SetProperty(item => item.State, "Processing")
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return;
        }

        var convergence = await query.SingleOrDefaultAsync(cancellationToken);
        if (convergence is null)
            return;
        convergence.PlannedWorkItemCount += seededCount;
        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory)
            convergence.DependencyInventoryWorkItemCount += seededCount;
        convergence.State = "Processing";
        convergence.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<HistoricalWorkSeedCandidate>> SelectUnseededEligibleWorkAsync(
        int evaluatorVersion,
        string phase,
        int take,
        CancellationToken cancellationToken)
    {
        var bounded = Math.Clamp(take, 1, 251);
        var work = _db.Set<LegendHistoricalReevaluationWorkItem>();
        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory)
        {
            // This is a bounded metadata-only inventory for evaluators that
            // completed before dependency edges were introduced. It never
            // calls SourceFamilies analysis and never changes canonical
            // evidence; the family identity merely gives the existing work
            // authority a resumable, language-safe partition.
            var cursor = await GetDependencyInventoryCursorAsync(cancellationToken);
            var cursorIdentity = cursor?.ToString("D") ?? "origin";
            var workIdentity = $"dependency-inventory:after:{cursorIdentity}";
            var exists = await work.AnyAsync(candidate => candidate.EvaluatorVersion == evaluatorVersion &&
                candidate.Phase == phase && candidate.WorkKind == CanonicalWorkKind &&
                candidate.WorkIdentity == workIdentity, cancellationToken);
            if (exists)
                return [];

            var rows = await _db.Set<LegendCurriculumExample>().AsNoTracking()
                .Where(item => item.SupersededUtc == null &&
                    (!cursor.HasValue || item.CurriculumFamilyId.CompareTo(cursor.Value) > 0))
                .Select(item => item.CurriculumFamilyId)
                .Distinct()
                .OrderBy(item => item)
                .Take(1)
                .ToListAsync(cancellationToken);
            return rows.Select(_ => new HistoricalWorkSeedCandidate(
                cursor ?? Guid.Empty,
                DependencyInventoryFamiliesPerWorkItem.ToString(System.Globalization.CultureInfo.InvariantCulture),
                workIdentity,
                "dependency-inventory"))
                .ToList();
        }

        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies)
        {
            var rows = await (
                from example in _db.Set<LegendCurriculumExample>().AsNoTracking()
                join unit in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on example.TextUnitId equals unit.Id
                join family in _db.Set<LegendCurriculumFamily>().AsNoTracking()
                    on example.CurriculumFamilyId equals family.Id
                where example.DerivedFromCurriculumExampleId == null && example.SupersededUtc == null &&
                    unit.IsTrainingEligible &&
                    !work.Any(item => item.EvaluatorVersion == evaluatorVersion && item.Phase == phase &&
                        item.WorkKind == CanonicalWorkKind && item.SubjectId == example.CurriculumFamilyId &&
                        item.SubjectScope == example.LanguageCode)
                select new { example.CurriculumFamilyId, example.LanguageCode, family.FamilyKey }
            ).Distinct()
                .OrderBy(item => item.CurriculumFamilyId)
                .ThenBy(item => item.LanguageCode)
                .Take(bounded)
                .ToListAsync(cancellationToken);
            return rows.Select(item => new HistoricalWorkSeedCandidate(
                item.CurriculumFamilyId,
                item.LanguageCode,
                $"source-family:{item.CurriculumFamilyId:D}|language:{item.LanguageCode}",
                $"source-language:{item.LanguageCode}",
                CanonicalFamilyMutationLane(item.FamilyKey))).ToList();
        }

        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.Alignments)
        {
            var rows = await (
                from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
                join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on alignment.SourceTextUnitId equals source.Id
                join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on alignment.TargetTextUnitId equals target.Id
                where alignment.SupersededUtc == null && source.IsTrainingEligible && target.IsTrainingEligible &&
                    _db.Set<LegendCurriculumExample>().Any(example =>
                        example.TextUnitId == source.Id && example.SupersededUtc == null) &&
                    !work.Any(item => item.EvaluatorVersion == evaluatorVersion && item.Phase == phase &&
                        item.WorkKind == CanonicalWorkKind && item.SubjectId == alignment.Id && item.SubjectScope == alignment.PairKey)
                orderby alignment.Id
                select new { alignment.Id, alignment.PairKey }
            ).Take(bounded).ToListAsync(cancellationToken);
            return rows.Select(item => new HistoricalWorkSeedCandidate(
                item.Id,
                item.PairKey,
                $"alignment:{item.Id:D}",
                $"alignment-pair:{item.PairKey}")).ToList();
        }

        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
        {
            var legacyCursorBoundary = await GetProviderObservationCursorBoundaryAsync(
                evaluatorVersion,
                cancellationToken);
            var rows = await _db.Set<LegendTranslationAlignment>().AsNoTracking()
                .Where(item => item.Provenance == LegendConnectKnowledgeProvenance.ProviderDerived &&
                    item.SupersededUtc == null &&
                    (!legacyCursorBoundary.HasValue || item.Id.CompareTo(legacyCursorBoundary.Value) > 0) &&
                    !work.Any(candidate => candidate.EvaluatorVersion == evaluatorVersion && candidate.Phase == phase &&
                        candidate.WorkKind == CanonicalWorkKind && candidate.SubjectId == item.Id &&
                        candidate.SubjectScope == item.PairKey))
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.PairKey })
                .Take(bounded)
                .ToListAsync(cancellationToken);
            return rows.Select(item => new HistoricalWorkSeedCandidate(
                item.Id,
                item.PairKey,
                $"provider-observation:{item.Id:D}",
                $"provider-observation:{item.Id:D}")).ToList();
        }

        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations)
        {
            var rows = await _db.MessageTranslations.AsNoTracking()
                .Where(item => !work.Any(candidate => candidate.EvaluatorVersion == evaluatorVersion &&
                    candidate.Phase == phase && candidate.WorkKind == CanonicalWorkKind &&
                    candidate.SubjectId == item.Id && candidate.SubjectScope == string.Empty))
                .OrderBy(item => item.Id)
                .Select(item => item.Id)
                .Take(bounded)
                .ToListAsync(cancellationToken);
            return rows.Select(item => new HistoricalWorkSeedCandidate(
                item,
                string.Empty,
                $"operational-translation:{item:D}",
                $"operational-translation:{item:D}")).ToList();
        }

        throw new ArgumentOutOfRangeException(nameof(phase), "Historical reevaluation phase is not claimable.");
    }

    private Task<Guid?> GetProviderObservationCursorBoundaryAsync(
        int evaluatorVersion,
        CancellationToken cancellationToken) =>
        WorkFor(
                evaluatorVersion,
                LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
            .Where(item => item.WorkKind == SeedWorkKind)
            .Select(item => item.SubjectId)
            .SingleOrDefaultAsync(cancellationToken);

    private Task<Guid?> GetDependencyInventoryCursorAsync(
        CancellationToken cancellationToken) => _db.Set<LegendConnectRuntimePolicy>()
        .AsNoTracking()
        .Where(item => item.ScopeKey == "Global" &&
            item.LanguageIntelligenceReevaluationPhase ==
                LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory)
        .Select(item => item.LanguageIntelligenceReevaluationCursor)
        .SingleOrDefaultAsync(cancellationToken);

    private static LegendHistoricalReevaluationWorkClaim ToClaim(
        LegendHistoricalReevaluationWorkItem item,
        Guid leaseToken,
        DateTime leaseExpiresUtc) => new(
            item.Id,
            item.EvaluatorVersion,
            item.Phase,
            item.WorkKind,
            item.SubjectId,
            item.SubjectScope,
            item.DependencyIdentity,
            leaseToken,
            leaseExpiresUtc);

    private static string FailureCode(Exception exception) =>
        "historical_reevaluation_" + exception.GetType().Name
            .ToLowerInvariant()[..Math.Min(exception.GetType().Name.Length, 80)];

    internal static string CanonicalFamilyMutationLane(string familyKey) =>
        "canonical-family:" + familyKey.Trim().ToLowerInvariant();

    private sealed record HistoricalWorkSeedCandidate(
        Guid SubjectId,
        string SubjectScope,
        string WorkIdentity,
        string DependencyIdentity,
        string? CanonicalMutationLane = null);

    /// <summary>
    /// Lifetime of the database ownership fence for one canonical evaluator.
    /// The evaluator and its conditional completion share the same DbContext
    /// transaction, so cancellation, deadlock, or process loss rolls back
    /// canonical writes before another lease can reclaim the work identity.
    /// </summary>
    internal sealed class LegendHistoricalReevaluationOwnedExecution : IAsyncDisposable
    {
        private readonly LegendConnectHistoricalReevaluationWorkAuthority _authority;
        private readonly LegendHistoricalReevaluationWorkClaim _claim;
        private IDbContextTransaction? _transaction;
        private bool _finished;

        internal DateTime LeaseExpiresUtc { get; }

        internal LegendHistoricalReevaluationOwnedExecution(
            LegendConnectHistoricalReevaluationWorkAuthority authority,
            LegendHistoricalReevaluationWorkClaim claim,
            DateTime leaseExpiresUtc,
            IDbContextTransaction? transaction)
        {
            _authority = authority;
            _claim = claim;
            LeaseExpiresUtc = leaseExpiresUtc;
            _transaction = transaction;
        }

        internal async Task<bool> CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (_finished)
                return false;

            if (!await _authority.CompleteOwnedAsync(_claim, cancellationToken))
                return false;

            if (_transaction is not null)
                await _transaction.CommitAsync(cancellationToken);
            _finished = true;
            return true;
        }

        internal async Task AbortAsync()
        {
            if (_finished)
                return;
            _finished = true;
            if (_transaction is not null)
                await _transaction.RollbackAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_finished)
                await AbortAsync();
            if (_transaction is not null)
            {
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }
    }
}

internal sealed record LegendHistoricalReevaluationWorkClaim(
    Guid WorkItemId,
    int EvaluatorVersion,
    string Phase,
    string WorkKind,
    Guid? SubjectId,
    string SubjectScope,
    string DependencyIdentity,
    Guid LeaseToken,
    DateTime LeaseExpiresUtc);

internal sealed record LegendClaimCandidate(Guid WorkItemId, string WorkKind);

/// <summary>
/// One retained Founder family routed through the existing durable work
/// authority. DependencyIdentity is execution metadata only; it is derived
/// from validated manifest declarations and cannot alter curriculum meaning.
/// </summary>
internal sealed record LegendFounderManifestFamilyWorkSeed(
    string FamilyKey,
    string DependencyIdentity,
    string CanonicalMutationLane);

internal sealed record LegendHistoricalReevaluationSeedResult(
    int SeededCount,
    bool SeedingComplete,
    bool MadeProgress)
{
    public static readonly LegendHistoricalReevaluationSeedResult NotApplicable = new(0, false, false);
}

internal sealed record LegendHistoricalReevaluationCursorAdoptionResult(
    bool Adopted,
    Guid? LegacyCursorBoundary,
    long LegacyCompletedEligibleCount,
    long RemainingEligibleCount)
{
    public static readonly LegendHistoricalReevaluationCursorAdoptionResult NotApplicable =
        new(false, null, 0, 0);
}

internal sealed record LegendHistoricalReevaluationStatus(
    int EvaluatorVersion,
    string Phase,
    long Total,
    long Pending,
    long Processing,
    long Completed,
    long Failed,
    long Reclaimable,
    long ActiveWorkerCount,
    int ConfiguredMaximumConcurrency,
    decimal PercentComplete,
    DateTime? OldestPendingUtc,
    DateTime? LastCompletionUtc,
    int TargetEvaluatorVersion,
    int CompletedEvaluatorVersion,
    string CurrentPhase);
