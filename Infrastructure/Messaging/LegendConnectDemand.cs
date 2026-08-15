using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal interface ITranslationDemandRecorder
{
    Task TryRecordAsync(
        string pairKey,
        int providerCharacters,
        bool translationMemoryHit = false,
        bool azureFallback = false,
        bool contextualCompositionObserved = false,
        bool contextualInternalServed = false,
        bool structuralInternalServed = false,
        CancellationToken cancellationToken = default);
}

internal interface ITranslationSystemUsageRecorder
{
    Task TryRecordSameLanguageBypassAsync(
        int characters = 0,
        CancellationToken cancellationToken = default);

    Task TryRecordAsync(
        TranslationSystemUsageDelta delta,
        CancellationToken cancellationToken = default);
}

internal sealed record TranslationSystemUsageDelta(
    long ProviderOperations = 0,
    long ProviderBillableCharacters = 0,
    long SameLanguageCharactersAvoided = 0,
    long TranslationMemoryCharactersAvoided = 0,
    long ContextualCharactersAvoided = 0,
    long QuotaDeniedRequests = 0,
    long ProviderFailures = 0,
    long GroupUniqueTargetReuses = 0,
    long SameLanguageBypasses = 0,
    long StructuralCompositionCharactersAvoided = 0);

/// <summary>
/// A retry-safe aggregate signal. It deliberately records pair metadata only;
/// private source text and member identity never enter demand intelligence.
/// </summary>
internal sealed class TranslationDemandRecorder : ITranslationDemandRecorder
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<TranslationDemandRecorder> _logger;

    public TranslationDemandRecorder(MasterAppDbContext db, ILogger<TranslationDemandRecorder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task TryRecordAsync(
        string pairKey,
        int providerCharacters,
        bool translationMemoryHit = false,
        bool azureFallback = false,
        bool contextualCompositionObserved = false,
        bool contextualInternalServed = false,
        bool structuralInternalServed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pairKey))
                return;

            var providerCharacterDelta = Math.Max(0, providerCharacters);
            var memoryDelta = translationMemoryHit ? 1 : 0;
            // AzureFallbackCount is the deployed aggregate name, but its
            // durable semantic is provider work required after the internal
            // route—not a completed Azure call.
            var providerFallbackRequiredDelta = azureFallback ? 1 : 0;
            var contextualObservedDelta = contextualCompositionObserved ? 1 : 0;
            var contextualServedDelta = contextualInternalServed ? 1 : 0;
            var structuralServedDelta = structuralInternalServed ? 1 : 0;
            var now = DateTime.UtcNow;

            if (_db.Database.IsRelational())
            {
                var affected = await ApplyRelationalDeltaAsync(
                    pairKey,
                    providerCharacterDelta,
                    memoryDelta,
                    providerFallbackRequiredDelta,
                    contextualObservedDelta,
                    contextualServedDelta,
                    structuralServedDelta,
                    now,
                    cancellationToken);
                if (affected == 1)
                    return;

                _db.Set<LegendTranslationPairDemand>().Add(new LegendTranslationPairDemand
                {
                    Id = Guid.NewGuid(),
                    PairKey = pairKey,
                    TranslationRequestCount = 1,
                    ProviderCharacterCount = providerCharacterDelta,
                    TranslationMemoryHitCount = memoryDelta,
                    AzureFallbackCount = providerFallbackRequiredDelta,
                    ContextualCompositionObservationCount = contextualObservedDelta,
                    ContextualInternalServeCount = contextualServedDelta,
                    StructuralInternalServeCount = structuralServedDelta,
                    LastRequestedUtc = now
                });
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    return;
                }
                catch (DbUpdateException)
                {
                    // The unique pair key elected another instance to create
                    // the aggregate. Apply this request's delta atomically to
                    // that row rather than silently losing telemetry.
                    _db.ChangeTracker.Clear();
                    await ApplyRelationalDeltaAsync(
                        pairKey,
                        providerCharacterDelta,
                        memoryDelta,
                        providerFallbackRequiredDelta,
                        contextualObservedDelta,
                        contextualServedDelta,
                        structuralServedDelta,
                        now,
                        cancellationToken);
                    return;
                }
            }

            var demand = await _db.Set<LegendTranslationPairDemand>()
                .SingleOrDefaultAsync(item => item.PairKey == pairKey, cancellationToken);
            if (demand is null)
            {
                demand = new LegendTranslationPairDemand { Id = Guid.NewGuid(), PairKey = pairKey };
                _db.Set<LegendTranslationPairDemand>().Add(demand);
            }
            demand.TranslationRequestCount++;
            demand.ProviderCharacterCount += providerCharacterDelta;
            demand.TranslationMemoryHitCount += memoryDelta;
            demand.AzureFallbackCount += providerFallbackRequiredDelta;
            demand.ContextualCompositionObservationCount += contextualObservedDelta;
            demand.ContextualInternalServeCount += contextualServedDelta;
            demand.StructuralInternalServeCount += structuralServedDelta;
            demand.LastRequestedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent aggregate updates are non-critical telemetry. The
            // next request reconciles the durable pair signal; translation is
            // intentionally never delayed by a demand write conflict.
            _db.ChangeTracker.Clear();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Legend Connect pair demand write failed. Pair={Pair}", pairKey);
        }
    }

    private Task<int> ApplyRelationalDeltaAsync(
        string pairKey,
        int providerCharacters,
        int memoryHits,
        int providerFallbacksRequired,
        int contextualObserved,
        int contextualServed,
        int structuralServed,
        DateTime now,
        CancellationToken cancellationToken) =>
        _db.Set<LegendTranslationPairDemand>()
            .Where(item => item.PairKey == pairKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.TranslationRequestCount, item => item.TranslationRequestCount + 1)
                .SetProperty(item => item.ProviderCharacterCount, item => item.ProviderCharacterCount + providerCharacters)
                .SetProperty(item => item.TranslationMemoryHitCount, item => item.TranslationMemoryHitCount + memoryHits)
                .SetProperty(item => item.AzureFallbackCount, item => item.AzureFallbackCount + providerFallbacksRequired)
                .SetProperty(item => item.ContextualCompositionObservationCount, item => item.ContextualCompositionObservationCount + contextualObserved)
                .SetProperty(item => item.ContextualInternalServeCount, item => item.ContextualInternalServeCount + contextualServed)
                .SetProperty(item => item.StructuralInternalServeCount, item => item.StructuralInternalServeCount + structuralServed)
                .SetProperty(item => item.LastRequestedUtc, now), cancellationToken);
}

