using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed record LegendTranslationMemoryMatch(string Text, decimal Confidence);

internal sealed record LegendContextualTranslationSuggestion(string Text, decimal Confidence);

/// <summary>
/// Read-only language intelligence evaluator. It never owns a provider and it
/// never formulates live output unless a future explicit configuration promotes
/// a trusted result beyond the default shadow-evaluation mode.
/// </summary>
internal interface ILegendConnectTranslationIntelligence
{
    Task<LegendTranslationMemoryMatch?> TryGetTrustedExactMemoryAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default);

    Task<LegendContextualTranslationSuggestion?> EvaluateContextAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default);

    bool IsContextualCompositionActive { get; }
}

internal sealed class LegendConnectTranslationIntelligence : ILegendConnectTranslationIntelligence
{
    private readonly MasterAppDbContext _db;
    private readonly decimal _minimumConfidence;
    private readonly ILegendConnectRuntimePolicyAuthority? _runtimePolicy;

    public LegendConnectTranslationIntelligence(
        MasterAppDbContext db,
        IConfiguration configuration,
        ILegendConnectRuntimePolicyAuthority? runtimePolicy = null)
    {
        _db = db;
        _minimumConfidence = Math.Clamp(
            configuration.GetValue<decimal?>("LegendConnect:ContextualComposition:MinimumConfidence") ?? 0.98m,
            0m,
            1m);
        IsContextualCompositionActive = string.Equals(
            configuration["LegendConnect:ContextualComposition:Mode"],
            "Active",
            StringComparison.OrdinalIgnoreCase);
        _runtimePolicy = runtimePolicy;
    }

    public bool IsContextualCompositionActive { get; }

    public async Task<LegendTranslationMemoryMatch?> TryGetTrustedExactMemoryAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default)
    {
        var hash = LegendLanguageIdentity.TextHash(text);
        var pairKey = LegendLanguageIdentity.PairKey(sourceLanguageCode, targetLanguageCode);
        var minimumConfidence = await MinimumConfidenceAsync(cancellationToken);
        var match = await (
            from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
            join source in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on alignment.TargetTextUnitId equals target.Id
            where alignment.PairKey == pairKey &&
                  alignment.SupersededUtc == null &&
                  source.LanguageCode == sourceLanguageCode &&
                  source.NormalizedHash == hash &&
                  (alignment.HumanVerified ||
                   (alignment.Confidence != null && alignment.Confidence >= minimumConfidence &&
                    alignment.QualityState == "Verified"))
            orderby alignment.HumanVerified descending, alignment.Confidence descending, alignment.UpdatedUtc descending
            select new { target.Text, alignment.Confidence }
        ).FirstOrDefaultAsync(cancellationToken);

        return match is null
            ? null
            : new LegendTranslationMemoryMatch(match.Text, match.Confidence ?? (match.Text.Length > 0 ? 1m : 0m));
    }

    public async Task<LegendContextualTranslationSuggestion?> EvaluateContextAsync(
        string sourceLanguageCode,
        string targetLanguageCode,
        string text,
        CancellationToken cancellationToken = default)
    {
        var pairKey = LegendLanguageIdentity.PairKey(sourceLanguageCode, targetLanguageCode);
        var minimumConfidence = await MinimumConfidenceAsync(cancellationToken);
        var pattern = LegendLanguageIdentity.ContextPatternSignature(text);
        if (string.IsNullOrWhiteSpace(pattern))
            return null;

        var candidates = await (
            from relationship in _db.Set<LegendLanguageContextRelationship>().AsNoTracking()
            join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                on relationship.RelatedTextUnitId equals target.Id
            where relationship.PairKey == pairKey &&
                  relationship.SourcePatternSignature == pattern &&
                  relationship.QualityState == "Verified" &&
                  relationship.Confidence >= minimumConfidence &&
                  target.LanguageCode == targetLanguageCode
            select new { target.Text, relationship.Confidence }
        ).Distinct().Take(2).ToListAsync(cancellationToken);

        // Ambiguous structural matches are observations, never a formulation.
        return candidates.Count == 1
            ? new LegendContextualTranslationSuggestion(candidates[0].Text, candidates[0].Confidence)
            : null;
    }

    private async Task<decimal> MinimumConfidenceAsync(CancellationToken cancellationToken) =>
        _runtimePolicy is null
            ? _minimumConfidence
            : (await _runtimePolicy.GetEffectiveAsync(cancellationToken)).ContextualMinimumConfidence;
}

internal interface ILegendConnectOperationalEventWriter
{
    Task TryRecordAsync(
        string category,
        string severity,
        string status,
        string? languageCode = null,
        string? pairKey = null,
        string? errorCode = null,
        string? correlationId = null,
        string? summary = null,
        bool isResolved = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One bounded diagnostic authority for Legend Connect. Callers provide only
/// operational metadata; message/corpus bodies and provider secrets are never
/// accepted by this API.
/// </summary>
internal sealed class LegendConnectOperationalEventWriter : ILegendConnectOperationalEventWriter
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<LegendConnectOperationalEventWriter> _logger;

    public LegendConnectOperationalEventWriter(
        MasterAppDbContext db,
        ILogger<LegendConnectOperationalEventWriter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task TryRecordAsync(
        string category,
        string severity,
        string status,
        string? languageCode = null,
        string? pairKey = null,
        string? errorCode = null,
        string? correlationId = null,
        string? summary = null,
        bool isResolved = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _db.Set<LegendConnectOperationalEvent>().Add(new LegendConnectOperationalEvent
            {
                Id = Guid.NewGuid(),
                Category = Bounded(category, 80),
                Severity = Bounded(severity, 20),
                Status = Bounded(status, 80),
                LanguageCode = Optional(languageCode, 32),
                PairKey = Optional(pairKey, 72),
                ErrorCode = Optional(errorCode, 80),
                CorrelationId = Optional(correlationId, 128),
                Summary = Optional(summary, 500),
                IsResolved = isResolved,
                OccurredUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(exception, "Legend Connect operational event write failed. Category={Category} Status={Status}", category, status);
        }
    }

    private static string Bounded(string value, int length) =>
        (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, length)];

    private static string? Optional(string? value, int length)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, length)];
    }
}
