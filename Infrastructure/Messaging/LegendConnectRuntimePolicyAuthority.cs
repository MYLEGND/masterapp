using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

/// <summary>
/// Durable Founder-operated policy for the existing Legend Connect worker,
/// capacity ledger, and intelligence evaluator. This class changes policy and
/// ordering only; it never acquires corpus, translates text, or owns a queue.
/// </summary>
internal sealed class LegendConnectRuntimePolicyAuthority : ILegendConnectRuntimePolicyAuthority
{
    private const string GlobalScope = "Global";
    private static readonly TimeSpan WorkerHealthyWindow = TimeSpan.FromMinutes(10);

    private readonly MasterAppDbContext _db;
    private readonly IControlledResourceAccessService _access;
    private readonly ILegendLanguageRegistry _languages;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LegendConnectRuntimePolicyAuthority> _logger;
    private readonly IAzureTranslatorSubscriptionCapacitySource? _azureSubscriptionCapacity;

    public LegendConnectRuntimePolicyAuthority(
        MasterAppDbContext db,
        IControlledResourceAccessService access,
        ILegendLanguageRegistry languages,
        IConfiguration configuration,
        ILogger<LegendConnectRuntimePolicyAuthority> logger,
        IAzureTranslatorSubscriptionCapacitySource? azureSubscriptionCapacity = null)
    {
        _db = db;
        _access = access;
        _languages = languages;
        _configuration = configuration;
        _logger = logger;
        _azureSubscriptionCapacity = azureSubscriptionCapacity;
    }