/// <summary>
/// Persists non-pair translation-path usage so Founder operations report real
/// same-language bypasses without retaining a per-message log.
/// </summary>
internal sealed class TranslationSystemUsageRecorder : ITranslationSystemUsageRecorder
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<TranslationSystemUsageRecorder> _logger;

    public TranslationSystemUsageRecorder(
        MasterAppDbContext db,
        ILogger<TranslationSystemUsageRecorder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task TryRecordSameLanguageBypassAsync(
        int characters = 0,
        CancellationToken cancellationToken = default) =>
        TryRecordAsync(
            new TranslationSystemUsageDelta(
                SameLanguageCharactersAvoided: Math.Max(0, characters),
                SameLanguageBypasses: 1),
            cancellationToken);

    public async Task TryRecordAsync(
        TranslationSystemUsageDelta delta,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var now = DateTime.UtcNow;
            if (_db.Database.IsRelational())
            {
                var affected = await ApplyRelationalDeltaAsync(today, delta, now, cancellationToken);
                if (affected == 1)
                    return;

                var usage = new LegendTranslationSystemUsage
                {
                    Id = Guid.NewGuid(),
                    UsageDate = today,
                    UpdatedUtc = now
                };
                Apply(usage, delta);
                _db.Set<LegendTranslationSystemUsage>().Add(usage);
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    return;
                }
                catch (DbUpdateException)
                {
                    // The unique daily key elected another instance to create
                    // the row. Atomically add this request's delta to it.
                    _db.ChangeTracker.Clear();
                    await ApplyRelationalDeltaAsync(today, delta, now, cancellationToken);
                    return;
                }
            }

            var inMemoryUsage = await _db.Set<LegendTranslationSystemUsage>()
                .SingleOrDefaultAsync(item => item.UsageDate == today, cancellationToken);
            if (inMemoryUsage is null)
            {
                inMemoryUsage = new LegendTranslationSystemUsage
                {
                    Id = Guid.NewGuid(),
                    UsageDate = today,
                    UpdatedUtc = now
                };
                _db.Set<LegendTranslationSystemUsage>().Add(inMemoryUsage);
            }
            Apply(inMemoryUsage, delta);
            inMemoryUsage.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Legend Connect system usage write failed.");
        }
    }

    private Task<int> ApplyRelationalDeltaAsync(
        DateOnly usageDate,
        TranslationSystemUsageDelta delta,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var sameLanguageBypasses = Math.Max(0, delta.SameLanguageBypasses);
        var providerOperations = Math.Max(0, delta.ProviderOperations);
        var providerBillableCharacters = Math.Max(0, delta.ProviderBillableCharacters);
        var sameLanguageCharactersAvoided = Math.Max(0, delta.SameLanguageCharactersAvoided);
        var translationMemoryCharactersAvoided = Math.Max(0, delta.TranslationMemoryCharactersAvoided);
        var structuralCompositionCharactersAvoided = Math.Max(0, delta.StructuralCompositionCharactersAvoided);
        var contextualCharactersAvoided = Math.Max(0, delta.ContextualCharactersAvoided);
        var quotaDeniedRequests = Math.Max(0, delta.QuotaDeniedRequests);
        var providerFailures = Math.Max(0, delta.ProviderFailures);
        var groupUniqueTargetReuses = Math.Max(0, delta.GroupUniqueTargetReuses);
        return _db.Set<LegendTranslationSystemUsage>()
            .Where(item => item.UsageDate == usageDate)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.SameLanguageBypassCount, item => item.SameLanguageBypassCount + sameLanguageBypasses)
                .SetProperty(item => item.ProviderOperationCount, item => item.ProviderOperationCount + providerOperations)
                .SetProperty(item => item.ProviderBillableCharacters, item => item.ProviderBillableCharacters + providerBillableCharacters)
                .SetProperty(item => item.SameLanguageCharactersAvoided, item => item.SameLanguageCharactersAvoided + sameLanguageCharactersAvoided)
                .SetProperty(item => item.TranslationMemoryCharactersAvoided, item => item.TranslationMemoryCharactersAvoided + translationMemoryCharactersAvoided)
                .SetProperty(item => item.StructuralCompositionCharactersAvoided, item => item.StructuralCompositionCharactersAvoided + structuralCompositionCharactersAvoided)
                .SetProperty(item => item.ContextualCharactersAvoided, item => item.ContextualCharactersAvoided + contextualCharactersAvoided)
                .SetProperty(item => item.QuotaDeniedRequestCount, item => item.QuotaDeniedRequestCount + quotaDeniedRequests)
                .SetProperty(item => item.ProviderFailureCount, item => item.ProviderFailureCount + providerFailures)
                .SetProperty(item => item.GroupUniqueTargetReuseCount, item => item.GroupUniqueTargetReuseCount + groupUniqueTargetReuses)
                .SetProperty(item => item.UpdatedUtc, now), cancellationToken);
    }

    private static void Apply(LegendTranslationSystemUsage usage, TranslationSystemUsageDelta delta)
    {
        usage.SameLanguageBypassCount += Math.Max(0, delta.SameLanguageBypasses);
        usage.ProviderOperationCount += Math.Max(0, delta.ProviderOperations);
        usage.ProviderBillableCharacters += Math.Max(0, delta.ProviderBillableCharacters);
        usage.SameLanguageCharactersAvoided += Math.Max(0, delta.SameLanguageCharactersAvoided);
        usage.TranslationMemoryCharactersAvoided += Math.Max(0, delta.TranslationMemoryCharactersAvoided);
        usage.StructuralCompositionCharactersAvoided += Math.Max(0, delta.StructuralCompositionCharactersAvoided);
        usage.ContextualCharactersAvoided += Math.Max(0, delta.ContextualCharactersAvoided);
        usage.QuotaDeniedRequestCount += Math.Max(0, delta.QuotaDeniedRequests);
        usage.ProviderFailureCount += Math.Max(0, delta.ProviderFailures);
        usage.GroupUniqueTargetReuseCount += Math.Max(0, delta.GroupUniqueTargetReuses);
    }
}

internal static class LegendCorpusCandidateScoring
{
    internal static long Score(LegendCorpusCandidate candidate, long pairDemand, int pairCoverage) =>
        ((long)Math.Max(0, candidate.Priority) * 1_000_000L) +
        (Math.Max(0, pairDemand) * 1_000L) +
        Math.Max(0, 100_000 - Math.Max(0, pairCoverage));
}
