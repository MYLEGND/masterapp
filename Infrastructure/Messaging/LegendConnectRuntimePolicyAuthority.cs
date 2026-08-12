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

    public LegendConnectRuntimePolicyAuthority(
        MasterAppDbContext db,
        IControlledResourceAccessService access,
        ILegendLanguageRegistry languages,
        IConfiguration configuration,
        ILogger<LegendConnectRuntimePolicyAuthority> logger)
    {
        _db = db;
        _access = access;
        _languages = languages;
        _configuration = configuration;
        _logger = logger;
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
        var checks = new List<LegendConnectReadinessCheck>();
        var databaseReady = await DatabaseReadyAsync(cancellationToken);
        checks.Add(Check("Database", databaseReady, databaseReady
            ? "The durable Legend Connect control-plane schema is reachable."
            : "The durable Legend Connect schema is unavailable."));

        var providerReady = IsAzureProviderConfigured();
        checks.Add(Check("Azure Provider", providerReady, providerReady
            ? "A server-configured Azure Translator endpoint and credential are available."
            : "Azure Translator is not configured on this server."));

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

        var capacityReady = policy.MonthlyProviderCapacityCharacters > 0 &&
                            policy.MaximumSafeCorpusConsumptionCharacters > 0;
        checks.Add(Check("Capacity Policy", capacityReady, capacityReady
            ? "Monthly provider and corpus-consumption limits are configured."
            : "Set a positive monthly provider capacity and safe corpus limit."));

        var reserveReady = policy.MonthlyProviderCapacityCharacters > 0 &&
                           policy.LiveTranslationReserveCharacters >= 0 &&
                           policy.LiveTranslationReserveCharacters < policy.MonthlyProviderCapacityCharacters &&
                           policy.MaximumSafeCorpusConsumptionCharacters <=
                           policy.MonthlyProviderCapacityCharacters - policy.LiveTranslationReserveCharacters;
        checks.Add(Check("Live Reserve", reserveReady, reserveReady
            ? "The protected live-translation reserve is valid."
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

    public async Task<LegendConnectRuntimePolicySnapshot> ConfigurePriorityOverrideAsync(
        string founderUserId,
        LegendConnectPriorityOverrideMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var founder = await RequireFounderAsync(founderUserId, cancellationToken);
        var language = string.IsNullOrWhiteSpace(mutation.LanguageCode)
            ? null
            : (await _languages.GetLanguageAsync(mutation.LanguageCode, cancellationToken))?.Code;
        var pair = await NormalizePairAsync(mutation.PairKey, cancellationToken);
        if (language is null && pair is null)
            throw new ArgumentException("Choose an enabled target language or directional pair.", nameof(mutation));
        if (language is not null && pair is not null &&
            !PairContainsLanguage(pair, language))
        {
            throw new ArgumentException("The selected directional pair must include the selected language.", nameof(mutation));
        }

        return await PersistFounderMutationAsync(async () =>
        {
            var policy = await GetTrackedPolicyAsync(cancellationToken);
            var before = ToSnapshot(policy, true);
            policy.PriorityMode = "FounderOverride";
            policy.PriorityLanguageCode = language;
            policy.PriorityPairKey = pair;
            policy.PriorityLevel = null;
            await ClearAutonomousLanguageFocusAsync(policy.Id, cancellationToken);
            policy.UpdatedByUserId = founder;
            policy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var after = ToSnapshot(policy, true);
            await WritePolicyAuditAsync(founder, "FounderPriorityOverrideEnabled", before, after, language, pair, cancellationToken);
            return after;
        }, cancellationToken);
    }

    public async Task<LegendConnectRuntimePolicySnapshot> DisablePriorityOverrideAsync(
        string founderUserId,
        CancellationToken cancellationToken = default)
    {
        var founder = await RequireFounderAsync(founderUserId, cancellationToken);
        return await PersistFounderMutationAsync(async () =>
        {
            var policy = await GetTrackedPolicyAsync(cancellationToken);
            var before = ToSnapshot(policy, true);
            policy.PriorityMode = "Automatic";
            policy.PriorityLanguageCode = null;
            policy.PriorityPairKey = null;
            policy.PriorityLevel = null;
            await ClearAutonomousLanguageFocusAsync(policy.Id, cancellationToken);
            policy.UpdatedByUserId = founder;
            policy.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var after = ToSnapshot(policy, true);
            await WritePolicyAuditAsync(founder, "FounderPriorityOverrideDisabled", before, after, null, null, cancellationToken);
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
                policy.PriorityMode = "FounderOverride";
            }
            else
            {
                policy.PriorityMode = "Automatic";
            }

            // The former single language/pair fields remain only for deployed
            // schema compatibility. The focused set is now the one current
            // Founder-controlled work-order authority.
            policy.PriorityLanguageCode = null;
            policy.PriorityPairKey = null;
            policy.PriorityLevel = null;
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

    public async Task<LegendConnectPriorityProgressSnapshot> GetPriorityProgressAsync(
        CancellationToken cancellationToken = default)
    {
        var policy = await GetEffectiveAsync(cancellationToken);
        if (!string.Equals(policy.PriorityMode, "FounderOverride", StringComparison.OrdinalIgnoreCase))
        {
            return new LegendConnectPriorityProgressSnapshot(
                "AUTOMATIC — DEMAND DRIVEN", 0, 0, 0, 0m, 0, null,
                "The existing demand-and-coverage planner is selecting work normally.");
        }

        var candidatesQuery = _db.Set<LegendCorpusCandidate>().AsNoTracking()
            .Where(item => item.IsApproved);

        if (policy.FocusedTargetLanguageCodes.Count > 0)
        {
            var focusedTargetLanguageCodes = policy.FocusedTargetLanguageCodes.ToArray();
            candidatesQuery = candidatesQuery.Where(item =>
                item.SourceLanguageCode == "en" &&
                focusedTargetLanguageCodes.Contains(item.TargetLanguageCode));
        }
        else if (!string.IsNullOrWhiteSpace(policy.PriorityPairKey))
        {
            var separatorIndex = policy.PriorityPairKey.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= policy.PriorityPairKey.Length - 1)
                throw new InvalidOperationException("Founder priority pair is not a valid directional language pair.");

            var sourceLanguageCode = policy.PriorityPairKey[..separatorIndex];
            var targetLanguageCode = policy.PriorityPairKey[(separatorIndex + 1)..];

            candidatesQuery = candidatesQuery.Where(item =>
                item.SourceLanguageCode == sourceLanguageCode &&
                item.TargetLanguageCode == targetLanguageCode);
        }
        else if (!string.IsNullOrWhiteSpace(policy.PriorityLanguageCode))
        {
            var priorityLanguageCode = policy.PriorityLanguageCode;

            candidatesQuery = candidatesQuery.Where(item =>
                item.SourceLanguageCode == priorityLanguageCode ||
                item.TargetLanguageCode == priorityLanguageCode);
        }
        else
        {
            throw new InvalidOperationException("Founder priority override has no language or directional pair.");
        }

        var candidates = await candidatesQuery.ToListAsync(cancellationToken);
        var pending = candidates.Where(item => item.ProcessingState is "Pending" or "Processing").ToList();
        var eligible = 0L;
        foreach (var candidate in pending)
        {
            var pairKey = LegendLanguageIdentity.PairKey(candidate.SourceLanguageCode, candidate.TargetLanguageCode);
            var sourceId = await _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                .Where(item => item.LanguageCode == candidate.SourceLanguageCode && item.NormalizedHash == candidate.SourceTextHash)
                .Select(item => (Guid?)item.Id)
                .SingleOrDefaultAsync(cancellationToken);
            var alreadyAligned = sourceId.HasValue && await _db.Set<LegendTranslationAlignment>().AsNoTracking()
                .AnyAsync(item => item.PairKey == pairKey && item.SourceTextUnitId == sourceId.Value && item.SupersededUtc == null, cancellationToken);
            if (!alreadyAligned)
                eligible++;
        }

        var pairKeys = candidates.Select(item => LegendLanguageIdentity.PairKey(item.SourceLanguageCode, item.TargetLanguageCode))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var coverage = pairKeys.Length == 0 ? 0 : await _db.Set<LegendTranslationAlignment>().AsNoTracking()
            .LongCountAsync(item => pairKeys.Contains(item.PairKey) && item.SupersededUtc == null, cancellationToken);
        IReadOnlyList<LegendTranslationPairDemand> demand = pairKeys.Length == 0
            ? Array.Empty<LegendTranslationPairDemand>()
            : await _db.Set<LegendTranslationPairDemand>().AsNoTracking()
                .Where(item => pairKeys.Contains(item.PairKey)).ToListAsync(cancellationToken);
        var opportunities = demand.Sum(item => item.TranslationRequestCount);
        var azure = demand.Sum(item => item.AzureFallbackCount);
        var successful = candidates.Where(item => item.ProcessingState == "Queued").ToList();
        var focused = policy.FocusedTargetLanguageCodes.Count > 0;
        return new LegendConnectPriorityProgressSnapshot(
            eligible == 0
                ? focused ? "FOCUS COMPLETE — NO ELIGIBLE MISSING WORK" : "PRIORITY COMPLETE — NO ELIGIBLE MISSING WORK"
                : focused ? "FOUNDER FOCUS ACTIVE" : "FOUNDER PRIORITY ACTIVE",
            eligible,
            pending.LongCount(),
            coverage,
            opportunities == 0 ? 0m : Math.Round((decimal)azure / opportunities, 4),
            successful.Sum(item => Math.Max(0, item.ProviderCharactersConsumed)),
            successful.Select(item => (DateTime?)item.ProcessedUtc).Max(),
            focused
                ? eligible == 0
                    ? "The selected English-to-target focus remains ready for future approved Founder learning sets."
                    : "Eligible English learning sets are expanding only into the selected target languages."
                : eligible == 0
                    ? "The override remains active for future approved eligible material without re-acquiring existing knowledge."
                    : "Eligible approved work is ordered ahead of ordinary autonomous demand work.");
    }

    public async Task<IReadOnlyList<LegendConnectFounderOperationalAuditSnapshot>> GetRecentAuditAsync(
        int take = 30,
        CancellationToken cancellationToken = default) =>
        await _db.Set<LegendConnectKnowledgeAuditEntry>().AsNoTracking()
            .Where(item => item.Action.StartsWith("RuntimePolicy") ||
                           item.Action.StartsWith("AutonomousAcquisition") ||
                           item.Action.StartsWith("FounderPriorityOverride") ||
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
            PriorityMode = "Automatic",
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
            "Automatic", null, null, null, null, null, DateTime.MinValue);
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
        string.Equals(policy.PriorityMode, "FounderOverride", StringComparison.OrdinalIgnoreCase) ? "FounderOverride" : "Automatic",
        Optional(policy.PriorityLanguageCode, 32),
        Optional(policy.PriorityPairKey, 72),
        Optional(policy.PriorityLevel, 40),
        policy.LastLearningWorkerHeartbeatUtc,
        policy.LastAcquisitionWorkerHeartbeatUtc,
        policy.UpdatedUtc);

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

    private async Task<string?> NormalizePairAsync(string? pairKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pairKey))
            return null;
        var segments = pairKey.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
            throw new ArgumentException("Choose a valid directional pair in source:target form.", nameof(pairKey));
        var source = await _languages.GetLanguageAsync(segments[0], cancellationToken);
        var target = await _languages.GetLanguageAsync(segments[1], cancellationToken);
        if (source is null || target is null || string.Equals(source.Code, target.Code, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected directional pair requires two enabled, distinct languages.", nameof(pairKey));
        return LegendLanguageIdentity.PairKey(source.Code, target.Code);
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

    private static bool PairContainsLanguage(string pair, string language) =>
        pair.Split(':', StringSplitOptions.TrimEntries).Any(segment =>
            string.Equals(segment, language, StringComparison.OrdinalIgnoreCase));

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
            Change("priority mode", before.PriorityMode, after.PriorityMode),
            Change("priority target", before.PriorityPairKey ?? before.PriorityLanguageCode ?? "Automatic", after.PriorityPairKey ?? after.PriorityLanguageCode ?? "Automatic"),
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