    public async Task<LegendConnectRuntimePolicySnapshot> GetEffectiveAsync(
        CancellationToken cancellationToken = default)
    {
        var policy = await _db.Set<LegendConnectRuntimePolicy>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.ScopeKey == GlobalScope, cancellationToken);
        return policy is null
            ? BootstrapPolicy()
            : await ToEffectiveSnapshotAsync(policy, true, cancellationToken);
    }

    public async Task<LegendConnectProductionReadinessSnapshot> GetReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        var policy = await GetEffectiveAsync(cancellationToken);
        var azureCapacity = _azureSubscriptionCapacity is null
            ? null
            : await _azureSubscriptionCapacity.GetCurrentAsync(cancellationToken);
        // When the Azure source is registered (as it is in production), a
        // failed read must never revive historical runtime-policy numbers.
        // Capacity is either Azure-synchronized or safely unavailable.
        var providerCapacity = _azureSubscriptionCapacity is null
            ? policy.MonthlyProviderCapacityCharacters
            : azureCapacity?.HourlyCharacterLimit ?? 0;
        var liveReserve = _azureSubscriptionCapacity is null
            ? policy.LiveTranslationReserveCharacters
            : azureCapacity?.HourlyLiveReserveCharacters ?? 0;
        var safeCorpusCapacity = _azureSubscriptionCapacity is null
            ? policy.MaximumSafeCorpusConsumptionCharacters
            : azureCapacity?.MaximumSafeHourlyCorpusCharacters ?? 0;
        var checks = new List<LegendConnectReadinessCheck>();
        var databaseReady = await DatabaseReadyAsync(cancellationToken);
        checks.Add(Check("Database", databaseReady, databaseReady
            ? "The durable Legend Connect control-plane schema is reachable."
            : "The durable Legend Connect schema is unavailable."));

        var providerReady = IsAzureProviderConfigured() &&
                            (_azureSubscriptionCapacity is null || azureCapacity?.IsAvailable == true);
        checks.Add(Check("Azure Provider", providerReady, providerReady
            ? azureCapacity is null
                ? "A server-configured Azure Translator endpoint and credential are available."
                : $"Azure Translator tier {azureCapacity.Tier} is synchronized from the configured Azure resource."
            : azureCapacity?.Detail ?? "Azure Translator is not configured on this server."));

        IReadOnlyList<LegendLanguageDefinitionSnapshot> languages;
        try
        {
            languages = await _languages.ListEnabledTranslationLanguagesAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Legend Connect language registry readiness could not be read.");
            languages = Array.Empty<LegendLanguageDefinitionSnapshot>();
        }
        var registryReady = languages.Any(item => item.IsLearningEnabled);
        checks.Add(Check("Language Registry", registryReady, registryReady
            ? $"{languages.Count(item => item.IsLearningEnabled)} enabled learning language(s) are available."
            : "No enabled learning language is available."));

        var now = DateTime.UtcNow;
        var learningWorkerReady = policy.LastLearningWorkerHeartbeatUtc is { } learningHeartbeat &&
                                  now - learningHeartbeat <= WorkerHealthyWindow;
        var acquisitionWorkerReady = policy.LastAcquisitionWorkerHeartbeatUtc is { } acquisitionHeartbeat &&
                                     now - acquisitionHeartbeat <= WorkerHealthyWindow;
        checks.Add(Check("Learning Worker", learningWorkerReady, learningWorkerReady
            ? "The existing learning worker reported within the safe health window."
            : "The learning worker has not reported within the safe health window."));
        checks.Add(Check("Acquisition Worker", acquisitionWorkerReady, acquisitionWorkerReady
            ? "The existing acquisition worker reported within the safe health window."
            : "The acquisition worker has not reported within the safe health window."));

        var capacityReady = providerCapacity > 0 && safeCorpusCapacity > 0;
        checks.Add(Check("Capacity Policy", capacityReady, capacityReady
            ? azureCapacity is null
                ? "Provider and corpus-consumption limits are configured."
                : azureCapacity.MonthlyIncludedCharacterAllowance is { } monthlyAllowance
                    ? $"Azure tier {azureCapacity.Tier} provides {monthlyAllowance:N0} included characters per month and {providerCapacity:N0} characters per rolling hour; corpus capacity is derived from both limits."
                    : $"Azure tier {azureCapacity.Tier} provides {providerCapacity:N0} characters per rolling hour; this metered tier has no fixed monthly included-character allowance."
            : azureCapacity is null
                ? "Set a positive provider capacity and safe corpus limit."
                : azureCapacity.Detail ?? "Azure Translator capacity cannot be synchronized."));

        var reserveReady = providerCapacity > 0 &&
                           liveReserve >= 0 &&
                           liveReserve < providerCapacity &&
                           safeCorpusCapacity <= providerCapacity - liveReserve;
        checks.Add(Check("Live Reserve", reserveReady, reserveReady
            ? azureCapacity is null
                ? "The protected live-translation reserve is valid."
                : $"A {AzureTranslatorSubscriptionCapacity.LiveReservePercent}% live reserve is derived from the current Azure tier."
            : "Live reserve must be below capacity and corpus consumption must remain outside it."));

        // Phase 12 makes historical convergence an explicit production gate.
        // The replay itself remains owned by the existing learning worker and
        // runtime-policy cursor. Readiness only observes that canonical state;
        // it does not create another replay path or mutate historical data.
        var convergence = await _db.Set<LegendConnectRuntimePolicy>()
            .AsNoTracking()
            .Where(item => item.ScopeKey == GlobalScope)
            .Select(item => new
            {
                item.TargetLanguageIntelligenceEvaluatorVersion,
                item.CompletedLanguageIntelligenceEvaluatorVersion,
                item.LanguageIntelligenceReevaluationPhase
            })
            .SingleOrDefaultAsync(cancellationToken);

        var historicalConvergenceReady =
            convergence is not null &&
            convergence.CompletedLanguageIntelligenceEvaluatorVersion >=
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current &&
            convergence.TargetLanguageIntelligenceEvaluatorVersion ==
                convergence.CompletedLanguageIntelligenceEvaluatorVersion &&
            string.Equals(
                convergence.LanguageIntelligenceReevaluationPhase,
                LegendConnectLanguageIntelligenceReevaluationPhases.Complete,
                StringComparison.Ordinal);

        checks.Add(Check(
            "Historical Convergence",
            historicalConvergenceReady,
            historicalConvergenceReady
                ? $"Historical language intelligence is converged at evaluator v{LegendConnectLanguageIntelligenceEvaluatorVersion.Current}."
                : $"Historical language intelligence must complete evaluator v{LegendConnectLanguageIntelligenceEvaluatorVersion.Current} before autonomous production learning is ready."));

        var candidates = await CandidateReadinessAsync(cancellationToken);
        var candidateReady = candidates.PendingEligible > 0;
        checks.Add(new LegendConnectReadinessCheck(
            "Approved Corpus",
            candidateReady ? "READY" : "IDLE",
            candidateReady
                ? $"{candidates.PendingEligible:N0} eligible approved candidate(s) await acquisition."
                : "No eligible approved corpus candidate is waiting. Submit source-language-only Founder-approved knowledge to queue missing enabled coverage."));

        var baseReady = databaseReady && providerReady && registryReady && learningWorkerReady &&
                        acquisitionWorkerReady && capacityReady && reserveReady &&
                        historicalConvergenceReady && policy.LearningEnabled;
        // An empty corpus queue is an expected idle condition, not a safety
        // failure. Enabling now means the single existing worker is ready to
        // claim future Founder-approved monolingual seeds without a second
        // activation step or a parallel scheduler.
        var canActivate = baseReady;
        var state = policy.CorpusAcquisitionEnabled
            ? baseReady ? candidateReady ? "ACTIVE" : "ACTIVE — NO ELIGIBLE WORK" : "DEGRADED"
            : baseReady ? candidateReady ? "READY" : "READY — NO ELIGIBLE WORK" : "BLOCKED";
        var summary = state switch
        {
            "ACTIVE" => "Autonomous acquisition is active and constrained by the protected live reserve.",
            "ACTIVE — NO ELIGIBLE WORK" => "Autonomous acquisition is active; no eligible approved work is waiting.",
            "READY" => "All activation gates pass. Founder may activate autonomous acquisition.",
            "READY — NO ELIGIBLE WORK" => "All safety gates pass. Founder may activate autonomous acquisition now; it will remain idle until approved source-language knowledge creates eligible missing coverage.",
            "DEGRADED" => "Autonomous acquisition is enabled but one or more safety gates no longer pass; no new work will start.",
            _ => FirstBlockedDetail(checks, policy.LearningEnabled)
        };
        return new LegendConnectProductionReadinessSnapshot(
            state,
            canActivate,
            summary,
            checks,
            candidates.Approved,
            candidates.PendingEligible,
            candidates.RejectedOrIneligible,
            candidates.Deduplicated,
            candidates.AwaitingKnowledgePairs);
    }

    public async Task<LegendConnectRuntimePolicySnapshot> UpdateAsync(
        string founderUserId,
        LegendConnectRuntimePolicyMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var founder = await RequireFounderAsync(founderUserId, cancellationToken);
        Validate(mutation);
        return await PersistFounderMutationAsync(async () =>
        {
            var policy = await GetTrackedPolicyAsync(cancellationToken);
            var before = ToSnapshot(policy, true);
            policy.MonthlyProviderCapacityCharacters = mutation.MonthlyProviderCapacityCharacters;
            policy.LiveTranslationReserveCharacters = mutation.LiveTranslationReserveCharacters;
            policy.MaximumSafeCorpusConsumptionCharacters = mutation.MaximumSafeCorpusConsumptionCharacters;
            policy.LearningEnabled = mutation.LearningEnabled;
            policy.ContextualCompositionMode = NormalizeContextualMode(mutation.ContextualCompositionMode);
            policy.ContextualMinimumConfidence = mutation.ContextualMinimumConfidence;
            policy.UpdatedByUserId = founder;
            policy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var after = ToSnapshot(policy, true);
            await WritePolicyAuditAsync(founder, "RuntimePolicyChanged", before, after, null, null, cancellationToken);
            return after;
        }, cancellationToken);
    }

    public async Task<LegendConnectRuntimePolicySnapshot> UpdateCompositionAsync(
        string founderUserId,
        bool learningEnabled,
        string? contextualCompositionMode,
        decimal contextualMinimumConfidence,
        CancellationToken cancellationToken = default)
    {
        var founder = await RequireFounderAsync(founderUserId, cancellationToken);
        if (contextualMinimumConfidence is < 0.90m or > 1m)
            throw new ArgumentException("Contextual confidence must remain between 0.90 and 1.00.", nameof(contextualMinimumConfidence));
        return await PersistFounderMutationAsync(async () =>
        {
            var policy = await GetTrackedPolicyAsync(cancellationToken);
            var before = ToSnapshot(policy, true);
            policy.LearningEnabled = learningEnabled;
            // The top-level production control owns explicit mode changes.
            // This existing composition-settings save preserves the current
            // server mode when no mode was supplied; it cannot reset Active,
            // Shadow, or Disabled through a second Founder settings path.
            policy.ContextualCompositionMode = contextualCompositionMode is null
                ? before.ContextualCompositionMode
                : NormalizeContextualMode(contextualCompositionMode);
            policy.ContextualMinimumConfidence = contextualMinimumConfidence;
            policy.UpdatedByUserId = founder;
            policy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var after = ToSnapshot(policy, true);
            await WritePolicyAuditAsync(founder, "RuntimeCompositionPolicyChanged", before, after, null, null, cancellationToken);
            return after;
        }, cancellationToken);
    }

    public async Task<LegendConnectRuntimePolicySnapshot> SetContextualCompositionModeAsync(
        string founderUserId,
        string contextualCompositionMode,
        CancellationToken cancellationToken = default)
    {
        var founder = await RequireFounderAsync(founderUserId, cancellationToken);
        var normalizedMode = NormalizeContextualMode(contextualCompositionMode);
        return await PersistFounderMutationAsync(async () =>
        {
            var policy = await GetTrackedPolicyAsync(cancellationToken);
            var before = ToSnapshot(policy, true);
            policy.ContextualCompositionMode = normalizedMode;
            policy.UpdatedByUserId = founder;
            policy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var after = ToSnapshot(policy, true);
            await WritePolicyAuditAsync(founder, "ContextualCompositionModeChanged", before, after, null, null, cancellationToken);
            return after;
        }, cancellationToken);
    }

    public async Task<LegendConnectProductionReadinessSnapshot> ActivateAsync(
        string founderUserId,
        CancellationToken cancellationToken = default)
    {
        var founder = await RequireFounderAsync(founderUserId, cancellationToken);
        var readiness = await GetReadinessAsync(cancellationToken);
        if (!readiness.CanActivate)
        {
            await WriteSimpleAuditAsync(founder, "AutonomousAcquisitionActivation", "Blocked", null, null,
                readiness.Summary, cancellationToken);
            return readiness;
        }

        await PersistFounderMutationAsync(async () =>
        {
            var policy = await GetTrackedPolicyAsync(cancellationToken);
            var before = ToSnapshot(policy, true);
            policy.CorpusAcquisitionEnabled = true;
            policy.UpdatedByUserId = founder;
            policy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            await WritePolicyAuditAsync(founder, "AutonomousAcquisitionActivated", before, ToSnapshot(policy, true), null, null, cancellationToken);
            return true;
        }, cancellationToken);
        return await GetReadinessAsync(cancellationToken);
    }

    public async Task<LegendConnectRuntimePolicySnapshot> PauseAsync(
        string founderUserId,
        CancellationToken cancellationToken = default)
    {
        var founder = await RequireFounderAsync(founderUserId, cancellationToken);
        return await PersistFounderMutationAsync(async () =>
        {
            var policy = await GetTrackedPolicyAsync(cancellationToken);
            var before = ToSnapshot(policy, true);
            policy.CorpusAcquisitionEnabled = false;
            policy.UpdatedByUserId = founder;
            policy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var after = ToSnapshot(policy, true);
            await WritePolicyAuditAsync(founder, "AutonomousAcquisitionPaused", before, after, null, null, cancellationToken);
            return after;
        }, cancellationToken);
    }

    public async Task<LegendConnectRuntimePolicySnapshot> ConfigureAutonomousLanguageFocusAsync(
        string founderUserId,
        LegendConnectAutonomousLanguageFocusMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var founder = await RequireFounderAsync(founderUserId, cancellationToken);
        var targetLanguageCodes = mutation.Enabled
            ? await NormalizeFocusTargetLanguageCodesAsync(mutation.TargetLanguageCodes, cancellationToken)
            : Array.Empty<string>();

        await PersistFounderMutationAsync(async () =>
        {
            var policy = await GetTrackedPolicyAsync(cancellationToken);
            var existing = await GetFocusedTargetLanguageCodesAsync(policy.Id, cancellationToken);
            await ClearAutonomousLanguageFocusAsync(policy.Id, cancellationToken);

            if (mutation.Enabled)
            {
                foreach (var targetLanguageCode in targetLanguageCodes)
                {
                    _db.Set<LegendConnectAutonomousLanguageFocus>().Add(new LegendConnectAutonomousLanguageFocus
                    {
                        Id = Guid.NewGuid(),
                        RuntimePolicyId = policy.Id,
                        TargetLanguageCode = targetLanguageCode,
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    });
                }
            }
            policy.UpdatedByUserId = founder;
            policy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            var action = mutation.Enabled
                ? "FounderAutonomousLanguageFocusEnabled"
                : "FounderAutonomousLanguageFocusDisabled";
            var prior = existing.Count == 0 ? "Automatic demand-driven" : string.Join(", ", existing);
            var current = targetLanguageCodes.Count == 0 ? "Automatic demand-driven" : string.Join(", ", targetLanguageCodes);
            await WriteSimpleAuditAsync(
                founder,
                action,
                "Succeeded",
                null,
                null,
                $"English-source acquisition focus changed from {prior} to {current}.",
                cancellationToken);
            return true;
        }, cancellationToken);

        return await GetEffectiveAsync(cancellationToken);
    }

    /// <summary>
    /// Owns only the durable version/checkpoint for the existing learning
    /// worker. The worker remains responsible for invoking the canonical
    /// curriculum and quality evaluators for each bounded page.
    /// </summary>
    public async Task<LegendConnectLanguageIntelligenceReevaluationSnapshot> GetOrStartLanguageIntelligenceReevaluationAsync(
        int evaluatorVersion,
        CancellationToken cancellationToken = default)
    {
        if (evaluatorVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(evaluatorVersion), "Evaluator version must be positive.");

        // In-memory tests use the tracked policy directly. Relational
        // deployments take an update lock on the one policy row while they
        // compare contracts and advance the target. That makes adoption one
        // database-authoritative decision: two App Service instances cannot
        // independently seed the same evaluator frontier from a stale
        // snapshot.
        if (!_db.Database.IsRelational())
        {
            var inMemoryPolicy = await GetTrackedPolicyAsync(cancellationToken);
            return await GetOrStartLanguageIntelligenceReevaluationCoreAsync(
                inMemoryPolicy,
                evaluatorVersion,
                cancellationToken);
        }

        // Bootstrap occurs before the adoption transaction so the unique
        // ScopeKey constraint remains the authority when two first-starting
        // instances race to create the singleton.
        _ = await GetLanguageIntelligencePolicyAsync(cancellationToken);
        if (_db.Database.CurrentTransaction is not null)
        {
            var participatingPolicy = await GetLockedLanguageIntelligencePolicyAsync(cancellationToken);
            return await GetOrStartLanguageIntelligenceReevaluationCoreAsync(
                participatingPolicy,
                evaluatorVersion,
                cancellationToken);
        }
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var policy = await GetLockedLanguageIntelligencePolicyAsync(cancellationToken);
        var snapshot = await GetOrStartLanguageIntelligenceReevaluationCoreAsync(
            policy,
            evaluatorVersion,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private async Task<LegendConnectLanguageIntelligenceReevaluationSnapshot>
        GetOrStartLanguageIntelligenceReevaluationCoreAsync(
            LegendConnectRuntimePolicy policy,
            int evaluatorVersion,
            CancellationToken cancellationToken)
    {
        if (policy.CompletedLanguageIntelligenceEvaluatorVersion >= evaluatorVersion &&
            policy.LanguageIntelligenceReevaluationPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete)
        {
            // A version watermark is only a checkpoint, never proof that the
            // deployment contract which produced a projection is current.
            // This matters when a corrected compiler ships under the same
            // evaluator generation as a previously completed binary.  The
            // singleton policy lock held by the caller makes this comparison
            // and any resulting replay adoption one database-authoritative
            // decision across all application instances.
            if (!await HasDerivationContractDriftAsync(evaluatorVersion, cancellationToken))
                return ToReevaluationSnapshot(policy);
        }

        // A newer binary must never reset, cancel, or silently replace a
        // durable in-flight older evaluator.  Its existing owner remains the
        // sole executor until its authoritative phase drain reaches Complete;
        // only then may dependency planning begin for the later contract.
        if (policy.TargetLanguageIntelligenceEvaluatorVersion > 0 &&
            policy.TargetLanguageIntelligenceEvaluatorVersion < evaluatorVersion &&
            LegendConnectLanguageIntelligenceReevaluationPhases.IsWorkPhase(
                policy.LanguageIntelligenceReevaluationPhase))
        {
            return ToReevaluationSnapshot(policy);
        }

        if (policy.TargetLanguageIntelligenceEvaluatorVersion == evaluatorVersion &&
            LegendConnectLanguageIntelligenceReevaluationPhases.IsWorkPhase(
                policy.LanguageIntelligenceReevaluationPhase))
        {
            // A convergence plan can discover a source-contract frontier
            // after an earlier V21 worker already advanced into a dependent
            // phase.  The durable plan, not the displayed evaluator number,
            // is authoritative: resume from its earliest affected phase so
            // stale source projections cannot be skipped and then falsely
            // counted as reusable by downstream alignment work.
            await RewindIncompleteConvergenceToEarliestFrontierAsync(
                policy,
                evaluatorVersion,
                cancellationToken);
            return ToReevaluationSnapshot(policy);
        }

        return await StartDependencyDrivenConvergenceAsync(policy, evaluatorVersion, cancellationToken);
    }

    /// <summary>
    /// Plans one forward evaluator convergence by comparing durable
    /// derivation-contract declarations.  This is the existing runtime-policy
    /// authority deciding the starting phase for the existing durable worker;
    /// it neither evaluates curriculum nor introduces a second scheduler.
    /// </summary>
    private async Task<LegendConnectLanguageIntelligenceReevaluationSnapshot>
        StartDependencyDrivenConvergenceAsync(
            LegendConnectRuntimePolicy policy,
            int evaluatorVersion,
            CancellationToken cancellationToken)
    {
        var baselineVersion = policy.CompletedLanguageIntelligenceEvaluatorVersion;
        var targetContracts = LegendConnectDerivationContracts.ForEvaluator(evaluatorVersion);

        // A previously completed evaluator predates contract persistence only
        // on its first upgrade. Bootstrap its declared contract set as durable
        // historical provenance. This is metadata-only reuse: no canonical
        // evidence is rebuilt, altered, or reclassified.
        var hasDurableContractHistory = await _db.Set<LegendLanguageDerivationContract>()
            .AnyAsync(cancellationToken);
        if (baselineVersion > 0 && !hasDurableContractHistory)
        {
            // If a completed watermark already equals the target evaluator,
            // bootstrap from the immediately preceding declared contract.
            // The durable rows were absent, so treating today's declaration
            // as historical would falsely make a changed compiler reusable.
            var historicalContractVersion = Math.Min(
                baselineVersion,
                Math.Max(0, evaluatorVersion - 1));
            if (historicalContractVersion > 0)
            {
                await EnsureContractDeclarationsAsync(
                    LegendConnectDerivationContracts.ForEvaluator(historicalContractVersion),
                    cancellationToken);
            }
        }

        var activeContracts = await _db.Set<LegendLanguageDerivationContract>()
            .Where(item => item.SupersededUtc == null)
            .ToListAsync(cancellationToken);

        var directChanges = await GetDirectContractChangesAsync(
            targetContracts,
            activeContracts,
            cancellationToken);
        var affectedKinds = ExpandAffectedContractKinds(targetContracts, directChanges);
        var affectedContracts = targetContracts
            .Where(item => affectedKinds.Contains(item.DerivationKind))
            .ToArray();
        var materializedAffectedContracts = affectedContracts
            .Where(item => item.RequiresHistoricalWork)
            .ToArray();
        var earliestPhase = materializedAffectedContracts
            .OrderBy(item => LegendConnectDerivationContracts.PhaseRank(item.EarliestPhase))
            .Select(item => item.EarliestPhase)
            .FirstOrDefault();
        var artifacts = await CountCanonicalArtifactsAsync(cancellationToken);
        // The contract declaration, rather than an evaluator number or a
        // feature-specific branch, determines whether retained canonical
        // identities need the compact dependency inventory on first use.
        var dependencyInventoryRequired = baselineVersion > 0 && artifacts.Total > 0 &&
            targetContracts.Any(item => item.RequiresDependencyInventory) &&
            !await _db.Set<LegendLanguageDerivationArtifact>()
                .AnyAsync(cancellationToken);
        var affectedArtifacts = materializedAffectedContracts.Length == 0
            ? 0
            : CountAffectedArtifacts(
                materializedAffectedContracts
                    .SelectMany(item => item.ArtifactKinds)
                    .ToHashSet(StringComparer.Ordinal),
                artifacts);
        var reusedArtifacts = artifacts.Total - affectedArtifacts;

        await PersistCurrentContractsAsync(
            targetContracts,
            affectedContracts,
            cancellationToken);
        var convergence = await UpsertConvergenceAsync(
            evaluatorVersion,
            baselineVersion,
            directChanges.Count,
            targetContracts.Count - directChanges.Count,
            artifacts.Total,
            reusedArtifacts,
            affectedArtifacts,
            earliestPhase,
            dependencyInventoryRequired,
            cancellationToken);

        var now = DateTime.UtcNow;
        policy.TargetLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
        policy.LanguageIntelligenceReevaluationCursor = null;
        policy.LanguageIntelligenceReevaluationStartedUtc = now;
        policy.UpdatedUtc = now;
        if (dependencyInventoryRequired)
        {
            // A pre-contract completed evaluator must first receive its
            // bounded dependency ledger. This is not SourceFamilies replay:
            // the canonical curriculum and all maturity/eligibility rows are
            // only read and mapped to their existing stable identities.
            policy.LanguageIntelligenceReevaluationPhase =
                LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory;
            policy.LanguageIntelligenceReevaluationCompletedUtc = null;
            convergence.State = "Queued";
            convergence.BlockingDependencyIdentity = "derivation-dependency-inventory";
            convergence.UpdatedUtc = now;
        }
        else if (earliestPhase is null)
        {
            // Runtime-only contracts (such as Stage 6 governed content
            // binding) create no historical canonical artifact. They are
            // immediately current after their existing dependencies have
            // already converged, with the durable convergence record exposing
            // that all prior artifacts were reused.
            policy.CompletedLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
            policy.LanguageIntelligenceReevaluationPhase = LegendConnectLanguageIntelligenceReevaluationPhases.Complete;
            policy.LanguageIntelligenceReevaluationCompletedUtc = now;
            convergence.State = "Reused";
            convergence.CompletedUtc = now;
            convergence.UpdatedUtc = now;
        }
        else
        {
            policy.LanguageIntelligenceReevaluationPhase = earliestPhase;
            policy.LanguageIntelligenceReevaluationCompletedUtc = null;
            convergence.State = "Queued";
            convergence.UpdatedUtc = now;
        }
        await _db.SaveChangesAsync(cancellationToken);
        return ToReevaluationSnapshot(policy);
    }

    private async Task RewindIncompleteConvergenceToEarliestFrontierAsync(
        LegendConnectRuntimePolicy policy,
        int evaluatorVersion,
        CancellationToken cancellationToken)
    {
        var convergence = await _db.Set<LegendLanguageDerivationConvergence>()
            .SingleOrDefaultAsync(item => item.TargetEvaluatorVersion == evaluatorVersion,
                cancellationToken);
        // Seeding advances this projection with ExecuteUpdate, which does not
        // refresh an entity already tracked by this worker scope. Reload before
        // deciding whether a rewind remains eligible so the database state,
        // rather than a stale queued instance, is authoritative.
        if (convergence is not null && _db.Database.IsRelational())
            await _db.Entry(convergence).ReloadAsync(cancellationToken);

        // Rewind is an adoption action for a newly queued convergence plan.
        // Once seeding has moved the plan to Processing, completed phases are
        // authoritative and the worker must continue forward instead of
        // cycling back to the original earliest frontier on every heartbeat.
        if (convergence is null ||
            !string.Equals(convergence.State, "Queued", StringComparison.Ordinal) ||
            policy.CompletedLanguageIntelligenceEvaluatorVersion < evaluatorVersion ||
            string.IsNullOrWhiteSpace(convergence.EarliestAffectedPhase) ||
            !LegendConnectLanguageIntelligenceReevaluationPhases.IsWorkPhase(
                convergence.EarliestAffectedPhase))
        {
            return;
        }

        var currentRank = LegendConnectDerivationContracts.PhaseRank(
            policy.LanguageIntelligenceReevaluationPhase);
        var requiredRank = LegendConnectDerivationContracts.PhaseRank(
            convergence.EarliestAffectedPhase);
        if (requiredRank >= currentRank)
            return;

        var now = DateTime.UtcNow;
        policy.LanguageIntelligenceReevaluationPhase = convergence.EarliestAffectedPhase;
        policy.LanguageIntelligenceReevaluationCursor = null;
        policy.LanguageIntelligenceReevaluationCompletedUtc = null;
        policy.UpdatedUtc = now;
        convergence.State = "Queued";
        convergence.CompletedUtc = null;
        convergence.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureContractDeclarationsAsync(
        IReadOnlyList<LegendConnectDerivationContractDefinition> declarations,
        CancellationToken cancellationToken)
    {
        var existingIdentities = await _db.Set<LegendLanguageDerivationContract>()
            .Select(item => item.ContractIdentity)
            .ToHashSetAsync(cancellationToken);
        var changed = false;
        foreach (var declaration in declarations)
        {
            if (existingIdentities.Contains(declaration.ContractIdentity))
                continue;
            _db.Set<LegendLanguageDerivationContract>().Add(NewContract(declaration));
            existingIdentities.Add(declaration.ContractIdentity);
            changed = true;
        }
        if (changed)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task PersistCurrentContractsAsync(
        IReadOnlyList<LegendConnectDerivationContractDefinition> targetContracts,
        IReadOnlyList<LegendConnectDerivationContractDefinition> affectedContracts,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var supersededContractIdentities = new List<string>();
        var allContracts = await _db.Set<LegendLanguageDerivationContract>().ToListAsync(cancellationToken);
        var byIdentity = allContracts.ToDictionary(item => item.ContractIdentity, StringComparer.Ordinal);
        var changed = false;
        foreach (var definition in targetContracts)
        {
            // A prior deployment may have written the newly declared identity
            // as Current without replaying its artifacts. Supersede every
            // other active identity for this derivation kind, not merely an
            // arbitrary newest row, so duplicate Current rows cannot hide a
            // stale projection on the next comparison.
            foreach (var active in allContracts.Where(item =>
                         item.SupersededUtc == null &&
                         string.Equals(item.DerivationKind, definition.DerivationKind, StringComparison.Ordinal) &&
                         !string.Equals(item.ContractIdentity, definition.ContractIdentity, StringComparison.Ordinal)))
            {
                active.State = "Superseded";
                active.SupersededUtc = now;
                active.UpdatedUtc = now;
                supersededContractIdentities.Add(active.ContractIdentity);
                changed = true;
            }

            if (!byIdentity.TryGetValue(definition.ContractIdentity, out var current))
            {
                current = NewContract(definition);
                _db.Set<LegendLanguageDerivationContract>().Add(current);
                byIdentity.Add(current.ContractIdentity, current);
                changed = true;
            }
            else if (current.SupersededUtc is not null || current.State != "Current")
            {
                current.SupersededUtc = null;
                current.State = "Current";
                current.UpdatedUtc = now;
                changed = true;
            }
        }
        if (changed)
            await _db.SaveChangesAsync(cancellationToken);

        // The dependency ledger records freshness without touching canonical
        // evidence.  Mark the complete affected contract frontier stale: a
        // direct source projection change invalidates each declared dependent
        // phase, but the existing leased worker is still the only authority
        // allowed to rebuild their projections.
        var affectedArtifactKinds = affectedContracts
            .SelectMany(item => item.ArtifactKinds)
            .ToHashSet(StringComparer.Ordinal);
        if (supersededContractIdentities.Count > 0 || affectedArtifactKinds.Count > 0)
        {
            var staleArtifacts = await _db.Set<LegendLanguageDerivationArtifact>()
                .Where(item => item.State == "Current" &&
                    (supersededContractIdentities.Contains(item.DerivationContractIdentity) ||
                     affectedArtifactKinds.Contains(item.ArtifactKind)))
                .ToListAsync(cancellationToken);
            foreach (var artifact in staleArtifacts)
            {
                artifact.State = "Stale";
                artifact.UpdatedUtc = now;
            }
            if (staleArtifacts.Count > 0)
                await _db.SaveChangesAsync(cancellationToken);
        }

        // Dependency rows are immutable contract provenance. Existing rows
        // remain historical; a new contract identity receives its exact
        // direct declarations once, independent of evaluator replay work.
        var dependencies = await _db.Set<LegendLanguageDerivationContractDependency>()
            .Select(item => new { item.DependentContractId, item.DependencyDerivationKind, item.DependencyContractIdentity })
            .ToListAsync(cancellationToken);
        var dependencyChanged = false;
        foreach (var definition in targetContracts)
        {
            var contract = byIdentity[definition.ContractIdentity];
            foreach (var dependencyKind in definition.DependencyKinds)
            {
                var dependency = targetContracts.Single(item =>
                    string.Equals(item.DerivationKind, dependencyKind, StringComparison.Ordinal));
                if (dependencies.Any(item => item.DependentContractId == contract.Id &&
                    item.DependencyDerivationKind == dependencyKind &&
                    item.DependencyContractIdentity == dependency.ContractIdentity))
                {
                    continue;
                }
                _db.Set<LegendLanguageDerivationContractDependency>().Add(new()
                {
                    Id = Guid.NewGuid(),
                    DependentContractId = contract.Id,
                    DependencyDerivationKind = dependencyKind,
                    DependencyContractIdentity = dependency.ContractIdentity,
                    CreatedUtc = now
                });
                dependencyChanged = true;
            }
        }
        if (dependencyChanged)
            await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Determines whether the durable artifact ledger proves that the active
    /// projection was produced under the deployment declaration.  Contract
    /// rows alone are insufficient: an interrupted older deployment can have
    /// recorded a new Current declaration while every retained artifact still
    /// carries the prior contract identity.
    /// </summary>
    private async Task<bool> HasDerivationContractDriftAsync(
        int evaluatorVersion,
        CancellationToken cancellationToken)
    {
        var targetContracts = LegendConnectDerivationContracts.ForEvaluator(evaluatorVersion);
        var activeContracts = await _db.Set<LegendLanguageDerivationContract>()
            .Where(item => item.SupersededUtc == null)
            .ToListAsync(cancellationToken);
        return (await GetDirectContractChangesAsync(
            targetContracts,
            activeContracts,
            cancellationToken)).Count > 0;
    }

    private async Task<IReadOnlyList<LegendConnectDerivationContractDefinition>>
        GetDirectContractChangesAsync(
            IReadOnlyList<LegendConnectDerivationContractDefinition> targetContracts,
            IReadOnlyList<LegendLanguageDerivationContract> activeContracts,
            CancellationToken cancellationToken)
    {
        var knownContracts = await _db.Set<LegendLanguageDerivationContract>()
            .AsNoTracking()
            .Select(item => new { item.DerivationKind, item.ContractIdentity })
            .ToListAsync(cancellationToken);
        var kindByIdentity = knownContracts
            .GroupBy(item => item.ContractIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.DerivationKind)
                    .Distinct(StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var currentArtifactContractIdentities = await _db.Set<LegendLanguageDerivationArtifact>()
            .AsNoTracking()
            .Where(item => item.State == "Current")
            .Select(item => item.DerivationContractIdentity)
            .Distinct()
            .ToListAsync(cancellationToken);

        return targetContracts
            .Where(definition =>
                !activeContracts.Any(item =>
                    string.Equals(item.DerivationKind, definition.DerivationKind, StringComparison.Ordinal) &&
                    string.Equals(item.ContractIdentity, definition.ContractIdentity, StringComparison.Ordinal)) ||
                currentArtifactContractIdentities.Any(identity =>
                    !string.Equals(identity, definition.ContractIdentity, StringComparison.Ordinal) &&
                    ((kindByIdentity.TryGetValue(identity, out var kinds) &&
                      kinds.Contains(definition.DerivationKind)) ||
                     // A failed/interrupted predecessor can leave a valid
                     // old artifact identity without a corresponding durable
                     // contract declaration.  The immutable deployment
                     // catalog still knows that identity's kind, so do not
                     // incorrectly reuse it merely because C4 recorded the
                     // newer declaration first.
                     LegendConnectDerivationContracts
                         .KnownContractIdentitiesFor(definition.DerivationKind)
                         .Contains(identity))))
            .ToArray();
    }

    private async Task<LegendLanguageDerivationConvergence> UpsertConvergenceAsync(
        int targetEvaluatorVersion,
        int baselineEvaluatorVersion,
        int changedContractCount,
        int reusedContractCount,
        long existingArtifacts,
        long reusedArtifacts,
        long affectedArtifacts,
        string? earliestPhase,
        bool requiresDependencyInventory,
        CancellationToken cancellationToken)
    {
        var existing = await _db.Set<LegendLanguageDerivationConvergence>()
            .SingleOrDefaultAsync(item => item.TargetEvaluatorVersion == targetEvaluatorVersion, cancellationToken);
        if (existing is null)
        {
            existing = new LegendLanguageDerivationConvergence
            {
                Id = Guid.NewGuid(),
                TargetEvaluatorVersion = targetEvaluatorVersion,
                CreatedUtc = DateTime.UtcNow
            };
            _db.Set<LegendLanguageDerivationConvergence>().Add(existing);
        }
        existing.BaselineEvaluatorVersion = baselineEvaluatorVersion;
        existing.EarliestAffectedPhase = earliestPhase;
        existing.ChangedContractCount = changedContractCount;
        existing.ReusedContractCount = reusedContractCount;
        existing.ExistingCanonicalArtifactCount = existingArtifacts;
        existing.ReusedCanonicalArtifactCount = reusedArtifacts;
        existing.AffectedCanonicalArtifactCount = affectedArtifacts;
        existing.RequiresDependencyInventory = requiresDependencyInventory;
        existing.DependencyInventoryWorkItemCount = 0;
        existing.PlannedWorkItemCount = 0;
        existing.BlockingDependencyIdentity = earliestPhase is null
            ? null
            : "derivation-contract-phase:" + earliestPhase;
        existing.UpdatedUtc = DateTime.UtcNow;
        return existing;
    }

    private static HashSet<string> ExpandAffectedContractKinds(
        IReadOnlyList<LegendConnectDerivationContractDefinition> contracts,
        IReadOnlyList<LegendConnectDerivationContractDefinition> directChanges)
    {
        var affected = directChanges.Select(item => item.DerivationKind)
            .ToHashSet(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var contract in contracts)
            {
                if (affected.Contains(contract.DerivationKind) ||
                    !contract.DependencyKinds.Any(affected.Contains))
                {
                    continue;
                }
                affected.Add(contract.DerivationKind);
                changed = true;
            }
        }
        return affected;
    }

    private static LegendLanguageDerivationContract NewContract(
        LegendConnectDerivationContractDefinition definition) => new()
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
    };

    private async Task<CanonicalArtifactCounts> CountCanonicalArtifactsAsync(CancellationToken cancellationToken)
    {
        var anchors = await _db.Set<LegendLanguageCompositionalAnchor>()
            .LongCountAsync(item => item.SupersededUtc == null, cancellationToken);
        var nodes = await _db.Set<LegendLanguageMeaningNodeEvidence>()
            .LongCountAsync(item => item.SupersededUtc == null, cancellationToken);
        var relations = await _db.Set<LegendLanguageMeaningRelationEvidence>()
            .LongCountAsync(item => item.SupersededUtc == null, cancellationToken);
        var transformations = await _db.Set<LegendSemanticTransitionEvidence>()
            .LongCountAsync(item => item.SupersededUtc == null, cancellationToken);
        var alignments = await _db.Set<LegendTranslationAlignment>()
            .LongCountAsync(item => item.SupersededUtc == null, cancellationToken);
        var provider = await _db.Set<LegendTranslationQualityEvidence>()
            .LongCountAsync(item => item.SupersededUtc == null, cancellationToken);
        var operational = await _db.MessageTranslations.LongCountAsync(cancellationToken);
        return new(anchors, nodes, relations, transformations, alignments, provider, operational);
    }

    private static long CountAffectedArtifacts(
        IReadOnlySet<string> artifactKinds,
        CanonicalArtifactCounts counts) => artifactKinds.Sum(counts.ForArtifactKind);

    private sealed record CanonicalArtifactCounts(
        long Anchors,
        long MeaningNodes,
        long MeaningRelations,
        long Transformations,
        long Alignments,
        long ProviderEvidence,
        long OperationalTranslations)
    {
        internal long Total => Anchors + MeaningNodes + MeaningRelations + Transformations +
            Alignments + ProviderEvidence + OperationalTranslations;

        internal long ForArtifactKind(string artifactKind) => artifactKind switch
        {
            "compositional-anchor" => Anchors,
            "meaning-node" => MeaningNodes,
            "meaning-relation" => MeaningRelations,
            "semantic-transformation" => Transformations,
            "translation-alignment" => Alignments,
            "provider-observation" => ProviderEvidence,
            "operational-translation" => OperationalTranslations,
            _ => 0
        };
    }

    public async Task AdvanceLanguageIntelligenceReevaluationAsync(
        int evaluatorVersion,
        string phase,
        Guid? lastProcessedId,
        bool phaseComplete,
        CancellationToken cancellationToken = default)
    {
        if (evaluatorVersion <= 0 || !LegendConnectLanguageIntelligenceReevaluationPhases.IsWorkPhase(phase))
            return;

        if (!_db.Database.IsRelational())
        {
            var inMemoryPolicy = await GetTrackedPolicyAsync(cancellationToken);
            if (inMemoryPolicy.TargetLanguageIntelligenceEvaluatorVersion != evaluatorVersion ||
                !string.Equals(inMemoryPolicy.LanguageIntelligenceReevaluationPhase, phase, StringComparison.Ordinal))
            {
                return;
            }

            if (lastProcessedId.HasValue)
                inMemoryPolicy.LanguageIntelligenceReevaluationCursor = lastProcessedId;
            if (phaseComplete)
            {
                inMemoryPolicy.LanguageIntelligenceReevaluationCursor = null;
                inMemoryPolicy.LanguageIntelligenceReevaluationPhase =
                    await ResolveNextReevaluationPhaseAsync(evaluatorVersion, phase, cancellationToken);
                if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory)
                    await MarkDependencyInventoryCompletedAsync(
                        evaluatorVersion,
                        inMemoryPolicy.LanguageIntelligenceReevaluationPhase,
                        cancellationToken);
                if (inMemoryPolicy.LanguageIntelligenceReevaluationPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete)
                {
                    inMemoryPolicy.CompletedLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
                    inMemoryPolicy.LanguageIntelligenceReevaluationCompletedUtc = DateTime.UtcNow;
                    await CompleteDerivationConvergenceAsync(evaluatorVersion, cancellationToken);
                }
            }

            inMemoryPolicy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        // Progress and adoption share the singleton policy lock. A detached
        // snapshot plus ExecuteUpdate can report success while a concurrent
        // transaction has already changed the row, which leaves a completed
        // durable phase stranded. Hold the policy row for the full guarded
        // transition instead: either this owner advances exactly its current
        // phase or it observes another owner and does nothing.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        try
        {
            var policy = await GetLockedLanguageIntelligencePolicyAsync(cancellationToken);
            if (policy.TargetLanguageIntelligenceEvaluatorVersion != evaluatorVersion ||
                !string.Equals(policy.LanguageIntelligenceReevaluationPhase, phase, StringComparison.Ordinal))
            {
                if (ownsTransaction)
                    await transaction!.RollbackAsync(CancellationToken.None);
                return;
            }

            var now = DateTime.UtcNow;
            var nextPhase = phase;
            if (lastProcessedId.HasValue)
                policy.LanguageIntelligenceReevaluationCursor = lastProcessedId;
            if (phaseComplete)
            {
                nextPhase = await ResolveNextReevaluationPhaseAsync(evaluatorVersion, phase, cancellationToken);
                policy.LanguageIntelligenceReevaluationCursor = null;
                policy.LanguageIntelligenceReevaluationPhase = nextPhase;
                if (nextPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete)
                {
                    policy.CompletedLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
                    policy.LanguageIntelligenceReevaluationCompletedUtc = now;
                }
            }
            policy.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);

            if (phaseComplete && phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory)
                await MarkDependencyInventoryCompletedAsync(evaluatorVersion, nextPhase, cancellationToken);
            if (phaseComplete && nextPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete)
                await CompleteDerivationConvergenceAsync(evaluatorVersion, cancellationToken);

            if (ownsTransaction)
                await transaction!.CommitAsync(cancellationToken);
        }
        catch
        {
            if (ownsTransaction)
                await transaction!.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<string> ResolveNextReevaluationPhaseAsync(
        int evaluatorVersion,
        string phase,
        CancellationToken cancellationToken)
    {
        if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory)
        {
            var convergence = await _db.Set<LegendLanguageDerivationConvergence>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.TargetEvaluatorVersion == evaluatorVersion, cancellationToken);
            return convergence?.EarliestAffectedPhase ??
                LegendConnectLanguageIntelligenceReevaluationPhases.Complete;
        }

        return phase switch
        {
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies =>
                LegendConnectLanguageIntelligenceReevaluationPhases.Alignments,
            LegendConnectLanguageIntelligenceReevaluationPhases.Alignments =>
                LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations =>
                LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations,
            _ => LegendConnectLanguageIntelligenceReevaluationPhases.Complete
        };
    }

    private async Task MarkDependencyInventoryCompletedAsync(
        int evaluatorVersion,
        string nextPhase,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var query = _db.Set<LegendLanguageDerivationConvergence>()
            .Where(item => item.TargetEvaluatorVersion == evaluatorVersion &&
                item.RequiresDependencyInventory);
        if (_db.Database.IsRelational())
        {
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.RequiresDependencyInventory, false)
                .SetProperty(item => item.State,
                    nextPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete
                        ? "Completed"
                        : "Queued")
                .SetProperty(item => item.CompletedUtc,
                    nextPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete
                        ? now
                        : (DateTime?)null)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return;
        }

        var convergence = await query.SingleOrDefaultAsync(cancellationToken);
        if (convergence is null)
            return;
        convergence.RequiresDependencyInventory = false;
        convergence.State = nextPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete
            ? "Completed"
            : "Queued";
        convergence.CompletedUtc = nextPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete
            ? now
            : null;
        convergence.UpdatedUtc = now;
    }

    private async Task CompleteDerivationConvergenceAsync(
        int evaluatorVersion,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var query = _db.Set<LegendLanguageDerivationConvergence>()
            .Where(item => item.TargetEvaluatorVersion == evaluatorVersion &&
                (item.State == "Queued" || item.State == "Processing"));
        if (_db.Database.IsRelational())
        {
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, "Completed")
                .SetProperty(item => item.CompletedUtc, now)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            return;
        }

        var convergence = await query.SingleOrDefaultAsync(cancellationToken);
        if (convergence is null)
            return;
        convergence.State = "Completed";
        convergence.CompletedUtc = now;
        convergence.UpdatedUtc = now;
    }

    public async Task<IReadOnlyList<LegendConnectFounderOperationalAuditSnapshot>> GetRecentAuditAsync(
        int take = 30,
        CancellationToken cancellationToken = default) =>
        await _db.Set<LegendConnectKnowledgeAuditEntry>().AsNoTracking()
            .Where(item => item.Action.StartsWith("RuntimePolicy") ||
                           item.Action.StartsWith("ContextualCompositionMode") ||
                           item.Action.StartsWith("AutonomousAcquisition") ||
                           item.Action.StartsWith("FounderAutonomousLanguageFocus"))
            .OrderByDescending(item => item.OccurredUtc)
            .Take(Math.Clamp(take, 1, 100))
            .Select(item => new LegendConnectFounderOperationalAuditSnapshot(
                item.FounderUserId, item.Action, item.Result, item.LanguageCode,
                item.PairKey, item.Detail, item.OccurredUtc))
            .ToListAsync(cancellationToken);

    public async Task RecordWorkerHeartbeatAsync(
        string worker,
        CancellationToken cancellationToken = default)
    {
        var normalized = worker?.Trim();
        if (normalized is not ("Learning" or "Acquisition"))
            return;
        try
        {
            var now = DateTime.UtcNow;
            if (!_db.Database.IsRelational())
            {
                var inMemoryPolicy = await GetTrackedPolicyAsync(cancellationToken);
                if (normalized == "Learning")
                    inMemoryPolicy.LastLearningWorkerHeartbeatUtc = now;
                else
                    inMemoryPolicy.LastAcquisitionWorkerHeartbeatUtc = now;
                inMemoryPolicy.UpdatedUtc = now;
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            var heartbeatQuery = _db.Set<LegendConnectRuntimePolicy>()
                .Where(item => item.ScopeKey == GlobalScope);
            var affected = normalized == "Learning"
                ? await heartbeatQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LastLearningWorkerHeartbeatUtc, now)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken)
                : await heartbeatQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LastAcquisitionWorkerHeartbeatUtc, now)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            if (affected == 1)
                return;

            var policy = await GetTrackedPolicyAsync(cancellationToken);
            if (normalized == "Learning")
                policy.LastLearningWorkerHeartbeatUtc = now;
            else
                policy.LastAcquisitionWorkerHeartbeatUtc = now;
            policy.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Legend Connect worker heartbeat could not be persisted. Worker={Worker}", normalized);
        }
    }

    private async Task<LegendConnectRuntimePolicy> GetTrackedPolicyAsync(CancellationToken cancellationToken)
    {
        var policy = await _db.Set<LegendConnectRuntimePolicy>()
            .SingleOrDefaultAsync(item => item.ScopeKey == GlobalScope, cancellationToken);
        if (policy is not null)
            return policy;

        var bootstrap = BootstrapPolicy();
        policy = new LegendConnectRuntimePolicy
        {
            Id = Guid.NewGuid(),
            ScopeKey = GlobalScope,
            MonthlyProviderCapacityCharacters = bootstrap.MonthlyProviderCapacityCharacters,
            LiveTranslationReserveCharacters = bootstrap.LiveTranslationReserveCharacters,
            MaximumSafeCorpusConsumptionCharacters = bootstrap.MaximumSafeCorpusConsumptionCharacters,
            CorpusAcquisitionEnabled = bootstrap.CorpusAcquisitionEnabled,
            LearningEnabled = bootstrap.LearningEnabled,
            ContextualCompositionMode = bootstrap.ContextualCompositionMode,
            ContextualMinimumConfidence = bootstrap.ContextualMinimumConfidence,
            UpdatedUtc = DateTime.UtcNow
        };
        _db.Set<LegendConnectRuntimePolicy>().Add(policy);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return policy;
        }
        catch (DbUpdateException)
        {
            _db.Entry(policy).State = EntityState.Detached;
            return await _db.Set<LegendConnectRuntimePolicy>()
                .SingleAsync(item => item.ScopeKey == GlobalScope, cancellationToken);
        }
    }

    private async Task<LegendConnectRuntimePolicy> GetLanguageIntelligencePolicyAsync(
        CancellationToken cancellationToken)
    {
        var policy = await _db.Set<LegendConnectRuntimePolicy>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.ScopeKey == GlobalScope, cancellationToken);
        if (policy is not null)
            return policy;

        _ = await GetTrackedPolicyAsync(cancellationToken);
        return await _db.Set<LegendConnectRuntimePolicy>().AsNoTracking()
            .SingleAsync(item => item.ScopeKey == GlobalScope, cancellationToken);
    }

    /// <summary>
    /// Returns the singleton runtime policy as a tracked row while the
    /// caller owns a serializable transaction. SQL Server uses an update lock
    /// so an adoption planner cannot race another planner or a phase advance
    /// from a different application instance. Other relational providers
    /// still receive the serializable transaction; the model itself contains
    /// no provider-specific replay behavior.
    /// </summary>
    private async Task<LegendConnectRuntimePolicy> GetLockedLanguageIntelligencePolicyAsync(
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsSqlServer())
        {
            return await _db.Set<LegendConnectRuntimePolicy>()
                .FromSqlInterpolated($"SELECT * FROM [LegendConnectRuntimePolicies] WITH (UPDLOCK, HOLDLOCK) WHERE [ScopeKey] = {GlobalScope}")
                .SingleAsync(cancellationToken);
        }

        return await _db.Set<LegendConnectRuntimePolicy>()
            .SingleAsync(item => item.ScopeKey == GlobalScope, cancellationToken);
    }

    private LegendConnectRuntimePolicySnapshot BootstrapPolicy()
    {
        var capacity = Math.Max(0, _configuration.GetValue<long?>("LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters") ?? 0);
        var reserve = Math.Max(0, _configuration.GetValue<long?>("LegendConnect:Providers:AzureTranslator:LiveReserveCharacters") ?? 0);
        var configuredCorpusMaximum = _configuration.GetValue<long?>("LegendConnect:CorpusAcquisition:MaximumSafeCorpusConsumptionCharacters");
        var corpusMaximum = configuredCorpusMaximum.HasValue
            ? Math.Max(0, configuredCorpusMaximum.Value)
            : Math.Max(0, capacity - reserve);
        return new LegendConnectRuntimePolicySnapshot(
            false,
            capacity,
            reserve,
            corpusMaximum,
            _configuration.GetValue<bool>("LegendConnect:CorpusAcquisition:Enabled"),
            _configuration.GetValue<bool?>("LegendConnect:Learning:Enabled") ?? true,
            NormalizeContextualMode(_configuration["LegendConnect:ContextualComposition:Mode"]),
            Math.Clamp(_configuration.GetValue<decimal?>("LegendConnect:ContextualComposition:MinimumConfidence") ?? 0.98m, 0.90m, 1m),
            null, null, DateTime.MinValue);
    }

    private static LegendConnectRuntimePolicySnapshot ToSnapshot(LegendConnectRuntimePolicy policy, bool persisted) => new(
        persisted,
        Math.Max(0, policy.MonthlyProviderCapacityCharacters),
        Math.Max(0, policy.LiveTranslationReserveCharacters),
        Math.Max(0, policy.MaximumSafeCorpusConsumptionCharacters),
        policy.CorpusAcquisitionEnabled,
        policy.LearningEnabled,
        NormalizeContextualMode(policy.ContextualCompositionMode),
        Math.Clamp(policy.ContextualMinimumConfidence, 0.90m, 1m),
        policy.LastLearningWorkerHeartbeatUtc,
        policy.LastAcquisitionWorkerHeartbeatUtc,
        policy.UpdatedUtc);

    private static LegendConnectLanguageIntelligenceReevaluationSnapshot ToReevaluationSnapshot(
        LegendConnectRuntimePolicy policy) => new(
        policy.TargetLanguageIntelligenceEvaluatorVersion,
        policy.CompletedLanguageIntelligenceEvaluatorVersion,
        policy.CursorReplayCompatibilityEvaluatorVersion,
        policy.LanguageIntelligenceReevaluationPhase,
        policy.LanguageIntelligenceReevaluationCursor,
        policy.LanguageIntelligenceReevaluationStartedUtc,
        policy.LanguageIntelligenceReevaluationCompletedUtc);

    private async Task<LegendConnectRuntimePolicySnapshot> ToEffectiveSnapshotAsync(
        LegendConnectRuntimePolicy policy,
        bool persisted,
        CancellationToken cancellationToken)
    {
        var focusedTargetLanguageCodes = await GetFocusedTargetLanguageCodesAsync(policy.Id, cancellationToken);
        return ToSnapshot(policy, persisted) with { FocusedTargetLanguageCodes = focusedTargetLanguageCodes };
    }

    private async Task<string> RequireFounderAsync(string founderUserId, CancellationToken cancellationToken)
    {
        var founder = Optional(founderUserId, 450)?.ToLowerInvariant();
        if (founder is null || !await _access.IsFounderManagerAsync(
                new MessagingActor(founder, MessagingParticipantTypes.Agent), cancellationToken))
            throw new UnauthorizedAccessException("Founder authority is required to manage Legend Connect runtime policy.");
        return founder;
    }

    private static void Validate(LegendConnectRuntimePolicyMutation mutation)
    {
        if (mutation.MonthlyProviderCapacityCharacters < 0 || mutation.LiveTranslationReserveCharacters < 0 ||
            mutation.MaximumSafeCorpusConsumptionCharacters < 0)
            throw new ArgumentException("Capacity values cannot be negative.", nameof(mutation));
        if (mutation.MonthlyProviderCapacityCharacters == 0 &&
            (mutation.LiveTranslationReserveCharacters != 0 || mutation.MaximumSafeCorpusConsumptionCharacters != 0))
            throw new ArgumentException("A zero provider capacity requires zero reserve and zero corpus capacity.", nameof(mutation));
        if (mutation.MonthlyProviderCapacityCharacters > 0 &&
            mutation.LiveTranslationReserveCharacters >= mutation.MonthlyProviderCapacityCharacters)
            throw new ArgumentException("Live reserve must be lower than monthly provider capacity.", nameof(mutation));
        if (mutation.MaximumSafeCorpusConsumptionCharacters >
            Math.Max(0, mutation.MonthlyProviderCapacityCharacters - mutation.LiveTranslationReserveCharacters))
            throw new ArgumentException("Corpus consumption cannot enter the protected live reserve.", nameof(mutation));
        if (mutation.ContextualMinimumConfidence is < 0.90m or > 1m)
            throw new ArgumentException("Contextual confidence must remain between 0.90 and 1.00.", nameof(mutation));
        _ = NormalizeContextualMode(mutation.ContextualCompositionMode);
    }

    private async Task<IReadOnlyList<string>> NormalizeFocusTargetLanguageCodesAsync(
        IReadOnlyCollection<string>? targetLanguageCodes,
        CancellationToken cancellationToken)
    {
        var requested = targetLanguageCodes?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        if (requested.Length == 0)
            throw new ArgumentException("Select at least one target language before turning acquisition focus on.", nameof(targetLanguageCodes));

        var normalized = new List<string>(requested.Length);
        foreach (var requestedLanguage in requested)
        {
            var language = await _languages.GetLanguageAsync(requestedLanguage, cancellationToken);
            if (language is null || !language.IsLearningEnabled ||
                string.Equals(language.Code, "en", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Each focus choice must be an enabled learning target language other than English.",
                    nameof(targetLanguageCodes));
            }

            normalized.Add(language.Code);
        }

        return normalized
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> GetFocusedTargetLanguageCodesAsync(
        Guid runtimePolicyId,
        CancellationToken cancellationToken) =>
        await _db.Set<LegendConnectAutonomousLanguageFocus>().AsNoTracking()
            .Where(item => item.RuntimePolicyId == runtimePolicyId)
            .OrderBy(item => item.TargetLanguageCode)
            .Select(item => item.TargetLanguageCode)
            .ToListAsync(cancellationToken);

    private async Task ClearAutonomousLanguageFocusAsync(
        Guid runtimePolicyId,
        CancellationToken cancellationToken)
    {
        var existing = await _db.Set<LegendConnectAutonomousLanguageFocus>()
            .Where(item => item.RuntimePolicyId == runtimePolicyId)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
            _db.RemoveRange(existing);
    }

    private async Task<(long Approved, long PendingEligible, long RejectedOrIneligible, long Deduplicated, long AwaitingKnowledgePairs)> CandidateReadinessAsync(
        CancellationToken cancellationToken)
    {
        var candidates = await _db.Set<LegendCorpusCandidate>().AsNoTracking().ToListAsync(cancellationToken);
        var approved = candidates.LongCount(item => item.IsApproved);
        var pending = candidates.LongCount(item => item.IsApproved && item.ProcessingState is "Pending" or "Processing");
        var rejected = candidates.LongCount(item => !item.IsApproved || item.ProcessingState == "Rejected");
        var deduplicated = candidates.LongCount(item => item.ProcessingState == "Deduplicated");
        var awaitingPairs = candidates.Where(item => item.IsApproved && item.ProcessingState is "Pending" or "Processing")
            .Select(item => LegendLanguageIdentity.PairKey(item.SourceLanguageCode, item.TargetLanguageCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .LongCount();
        return (approved, pending, rejected, deduplicated, awaitingPairs);
    }

    private async Task<bool> DatabaseReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _db.Set<LegendConnectRuntimePolicy>().AsNoTracking().Take(1).ToListAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Legend Connect runtime schema readiness check failed.");
            return false;
        }
    }

    private bool IsAzureProviderConfigured()
    {
        var endpoint = (_configuration["AzureTranslator:Endpoint"] ?? string.Empty).Trim();
        var key = (_configuration["AzureTranslator:Key"] ?? Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_KEY") ?? string.Empty).Trim();
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
               !string.IsNullOrWhiteSpace(key);
    }

    private async Task WritePolicyAuditAsync(
        string founder,
        string action,
        LegendConnectRuntimePolicySnapshot before,
        LegendConnectRuntimePolicySnapshot after,
        string? language,
        string? pair,
        CancellationToken cancellationToken)
    {
        var detail = string.Join("; ", new[]
        {
            Change("monthly provider capacity", before.MonthlyProviderCapacityCharacters, after.MonthlyProviderCapacityCharacters),
            Change("live reserve", before.LiveTranslationReserveCharacters, after.LiveTranslationReserveCharacters),
            Change("safe corpus limit", before.MaximumSafeCorpusConsumptionCharacters, after.MaximumSafeCorpusConsumptionCharacters),
            Change("acquisition", before.CorpusAcquisitionEnabled, after.CorpusAcquisitionEnabled),
            Change("learning", before.LearningEnabled, after.LearningEnabled),
            Change("context mode", before.ContextualCompositionMode, after.ContextualCompositionMode),
            Change("context confidence", before.ContextualMinimumConfidence, after.ContextualMinimumConfidence),
            Change("acquisition focus", FocusSummary(before), FocusSummary(after))
        }.Where(item => item is not null));
        await WriteSimpleAuditAsync(founder, action, "Succeeded", language, pair, detail, cancellationToken);
    }

    private static string FocusSummary(LegendConnectRuntimePolicySnapshot policy) =>
        policy.FocusedTargetLanguageCodes.Count == 0
            ? "Automatic demand-driven"
            : string.Join(", ", policy.FocusedTargetLanguageCodes);

    private async Task WriteSimpleAuditAsync(
        string founder,
        string action,
        string result,
        string? language,
        string? pair,
        string? detail,
        CancellationToken cancellationToken)
    {
        _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
        {
            Id = Guid.NewGuid(),
            FounderUserId = founder,
            Action = action,
            Result = result,
            LanguageCode = Optional(language, 32) ?? string.Empty,
            PairKey = Optional(pair, 72),
            Detail = Optional(detail, 500),
            OccurredUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<T> PersistFounderMutationAsync<T>(
        Func<Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var result = await mutation();
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private static LegendConnectReadinessCheck Check(string name, bool ready, string detail) =>
        new(name, ready ? "READY" : "BLOCKED", detail);

    private static string FirstBlockedDetail(IEnumerable<LegendConnectReadinessCheck> checks, bool learningEnabled) =>
        !learningEnabled ? "Learning is paused by the durable Founder runtime policy." :
        checks.FirstOrDefault(item => item.State == "BLOCKED")?.Detail ?? "Legend Connect is not ready for autonomous acquisition.";

    private static string NormalizeContextualMode(string? mode)
    {
        var normalized = mode?.Trim();
        return normalized?.ToLowerInvariant() switch
        {
            "disabled" => "Disabled",
            "shadow" => "Shadow",
            "active" => "Active",
            _ => throw new ArgumentException("Contextual composition mode must be Disabled, Shadow, or Active.", nameof(mode))
        };
    }

    private static string? Optional(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private static string? Change<T>(string name, T before, T after) where T : notnull =>
        EqualityComparer<T>.Default.Equals(before, after) ? null : $"{name}: {before} → {after}";
}
