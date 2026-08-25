using System.Text.Json;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed class LegendConnectCurriculumManifestProcessor
{
    private readonly MasterAppDbContext _db;
    private readonly LegendConnectCurriculumService _curriculum;
    private readonly LegendConnectHistoricalReevaluationWorkAuthority _durableWork;
    private readonly ILogger<LegendConnectCurriculumManifestProcessor> _logger;

    public LegendConnectCurriculumManifestProcessor(
        MasterAppDbContext db,
        LegendConnectCurriculumService curriculum,
        LegendConnectHistoricalReevaluationWorkAuthority durableWork,
        ILogger<LegendConnectCurriculumManifestProcessor> logger)
    {
        _db = db;
        _curriculum = curriculum;
        _durableWork = durableWork;
        _logger = logger;
    }

    internal async Task<int> ProcessPendingAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        var evaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        await SeedDurableFamilyWorkAsync(
            _durableWork,
            evaluatorVersion,
            Math.Clamp(take, 1, 32),
            cancellationToken);
        var processed = 0;
        for (var index = 0; index < Math.Clamp(take, 1, 32); index++)
        {
            if (!await ProcessNextDurableAsync(
                    evaluatorVersion,
                    "founder-manifest-compatibility",
                    cancellationToken))
                break;
            processed++;
        }
        // A receipt can be behind its already completed durable children
        // after a capability/version metadata repair. The durable child rows
        // remain the sole execution record; reconcile the parent projection
        // from that authority instead of reopening completed family work.
        await RefreshDurableManifestStatusesAsync(evaluatorVersion, cancellationToken);
        return processed;
    }

    /// <summary>
    /// Pumps exactly one Founder-manifest item through the sole durable work
    /// authority. This contains no independent claim, lease, retry, or
    /// completion semantics: those all remain database-authoritative in
    /// <see cref="LegendConnectHistoricalReevaluationWorkAuthority"/>.
    /// </summary>
    internal async Task<bool> ProcessNextDurableAsync(
        int evaluatorVersion,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        var claim = await _durableWork.TryClaimNextFounderManifestWorkAsync(
            evaluatorVersion,
            workerId,
            cancellationToken);
        if (claim is null)
            return false;

        try
        {
            if (claim.SubjectId is not Guid manifestId ||
                !int.TryParse(claim.SubjectScope, out var familyOrRelationIndex))
                throw new InvalidOperationException(
                    "Founder curriculum work requires a manifest identity and durable item index.");

            var completed = false;
            await using (var execution = await _durableWork.TryBeginOwnedExecutionAsync(
                claim,
                cancellationToken))
            {
                if (execution is null)
                    return false;

                if (claim.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestFamilyWorkKind)
                {
                    await ProcessDurableFamilyAsync(manifestId, familyOrRelationIndex, cancellationToken);
                }
                else if (claim.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestSemanticRelationWorkKind)
                {
                    await ProcessDurableSemanticRelationAsync(
                        manifestId,
                        familyOrRelationIndex,
                        evaluatorVersion,
                        cancellationToken);
                }
                else if (claim.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.DerivationLedgerWorkKind)
                {
                    await ProcessDurableFamilyDerivationLedgerAsync(
                        manifestId,
                        familyOrRelationIndex,
                        evaluatorVersion,
                        cancellationToken);
                }
                else
                {
                    throw new InvalidOperationException("Founder curriculum work kind is not governed by this processor.");
                }

                if (!await execution.CompleteAsync(cancellationToken))
                {
                    await execution.AbortAsync();
                    return false;
                }
                completed = true;
            }

            if (!completed)
                return false;
            // The receipt is a read projection.  It must run after the
            // owned canonical/ledger transaction commits, never while an
            // evaluator still owns row locks for the family work item.
            await RefreshDurableManifestStatusAsync(manifestId, evaluatorVersion, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await _durableWork.ReleaseAsync(
                claim,
                "founder_manifest_execution_cancelled",
                CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException)
        {
            await _durableWork.ReleaseAsync(
                claim,
                "founder_manifest_worker_cancelled",
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await _durableWork.FailAsync(
                claim,
                "founder_manifest_family_failure",
                CancellationToken.None,
                exception.ToString());
            if (claim.SubjectId is Guid manifestId)
                await RefreshDurableManifestStatusAsync(
                    manifestId,
                    evaluatorVersion,
                    CancellationToken.None);
            _logger.LogWarning(
                exception,
                "Legend Connect Founder curriculum durable work failed safely. WorkItemId={WorkItemId}",
                claim.WorkItemId);
            return true;
        }
    }

    /// <summary>
    /// Converts retained manifest families into the existing leased evaluator
    /// authority.  The manifest remains the durable submission/completion
    /// record; this method adds no curriculum interpretation and creates no
    /// alternate scheduler or evidence path.
    /// </summary>
    internal async Task<int> SeedDurableFamilyWorkAsync(
        LegendConnectHistoricalReevaluationWorkAuthority durableWork,
        int evaluatorVersion,
        int take,
        CancellationToken cancellationToken = default)
    {
        var manifests = await _db.Set<LegendCurriculumManifestWorkItem>()
            .Where(item =>
                item.ProcessingState != LegendConnectHistoricalReevaluationWorkAuthority.Retired &&
                // A payload rejected before canonical processing has an
                // exact actionable error and must remain fail-closed. Other
                // terminal receipts are safely resumed through their existing
                // deterministic durable child identities after a corrected
                // evaluator deployment; no row is manually rewritten.
                (item.ProcessingState != "Failed" ||
                 (item.LastErrorCode != "curriculum_manifest_payload_invalid" &&
                  item.LastErrorCode != "curriculum_manifest_payload_mismatch" &&
                  item.TargetLanguageIntelligenceEvaluatorVersion < evaluatorVersion)) &&
                (item.ProcessingState != "Completed" ||
                 item.CompletedLanguageIntelligenceEvaluatorVersion < evaluatorVersion))
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id)
            .Take(Math.Clamp(take, 1, 32))
            .ToListAsync(cancellationToken);
        if (manifests.Count > 0)
        {
            // Language-pair identity is shared by otherwise independent
            // English families. Establish it through the existing registry
            // before durable parallel ownership begins, so workers never
            // contend to manufacture the same canonical pair under their
            // long-lived canonical-mutation transactions.
            await _curriculum.EnsureFounderEnglishExpansionPairsAsync(cancellationToken);
        }
        var seeded = 0;
        foreach (var manifest in manifests)
        {
            LegendConnectCurriculumManifestSubmission? payload;
            try
            {
                payload = JsonSerializer.Deserialize<LegendConnectCurriculumManifestSubmission>(manifest.PayloadJson);
            }
            catch (Exception exception)
            {
                await MarkFailedAsync(manifest.Id, "curriculum_manifest_payload_invalid", exception.Message, cancellationToken);
                continue;
            }

            var families = payload?.Families?.ToArray() ?? [];
            if (families.Length != manifest.FamilyCount || families.Length == 0 ||
                families.Any(item => string.IsNullOrWhiteSpace(item.FamilyKey)))
            {
                await MarkFailedAsync(manifest.Id, "curriculum_manifest_payload_mismatch",
                    "The durable manifest payload no longer matches its accepted family count.", cancellationToken);
                continue;
            }
            var semanticRelationships = payload?.CrossExampleSemanticRelationships?.ToArray() ?? [];

            await _curriculum.EnsureFounderManifestLexicalPrerequisitesAsync(families, cancellationToken);

            // The parent manifest is a projection of durable child work.
            // Do not reopen a completed receipt merely because its projection
            // records an older evaluator. Existing current-evaluator children
            // are the execution authority and may already prove completion.
            var parentChanged = manifest.ProcessingState != "Processing" ||
                manifest.TargetLanguageIntelligenceEvaluatorVersion != evaluatorVersion;
            var familyWorkChanged = await durableWork.SeedFounderManifestFamiliesAsync(
                evaluatorVersion,
                manifest.Id,
                BuildFamilyWorkSeeds(families),
                cancellationToken);
            var relationshipWorkChanged = await durableWork.SeedFounderManifestSemanticRelationsAsync(
                evaluatorVersion,
                manifest.Id,
                semanticRelationships.Length,
                cancellationToken);
            var ledgerWorkChanged = false;
            if (LegendConnectDerivationContracts.ForEvaluator(evaluatorVersion)
                .Any(item => item.RequiresDependencyInventory))
            {
                ledgerWorkChanged = await durableWork.SeedFounderManifestDerivationLedgersAsync(
                    evaluatorVersion,
                    manifest.Id,
                    BuildFamilyWorkSeeds(families),
                    cancellationToken);
            }
            if (parentChanged || familyWorkChanged || relationshipWorkChanged || ledgerWorkChanged)
            {
                manifest.TargetLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
                manifest.ProcessingState = "Processing";
                manifest.LeaseExpiresUtc = null;
                manifest.UpdatedUtc = DateTime.UtcNow;
                seeded++;
            }
        }
        if (seeded > 0)
            await _db.SaveChangesAsync(cancellationToken);
        return seeded;
    }

    private static IReadOnlyList<LegendFounderManifestFamilyWorkSeed> BuildFamilyWorkSeeds(
        IReadOnlyList<LegendConnectCurriculumBatchSubmission> families)
    {
        // This is not curriculum interpretation: each identity is an exact,
        // normalized Founder-declared dimension/value already accepted by the
        // manifest parser. Surface carrier dimensions are explicitly marked
        // by the same @ground declaration and cannot make semantic families
        // appear to collide merely because their prose happens to match.
        var declaredSemanticIdentities = families
            .Select(family => family.Examples
                .SelectMany(example => example.Variations)
                .Where(variation => !(family.SemanticSpanGroundings ?? [])
                    .Select(grounding => grounding.SurfaceDimension)
                    .Contains(variation.Key, StringComparer.Ordinal))
                .Select(variation =>
                    $"{variation.Key.Trim().ToLowerInvariant()}\u001f{variation.Value.Trim().ToLowerInvariant()}")
                .ToHashSet(StringComparer.Ordinal))
            .ToArray();

        var sharesDeclaredSemanticIdentity = new bool[families.Count];
        for (var left = 0; left < declaredSemanticIdentities.Length - 1; left++)
        {
            for (var right = left + 1; right < declaredSemanticIdentities.Length; right++)
            {
                if (!declaredSemanticIdentities[left].Overlaps(declaredSemanticIdentities[right]))
                    continue;

                sharesDeclaredSemanticIdentity[left] = true;
                sharesDeclaredSemanticIdentity[right] = true;
            }
        }

        return families
            .Select((family, index) => new LegendFounderManifestFamilyWorkSeed(
                family.FamilyKey,
                sharesDeclaredSemanticIdentity[index]
                    // A bounded manifest-local collision must be serialized
                    // before any family begins its owned mutation transaction.
                    // All genuinely independent families retain individual
                    // lanes and keep the configured worker parallelism.
                    ? "founder-curriculum-semantic-collision:en"
                    : $"founder-curriculum-family:{family.FamilyKey.Trim().ToLowerInvariant()}",
                // This is deliberately distinct from the phase-local
                // dependency lane. It is the one stable ownership fence that
                // SourceFamilies replay and normal Founder intake share for
                // the same canonical family.
                LegendConnectHistoricalReevaluationWorkAuthority.CanonicalFamilyMutationLane(
                    family.FamilyKey)))
            .ToArray();
    }

    /// <summary>Runs one leased manifest family through the unchanged canonical curriculum authority.</summary>
    internal async Task ProcessDurableFamilyAsync(
        Guid manifestId,
        int familyIndex,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleAsync(item => item.Id == manifestId, cancellationToken);
        var payload = JsonSerializer.Deserialize<LegendConnectCurriculumManifestSubmission>(manifest.PayloadJson);
        var families = payload?.Families?.ToArray() ?? [];
        if (families.Length != manifest.FamilyCount || familyIndex < 0 || familyIndex >= families.Length)
            throw new InvalidOperationException("The leased Founder curriculum family does not match its retained manifest.");

        var family = families[familyIndex];
        var result = await _curriculum.SubmitFounderEnglishBatchAsync(family, cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"{result.ErrorCode ?? "curriculum_family_processing_rejected"}: " +
                (result.Message ?? "The canonical curriculum authority rejected the family without additional detail."));

        _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
        {
            Id = Guid.NewGuid(),
            FounderUserId = manifest.FounderUserId,
            Action = "FounderCurriculumFamilyProcessed",
            Result = result.DuplicatePrevented ? "CanonicalReuse" : "Succeeded",
            LanguageCode = "en",
            Detail = Truncate(
                $"Manifest {manifest.ManifestHash[..12]} family {familyIndex + 1}/{manifest.FamilyCount}: {family.FamilyKey}. " +
                $"Evaluator v{manifest.TargetLanguageIntelligenceEvaluatorVersion}. " +
                (result.Message ?? "Canonical curriculum processing completed."), 500),
            OccurredUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Projects compact derivation dependencies only after the manifest's
    /// canonical family and cross-example relation work has committed.  This
    /// delegates to the same curriculum ledger writer used by historical
    /// dependency convergence; it has no independent semantic mutation path.
    /// </summary>
    internal async Task ProcessDurableFamilyDerivationLedgerAsync(
        Guid manifestId,
        int familyIndex,
        int evaluatorVersion,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleAsync(item => item.Id == manifestId, cancellationToken);
        var payload = JsonSerializer.Deserialize<LegendConnectCurriculumManifestSubmission>(manifest.PayloadJson);
        var families = payload?.Families?.ToArray() ?? [];
        if (families.Length != manifest.FamilyCount || familyIndex < 0 || familyIndex >= families.Length)
            throw new InvalidOperationException("The leased Founder dependency ledger does not match its retained family.");

        var familyKey = families[familyIndex].FamilyKey.Trim().ToLowerInvariant();
        var familyId = await _db.Set<LegendCurriculumFamily>()
            .Where(item => item.FamilyKey == familyKey)
            .Select(item => (Guid?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (familyId is null)
            throw new InvalidOperationException("The retained Founder family was not canonically admitted before dependency inventory.");

        await _curriculum.RefreshCurrentDerivationDependenciesForFamilyAsync(
            familyId.Value,
            evaluatorVersion,
            cancellationToken);
    }

    /// <summary>
    /// Executes one already accepted Founder semantic relationship only after
    /// all of the manifest's governed example families have completed. The
    /// curriculum service remains the sole relation/transition derivation
    /// authority; this processor supplies durable bounded execution only.
    /// </summary>
    internal async Task ProcessDurableSemanticRelationAsync(
        Guid manifestId,
        int relationshipIndex,
        int evaluatorVersion,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleAsync(item => item.Id == manifestId, cancellationToken);
        var payload = JsonSerializer.Deserialize<LegendConnectCurriculumManifestSubmission>(manifest.PayloadJson);
        var relationships = payload?.CrossExampleSemanticRelationships?.ToArray() ?? [];
        if (relationshipIndex < 0 || relationshipIndex >= relationships.Length)
            throw new InvalidOperationException("The leased Founder semantic relationship does not match its retained manifest.");

        await _curriculum.PersistFounderCrossExampleSemanticRelationAsync(
            relationships[relationshipIndex],
            evaluatorVersion,
            cancellationToken);
        _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
        {
            Id = Guid.NewGuid(),
            FounderUserId = manifest.FounderUserId,
            Action = "FounderCrossExampleSemanticRelationProcessed",
            Result = "Succeeded",
            LanguageCode = "en",
            Detail = Truncate(
                $"Manifest {manifest.ManifestHash[..12]} semantic relationship {relationshipIndex + 1}/{relationships.Length}. " +
                $"Evaluator v{evaluatorVersion} projected through the governed transition authority.",
                500),
            OccurredUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Derives the manifest status solely from its durable child work.</summary>
    internal async Task RefreshDurableManifestStatusAsync(
        Guid manifestId,
        int evaluatorVersion,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleAsync(item => item.Id == manifestId, cancellationToken);
        // The Founder manifest receipt represents canonical curriculum
        // admission plus its governed cross-example semantic relationships.
        // Dependency inventory is a downstream derivation-convergence concern
        // with its own durable work/state. It must not reopen or prolong the
        // accepted Founder manifest receipt merely because a newer evaluator
        // requires metadata projection.
        var children = await _db.Set<LegendHistoricalReevaluationWorkItem>()
            .Where(item => item.EvaluatorVersion == evaluatorVersion &&
                item.Phase == LegendConnectHistoricalReevaluationWorkAuthority.FounderCurriculumPhase &&
                (item.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestFamilyWorkKind ||
                 item.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestSemanticRelationWorkKind) &&
                item.SubjectId == manifestId)
            .ToListAsync(cancellationToken);
        var payload = JsonSerializer.Deserialize<LegendConnectCurriculumManifestSubmission>(manifest.PayloadJson);
        var relationshipCount = payload?.CrossExampleSemanticRelationships?.Count ?? 0;
        var requiredWorkCount = manifest.FamilyCount + relationshipCount;
        var completed = children.Count(item => item.ProcessingState == "Completed");
        var completedFamilies = children.Count(item =>
            item.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestFamilyWorkKind &&
            item.ProcessingState == "Completed");
        var failed = children.FirstOrDefault(item =>
            item.ProcessingState == "Failed" ||
            item.ProcessingState == LegendConnectHistoricalReevaluationWorkAuthority.Retired);
        // This is the existing UI/progress field for family ingestion.  The
        // later semantic-relation work belongs to the same durable manifest
        // lifecycle, but must not make the family counter exceed FamilyCount.
        manifest.NextFamilyIndex = completedFamilies;
        manifest.LeaseExpiresUtc = null;
        manifest.UpdatedUtc = DateTime.UtcNow;
        if (failed is not null)
        {
            // The parent is only a projection of the durable child authority.
            // A terminal child therefore retires the parent in place too,
            // preserving its historical identity/error without making the
            // receipt executable or seedable again.
            manifest.ProcessingState = failed.ProcessingState == LegendConnectHistoricalReevaluationWorkAuthority.Retired
                ? LegendConnectHistoricalReevaluationWorkAuthority.Retired
                : "Failed";
            manifest.AttemptCount = failed.AttemptCount;
            manifest.LastErrorCode = failed.LastErrorCode ?? "curriculum_manifest_family_failed";
            manifest.LastErrorMessage = failed.LastErrorMessage;
        }
        else if (children.Count == requiredWorkCount && completed == requiredWorkCount)
        {
            manifest.ProcessingState = "Completed";
            manifest.CompletedLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
            manifest.CompletedUtc = DateTime.UtcNow;
            manifest.LastErrorCode = null;
            manifest.LastErrorMessage = null;
        }
        else
        {
            manifest.ProcessingState = "Processing";
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reconciles parent receipts after a bounded worker drain. Individual
    /// family workers can complete concurrently, so each worker's immediate
    /// parent projection is deliberately advisory. This final, fresh query is
    /// the authoritative completion check: a manifest is completed only when
    /// every durable child for the active evaluator is durably completed.
    /// </summary>
    internal async Task<int> RefreshDurableManifestStatusesAsync(
        int evaluatorVersion,
        CancellationToken cancellationToken = default)
    {
        var manifestIds = await _db.Set<LegendCurriculumManifestWorkItem>()
            .AsNoTracking()
            .Where(item => item.ProcessingState == "Processing" &&
                item.TargetLanguageIntelligenceEvaluatorVersion == evaluatorVersion)
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var manifestId in manifestIds)
            await RefreshDurableManifestStatusAsync(manifestId, evaluatorVersion, cancellationToken);
        return manifestIds.Count;
    }

    private async Task MarkFailedAsync(
        Guid id,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var work = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleAsync(item => item.Id == id, cancellationToken);
        work.ProcessingState = "Failed";
        work.LastErrorCode = Truncate(errorCode, 120);
        work.LastErrorMessage = Truncate(message, 1000);
        work.LeaseExpiresUtc = null;
        work.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string? value, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }
}

public sealed class LegendConnectCurriculumManifestHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LegendConnectCurriculumManifestHostedService> _logger;

    public LegendConnectCurriculumManifestHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<LegendConnectCurriculumManifestHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var durableWork = scope.ServiceProvider
                    .GetRequiredService<LegendConnectHistoricalReevaluationWorkAuthority>();
                var processor = scope.ServiceProvider
                    .GetRequiredService<LegendConnectCurriculumManifestProcessor>();
                var evaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
                var seeded = await processor.SeedDurableFamilyWorkAsync(
                    durableWork, evaluatorVersion, durableWork.MaximumConcurrency * 2, stoppingToken);
                var workers = Enumerable.Range(0, durableWork.MaximumConcurrency)
                    .Select(slot => ProcessDurableManifestSlotAsync(
                        evaluatorVersion,
                        $"{Environment.MachineName}:founder-manifest:{slot}",
                        stoppingToken))
                    .ToArray();
                var processed = (await Task.WhenAll(workers)).Sum();
                // Workers use independent scopes, so their parent receipt
                // snapshots may complete in any order. Re-read all active
                // receipts after the bounded drain before deciding whether
                // the host has work left; this preserves durable child work
                // as the only completion authority.
                _ = await processor.RefreshDurableManifestStatusesAsync(
                    evaluatorVersion, stoppingToken);
                if (seeded > 0 || processed > 0)
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Legend Connect curriculum manifest background processing cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task<int> ProcessDurableManifestSlotAsync(
        int evaluatorVersion,
        string workerId,
        CancellationToken stoppingToken)
    {
        var processed = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider
                .GetRequiredService<LegendConnectCurriculumManifestProcessor>();
            if (!await processor.ProcessNextDurableAsync(
                    evaluatorVersion,
                    workerId,
                    stoppingToken))
                return processed;
            processed++;
        }
        return processed;
    }
}
