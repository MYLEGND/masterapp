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
        CancellationToken cancellationToken = default);
}

internal interface ITranslationSystemUsageRecorder
{
    Task TryRecordSameLanguageBypassAsync(CancellationToken cancellationToken = default);
}

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
        CancellationToken cancellationToken = default)
    {
        try
        {
            var demand = await _db.Set<LegendTranslationPairDemand>()
                .SingleOrDefaultAsync(item => item.PairKey == pairKey, cancellationToken);
            if (demand is null)
            {
                demand = new LegendTranslationPairDemand
                {
                    Id = Guid.NewGuid(),
                    PairKey = pairKey,
                    TranslationRequestCount = 1,
                    ProviderCharacterCount = Math.Max(0, providerCharacters),
                    TranslationMemoryHitCount = translationMemoryHit ? 1 : 0,
                    AzureFallbackCount = azureFallback ? 1 : 0,
                    ContextualCompositionObservationCount = contextualCompositionObserved ? 1 : 0,
                    LastRequestedUtc = DateTime.UtcNow
                };
                _db.Set<LegendTranslationPairDemand>().Add(demand);
            }
            else
            {
                demand.TranslationRequestCount++;
                demand.ProviderCharacterCount += Math.Max(0, providerCharacters);
                demand.TranslationMemoryHitCount += translationMemoryHit ? 1 : 0;
                demand.AzureFallbackCount += azureFallback ? 1 : 0;
                demand.ContextualCompositionObservationCount += contextualCompositionObserved ? 1 : 0;
                demand.LastRequestedUtc = DateTime.UtcNow;
            }
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

    public async Task TryRecordSameLanguageBypassAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var usage = await _db.Set<LegendTranslationSystemUsage>()
                .SingleOrDefaultAsync(item => item.UsageDate == today, cancellationToken);
            if (usage is null)
            {
                usage = new LegendTranslationSystemUsage
                {
                    Id = Guid.NewGuid(),
                    UsageDate = today,
                    SameLanguageBypassCount = 1,
                    UpdatedUtc = DateTime.UtcNow
                };
                _db.Set<LegendTranslationSystemUsage>().Add(usage);
            }
            else
            {
                usage.SameLanguageBypassCount++;
                usage.UpdatedUtc = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Legend Connect same-language usage write failed.");
        }
    }
}

internal static class LegendCorpusCandidateScoring
{
    internal static long Score(LegendCorpusCandidate candidate, long pairDemand, int pairCoverage) =>
        ((long)Math.Max(0, candidate.Priority) * 1_000_000L) +
        (Math.Max(0, pairDemand) * 1_000L) +
        Math.Max(0, 100_000 - Math.Max(0, pairCoverage));
}
