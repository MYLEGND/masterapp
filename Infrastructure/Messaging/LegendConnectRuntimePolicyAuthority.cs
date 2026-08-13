using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
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

        var candidates = await CandidateReadinessAsync(cancellationToken);
        var candidateReady = candidates.PendingEligible > 0;
        checks.Add(new LegendConnectReadinessCheck(
            "Approved Corpus",
            candidateReady ? "READY" : "IDLE",
            candidateReady
                ? $"{candidates.PendingEligible:N0} eligible approved candidate(s) await acquisition."
                : "No eligible approved corpus candidate is waiting. Submit source-language-only Founder-approved knowledge to queue missing enabled coverage."));

        var baseReady = databaseReady && providerReady && registryReady && learningWorkerReady &&
                        acquisitionWorkerReady && capacityReady && reserveReady && policy.LearningEnabled;
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

        if (!_db.Database.IsRelational())
        {
            var inMemoryPolicy = await GetTrackedPolicyAsync(cancellationToken);
            if (inMemoryPolicy.CompletedLanguageIntelligenceEvaluatorVersion >= evaluatorVersion &&
                inMemoryPolicy.LanguageIntelligenceReevaluationPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete)
            {
                return ToReevaluationSnapshot(inMemoryPolicy);
            }

            if (inMemoryPolicy.TargetLanguageIntelligenceEvaluatorVersion != evaluatorVersion ||
                !LegendConnectLanguageIntelligenceReevaluationPhases.IsWorkPhase(
                    inMemoryPolicy.LanguageIntelligenceReevaluationPhase))
            {
                inMemoryPolicy.TargetLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
                inMemoryPolicy.LanguageIntelligenceReevaluationPhase = LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies;
                inMemoryPolicy.LanguageIntelligenceReevaluationCursor = null;
                inMemoryPolicy.LanguageIntelligenceReevaluationStartedUtc = DateTime.UtcNow;
                inMemoryPolicy.LanguageIntelligenceReevaluationCompletedUtc = null;
                inMemoryPolicy.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
            }

            return ToReevaluationSnapshot(inMemoryPolicy);
        }

        var policy = await GetLanguageIntelligencePolicyAsync(cancellationToken);
        if (policy.CompletedLanguageIntelligenceEvaluatorVersion >= evaluatorVersion &&
            policy.LanguageIntelligenceReevaluationPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete)
        {
            return ToReevaluationSnapshot(policy);
        }

        var phaseIsValid = LegendConnectLanguageIntelligenceReevaluationPhases.IsWorkPhase(
            policy.LanguageIntelligenceReevaluationPhase);
        if (policy.TargetLanguageIntelligenceEvaluatorVersion != evaluatorVersion || !phaseIsValid)
        {
            var now = DateTime.UtcNow;
            await _db.Set<LegendConnectRuntimePolicy>()
                .Where(item => item.ScopeKey == GlobalScope)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.TargetLanguageIntelligenceEvaluatorVersion, evaluatorVersion)
                    .SetProperty(item => item.LanguageIntelligenceReevaluationPhase,
                        LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies)
                    .SetProperty(item => item.LanguageIntelligenceReevaluationCursor, (Guid?)null)
                    .SetProperty(item => item.LanguageIntelligenceReevaluationStartedUtc, now)
                    .SetProperty(item => item.LanguageIntelligenceReevaluationCompletedUtc, (DateTime?)null)
                    .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
            policy.TargetLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
            policy.LanguageIntelligenceReevaluationPhase = LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies;
            policy.LanguageIntelligenceReevaluationCursor = null;
            policy.LanguageIntelligenceReevaluationStartedUtc = now;
            policy.LanguageIntelligenceReevaluationCompletedUtc = null;
            policy.UpdatedUtc = now;
        }

        return ToReevaluationSnapshot(policy);
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
                inMemoryPolicy.LanguageIntelligenceReevaluationPhase = phase switch
                {
                    LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies =>
                        LegendConnectLanguageIntelligenceReevaluationPhases.Alignments,
                    LegendConnectLanguageIntelligenceReevaluationPhases.Alignments =>
                        LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
                    _ => LegendConnectLanguageIntelligenceReevaluationPhases.Complete
                };
                if (inMemoryPolicy.LanguageIntelligenceReevaluationPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete)
                {
                    inMemoryPolicy.CompletedLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
                    inMemoryPolicy.LanguageIntelligenceReevaluationCompletedUtc = DateTime.UtcNow;
                }
            }

            inMemoryPolicy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var policy = await GetLanguageIntelligencePolicyAsync(cancellationToken);
        // A newer deployment may have already begun another evaluator pass;
        // stale worker pages must not move its durable cursor backward.
        if (policy.TargetLanguageIntelligenceEvaluatorVersion != evaluatorVersion ||
            !string.Equals(policy.LanguageIntelligenceReevaluationPhase, phase, StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var nextPhase = phase;
        var completedVersion = policy.CompletedLanguageIntelligenceEvaluatorVersion;
        var completedUtc = policy.LanguageIntelligenceReevaluationCompletedUtc;
        if (phaseComplete)
        {
            nextPhase = phase switch
            {
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies =>
                    LegendConnectLanguageIntelligenceReevaluationPhases.Alignments,
                LegendConnectLanguageIntelligenceReevaluationPhases.Alignments =>
                    LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
                _ => LegendConnectLanguageIntelligenceReevaluationPhases.Complete
            };
            if (nextPhase == LegendConnectLanguageIntelligenceReevaluationPhases.Complete)
            {
                completedVersion = evaluatorVersion;
                completedUtc = now;
            }
        }

        // Heartbeats update this singleton too. A conditional update avoids a
        // row-version collision after a successful canonical evaluator page.
        var update = _db.Set<LegendConnectRuntimePolicy>()
            .Where(item => item.ScopeKey == GlobalScope &&
                item.TargetLanguageIntelligenceEvaluatorVersion == evaluatorVersion &&
                item.LanguageIntelligenceReevaluationPhase == phase);
        var updated = phaseComplete
            ? await update.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LanguageIntelligenceReevaluationCursor, (Guid?)null)
                .SetProperty(item => item.LanguageIntelligenceReevaluationPhase, nextPhase)
                .SetProperty(item => item.CompletedLanguageIntelligenceEvaluatorVersion, completedVersion)
                .SetProperty(item => item.LanguageIntelligenceReevaluationCompletedUtc, completedUtc)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken)
            : lastProcessedId.HasValue
            ? await update.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LanguageIntelligenceReevaluationCursor, lastProcessedId)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken)
            : await update.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
        _ = updated;
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
